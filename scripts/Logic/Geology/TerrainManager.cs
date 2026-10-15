using System;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // TerrainSpec（本阶段配置）
using World.H3Grid;          // Ball（H3 球壳数据层）
using World.Utils;           // SphericalFbmNoise（三档变化场）

namespace World.Logic;

/// <summary>
/// ① 地形实现：高度合成 = 常量基线 + 三档变化场 → 逐格最终海拔。
/// 陆格 = 基线 + 三档幅值 × 各自变化场；海格 = `SeaLevelM`（一档平地，海底地形已退役）。
/// 三档变化是"让水文活着"的最小代价——单流向汇流需要坡向，完全平的陆面流不动。
///
/// 幅度与尺度旋钮全在 <see cref="TerrainSpec"/>（本类不藏数）。出厂档：基线 30 m + 三档幅值 45 / 27 / 15
/// ⇒ 均方根合成 ≈ 55 m（峰 ≈ 1.1×）⇒ 陆面约落在 [0, 90] m——这是待地形塑造的**低平台**，
/// 抬升（山脉 / 岛弧 / 洋中脊）与下陷（裂谷 / 海沟）都从这个底子往上叠。
///
/// 契约（世界事实 `FinalLand = 高度 &gt; SeaLevelM` 的口径不变）：
/// ① 海陆分支由**掩码**决定（陆格恒 &gt; 海面标高）；
/// ② 陆格海拔钳 ≥ `MinLandElevationM`（放置期陆地不因变化场淹死）。
/// </summary>
public sealed class TerrainManager
{
	/// <summary>逐格最终海拔（米；渲染 / 信息卡 / 下游阶段的唯一来源）。</summary>
	public float[] HeightM { get; private set; } = Array.Empty<float>();

	/// <summary>
	/// 逐格合成最终海拔。三档变化场由种子派生三条独立流，构造一次、全场只读。
	/// 逐格并行：每格只读共享只读态、只写 <c>h[i]</c> ⇒ 与线程调度无关，逐位确定性保持。
	/// </summary>
	/// <param name="ball">球壳数据层。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">本阶段配置（海面标高 + 陆面幅度 + 三档波长 / 层数）。</param>
	/// <param name="placementLand">上游板块阶段的陆地掩码。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, bool[] placementLand)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (placementLand == null) throw new ArgumentNullException(nameof(placementLand));
		int n = ball.CellDirs.Length;
		if (placementLand.Length != n)
			throw new ArgumentException($"掩码长度 {placementLand.Length} ≠ 格数 {n}", nameof(placementLand));

		// 陆格保底必须高过海面，否则"陆"也会被判成海（FinalLand = 高度 > SeaLevelM）
		if (!(spec.MinLandElevationM > spec.SeaLevelM))
			throw new ArgumentException(
				$"陆格保底 {spec.MinLandElevationM} m 须 > 海面标高 {spec.SeaLevelM} m" +
				"（否则放置期陆地一律被判成海）", nameof(spec));

		var large = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Large), spec.LargeWavelengthKm, spec.LargeOctaves);
		var medium = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Medium), spec.MediumWavelengthKm, spec.MediumOctaves);
		var detail = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Detail), spec.RegionalWavelengthKm, spec.RegionalOctaves);

		// 幅度预先取出（逐格循环里不再读属性）
		float baseM = spec.LandBaseM;
		float largeAmp = spec.LargeVariationM;
		float mediumAmp = spec.MediumVariationM;
		float detailAmp = spec.RegionalNoiseM;
		float minLandM = spec.MinLandElevationM;
		float seaM = spec.SeaLevelM;

		// 陆面连续口：常量基线 + 三档变化 + MinLand 钳
		float SampleLand(Vector3 dir) => MathF.Max(
			baseM
			+ largeAmp * large.Sample(dir)
			+ mediumAmp * medium.Sample(dir)
			+ detailAmp * detail.Sample(dir),
			minLandM);

		var dirs = ball.CellDirs;
		var h = new float[n];
		Parallel.For(0, n, i =>
		{
			h[i] = placementLand[i] ? SampleLand(dirs[i]) : seaM;
		});
		HeightM = h;
	}
}
