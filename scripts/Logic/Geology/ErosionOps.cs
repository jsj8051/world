using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // TerrainSpec（细化幅度 + 侵蚀旋钮）
using World.H3Grid;          // Ball（H3 球壳数据层）
using World.Utils;           // SphericalFbmNoise（分形细化噪声）

namespace World.Logic;

/// <summary>
/// 山体的**分形细化 + 虚拟侵蚀**算子：把 <see cref="HighlandOps"/> 合成的构造抬升
/// 从"一堵光滑的墙"刻成"脊谷相间的山系"。洋壳格（抬升恒 0）原样返回。
///
/// 两步，顺序不能反：
/// · **分形细化**：乘一层**中心化**的 ridged 分形噪声（再按均值归位 ⇒ 只改形状、不改平均幅度）
///   ⇒ 山脊带细碎起伏。必须在排水之前做：光滑面上的单流向会退化成规则的径向纹，
///   出不来树枝状沟谷。
/// · **虚拟侵蚀**：迭代 <see cref="TerrainSpec.ErosionSteps"/> 步，每步走
///   「填洼（改副本）→ 单流向 → 汇流累积 <c>A</c> → 逐格下切
///   <c>cut = ErosionDepthM · (A/A_max)^ErosionExponent · min(1, S/S_ref)</c>」。
///
/// **「虚拟」的界线**：不做时间演化的物理求解（参考口径是 dt / K / m 迭代几十步的隐式解），
/// 每一步都是"按当前的汇水量与坡度直接切一刀"——只要形状，不要量纲、不要量级可比。
/// 坡度取**逐跳落差**并按自己的分位归一 ⇒ 不必换算成物理坡度。
///
/// 只作用在**构造抬升**（山带 + 老山 + 丘陵）上，不碰陆面基底 ⇒ **海岸线不动**
/// （海岸由基底穿过海面定）；单步下切恒 ≤ <see cref="TerrainSpec.ErosionMaxCutFraction"/> × 当前抬升
/// ⇒ 抬升恒 ≥ 0，不会把山削穿成负高度。
/// </summary>
public static class ErosionOps
{
	// 分形细化的纹理尺度（形状常量；与 HighlandOps / OceanDepthOps 的噪声尺度同类，不进 TerrainSpec）。
	const float DetailWavelengthKm = 180f;
	const int DetailOctaves = 4;

	/// <summary>坡度的参考分位（逐跳落差的分位）——用它归一坡度 ⇒ 与高度口径无关，换尺度不用重调。</summary>
	const double SlopeRefQuantile = 0.90;

	/// <summary>填洼时为保证**严格下降**注入的微小梯度（m）。</summary>
	const float DescentEpsilonM = 1e-3f;

	/// <summary>
	/// 细化 + 虚拟侵蚀。返回**新数组**（陆壳的构造抬升；洋壳格恒 0）。
	/// </summary>
	/// <param name="ball">球壳数据层。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">本阶段配置（细化幅度 + 侵蚀深度 / 指数 / 下切上限 / 步数）。</param>
	/// <param name="continental">逐格是否陆壳。</param>
	/// <param name="reliefM">逐格**构造抬升**（山带 + 老山 + 丘陵；≥ 0）。</param>
	public static float[] Build(Ball ball, int seed, TerrainSpec spec, bool[] continental, float[] reliefM)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (continental == null) throw new ArgumentNullException(nameof(continental));
		if (reliefM == null) throw new ArgumentNullException(nameof(reliefM));

		int n = ball.CellDirs.Length;
		if (continental.Length != n)
			throw new ArgumentException($"陆壳掩码长度 {continental.Length} ≠ 格数 {n}", nameof(continental));
		if (reliefM.Length != n)
			throw new ArgumentException($"抬升数组长度 {reliefM.Length} ≠ 格数 {n}", nameof(reliefM));

		var dirs = ball.CellDirs;
		var height = new float[n];     // 工作高度：细化后的构造抬升，也是被下切的对象
		var active = new bool[n];      // 参与细化 / 侵蚀的格（陆壳 且 抬升 > 0）

		// ── ① 分形细化 ──
		var detail = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_MountainDetail), DetailWavelengthKm, DetailOctaves);
		var mult = new float[n];
		double multSum = 0;
		int multCount = 0;
		for (int i = 0; i < n; i++)
		{
			if (!continental[i] || !(reliefM[i] > 0f)) continue;
			mult[i] = 1f + spec.MountainDetailAmount * (detail.SampleRidged(dirs[i]) * 2f - 1f);
			multSum += mult[i];
			multCount++;
		}
		float multMean = multCount > 0 ? (float)(multSum / multCount) : 1f;
		if (!(multMean > 0f)) multMean = 1f;
		Parallel.For(0, n, i =>
		{
			if (!(mult[i] > 0f)) return;
			height[i] = reliefM[i] * mult[i] / multMean;
			active[i] = true;
		});

		// ── ② 虚拟侵蚀 ──
		int steps = Math.Max(0, spec.ErosionSteps);
		float depth = Math.Max(0f, spec.ErosionDepthM);
		float exponent = Math.Max(0f, spec.ErosionExponent);
		float capFraction = Math.Clamp(spec.ErosionMaxCutFraction, 0f, 1f);
		for (int step = 0; step < steps; step++)
		{
			// a) 填洼（改副本，绝不回写）——保证每格都有下游；同时拿到升序弹出序
			var filled = (float[])height.Clone();
			int[] order = FillAndOrder(ball, filled);
			if (order.Length == 0) break;                    // 没有出口：无从排水

			// b) 单流向：严格更低的邻居中最低者（平局取格索引最小 ⇒ 确定性；等高不走边 ⇒ 无环）
			var downstream = new int[n];
			Array.Fill(downstream, -1);
			Parallel.For(0, n, i =>
			{
				if (!active[i]) return;
				int best = -1;
				float bestH = float.PositiveInfinity;
				foreach (int j in ball.CellNeighbors[i])
				{
					float hj = filled[j];
					if (hj >= filled[i]) continue;
					if (hj < bestH || (hj == bestH && j < best)) { bestH = hj; best = j; }
				}
				downstream[i] = best;
			});

			// c) 汇流累积：逆着弹出序走 = 高程降序 = 拓扑序（下游格恒排在更后）
			var accum = new float[n];
			for (int i = 0; i < n; i++)
				if (active[i]) accum[i] = 1f;
			float aMax = 0f;
			for (int k = order.Length - 1; k >= 0; k--)
			{
				int i = order[k];
				float a = accum[i];
				if (a > aMax) aMax = a;
				int d = downstream[i];
				if (d >= 0) accum[d] += a;
			}
			if (!(aMax > 0f)) break;

			// d) 逐跳落差（读填洼后的面：每格都有下游 ⇒ 落差恒 > 0），按其分位归一
			var slope = new float[n];
			Parallel.For(0, n, i =>
			{
				if (!active[i]) return;
				float best = 0f;
				foreach (int j in ball.CellNeighbors[i])
				{
					float drop = filled[i] - filled[j];
					if (drop > best) best = drop;
				}
				slope[i] = best;
			});
			float slopeRef = Quantile(slope, active, SlopeRefQuantile);
			if (!(slopeRef > 0f)) break;

			// e) 下切：汇水量越大、坡越陡 ⇒ 切得越深；上界 = 当前抬升的一档比例（防削穿）
			Parallel.For(0, n, i =>
			{
				if (!active[i]) return;
				float aRel = accum[i] / aMax;
				float flow = MathF.Pow(aRel, exponent);
				float slopeF = MathF.Min(1f, slope[i] / slopeRef);
				float cut = depth * flow * slopeF;
				float cap = capFraction * height[i];
				if (cut > cap) cut = cap;
				height[i] -= cut;
			});
		}

		return height;
	}

	/// <summary>
	/// priority-flood 填洼（Barnes 改进版：从所有出口按高度升序往外抬），**就地改** <paramref name="h"/>，
	/// 并返回**升序弹出序**。下游格恒早于上游格 ⇒ 逆序即拓扑序，不必另做排序。
	/// 出口 = <c>h ≤ 0</c>（构造抬升为 0 的格：平原 / 洋壳）。
	/// </summary>
	static int[] FillAndOrder(Ball ball, float[] h)
	{
		int n = h.Length;
		var visited = new bool[n];
		var pq = new PriorityQueue<int, float>();
		var order = new List<int>(n);
		for (int i = 0; i < n; i++)
			if (h[i] <= 0f) { visited[i] = true; pq.Enqueue(i, h[i]); }

		while (pq.Count > 0)
		{
			int c = pq.Dequeue();
			order.Add(c);
			foreach (int nb in ball.CellNeighbors[c])
			{
				if (nb < 0 || nb >= n || visited[nb]) continue;
				visited[nb] = true;
				if (h[nb] <= h[c]) h[nb] = h[c] + DescentEpsilonM;
				pq.Enqueue(nb, h[nb]);
			}
		}
		return order.ToArray();
	}

	/// <summary>逐格场在**指定格集**上的分位阈值（只统计 &gt; 0 的格；排序在副本上做 ⇒ 与线程调度无关）。
	/// 格集为空 ⇒ 0。</summary>
	static float Quantile(float[] field, bool[] keep, double q)
	{
		var samples = new List<float>();
		for (int i = 0; i < field.Length; i++)
			if (keep[i] && field[i] > 0f) samples.Add(field[i]);
		if (samples.Count == 0) return 0f;
		samples.Sort();
		int idx = (int)Math.Floor(q * samples.Count);
		if (idx < 0) idx = 0;
		if (idx >= samples.Count) idx = samples.Count - 1;
		return samples[idx];
	}
}
