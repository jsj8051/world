using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.Utils;
using World.Utils.H3;

namespace World.NewHexWorld.Plate
{
	// 板表行（设计入口 §3.2）：每板 = Id、种子格心方向（SeedDir）、陆性标记 IsLand、Ω 占位。
	// Ω/R 为将来运动学扩展预留（本阶段无运动：Omega 恒 Zero，R 旋转占位需要时按新设计补字段）。
	public sealed class PlateRecord
	{
		public int Id;            // 板块号（下标 = Plates 数组位）
		public Vector3 SeedDir;   // 种子格心方向（单位向量；诊断/将来扩展用）
		public bool IsLand;       // 陆性标记——海陆 = 板块属性（陆性板 → 大陆块，二元跳变）
		public Vector3 Omega;     // Ω 角速度占位（无运动不演化；设计 §3.2 预留字段）
	}

	// H3 球面静态地壳生成（设计-01 v1.2 实现；入口 v11）：一次生成、无演化步进、无 Step。
	// 算法（01 §2，2026-09-09 起弃 fibonacci 胞切分层）：DeterministicRandom(seed) 直接从 N 个
	// H3 格抽 P 个种子格 → 在格邻居图上按"板当前边界格数"加权随机归并铺满全球（加权语义 =
	// 用户拍板 09-07：大板长得快，板块大小自然有方差）→ 同一 rng 续流逐板掷 LandFrac 定陆/洋
	// → 两级料场 + Age + 海拔初值（成对写定，将来要素生成器再成对加起伏）。
	// 输出：Crust 六场（与 Ball.CellIds 对齐）+ Plates 板表；另附边界提取静态口（01 §4，渲染派生数据）：
	// 链（ExtractBoundaryChains，测试锚定的诊断/对账基准）与描边格边集（ExtractBoundaryCellEdges，渲染在用）。
	public class H3Plate
	{
		// ── 常量（设计入口 §3.3；判读后按需调）──
		public const float LandFelsicThicknessM = 35000f;   // 陆性板长英质厚（m）
		public const float OceanMaficThicknessM = 7000f;    // 洋性板镁铁质厚（m）
		public const float LandElevationM = 800f;           // 陆性板海拔初值（m，≈地球大陆平均 ~840 取整）
		public const float OceanDepthM = 3700f;             // 洋性板水深（海拔存负值；≈地球海洋平均 ~3688）
		public const float ContinentalAgeMy = 1000f;        // 陆壳年龄（My 占位标记；无演化）
		public const float OceanicAgeMy = 0f;               // 洋壳年龄（My）
		const int CellDivisor = 100;                        // 胞比例：K = max(10, round(N/100))（01 §3 参数表）

		public Crust Crust { get; private set; }        // 六场（生成后只读；UI 经 MapMode 派生，逻辑层唯一权威）
		public PlateRecord[] Plates { get; private set; }   // 板表（下标 = 板 Id）
		public int NumPlates { get; private set; }
		public float LandFrac { get; private set; }     // 板为陆性的概率（实际陆占比随板大小涨落）

		readonly Ball _ball;

		public H3Plate(Ball ball)
		{
			_ball = ball;
		}

		/// <summary>一次静态生成全球海陆初始地壳场（设计-01 v1.2 §2 伪码的落地；同 seed 同参数逐位一致）。</summary>
		public void CreatePlates(int numPlates, int seed, float landFrac = 1f / 3f)
		{
			// 参数校验前置：任何实例状态被写之前定局（抛异常时对象保持干净，可安全换参重试）
			if (numPlates < 2) throw new ArgumentOutOfRangeException(nameof(numPlates), "板块数至少 2");
			int cellCount = _ball.CellIds.Length;
			if (numPlates > cellCount)
				throw new ArgumentOutOfRangeException(nameof(numPlates), $"板块数 {numPlates} 不能超过格数 {cellCount}");

			NumPlates = numPlates;
			LandFrac = landFrac;

			var rng = new DeterministicRandom(seed);

			// 1. 板块种子格：DeterministicRandom 从 N 格无重复抽 P 个（partial Fisher-Yates，rng 消耗固定）
			int[] seeds = PickSeedCells(rng, cellCount);                    // seeds[p] = 板 p 的种子格下标

			// 2. 种子归并生长（加权）：每步按"板边界格数"随机抽板、板内随机弹 1 个边界格吞下。
			//    大板边界大 → 抽中概率高 → 自然大小方差（富者愈富；用户拍板 09-07）
			int[] plateOfCell = WeightedGrow(rng, seeds, cellCount);

			// 3. 海陆赋性：同一 rng 续流逐板掷 LandFrac 概率（seed 定局：同 seed 全球同局）
			var isLand = new bool[numPlates];
			for (int p = 0; p < numPlates; p++) isLand[p] = rng.NextDouble() < landFrac;

			// 4. 两级料场 + 年龄 + 海拔初值（成对写定；Sediment 全 0 暂不使用）
			Crust = BuildCrustFields(plateOfCell, isLand);

			// 板表：种子格心方向 + 陆性标记 + 占位字段
			Plates = new PlateRecord[numPlates];
			for (int p = 0; p < numPlates; p++)
			{
				Plates[p] = new PlateRecord
				{
					Id = p,
					SeedDir = _ball.CellCenters[seeds[p]],
					IsLand = isLand[p],
					Omega = Vector3.Zero,
				};
			}
		}

		// ── 生成内部步骤 ──────────────────────────────────────────────

		// 无重复抽 P 个种子格：partial Fisher-Yates 洗前 P 位（rng 消耗固定 P 次 → 确定性）。
		int[] PickSeedCells(DeterministicRandom rng, int cellCount)
		{
			var deck = new int[cellCount];
			for (int i = 0; i < cellCount; i++) deck[i] = i;
			var seeds = new int[NumPlates];
			for (int s = 0; s < NumPlates; s++)
			{
				int pick = s + rng.Next(cellCount - s);
				(deck[s], deck[pick]) = (deck[pick], deck[s]);
				seeds[s] = deck[s];
			}
			return seeds;
		}

		// 加权归并生长核心（直接长在 H3 格邻居图上，2026-09-09 起无胞层）。不变量：
		// plateFrontier[p] = 与板 p 邻接的【未归属】格（无脏条目——格被吞时已从其余板 frontier
		// 同步移除，故 activeCount 精确）；cellPlates[c] = 未归属格 c 当前邻接的板集合。
		// 格图连通 → 必然铺满；若中途无边界格可吞 = 实现缺陷，当场暴露。
		int[] WeightedGrow(DeterministicRandom rng, int[] seeds, int cellCount)
		{
			int n = cellCount, p = NumPlates;
			var claimed = new bool[n];
			var plateOfCell = new int[n];
			var plateFrontier = new List<int>[p];
			var cellPlates = new List<int>[n];          // 只对未归属格有效（被吞后弃用）
			for (int q = 0; q < p; q++) plateFrontier[q] = new List<int>();
			for (int c = 0; c < n; c++) cellPlates[c] = new List<int>();

			// 种子格入板；其未归属邻居格进本板 frontier（含邻接板记录）
			for (int s = 0; s < p; s++)
			{
				int seed = seeds[s];
				claimed[seed] = true;
				plateOfCell[seed] = s;
				// ⚠️ 本种子在更早初始化板的 frontier 里躺过（当时它未 claimed、是合法边界格）——
				// 现在已成板格，须从那些板的 frontier 移除，否则归并时会把它误吞（空板/错板 bug，2026-09-07 修）
				foreach (int q in cellPlates[seed])
					if (q != s) plateFrontier[q].Remove(seed);
				cellPlates[seed].Clear();   // 已 claimed，弃用邻接板记录
				foreach (int nb in _ball.CellNeighbors[seed])
				{
					if (claimed[nb]) continue;
					if (!cellPlates[nb].Contains(s))
					{
						cellPlates[nb].Add(s);
						plateFrontier[s].Add(nb);
					}
				}
			}

			int unclaimed = n - p;
			while (unclaimed > 0)
			{
				// 总权重 = 各板边界格数之和；随机落点选板（富者愈富的"富"= 边界大）
				int total = 0;
				for (int q = 0; q < p; q++) total += plateFrontier[q].Count;
				if (total <= 0)
					throw new InvalidOperationException($"归并未铺满（剩 {unclaimed} 格无边界可吞）：格图不连通或实现缺陷");

				// 前缀和落点选板：零边界板直接越过（count=0 时不减但前进），必落在某非零板内
				int plate = 0;
				int roll = rng.Next(total);
				while (plate < p - 1 && roll >= plateFrontier[plate].Count)
				{
					roll -= plateFrontier[plate].Count;
					plate++;
				}

				// 板内随机弹 1 个边界格（swap-remove，O(1)）
				var list = plateFrontier[plate];
				int idx = rng.Next(list.Count);
				int cell = list[idx];
				list[idx] = list[^1];
				list.RemoveAt(list.Count - 1);
				claimed[cell] = true;
				plateOfCell[cell] = plate;
				unclaimed--;

				// 该格同时是其它板的边界格 → 从它们的 frontier 移除（维护无脏不变量）
				foreach (int q in cellPlates[cell])
					if (q != plate) plateFrontier[q].Remove(cell);

				// 该格的未归属邻居纳入本板 frontier（记录邻接板；去重防同板重复入列）
				foreach (int nb in _ball.CellNeighbors[cell])
				{
					if (claimed[nb]) continue;
					if (!cellPlates[nb].Contains(plate))
					{
						cellPlates[nb].Add(plate);
						plateFrontier[plate].Add(nb);
					}
				}
			}
			return plateOfCell;
		}

		// 场填充：格归属板 → 按板陆性写两级料场/年龄/海拔（Sediment 留 0）。
		Crust BuildCrustFields(int[] plateOfCell, bool[] isLand)
		{
			int n = _ball.CellIds.Length;
			var crust = new Crust
			{
				PlateId = new int[n],
				FelsicThick = new float[n],
				MaficThick = new float[n],
				SedimentThick = new float[n],
				Age = new float[n],
				Elevation = new float[n],
			};
			for (int i = 0; i < n; i++)
			{
				int plate = plateOfCell[i];
				crust.PlateId[i] = plate;
				if (isLand[plate])
				{
					crust.FelsicThick[i] = LandFelsicThicknessM;
					crust.Age[i] = ContinentalAgeMy;
					crust.Elevation[i] = LandElevationM;
				}
				else
				{
					crust.MaficThick[i] = OceanMaficThicknessM;
					crust.Age[i] = OceanicAgeMy;
					crust.Elevation[i] = -OceanDepthM;
				}
			}
			return crust;
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
