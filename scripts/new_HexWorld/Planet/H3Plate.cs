using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.Utils;
using World.Utils.H3;

namespace World.NewHexWorld.Plate
{
	// 板表行（设计入口 §3.2）：每板 = Id、种子胞心方向（SeedDir）、陆性标记 IsLand、Ω 占位。
	// Ω/R 为将来运动学扩展预留（本阶段无运动：Omega 恒 Zero，R 旋转占位需要时按新设计补字段）。
	public sealed class PlateRecord
	{
		public int Id;            // 板块号（下标 = Plates 数组位）
		public Vector3 SeedDir;   // 种子胞心方向（单位向量；诊断/将来扩展用）
		public bool IsLand;       // 陆性标记——海陆 = 板块属性（陆性板 → 大陆块，二元跳变）
		public Vector3 Omega;     // Ω 角速度占位（无运动不演化；设计 §3.2 预留字段）
	}

	// H3 球面静态地壳生成（设计-01 v1.1 实现；入口 v10）：一次生成、无演化步进、无 Step。
	// 算法（01 §2）：fibonacci/黄金角撒 K≈N/100 个细多边形胞心 → 每格归最近胞心成胞 →
	// DeterministicRandom(seed) 抽 P 个种子胞 → 按"板当前边界胞数"加权随机归并铺满全球
	// （加权语义 = 用户拍板 09-07：大板长得快，板块大小自然有方差）→ 同一 rng 续流逐板掷
	// LandFrac 定陆/洋 → 两级料场 + Age + 海拔初值（成对写定，将来要素生成器再成对加起伏）。
	// 输出：Crust 六场（与 Ball.CellIds 对齐）+ Plates 板表；另附边界提取静态口（01 §4，渲染派生数据）：
// 链（ExtractBoundaryChains，测试锚定的诊断/对账基准）与描边角点集（ExtractBoundaryVerts，渲染在用）。
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
		int _crustCount;        // 胞数 K

		public H3Plate(Ball ball)
		{
			_ball = ball;
		}

		/// <summary>一次静态生成全球海陆初始地壳场（设计-01 v1.1 §2 伪码的落地；同 seed 同参数逐位一致）。</summary>
		public void CreatePlates(int numPlates, int seed, float landFrac = 1f / 3f)
		{
			// 参数校验前置：任何实例状态被写之前定局（抛异常时对象保持干净，可安全换参重试）
			if (numPlates < 2) throw new ArgumentOutOfRangeException(nameof(numPlates), "板块数至少 2");
			int crustCount = Math.Max(10, (int)Math.Round(_ball.CellIds.Length / 100.0));
			if (numPlates > crustCount)
				throw new ArgumentOutOfRangeException(nameof(numPlates), $"板块数 {numPlates} 不能超过胞数 {crustCount}");

			NumPlates = numPlates;
			LandFrac = landFrac;
			int cellCount = _ball.CellIds.Length;
			_crustCount = crustCount;

			var rng = new DeterministicRandom(seed);

			// 1. 胞切分（唯一一次全局划分，之后永不重划）：fibonacci 撒胞心 → 每格归最近胞心
			Vector3[] crustCenters = FibonacciSphereCenters(_crustCount);
			int[] cellCrust = AssignCellsToCrusts(crustCenters);            // 每格所属胞号
			var crustNeighbors = BuildCrustNeighbors(cellCrust);            // 每胞邻居胞表（格邻居诱导）

			// 2. 板块种子胞：DeterministicRandom 从 K 胞无重复抽 P 个（partial Fisher-Yates，rng 消耗固定）
			int[] seeds = PickSeedCrusts(rng);                              // seeds[p] = 板 p 的种子胞号

			// 3. 种子归并生长（加权）：每步按"板边界胞数"随机抽板、板内随机弹 1 个边界胞吞下。
			//    大板边界大 → 抽中概率高 → 自然大小方差（富者愈富；用户拍板 09-07）
			int[] plateOfCrust = WeightedGrow(rng, seeds, crustNeighbors, cellCrust, cellCount);

			// 4. 海陆赋性：同一 rng 续流逐板掷 LandFrac 概率（seed 定局：同 seed 全球同局）
			var isLand = new bool[numPlates];
			for (int p = 0; p < numPlates; p++) isLand[p] = rng.NextDouble() < landFrac;

			// 5-6. 两级料场 + 年龄 + 海拔初值（成对写定；Sediment 全 0 暂不使用）
			Crust = BuildCrustFields(cellCrust, plateOfCrust, isLand);

			// 板表：种子胞心方向 + 陆性标记 + 占位字段
			Plates = new PlateRecord[numPlates];
			for (int p = 0; p < numPlates; p++)
			{
				Plates[p] = new PlateRecord
				{
					Id = p,
					SeedDir = crustCenters[seeds[p]],
					IsLand = isLand[p],
					Omega = Vector3.Zero,
				};
			}
		}

		// ── 生成内部步骤 ──────────────────────────────────────────────

		// 球面均匀撒 K 个胞心（fibonacci 球面近似，2026-09-03 同款公式）：y 均匀（=sinφ → 环带面积均匀），
		// 经度按黄金角步进；输出单位方向向量。确定性（无随机）。
		static Vector3[] FibonacciSphereCenters(int count)
		{
			var dirs = new Vector3[count];
			for (int i = 0; i < count; i++)
			{
				float y = 1f - 2f * (i + 0.5f) / count;                          // 纬度分量均匀分布（±1）
				float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));            // 该纬度的圆半径
				float theta = (float)(Math.PI * (1 + Math.Sqrt(5)) * i);         // 黄金角经度步进
				dirs[i] = new Vector3(Mathf.Cos(theta) * radius, y, Mathf.Sin(theta) * radius);
			}
			return dirs;
		}

		// 每格归最近胞心（max dot，格心与胞心同单位球）→ 格 → 胞归属表。
		// ⚠️ O(N·K) 朴素实现：res3 ≈17M 点积毫秒级、res4 ≈0.8G 秒级——单次生成可接受；
		//    将来 res5（N≈2M、K≈2 万）需换桶索引，勿直接上（性能红线，见开发规范 §5）。
		int[] AssignCellsToCrusts(Vector3[] crustCenters)
		{
			var cells = _ball.CellCenters;
			var cellCrust = new int[cells.Length];
			for (int i = 0; i < cells.Length; i++)
			{
				Vector3 c = cells[i];
				int best = 0;
				float bestDot = float.MinValue;
				for (int k = 0; k < crustCenters.Length; k++)
				{
					float d = c.Dot(crustCenters[k]);
					if (d > bestDot) { bestDot = d; best = k; }   // 严格大于：平局取先者（遍历序固定 → 确定性）
				}
				cellCrust[i] = best;
			}
			return cellCrust;
		}

		// 胞相邻表：两胞相邻 ⇔ 存在格对 (a∈A, b∈B) 且 a、b 是格邻居（Ball.CellNeighbors，已按 id 排序）。
		// 遍历全部格邻居对收集去重（胞度小，Contains 线性查重即可）；List 顺序 = 遍历顺序（确定性）。
		List<int>[] BuildCrustNeighbors(int[] cellCrust)
		{
			var neighbors = new List<int>[_crustCount];
			for (int k = 0; k < _crustCount; k++) neighbors[k] = new List<int>();
			for (int i = 0; i < cellCrust.Length; i++)
			{
				int a = cellCrust[i];
				foreach (int j in _ball.CellNeighbors[i])
				{
					int b = cellCrust[j];
					if (a != b && !neighbors[a].Contains(b)) neighbors[a].Add(b);
				}
			}
			return neighbors;
		}

		// 无重复抽 P 个种子胞：partial Fisher-Yates 洗前 P 位（rng 消耗固定 P 次 → 确定性）。
		int[] PickSeedCrusts(DeterministicRandom rng)
		{
			var deck = new int[_crustCount];
			for (int k = 0; k < _crustCount; k++) deck[k] = k;
			var seeds = new int[NumPlates];
			for (int s = 0; s < NumPlates; s++)
			{
				int pick = s + rng.Next(_crustCount - s);
				(deck[s], deck[pick]) = (deck[pick], deck[s]);
				seeds[s] = deck[s];
			}
			return seeds;
		}

		// 加权归并生长核心。不变量：plateFrontier[p] = 与板 p 邻接的【未归属】胞（无脏条目——
		// 胞被吞时已从其余板 frontier 同步移除，故 activeCount 精确）；cellPlates[c] = 未归属胞 c
		// 当前邻接的板集合。胞图连通 → 必然铺满；若中途无边界胞可吞 = 实现缺陷，当场暴露。
		int[] WeightedGrow(DeterministicRandom rng, int[] seeds, List<int>[] crustNeighbors,
			int[] cellCrust, int cellCount)
		{
			int k = _crustCount, p = NumPlates;
			var claimed = new bool[k];
			var plateOfCrust = new int[k];
			var plateFrontier = new List<int>[p];
			var cellPlates = new List<int>[k];          // 只对未归属胞有效（被吞后弃用）
			for (int q = 0; q < p; q++) plateFrontier[q] = new List<int>();
			for (int c = 0; c < k; c++) cellPlates[c] = new List<int>();

			// 种子胞入板；其未归属邻居胞进本板 frontier（含邻接板记录）
			for (int s = 0; s < p; s++)
			{
				int seed = seeds[s];
				claimed[seed] = true;
				plateOfCrust[seed] = s;
				// ⚠️ 本种子在更早初始化板的 frontier 里躺过（当时它未 claimed、是合法边界胞）——
				// 现在已成板胞，须从那些板的 frontier 移除，否则归并时会把它误吞（空板/错板 bug，2026-09-07 修）
				foreach (int q in cellPlates[seed])
					if (q != s) plateFrontier[q].Remove(seed);
				cellPlates[seed].Clear();   // 已 claimed，弃用邻接板记录
				foreach (int nb in crustNeighbors[seed])
				{
					if (claimed[nb]) continue;
					if (!cellPlates[nb].Contains(s))
					{
						cellPlates[nb].Add(s);
						plateFrontier[s].Add(nb);
					}
				}
			}

			int unclaimed = k - p;
			while (unclaimed > 0)
			{
				// 总权重 = 各板边界胞数之和；随机落点选板（富者愈富的"富"= 边界大）
				int total = 0;
				for (int q = 0; q < p; q++) total += plateFrontier[q].Count;
				if (total <= 0)
					throw new InvalidOperationException($"归并未铺满（剩 {unclaimed} 胞无边界可吞）：胞图不连通或实现缺陷");

				// 前缀和落点选板：零边界板直接越过（count=0 时不减但前进），必落在某非零板内
				int plate = 0;
				int roll = rng.Next(total);
				while (plate < p - 1 && roll >= plateFrontier[plate].Count)
				{
					roll -= plateFrontier[plate].Count;
					plate++;
				}

				// 板内随机弹 1 个边界胞（swap-remove，O(1)）
				var list = plateFrontier[plate];
				int idx = rng.Next(list.Count);
				int crust = list[idx];
				list[idx] = list[^1];
				list.RemoveAt(list.Count - 1);
				claimed[crust] = true;
				plateOfCrust[crust] = plate;
				unclaimed--;

				// 该胞同时是其它板的边界胞 → 从它们的 frontier 移除（维护无脏不变量）
				foreach (int q in cellPlates[crust])
					if (q != plate) plateFrontier[q].Remove(crust);

				// 胞的未归属邻居纳入本板 frontier（记录邻接板；去重防同板重复入列）
				foreach (int nb in crustNeighbors[crust])
				{
					if (claimed[nb]) continue;
					if (!cellPlates[nb].Contains(plate))
					{
						cellPlates[nb].Add(plate);
						plateFrontier[plate].Add(nb);
					}
				}
			}
			return plateOfCrust;
		}

		// 场填充：胞归属 → 板 → 按板陆性写两级料场/年龄/海拔（Sediment 留 0）。
		Crust BuildCrustFields(int[] cellCrust, int[] plateOfCrust, bool[] isLand)
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
				int plate = plateOfCrust[cellCrust[i]];
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

	/// <summary>提取板块边界【角点】集（描边渲染用，2026-09-08 拍板）：遍历异板格邻居对 → 两格顶点集
	/// 交集得共享边（H3 顶点 id 精确匹配，无坐标容差）→ 边两端顶点对边界两侧【各格】记一笔
	/// (格 id, 顶点 id)。View 按此把每格角点副本的边界权重置 0，片元压暗成轮廓带——同一边界两侧
	/// 格各暗半带，描边骑缝对称。与 ExtractBoundaryChains 同一边集，无遗漏无重复。</summary>
	public static HashSet<(ulong cell, ulong vid)> ExtractBoundaryVerts(Ball ball, int[] plateId)
	{
		var verts = new HashSet<(ulong, ulong)>();
		foreach (var e in CollectBoundaryEdges(ball, plateId))
		{
			ulong ci = ball.CellIds[e.ci], cj = ball.CellIds[e.cj];
			verts.Add((ci, e.a));
			verts.Add((ci, e.b));
			verts.Add((cj, e.a));
			verts.Add((cj, e.b));
		}
		return verts;
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
