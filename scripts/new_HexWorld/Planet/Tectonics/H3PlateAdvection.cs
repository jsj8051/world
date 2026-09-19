using System;
using System.Collections.Generic;
using Godot;
using World.Tectonics;                 // MaterialDensity
using World.Utils;
using World.Utils.H3;

namespace World.NewHexWorld.Plate
{
	// 板块平流（设计-03 §3.1/§3.2；流链元胞模型）。
	//
	// 搬运：起跳板的每个格沿本板速度方向走一格（板级相位决定起跳节奏，起跳频率 = 动力学速度）。
	// 意图形成一条条流链（同向排列的格串），结算从链头向链尾一趟完成：
	//   链头遇异板：本格更密 → 俯冲（守恒组埋入对方格下、mafic/age 记回地幔、本格腾空 = 下盘传送带）；
	//     更轻/等重 → 顶死停驻（本格不动，后方来料叠上来 = 造山）。顶死另需接触上有长英质（陆壳）
	//     参与——洋-洋汇聚一律俯冲（03 §3.5 规则表）。
	//   链中每格：前移一格，填入前方腾出的格（纯置换，归属随物质走）。
	//   折叠（两格的前驱指向同一格）：同板多料合一。
	//   链尾腾空：空洞，由裂谷步骤按本板补料（归属永不因填充改变）。
	//
	// 俯冲/顶死判据在 H3PlateContact.JamsInto（与运动学"顶死零功修正"共用，防口径漂移），
	// 落点邻居选择共用 FlowNeighbor。JamCellCount/JamCellsPerPlate 只记真接触顶死——
	// 链头撞同板未起跳格是相位停驻，不计。
	//
	// 离散残差处理（填充/折叠都不改搬运规则）：
	//   ① 空洞 → 同板料邻居里多数相那批按 1/d² 加权平均八场写入（与周边连续过渡，不出水也不块斑）；
	//   ② 同板多源（折叠）→ 质量加权平均柱写出 + 差额按 1/d² 全额摊给同板邻居——
	//      顶死接触前缘格优先（富余属于造山带 = 沿带生长），其次同相邻居（永不改相），
	//      都够不着则记回地幔。
	//
	// 归属 = 顶层物质的板号，随物质走；异板色翻转在规则层面不存在：跨板接触只有俯冲埋入
	// （保持对方色）与顶死（保持己方色）。板内非主体小连通块的清理在
	// H3DynamicTectonics.MergeStrayFragments（只改标签、不动物质）。
	//
	// 质量对账：搬运环节守恒；质量只经俯冲消减（回地幔）、差额摊不出去（回地幔）、
	// 与空洞填充（自地幔新增）三处进出。
	public sealed class H3PlateAdvection
	{
		public int HoleCount { get; private set; }             // 本步空洞格数（未填，待裂谷）
		public int MixedCellCount { get; private set; }        // 异板混合格数（= 俯冲埋入/汇聚堆叠格）
		public int OverflowCellCount { get; private set; }     // 同板多源格数（= 压缩增厚格）
		public int ResampledCellCount { get; private set; }    // 陆内拉伸重采样补料格数
		/// <summary>本步按"邻域加权平均"填充的空洞格数（与 <see cref="NewCrustFilledCount"/> 一起
		/// 构成填充路径判读口）。</summary>
		public int HoleFillAveragedCount { get; private set; }
		/// <summary>本步按兜底"注新洋壳"填充的空洞格数（空洞被异板完全包围、无同板料邻居时才走）。</summary>
		public int NewCrustFilledCount { get; private set; }
		public double RecycledToMantleMass { get; private set; }
		// ── 俯冲再循环旋钮（持有方 H3DynamicTectonics 每步推入）──
		/// <summary>埋入层沉积类（sediment/sedimentary）随俯冲回地幔的比例。1 = 全额回收；
		/// 只作用于真混合格（有上盘顶层压着），全埋入格整柱保留。</summary>
		public float RecycleSedimentFraction = 1f;
		/// <summary>埋入层长英质类（metamorphic/felsic×2）随俯冲回地幔的刮削比例。只作用于被俯冲板
		/// 驮着的薄层长英质；陆壳本体浮力大、罕被俯冲。</summary>
		public float RecycleFelsicFraction = 0.2f;
		/// <summary>本步随俯冲回地幔的守恒组质量（与 mafic 消减分账，已并入 RecycledToMantleMass）。</summary>
		public double RecycledConservedMass { get; private set; }

		// ── 弧岩浆回流（陆壳收支的入口通道）──
		/// <summary>弧回流系数：洋壳俯冲到**陆壳上盘**时，每埋入 1 kg 洋壳即按此比例向上盘格
		/// 从地幔新生长英质火山岩（记创建账）。标定靶 = 长英质刮削累计 ≈ 弧回流累计
		///（H3DynamicTectonics.FelsicScrapedCum / FelsicArcReturnedCum）。</summary>
		public float ArcFelsicReturnFraction = 0.1f;
		/// <summary>本步弧回流新生的长英质质量（创建账口径；0 = 本步无洋→陆俯冲）。</summary>
		public double ArcFelsicReturnedMassLastStep { get; private set; }
		/// <summary>本步被俯冲刮削回地幔的长英质质量（再循环口径的长英质部分）。</summary>
		public double FelsicScrapedMassLastStep { get; private set; }

		// ── 洋底扩张（威尔逊旋回引擎）──
		/// <summary>离散边界注新洋壳：空洞若位于正在撕开的板块边界（相对速度法向负收敛超阈值），
		/// 直接注 age=0 脊轴新洋壳——洋中脊持续造洋、大陆裂谷长出窄洋（新洋格稀释陆占比）。</summary>
		public bool EnableSpreading = true;
		/// <summary>离散发育阈值（km/My）：相对速度法向分离分量超过它才注壳；汇聚/走滑边不注。</summary>
		public float SpreadingSpeedKmPerMy = 0.05f;
		/// <summary>本步在离散边界注入的新洋壳格数（扩张系统是否在工作的直接读数）。</summary>
		public int SpreadingFilledCount { get; private set; }
		/// <summary>本步新增地壳质量（洋中脊/兜底填新洋壳 + 陆内重采样复制）。
		/// 与 RecycledToMantleMass（消减）构成质量进出仅有的两户。</summary>
		public double CrustCreatedMass { get; private set; }
		public int JamCellCount { get; private set; }          // 本步真接触顶死格数（foreign 才计；相位停驻不算）
		/// <summary>本步陆-陆碰撞格数（顶死接触且两侧皆陆）——周期重启触发③的输入。</summary>
		public int ContinentalJamCellCount { get; private set; }

		/// <summary>顶死格按板分布（键 = 顶死格的板号；缺口量测口）。</summary>
		public IReadOnlyDictionary<int, int> JamCellsPerPlate => _jamCellsPerPlate;
		readonly Dictionary<int, int> _jamCellsPerPlate = new();

		/// <summary>本步板片账户入账（键 = 俯冲来料板）：质量 = 俯冲格 mafic 面密度，
		/// 方向 = 来料格→目标格切向单位向量（质量加权累计由 H3PlateMotion.AddSlab 承担）。</summary>
		public IReadOnlyDictionary<int, (double mass, Vector3 dir)> SlabInflow => _slabInflow;
		readonly Dictionary<int, (double mass, Vector3 dir)> _slabInflow = new();

		/// <summary>本步逐板对顶死接触计数（键 = (小板号, 大板号)）：缝合触发器的输入——
		/// 只有实际碰撞的边界才累积缝合资格，单纯邻接不算。</summary>
		public IReadOnlyDictionary<(int a, int b), int> JamPairCounts => _jamPairs;
		readonly Dictionary<(int, int), int> _jamPairs = new();

		/// <summary>本步顶死格清单（增生楔的落点 = 造山前缘）。</summary>
		public IReadOnlyList<(int cell, int plate)> JamContacts => _jamContacts;
		readonly List<(int cell, int plate)> _jamContacts = new();


		// ── 换色事件日志（诊断；kind：0 = arrival 翻色，1 = 链尾新洋壳，2 = 陆内重采样）──
		public readonly List<(int step, int cell, int from, int to, int kind)> ChangeEvents = new();

		int _currentStep;                      // 当前步号（换色事件日志用）
		readonly int[] _successor;             // 起跳格的流向后继（意图落点）
		readonly bool[] _moves;                // 该格本步是否真的移动（起跳且方向有效）
		readonly int[] _predFirst;             // 反向指针：每格的前驱链表头（谁要移进本格）
		readonly int[] _predNext;              // 前驱链表 next
		readonly bool[] _settled;              // 该起跳格的物质已结算
		readonly int[] _depositHead;           // 每格的沉积链表头（哪些源格的物质落到本格）
		readonly int[] _depositNext;           // 沉积链表 next
		readonly bool[] _depositBuried;        // 该沉积是否为俯冲埋入（异板格下，不参与顶层）
		readonly Queue<int> _settledQueue;     // 已结算格队列（供前驱链式补位）
		readonly int[] _holePlate;             // 空洞的链尾板（恒等填充的归属来源）
		readonly float[] _sumScratch;          // 逐格八场求和暂存
		readonly float[] _buriedConserved = new float[5];      // 埋入层守恒组暂存（再循环回收扣减用，逐格重置）
		readonly float[] _arcReturn;           // 本步弧回流待落场质量（上盘格 → 写出段后统一落，Pass C 累计）
		readonly int[] _annexPlateScratch = new int[6];   // AnnexToMajority 逐邻居板分类暂存（不分配）
		readonly int[] _annexCountScratch = new int[6];
		readonly int[] _holeCells;             // 本步腾空的空洞格清单（写出段收集；填洞只扫清单，免全表扫）
		int _holeCellCount;
		bool _holeListValid;                   // 清单与 _holePlate 同步有效（Step 写出段置位，填洞消费后复位）

		public H3PlateAdvection(int cellCount)
		{
			_successor = new int[cellCount];
			_moves = new bool[cellCount];
			_predFirst = new int[cellCount];
			_predNext = new int[cellCount];
			_settled = new bool[cellCount];
			_depositHead = new int[cellCount];
			_depositNext = new int[cellCount];
			_depositBuried = new bool[cellCount];
			_settledQueue = new Queue<int>();
			_holePlate = new int[cellCount];
			_holeCells = new int[cellCount];
			_arcReturn = new float[cellCount];
			_sumScratch = new float[8];
		}

		/// <summary>走一步平流：起跳板沿流向整链移动一格；链头定接触（俯冲/顶死），
		/// 链尾生新壳。归属 = 顶层物质的板号。双缓冲：调用方持有两块 H3PlateFields
		/// 交替使用（避免原地搬动时源被覆盖）。</summary>
		public void Step(Ball ball, H3PlateFields source, H3PlateMotion motion, H3PlateFields target,
			MaterialDensity material, int stepIndex)
		{
			int n = ball.CellIds.Length;
			var centers = ball.CellCenters;
			var neighbors = ball.CellNeighbors;
			var neighborDirs = ball.CellNeighborDirs;   // 构造期归一的逐邻居切向方向（Pass A 免逐邻居投影+归一化）
			var dirs = ball.CellDirs;              // 构造期归一的格心方向（Pass A / 俯冲流向免逐格 Normalized）
			_currentStep = stepIndex;

			// ── 复位（置常量的段走向量化填充；_successor 是恒等映射只能逐元素写）──
			for (int i = 0; i < n; i++) _successor[i] = i;
			Array.Clear(_moves, 0, n);
			Array.Fill(_predFirst, -1);
			Array.Clear(_settled, 0, n);
			Array.Fill(_depositHead, -1);
			Array.Clear(_depositBuried, 0, n);
			Array.Fill(_holePlate, -1);
			Array.Clear(_arcReturn, 0, n);
			_holeCellCount = 0;
			_holeListValid = false;
			_settledQueue.Clear();
			HoleCount = 0; MixedCellCount = 0; OverflowCellCount = 0;
			ResampledCellCount = 0; RecycledToMantleMass = 0; JamCellCount = 0; CrustCreatedMass = 0;
			HoleFillAveragedCount = 0; NewCrustFilledCount = 0;
			RecycledConservedMass = 0;
			ArcFelsicReturnedMassLastStep = 0;
			FelsicScrapedMassLastStep = 0;
			ContinentalJamCellCount = 0;
			_jamCellsPerPlate.Clear();
			_slabInflow.Clear();
			_jamPairs.Clear();
			_jamContacts.Clear();
			ChangeEvents.Clear();

			var srcPools = source.AllPools();
			var fired = motion.PlateFired;
			var velocity = motion.Velocity;

			// ── Pass A：起跳格的流向后继（意图）。地图型并行：写只落 _successor[i]/_moves[i]，
			//    读的 fired/velocity/邻接表/预计算方向表在本段只读。──
			int[] successor = _successor;
			bool[] moves = _moves;
			ParallelLoops.For(n, i =>
			{
				int plate = source.PlateId[i];
				if (plate < 0) return;                             // 无料格
				if (plate >= fired.Length || !fired[plate]) return;    // 本板本步未起跳
				Vector3 v = velocity[i];
				float speedRadPerMy = v.Length();
				if (speedRadPerMy <= 1e-9f) return;                // 速度为零 → 原地
				Vector3 radial = dirs[i];
				Vector3 dir = v - radial * v.Dot(radial);
				float dirLength = dir.Length();
				if (dirLength <= 1e-12f) return;
				dir /= dirLength;
				int best = H3PlateContact.FlowNeighbor(neighborDirs, neighbors, i, dir);
				if (best < 0 || best == i) return;
				successor[i] = best;
				moves[i] = true;                                   // 该格本步移动一格
			});

			// ── Pass B：反向指针（前驱链表，谁要移进本格）+ 未起跳格原地沉积 ──
			for (int i = 0; i < n; i++)
			{
				if (source.PlateId[i] < 0) continue;
				if (_moves[i])
				{
					int t = _successor[i];
					_predNext[i] = _predFirst[t];
					_predFirst[t] = i;                             // 后进先出；格序固定 ⇒ 确定性
				}
				else
				{
					Deposit(i, i, buried: false);                  // 未起跳：原地沉积（驻留）
					_settled[i] = true;
				}
			}

			// ── Pass C：链头识别 + 接触结算 ──
			// 链头 = 起跳格，其后继是【异板物质】（触头接触）或【驻留格】（未起跳/已顶死，被阻挡）。
			// 链头结算后入队；队列向链尾传播：后继腾空的格由前驱补位（链推进），后继驻留的格
			// 收前驱叠放（造山堆厚）。链中格（后继同板且在动）不在此结算——等队列传播。
			for (int i = 0; i < n; i++)
			{
				if (!_moves[i]) continue;
				int t = _successor[i];
				int plateI = source.PlateId[i];
				int plateT = source.PlateId[t];
				bool foreign = plateT >= 0 && plateT != plateI;

				if (foreign && !H3PlateContact.JamsInto(
					motion.DensityFor(source, i, material), motion.DensityFor(source, t, material),
					source.IsLand(i), source.IsLand(t)))
				{
					Deposit(i, t, buried: true);                   // 俯冲：守恒组埋入对方格下
					double subductedMafic = source.MaficVolcanic[i] + source.MaficPlutonic[i];
					// 弧岩浆回流：洋壳俯冲到陆壳上盘 ⇒ 按埋入 mafic 通量记账，写出段后从地幔
					// 新生长英质火山岩落到上盘格（创建账；洋-洋俯冲不触发——不造 intra-oceanic 弧）。
					if (source.IsLand(t) && !source.IsLand(i))
						_arcReturn[t] += ArcFelsicReturnFraction * (float)subductedMafic;
					// 板片账户入账：质量 × 流向（i→t 切向单位向量）——已俯冲的物质下步继续拉本板。
					// mafic 的"回地幔"记账不在 Pass C 做：埋入物质是否真被压掉取决于写出段的落位
					//（目标格自身移走时，唯一落位 = 埋入层本身，物质保留在场上，若已记消减 = 凭空消失）。
					Vector3 delta = centers[t] - centers[i];
					Vector3 radialI = dirs[i];
					Vector3 flow = delta - radialI * delta.Dot(radialI);
					float flowLength = flow.Length();
					if (flowLength > 1e-12f)
					{
						var account = _slabInflow.GetValueOrDefault(plateI);
						_slabInflow[plateI] = (account.mass + subductedMafic,
							account.dir + flow / flowLength * (float)subductedMafic);
					}
					_settled[i] = true;
					_settledQueue.Enqueue(t);                      // t 收到埋入层
					_settledQueue.Enqueue(i);                      // i 腾空：前驱补位
				}
				else if (foreign || !_moves[t])
				{
					// 链头驻留：顶死（不比目标密）或后继未起跳（相位停驻）；本格不动，后方来料叠上
					if (foreign)
					{
						JamCellCount++;                            // 真接触顶死（相位停驻不计）
						if (source.IsLand(i) && source.IsLand(t)) ContinentalJamCellCount++;   // 陆-陆碰撞
						int plate = source.PlateId[i];
						_jamCellsPerPlate[plate] = _jamCellsPerPlate.GetValueOrDefault(plate) + 1;
						_jamContacts.Add((i, plate));              // 前缘清单（屈服流档①目标）
						var pairKey = (Math.Min(plateI, plateT), Math.Max(plateI, plateT));
						_jamPairs[pairKey] = _jamPairs.GetValueOrDefault(pairKey) + 1;   // 缝合触发器输入
					}
					Deposit(i, i, buried: false);
					_settled[i] = true;
					_settledQueue.Enqueue(i);
				}
				// else：链中格（后继同板且在动）——留给队列传播结算
			}
			// 链式补位：腾空/受料格由前驱填入（前驱再腾空再入队）
			DrainSettledQueue();

			// 环：未被链头覆盖的闭合流环（纯旋转）→ 沿环旋转一格（纯置换）。
			// 环格结算后也必须入队：否则喂进环的前驱链永远等不到结算，物质凭空蒸发。
			for (int i = 0; i < n; i++)
			{
				if (!_moves[i] || _settled[i]) continue;
				Deposit(i, _successor[i], buried: false);
				_settled[i] = true;
				_settledQueue.Enqueue(i);
			}
			DrainSettledQueue();

			// ── 写出：逐格叠放沉积物（同板八场求和；异板守恒求和 + 顶层裁决 + 埋入回地幔）──
			var tgtPools = target.AllPools();
			for (int t = 0; t < n; t++)
			{
				bool wasMaterial = source.PlateId[t] >= 0;
				int head = _depositHead[t];
				if (head == -1)
				{
					// 无沉积落位：整格清写——必须无条件（含无料格）：双缓冲乒乓下跳过写入
					// 会复活两步前的陈旧质量（幽灵格）。有料格腾空 = 空洞（待裂谷恒等填充）。
					if (wasMaterial)
					{
						_holePlate[t] = source.PlateId[t];             // 链尾板 = 恒等填充归属
						HoleCount++;
						_holeCells[_holeCellCount++] = t;              // 空洞清单（填洞只扫清单，免全表扫）
					}
					target.ClearCell(t);
					target.PlateId[t] = -1;
					continue;
				}

				// 沉积清点：板数（同板 / 异板混合）
				int depositCount = 0;
				int firstPlate = source.PlateId[head];
				bool mixed = false;
				for (int d = head; d != -1; d = _depositNext[d])
				{
					depositCount++;
					if (source.PlateId[d] != firstPlate) mixed = true;
				}

				for (int k = 0; k < 8; k++) _sumScratch[k] = 0f;
				for (int k = 0; k < 5; k++) _buriedConserved[k] = 0f;
				float ageMax = 0f;
				float topDensity = float.MaxValue;
				int topPlate = -1;
				float topMaficVolcanic = 0f, topMaficPlutonic = 0f, topAge = 0f;
				double buriedLoss = 0;                         // 埋入层 mafic 暂存（确认非"全埋入"才记销毁）
				for (int d = head; d != -1; d = _depositNext[d])
				{
					int dp = source.PlateId[d];
					for (int k = 0; k < 8; k++) _sumScratch[k] += srcPools[k][d];
					if (srcPools[7][d] > ageMax) ageMax = srcPools[7][d];   // Age = 场 7（最大 = 最老柱）

					if (!mixed)
					{
						// 同板：无顶层竞争（mafic 已随八场求和）。⚠️ 含"唯一落位 = 埋入层"的格子
						//（俯冲目标自身移走）：埋入物质成为该格内容 = 保留在场上，不记回地幔。
						continue;
					}
					if (_depositBuried[d])
					{
						buriedLoss += srcPools[5][d] + srcPools[6][d];     // 埋入暂存（全埋入格不记销毁，见下）
						for (int k = 0; k < 5; k++) _buriedConserved[k] += srcPools[k][d];   // 再循环回收基数
						continue;                                          // 不参与顶层裁决
					}

					float density = motion.DensityFor(source, d, material);
					if (density < topDensity)
					{
						// 顶层裁决：被顶替的前胜者也是败者——它的 mafic 既没写出也不该留在场上，顶替时补记消减。
						if (topPlate >= 0)
						{
							RecycledToMantleMass += topMaficVolcanic + topMaficPlutonic;
						}
						topDensity = density;
						topPlate = dp;
						topMaficVolcanic = srcPools[5][d];
						topMaficPlutonic = srcPools[6][d];
						topAge = srcPools[7][d];
					}
					else
					{
						RecycledToMantleMass += srcPools[5][d] + srcPools[6][d];   // 被压异板 mafic 回地幔
					}
				}

				if (mixed) MixedCellCount++;
				else if (depositCount > 1) OverflowCellCount++;

				// 混合格但无非埋入沉积（俯冲目标自身移走、唯一落位 = 埋入层）：顶层裁决无人参赛，
				// 唯一内容 = 埋入层本身 → 按同板口径整柱保留（mafic 随求和写出、不记消减、归属 = 埋入板）。
				// 不这样会写出 mass>0 且 PlateId=-1 的孤儿格 → 双缓冲乒乓复活陈旧质量。
				// 全埋入格不能把埋入 mafic 记为回地幔——它已在写出段被完整保留（记销毁 + 写出 = 重复记账）。
				bool allBuried = false;
				if (mixed && topPlate < 0) { mixed = false; allBuried = true; }
				if (!allBuried)
				{
					RecycledToMantleMass += buriedLoss;

					// 俯冲再循环：埋入层守恒组按比例回地幔（沉积类全额、长英质类按刮削比例），
					// 从写出量里逐池扣掉。仅真混合生效，全埋入格整柱保留不回收。
					double recycled = _buriedConserved[0] * (double)RecycleSedimentFraction
						+ _buriedConserved[1] * (double)RecycleSedimentFraction
						+ _buriedConserved[2] * (double)RecycleFelsicFraction
						+ _buriedConserved[3] * (double)RecycleFelsicFraction
						+ _buriedConserved[4] * (double)RecycleFelsicFraction;
						if (recycled > 0)
					{
						RecycledToMantleMass += recycled;
						RecycledConservedMass += recycled;
						FelsicScrapedMassLastStep += (double)(_buriedConserved[2] + _buriedConserved[3] + _buriedConserved[4])
							* RecycleFelsicFraction;   // 弧回流的配平对手（陆壳收支判读口）
						_sumScratch[0] -= (float)(_buriedConserved[0] * RecycleSedimentFraction);
						_sumScratch[1] -= (float)(_buriedConserved[1] * RecycleSedimentFraction);
						_sumScratch[2] -= (float)(_buriedConserved[2] * RecycleFelsicFraction);
						_sumScratch[3] -= (float)(_buriedConserved[3] * RecycleFelsicFraction);
						_sumScratch[4] -= (float)(_buriedConserved[4] * RecycleFelsicFraction);
					}
				}

				// 同板多源（= 压缩增厚格）直接**求和**写出（叠瓦堆厚：两根柱叠成一根）。
				// 超过岩石圈支撑阈值的部分由 H3DynamicTectonics 的屈服流在本步 5 摊给邻域——
				// "先堆后摊"，摊开是材料响应不是合并规则的一部分。
				for (int k = 0; k < 5; k++) tgtPools[k][t] = _sumScratch[k];
				if (!mixed)
				{
					tgtPools[5][t] = _sumScratch[5];               // 同板：mafic 也守恒
					tgtPools[6][t] = _sumScratch[6];
					tgtPools[7][t] = ageMax;
				}
				else
				{
					tgtPools[5][t] = topMaficVolcanic;             // 异板：顶层 mafic/age
					tgtPools[6][t] = topMaficPlutonic;
					tgtPools[7][t] = topAge;
				}
				int plateIdT = mixed ? topPlate : firstPlate;      // 同板：归属 = 该板（顶层裁决只管异板）
				target.PlateId[t] = plateIdT;
				if (wasMaterial && plateIdT != source.PlateId[t])
					ChangeEvents.Add((_currentStep, t, source.PlateId[t], plateIdT, 0));
			}

			// 弧岩浆落场（在整轮写出之后：上盘格的本步内容已定，可直接追加）。
			// 从地幔新生长英质火山岩（FelsicVolcanic = 弧火山池），记创建账。
			for (int t = 0; t < n; t++)
			{
				float arc = _arcReturn[t];
				if (arc <= 0f) continue;
				_arcReturn[t] = 0f;
				if (target.PlateId[t] < 0) continue;               // 退化防御：上盘格成了空洞（不该发生）
				target.FelsicVolcanic[t] += arc;
				CrustCreatedMass += arc;
				ArcFelsicReturnedMassLastStep += arc;
			}

			// 空洞清单此刻与 _holePlate 完全同步（填洞走清单扫描；Step 外直调填洞走全表扫回退）
			_holeListValid = true;
		}


		// 沉积登记：源格 d 的物质落到格 t。buried = 俯冲埋入（异板格下，不参与顶层裁决，
		// 其 mafic 在写出段记回地幔）。
		void Deposit(int d, int t, bool buried)		{
			_depositNext[d] = _depositHead[t];
			_depositHead[t] = d;
			_depositBuried[d] = buried;
		}

		// 队列排空：已结算格的前驱依次补位（v 驻留 → 前驱叠上 = 造山；v 腾空 → 前驱推进）。
		void DrainSettledQueue()
		{
			while (_settledQueue.Count > 0)
			{
				int v = _settledQueue.Dequeue();
				for (int p = _predFirst[v]; p != -1; p = _predNext[p])
				{
					if (_settled[p]) continue;                     // 已结算（环回/二次指向）
					Deposit(p, v, buried: false);                  // 前驱物质前移填入
					_settled[p] = true;
					_settledQueue.Enqueue(p);                      // p 腾空 → 它的前驱再补
				}
			}
		}

		/// <summary>空洞补料（03 §2.2 第 5 步；恒等填充）：链尾空洞由链尾板补料——
		/// 有本板陆邻居则重采样本板陆壳（陆内拉伸接续），否则填本板新洋壳（洋中脊生长）。
		/// 兜底：被异板完全包围的孤立空洞按多数邻居并入。填充永不改变归属。
		/// 多遍填充：一遍只够得着"有料邻居"的空洞；2 格以上的空洞簇要下一遍才够得着。
		/// 性能：Step 刚跑完时空洞清单有效 → 只扫空洞格；Step 之外直调（测试手工构造）
		/// 清单无效 → 回退全表扫。</summary>
		public int FillHolesWithNewOceanicCrust(Ball ball, H3PlateFields fields, H3PlateMotion motion,
			float maficMassPerArea)
		{
			SpreadingFilledCount = 0;
			bool useHoleList = _holeListValid;
			_holeListValid = false;
			int filled = 0;
			while (true)                                                   // 第一级：恒等填充
			{
				int pass = FillOneHoleLayer(ball, fields, motion, maficMassPerArea, false, useHoleList);
				filled += pass;
				if (pass == 0) break;
			}
			while (true)                                                   // 第二级：孤立空洞并入多数邻居
			{
				int pass = FillOneHoleLayer(ball, fields, motion, maficMassPerArea, true, useHoleList);
				filled += pass;
				if (pass == 0) break;
			}
			HoleCount -= filled;
			return filled;
		}

		// 单遍：把"材料为空"的格按链尾板恒等补上。annexFallback = true 时，
		// 恒等够不着的孤立空洞（被异板完全包围）并入多数邻居。
		//
		// 空洞填充口径：取同板料邻居里多数相（陆多 → 陆；洋多 → 洋）的那一批，按 1/d² 权重
		// 加权平均八场写入——厚度/年龄/相都与周边连续过渡，既不出水也不留同值块斑。
		// 写入的质量记创建账（填充本就是"自地幔新增"）。
		// 洋底扩张：离散边界的空洞先于多数相判定——任一异板邻居与本板的相对速度沿边界法向
		// 的分离分量超阈值（正在撕开）⇒ 直接注 age=0 脊轴新洋壳。陆内裂谷由此长出窄洋，
		// 相变在这里是有意为之（威尔逊旋回的起点）。
		int FillOneHoleLayer(Ball ball, H3PlateFields fields, H3PlateMotion motion, float maficMassPerArea,
			bool annexFallback, bool useHoleList)
		{
			int n = ball.CellIds.Length;
			var neighbors = ball.CellNeighbors;
			var centers = ball.CellCenters;
			var pools = fields.AllPools();
			int filled = 0;
			int scanCount = useHoleList ? _holeCellCount : n;
			for (int scan = 0; scan < scanCount; scan++)
			{
				int t = useHoleList ? _holeCells[scan] : scan;
				if (fields.TotalMass(t) > 0f) continue;                    // 有料 → 不是空洞
				int holePlate = _holePlate[t];

				// 链尾板明确 → 恒等填充（归属永不因填充改变）
				if (holePlate >= 0)
				{
					// 洋底扩张：离散边界的空洞注 age=0 脊轴新洋壳（归属仍 = 链尾板）
					if (EnableSpreading && IsDivergentHole(ball, fields, motion, t, holePlate))
					{
						fields.SetNewOceanicCrust(t, maficMassPerArea, holePlate);
						CrustCreatedMass += maficMassPerArea;
						SpreadingFilledCount++;
						ChangeEvents.Add((_currentStep, t, -1, holePlate, 5));   // kind 5 = 洋底扩张注壳
						filled++;
						continue;
					}

					int landCount = 0, oceanCount = 0;
					float landInverseDistance = 0f, oceanInverseDistance = 0f;
					foreach (int neighbor in neighbors[t])
					{
						if (fields.TotalMass(neighbor) <= 0f) continue;
						if (fields.PlateId[neighbor] != holePlate) continue;
						if (fields.IsLand(neighbor))
						{
							landCount++;
							landInverseDistance += InverseDistance(centers, neighbor, t);
						}
						else
						{
							oceanCount++;
							oceanInverseDistance += InverseDistance(centers, neighbor, t);
						}
					}

					// 多数相（陆多 → 陆内拉伸；洋多 → 海底连续）：相内按 1/d² 权重加权平均
					if (landCount + oceanCount > 0)
					{
						bool anyLand = landCount >= oceanCount;
						float phaseWeight = anyLand ? landInverseDistance : oceanInverseDistance;
						if (phaseWeight <= 0f) continue;               // 防御：多数相权重必 > 0
						double writtenMass = 0;
						for (int k = 0; k < 8; k++) _sumScratch[k] = 0f;
						foreach (int neighbor in neighbors[t])
						{
							if (fields.TotalMass(neighbor) <= 0f) continue;
							if (fields.PlateId[neighbor] != holePlate) continue;
							if (fields.IsLand(neighbor) != anyLand) continue;
							float weight = InverseDistance(centers, neighbor, t) / phaseWeight;
							for (int k = 0; k < 8; k++) _sumScratch[k] += pools[k][neighbor] * weight;
						}
						for (int k = 0; k < 7; k++)
						{
							pools[k][t] = _sumScratch[k];              // 七个质量场：加权平均
							writtenMass += _sumScratch[k];
						}
						pools[7][t] = _sumScratch[7];                  // 年龄同样加权平均（连续过渡）
						fields.PlateId[t] = holePlate;
						CrustCreatedMass += writtenMass;               // 填充 = 自地幔新增（创建账）
						HoleFillAveragedCount++;
						if (anyLand) ResampledCellCount++;
						ChangeEvents.Add((_currentStep, t, -1, holePlate, anyLand ? 2 : 3));
						filled++;
						continue;
					}

					// 无同板料邻居：第一级等下一遍；第二级（兜底）并入多数邻居
					if (!annexFallback) continue;                          // 等下一遍（邻居可能被同板填充）
				}

				if (AnnexToMajority(ball, fields, t, maficMassPerArea)) NewCrustFilledCount++;
				filled++;
			}
			return filled;
		}

		// 空洞的离散发育判定：存在异板邻居 nb，使（本板速度 − nb 速度）沿 t→nb 边界法向的
		// 分量为负且幅值超 SpreadingSpeedKmPerMy = 两板正在撕开。
		// 符号约定与 H3PlateBoundary 同源（v_i − v_j、n̂ = i→j，v_n > 0 ⟺ 汇聚）。本板速度取
		// 空洞同板料邻居的速度均值（空洞自身无料，速度场无意义）。遍历序固定 ⇒ 确定性。
		bool IsDivergentHole(Ball ball, H3PlateFields fields, H3PlateMotion motion, int t, int holePlate)
		{
			var neighbors = ball.CellNeighbors;
			var neighborDirs = ball.CellNeighborDirs;              // 构造期归一（原每个异板邻居投影+归一一次）
			Vector3 vSelf = Vector3.Zero;
			int samePlateCount = 0;
			foreach (int nb in neighbors[t])
			{
				if (fields.TotalMass(nb) <= 0f || fields.PlateId[nb] != holePlate) continue;
				vSelf += motion.Velocity[nb];
				samePlateCount++;
			}
			if (samePlateCount == 0) return false;
			vSelf /= samePlateCount;

			for (int k = 0; k < neighbors[t].Length; k++)
			{
				int nb = neighbors[t][k];
				int nbPlate = fields.PlateId[nb];
				if (nbPlate < 0 || nbPlate == holePlate) continue;
				if (fields.TotalMass(nb) <= 0f) continue;
				Vector3 normal = neighborDirs[t][k];               // t → nb（空洞板 → 异板）
				if (normal == Vector3.Zero) continue;              // 退化边：与现场版 continue 同语义
				Vector3 relative = vSelf - motion.Velocity[nb];
				float convergenceKmPerMy = relative.Dot(normal) * H3PlateBoundary.EarthRadiusKm;
				if (convergenceKmPerMy < -SpreadingSpeedKmPerMy) return true;   // 负收敛 = 撕开
			}
			return false;
		}

		// 权重 = 1/d²（球面弦距平方；d → 0 时取一个有限大值，防除零——空洞与邻居格心必不重合）。
		static float InverseDistance(Vector3[] centers, int from, int to)
		{
			float distanceSquared = (centers[from] - centers[to]).LengthSquared();
			return distanceSquared > 1e-12f ? 1f / distanceSquared : 1e12f;
		}

		// 孤立空洞并入多数料邻居（被异板完全包围的链尾兜底）。返回是否真的填了。
		bool AnnexToMajority(Ball ball, H3PlateFields fields, int t, float maficMassPerArea)
		{
			var neighbors = ball.CellNeighbors;
			int distinct = 0;
			var plateScratch = _annexPlateScratch;                    // 复用实例缓冲
			var countScratch = _annexCountScratch;
			foreach (int neighbor in neighbors[t])
			{
				if (fields.TotalMass(neighbor) <= 0f) continue;
				int plate = fields.PlateId[neighbor];
				int slot = -1;
				for (int k = 0; k < distinct; k++)
					if (plateScratch[k] == plate) { slot = k; break; }
				if (slot < 0) { slot = distinct++; plateScratch[slot] = plate; countScratch[slot] = 0; }
				countScratch[slot]++;
			}
			if (distinct == 0) return false;                              // 本遍够不着（等下一遍邻居被填）
			int majorityPlate = plateScratch[0];
			majorityOf(plateScratch, countScratch, distinct, ref majorityPlate);
			CrustCreatedMass += maficMassPerArea;                       // 兜底注壳也入创建账（对账口径）
			fields.SetNewOceanicCrust(t, maficMassPerArea, majorityPlate);
			ChangeEvents.Add((_currentStep, t, -1, majorityPlate, 1));
			return true;
		}

		// 定长小数组的多数板（计数严格更大者胜；序固定 ⇒ 确定性）。
		static void majorityOf(int[] plates, int[] counts, int distinct, ref int plate)
		{
			int best = counts[0];
			for (int k = 1; k < distinct; k++)
				if (counts[k] > best) { best = counts[k]; plate = plates[k]; }
		}
	}
}
