using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 山体的分形细化 + 虚拟侵蚀算子：把 <see cref="HighlandOps"/> 合成的构造抬升刻成脊谷相间的山系。
/// 两步顺序不能反：必须先细化再排水，光滑面上先排水只会退化成规则径向纹，出不来树枝状沟谷。
/// 细化取中心化普通 fBm（按均值归位 ⇒ 只改形状不改平均幅度），陡坡按坡度分位抑制幅度；不可换
/// ridged：其脊落在 Perlin 零等值面上、每隔半个基波长一条长棱，一格 1.4 km 时落到采样极限以下，
/// 沿六边形格轴混叠成条纹。
/// 虚拟侵蚀不做物理量纲：不做 dt / K / m 迭代几十步的隐式解，每步按当前汇水量与坡度直接切一刀，
/// 只要形状，不求量级可比；坡度取逐跳落差并按自身分位归一，故不必换算物理坡度。
/// 只作用在构造抬升上（不碰陆面基底 ⇒ 海岸线不动），单步下切恒 ≤ <see cref="TerrainSpec.ErosionMaxCutFraction"/>
/// × 当前抬升 ⇒ 抬升恒 ≥ 0。
/// 沉积沿排水路径把物质搬给更低的邻格（含洋壳格）⇒ 洋壳格不保证为 0，调用方只应读陆壳格。
/// </summary>
public static class ErosionOps
{
	// 分形细化的纹理尺度（形状常量，不进 TerrainSpec）：27 km ≈ 11 倍邻格中心距（2.47 km）。
	// 细化只负责把山带打散成几个山块，一格一格的脊谷交给侵蚀去刻。
	const float DetailWavelengthKm = 27f;

	/// <summary>最多叠几层。实际层数还要被采样极限截断（见 <see cref="MinDetailCellRatio"/>）。</summary>
	const int DetailOctaves = 4;

	/// <summary>能真实采到的最短波长（倍邻格中心距）：nearest 逐格取色下，一个波长至少要跨
	/// 3 格才成形；降到 2 格附近与六边形格轴共振、混叠成沿格轴的长条 ⇒ 更细的倍频层直接不叠。</summary>
	const double MinDetailCellRatio = 3.0;

	/// <summary>坡度的参考分位（逐跳落差的分位）——用它归一坡度 ⇒ 与高度口径无关，换尺度不用重调。</summary>
	const double SlopeRefQuantile = 0.90;

	/// <summary>细化抑制的低 / 高分位：低分位以下不抑制，高分位以上压到最狠。</summary>
	const double DetailSlopeLowQuantile = 0.65;
	const double DetailSlopeHighQuantile = 0.95;

	/// <summary>填洼时为保证严格下降注入的微小梯度（m）。</summary>
	const float DescentEpsilonM = 1e-3f;

	/// <summary>沉积的落差尺度（m）：落差远小于它时沉积比例低，落差大时接近沉积上限。</summary>
	const float DepositScaleM = 20f;

	/// <summary>热力侵蚀每步的松弛系数（每次只搬走超出休止角那部分的一小截）。</summary>
	const float TalusRelax = 0.4f;

	/// <summary>热力侵蚀的休止角分位（逐跳落差的分位）：只搬走比它更陡的边。</summary>
	const double TalusQuantile = 0.99;

	/// <summary>热力侵蚀的总遍数（在所有水力侵蚀步之后做）。</summary>
	const int TalusPasses = 2;

	/// <summary>平原下切的坡度归一化分位。</summary>
	const double PlainsSlopeQuantile = 0.90;

	/// <summary>汇流量归一的分位：用 P99(A) 而不是 A_max（多流向把水摊开后，几乎没有格接近 A_max）。</summary>
	const double AccumRefQuantile = 0.99;

	/// <summary>多流向的落差权重指数（正整数）：越大越集中到最陡的几条下游（4 ≈ 限指数多流向）。</summary>
	const int FlowConcentration = 4;

	/// <summary>平原下切的最小离海距离（格）。</summary>
	const double PlainsCoastCells = 2.0;

	/// <summary>平原下切的最小离海面高度（m）。</summary>
	const float PlainsMinAboveSeaM = 3f;

	/// <summary>细化 + 虚拟侵蚀，返回新数组（陆壳格经细化 / 下切 / 沉积后的构造抬升）。
	/// 沉积会沿排水路径把物质搬给更低的邻格（含洋壳格）⇒ 洋壳格不保证为 0；调用方只应读陆壳格。</summary>
	/// <param name="spec">本阶段配置（细化幅度 / 抑制 / 侵蚀深度 / 步数 / 沉积）。</param>
	/// <param name="reliefM">逐格构造抬升（山带 + 老山 + 丘陵；≥ 0）。</param>
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
		float neighborKm = (float)SpatialScale.Of(ball).NeighborCenterKm;

		// ── ① 分形细化（陡坡抑制）──
		float[] slope = SlopeFromRelief(ball, reliefM, continental, neighborKm, out active);
		float slopeLow = Quantile(slope, active, DetailSlopeLowQuantile);
		float slopeHigh = Quantile(slope, active, DetailSlopeHighQuantile);
		float suppression = Math.Clamp(spec.MountainDetailSuppression, 0f, 0.9f);

		// 层数按采样极限截断：波长不到 3 倍邻格中心距的倍频层采不出来，叠上去只会变成条纹伪影
		double minKm = MinDetailCellRatio * neighborKm;
		int octaves = NoiseSampling.ClampOctaves(DetailOctaves, DetailWavelengthKm, minKm);
		var detail = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_MountainDetail), DetailWavelengthKm, octaves);

		var mult = new float[n];
		double multSum = 0;
		int multCount = 0;
		for (int i = 0; i < n; i++)
		{
			if (!active[i]) continue;
			float suppress = 1f;
			if (suppression > 0f && slopeHigh > slopeLow)
				suppress = 1f / (1f + suppression * MathOps.Smoothstep(slopeLow, slopeHigh, slope[i]));
			mult[i] = 1f + spec.MountainDetailAmount * detail.Sample(dirs[i]) * suppress;
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
		float deposition = Math.Clamp(spec.ErosionDeposition, 0f, 0.45f);

		for (int step = 0; step < steps; step++)
		{
			// a) 填洼（改副本，绝不回写）——保证每格都有下游；同时拿到升序弹出序
			var filled = (float[])height.Clone();
			int[] order = FillAndOrder(ball, filled);
			if (order.Length == 0) break;

			// b) 逆着弹出序走 = 高程降序 = 拓扑序（下游格恒排在更后），一次遍历即可累积。
			// 多流向（按落差权重分摊）而非单流向：单流向会让汇流量场成为一排竖条，
			// 切出平行长沟；多流向让 A 连续，谷网才分叉。
			var accum = new float[n];
			AccumulateMultiFlow(ball, filled, order, active, accum);
			float aRef = Quantile(accum, active, AccumRefQuantile);
			if (!(aRef > 0f)) break;

			// c) 逐跳落差（读填洼后的面：每格都有下游 ⇒ 落差恒 > 0），按其分位归一
			var slopeNow = new float[n];
			SlopeToDownstream(ball, filled, active, slopeNow);
			float slopeRef = Quantile(slopeNow, active, SlopeRefQuantile);
			if (!(slopeRef > 0f)) break;

			// d) 下切：汇水量越大、坡越陡 ⇒ 切得越深；上界 = 当前抬升的一档比例（防削穿）
			var cut = new float[n];
			Parallel.For(0, n, i =>
			{
				if (!active[i]) return;
				float aRel = MathF.Min(1f, accum[i] / aRef);
				float flow = MathF.Pow(aRel, exponent);
				float slopeF = MathF.Min(1f, slopeNow[i] / slopeRef);
				float c = depth * flow * slopeF;
				float cap = capFraction * height[i];
				if (c > cap) c = cap;
				if (c > 0f)
				{
					cut[i] = c;
					height[i] -= c;
				}
			});

			// e) 沉积必须在下切之后（它读本步的 cut），并沿与汇流累积同一套排水路径。
			if (deposition > 0f) Deposit(ball, filled, order, cut, deposition, height);
		}

		// ── ③ 热力侵蚀：把超过休止角的陡边削缓（只搬最陡的那条边）──
		// `ErosionSteps = 0` = 关断整套虚拟侵蚀（含热力遍）：step 循环不跑时这里也不跑。
		if (steps > 0 && TalusPasses > 0)
		{
			var talusSlope = new float[n];
			Parallel.For(0, n, i =>
			{
				if (!active[i]) return;
				float best = 0f;
				foreach (int j in ball.CellNeighbors[i])
				{
					float drop = height[i] - height[j];
					if (drop > best) best = drop;
				}
				talusSlope[i] = best;
			});
			float talusDrop = Quantile(talusSlope, active, TalusQuantile);
			if (talusDrop > 0f)
				for (int pass = 0; pass < TalusPasses; pass++)
					Thermal(ball, height, active, talusDrop);
		}

		return height;
	}

	/// <summary>平原浅下切：在最终陆面上按虚拟汇水量切浅槽，让水系在平原上留下河谷
	/// （不碰海格、不碰离海 &lt;2 格的格）。封顶 <c>h − sea − 1</c> ⇒ 不会被切到海面以下，海岸线不变。</summary>
	/// <param name="spec">本阶段配置（<see cref="TerrainSpec.PlainsIncisionM"/> /
	/// <see cref="TerrainSpec.ErosionExponent"/> / 海面）。</param>
	/// <param name="heightM">逐格最终高度（就地改写陆格）。</param>
	public static void IncisePlains(Ball ball, TerrainSpec spec, float[] heightM, bool[] continental)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));
		if (continental == null) throw new ArgumentNullException(nameof(continental));

		float depth = spec.PlainsIncisionM;
		if (!(depth > 0f)) return;
		float exponent = Math.Max(0f, spec.ErosionExponent);

		int n = heightM.Length;
		if (continental.Length != n)
			throw new ArgumentException($"陆壳掩码长度 {continental.Length} ≠ 格数 {n}", nameof(continental));

		float seaM = spec.SeaLevelM;
		double neighborKm = SpatialScale.Of(ball).NeighborCenterKm;
		float coastGuardKm = (float)(PlainsCoastCells * neighborKm);

		// 离海距离（多源 Dijkstra，截断 3 格足够判「≥2 格」）
		var seaSources = new List<int>();
		for (int i = 0; i < n; i++)
			if (!continental[i] || heightM[i] <= seaM) seaSources.Add(i);
		if (seaSources.Count == 0) return;
		var coast = DistanceFieldOps.Build(ball, seaSources, _ => 0f, 3.0 * neighborKm);

		// 排水面：相对海面、海格置 0（填洼的出口）
		var work = new float[n];
		Parallel.For(0, n, i => work[i] = MathF.Max(0f, heightM[i] - seaM));
		var filled = (float[])work.Clone();
		int[] order = FillAndOrder(ball, filled);
		if (order.Length == 0) return;

		var active = new bool[n];
		for (int i = 0; i < n; i++) active[i] = continental[i] && heightM[i] > seaM;

		// 汇流累积（与山体侵蚀同一套多流向口径）
		var accum = new float[n];
		AccumulateMultiFlow(ball, filled, order, active, accum);
		float aRef = Quantile(accum, active, AccumRefQuantile);
		if (!(aRef > 0f)) return;

		var slope = new float[n];
		SlopeToDownstream(ball, filled, active, slope);
		float slopeRef = Quantile(slope, active, PlainsSlopeQuantile);
		if (!(slopeRef > 0f)) return;

		Parallel.For(0, n, i =>
		{
			if (!active[i]) return;
			float aboveSea = heightM[i] - seaM;
			if (aboveSea <= PlainsMinAboveSeaM) return;
			if (coast.DistKm[i] < coastGuardKm) return;

			float aRel = MathF.Min(1f, accum[i] / aRef);
			float flow = MathF.Pow(aRel, exponent);
			float cut = depth * flow * MathF.Min(1f, slope[i] / slopeRef);
			float cap = aboveSea - 1f;
			if (cut > cap) cut = cap;
			if (cut > 0f) heightM[i] -= cut;
		});
	}

	/// <summary>多流向汇流累积：逆着升序弹出序走 = 高程降序 = 拓扑序，一次遍历即可。</summary>
	static void AccumulateMultiFlow(Ball ball, float[] filled, int[] order, bool[] active, float[] accum)
	{
		for (int i = 0; i < accum.Length; i++)
			if (active[i]) accum[i] = 1f;

		for (int k = order.Length - 1; k >= 0; k--)
		{
			int i = order[k];
			float a = accum[i];
			if (!(a > 0f)) continue;

			float sumWeight = 0f;
			foreach (int j in ball.CellNeighbors[i])
			{
				float drop = filled[i] - filled[j];
				if (drop > 0f) sumWeight += FlowWeight(drop);
			}
			if (!(sumWeight > 0f)) continue;

			float share = a / sumWeight;
			foreach (int j in ball.CellNeighbors[i])
			{
				float drop = filled[i] - filled[j];
				if (drop > 0f) accum[j] += FlowWeight(drop) * share;
			}
		}
	}

	/// <summary>逐格到更低邻居的最大逐跳落差（m；填洼后每格都有下游 ⇒ 恒 &gt; 0）。</summary>
	static void SlopeToDownstream(Ball ball, float[] filled, bool[] active, float[] slope)
	{
		Parallel.For(0, slope.Length, i =>
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
	}

	/// <summary>沉积：按拓扑序（高程降序）把每格的下切量 + 上游来沙按落差分摊给下游，在下游格沉积。
	/// 沉积量 ∝ <c>1 − exp(−drop / depScale)</c>；剩余泥沙继续往下游走。</summary>
	static void Deposit(Ball ball, float[] filled, int[] order, float[] cut, float deposition, float[] height)
	{
		int n = height.Length;
		var sediment = new float[n];    // 从上游收到的泥沙（逐格累加；下游格在更后处理）
		for (int k = order.Length - 1; k >= 0; k--)
		{
			int i = order[k];
			float s = cut[i] + sediment[i];
			if (!(s > 0f)) continue;

			float sumDrop = 0f;
			foreach (int j in ball.CellNeighbors[i])
			{
				float drop = filled[i] - filled[j];
				if (drop > 0f) sumDrop += drop;
			}
			if (!(sumDrop > 0f)) continue;

			float share = s / sumDrop;
			foreach (int j in ball.CellNeighbors[i])
			{
				float drop = filled[i] - filled[j];
				if (!(drop > 0f)) continue;
				float branch = share * drop;
				float deposit = branch * deposition * (1f - MathF.Exp(-drop / DepositScaleM));
				if (deposit > 0f) height[j] += deposit;
				sediment[j] += branch - deposit;
			}
		}
	}

	/// <summary>热力侵蚀：每格把最陡一条边上超出休止角（<paramref name="talusDrop"/>）的物质搬给该低邻居。
	/// 先算「目标 + 搬运量」，再按邻居反查收到多少（双缓冲 ⇒ 与线程调度无关）。</summary>
	static void Thermal(Ball ball, float[] height, bool[] active, float talusDrop)
	{
		int n = height.Length;
		var target = new int[n];
		var move = new float[n];
		Array.Fill(target, -1);

		Parallel.For(0, n, i =>
		{
			if (!active[i]) return;
			float h = height[i];
			float best = talusDrop;
			int bestJ = -1;
			foreach (int j in ball.CellNeighbors[i])
			{
				float drop = h - height[j];
				if (drop > best)
				{
					best = drop;
					bestJ = j;
				}
			}
			if (bestJ < 0) return;
			target[i] = bestJ;
			move[i] = 0.5f * (best - talusDrop) * TalusRelax;
		});

		var delta = new float[n];
		Parallel.For(0, n, i =>
		{
			float incoming = 0f;
			foreach (int j in ball.CellNeighbors[i])
				if (target[j] == i) incoming += move[j];
			delta[i] = incoming - move[i];
		});

		Parallel.For(0, n, i =>
		{
			float v = height[i] + delta[i];
			height[i] = v > 0f ? v : 0f;
		});
	}

	/// <summary>由构造抬升算逐格最大落差（m / 邻格中心距）；同时标出参与细化 / 侵蚀的格。</summary>
	static float[] SlopeFromRelief(Ball ball, float[] reliefM, bool[] continental, float neighborKm,
		out bool[] active)
	{
		int n = reliefM.Length;
		var slope = new float[n];
		var act = new bool[n];
		var neighbors = ball.CellNeighbors;
		Parallel.For(0, n, i =>
		{
			if (!continental[i] || !(reliefM[i] > 0f)) return;
			act[i] = true;
			float best = 0f;
			foreach (int j in neighbors[i])
			{
				float drop = reliefM[i] - reliefM[j];
				if (drop > best) best = drop;
			}
			slope[i] = best / MathF.Max(neighborKm, 1e-3f);
		});
		active = act;
		return slope;
	}

	/// <summary>priority-flood 填洼（Barnes 改进版：从所有出口按高度升序往外抬），就地改 <paramref name="h"/>，
	/// 返回升序弹出序：下游格恒早于上游格 ⇒ 逆序即拓扑序，不必另做排序。
	/// 出口 = <c>h ≤ 0</c>（构造抬升为 0 的格：平原 / 洋壳）。</summary>
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

	/// <summary>多流向的落差权重（<c>落差^FlowConcentration</c>）：把水集中到最陡的下游，汇流量才拉得开。</summary>
	static float FlowWeight(float drop)
	{
		float weight = 1f;
		for (int k = 0; k < FlowConcentration; k++) weight *= drop;
		return weight;
	}

	/// <summary>逐格场在指定格集上的分位阈值（只统计 &gt; 0 的格；格集为空 ⇒ 0）。</summary>
	static float Quantile(float[] field, bool[] keep, double q)
	{
		var samples = new List<float>();
		for (int i = 0; i < field.Length; i++)
			if (keep[i] && field[i] > 0f) samples.Add(field[i]);
		if (samples.Count == 0) return 0f;
		samples.Sort();
		return MathOps.QuantileSorted(samples, q);
	}

}
