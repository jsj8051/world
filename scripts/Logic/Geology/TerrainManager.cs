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

	/// <summary>逐格合成最终海拔：陆壳取陆面基底 + 高地，洋壳取海底 + 岛弧成岛。</summary>
	/// <param name="plateOfCell">上游板块阶段的逐格板块号。</param>
	/// <param name="plates">上游板块阶段的板块表（壳型 / 欧拉矢量 / 密度）。</param>
	/// <param name="boundary">上游 ⓪ 的边界场（逐格运动 / 角色 / 跳数；本阶段只读）。</param>
	/// <param name="landBaseDropM">上游 ⓪ 给出的陆面基线偏移（米；群岛档压基线，其余 0）。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell, Plate[] plates,
		PlateBoundaryFields boundary, float landBaseDropM)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		if (boundary == null) throw new ArgumentNullException(nameof(boundary));
		int n = ball.CellDirs.Length;
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));

		// 陆壳掩码：决定基底铺陆面还是海底，不决定海陆——海陆由最终高度定。
		var continental = new bool[n];
		for (int i = 0; i < n; i++)
			continental[i] = plates[plateOfCell[i]].Crust == CrustType.Continental;

		float seaM = spec.SeaLevelM;
		float baseM = spec.LandBaseM - landBaseDropM;   // 群岛档压低陆面基线

		var land = LandFieldOps.Build(ball, seed, spec, continental, baseM);
		var highland = HighlandOps.Build(ball, seed, spec, plateOfCell, continental,
			land.HeightM, land.Inland, boundary);

		// 陆面合成 + 分布重映射：重映射只作用于海面以上、保号单调 ⇒ 海岸线不动。
		var landComposite = new float[n];
		Parallel.For(0, n, i =>
		{
			if (continental[i])
				landComposite[i] = land.HeightM[i] + highland.MountainM[i] + highland.PlateauM[i];
		});
		RemapOps.ApplyLand(ball, spec, landComposite, continental);

		// 露出水面的格 = 陆壳且合成高度 > 海面；它是海底「离岸距离」与大陆架的源。
		var aboveSea = new bool[n];
		Parallel.For(0, n, i => aboveSea[i] = continental[i] && landComposite[i] > seaM);

		var ocean = OceanDepthOps.Build(ball, seed, spec, continental, aboveSea, boundary);

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
		float verticalScale = spec.VerticalScale;
		if (!(verticalScale > 0f)) verticalScale = 1f;
		if (verticalScale != 1f)
			for (int i = 0; i < n; i++) h[i] *= verticalScale;

		HeightM = h;

		TerrainFactsOps.Field facts = TerrainFactsOps.Build(ball, h, seaM);
		FinalLand = facts.Land;
		FinalLandFraction = facts.LandFraction;
		FinalLandmassId = facts.LandmassId;
		FinalLandmassCount = facts.LandmassCount;
		FinalDistToCoast = facts.DistToCoast;
		FinalDistToLand = facts.DistToLand;
	}
}
