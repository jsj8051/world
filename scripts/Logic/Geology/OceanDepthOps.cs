using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 海底深度算子：给洋壳格算海底深度（负值），另给岛弧成岛的附加高度；陆壳格不参与
/// （陆面由 <see cref="TerrainManager"/> 装配）。
/// 三条互补来源：洋中脊与岛弧吃 <see cref="PlateBoundaryOps"/> 的边界场，大陆架 / 大陆坡吃离岸距离。
/// 洋中脊按到大洋张裂边界的距离当年龄（深度 ∝ √年龄，脊顶约 −143 m、最老洋底约 −287 m，
/// 脊顶再按张裂强度隆起）；大陆架 / 大陆坡近岸 −1.6 → −9.6 m，出架后下到深海
/// （被动边缘宽、俯冲侧窄）；岛弧（<see cref="BoundaryRole.Arc"/>）在上盘洋壳的海沟后方窄带隆起，
/// 沿带噪声峰处再叠高度 ⇒ 抬过海面成一串岛。海沟在俯冲侧（<see cref="BoundaryRole.Slab"/>）、
/// 平滑之后才挖（否则会被抹平）。深度只在此处钳制：上界 <see cref="TerrainSpec.OceanShallowestM"/>、
/// 下界 <see cref="TerrainSpec.OceanDeepestM"/>。
/// </summary>
public static class OceanDepthOps
{
	// 海底噪声的波长 / 层数是纹理尺度（形状常量），不进 TerrainSpec——那里放能改画面的物理旋钮。
	const float SeaLargeWavelengthKm = 407f;
	const int SeaLargeOctaves = 5;
	const float SeaFineWavelengthKm = 91f;
	const int SeaFineOctaves = 4;      // 最细层 11.4 km ≈ 4.6 格（再细就压到采样极限，混叠成条纹）
	const float ShelfNoiseWavelengthKm = 509f;
	const int ShelfNoiseOctaves = 3;
	const float ArcBeadWavelengthKm = 40f;
	const float ArcIsleWavelengthKm = 104f;
	const int ArcIsleOctaves = 4;

	/// <summary>结果。<c>DepthM</c>：洋壳格的海底深度（恒 ≤ <see cref="TerrainSpec.OceanShallowestM"/>；陆壳格不用）；
	/// <c>ArcIslandM</c>：岛弧成岛的附加高度（≥ 0；其余格 = 0）。</summary>
	public readonly struct Field
	{
		public readonly float[] DepthM;
		public readonly float[] ArcIslandM;

		internal Field(float[] depthM, float[] arcIslandM)
		{
			DepthM = depthM;
			ArcIslandM = arcIslandM;
		}
	}

	/// <summary>构建海底场：只算洋壳格，陆壳格留 0。</summary>
	/// <param name="land">逐格是否露出水面（陆壳且合成高度 &gt; 海面）——离岸距离的源。</param>
	/// <param name="motion">逐格边界法向相对速率（&lt; 0 汇聚 / &gt; 0 张裂）。</param>
	/// <param name="role">逐格汇聚角色（见 <see cref="BoundaryRole"/>）。</param>
	public static Field Build(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell,
		bool[] continental, bool[] land, float[] motion, BoundaryRole[] role)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (continental == null) throw new ArgumentNullException(nameof(continental));
		if (land == null) throw new ArgumentNullException(nameof(land));
		if (motion == null) throw new ArgumentNullException(nameof(motion));
		if (role == null) throw new ArgumentNullException(nameof(role));

		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		var neighbors = ball.CellNeighbors;

		// ── 源集：活动边界格（|motion| 超过 ActiveMotion；阈值与山带共用）──
		var convSources = new List<int>();
		var divSources = new List<int>();
		for (int i = 0; i < n; i++)
		{
			int p = plateOfCell[i];
			int[] nb = neighbors[i];
			bool boundary = false;
			for (int k = 0; k < nb.Length; k++)
				if (plateOfCell[nb[k]] != p) { boundary = true; break; }
			if (!boundary) continue;

			float m = motion[i];
			if (m < -PlateBoundaryOps.ActiveMotion) convSources.Add(i);
			else if (m > PlateBoundaryOps.ActiveMotion) divSources.Add(i);
		}

		// ── 三个真实距离场（km）：到汇聚边界 / 到大洋张裂边界 / 到陆地 ──
		// 截断半径取该场最远的有效尺度（带外 / 年龄满值后不再改变结果），越远越省。
		var conv = DistanceFieldOps.Build(ball, convSources,
			i => MathF.Min(1f, -motion[i] / PlateBoundaryOps.MotionScale),
			4f * spec.ActiveMarginBandKm);

		// 洋中脊只在大洋里：大陆里的张裂是裂谷，不算洋中脊。
		var ridgeSources = new List<int>();
		foreach (int i in divSources)
			if (!continental[i]) ridgeSources.Add(i);
		var ridge = DistanceFieldOps.Build(ball, ridgeSources,
			i => MathF.Min(1f, motion[i] / PlateBoundaryOps.MotionScale),
			MathF.Max(spec.RidgeAgeSpanKm, 4f * spec.RidgeBandKm));

		var coastSources = new List<int>();
		for (int i = 0; i < n; i++)
			if (land[i]) coastSources.Add(i);
		var coast = DistanceFieldOps.Build(ball, coastSources, _ => 0f,
			MathF.Max(spec.ShelfMinWidthKm, spec.ShelfMaxWidthKm) + spec.SlopeSpanKm);

		// ── 噪声场 ──
		var seaLarge = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_Sea), SeaLargeWavelengthKm, SeaLargeOctaves);
		var seaFine = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_SeaFine), SeaFineWavelengthKm, SeaFineOctaves);
		var shelfNoise = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_Shelf), ShelfNoiseWavelengthKm, ShelfNoiseOctaves);
		var beadNoise = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_ArcBead), ArcBeadWavelengthKm, 1);
		var isleNoise = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Ocean_ArcIsle), ArcIsleWavelengthKm, ArcIsleOctaves);

		// ── 逐格海底剖面（只算洋壳；陆壳格保持 0）──
		var depth = new float[n];
		var arcIsland = new float[n];
		Parallel.For(0, n, i =>
		{
			if (continental[i]) return;
			Vector3 dir = dirs[i];

			// ① 洋壳年龄 → 深度剖面（∝ √年龄）＋ 脊顶隆起 ＋ 深海丘陵
			float rs = ridge.Strength[i];
			float age = MathF.Min(1f, ridge.DistKm[i] / spec.RidgeAgeSpanKm);
			float d = spec.RidgeCrestDepthM - spec.RidgeAgeDepthM * MathF.Sqrt(age)
				+ spec.RidgeCrestRiseM * rs * MathOps.GaussianBand(ridge.DistKm[i], spec.RidgeBandKm);
			d += spec.AbyssHillLargeM * seaLarge.Sample(dir)
				+ spec.AbyssHillFineM * seaFine.Sample(dir);

			// ② 大陆架 / 大陆坡：离岸一段浅，出架后顺着坡下到深海
			float active = conv.Strength[i] * MathOps.GaussianBand(conv.DistKm[i], spec.ActiveMarginBandKm);
			float shelfWidth = (spec.ShelfMinWidthKm
					+ (spec.ShelfMaxWidthKm - spec.ShelfMinWidthKm) * (1f - active)
						* (0.35f + 0.65f * MathOps.Smoothstep(-0.4f, 0.5f, shelfNoise.Sample(dir))))
				* (coast.DistKm[i] < spec.MarginWidenKm ? 1.3f : 0.7f);
			if (shelfWidth < 1f) shelfWidth = 1f;

			float dc = coast.DistKm[i];
			float shelf = dc < shelfWidth
				? spec.ShelfShallowestM + (spec.ShelfDeepestM - spec.ShelfShallowestM) * (dc / shelfWidth)
				: spec.ShelfDeepestM + (spec.SlopeBottomM - spec.ShelfDeepestM)
					* MathOps.Smoothstep(0f, spec.SlopeSpanKm, dc - shelfWidth);
			d = MathF.Max(d, shelf);

			// ③ 岛弧：上盘的洋壳在海沟后方一条窄带上隆起成海底脊
			float cs = conv.Strength[i];
			float cd = conv.DistKm[i];
			float arcBand = MathOps.GaussianBand(cd - spec.ArcOffsetKm, spec.ArcBandKm);
			bool overriding = role[i] == BoundaryRole.Arc;
			if (overriding)
				d = MathF.Max(d, spec.ArcCrestDepthM - spec.ArcRiseM * (1f - cs * arcBand));

			depth[i] = MathF.Min(spec.OceanShallowestM, d);

			// ④ 岛弧成岛：沿带的 bead 峰处再叠一层高度 ⇒ 抬过海面即成一串岛
			if (overriding)
			{
				float beads = MathOps.Smoothstep(-0.35f, 0.45f,
					beadNoise.Sample(dir) + 0.35f * isleNoise.Sample(dir));
				arcIsland[i] = MathF.Max(0f, spec.ArcIslandPeakM * cs * arcBand * beads);
			}
		});

		// ── 先平滑海底（削距离场折线），海沟在平滑之后才挖——窄而深，不被邻域均值抹平 ──
		FieldBlurOps.Blur(ball, depth, FieldBlurOps.NeighborMeanWeight, spec.OceanSmoothPasses);

		var trench = new float[n];
		Parallel.For(0, n, i =>
		{
			if (continental[i]) return;
			if (role[i] != BoundaryRole.Slab) return;
			trench[i] = (spec.TrenchDepthM + spec.TrenchConvDepthM * conv.Strength[i])
				* conv.Strength[i]
				* MathOps.GaussianBand(conv.DistKm[i], spec.TrenchBandKm)
				* MathOps.Smoothstep(spec.TrenchMinCoastKm, spec.TrenchFullCoastKm, coast.DistKm[i]);
		});
		FieldBlurOps.Blur(ball, trench, FieldBlurOps.NeighborMeanWeight, spec.TrenchSmoothPasses);

		for (int i = 0; i < n; i++)
		{
			if (continental[i]) continue;
			depth[i] = Math.Clamp(depth[i] - trench[i], spec.OceanDeepestM, spec.OceanShallowestM);
		}

		return new Field(depth, arcIsland);
	}

}
