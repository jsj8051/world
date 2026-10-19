using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // TerrainSpec（高地旋钮）+ BoundaryRole（四档汇聚角色）
using World.H3Grid;          // Ball（H3 球壳数据层）
using World.Utils;           // SphericalFbmNoise（山脊 / 老山 / 丘陵 / 高原场）

namespace World.Logic;

/// <summary>
/// 陆壳高地的构造算子：给**陆壳**的格算出两样加性高度 ——
/// <c>MountainM</c>（山带 + 古老山系 + 丘陵）与 <c>PlateauM</c>（高原底座）。
/// 洋壳的格一律不参与——它们由 <see cref="OceanDepthOps"/> 定。
///
/// **山带按汇聚角色分档**（见 <see cref="BoundaryRole"/>）：同样一场碰撞，
/// 陆×陆两侧都长宽带高山、陆×洋只在陆侧退后一条窄山链、俯冲侧只剩一道低矮增生楔。
/// 带宽 = 基础带宽 + 加宽量 × 汇聚强度（撞得越狠铺得越开）；陆侧山链的峰**内移**一段
/// （海岸那一段留给大陆架）。张裂带把抬升压低——张裂处不该跟着旁边的山一起被抬起来。
///
/// 幅度走**归一制**：先算全场相对抬升，再把第 99.7 百分位映射到
/// <see cref="TerrainSpec.MountainPeakM"/>（⇒ 峰高跨种子可比），最后把折点以上的部分压缩
/// （⇒ 不会冒出万米高峰）。归一在**平滑之后**做 ⇒ 平毛刺不改峰高目标。
///
/// 高原底座是**独立的一层**（不是把山铺平）：山带背后一大片平坦高地，沿海岸收掉。
///
/// 最后一步把山体交给 <see cref="ErosionOps"/> 细化 + 虚拟侵蚀（分形噪声加细碎起伏、
/// 按虚拟水系下切）⇒ 山带不再是等高的墙。
///
/// 与 <see cref="OceanDepthOps"/> 同分工：本类只产"陆壳的构造高度"，
/// 陆面的噪声基底由 <see cref="TerrainManager.SampleLand"/> 给。
/// </summary>
public static class HighlandOps
{
	/// <summary>活动边界的判定阈值（法向相对速率，无量纲）：超过它才算一条张裂边界。</summary>
	const float ActiveMotion = 0.25f;

	/// <summary>强度归一尺度：<c>cs = clamp(|motion| / 本值, 0, 1)</c>。</summary>
	const float MotionScale = 0.9f;

	/// <summary>平滑权重 ≈ 6/7 —— 等价于「自身 + 6 个邻居 取平均」（球面网格上九成以上的格是 6 邻居）。</summary>
	const float BlurWeight = 6f / 7f;

	/// <summary>带宽 km → 跳数 的折算系数：实测「真实邻格中心距 / 等积格边长」≈ 1.78
	/// （直接用 `CellEdgeKm` 折算会把带宽低估约 1.8 倍）。</summary>
	const double HopToEdgeRatio = 1.78;

	// 噪声尺度（形状常量，与 `OceanDepthOps` 同分工：物理旋钮进 TerrainSpec，纹理尺度留在这里）
	const float OldWavelengthKm = 8100f;
	const int OldOctaves = 5;
	const float HillsWavelengthKm = 7000f;
	const int HillsOctaves = 4;
	const float ReachWavelengthKm = 14000f;
	const int ReachOctaves = 4;
	const float AlongWavelengthKm = 6700f;
	const int AlongOctaves = 4;
	const float ShieldWavelengthKm = 12800f;
	const int ShieldOctaves = 4;

	// 「冒头门」的分位（在陆壳格上取）：低分位 = 开始出现，高分位 = 完全出现。
	// 用分位而不是固定阈值 ⇒ 与具体噪声实现的值域无关，换噪声也不用重调。
	// 覆盖率 ≈ (1 − 低分位)：老山 ≈ 10%（成形于最陡的 1%），丘陵 ≈ 30%（成形于最丘的 3%）。
	const double OldQtyLow = 0.90;
	const double OldQtyHigh = 0.99;
	const double HillsQtyLow = 0.70;
	const double HillsQtyHigh = 0.97;
	const double AlongQtyLow = 0.55;
	const double AlongQtyHigh = 0.85;
	const double InlandQtyLow = 0.55;
	const double InlandQtyHigh = 0.90;
	const double ShieldQtyLow = 0.45;
	const double ShieldQtyHigh = 0.85;

	/// <summary>分位抽样的上限格数（超过就隔格取）——只用于定阈值，不必全量排序。</summary>
	const int QuantileSampleCap = 262144;

	/// <summary>结果。<c>MountainM</c>：山带 + 老山 + 丘陵（≥ 0）；<c>PlateauM</c>：高原底座（≥ 0）。
	/// 两者都只对陆壳格有效（洋壳格 = 0）。</summary>
	public readonly struct Field
	{
		public readonly float[] MountainM;
		public readonly float[] PlateauM;

		internal Field(float[] mountainM, float[] plateauM)
		{
			MountainM = mountainM;
			PlateauM = plateauM;
		}
	}

	/// <param name="ball">球壳数据层。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">本阶段配置（山带分档 / 老山 / 丘陵 / 裂谷 / 高原）。</param>
	/// <param name="plateOfCell">逐格板块号（判边界格用）。</param>
	/// <param name="continental">逐格是否陆壳。</param>
	/// <param name="landHeight">逐格**陆面基底**（常量基线 + 三档变化场；非陆壳格不用）。</param>
	/// <param name="motion">逐格边界法向相对速率（&lt; 0 汇聚 / &gt; 0 张裂）。</param>
	/// <param name="role">逐格汇聚角色。</param>
	/// <param name="large">大尺度陆海场——兼作"有多深入大陆内"的判据（盾状台地用）。</param>
	public static Field Build(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell,
		bool[] continental, float[] landHeight, float[] motion, BoundaryRole[] role,
		SphericalFbmNoise large)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (continental == null) throw new ArgumentNullException(nameof(continental));
		if (landHeight == null) throw new ArgumentNullException(nameof(landHeight));
		if (motion == null) throw new ArgumentNullException(nameof(motion));
		if (role == null) throw new ArgumentNullException(nameof(role));
		if (large == null) throw new ArgumentNullException(nameof(large));

		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		float seaM = spec.SeaLevelM;
		float hopKm = (float)(SpatialScale.Of(ball).CellEdgeKm * HopToEdgeRatio);

		// ── ① 边界格标记（跳数 0 = 该格有异板块邻居）+ 到最近汇聚边界的跳数（同板块内扩散）──
		int[] boundaryHops = PlateBoundaryOps.BoundaryHops(ball, plateOfCell);
		int[] hops = ConvergenceHops(ball, plateOfCell, role, boundaryHops);

		// ── ② 张裂带：到最近活动张裂边界的真实距离 + 强度 ──
		var riftSources = new List<int>();
		for (int i = 0; i < n; i++)
			if (boundaryHops[i] == 0 && motion[i] > ActiveMotion) riftSources.Add(i);
		var rift = DistanceFieldOps.Build(ball, riftSources,
			i => MathF.Min(1f, motion[i] / MotionScale), 4f * spec.RiftBandKm);

		// ── ③ 噪声场 ──
		var ridge = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Ridge), spec.MountainRidgeWavelengthKm, spec.MountainRidgeOctaves);
		var oldRange = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_OldRange), OldWavelengthKm, OldOctaves);
		var hills = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Hills), HillsWavelengthKm, HillsOctaves);
		var reach = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Reach), ReachWavelengthKm, ReachOctaves);
		var along = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Along), AlongWavelengthKm, AlongOctaves);
		var shield = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Shield), ShieldWavelengthKm, ShieldOctaves);

		// ── ④ 取原始噪声（冒头门要按各自的分布定阈值，故先存后门）──
		var tectonic = new float[n];   // 山带抬升（相对值，归一前）
		var oldRaw = new float[n];     // 老山脊（ridged，∈ [0,1]）
		var hillsRaw = new float[n];   // 丘陵场
		var alongRaw = new float[n];   // 高原沿走向
		var inlandRaw = new float[n];  // 大陆内部程度
		var shieldRaw = new float[n];  // 盾状台地
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			Vector3 dir = dirs[i];
			oldRaw[i] = oldRange.SampleRidged(dir);
			hillsRaw[i] = hills.Sample(dir);
			alongRaw[i] = along.Sample(dir);
			inlandRaw[i] = large.Sample(dir);
			shieldRaw[i] = shield.Sample(dir);
		});

		float[] oldQ = Quantiles(n, oldRaw, i => continental[i], OldQtyLow, OldQtyHigh);
		float[] hillsQ = Quantiles(n, hillsRaw, i => continental[i], HillsQtyLow, HillsQtyHigh);
		float[] alongQ = Quantiles(n, alongRaw, i => continental[i], AlongQtyLow, AlongQtyHigh);
		float[] inlandQ = Quantiles(n, inlandRaw, i => continental[i], InlandQtyLow, InlandQtyHigh);
		float[] shieldQ = Quantiles(n, shieldRaw, i => continental[i], ShieldQtyLow, ShieldQtyHigh);

		// 就地把"原始噪声"折成"权重场"（本节之后这些数组一律按权重读）
		var oldWeight = oldRaw;        // 老山门 × 丘陵门（"这一带本来就有起伏"）
		var hillsWeight = hillsRaw;    // 丘陵区门
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			float oldGate = Smoothstep(oldQ[0], oldQ[1], oldRaw[i]);
			float hillsValue = hillsRaw[i];
			oldWeight[i] = oldGate * Smoothstep(hillsQ[0], hillsQ[1], hillsValue + 0.3f);
			hillsWeight[i] = Smoothstep(hillsQ[0], hillsQ[1], hillsValue);
			alongRaw[i] = Smoothstep(alongQ[0], alongQ[1], alongRaw[i]);
			inlandRaw[i] = Smoothstep(inlandQ[0], inlandQ[1], inlandRaw[i]);
			shieldRaw[i] = Smoothstep(shieldQ[0], shieldQ[1], shieldRaw[i]);
		});

		// ── ⑤ 山带（相对值；归一留给下一步）──
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			BoundaryRole r = role[i];
			if (r == BoundaryRole.None) return;
			int h = hops[i];
			if (h < 0) return;
			float cs = MathF.Min(1f, -motion[i] / MotionScale);
			if (!(cs > 0f)) return;

			float cd = h * hopKm;
			float coef, bandKm, centerKm;
			switch (r)
			{
				case BoundaryRole.Lower:
					coef = spec.MountainLowerCoef;
					bandKm = spec.MountainUpperBandKm + spec.MountainBandConvSpreadKm * cs;
					centerKm = 0f;
					break;
				case BoundaryRole.Arc:
					coef = spec.MountainArcCoef;
					bandKm = spec.MountainArcBandKm;
					centerKm = spec.MountainArcInlandKm;
					break;
				case BoundaryRole.Slab:
					coef = spec.MountainSlabCoef;
					bandKm = spec.MountainSlabBandKm;
					centerKm = 0f;
					break;
				default:   // Upper
					coef = spec.MountainUpperCoef;
					bandKm = spec.MountainUpperBandKm + spec.MountainBandConvSpreadKm * cs;
					centerKm = 0f;
					break;
			}

			float mod = spec.MountainRidgeFloor
				+ (1f - spec.MountainRidgeFloor) * (ridge.Sample(dirs[i]) * 0.5f + 0.5f);
			tectonic[i] = coef * spec.MountainPeakM * cs * Band(cd, centerKm, bandKm) * mod;
		});

		// ── ⑥ 平滑（归一之前 ⇒ 削毛刺不改峰高目标）──
		FieldBlurOps.Blur(ball, tectonic, spec.MountainSmoothWeight, 1);

		// ── ⑦ 归一 + 压峰：第 99.7 百分位 → MountainPeakM，折点以上只留一小截 ──
		float[] refQ = Quantiles(n, tectonic, i => continental[i] && tectonic[i] > 0f, 0.997);
		float gain = refQ[0] > 0f ? spec.MountainPeakM / refQ[0] : 0f;
		float knee = spec.MountainKnee * spec.MountainPeakM;

		// ── ⑧ 合成：山带（压峰）+ 老山 + 丘陵，再被张裂带压低；高原底座另走一条 ──
		var mountain = new float[n];
		var plateau = new float[n];
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			Vector3 dir = dirs[i];
			BoundaryRole r = role[i];
			float cs = MathF.Min(1f, -motion[i] / MotionScale);
			if (!(cs > 0f)) cs = 0f;
			float cd = hops[i] >= 0 ? hops[i] * hopKm : float.MaxValue;

			float u = tectonic[i] * gain;
			if (u > knee) u = knee + (u - knee) * spec.MountainKneeCompress;
			u += spec.OldMountainHeightM * oldWeight[i]
				+ spec.HillsHeightM * hillsWeight[i];

			float suppress = 1f - spec.RiftSuppressStrength * rift.Strength[i]
				* Band(rift.DistKm[i], 0f, spec.RiftBandKm);
			mountain[i] = u * suppress;

			// 高原底座：碰撞带 / 强俯冲带上盘背后的一大片平坦高地
			float pb = 0f;
			if ((r == BoundaryRole.Upper || r == BoundaryRole.Arc) && cs > spec.PlateauConvMin)
			{
				float reachKm = (r == BoundaryRole.Upper ? spec.PlateauUpperReachKm : spec.PlateauArcReachKm)
					+ spec.PlateauReachSpreadKm * (0.5f + 0.5f * reach.Sample(dir));
				float inBand = Smoothstep(0f, spec.PlateauInnerRampKm, cd)
					* (1f - Smoothstep(reachKm, reachKm + spec.PlateauOuterRampKm, cd));
				pb = (r == BoundaryRole.Upper ? spec.PlateauUpperHeightM : spec.PlateauArcHeightM)
					* Smoothstep(spec.PlateauConvMin, spec.PlateauConvFull, cs)
					* inBand * alongRaw[i];
			}
			pb += spec.PlateauShieldHeightM * shieldRaw[i] * inlandRaw[i];
			plateau[i] = pb * Smoothstep(0f, spec.PlateauCoastFadeM, landHeight[i] - seaM);
		});

		// ── ⑨ 山体细化 + 虚拟侵蚀：光滑的一堵墙 → 脊谷相间的山系 ──
		mountain = ErosionOps.Build(ball, seed, spec, continental, mountain);

		FieldBlurOps.Blur(ball, plateau, BlurWeight, spec.PlateauSmoothPasses);

		return new Field(mountain, plateau);
	}

	/// <summary>顶到最近**汇聚**边界格的跳数：源 = 有汇聚角色的边界格（<paramref name="boundaryHops"/> 为 0）；
	/// 只在同板块内扩散（俯冲侧 / 跨板块都不会长山）；够不到 = −1。</summary>
	static int[] ConvergenceHops(Ball ball, int[] plateOfCell, BoundaryRole[] role, int[] boundaryHops)
	{
		int n = ball.CellDirs.Length;
		var neighbors = ball.CellNeighbors;
		var hops = new int[n];
		var queue = new Queue<int>(n);

		for (int i = 0; i < n; i++)
		{
			hops[i] = -1;
			if (role[i] == BoundaryRole.None) continue;
			if (boundaryHops[i] != 0) continue;
			hops[i] = 0;
			queue.Enqueue(i);
		}

		while (queue.Count > 0)                                   // 多源 BFS，先到者胜 ⇒ 确定性
		{
			int i = queue.Dequeue();
			foreach (int j in neighbors[i])
			{
				if (hops[j] >= 0) continue;
				if (role[j] == BoundaryRole.None) continue;
				if (plateOfCell[j] != plateOfCell[i]) continue;    // 不跨板块
				hops[j] = hops[i] + 1;
				queue.Enqueue(j);
			}
		}
		return hops;
	}

	/// <summary>
	/// 逐格场在**指定格集**上的分位阈值。格数超过 <see cref="QuantileSampleCap"/> 时隔格取样
	/// （只用来定阈值，不必全量排序）；排序在副本上做 ⇒ 与线程调度无关。
	/// </summary>
	/// <param name="n">格数。</param>
	/// <param name="field">逐格场。</param>
	/// <param name="keep">格集谓词（非该集的格不参与统计）。</param>
	/// <param name="quantiles">要取的分位（0~1），返回顺序一致。</param>
	static float[] Quantiles(int n, float[] field, Func<int, bool> keep, params double[] quantiles)
	{
		var result = new float[quantiles.Length];
		int count = 0;
		for (int i = 0; i < n; i++) if (keep(i)) count++;
		if (count == 0) return result;

		int stride = Math.Max(1, count / QuantileSampleCap);
		var samples = new List<float>(Math.Min(count, QuantileSampleCap) + 1);
		int seen = 0;
		for (int i = 0; i < n && samples.Count < QuantileSampleCap; i++)
		{
			if (!keep(i)) continue;
			if (seen++ % stride != 0) continue;
			samples.Add(field[i]);
		}
		samples.Sort();
		int m = samples.Count;
		for (int k = 0; k < quantiles.Length; k++)
		{
			int idx = (int)Math.Floor(quantiles[k] * m);
			if (idx < 0) idx = 0;
			if (idx >= m) idx = m - 1;
			result[k] = samples[idx];
		}
		return result;
	}

	/// <summary>高斯带：<c>exp(−((d−center)/w)²)</c>；<c>|t| &gt; 4</c> ⇒ 0（远场截断）。<c>w ≤ 0</c> ⇒ 0（关断）。</summary>
	static float Band(float d, float center, float w)
	{
		if (!(w > 0f)) return 0f;
		float t = (d - center) / w;
		if (t > 4f || t < -4f) return 0f;
		return MathF.Exp(-t * t);
	}

	/// <summary>平滑阶跃：<c>x ≤ a</c> ⇒ 0、<c>x ≥ b</c> ⇒ 1，中间二阶连续。<c>b ≤ a</c> ⇒ 硬阶跃。</summary>
	static float Smoothstep(float a, float b, float x)
	{
		if (!(b > a)) return x >= b ? 1f : 0f;
		float t = (x - a) / (b - a);
		if (t <= 0f) return 0f;
		if (t >= 1f) return 1f;
		return t * t * (3f - 2f * t);
	}
}
