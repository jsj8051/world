using System;
using System.Collections.Generic;
using Godot;                // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;          // DeterministicRandom（确定性红线）
using World.Utils.H3;

namespace World.NoiseWorld;

// 板块划分（纯分割，无构造模拟）· 分配核七修史：
//   一修 前沿随机填充（细格，原 H3Plate.GrowPlates）：均匀随机 ⇒ 各向同性，板界太圆；
//   二修 噪声偏置生长：顺全局噪声脊蛇形 ⇒ 超级板包圈；
//   三修 加权 Dijkstra：不包圈但全场单噪声 ⇒ 大圆弧；
//   四修 每板噪声调价：最优路径把折扣"挑着走"平均掉 ⇒ 还是圆弧；
//   五修 直接距离+每板加性噪声（扭曲 Voronoi）：弧没了但基波长 > 板块周长 ⇒ 圆斑 + 两巨板；
//   六修 波长 2000×4 倍频 + 尺寸压缩：碎弯出来了，仍是解析斑块气质；
//   七修（用户拍板算法 2026-09-30）：撒 1000 点 → 粗 Voronoi 层 → 选 n 起源 → 粗图随机吞并到剩 n 板；
//   八修（本版，**地壳分布合理性**，方案源：platec/WorldEngine 的权重校准 + 大陆聚簇）：
//     ① **面积拟合**——目标份额取自层级分布（0.2+u 归一 ⇒ 巨板/中板/小板的地球式层级），速率按
//        (目标/实际)^0.5 迭代校准 5 次 ⇒ 份额贴合目标层级，不再"速率随机 ⇒ 份额随机"；
//     ② **大陆聚簇**——陆/洋身份不再均匀洗牌：1–2 个陆簇焦点（双簇强制角距 >60°），离焦点最近的
//        k 块板为陆板 ⇒ 大陆聚成地球式的一两个超级陆块 + 广阔大洋，而非均匀散布。
// 护栏：细格份额 < 公平×25%（碎片）或 > 35%（巨板）换种子重长（最多 8 次、取最小份额最大者）。
// **板块身份（"是什么就是什么"第一步，2026-09-30）**：每板一枚陆/洋身份（见②聚簇规则）；
//   海陆由身份定死，地形只做域内起伏。
// 确定性：全部随机走 DeterministicRandom 固定偏移（撒点 +5333 / 重采样 +31·attempt / 身份 +999 /
//   运动向 +7919）⇒ 同 seed 同网格逐位同。
public sealed class NoisePlates
{
	public const int ScatterCount = 1000;           // 粗 Voronoi 层撒点数（用户拍板）
	public const int CoarseRes = 2;                 // 粗格层分辨率（5882 格；细格 ≥ res2 时生效）
	public const float GrowthRateExponent = 0.8f;   // 初始速率悬殊度（面积拟合会把份额校到目标，此项只管起点）
	public const int GrowthMinShareAttempts = 8;    // 退化重采样次数上限
	public const int AreaFitIterations = 5;         // 面积拟合迭代次数（platec 式权重校准）
	public const float MaxShareCap = 0.35f;         // 单板份额上限（地球太平洋 ≈20%；35% = 容忍上限）
	public const float MinShareFairFraction = 0.25f; // 碎板下限 = 公平份额 × 此值（P=12 ⇒ ~2%，地球小板量级）
	public const float GrowthStarveFloorFraction = 0.3f; // 吞并防饿死地板（0.1 时 40% 巨板 + 碎片判读后收紧）
	public const float SmoothProbability = 0.3f;    // 边界平滑强度（用户拍板 30%）：异板粗格翻向邻域多数板的概率
	public const int SmoothPasses = 2;              // 平滑遍数（用户"再磨圆一点"：2 遍 ≈ 51% 翻格率 + 级联圆滑）
	public const float BottleneckMinLobeFraction = 0.15f; // 颈宽护栏：割点两侧部件下限 = 板格数 × 此值（且 ≥2 格）

	int[] _plateOfCell = Array.Empty<int>();
	int _numPlates;
	int _seed = int.MinValue;
	int _cellCount = -1;
	float _landTarget = -1f;
	bool[] _landPlate = Array.Empty<bool>();
	Vector3[] _omega = Array.Empty<Vector3>();

	/// <summary>每格归属板号（与 Ball.CellIds 对齐；Generate 后有效）。</summary>
	public int[] PlateOfCell => _plateOfCell;

	/// <summary>板数（= 起源数；板号 0..N-1，吞并认领制天然无空板）。</summary>
	public int NumPlates => _numPlates;

	/// <summary>每板陆/洋身份（true = 陆板；"是什么就是什么"第一步的身份源，长度 = NumPlates）。</summary>
	public bool[] LandPlate => _landPlate;

	/// <summary>每板角速度轴 × 速率 ∈[0.5,1.5]（时间单位任意——只有**相对**值参与碰撞判据）。
	/// 固定次序派生（p 升序，每板 x/y/z/speed 四掷）⇒ 同 seed 逐位同。</summary>
	public Vector3[] Omega => _omega;

	/// <summary>本次划分用的种子（幂等跳过时 = 上次种子；地形侧快照判据用）。</summary>
	public int SeedUsed => _seed;

	/// <summary>全量重算；同（格数, 板数, seed, 陆板占比）幂等跳过——参数面板每次微调都路过这里，段缓存同思路。</summary>
	public void Generate(Ball ball, int numPlates, int seed, float landFractionTarget)
	{
		int n = ball.CellIds.Length;
		if (numPlates == _numPlates && seed == _seed && landFractionTarget == _landTarget
			&& n == _cellCount && _plateOfCell.Length == n) return;
		if (numPlates < 2) throw new ArgumentOutOfRangeException(nameof(numPlates), "板块数至少 2");
		if (numPlates > n) throw new ArgumentOutOfRangeException(nameof(numPlates), $"板块数 {numPlates} 不能超过格数 {n}");

		int coarseRes = Math.Min(CoarseRes, ball.Res);
		int[] best = null;
		int[] bestShapeOk = null;
		double bestMinShare = -1.0;
		double bestShapeOkMinShare = -1.0;
		double fairShare = 1.0 / Math.Max(numPlates, 1);
		var originDirs = new Vector3[numPlates];               // 起源方向（最后一次 attempt 的留存，聚簇身份用）
		for (int attempt = 0; attempt < GrowthMinShareAttempts; attempt++)
		{
			var rng = new DeterministicRandom(seed + 31 * attempt);

			// ① 粗格层：撒点 → coarseRes 格去重（保持抽取序；均匀球面方向 z/θ 两掷）
			var srng = new DeterministicRandom(seed + 5333);
			var coarseSeen = new HashSet<ulong>();
			var coarseCells = new List<ulong>();
			for (int k = 0; k < ScatterCount; k++)
			{
				double z = srng.NextDouble() * 2.0 - 1.0;
				double theta = srng.NextDouble() * 2.0 * Math.PI;
				double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
				var dir = new Vector3((float)(r * Math.Cos(theta)), (float)z, (float)(r * Math.Sin(theta)));
				var cc = H3.LatLngToCell(new LatLng(Math.Asin(Math.Clamp(dir.Y, -1.0, 1.0)),
					Math.Atan2(dir.Z, dir.X)), coarseRes);
				if (coarseSeen.Add(cc)) coarseCells.Add(cc);
			}
			int C = coarseCells.Count;

			// ② 细格 → 粗格（多源 BFS：粗格在细 res 的代表子格为源 ⇒ 粗区连通、全球无遗漏）
			var idx = new Dictionary<ulong, int>(n);
			for (int i = 0; i < n; i++) idx[ball.CellIds[i]] = i;
			var coarseOfFine = new int[n];
			Array.Fill(coarseOfFine, -1);
			var coarseSrcFine = new int[C];                        // 粗格代表细格下标（起源方向用）
			var queue = new Queue<int>();
			for (int c = 0; c < C; c++)
			{
				ulong child = H3.CellToChildren(coarseCells[c], ball.Res)[0];
				int src = idx[child];
				coarseSrcFine[c] = src;
				coarseOfFine[src] = c;
				queue.Enqueue(src);
			}
			var neighbors = ball.CellNeighbors;
			while (queue.Count > 0)
			{
				int i = queue.Dequeue();
				int ci = coarseOfFine[i];
				foreach (int j in neighbors[i])
					if (coarseOfFine[j] < 0) { coarseOfFine[j] = ci; queue.Enqueue(j); }
			}

			// ③ 粗邻接 = 细邻接提升去重（无向，有序对键防重复）
			var coarseNeighbors = new List<int>[C];
			for (int c = 0; c < C; c++) coarseNeighbors[c] = new List<int>();
			var seenEdges = new HashSet<long>();
			for (int i = 0; i < n; i++)
			{
				int a = coarseOfFine[i];
				foreach (int j in neighbors[i])
				{
					int b = coarseOfFine[j];
					if (a == b) continue;
					long key = a < b ? a * 100000L + b : b * 100000L + a;
					if (seenEdges.Add(key)) { coarseNeighbors[a].Add(b); coarseNeighbors[b].Add(a); }
				}
			}

			// ④ 选 n 个起源 + 速率 + **面积拟合**（八修①）：目标份额取层级分布（0.2+u 归一），
			//    速率按 (目标/实际)^0.5 迭代校准 ⇒ 份额贴合地球式层级，不再随速率随机。
			int[] origins = PickSeedCells(rng, C, numPlates);
			for (int p = 0; p < numPlates; p++)
				originDirs[p] = ball.CellDirs[coarseSrcFine[origins[p]]];

			var rates = new float[numPlates];
			var targetShare = new float[numPlates];
			float sumRaw = 0f;
			for (int p = 0; p < numPlates; p++)
			{
				float u = MathF.Max((float)rng.NextDouble(), 1e-4f);   // 防下溢出 0（0 的幂 = 恒零权重）
				rates[p] = 0.05f + 0.95f * MathF.Pow(u, GrowthRateExponent);
				targetShare[p] = 0.2f + (float)rng.NextDouble();       // 层级分布（巨板 ~6× 小板）
				sumRaw += targetShare[p];
			}
			for (int p = 0; p < numPlates; p++) targetShare[p] /= sumRaw;

			int[] plateOfCoarse = null;
			var coarseCounts = new int[numPlates];
			for (int iter = 0; iter < AreaFitIterations; iter++)
			{
				plateOfCoarse = GrowCoarse(C, coarseNeighbors, origins, rates, rng);
				Array.Clear(coarseCounts, 0, numPlates);
				foreach (int pc in plateOfCoarse) coarseCounts[pc]++;
				for (int p = 0; p < numPlates; p++)
				{
					double actual = coarseCounts[p] / (double)C;
					if (actual < 1e-6) { rates[p] = Math.Min(rates[p] * 1.5f, 1f); continue; }   // 饿死板提速
					rates[p] = Math.Clamp(rates[p] * MathF.Sqrt((float)(targetShare[p] / actual)), 0.05f, 1f);
				}
			}
			for (int pass = 0; pass < SmoothPasses; pass++)
				SmoothCoarse(C, coarseNeighbors, plateOfCoarse, rng);

			// ⑤ 细格随粗格落地 + 细格份额
			var plateOfCell = new int[n];
			var counts = new int[numPlates];
			for (int i = 0; i < n; i++)
			{
				int p = plateOfCoarse[coarseOfFine[i]];
				plateOfCell[i] = p;
				counts[p]++;
			}
			double minShare = double.MaxValue, maxShare = 0.0;
			foreach (int c in counts)
			{
				double sh = c / (double)n;
				if (sh < minShare) minShare = sh;
				if (sh > maxShare) maxShare = sh;
			}

			// ⑤b 形状护栏（**细格分水岭检测**，2026-10-02 二版）：板内到板边界深度 ≥2 格的
			// "深核"若 ≥2 个、且各自流域（全板格归入最近深核）都 ≥ 公平×25% ⇒ 沙漏/窄颈病态。
			// 对任意 ≤4 格宽的走廊都成立（旧"拔一格"检测对 2-4 格宽走廊有盲区——用户判读）。
			bool hasBottleneck = HasFineBottleneck(plateOfCell, counts, neighbors);

			bool sharesOk = minShare >= fairShare * MinShareFairFraction && maxShare <= MaxShareCap;
			if (sharesOk && !hasBottleneck)
			{
				best = plateOfCell;                                // 全达标 → 即收
				break;
			}
			// 兜底追踪：优先形状合格者（份额可稍越界），其次份额最优者
			if (!hasBottleneck && (bestShapeOk == null || minShare > bestShapeOkMinShare))
			{
				bestShapeOk = plateOfCell;
				bestShapeOkMinShare = minShare;
			}
			if (best == null || minShare > bestMinShare)
			{
				best = plateOfCell;
				bestMinShare = minShare;
			}
		}
		_plateOfCell = bestShapeOk ?? best;                        // 形状合格优先于份额最优
		_numPlates = numPlates;
		_seed = seed;
		_cellCount = n;
		_landTarget = landFractionTarget;

		// 板块身份（"是什么就是什么"第一步 + 八修②**大陆聚簇**）：1–2 个陆簇焦点（双簇强制角距
		// >60°），离最近焦点最近的 k 块板为陆板（k = round(板数 × 目标陆地占比)，clamp [1, P−1]）
		// ⇒ 大陆聚成地球式的一两个超级陆块 + 广阔大洋，而非均匀散布。
		// 注：陆地面积 = 陆板面积之和（跟随吞并结果），目标占比只控制**陆板数量**——这是
		//   身份定海陆语义的固有特性，不是偏差。DeterministicRandom(seed+999) 固定消耗次序。
		if (_landPlate.Length != numPlates) _landPlate = new bool[numPlates];
		Array.Clear(_landPlate, 0, numPlates);
		var irng = new DeterministicRandom(seed + 999);
		int clusterCount = 1 + irng.Next(2);
		var foci = new Vector3[clusterCount];
		for (int c = 0; c < clusterCount; c++)
		{
			Vector3 f;
			do
			{
				double z = irng.NextDouble() * 2.0 - 1.0;
				double theta = irng.NextDouble() * 2.0 * Math.PI;
				double r = Math.Sqrt(Math.Max(0.0, 1.0 - z * z));
				f = new Vector3((float)(r * Math.Cos(theta)), (float)z, (float)(r * Math.Sin(theta)));
			} while (c > 0 && f.Dot(foci[c - 1]) > MathF.Cos(MathF.PI / 3f));   // 双簇角距 >60°
			foci[c] = f;
		}
		int landCount = Math.Clamp((int)MathF.Round(numPlates * landFractionTarget), 1, numPlates - 1);
		var byDist = new List<(float dist, int p)>(numPlates);
		for (int p = 0; p < numPlates; p++)
		{
			float bestDist = float.MaxValue;
			foreach (var f in foci)
				bestDist = MathF.Min(bestDist, MathF.Acos(Math.Clamp(originDirs[p].Dot(f), -1f, 1f)));
			byDist.Add((bestDist, p));
		}
		byDist.Sort((a, b) => a.dist != b.dist
			? a.dist.CompareTo(b.dist)
			: a.p.CompareTo(b.p));                                 // (距离, 板号) 全序 ⇒ 确定性
		for (int i = 0; i < landCount; i++) _landPlate[byDist[i].p] = true;

		// 板块运动向量（C 方案）：每板一条随机角速度轴 × 速率。与撒点/重采样 rng 偏移互不串扰；
		// 固定消耗次序 ⇒ 同 seed 逐位同。
		if (_omega.Length != numPlates) _omega = new Vector3[numPlates];
		var mrng = new DeterministicRandom(seed + 7919);
		for (int p = 0; p < numPlates; p++)
		{
			var axis = new Vector3(
				(float)mrng.NextDouble() * 2f - 1f,
				(float)mrng.NextDouble() * 2f - 1f,
				(float)mrng.NextDouble() * 2f - 1f);
			if (axis.LengthSquared() < 1e-6f) axis = Vector3.Up;   // 防零轴（概率 ~0）
			float speed = 0.5f + (float)mrng.NextDouble();
			_omega[p] = axis.Normalized() * speed;
		}
	}

	// 粗图吞并本体（原 H3Plate.GrowPlates 逐行迁移到粗格层）：frontiers[p] = 板 p 的候选粗格表
	//（贴邻本板已认领格的未认领格；含过期项——弹出时惰性丢弃）。每轮：
	// ① 按 weight_p = rate_p × 前沿长度 加权抽板（带防饿死地板）；
	// ② 抽中板从前沿随机抽一格：已认领 → 丢弃重抽；未认领 → 认领，其未认领邻居入列。
	// "全板前沿同空而未铺满"在球面连通图上不可达；total ≤ 0 的防御出口只保不崩。
	static int[] GrowCoarse(int totalCoarse, List<int>[] coarseNeighbors, int[] origins, float[] rates,
		DeterministicRandom rng)
	{
		var plateOfCoarse = new int[totalCoarse];
		Array.Fill(plateOfCoarse, -1);
		var frontiers = new List<int>[origins.Length];
		for (int p = 0; p < origins.Length; p++)
		{
			plateOfCoarse[origins[p]] = p;
			frontiers[p] = new List<int>();
			foreach (int nb in coarseNeighbors[origins[p]])
				if (plateOfCoarse[nb] < 0) frontiers[p].Add(nb);
		}

		int claimed = origins.Length;
		var weights = new double[origins.Length];              // 本轮拾取权重（含防饿死地板）
		while (claimed < totalCoarse)
		{
			double totalRaw = 0;
			int aliveFrontiers = 0;
			for (int p = 0; p < frontiers.Length; p++)
			{
				totalRaw += (double)rates[p] * frontiers[p].Count;
				if (frontiers[p].Count > 0) aliveFrontiers++;
			}
			if (totalRaw <= 0 || aliveFrontiers == 0) break;   // 防御（不可达）：无处可长
			double floorW = GrowthStarveFloorFraction * totalRaw / aliveFrontiers;
			double total = 0;
			for (int p = 0; p < frontiers.Length; p++)
			{
				weights[p] = frontiers[p].Count > 0
					? Math.Max((double)rates[p] * frontiers[p].Count, floorW)
					: 0.0;
				total += weights[p];
			}
			double pick = rng.NextDouble() * total;
			double acc = 0;
			int chosen = frontiers.Length - 1;                 // 兜底：pick 贴 total 上界时归末板
			for (int p = 0; p < frontiers.Length; p++)
			{
				acc += weights[p];
				if (pick < acc) { chosen = p; break; }
			}

			var frontier = frontiers[chosen];
			int cell = -1;
			while (frontier.Count > 0)
			{
				int idx2 = rng.Next(frontier.Count);
				cell = frontier[idx2];
				frontier[idx2] = frontier[frontier.Count - 1];     // swap-remove：O(1) 弹随机位
				frontier.RemoveAt(frontier.Count - 1);
				if (plateOfCoarse[cell] < 0) break;                // 活格 → 认领
				cell = -1;                                         // 过期项 → 丢弃重抽
			}
			if (cell < 0) continue;                                // 前沿全过期 → 下轮重抽板

			plateOfCoarse[cell] = chosen;
			claimed++;
			foreach (int nb in coarseNeighbors[cell])
				if (plateOfCoarse[nb] < 0) frontiers[chosen].Add(nb);
		}
		return plateOfCoarse;
	}

	// 边界平滑（用户拍板"平滑 30%"，2026-09-30）：对异板粗格做一次多数决松弛——闭邻域
	//（自 + 粗邻居）计票，多数板**严格多于**自板的格，以 SmoothProbability 概率翻向多数板
	//（并列/少数保留自板，无板号偏置；翻格按固定顺序逐个过 rng 门 ⇒ 确定性）。
	// 单次应用不迭代 ⇒ 锯齿保留 ~70%；翻格可能清空极小板 ⇒ 交给份额护栏重采样。
	static void SmoothCoarse(int totalCoarse, List<int>[] coarseNeighbors, int[] plateOfCoarse,
		DeterministicRandom rng)
	{
		var flips = new List<(int cell, int plate)>();
		var votes = new Dictionary<int, int>();
		for (int c = 0; c < totalCoarse; c++)
		{
			int self = plateOfCoarse[c];
			var nb = coarseNeighbors[c];
			if (nb.Count == 0) continue;
			votes.Clear();
			foreach (int j in nb)
			{
				int pj = plateOfCoarse[j];
				votes.TryGetValue(pj, out int v);
				votes[pj] = v + 1;
			}
			int selfVotes = (votes.TryGetValue(self, out int sv) ? sv : 0) + 1;   // 闭邻域：自投一票
			int bestPlate = self, bestVotes = selfVotes;
			foreach (var kv in votes)
			{
				if (kv.Key == self) continue;
				if (kv.Value > bestVotes) { bestPlate = kv.Key; bestVotes = kv.Value; }
			}
			if (bestPlate != self) flips.Add((c, bestPlate));
		}
		foreach (var (cell, plate) in flips)
			if (rng.NextDouble() < SmoothProbability) plateOfCoarse[cell] = plate;
	}

	// 形状护栏：**分水岭检测**。① 逐板格算到板边界的距离（边界格 = 有异板邻居）；
	// ② "深核" = 距边界 ≥2 格的格子（BFS 连通分量）；
	// ③ 深核 ≥2 个 ⇒ 多核流域（全板格归入最近深核）；两大流域都 ≥ 公平×25% ⇒ 沙漏/窄颈。
	// 对任意 ≤4 格宽的走廊都成立（旧"拔一格"检测对 2-4 格宽走廊有盲区）；半岛/海湾不受影响。
	// 多源 BFS 流域的归属与处理序无关（同距离时先到先得，序固定 ⇒ 确定性）。
	public static bool HasFineBottleneck(int[] plateOfCell, int[] plateCellCounts, int[][] neighbors)
	{
		int n = plateOfCell.Length;
		int P = 0;
		foreach (int p in plateOfCell) if (p + 1 > P) P = p + 1;

		var dist = new int[n];                                 // 到板边界距离
		var label = new int[n];                                // 深核组件号 / 流域归属
		var bfs = new Queue<int>(n);
		for (int p = 0; p < P; p++)
		{
			int plateCells = plateCellCounts[p];
			if (plateCells < 3) continue;
			float minLobe = Math.Max(2f, plateCells * BottleneckMinLobeFraction);

			// ① 边界距离 BFS
			Array.Fill(dist, -1);
			for (int i = 0; i < n; i++)
			{
				if (plateOfCell[i] != p) continue;
				bool edge = false;
				foreach (int j in neighbors[i])
					if (plateOfCell[j] != p) { edge = true; break; }
				if (edge) { dist[i] = 0; bfs.Enqueue(i); }
			}
			while (bfs.Count > 0)
			{
				int i = bfs.Dequeue();
				foreach (int j in neighbors[i])
					if (plateOfCell[j] == p && dist[j] < 0) { dist[j] = dist[i] + 1; bfs.Enqueue(j); }
			}

			// ② 深核组件（dist ≥ 2）
			Array.Fill(label, -1);
			int comp = 0;
			for (int i = 0; i < n; i++)
			{
				if (plateOfCell[i] != p || dist[i] < 2 || label[i] >= 0) continue;
				label[i] = comp;
				bfs.Enqueue(i);
				while (bfs.Count > 0)
				{
					int u = bfs.Dequeue();
					foreach (int j in neighbors[u])
					{
						if (plateOfCell[j] != p || dist[j] < 2 || label[j] >= 0) continue;
						label[j] = comp;
						bfs.Enqueue(j);
					}
				}
				comp++;
			}
			if (comp < 2) continue;                            // 单深核 ⇒ 无沙漏

			// ③ 流域：全板格归入最近深核，两大流域 ⇒ 沙漏
			// （不清空 label——深核格在 ② 已带组件号，即流域种子）
			for (int i = 0; i < n; i++)
				if (plateOfCell[i] == p && label[i] >= 0) bfs.Enqueue(i);
			while (bfs.Count > 0)
			{
				int i = bfs.Dequeue();
				foreach (int j in neighbors[i])
					if (plateOfCell[j] == p && label[j] < 0) { label[j] = label[i]; bfs.Enqueue(j); }
			}
			var ws = new int[comp];
			for (int i = 0; i < n; i++) if (plateOfCell[i] == p && label[i] >= 0) ws[label[i]]++;
			int big = 0;
			foreach (int sz in ws) if (sz >= minLobe) big++;
			if (big >= 2) return true;                         // 沙漏/窄颈
		}
		return false;
	}

	// 无重复抽 count 个起源：partial Fisher-Yates 洗前 count 位（rng 消耗固定 count 次 → 确定性）。
	static int[] PickSeedCells(DeterministicRandom rng, int cellCount, int count)
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
}
