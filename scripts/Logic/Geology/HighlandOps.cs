using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 陆壳高地的构造算子：给陆壳格算 <c>MountainM</c>（山带 + 老山 + 丘陵）与 <c>PlateauM</c>
/// （高原底座）两样加性高度；洋壳格不参与，海底由 <see cref="OceanDepthOps"/> 定。
/// 山带按 <see cref="BoundaryRole"/> 分档（陆×陆两侧宽带高山、陆×洋陆侧退后窄链、俯冲侧低矮增生楔），
/// 陆×陆带宽随汇聚强度加宽、张裂带压低抬升；走向弯曲与 <see cref="SphericalFbmNoise.SampleRidged"/>
/// 山脊骨架只改形状，不改峰高目标。
/// 幅度走归一制：相对抬升的 P99.7 映射到 <see cref="TerrainSpec.MountainPeakM"/>、折点以上压峰；
/// 归一在平滑之后做，故削毛刺不改峰高目标。高原底座是独立一层且沿海岸收掉，末步交
/// <see cref="ErosionOps"/> 细化 + 虚拟侵蚀；本类只产构造高度，陆面噪声基底由
/// <see cref="TerrainManager.SampleLand"/> 给。
/// </summary>
public static class HighlandOps
{
	// 噪声尺度（形状常量）：物理旋钮进 TerrainSpec，纹理尺度留在这里（与 OceanDepthOps 同分工）。
	const float OldWavelengthKm = 430f;
	const int OldOctaves = 5;
	const float HillsWavelengthKm = 370f;
	const int HillsOctaves = 4;
	const float ReachWavelengthKm = 743f;
	const int ReachOctaves = 4;
	const float AlongWavelengthKm = 356f;
	const int AlongOctaves = 4;
	const float ShieldWavelengthKm = 679f;
	const int ShieldOctaves = 4;
	const float BeltWarpWavelengthKm = 110f;
	const int BeltWarpOctaves = 2;
	const float BranchWavelengthKm = 45f;
	const int BranchOctaves = 2;

	// 「冒头门」的分位（只在陆壳格上取）：低分位开始出现、高分位完全出现。
	// 用分位而非固定阈值 ⇒ 与噪声值域无关；覆盖率 ≈ 1 − 低分位（老山 ≈10%，丘陵 ≈30%）。
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

	/// <summary>分位抽样的样本上限（格集超过它就按步长抽样；只用于定阈值，不必全量排序）。</summary>
	const int QuantileSampleCap = 262144;

	/// <summary>结果。<c>MountainM</c>：山带 + 老山 + 丘陵（≥ 0）；<c>PlateauM</c>：高原底座（≥ 0）。
	/// 两场只在陆壳格上有意义；<c>MountainM</c> 经 <see cref="ErosionOps"/> 沉积后可能被相邻格搬入物质 ⇒ 只读陆壳格。</summary>
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

	/// <summary>构建陆壳高地场；洋壳格不参与。</summary>
	/// <param name="landHeight">逐格陆面基底（常量基线 + 三档变化场；非陆壳格不用）。</param>
	/// <param name="motion">逐格边界法向相对速率（&lt; 0 汇聚 / &gt; 0 张裂）。</param>
	/// <param name="large">大尺度陆海场——兼作「有多深入大陆」的判据（盾状台地用）。</param>
	/// <param name="landDir">逐格陆面采样方向（已过域扭曲，含近岸细节；与陆面基底同一坐标）。</param>
	public static Field Build(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell,
		bool[] continental, float[] landHeight, float[] motion, BoundaryRole[] role,
		SphericalFbmNoise large, Vector3[] landDir)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (continental == null) throw new ArgumentNullException(nameof(continental));
		if (landHeight == null) throw new ArgumentNullException(nameof(landHeight));
		if (motion == null) throw new ArgumentNullException(nameof(motion));
		if (role == null) throw new ArgumentNullException(nameof(role));
		if (large == null) throw new ArgumentNullException(nameof(large));
		if (landDir == null) throw new ArgumentNullException(nameof(landDir));

		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		float seaM = spec.SeaLevelM;

		// ── ① 边界格 + 到最近汇聚边界的真实距离 ──
		// 山带坐标必须是 km 连续量：跳数 × 常数只有整数档 ⇒ 高斯带被量化成等跳数的平行长条（山带成阶梯）。
		int[] boundaryHops = PlateBoundaryOps.BoundaryHops(ball, plateOfCell);
		float[] convKm = ConvergenceDistanceKm(ball, spec, plateOfCell, role, boundaryHops);

		// ── ② 张裂带：到最近活动张裂边界的真实距离 + 强度（⑧ 用来压低抬升）──
		var riftSources = new List<int>();
		for (int i = 0; i < n; i++)
			if (boundaryHops[i] == 0 && motion[i] > PlateBoundaryOps.ActiveMotion) riftSources.Add(i);
		var rift = DistanceFieldOps.Build(ball, riftSources,
			i => MathF.Min(1f, motion[i] / PlateBoundaryOps.MotionScale), 4f * spec.RiftBandKm);

		// ── ③ 噪声场 ──
		var ridge = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Ridge), spec.MountainRidgeWavelengthKm,
			NoiseSampling.ClampRidgedOctaves(spec.MountainRidgeOctaves, spec.MountainRidgeWavelengthKm,
				SpatialScale.Of(ball).NeighborCenterKm));
		var branch = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_RidgeBranch), BranchWavelengthKm,
			NoiseSampling.ClampRidgedOctaves(BranchOctaves, BranchWavelengthKm,
				SpatialScale.Of(ball).NeighborCenterKm));
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
		var beltWarp = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_BeltWarp), BeltWarpWavelengthKm,
			NoiseSampling.ClampOctaves(BeltWarpOctaves, BeltWarpWavelengthKm,
				3.0 * SpatialScale.Of(ball).NeighborCenterKm));

		// ── ④ 取原始噪声（冒头门要按各自的分布定阈值，故先存原始值、再折权重）──
		var tectonic = new float[n];   // 山带抬升（相对值，⑦ 才归一）
		var oldRaw = new float[n];     // 老山脊（ridged）
		var hillsRaw = new float[n];
		var alongRaw = new float[n];   // 高原沿走向
		var inlandRaw = new float[n];  // 大陆内部程度
		var shieldRaw = new float[n];  // 盾状台地
		var beltOffset = new float[n]; // 横向偏移（km）
		float beltWarpKm = Math.Clamp(spec.MountainBeltWarpKm, 0f, 15f);
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			Vector3 dir = dirs[i];
			oldRaw[i] = oldRange.SampleRidged(dir);
			hillsRaw[i] = hills.Sample(dir);
			alongRaw[i] = along.Sample(dir);
			inlandRaw[i] = large.Sample(landDir[i]);
			shieldRaw[i] = shield.Sample(dir);
			beltOffset[i] = beltWarpKm * beltWarp.Sample(dir);
		});

		float[] oldQ = Quantiles(n, oldRaw, i => continental[i], OldQtyLow, OldQtyHigh);
		float[] hillsQ = Quantiles(n, hillsRaw, i => continental[i], HillsQtyLow, HillsQtyHigh);
		float[] alongQ = Quantiles(n, alongRaw, i => continental[i], AlongQtyLow, AlongQtyHigh);
		float[] inlandQ = Quantiles(n, inlandRaw, i => continental[i], InlandQtyLow, InlandQtyHigh);
		float[] shieldQ = Quantiles(n, shieldRaw, i => continental[i], ShieldQtyLow, ShieldQtyHigh);

		// 就地把原始噪声折成权重场：本节之后这些数组（含 oldRaw / hillsRaw 别名）一律按权重读。
		var oldWeight = oldRaw;        // 老山门 × 丘陵门：这一带本来就有起伏
		var hillsWeight = hillsRaw;
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			float oldGate = MathOps.Smoothstep(oldQ[0], oldQ[1], oldRaw[i]);
			float hillsValue = hillsRaw[i];
			oldWeight[i] = oldGate * MathOps.Smoothstep(hillsQ[0], hillsQ[1], hillsValue + 0.3f);
			hillsWeight[i] = MathOps.Smoothstep(hillsQ[0], hillsQ[1], hillsValue);
			alongRaw[i] = MathOps.Smoothstep(alongQ[0], alongQ[1], alongRaw[i]);
			inlandRaw[i] = MathOps.Smoothstep(inlandQ[0], inlandQ[1], inlandRaw[i]);
			shieldRaw[i] = MathOps.Smoothstep(shieldQ[0], shieldQ[1], shieldRaw[i]);
		});

		// ── ⑤ 山带（相对值；⑦ 才归一）──
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			BoundaryRole r = role[i];
			if (r == BoundaryRole.None) return;
			float cdRaw = convKm[i];
			if (cdRaw < 0f) return;
			float cs = MathF.Min(1f, -motion[i] / PlateBoundaryOps.MotionScale);
			if (!(cs > 0f)) return;

			float asymmetry = Math.Clamp(spec.MountainAsymmetry, 0f, 0.45f);
			float skewPower = 1.6f - 0.4f * (asymmetry / 0.45f);

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

			// 山带走向弯曲：只偏移 Upper / Lower / Arc 的距离；Slab 仍用原距离。
			float cd = r == BoundaryRole.Slab ? cdRaw : MathF.Max(0f, cdRaw + beltOffset[i]);

			// 山脊骨架：ridged 的零等值面成脊，脊距 ≥3 格（再细就混叠）；次级脊（45 km / 2 层）
			// 把主脊打散成有分支的山系，并让带宽有鼓包 / 收窄。
			float ridgeFloor = Math.Clamp(spec.MountainRidgeFloor, 0f, 1f);
			float sharpness = Math.Clamp(spec.MountainRidgeSharpness, 1f, 3f);
			float skeleton = ridge.SampleRidged(dirs[i]);
			float branchSkel = branch.SampleRidged(dirs[i]);
			bandKm *= 0.85f + 0.30f * branchSkel;
			float mod = ridgeFloor
				+ (1f - ridgeFloor) * MathF.Pow(skeleton, sharpness) * (0.60f + 0.40f * branchSkel);
			tectonic[i] = coef * spec.MountainPeakM * cs
				* BandProfile(cd, centerKm, bandKm, asymmetry, skewPower) * mod;
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
			float cs = MathF.Min(1f, -motion[i] / PlateBoundaryOps.MotionScale);
			if (!(cs > 0f)) cs = 0f;
			float cd = convKm[i] < 0f
				? float.MaxValue
				: (r == BoundaryRole.Slab ? convKm[i] : MathF.Max(0f, convKm[i] + beltOffset[i]));

			float u = tectonic[i] * gain;
			if (u > knee) u = knee + (u - knee) * spec.MountainKneeCompress;
			u += spec.OldMountainHeightM * oldWeight[i]
				+ spec.HillsHeightM * hillsWeight[i];

			float suppress = 1f - spec.RiftSuppressStrength * rift.Strength[i]
				* MathOps.GaussianBand(rift.DistKm[i], 0f, spec.RiftBandKm);
			mountain[i] = u * suppress;

			// 高原底座：上盘（Upper / Arc）在汇聚够强时背后的一大片平坦高地
			float pb = 0f;
			if ((r == BoundaryRole.Upper || r == BoundaryRole.Arc) && cs > spec.PlateauConvMin)
			{
				float reachKm = (r == BoundaryRole.Upper ? spec.PlateauUpperReachKm : spec.PlateauArcReachKm)
					+ spec.PlateauReachSpreadKm * (0.5f + 0.5f * reach.Sample(dir));
				float inBand = MathOps.Smoothstep(0f, spec.PlateauInnerRampKm, cd)
					* (1f - MathOps.Smoothstep(reachKm, reachKm + spec.PlateauOuterRampKm, cd));
				pb = (r == BoundaryRole.Upper ? spec.PlateauUpperHeightM : spec.PlateauArcHeightM)
					* MathOps.Smoothstep(spec.PlateauConvMin, spec.PlateauConvFull, cs)
					* inBand * alongRaw[i];
			}
			pb += spec.PlateauShieldHeightM * shieldRaw[i] * inlandRaw[i];
			plateau[i] = pb * MathOps.Smoothstep(0f, spec.PlateauCoastFadeM, landHeight[i] - seaM);
		});

		// ── ⑨ 山体细化 + 虚拟侵蚀：光滑的一堵墙 → 脊谷相间的山系 ──
		mountain = ErosionOps.Build(ball, seed, spec, continental, mountain);

		FieldBlurOps.Blur(ball, plateau, FieldBlurOps.NeighborMeanWeight, spec.PlateauSmoothPasses);

		return new Field(mountain, plateau);
	}

	/// <summary>到最近汇聚边界的真实距离（km）：源 = 有汇聚角色且 <paramref name="boundaryHops"/> 为 0 的边界格，
	/// 只在同板块内扩散——山带不会跨板块铺到对侧；够不到 = −1。
	/// 搜索半径按本阶段最大的带宽 / 高原进深给，超出即视为够不到。</summary>
	static float[] ConvergenceDistanceKm(Ball ball, TerrainSpec spec, int[] plateOfCell,
		BoundaryRole[] role, int[] boundaryHops)
	{
		int n = ball.CellDirs.Length;
		var sources = new List<int>();
		for (int i = 0; i < n; i++)
			if (role[i] != BoundaryRole.None && boundaryHops[i] == 0) sources.Add(i);

		var result = new float[n];
		Array.Fill(result, -1f);
		if (sources.Count == 0) return result;

		// 搜索半径：山带 4 倍带宽（高斯带在 4 倍处截断）与高原最大进深取大者。
		double outerBand = 4.0 * (spec.MountainUpperBandKm + spec.MountainBandConvSpreadKm);
		outerBand = Math.Max(outerBand, spec.MountainArcInlandKm + 4.0 * spec.MountainArcBandKm);
		outerBand = Math.Max(outerBand, 4.0 * spec.MountainSlabBandKm);
		double reach = Math.Max(spec.PlateauUpperReachKm, spec.PlateauArcReachKm)
			+ spec.PlateauReachSpreadKm + spec.PlateauOuterRampKm;
		double capKm = Math.Max(outerBand, reach) + 1.0;

		var field = DistanceFieldOps.Build(ball, sources, _ => 1f, capKm,
			(i, j) => plateOfCell[j] == plateOfCell[i]);
		for (int i = 0; i < n; i++)
			if (field.Src[i] >= 0) result[i] = field.DistKm[i];
		return result;
	}

	/// <summary>逐格场在指定格集上的分位阈值。格集超过 <see cref="QuantileSampleCap"/> 时按
	/// <c>步长 = ceil(格集大小 / 上限)</c> 在全集上抽样（样本数 ≤ 上限），不做全量排序。</summary>
	static float[] Quantiles(int n, float[] field, Func<int, bool> keep, params double[] quantiles)
	{
		var result = new float[quantiles.Length];
		int count = 0;
		for (int i = 0; i < n; i++) if (keep(i)) count++;
		if (count == 0) return result;

		int stride = Math.Max(1, (count + QuantileSampleCap - 1) / QuantileSampleCap);
		var samples = new List<float>(Math.Min(count, QuantileSampleCap) + 1);
		int seen = 0;
		for (int i = 0; i < n && samples.Count < QuantileSampleCap; i++)
		{
			if (!keep(i)) continue;
			if (seen++ % stride != 0) continue;
			samples.Add(field[i]);
		}
		samples.Sort();
		for (int k = 0; k < quantiles.Length; k++)
			result[k] = MathOps.QuantileSorted(samples, quantiles[k]);
		return result;
	}

	/// <summary>山带剖面。<paramref name="center"/> &gt; 0（陆×洋山链）⇒ 双侧不同宽：海侧 <c>w(1−a)</c> 陡、
	/// 陆侧 <c>w(1+a)</c> 缓；<paramref name="center"/> = 0（陆×陆 / 增生楔，d 只有正值）⇒ 单侧偏斜
	/// <c>exp(−(d/w')^p)</c>，<paramref name="skewPower"/> ∈ [1.2,1.6] ⇒ 边界侧陡、向内陆拖长。</summary>
	static float BandProfile(float d, float center, float w, float asymmetry, float skewPower)
	{
		if (!(w > 0f)) return 0f;
		if (center > 0f)
		{
			float sideWidth = d < center ? w * (1f - asymmetry) : w * (1f + asymmetry);
			return MathOps.GaussianBand(d, center, sideWidth);
		}
		float width = w * (1f + asymmetry);
		if (!(width > 0f)) return 0f;
		float t = d / width;
		if (t > 4f) return 0f;
		return MathF.Exp(-MathF.Pow(t, skewPower));
	}

}
