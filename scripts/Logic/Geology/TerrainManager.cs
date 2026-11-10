using System;
using System.Threading.Tasks;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// ① 地形装配：陆面基底（<see cref="LandFieldOps"/>）→ 高地（<see cref="HighlandOps"/>）→ 重映射（<see cref="RemapOps"/>）
/// → 海底（<see cref="OceanDepthOps"/>）→ 平原下切（<see cref="ErosionOps.IncisePlains"/>）→ 垂直倍率
/// → 世界事实（<see cref="TerrainFactsOps"/>）。
/// 逐格最终海拔 = （陆壳：陆面基底 + 山带 / 老山丘陵 / 高原底座，经重映射；或洋壳：海底）× <see cref="TerrainSpec.VerticalScale"/>。
/// 陆面基线穿过海面 ⇒ 海岸线就是最终高度场穿过海面的结果，没有独立的造陆 / 判陆步骤。
/// 契约：<c>FinalLand = 高度 &gt; SeaLevelM</c>；陆壳高地的加性抬升恒 ≥ 0，
/// 洋壳海底恒 ≤ <see cref="TerrainSpec.OceanShallowestM"/>（只有岛弧成岛项能抬出水面）。
/// </summary>
public sealed class TerrainManager
{
	/// <summary>逐格最终海拔（米；已乘 <see cref="TerrainSpec.VerticalScale"/>；渲染 / 信息卡 / 下游阶段的唯一来源）。</summary>
	public float[] HeightM { get; private set; } = Array.Empty<float>();

	/// <summary>最终陆海掩码（世界事实；与渲染高度逐格一致）。</summary>
	public bool[] FinalLand { get; private set; } = Array.Empty<bool>();
	/// <summary>陆占比 ∈[0,1]（格 + 6 邻居 FinalLand 占比；连续辅助数据）。</summary>
	public float[] FinalLandFraction { get; private set; } = Array.Empty<float>();
	/// <summary>陆块归属（FinalLand 连通分量；海格 = −1）。</summary>
	public int[] FinalLandmassId { get; private set; } = Array.Empty<int>();
	public int FinalLandmassCount { get; private set; }
	/// <summary>陆格离海跳数（海格 = 0）。</summary>
	public int[] FinalDistToCoast { get; private set; } = Array.Empty<int>();
	/// <summary>海格离岸跳数（陆格 = 0）。</summary>
	public int[] FinalDistToLand { get; private set; } = Array.Empty<int>();

	/// <summary>本阶段的构造影响场（地形与地壳图共用同一份）。</summary>
	public TectonicInfluenceSet Tectonic { get; private set; } = null;

	/// <summary>逐格合成最终海拔：陆壳取陆面基底 + 高地，洋壳取海底 + 岛弧成岛。</summary>
	/// <param name="plateOfCell">上游板块阶段的逐格板块号。</param>
	/// <param name="plates">上游板块阶段的板块表（欧拉矢量 / 密度）。</param>
	/// <param name="crust">上游逐格壳型（陆壳铺陆面、洋壳铺海底）。</param>
	/// <param name="boundaries">上游 ⓪ 的边界集（构造影响场的距离源）。</param>
	/// <param name="boundary">上游 ⓪ 的边界场（逐边运动 / 类型 / 两侧角色；本阶段只读）。</param>
	/// <param name="landBaseDropM">上游 ⓪ 给出的陆面基线偏移（米；群岛档压基线，其余 0）。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell, Plate[] plates,
		CrustField crust, PlateBoundarySet boundaries, PlateBoundaryFields boundary, float landBaseDropM)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		if (crust == null) throw new ArgumentNullException(nameof(crust));
		if (boundaries == null) throw new ArgumentNullException(nameof(boundaries));
		if (boundary == null) throw new ArgumentNullException(nameof(boundary));
		int n = ball.CellDirs.Length;
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));

		if (crust.Type.Length != n)
			throw new ArgumentException($"壳型长度 {crust.Type.Length} ≠ 格数 {n}", nameof(crust));

		// 陆壳掩码：决定基底铺陆面还是海底，不决定海陆——海陆由最终高度定。
		var continental = ContinentalMask(crust);
		float baseM = spec.LandBaseM - landBaseDropM;   // 群岛档压低陆面基线

		TectonicInfluenceSet tectonic = TectonicInfluenceOps.Build(ball, boundaries, plates, crust, plateOfCell,
			boundary.EdgeMotion, TectonicInfluenceOps.WorldSearchRadiusKm);

		var land = LandFieldOps.Build(ball, seed, spec, continental, baseM);
		var highland = HighlandOps.Build(ball, seed, spec, continental,
			land.HeightM, land.Inland, boundaries, boundary, tectonic);

		// 陆面合成 + 分布重映射：重映射只作用于海面以上、保号单调 ⇒ 海岸线不动。
		var landComposite = new float[n];
		Parallel.For(0, n, i =>
		{
			if (continental[i])
				landComposite[i] = land.HeightM[i] + highland.MountainM[i]
					+ highland.PlateauM[i] - highland.SubsidenceM[i];
		});
		float[] height = ComposeSurface(ball, seed, spec, continental, landComposite, boundary, tectonic);
		SetSurface(ball, height, spec.SeaLevelM, tectonic);
	}

	/// <summary>零步只铺陆面基底、初始洋底和岸坡，构造增量由演化模型负责。</summary>
	internal static float[] BuildInitialHeight(Ball ball, WorldSpec spec, CrustField crust, float landBaseDropM)
	{
		TerrainSpec terrain = spec.Terrain;
		EvolutionSpec evolution = spec.Evolution;
		evolution.Validate();
		var continental = ContinentalMask(crust);
		var land = LandFieldOps.Build(ball, spec.Seed, terrain, continental, terrain.LandBaseM - landBaseDropM);
		// 演化的高程为最终米，海底算子在生成基准上计算。
		double initialAge = Math.Min(evolution.InitialOceanAgeMa, evolution.MaxCoolingAgeMa);
		float abyssDepth = (float)((evolution.NewOceanElevationM
			- evolution.CoolingDepthMPerSqrtMa * Math.Sqrt(initialAge)) / HeightScale(terrain));
		return ComposeSurface(ball, spec.Seed, terrain, continental, land.HeightM, PlateBoundaryFields.Empty,
			TectonicInfluenceSet.Empty, abyssDepth);
	}

	static bool[] ContinentalMask(CrustField crust)
	{
		var continental = new bool[crust.Type.Length];
		for (int i = 0; i < continental.Length; i++)
			continental[i] = crust.Type[i] == CrustType.Continental;
		return continental;
	}

	static float[] ComposeSurface(Ball ball, int seed, TerrainSpec spec, bool[] continental,
		float[] landComposite, PlateBoundaryFields boundary, TectonicInfluenceSet tectonic, float? abyssDepth = null)
	{
		int n = continental.Length;
		float seaM = spec.SeaLevelM;
		RemapOps.ApplyLand(ball, spec, landComposite, continental);

		// 露出水面的格 = 陆壳且合成高度 > 海面；它是海底「离岸距离」与大陆架的源。
		var aboveSea = new bool[n];
		Parallel.For(0, n, i => aboveSea[i] = continental[i] && landComposite[i] > seaM);

		var ocean = abyssDepth is float depth
			? OceanDepthOps.BuildBase(ball, seed, spec, continental, aboveSea, depth)
			: OceanDepthOps.Build(ball, seed, spec, continental, aboveSea, boundary, tectonic);

		// 加性抬升恒 ≥ 0 ⇒ 不会把格压到海面之下；海岸线由合成高度穿过海面自然形成。
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
		float verticalScale = HeightScale(spec);
		if (verticalScale != 1f)
			for (int i = 0; i < n; i++) h[i] *= verticalScale;

		return h;
	}

	static float HeightScale(TerrainSpec spec) => spec.VerticalScale > 0f ? spec.VerticalScale : 1f;

	/// <summary>
	/// 用已保存的最终事实装配地形（导入地图用）。只校验，不派生距离或陆块。
	/// </summary>
	internal void LoadSaved(float[] heightM, bool[] finalLand, float[] finalLandFraction,
		int[] finalLandmassId, int finalLandmassCount, int[] finalDistToCoast, int[] finalDistToLand,
		TectonicInfluenceSet tectonic)
	{
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));
		int n = heightM.Length;
		if (finalLand == null || finalLand.Length != n
			|| finalLandFraction == null || finalLandFraction.Length != n
			|| finalLandmassId == null || finalLandmassId.Length != n
			|| finalDistToCoast == null || finalDistToCoast.Length != n
			|| finalDistToLand == null || finalDistToLand.Length != n)
			throw new ArgumentException("导入地形的逐格数组长度不一致");
		HeightM = heightM;
		FinalLand = finalLand;
		FinalLandFraction = finalLandFraction;
		FinalLandmassId = finalLandmassId;
		FinalLandmassCount = finalLandmassCount;
		FinalDistToCoast = finalDistToCoast;
		FinalDistToLand = finalDistToLand;
		Tectonic = tectonic;
	}

	internal void SetSurface(Ball ball, float[] height, float seaLevelM, TectonicInfluenceSet tectonic)
	{
		TerrainFactsOps.Field facts = TerrainFactsOps.Build(ball, height, seaLevelM);
		HeightM = height;
		Tectonic = tectonic;
		FinalLand = facts.Land;
		FinalLandFraction = facts.LandFraction;
		FinalLandmassId = facts.LandmassId;
		FinalLandmassCount = facts.LandmassCount;
		FinalDistToCoast = facts.DistToCoast;
		FinalDistToLand = facts.DistToLand;
	}
}
