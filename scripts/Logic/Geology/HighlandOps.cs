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
/// 幅度走归一制：相对抬升的 P99.7 映射到 <see cref="TerrainSpec.MountainPeakM"/>；
/// 归一在平滑之后做，故削毛刺不改峰高目标。高原底座是独立一层且沿海岸收掉，末步交
/// <see cref="ErosionOps"/> 细化 + 虚拟侵蚀；本类只产构造高度，陆面噪声基底与“多深入大陆”的
/// 大尺度值由 <see cref="LandFieldOps"/> 给。
/// </summary>
public static class HighlandOps
{
	// 噪声尺度（形状常量）：物理旋钮进 TerrainSpec，纹理尺度留在这里（与 OceanDepthOps 同分工）。
	const float OldWavelengthKm = 8091f;
	const int OldOctaves = 5;
	const float HillsWavelengthKm = 6962f;
	const int HillsOctaves = 4;
	const float ReachWavelengthKm = 13981f;
	const int ReachOctaves = 4;
	const float AlongWavelengthKm = 6699f;
	const int AlongOctaves = 4;
	const float ShieldWavelengthKm = 12776f;
	const int ShieldOctaves = 4;
	const float BeltWarpWavelengthKm = 2070f;
	const int BeltWarpOctaves = 2;
	const float BranchWavelengthKm = 847f;
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

	/// <summary>结果。<c>MountainM</c>：山带 + 老山 + 丘陵 + 裂谷肩（≥ 0）；<c>PlateauM</c>：高原底座（≥ 0）；
	/// <c>SubsidenceM</c>：裂谷轴部沉降（≥ 0，侵蚀后单独扣除）。三场只在陆壳格上有意义。</summary>
	public readonly struct Field
	{
		public readonly float[] MountainM;
		public readonly float[] PlateauM;
		public readonly float[] SubsidenceM;

		internal Field(float[] mountainM, float[] plateauM, float[] subsidenceM)
		{
			MountainM = mountainM;
			PlateauM = plateauM;
			SubsidenceM = subsidenceM;
		}
	}

	/// <summary>构建陆壳高地场；洋壳格不参与。</summary>
	/// <param name="landHeight">逐格陆面基底（常量基线 + 三档变化场；非陆壳格不用）。</param>
	/// <param name="inland">大尺度场在陆面采样方向上的值（盾状台地判“多深入大陆”用）。</param>
	/// <param name="boundaries">板块边界集（逐边两侧角色）。</param>
	/// <param name="boundary">板块边界场（逐边运动 / 逐边角色；只读）。</param>
	/// <param name="tectonic">逐通道构造影响场（与地图同一份）。</param>
	public static Field Build(Ball ball, int seed, TerrainSpec spec, bool[] continental,
		float[] landHeight, float[] inland, PlateBoundarySet boundaries,
		PlateBoundaryFields boundary, TectonicInfluenceSet tectonic)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (continental == null) throw new ArgumentNullException(nameof(continental));
		if (landHeight == null) throw new ArgumentNullException(nameof(landHeight));
		if (inland == null) throw new ArgumentNullException(nameof(inland));
		if (boundaries == null) throw new ArgumentNullException(nameof(boundaries));
		if (boundary == null) throw new ArgumentNullException(nameof(boundary));
		if (tectonic == null) throw new ArgumentNullException(nameof(tectonic));

		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		float seaM = spec.SeaLevelM;

		// ── ① 汇聚距离 / 强度 / 角色：全部由通道影响场现算，不再用 CellRole ──
		(float[] convKm, float[] convStrength, BoundaryRole[] role) =
			ConvergenceFields(n, boundaries, boundary, tectonic);

		// ── ② 张裂带：ContinentalRift 通道（⑧ 用来压低抬升）──
		TectonicInfluenceField rift = tectonic.Get(TectonicChannel.ContinentalRift);

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
		var uplift = new float[n];   // 山带抬升（相对值，⑦ 才归一）
		var oldRaw = new float[n];     // 老山脊（ridged）
		var hillsRaw = new float[n];
		var alongRaw = new float[n];   // 高原沿走向
		var inlandRaw = new float[n];  // 大陆内部程度
		var shieldRaw = new float[n];  // 盾状台地
		var beltOffset = new float[n]; // 横向偏移（km）
		float beltWarpKm = Math.Clamp(spec.MountainBeltWarpKm, 0f, 282f);
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			Vector3 dir = dirs[i];
			oldRaw[i] = oldRange.SampleRidged(dir);
			hillsRaw[i] = hills.Sample(dir);
			alongRaw[i] = along.Sample(dir);
			inlandRaw[i] = inland[i];
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
			float cs = convStrength[i];
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

			// 山脊骨架：ridged 的零等值面成脊，脊距 ≥3 格（再细就混叠）；次级脊（847 km / 2 层）
			// 把主脊打散成有分支的山系，并让带宽有鼓包 / 收窄。
			float ridgeFloor = Math.Clamp(spec.MountainRidgeFloor, 0f, 1f);
			float sharpness = Math.Clamp(spec.MountainRidgeSharpness, 1f, 3f);
			float skeleton = ridge.SampleRidged(dirs[i]);
			float branchSkel = branch.SampleRidged(dirs[i]);
			bandKm *= 0.85f + 0.30f * branchSkel;
			float mod = ridgeFloor
				+ (1f - ridgeFloor) * MathF.Pow(skeleton, sharpness) * (0.60f + 0.40f * branchSkel);
			uplift[i] = coef * spec.MountainPeakM * cs
				* BandProfile(cd, centerKm, bandKm, asymmetry, skewPower) * mod;
		});

		// ── ⑥ 平滑（归一之前 ⇒ 削毛刺不改峰高目标）──
		FieldBlurOps.Blur(ball, uplift, spec.MountainSmoothWeight, 1);

		// ── ⑦ 归一：第 99.7 百分位 → MountainPeakM ──
		float[] refQ = Quantiles(n, uplift, i => continental[i] && uplift[i] > 0f, 0.997);
		float gain = refQ[0] > 0f ? spec.MountainPeakM / refQ[0] : 0f;

		// ── ⑧ 合成：山带（已归一）+ 老山 + 丘陵，再被张裂带压低；高原底座另走一条 ──
		var mountain = new float[n];
		var plateau = new float[n];
		var subsidence = new float[n];
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			Vector3 dir = dirs[i];
			BoundaryRole r = role[i];
			float cs = convStrength[i];
			float cd = convKm[i] < 0f
				? float.MaxValue
				: (r == BoundaryRole.Slab ? convKm[i] : MathF.Max(0f, convKm[i] + beltOffset[i]));

			float u = uplift[i] * gain;
			u += spec.OldMountainHeightM * oldWeight[i]
				+ spec.HillsHeightM * hillsWeight[i];

			bool hasRift = rift.SourceIndex.Length == n && rift.SourceIndex[i] >= 0;
			float riftDist = hasRift ? rift.DistanceKm[i] : float.MaxValue;
			float riftStrength = hasRift
				? TectonicSourceOps.StrengthAt(rift, i, boundary.EdgeMotion)
				: 0f;
			float riftBand = hasRift ? MathOps.GaussianBand(riftDist, 0f, spec.RiftBandKm) : 0f;
			float riftCore = hasRift
				? MathOps.GaussianBand(riftDist, 0f, spec.RiftBandKm * 0.55f) : 0f;
			float riftShoulder = hasRift
				? MathOps.GaussianBand(riftDist, spec.RiftBandKm, spec.RiftBandKm * 0.6f) : 0f;
			float suppress = 1f - spec.RiftSuppressStrength * riftStrength * riftBand;
			mountain[i] = u * suppress + spec.RiftShoulderHeightM * riftStrength * riftShoulder;
			subsidence[i] = spec.RiftDepthM * riftStrength * riftCore;

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

		return new Field(mountain, plateau, subsidence);
	}

	/// <summary>从三个汇聚通道里取最近的一个，并由通道 + 逐边侧角色定角色；无通道 = None / −1 / 0。</summary>
	static (float[] km, float[] strength, BoundaryRole[] role) ConvergenceFields(int n,
		PlateBoundarySet boundaries, PlateBoundaryFields boundary, TectonicInfluenceSet tectonic)
	{
		var km = new float[n];
		Array.Fill(km, -1f);
		var strength = new float[n];
		var role = new BoundaryRole[n];
		TectonicChannel[] channels =
		{
			TectonicChannel.ContinentalCollision,
			TectonicChannel.ContinentalArc,
			TectonicChannel.Subduction,
		};

		for (int i = 0; i < n; i++)
		{
			float bestKm = float.MaxValue;
			float bestStrength = 0f;
			BoundaryRole bestRole = BoundaryRole.None;
			foreach (TectonicChannel channel in channels)
			{
				TectonicInfluenceField field = tectonic.Get(channel);
				if (field.SourceIndex.Length != n) continue;
				int s = field.SourceIndex[i];
				if (s < 0 || field.DistanceKm[i] >= bestKm) continue;

				TectonicSource source = field.Sources[s];
				PlateBoundaryEdge edge = boundaries.Edges[source.EdgeIndex];
				BoundarySideRole side = source.SideCell == edge.CellA
					? boundary.EdgeRoleA[source.EdgeIndex]
					: boundary.EdgeRoleB[source.EdgeIndex];
				bestRole = channel switch
				{
					TectonicChannel.ContinentalCollision =>
						side == BoundarySideRole.Underthrusting ? BoundaryRole.Lower : BoundaryRole.Upper,
					TectonicChannel.ContinentalArc => BoundaryRole.Arc,
					TectonicChannel.Subduction => BoundaryRole.Slab,
					_ => BoundaryRole.None,
				};
				bestKm = field.DistanceKm[i];
				bestStrength = TectonicSourceOps.StrengthAt(field, i, boundary.EdgeMotion);
			}
			role[i] = bestRole;
			if (bestRole == BoundaryRole.None) continue;
			km[i] = bestKm;
			strength[i] = bestStrength;
		}
		return (km, strength, role);
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
