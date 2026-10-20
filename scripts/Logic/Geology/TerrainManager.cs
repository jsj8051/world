using System;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>
/// ① 地形装配：逐格最终海拔 = （陆壳：陆面基底 + <see cref="HighlandOps"/> 的山带 / 老山丘陵 / 高原底座，
/// 经 <see cref="RemapOps"/> 重映射；或洋壳：海底，见 <see cref="OceanDepthOps"/>）× <see cref="TerrainSpec.VerticalScale"/>。
/// 陆面基线穿过海面 ⇒ 海岸线就是最终高度场穿过海面的结果，没有独立的造陆 / 判陆步骤。
/// 本类实际做：铺陆面基底（三档变化场 + <see cref="DomainWarpOps"/> 域扭曲，让海岸不再沿 Voronoi 边界走直线）、
/// 算陆壳 / 露出水面掩码、串联两套算子、分布重映射、合成 <see cref="HeightM"/>；
/// <see cref="ErosionOps.IncisePlains"/> 在最后切平原浅槽，只切最终陆面且封顶 h − sea − 1 ⇒ 不造海、不后退海岸。
/// 物理旋钮都在 <see cref="TerrainSpec"/>；采样下限 / 域扭曲倍频层数是实现常量。
/// 契约：<c>FinalLand = 高度 &gt; SeaLevelM</c> 是世界事实的唯一口径；陆壳高地的加性抬升恒 ≥ 0，
/// 洋壳海底恒 ≤ <see cref="TerrainSpec.OceanShallowestM"/>（只有岛弧成岛项能抬出水面）。
/// </summary>
public sealed class TerrainManager
{
	/// <summary>逐格最终海拔（米；已乘 <see cref="TerrainSpec.VerticalScale"/>；渲染 / 信息卡 / 下游阶段的唯一来源）。</summary>
	public float[] HeightM { get; private set; } = Array.Empty<float>();

	/// <summary>
	/// 逐格合成最终海拔：陆壳取陆面基底 + 高地，洋壳取海底 + 岛弧成岛。
	/// 逐格并行只读共享态、各写本格 ⇒ 与线程调度无关，逐位确定性保持。
	/// </summary>
	/// <param name="plateOfCell">上游板块阶段的逐格板块号。</param>
	/// <param name="plates">上游板块阶段的板块表（壳型 / 欧拉矢量 / 密度）。</param>
	/// <param name="landBaseDropM">上游 ⓪ 给出的陆面基线偏移（米；群岛档压基线，其余 0）。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell, Plate[] plates,
		float landBaseDropM)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		int n = ball.CellDirs.Length;
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));

		var scale = SpatialScale.Of(ball);
		double minWavelengthKm = 3.0 * scale.NeighborCenterKm;
		var large = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Large), spec.LargeWavelengthKm,
			NoiseSampling.ClampOctaves(spec.LargeOctaves, spec.LargeWavelengthKm, minWavelengthKm));
		var medium = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Medium), spec.MediumWavelengthKm,
			NoiseSampling.ClampOctaves(spec.MediumOctaves, spec.MediumWavelengthKm, minWavelengthKm));
		var detail = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Detail), spec.RegionalWavelengthKm,
			NoiseSampling.ClampOctaves(spec.RegionalOctaves, spec.RegionalWavelengthKm, minWavelengthKm));

		// 域扭曲：陆面大尺度 + 近岸细节各一层；两层都用 2 个倍频，最细层仍 ≥ 3 倍邻格中心距（采样下限）。
		var landWarp = DomainWarpOps.Create(seed,
			SeedDerivation.Terrain_LandWarpX, SeedDerivation.Terrain_LandWarpY, SeedDerivation.Terrain_LandWarpZ,
			spec.LandWarpKm, spec.LandWarpWavelengthKm,
			NoiseSampling.ClampOctaves(2, spec.LandWarpWavelengthKm, minWavelengthKm));
		var coastWarp = DomainWarpOps.Create(seed,
			SeedDerivation.Terrain_CoastWarpX, SeedDerivation.Terrain_CoastWarpY, SeedDerivation.Terrain_CoastWarpZ,
			spec.CoastWarpKm, spec.CoastWarpWavelengthKm,
			NoiseSampling.ClampOctaves(2, spec.CoastWarpWavelengthKm, minWavelengthKm));

		var role = PlateBoundaryOps.BoundaryRoles(ball, plateOfCell, plates);
		var motion = PlateBoundaryOps.BoundaryMotion(ball, plateOfCell, plates);

		// 陆壳掩码：决定基底铺陆面还是海底，不决定海陆——海陆由最终高度定。
		var continental = new bool[n];
		for (int i = 0; i < n; i++)
			continental[i] = plates[plateOfCell[i]].Crust == CrustType.Continental;

		float seaM = spec.SeaLevelM;
		var dirs = ball.CellDirs;

		// ⓪ 布局档已折成一个数：群岛档压低陆面基线 ⇒ 陆壳大半变浅海，只有高处以岛链露出。
		float baseM = spec.LandBaseM - landBaseDropM;

		// 陆面基底（常量基线 + 三档变化场）只对陆壳格算。采样方向先被陆面域扭曲推开，
		// 近岸再按 |高度 − 海面| 叠一层细节扭曲。landDir 留给 HighlandOps 判"多深入大陆"，
		// 必须与陆面基底同一坐标。
		var landDir = new Vector3[n];
		var landHeight = new float[n];
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			Vector3 q = landWarp.Apply(dirs[i]);
			float h = SampleLand(spec, baseM, large, medium, detail, q);
			if (spec.CoastWarpKm > 0f)
			{
				float nearCoast = 1f - MathOps.Smoothstep(
					spec.CoastWarpBandLowM, spec.CoastWarpBandHighM, MathF.Abs(h - seaM));
				if (nearCoast > 0f)
				{
					q = q.Lerp(coastWarp.Apply(q), nearCoast).Normalized();
					h = SampleLand(spec, baseM, large, medium, detail, q);
				}
			}
			landDir[i] = q;
			landHeight[i] = h;
		});

		var highland = HighlandOps.Build(ball, seed, spec, plateOfCell, continental, landHeight, motion, role,
			large, landDir);

		// 陆面合成 + 分布重映射：重映射只作用于海面以上、保号单调 ⇒ 海岸线不动。
		var landComposite = new float[n];
		Parallel.For(0, n, i =>
		{
			if (continental[i]) landComposite[i] = landHeight[i] + highland.MountainM[i] + highland.PlateauM[i];
		});
		RemapOps.ApplyLand(ball, spec, landComposite, continental);

		// 露出水面的格 = 陆壳且合成高度 &gt; 海面；它是海底「离岸距离」与大陆架的源。
		var aboveSea = new bool[n];
		Parallel.For(0, n, i => aboveSea[i] = continental[i] && landComposite[i] > seaM);

		var ocean = OceanDepthOps.Build(ball, seed, spec, plateOfCell, continental, aboveSea, motion, role);

		// 加性抬升恒 ≥ 0 ⇒ 不会把格压到海面之下；海岸线由合成高度穿过海面自然形成，
		// 海陆此后一律由高度定（<c>FinalLand = 高度 &gt; SeaLevelM</c>）。
		var h = new float[n];
		Parallel.For(0, n, i =>
		{
			h[i] = continental[i]
				? landComposite[i]
				: ocean.DepthM[i] + ocean.ArcIslandM[i];
		});

		// 平原浅下切：只在最终陆面、离海 ≥2 格处切浅槽（封顶 h − sea − 1 ⇒ 不造海、不后退海岸）。
		ErosionOps.IncisePlains(ball, spec, h, continental);

		// 垂直倍率：只放大高度，不改水平几何；海陆符号（海岸线）逐格不变。
		float verticalScale = spec.VerticalScale;
		if (!(verticalScale > 0f)) verticalScale = 1f;
		if (verticalScale != 1f)
			for (int i = 0; i < n; i++) h[i] *= verticalScale;

		HeightM = h;
	}

	/// <summary>陆面基底 = 基线 + 三档幅值 × 各自变化场；不夹保底——基线可落到海面之下，
	/// 海岸线才能由最终高度穿过海面自然形成。</summary>
	/// <param name="baseM">陆面基线（米；已按陆块布局档调整过）。</param>
	static float SampleLand(TerrainSpec spec, float baseM, SphericalFbmNoise large, SphericalFbmNoise medium,
		SphericalFbmNoise detail, Vector3 dir) =>
			baseM
			+ spec.LargeVariationM * large.Sample(dir)
			+ spec.MediumVariationM * medium.Sample(dir)
			+ spec.RegionalNoiseM * detail.Sample(dir);
}
