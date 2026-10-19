using System;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // TerrainSpec（本阶段配置）+ Plate / CrustType（板块输入）
using World.H3Grid;          // Ball（H3 球壳数据层）
using World.Utils;           // SphericalFbmNoise（三档变化场）

namespace World.Logic;

/// <summary>
/// ① 地形装配：逐格最终海拔 = **陆壳**（陆面基底 + <see cref="HighlandOps"/> 的山带 / 老山丘陵 / 高原底座）
/// 或 **洋壳**（海底，见 <see cref="OceanDepthOps"/>）。
/// 陆面基线**穿过海面** ⇒ **海岸线就是高度场穿过海面的结果**——不存在独立的"造陆 / 判陆"步骤。
/// 三档变化是"让水文活着"的最小代价——单流向汇流需要坡向，完全平的陆面流不动。
///
/// 山带的**算法**在 <see cref="HighlandOps"/>、海底的在 <see cref="OceanDepthOps"/>，
/// 本类只负责：铺陆面基底、串联两者、把结果合成 <see cref="HeightM"/>。
///
/// 幅度与尺度旋钮全在 <see cref="TerrainSpec"/>（本类不藏数）。出厂档：陆面基线 180 m + 三档幅值 900 / 300 / 120
/// ⇒ 合成 σ ≈ 277 m（大尺度项占 89% —— 海陆分异由它主导、海岸随大尺度场成片而非碎斑；
/// 中/细节项只负责在陆内造出丘陵与碎斑，把"几坨大色块"打散）
/// ⇒ 约 1/4 的陆壳落在海面之下（海湾 / 陆内海 / 海峡）；海底最深约 −10500 m（海沟）。
///
/// 契约（`FinalLand = 高度 &gt; SeaLevelM` 是世界事实的唯一口径）：
/// ① **海陆全部由最终高度定**（壳型只决定基底铺"陆面"还是"海底"）；
/// ② 陆壳的抬升恒 ≥ 0，洋壳的海底恒 ≤ `OceanShallowestM`（只靠岛弧成岛项才能抬出水面）。
/// </summary>
public sealed class TerrainManager
{
	/// <summary>逐格最终海拔（米；渲染 / 信息卡 / 下游阶段的唯一来源）。</summary>
	public float[] HeightM { get; private set; } = Array.Empty<float>();

	/// <summary>
	/// 逐格合成最终海拔：陆壳的格取陆面基底 + 高地（山带 / 老山丘陵 / 高原），洋壳的格取海底 + 岛弧成岛。
	/// 变化场与构造场都由种子 / 板块产物派生，构造一次、全场只读。
	/// 逐格并行：每格只读共享只读态、只写 <c>h[i]</c> ⇒ 与线程调度无关，逐位确定性保持。
	/// </summary>
	/// <param name="ball">球壳数据层。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">本阶段配置（海面标高 + 陆面幅度 + 三档场尺度 + 高地段 + 海底段）。</param>
	/// <param name="plateOfCell">上游板块阶段的逐格板块号。</param>
	/// <param name="plates">上游板块阶段的板块表（壳型 / 欧拉矢量 / 密度）。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell, Plate[] plates)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		int n = ball.CellDirs.Length;
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));

		var large = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Large), spec.LargeWavelengthKm, spec.LargeOctaves);
		var medium = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Medium), spec.MediumWavelengthKm, spec.MediumOctaves);
		var detail = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Detail), spec.RegionalWavelengthKm, spec.RegionalOctaves);

		var role = PlateBoundaryOps.BoundaryRoles(ball, plateOfCell, plates);
		var motion = PlateBoundaryOps.BoundaryMotion(ball, plateOfCell, plates);

		// 陆壳掩码：决定基底取陆面合成还是海底（**不决定海陆**——海陆由最终高度定）
		var continental = new bool[n];
		for (int i = 0; i < n; i++)
			continental[i] = plates[plateOfCell[i]].Crust == CrustType.Continental;

		float seaM = spec.SeaLevelM;
		var dirs = ball.CellDirs;

		// 陆面基底（常量基线 + 三档变化场）——只对陆壳的格算
		var landHeight = new float[n];
		Parallel.For(0, n, i =>
		{
			if (continental[i]) landHeight[i] = SampleLand(spec, large, medium, detail, dirs[i]);
		});

		// 构造高地：山带（按汇聚角色分档）+ 古老山系 / 丘陵 + 高原底座
		var highland = HighlandOps.Build(ball, seed, spec, plateOfCell, continental, landHeight, motion, role, large);

		// 露出水面的格 = 陆壳 且 基底 + 山带 + 高原 &gt; 海面 —— 海底的「离岸距离」与大陆架都以它为源
		var aboveSea = new bool[n];
		Parallel.For(0, n, i =>
			aboveSea[i] = continental[i] && landHeight[i] + highland.MountainM[i] + highland.PlateauM[i] > seaM);

		// 海底（洋壳）：洋中脊 / 深海平原 / 大陆架 / 大陆坡 / 海沟 / 岛弧
		var ocean = OceanDepthOps.Build(ball, seed, spec, plateOfCell, continental, aboveSea, motion, role);

		// 抬升恒 ≥ 0 ⇒ 不会把任何格压到海面之下。陆面基线穿过海面 ⇒ 海岸线在这里自然形成，
		// 海陆此后一律由高度定（`FinalLand = 高度 > SeaLevelM`）。
		var h = new float[n];
		Parallel.For(0, n, i =>
		{
			h[i] = continental[i]
				? landHeight[i] + highland.MountainM[i] + highland.PlateauM[i]
				: ocean.DepthM[i] + ocean.ArcIslandM[i];
		});
		HeightM = h;
	}

	/// <summary>陆面基底：常量基线 + 三档幅值 × 各自变化场。**不夹保底**——基线可落到海面之下，
	/// 海岸线正是这个场穿过海面的结果。</summary>
	/// <param name="spec">本阶段配置（基线 + 三档幅值）。</param>
	/// <param name="large">大尺度变化场。</param>
	/// <param name="medium">中尺度变化场。</param>
	/// <param name="detail">细节变化场。</param>
	/// <param name="dir">格心单位方向。</param>
	static float SampleLand(TerrainSpec spec, SphericalFbmNoise large, SphericalFbmNoise medium,
		SphericalFbmNoise detail, Vector3 dir) =>
			spec.LandBaseM
			+ spec.LargeVariationM * large.Sample(dir)
			+ spec.MediumVariationM * medium.Sample(dir)
			+ spec.RegionalNoiseM * detail.Sample(dir);
}
