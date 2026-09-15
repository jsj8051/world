using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.Utils;
using World.Utils.H3;

namespace World.NewHexWorld.Plate
{
	// 板表行（设计入口 §3.2）：每板 = Id、种子格心方向（SeedDir）、陆性标记 IsLand、欧拉极 Ω。
	// ⚠️ 03 起语义变更：板表的陆性不再是"板属性"（动态模型里**一块板可以同时有陆有洋**，
	// 陆性由逐格地壳决定）——`IsLand` 改报"该板过半格为陆"的**派生统计**，仅供 UI 显示；
	// `Omega` 改报该板**最后一步的旋转向量**（rad/My，诊断/判读用），不再是"抽出来的欧拉极"。
	public sealed class PlateRecord
	{
		public int Id;            // 板块号（下标 = Plates 数组位）
		public Vector3 SeedDir;   // 种子格心方向（单位向量；诊断/将来扩展用）
		public bool IsLand;       // 陆性标记——海陆 = 板块属性（陆性板 → 大陆块，二元跳变）
		public Vector3 Omega;     // 欧拉极（rad/My）；v = Ω × r，地形只依赖板间 Ω 之差（02 §2.2）
	}

// H3 球面地壳生成（**03 起改走动态路线**）：初始分板（抽种子格 + **加权随机生长**，
// 2026-09-15 拍板：泛洪式，边界有机——弃撒点-合并 Voronoi 的细胞感）→ 交给 `H3DynamicTectonics` 跑 600 My 板块演化
// （浮力驱动运动 → 物质层阻挡平流 → 裂谷 → 俯冲 → 均衡 → 水量守恒海平面）→ 结果写回 Crust 六场。
// ⚠️【演化暂停 2026-09-15】板块演化先注释（BeginCreatePlates 内 _totalSteps = 0），
// 生成 = 初始分板 + 初始地壳 + 写回；恢复演化还原那一行即可。
// 三个类（PlateMotion / PlateBoundary / LandformGenerator）已删；H3PlateTerritory（领土场）
// v1.4 删——**归属随物质走**（用户拍板"想象的方法不需要归属判断"），`PlateId` = 格内顶层物质的板号。
// 输出：Crust 六场（与 Ball.CellIds 对齐）+ Plates 板表 + H3PlateBoundary 板缘分类；
// 另附边界提取静态口（01 §4，渲染派生数据）：链（ExtractBoundaryChains，诊断/对账基准）
// 与描边格边集（ExtractBoundaryCellEdges，渲染在用）—— 两者只依赖 plateId，与路线无关。
	public class H3Plate
	{
		// ── 常量（设计入口 §3.3；判读后按需调）──
		public const float LandFelsicThicknessM = 35000f;   // 陆性板长英质厚（m）
		public const float OceanMaficThicknessM = 7000f;    // 洋性板镁铁质厚（m）
		public const float LandElevationM = 800f;           // 陆性板海拔初值（m，≈地球大陆平均 ~840 取整）
		public const float OceanDepthM = 3700f;             // 洋性板水深（海拔存负值；≈地球海洋平均 ~3688）
		public const float ContinentalAgeMy = 1000f;        // 陆壳年龄（My 占位标记；无演化）
		public const float OceanicAgeMy = 0f;               // 洋壳年龄初值（01 两级模板；02 §4 起洋区随即被热沉降年龄覆盖）

		// ── 模拟旋钮（判读后按需调；默认 = 老实现默认档）──
		public float RunMy = 600f;             // 总时长（My）
		public float StepMy = 4f;              // 时间步（My）
		public float OceanScale = 1f;          // 水量系数（× 2000 m 全球平均水深基准）
		public float LandOceanNoiseBlend = 0.7f;   // 初始陆洋混合（0=整板陆/洋、1=纯噪声斑块；语义见 H3DynamicTectonics）

		/// <summary>分板生长率幂指数（加权随机生长）：每板生长率 = 0.05 + 0.95·u^此值（u ~ U(0,1)，逐板一掷）。
		/// 0 = 全板近等速（只剩随机漂移的温和大小差）；越大 = 板块大小越悬殊（"巨板 + 一串小板"）。
		/// 默认 1.5 = 明显悬殊档。0.05 地板防"一步没长就被围死"的单格微型板。</summary>
		public float GrowthRateExponent = 1.5f;

		public Crust Crust { get; private set; }        // 六场（生成后只读；UI 经 MapMode 派生，逻辑层唯一权威）
		public PlateRecord[] Plates { get; private set; }   // 板表（下标 = 板 Id）
		public H3PlateBoundary Boundary { get; private set; }  // 逐格板缘分类（动态速度场口径；UI 行 + 后续地形带挂载口）
		public H3DynamicTectonics Simulation { get; private set; }   // 动态模拟本体（诊断/后续批次取数口）
		public int NumPlates { get; private set; }
		public float InitialOceanFraction { get; private set; }   // 初始地壳的海洋格占比（老 landFrac 的对应物）

		readonly Ball _ball;

		public H3Plate(Ball ball)
		{
			_ball = ball;
		}

		/// <summary>生成全球地壳场（**动态路线**，设计-03 §2.2）：初始分板 → 板块演化 → 写回六场 + 板缘分类。
		/// 同 seed 同参数逐位一致（模拟各步的遍历序与浮点累加序都固定）。
		/// 04 批次 5：内部走 Begin/Advance/Finish 三段——同步调用与分帧调用**同一路径**（产物一致）。</summary>
		/// <param name="oceanFraction">初始地壳的海洋格占比（老 landFrac 的对应物；默认 0.6）。</param>
		public void CreatePlates(int numPlates, int seed, float oceanFraction = 0.6f)
		{
			BeginCreatePlates(numPlates, seed, oceanFraction);
			while (AdvanceCreation(int.MaxValue)) { }        // 同步路径：一次跑完
			FinishCreatePlates();
		}

		// ── 分帧生成三段口（04 批次 5；编辑期不再一次阻塞数秒）──

		/// <summary>阶段一：参数校验 + 初始分板 + 模拟初始化（时间步不在此跑）。</summary>
		public void BeginCreatePlates(int numPlates, int seed, float oceanFraction = 0.6f)
		{
			// 参数校验前置：任何实例状态被写之前定局（抛异常时对象保持干净，可安全换参重试）
			if (numPlates < 2) throw new ArgumentOutOfRangeException(nameof(numPlates), "板块数至少 2");
			int cellCount = _ball.CellIds.Length;
			if (numPlates > cellCount)
				throw new ArgumentOutOfRangeException(nameof(numPlates), $"板块数 {numPlates} 不能超过格数 {cellCount}");

			NumPlates = numPlates;
			InitialOceanFraction = oceanFraction;

			// 【演化恢复 2026-09-15】此前暂停演化只留初始化（_totalSteps = 0），现按用户要求恢复，
			// 并已并入自旧项目移植的侵蚀/沉积步（见 H3DynamicTectonics.Step 流水线）。
			_totalSteps = Math.Max(1, (int)(RunMy / StepMy));
			_stepsDone = 0;

			// 1. 初始分板（抽种子格 + 加权随机生长；03 §5 改进 7 的复用口）
			int[] plateOfCell = SplitIntoPlates(numPlates, seed);

			// 2. 板块演化准备（03 §2.2 流水线：老化 → 运动 → 阻挡平流 → 裂谷 → 俯冲记账 → 均衡 → 挠曲 → 海平面）
			Simulation = new H3DynamicTectonics(_ball)
			{
				RunMy = RunMy,
				StepMy = StepMy,
				OceanScale = OceanScale,
				OceanFraction = oceanFraction,
				LandOceanNoiseBlend = LandOceanNoiseBlend,
				// 重启循环的重分板口（04 批次 4）：僵局触发时全部重分板、物质场保留（seed 派生自
				// RestartCount，同种子可复现）；超大陆旋回的兜底生成器。
				Repartition = k => SplitIntoPlates(k, seed + 977 * Simulation.RestartCount),
			};
			Simulation.Initialize(plateOfCell, seed);
		}

		int _totalSteps, _stepsDone;

		/// <summary>阶段二：推进至多 <paramref name="maxSteps"/> 步；返回是否**仍未完成**（true = 还要继续）。</summary>
		public bool AdvanceCreation(int maxSteps = 8)
		{
			if (Simulation == null) return false;
			int budget = Math.Max(1, maxSteps);
			while (_stepsDone < _totalSteps && budget-- > 0)
			{
				Simulation.Step();
				_stepsDone++;
			}
			return _stepsDone < _totalSteps;
		}

		/// <summary>生成进度 ∈ [0,1]（分帧路径显示用）。</summary>
		public float CreationProgress => _totalSteps <= 0 ? 1f : Math.Clamp(_stepsDone / (float)_totalSteps, 0f, 1f);

		/// <summary>阶段三：板缘分类 + 构造地形带 + 写回六场 + 板表。</summary>
		public void FinishCreatePlates()
		{
			// 3. 板缘分类（逐格主导边；UI 行 + 地形带的速率口径）→ 构造地形带（海沟/弧，04 批次 3）
			int n = _ball.CellIds.Length;
			Boundary = new H3PlateBoundary(n);
			Boundary.Build(_ball, Simulation.Fields, Simulation.Motion.Velocity);
			Simulation.ApplyBoundaryRelief();

			// 4. 结果写回 Crust 六场（渲染与地图模式唯一认的口径）
			Crust = new Crust
			{
				PlateId = new int[n],
				FelsicThick = new float[n],
				MaficThick = new float[n],
				SedimentThick = new float[n],
				Age = new float[n],
				Elevation = new float[n],
			};
			Simulation.WriteToCrust(Crust);

			// 5. 板表：SeedDir = 该板格心均值方向；IsLand = 过半格为陆的**派生统计**（动态模型里
			//    一块板可以同时有陆有洋，这里只为 UI 显示）；Omega = 最后一步的旋转向量（诊断用）。
			//    ⚠️ 04 批次 4：缝合/裂解/重启会改变板数与板号（裂解的新板号 ≥ 初始板数）——
			//    表长按**终态最大板号 + 1**取（NumPlates 仍报初始板数；空位 totalCount = 0）。
			int maxPlateId = -1;
			for (int i = 0; i < n; i++)
			{
				int p = Crust.PlateId[i];
				if (p > maxPlateId) maxPlateId = p;
			}
			int tableLength = Math.Max(maxPlateId + 1, NumPlates);
			Plates = new PlateRecord[tableLength];
			var landCount = new int[tableLength];
			var totalCount = new int[tableLength];
			var directionSum = new Vector3[tableLength];
			for (int i = 0; i < n; i++)
			{
				int p = Crust.PlateId[i];
				if (p < 0 || p >= tableLength) continue;
				totalCount[p]++;
				if (Crust.IsLand(i)) landCount[p]++;
				directionSum[p] += _ball.CellCenters[i];
			}
			for (int p = 0; p < tableLength; p++)
			{
				Plates[p] = new PlateRecord
				{
					Id = p,
					SeedDir = totalCount[p] > 0 ? directionSum[p] / totalCount[p] : Vector3.Zero,
					IsLand = totalCount[p] > 0 && landCount[p] * 2 >= totalCount[p],
					Omega = Simulation.LastRotationVector(p),
				};
			}
		}

		// ── 生成内部步骤 ──────────────────────────────────────────────

		/// <summary>初始分板（用户 2026-09-15 拍板改**加权随机生长**，替换 09-12 的撒点-合并式——
		/// Voronoi 细胞感太重）：
		/// ① 随机抽 <paramref name="numPlates"/> 个种子格（板号 = 种子下标，天然 0..P-1 且无空板）；
		/// ② 每板掷一个生长率 rate = u^<see cref="GrowthRateExponent"/>（u ~ U(0,1)；指数 0 = 等速，
		///    越大大小越悬殊）；
		/// ③ 加权前沿生长：反复"按 rate×前沿格数 抽板 → 该板从前沿随机抽一格**贴邻认领**"，铺满全球为止。
		///    前沿越长的板越常被抽中 = 富者愈富，大小悬殊与蜿蜒边界自然涌现；
		/// ④ 逐格贴邻认领 ⇒ 每板天然单连通——不需要板号重排、多数决平滑、连通性清理。
		/// 全程确定性（rng 消耗序固定、前沿出入序固定）。
		/// **动态板块模拟的初始条件复用口**（03 §5 改进 7）；只返回每格归属板，不跑静态地形管线。</summary>
	public int[] SplitIntoPlates(int numPlates, int seed)
	{
		NumPlates = numPlates;
		var rng = new DeterministicRandom(seed);
		int[] seeds = PickSeedCells(rng, cellCount: _ball.CellIds.Length, count: numPlates);

		var rates = new float[numPlates];
		for (int p = 0; p < numPlates; p++)
		{
			float u = MathF.Max((float)rng.NextDouble(), 1e-4f);   // 防下溢出 0（0 的幂 = 恒零生长率）
			rates[p] = 0.05f + 0.95f * MathF.Pow(u, GrowthRateExponent);   // 地板 0.05：最衰的板也有可见体量
		}
		return GrowPlates(seeds, rates, rng);
	}

	// 加权前沿生长本体。frontiers[p] = 板 p 的候选格表（贴邻本板已认领格的未认领格；含**过期项**——
	// 格被别板抢先认领后残留的条目，弹出时惰性丢弃，免去实时去重）。每轮：
	// ① 按 weight_p = rate_p × 前沿长度 加权抽板（前沿长 = 边界多 = 长得快，富者愈富）；
	// ② 抽中板从前沿随机抽一格：已认领 → 丢弃重抽；未认领 → 认领，其未认领邻居入列。
	// "全板前沿同空而未铺满"在球面连通图上不可达（已认领区恒连通 ⇒ 未认领区必邻某板前沿）；
	// total ≤ 0 的防御出口只保不崩（留无主格让测试抓），不保铺满。
	int[] GrowPlates(int[] seeds, float[] rates, DeterministicRandom rng)
	{
		int n = _ball.CellIds.Length;
		var neighbors = _ball.CellNeighbors;
		var plateOfCell = new int[n];
		Array.Fill(plateOfCell, -1);
		var frontiers = new List<int>[seeds.Length];
		for (int p = 0; p < seeds.Length; p++)
		{
			plateOfCell[seeds[p]] = p;
			frontiers[p] = new List<int>();
			foreach (int nb in neighbors[seeds[p]])
				if (plateOfCell[nb] < 0) frontiers[p].Add(nb);
		}

		int claimed = seeds.Length;
		while (claimed < n)
		{
			double total = 0;
			for (int p = 0; p < frontiers.Length; p++) total += (double)rates[p] * frontiers[p].Count;
			if (total <= 0) break;                                 // 防御（不可达）：无处可长
			double pick = rng.NextDouble() * total;
			double acc = 0;
			int chosen = frontiers.Length - 1;                     // fp 兜底：pick 贴 total 上界时归末板
			for (int p = 0; p < frontiers.Length; p++)
			{
				acc += (double)rates[p] * frontiers[p].Count;
				if (pick < acc) { chosen = p; break; }
			}

			var frontier = frontiers[chosen];
			int cell = -1;
			while (frontier.Count > 0)
			{
				int idx = rng.Next(frontier.Count);
				cell = frontier[idx];
				frontier[idx] = frontier[frontier.Count - 1];      // swap-remove：O(1) 弹随机位
				frontier.RemoveAt(frontier.Count - 1);
				if (plateOfCell[cell] < 0) break;                  // 活格 → 认领
				cell = -1;                                         // 过期项 → 丢弃重抽
			}
			if (cell < 0) continue;                                // 前沿全过期 → 下轮重抽板

			plateOfCell[cell] = chosen;
			claimed++;
			foreach (int nb in neighbors[cell])
				if (plateOfCell[nb] < 0) frontiers[chosen].Add(nb);
		}
		return plateOfCell;
	}

	// 无重复抽 count 个撒点格：partial Fisher-Yates 洗前 count 位（rng 消耗固定 count 次 → 确定性）。
		int[] PickSeedCells(DeterministicRandom rng, int cellCount, int count)
		{
			var deck = new int[cellCount];
			for (int i = 0; i < cellCount; i++) deck[i] = i;
			var seeds = new int[count];
			for (int s = 0; s < count; s++)
			{
				int pick = s + rng.Next(cellCount - s);
				(deck[s], deck[pick]) = (deck[pick], deck[s]);
				seeds[s] = deck[s];
			}
			return seeds;
		}

	// ── 边界提取（01 §4，纯几何渲染派生口；两个出口共用同一异板共享边收集）──

	/// <summary>提取板块边界【格边】集（描边渲染用，2026-09-09 v2 拍板，替代旧角点集）：遍历异板
	/// 格邻居对 → 共享边两端顶点在边界两侧【各格】名下记账 (格 id, 顶点对)。View 按此把每格
	/// 【每条格边】的边界旗（UV2.y）置 1，片元只压暗旗标边成轮廓带——粒度 = 边而非角点：同板边
	/// 即使两端角点都贴邻边界边（如五边贴异板的格）也绝不被误描（角点粒度旧账法整条误描，
	/// 2026-09-09 用户报告）。顶点对规范化序（va&lt;vb）——查找端（BallMesh 逐格取角点对）顺序
	/// 不定，集合必须 canonical。与 ExtractBoundaryChains 同一边集，无遗漏无重复。</summary>
	public static HashSet<(ulong cell, ulong va, ulong vb)> ExtractBoundaryCellEdges(Ball ball, int[] plateId)
	{
		var edges = new HashSet<(ulong cell, ulong va, ulong vb)>();
		foreach (var e in CollectBoundaryEdges(ball, plateId))
		{
			ulong ci = ball.CellIds[e.ci], cj = ball.CellIds[e.cj];
			(ulong va, ulong vb) = e.a < e.b ? (e.a, e.b) : (e.b, e.a);
			edges.Add((ci, va, vb));
			edges.Add((cj, va, vb));
		}
		return edges;
	}

	/// <summary>提取板块边界链集合（01 §4 派生口，测试锚定）：异板共享边按端点连通成链；端点度 ≥3
	/// （三板块交汇）断链、各成独立链段；闭合环/开放链各成一条。每条链 = 一串球面点列
	/// （六边形胞边锯齿，不做样条平滑——用户拍板默认）。链段无重叠无遗漏（可单测对账）。
	/// ⚠️ 渲染已改描边方案（ExtractBoundaryVerts），本口保留为设计派生口 + 诊断/对账基准。</summary>
	public static List<Vector3[]> ExtractBoundaryChains(Ball ball, int[] plateId)
	{
		// 1) 异板格对 → 共享边（每条邻居对只处理一次：j > i 下标判重）
		var edges = CollectBoundaryEdges(ball, plateId)
			.Select(e => (v1: e.a, v2: e.b))
			.ToList();

		var chains = new List<Vector3[]>();
		if (edges.Count == 0) return chains;

			// 2) 顶点度（共享边数）+ 每顶点邻接边表（断链判定与链追踪用）
			var degree = new Dictionary<ulong, int>();
			var edgesAt = new Dictionary<ulong, List<int>>();
			for (int e = 0; e < edges.Count; e++)
			{
				ulong a = edges[e].v1, b = edges[e].v2;
				degree.TryGetValue(a, out int da);
				degree.TryGetValue(b, out int db);
				degree[a] = da + 1;
				degree[b] = db + 1;
				if (!edgesAt.TryGetValue(a, out var la)) edgesAt[a] = la = new List<int>();
				if (!edgesAt.TryGetValue(b, out var lb)) edgesAt[b] = lb = new List<int>();
				la.Add(e);
				lb.Add(e);
			}

			// 3) 连通成链：从每条未用边出发，度 2 顶点穿过、度 ≥3 断链（顶点为链端点入列）。
			//    链 = 反向延伸点列（反转）+ [端点 a, 端点 b] + 正向延伸点列；环闭合自然首尾相接。
			var used = new bool[edges.Count];
			for (int start = 0; start < edges.Count; start++)
			{
				if (used[start]) continue;
				used[start] = true;
				ulong a = edges[start].v1, b = edges[start].v2;

				var head = WalkChain(ball, edges, edgesAt, degree, used, a, start);
				var tail = WalkChain(ball, edges, edgesAt, degree, used, b, start);
				head.Reverse();   // head 自 a 反向收集 → 反序后接在 a 前

				var chain = new List<Vector3>(head.Count + tail.Count + 2);
				chain.AddRange(head);
				chain.Add(ball.VertexPositions[ball.VertexIndexOf(a)]);
				chain.Add(ball.VertexPositions[ball.VertexIndexOf(b)]);
				chain.AddRange(tail);
				chains.Add(chain.ToArray());
			}
			return chains;
		}

		// 异板格邻居对 → 共享边全集（每条邻居对只处理一次：j > i 下标判重）。输出格【下标】对 +
	// 共享边两端顶点 id——链提取与描边角点集两出口共用同一边集（单一事实源，防两路口径漂移）。
	static List<(int ci, int cj, ulong a, ulong b)> CollectBoundaryEdges(Ball ball, int[] plateId)
	{
		var edges = new List<(int ci, int cj, ulong a, ulong b)>();
		int n = plateId.Length;
		for (int i = 0; i < n; i++)
		{
			int pi = plateId[i];
			ulong cellI = ball.CellIds[i];
			foreach (int j in ball.CellNeighbors[i])
			{
				if (j <= i || plateId[j] == pi) continue;
				(var a, var b) = SharedEdgeEndpoints(ball, cellI, ball.CellIds[j]);
				edges.Add((i, j, a, b));
			}
		}
		return edges;
	}

	// 两格顶点集交集 = 共享边两端点（顶点数 ≤10，双重循环即可；H3 相邻格恰共享一条整边）。
		static (ulong, ulong) SharedEdgeEndpoints(Ball ball, ulong cellI, ulong cellJ)
		{
			ulong[] vi = H3.CellToVertexes(cellI);
			ulong[] vj = H3.CellToVertexes(cellJ);
			ulong first = 0, second = 0;
			int count = 0;
			foreach (ulong v in vj)
			{
				foreach (ulong u in vi)
				{
					if (v != u) continue;
					if (count == 0) first = v; else second = v;
					count++;
					break;
				}
			}
			if (count != 2)
				throw new InvalidOperationException($"格对共享边异常（{count} 个公共顶点，H3 相邻格应恰共享 2 个）：{H3.H3ToString(cellI)} / {H3.H3ToString(cellJ)}");
			return (first, second);
		}

		// 从边端点 v0 沿"非 prevEdge 的未用边"延伸链：仅度 2 顶点可穿过（度 ≥3 三板块交汇 → 断链，
		// 该顶点作为链端点已在穿过时入列）；无路（环闭合回到主链已用边）停止且不重复加末端点。
		// 返回延伸途中顶点位置（不含出发点 v0——主链 [a,b] 已含它）。
		static List<Vector3> WalkChain(Ball ball, List<(ulong v1, ulong v2)> edges, Dictionary<ulong, List<int>> edgesAt,
			Dictionary<ulong, int> degree, bool[] used, ulong v0, int prevEdge)
		{
			var pts = new List<Vector3>();
			ulong cur = v0;
			while (true)
			{
				if (degree[cur] != 2) break;                       // 端点（含 v0 自身是三叉）：主链起点已含
				int nextEdge = -1;
				foreach (int e in edgesAt[cur])
				{
					if (e != prevEdge && !used[e]) { nextEdge = e; break; }
				}
				if (nextEdge < 0) break;                           // 环闭合（回到主链已用边）
				used[nextEdge] = true;
				ulong next = edges[nextEdge].v1 == cur ? edges[nextEdge].v2 : edges[nextEdge].v1;
				cur = next;
				prevEdge = nextEdge;
				pts.Add(ball.VertexPositions[ball.VertexIndexOf(cur)]);   // 穿过的新顶点入列（含尾端三叉点）
			}
			return pts;
		}
	}
}
