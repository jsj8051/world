using System;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.H3Grid;          // Ball（H3 球壳数据层）
using World.Utils;

namespace World.Logic;

/// <summary>
/// 高度合成器：常量基线 + 三档变化场 → 最终地表（连续 `SampleLand` + 逐格 `HeightM`）。
/// 陆格 = `LandBaseM` + 三档变化；海格 = `FlatSeaFloorM`（0 m）。
/// 三档变化是"让水文活着"的最小代价——单流向汇流需要坡向，完全平的陆面流不动。
///
/// 幅度预算：基线 30 m + 三档幅值（45 / 27 / 15，均方根合成 ≈ 55 m，峰 ≈ 1.1×）
/// ⇒ 陆面约落在 [0, 90] m（下限由 `MinLandElevationM` 兜住）——这是待地形塑造的**低平台**，
/// 抬升（山脉/岛弧/洋中脊）与下陷（裂谷/海沟）都从这个底子往上叠。
///
/// 契约：
/// ① 海陆分支由**掩码**决定（陆格恒 &gt; 0 ⇒ 世界事实 `FinalLand = 高度 &gt; 0` 口径不变）；
/// ② 陆格海拔钳 ≥ `MinLandElevationM`（放置期陆地不因变化场淹死）。
/// </summary>
public sealed class HeightComposer
{
	public const float LandBaseM = 30f;         // 陆面常量基线（米）——低平台底子
	public const float RegionalNoiseM = 15f;    // H_detail：小尺度细节（米）
	public const float MediumVariationM = 27f;  // H_medium：丘陵/山谷起伏（300-900 km 波长）
	public const float LargeVariationM = 45f;   // H_large：大地形起伏（5000 km 波长）
	public const float MinLandElevationM = 5f;  // 陆格海拔保底（放置期陆地不因变化场淹死）
	/// <summary>海面/海底：**平坦 0 m**（海底地形已退役）。</summary>
	public const float FlatSeaFloorM = 0f;

	readonly SphericalFbmNoise _large, _medium, _detail;   // 三档 variation（连续场；种子派生同前）

	public HeightComposer(int seed)
	{
		_large = new SphericalFbmNoise(SeedDerivation.Derive(seed, SeedDerivation.Composer_Large), 5000f, 2);
		_medium = new SphericalFbmNoise(SeedDerivation.Derive(seed, SeedDerivation.Composer_Medium), 600f, 3);
		_detail = new SphericalFbmNoise(SeedDerivation.Derive(seed, SeedDerivation.Composer_Detail), 90f, 2);
	}

	/// <summary>最终逐格海拔（米；渲染/信息卡唯一来源）。</summary>
	public float[] HeightM { get; private set; } = Array.Empty<float>();

	/// <summary>陆面连续口：常量基线 + 三档变化 + MinLand 钳。</summary>
	public float SampleLand(Vector3 dir)
	{
		float h = LandBaseM
			+ LargeVariationM * _large.Sample(dir)
			+ MediumVariationM * _medium.Sample(dir)
			+ RegionalNoiseM * _detail.Sample(dir);
		return MathF.Max(h, MinLandElevationM);
	}

	/// <summary>
	/// **表现层单点口**（--cont 逐像素等）：单点海陆分支。世界逻辑（FinalGeography）
	/// **不走本口**——按格掩码分支（决策 07 v2：单点口径只属表现层，不进世界事实）。
	/// </summary>
	/// <param name="isLand">该点是否陆地——掩码是**逐格量**，连续点判据由调用方回读
	///   （<see cref="LandMaskOps.IsLandAt"/>）；合成器自己不持海陆口径。</param>
	public float SampleSurface(Vector3 dir, bool isLand) => isLand ? SampleLand(dir) : FlatSeaFloorM;

	public void Generate(Ball ball, bool[] placementLand)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (placementLand == null) throw new ArgumentNullException(nameof(placementLand));
		int n = ball.CellDirs.Length;
		if (placementLand.Length != n)
			throw new ArgumentException($"掩码长度 {placementLand.Length} ≠ 格数 {n}", nameof(placementLand));
		var dirs = ball.CellDirs;

		// ── 逐格：海格 = 平坦 0 m；陆格 = 常量 + 变化 ──
		// 逐格并行；每格只读共享只读态、只写 h[i] ⇒ 与线程调度无关，逐位确定性保持。
		var h = new float[n];
		Parallel.For(0, n, i =>
		{
			h[i] = placementLand[i] ? SampleLand(dirs[i]) : FlatSeaFloorM;
		});
		HeightM = h;
	}
}
