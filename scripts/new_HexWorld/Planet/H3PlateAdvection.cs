using System;
using System.Collections.Generic;
using Godot;
using World.Tectonics;                 // MaterialDensity
using World.Utils;
using World.Utils.H3;

namespace World.NewHexWorld.Plate
{
	// 板块平流（设计-03 §3.1/§3.2；v1.7 定稿：**流链元胞模型**——用户拍板的目标导向设计）。
	//
	// 搬运：起跳板的每个格沿本板速度方向**走一格**（板级相位决定起跳节奏，起跳频率 =
	// 动力学速度）。意图形成一条条**流链**（同向排列的格串），结算从链头向链尾一趟完成：
	//
//   链头遇异板：本格更密 → **俯冲**（守恒组埋入对方格下、mafic/age 记回地幔、本格腾空
//   = 下盘传送带）；更轻/等重 → **顶死停驻**（本格不动，后方来料叠上来 = 造山）。
	//   链中每格：前移一格，填入前方腾出的格（纯置换，归属随物质走）。
	//   折叠（两格的前驱指向同一格）：同板多料合一。
	//   链尾腾空：**空洞**，由裂谷步骤按本板补料（归属永不因填充改变）。
	//
	// v1.8：链头的俯冲/顶死判据抽到 H3PlateContact.JamsInto（与运动学"顶死零功修正"共用，
	// 防口径漂移），落点邻居选择同样共用 FlowNeighbor。JamCellCount / JamCellsPerPlate 记录
	// **真接触顶死**（链头撞异板物质且不比目标密）——链头撞同板未起跳格是相位停驻，不计。
	//
	// v1.12（2026-09-15，**治乱**：用户拍板"空洞多源都用加权平均来处理"）：本条改动的是
	// 两种离散残差的写出/填充口径，不动搬运规则本身——
	//   ① **空洞 → 邻域加权平均填充**（旧：洋空洞注 0 龄脊轴壳）。旧口径有两处假象：age = 0 的
	//      位移 −2500 m 在模型当前海平面（−2700 ~ −4000 m）下**高出水面** ⇒ 海岸与深海里撒满
	//      "假浅滩/假陆地"单格点；整格复制邻居虽然不出水，却留下同值块斑。现取"同板料邻居里
	//      多数相"那一批按 **1/d² 权重加权平均八场**写入 ⇒ 与周边连续过渡（既不假出水也不块斑）。
	//   ② **同板多源（折叠）→ 质量加权平均写出 + 差额摊出**（旧：八场直接求和）。求和让一个格
	//      瞬间堆出两格料 ⇒ 单格尖峰（碰撞带上是假山、板内是离散残块）。现写出各来料的**质量
	//      加权平均柱**，差额按 1/d² **全额摊给同板邻居**（守恒：只搬不增删）——目标有优先级：
	//      本步**顶死接触前缘格**优先（富余属于造山带，摊在这里 = 沿带生长），其次**同相**邻居
	//      （陆→陆 / 洋→洋，永不改相：首跑实测"摊给任意同板邻居"会把长英质摊进同板大洋边缘、
	//      把世界推成 99.5% 陆地），都够不着则记回地幔（审计闭环）。
	// ⚠️ 两处的**质量账**不变：填充量记创建（自地幔新增），摊出量只搬不增删（既不记创建也不记
	// 消减），摊不出去的才记消减 —— `TotalCrustMass() ≈ Initial + Created − Destroyed` 全程成立。
	//
	// 归属 = 顶层物质的板号，随物质走（用户拍板"不需要归属判断"）；异板色翻转在规则层面不存在：
	// 跨板接触只有俯冲埋入（保持对方色）与顶死（保持己方色）。板内**非主体小连通块**的一致性
	// 清理不在这里，在 H3DynamicTectonics.MergeStrayFragments（只改标签、不动物质）。
	//
	// 质量对账：搬运环节质量守恒（求和/置换/加权平均 + 差额摊出）；质量只经俯冲消减（回地幔）、
	// 差额摊不出去（回地幔）、与空洞填充（自地幔新增）三处进出。
	public sealed class H3PlateAdvection
	{
		public int HoleCount { get; private set; }             // 本步空洞格数（未填，待裂谷）
		public int MixedCellCount { get; private set; }        // 异板混合格数（= 俯冲埋入/汇聚堆叠格）
		public int OverflowCellCount { get; private set; }     // 同板多源格数（= 压缩增厚格）
		public int ResampledCellCount { get; private set; }    // 陆内拉伸重采样补料格数
		/// <summary>本步按"邻域加权平均"填充的空洞格数（04 批次 6 治乱；用户 2026-09-15 口径）。
		/// 与 <see cref="NewCrustFilledCount"/> 一起构成填充路径的判读口（哪个分支在生效）。</summary>
		public int HoleFillAveragedCount { get; private set; }
		/// <summary>本步按"注新洋壳"填充的空洞格数——**只剩兜底分支**（空洞被异板完全包围、
		/// 连一个同板料邻居都没有）。该分支生成 age = 0 的脊轴壳（位移 −2500 m），在模型当前
		/// 海平面（≈ −2700 ~ −4000 m）下高出水面 ⇒ 假陆地/假浅滩。故只保留为兜底。</summary>
		public int NewCrustFilledCount { get; private set; }
		/// <summary>本步同板多源格（一个格收到 ≥2 份同板来料）**改按质量加权平均写出**的格数
		/// （04 批次 6 治乱：不再求和堆尖峰，见 Step 写出段与本类头注）。</summary>
		public int OverflowAveragedCells { get; private set; }
		/// <summary>本步由加权平均差额**摊给邻域**的质量（kg/m² 面密度口径；守恒项，不进创建/消减账）。
		/// 摊不出去的部分（邻域全是无料/异板）记入 <see cref="OverflowToMantleMass"/>。</summary>
		public double OverflowSpilledMass { get; private set; }
		/// <summary>本步加权平均差额里摊不出去、只能记回地幔的部分（并入 RecycledToMantleMass）。</summary>
		public double OverflowToMantleMass { get; private set; }
		public double RecycledToMantleMass { get; private set; }
		// ── 俯冲再循环旋钮（v1.14 陆增棘轮对冲，2026-09-15 用户拍板方案②；持有方 = H3DynamicTectonics，每步推入）──
		/// <summary>埋入层**沉积类**（sediment/sedimentary）随俯冲回地幔的比例。1 = 全额——侵蚀搬进
		/// 海沟的沉积物随板回收（地球沉积物再循环主通道）；0 = 老口径（守恒组永远埋进上盘，
		/// 陆化只进不出 = 陆增棘轮的根因之一）。只作用于"真混合"格（有上盘顶层压着）；全埋入格
		/// （唯一落位 = 埋入层，无俯冲消费）整柱保留，与 buriedLoss 口径一致。</summary>
		public float RecycleSedimentFraction = 1f;
		/// <summary>埋入层**长英质类**（metamorphic/felsic×2）随俯冲回地幔的刮削比例。只作用于被
		/// 俯冲板驮着的薄层长英质（陆源尘）；陆壳本体浮力大、罕被俯冲（密度裁决天然豁免）。</summary>
		public float RecycleFelsicFraction = 0.2f;
		/// <summary>本步随俯冲回地幔的**守恒组**质量（判读口；与 mafic 消减分账，量已并入
		/// <see cref="RecycledToMantleMass"/> 与质量账本）。</summary>
		public double RecycledConservedMass { get; private set; }

		// ── 洋底扩张旋钮（v1.15 陆增棘轮的结构性对冲，2026-09-15 用户拍板方案①；威尔逊旋回引擎）──
		/// <summary>离散边界注新洋壳开关：空洞若位于正在撕开的板块边界（相对速度法向负收敛超阈值），
		/// 不走"多数相加权平均"而直接注 age=0 脊轴新洋壳——洋中脊持续造洋、大陆裂谷长出窄洋
		/// （新洋格由此诞生，陆占比被稀释）。0 龄壳位移 −2500 m，在修正后的海平面（≈ −2.2 km）下
		/// 位于水下 ~300 m，不再有 v1.12 治乱时的"假浅滩"问题。</summary>
		public bool EnableSpreading = true;
		/// <summary>离散发育阈值（km/My）：空洞处本板与异板的相对速度法向**分离**分量超过它才注壳
		/// （真实慢速洋脊半速率 ~0.5 cm/yr = 0.05 km/My 量级）。汇聚/走滑边（正收敛/近零）不注。</summary>
		public float SpreadingSpeedKmPerMy = 0.05f;
		/// <summary>本步在离散边界注入的新洋壳格数（判读口——扩张系统是否在工作的直接读数）。</summary>
		public int SpreadingFilledCount { get; private set; }
		/// <summary>本步**新增**地壳质量（kg/m² 口径总量；04 批次 0 守恒对账口）：洋中脊/兜底填新洋壳
		/// + 陆内重采样复制的整格质量。与 RecycledToMantleMass（消减）一起构成质量进出仅有的两户。</summary>
		public double CrustCreatedMass { get; private set; }
		public int JamCellCount { get; private set; }          // 本步真接触顶死格数（foreign 才计；相位停驻不算）
		/// <summary>本步**陆-陆碰撞**格数（顶死接触且两侧都是陆壳）——周期重启触发③的输入
		/// （platec `continental_collisions` 口径：无陆-陆碰撞太久 ⇒ 没意思了 ⇒ 重启）。</summary>
		public int ContinentalJamCellCount { get; private set; }

		/// <summary>顶死格按板分布（键 = 顶死格的板号；缺口量测口，v1.8）。</summary>
		public IReadOnlyDictionary<int, int> JamCellsPerPlate => _jamCellsPerPlate;
		readonly Dictionary<int, int> _jamCellsPerPlate = new();

		/// <summary>本步板片账户入账（04 批次 2；键 = 俯冲来料板）：质量 = 俯冲格 mafic 面密度，
		/// 方向 = 来料格→目标格切向单位向量（质量加权累计由 H3PlateMotion.AddSlab 承担）。</summary>
		public IReadOnlyDictionary<int, (double mass, Vector3 dir)> SlabInflow => _slabInflow;
		readonly Dictionary<int, (double mass, Vector3 dir)> _slabInflow = new();

		/// <summary>本步逐板对**顶死接触计数**（04 批次 4；键 = (小板号, 大板号)）：缝合触发器的
		/// 输入——只有"实际碰撞"的边界才累积缝合资格，单纯邻接（转换边/离散边）不算。</summary>
		public IReadOnlyDictionary<(int a, int b), int> JamPairCounts => _jamPairs;
		readonly Dictionary<(int, int), int> _jamPairs = new();

		/// <summary>本步顶死格清单（04 批次 4；增生楔的落点 = 造山前缘）。</summary>
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
		readonly float[] _averageScratch = new float[8];       // 加权平均柱暂存（同板多源写出）
		readonly List<int> _spillCells = new();                // 待摊差额的格（Step 末尾统一摊给邻域）
		readonly List<float[]> _spillExcess = new();           // 对应七场差额（数组走池复用，避免每步分配）
		readonly Stack<float[]> _spillPool = new();
		readonly bool[] _jamMark;              // 本步顶死接触格标记（差额摊出的档①目标；Pass C 写入）
		readonly int[] _annexPlateScratch = new int[6];   // AnnexToMajority 逐邻居板分类暂存（不分配）
		readonly int[] _annexCountScratch = new int[6];

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
			_jamMark = new bool[cellCount];
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
			_currentStep = stepIndex;

			// ── 复位 ──
			for (int i = 0; i < n; i++)
			{
				_successor[i] = i;
				_moves[i] = false;
				_predFirst[i] = -1;
				_settled[i] = false;
				_depositHead[i] = -1;
				_depositBuried[i] = false;
				_holePlate[i] = -1;
				_jamMark[i] = false;
			}
			_settledQueue.Clear();
			HoleCount = 0; MixedCellCount = 0; OverflowCellCount = 0;
			ResampledCellCount = 0; RecycledToMantleMass = 0; JamCellCount = 0; CrustCreatedMass = 0;
			HoleFillAveragedCount = 0; NewCrustFilledCount = 0;
			OverflowAveragedCells = 0; OverflowSpilledMass = 0; OverflowToMantleMass = 0;
			RecycledConservedMass = 0;
			ContinentalJamCellCount = 0;
			_jamCellsPerPlate.Clear();
			_slabInflow.Clear();
			_jamPairs.Clear();
			_jamContacts.Clear();
			ChangeEvents.Clear();

			var srcPools = source.AllPools();
			var fired = motion.PlateFired;
			var velocity = motion.Velocity;

			// ── Pass A：起跳格的流向后继（意图）──
			for (int i = 0; i < n; i++)
			{
				int plate = source.PlateId[i];
				if (plate < 0) continue;                           // 无料格
				if (plate >= fired.Length || !fired[plate]) continue;   // 本板本步未起跳
				Vector3 v = velocity[i];
				float speedRadPerMy = v.Length();
				if (speedRadPerMy <= 1e-9f) continue;              // 速度为零 → 原地
				Vector3 radial = centers[i].Normalized();
				Vector3 dir = v - radial * v.Dot(radial);
				float dirLength = dir.Length();
				if (dirLength <= 1e-12f) continue;
				dir /= dirLength;
				int best = H3PlateContact.FlowNeighbor(centers, neighbors, i, dir);
				if (best < 0 || best == i) continue;
				_successor[i] = best;
				_moves[i] = true;                                  // 该格本步移动一格
			}

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
			// 收前驱叠放（**造山堆厚**）。⚠️ 链中格（后继同板且在动）**不在此结算**——等队列
			// 传播（04 批次 4 修：旧代码 else 无 `_moves[t]` 门控，链中格被原地结算 ⇒ 队列/环
			// 传播成死代码、同板多源恒 0、造山堆厚结构性不可能）。
			for (int i = 0; i < n; i++)
			{
				if (!_moves[i]) continue;
				int t = _successor[i];
				int plateI = source.PlateId[i];
				int plateT = source.PlateId[t];
				bool foreign = plateT >= 0 && plateT != plateI;

				if (foreign && !H3PlateContact.JamsInto(
					motion.DensityFor(source, i, material), motion.DensityFor(source, t, material)))
				{
					Deposit(i, t, buried: true);                   // 俯冲：守恒组埋入对方格下
					double subductedMafic = source.MaficVolcanic[i] + source.MaficPlutonic[i];
					// 板片账户入账（04 批次 2）：质量 × 流向（i→t 切向单位向量）——已俯冲的物质
					// 下步继续拉本板（testproject T1：回地幔 ≠ 拉力消失）。
					// ⚠️ mafic 的"回地幔"记账不在 Pass C 做——埋入物质是否真被压掉取决于写出段的
					// 落位（修复 04 批次 2 抓到的双记账：目标格自身移走时，唯一落位 = 埋入层本身，
					// 物质保留在场上，此时若已记消减 = 质量凭空消失）。
					Vector3 delta = centers[t] - centers[i];
					Vector3 radialI = centers[i].Normalized();
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
						JamCellCount++;                            // 真接触顶死（相位停驻不计，v1.8 量测口）
						_jamMark[i] = true;                        // 差额摊出的档①目标（04 批次 6）
						if (source.IsLand(i) && source.IsLand(t)) ContinentalJamCellCount++;   // 陆-陆碰撞（platec 口径）
						int plate = source.PlateId[i];
						_jamCellsPerPlate[plate] = _jamCellsPerPlate.GetValueOrDefault(plate) + 1;
						_jamContacts.Add((i, plate));              // 04 批次 4：前缘清单（增生楔落点）
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

			// 环：未被链头覆盖的闭合流环（纯旋转）→ 沿环旋转一格（纯置换）。⚠️ 环格结算后
			// **也要入队**（04 批次 4 修：否则喂进环的前驱链永远等不到结算——物质凭空蒸发，
			// 全程实测 39% 质量丢失）。
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
					// 无沉积落位：整格清写（04 批次 4 修——**无条件**，包括"无料格"：双缓冲乒乓下
					// 跳过写入会复活两步前的陈旧质量（幽灵格），实测全程账面失守 4×；8 个浮点写
					// 的代价换掉整类幽灵）。有料格腾空 = 空洞（待裂谷恒等填充）。
					if (wasMaterial)
					{
						_holePlate[t] = source.PlateId[t];             // 链尾板 = 恒等填充归属
						HoleCount++;
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
						// 04 批次 4 修（真泄漏根因）：**被顶替的前胜者也是败者**——顶层裁决的
						// "比较-覆盖"只给"当前不胜"者记账，被后来更轻者顶掉的前胜者 mafic 既没写出
						// 也没记消减（实测三源混合链每步蒸发格级质量，全程账面失守）。顶替时补记。
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

				// 04 批次 4 补全（批次 2 修复的漏网分支）：混合格但**无非埋入沉积**（俯冲目标
				// 自身移走、唯一落位 = 埋入层）——顶层裁决无人参赛（topPlate=-1）。此时唯一内容
				// = 埋入层本身 → 按同板口径整柱保留（mafic 随求和写出、不记消减、归属 = 埋入板）。
				// ⚠️ 不修则写出 mass>0 且 PlateId=-1 的孤儿格：下步平流当无料跳过 + 写出段跳过
				// 写入 → 双缓冲乒乓复活陈旧质量（实测全程账面失守 4×）。
				// 全埋入格（无非埋入沉积）：唯一内容 = 埋入层本身 ⇒ 整柱保留（按同板口径写出）。
				// ⚠️ 此时**不能**把埋入 mafic 记为回地幔——它已在写出段被完整保留（04 批次 4 修：
				// 旧写法"记销毁 + 写出"重复记账，实测账面多计 8.9e8）。
				bool allBuried = false;
				if (mixed && topPlate < 0) { mixed = false; allBuried = true; }
				if (!allBuried)
				{
					RecycledToMantleMass += buriedLoss;

					// v1.14 俯冲再循环（陆增棘轮对冲）：埋入层守恒组按比例回地幔——沉积类全额、
					// 长英质类按刮削比例；从写出量里逐池扣掉。仅"真混合"（有上盘顶层压着）生效，
					// 全埋入格整柱保留不回收，与 buriedLoss 同一口径。
					double recycled = _buriedConserved[0] * (double)RecycleSedimentFraction
						+ _buriedConserved[1] * (double)RecycleSedimentFraction
						+ _buriedConserved[2] * (double)RecycleFelsicFraction
						+ _buriedConserved[3] * (double)RecycleFelsicFraction
						+ _buriedConserved[4] * (double)RecycleFelsicFraction;
					if (recycled > 0)
					{
						RecycledToMantleMass += recycled;
						RecycledConservedMass += recycled;
						_sumScratch[0] -= (float)(_buriedConserved[0] * RecycleSedimentFraction);
						_sumScratch[1] -= (float)(_buriedConserved[1] * RecycleSedimentFraction);
						_sumScratch[2] -= (float)(_buriedConserved[2] * RecycleFelsicFraction);
						_sumScratch[3] -= (float)(_buriedConserved[3] * RecycleFelsicFraction);
						_sumScratch[4] -= (float)(_buriedConserved[4] * RecycleFelsicFraction);
					}
				}

				// 同板多源（= 压缩增厚格）改**质量加权平均**写出 + 差额摊给邻域（04 批次 6 治乱；
				// 用户 2026-09-15 口径）。旧口径是八场直接求和 ⇒ 一个格瞬间堆出两格料 ⇒ 尖峰
				// （碰撞带上表现为单格拔高的假山、板内表现为离散残差堆块）。
				// 新口径：本格写出各来料的**质量加权平均柱**（厚度/年龄/相都取加权平均 = 与来料
				// 连续过渡），差额（Σ − 加权平均）按 1/d² 权重**摊给同板有料邻居**（守恒：只搬不
				// 增删，不进创建/消减账）。总量守恒由"权重和 = 1 的全额摊出"保证。
				bool averagedOverflow = !mixed && depositCount > 1 && AverageOverflowCell(source, t, head);

				for (int k = 0; k < 5; k++) tgtPools[k][t] = _sumScratch[k];   // 守恒组（平均值已代回 _sumScratch）
				if (!mixed)
				{
					tgtPools[5][t] = _sumScratch[5];               // 同板：mafic 也守恒（或加权平均）
					tgtPools[6][t] = _sumScratch[6];
					tgtPools[7][t] = averagedOverflow ? _sumScratch[7] : ageMax;   // 平均口径下年龄同为加权平均
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

			// 摊出加权平均的差额（必须在整轮写出之后：邻居格本步的写出不被本步摊入覆盖）
			SpillOverflowExcess(ball, target);

		}

		// ── 同板多源：质量加权平均写出 + 差额摊给邻域（04 批次 6 治乱）──
		// 输入：_sumScratch[0..6] = 各来料八场之和（已在写出段算好）。本方法把 _sumScratch 就地
		// 改写成**质量加权平均柱**，并把差额按 1/d² 权重排进摊出队列（Step 末尾 SpillOverflowExcess
		// 全额摊给同板有料邻居 ⇒ 全球质量守恒，差额不进创建/消减账）。
		// 权重 = 各来料的总质量占比（kg/m² 面密度；总质量为零的退化情形直接返回 false 走原求和）。
		bool AverageOverflowCell(H3PlateFields source, int t, int head)
		{
			var srcPools = source.AllPools();
			double totalMass = 0;
			for (int k = 0; k < 7; k++) totalMass += _sumScratch[k];
			if (totalMass <= 0) return false;

			for (int k = 0; k < 8; k++) _averageScratch[k] = 0f;
			for (int d = head; d != -1; d = _depositNext[d])
			{
				double mass = 0;
				for (int k = 0; k < 7; k++) mass += srcPools[k][d];
				float weight = (float)(mass / totalMass);
				for (int k = 0; k < 8; k++) _averageScratch[k] += srcPools[k][d] * weight;   // Age 也按质量权重平均
			}

			float[] excess = _spillPool.Count > 0 ? _spillPool.Pop() : new float[7];
			double excessTotal = 0;
			for (int k = 0; k < 7; k++)
			{
				excess[k] = _sumScratch[k] - _averageScratch[k];       // 差额（≥ 0：平均 ≤ 和）
				if (excess[k] < 0f) excess[k] = 0f;
				excessTotal += excess[k];
				_sumScratch[k] = _averageScratch[k];                   // 就地换成平均值
			}
			_sumScratch[7] = _averageScratch[7];
			if (excessTotal > 0)
			{
				_spillCells.Add(t);
				_spillExcess.Add(excess);
			}
			else
			{
				_spillPool.Push(excess);                               // 无差额 → 数组还池
			}
			OverflowAveragedCells++;
			return true;
		}

		// 把加权平均的差额全额摊给同板邻居（权重 = 1/d²，和归一 ⇒ 全额摊出 = 守恒）。
		// 目标选择（04 批次 6 首跑实测的教训：**不能摊给任意同板邻居**——大陆碰撞的富余长英质
		// 会被摊进同板的大洋边缘，凡沾到一点长英质就恒判为陆（`IsLand = felsic > 0`），实测把
		// 世界推成 99.5% 陆地）。两档目标：
		//   ① 本步**顶死接触格**（碰撞前缘）优先——富余质量属于造山带，摊在这里 = 沿带生长；
		//   ② 没有前缘邻格时，只摊给**同相**（陆→陆 / 洋→洋）的同板有料邻居 ⇒ 永不改相。
		// 两档都够不着（孤立多源格）⇒ 记回地幔（并入 RecycledToMantleMass，保持审计闭环）。
		void SpillOverflowExcess(Ball ball, H3PlateFields target)
		{
			if (_spillCells.Count == 0) return;
			var neighbors = ball.CellNeighbors;
			var centers = ball.CellCenters;
			var tgtPools = target.AllPools();
			for (int s = 0; s < _spillCells.Count; s++)
			{
				int t = _spillCells[s];
				float[] excess = _spillExcess[s];
				double excessTotal = 0;
				for (int k = 0; k < 7; k++) excessTotal += excess[k];
				int plate = target.PlateId[t];
				bool land = target.IsLand(t);

				// 档①：顶死接触格邻格（本步 Pass C 已标记）——富余质量属于造山带，摊在这里 = 沿带生长
				float weightSum = 0f;
				foreach (int nb in neighbors[t])
					if (SpillEligible(target, nb, t, plate, land, jamOnly: true))
						weightSum += InverseDistance(centers, nb, t);
				bool jamTier = weightSum > 0f;
				// 档②：没有前缘邻格 → 只摊给**同相**（陆→陆 / 洋→洋）的同板有料邻居 ⇒ 永不改相
				if (!jamTier)
					foreach (int nb in neighbors[t])
						if (SpillEligible(target, nb, t, plate, land, jamOnly: false))
							weightSum += InverseDistance(centers, nb, t);

				if (weightSum > 0f)
				{
					foreach (int nb in neighbors[t])
					{
						if (!SpillEligible(target, nb, t, plate, land, jamTier)) continue;
						float weight = InverseDistance(centers, nb, t) / weightSum;
						for (int k = 0; k < 7; k++) tgtPools[k][nb] += excess[k] * weight;
					}
					OverflowSpilledMass += excessTotal;
				}
				else
				{
					OverflowToMantleMass += excessTotal;               // 摊不出去：回地幔（审计闭环）
					RecycledToMantleMass += excessTotal;
				}
				_spillPool.Push(excess);
			}
			_spillCells.Clear();
			_spillExcess.Clear();
		}

		// 摊出资格：非自身、有料、同板、**同相**（两档都要求——首跑实测把洋格富余摊进陆格前缘
		// 会制造新的相变格）；档①另需顶死接触标记。
		bool SpillEligible(H3PlateFields target, int nb, int t, int plate, bool land, bool jamOnly)
		{
			if (nb == t) return false;
			if (target.TotalMass(nb) <= 0f) return false;
			if (target.PlateId[nb] != plate) return false;
			if (target.IsLand(nb) != land) return false;
			if (jamOnly) return _jamMark[nb];
			return true;
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

		/// <summary>空洞补料（03 §2.2 第 5 步；**恒等填充**）：链尾空洞由链尾板补料——
		/// 有本板陆邻居则重采样本板陆壳（陆内拉伸接续），否则填本板新洋壳（洋中脊生长）。
		/// 兜底：被异板完全包围的孤立空洞按多数邻居并入。**填充永不改变归属**。
		/// 多遍填充：一遍只够得着"有料邻居"的空洞；2 格以上的空洞簇要下一遍才够得着。</summary>
		public int FillHolesWithNewOceanicCrust(Ball ball, H3PlateFields fields, H3PlateMotion motion,
			float maficMassPerArea)
		{
			SpreadingFilledCount = 0;
			int filled = 0;
			while (true)                                                   // 第一级：恒等填充
			{
				int pass = FillOneHoleLayer(ball, fields, motion, maficMassPerArea, false);
				filled += pass;
				if (pass == 0) break;
			}
			while (true)                                                   // 第二级：孤立空洞并入多数邻居
			{
				int pass = FillOneHoleLayer(ball, fields, motion, maficMassPerArea, true);
				filled += pass;
				if (pass == 0) break;
			}
			HoleCount -= filled;
			return filled;
		}

		// 单遍：把"材料为空"的格按链尾板恒等补上。annexFallback = true 时，
		// 恒等够不着的孤立空洞（被异板完全包围）并入多数邻居。
		//
		// ⚠️ **04 批次 6 治乱：空洞按"邻域加权平均"填充**（用户 2026-09-15 拍板口径）。
		// 旧写法对洋空洞注 age = 0、7100 m 的脊轴壳 ⇒ 位移 −2500 m，而模型海平面解在 −2700 ~ −4000 m
		// ⇒ **新壳出水**：海岸与深海里撒满"假浅滩/假陆地"单格点，陆占比被这条通道单向推高。
		// 复制最近邻居虽然不出水，但会留下"整块复制的同值斑"（块状假象）。
		// 现口径：取同板料邻居里**多数相**（陆多 → 陆；洋多 → 洋）的那一批，按 **1/d² 权重**加权
		// 平均八场写入 —— 厚度/年龄/密度都与周边连续过渡，既不出水也不留块斑。
		// 质量账：写入的质量记创建账（与复制同口径：填充本就是"自地幔新增"）。
		// ⚠️ v1.15 洋底扩张（方案①）：**离散边界的空洞先于多数相判定**——空洞任一异板邻居与本板的
		// 相对速度沿边界法向的分离分量超阈值（正在撕开）⇒ 直接注 age=0 脊轴新洋壳（威尔逊旋回引擎）。
		// 当时的"假浅滩"顾虑已随海平面分母修正消失（−2500 m 壳如今在水下 ~300 m）。陆内裂谷由此
		// 长出窄洋——大陆被撕开、新洋格稀释陆占比，这是对陆增棘轮的结构性对冲。
		int FillOneHoleLayer(Ball ball, H3PlateFields fields, H3PlateMotion motion, float maficMassPerArea,
			bool annexFallback)
		{
			int n = ball.CellIds.Length;
			var neighbors = ball.CellNeighbors;
			var centers = ball.CellCenters;
			var pools = fields.AllPools();
			int filled = 0;
			for (int t = 0; t < n; t++)
			{
				if (fields.TotalMass(t) > 0f) continue;                    // 有料 → 不是空洞
				int holePlate = _holePlate[t];

				// 链尾板明确 → 恒等填充（归属永不因填充改变）
				if (holePlate >= 0)
				{
					// v1.15 洋底扩张：离散边界的空洞注 age=0 脊轴新洋壳（归属仍 = 链尾板）。
					// 相变在这里是**有意为之**：陆内裂谷长出窄洋 = 威尔逊旋回的起点。
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

		// 空洞的离散发育判定（v1.15 洋底扩张）：存在异板邻居 nb，使（本板速度 − nb 速度）沿
		// t→nb 边界法向的分量为负且幅值超 <see cref="SpreadingSpeedKmPerMy"/> = 两板正在撕开。
		// 符号约定与 H3PlateBoundary 同源（v_i − v_j、n̂ = i→j，v_n > 0 ⟺ 汇聚）。本板速度取
		// 空洞同板料邻居的速度均值（空洞自身无料，速度场无意义）。遍历序固定 ⇒ 确定性。
		bool IsDivergentHole(Ball ball, H3PlateFields fields, H3PlateMotion motion, int t, int holePlate)
		{
			var neighbors = ball.CellNeighbors;
			var centers = ball.CellCenters;
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

			foreach (int nb in neighbors[t])
			{
				int nbPlate = fields.PlateId[nb];
				if (nbPlate < 0 || nbPlate == holePlate) continue;
				if (fields.TotalMass(nb) <= 0f) continue;
				Vector3 delta = centers[nb] - centers[t];
				Vector3 radial = centers[t].Normalized();
				Vector3 tangential = delta - radial * delta.Dot(radial);
				if (tangential.LengthSquared() <= 1e-12f) continue;
				Vector3 normal = tangential.Normalized();              // t → nb（空洞板 → 异板）
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
			var plateScratch = _annexPlateScratch;                    // 04 批次 1：提为实例字段（原每次 new int[6]×2）
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
