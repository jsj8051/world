using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.H3Grid;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.WorldGen;

// 世界生成空间 · 高度合成器（架构 v1 冻结版，决策 07 数据语义链 + 08 契约）：
//   **SampleSurface(dir) = 最终地表的连续单一事实源**。
//   合成 = Base（区域基础高度）→ 按序 lerp 特征场列表（influence/target 语义）→ 三档变化 → 保底。
// ★特征优先级规则（设计约定）：列表**后位覆盖先位**——Mountain(4) > Basin(3) > Plateau(2) >
//   Base(0)。排序依据 = 空间局部性/强度（越局部越"尖锐"优先级越高）。**新特征插位准则**：
//   按局部性插入列表并在本表登记（Volcano 最局部 → Mountain 之后；Erosion 全局弱场 → Detail 段）。
//   插错位置的症状 = "A 一加 B 就变了"。影响度接近 1 时后位完全接管前位。
// ★契约（ArchitectureContractTests 钉死）：
//   ① 合成器**不依赖具体 Feature 类**——只消费 ITerrainField 列表（新特征零合成器改动）；
//   ② 陆/海作用域声明（TerrainDomain）：LandOnly 特征不影响海洋剖面（高原/盆地），
//      LandAndSea 特征跨海（山脉/火山——火山在海上即海山，物理成立）；
//   ③ 海侧 = Bathymetry + LandAndSea 特征 lerp（只向目标拉）；陆侧钳 ≥ MinLandElevationM。
// 1 pass 图上平滑 = 离散化后处理（抗混叠），仅放置期陆格参与——连续场本身不含。
/// <summary>特征作用域：陆海皆可（山脉/火山——跨海即海山/海底构造）或仅陆（高原/盆地）。</summary>
public enum TerrainDomain { LandAndSea, LandOnly }

/// <summary>特征场列表项：特征口 + 作用域（合成器按列表序 lerp，后位覆盖先位）。</summary>
public readonly record struct FeatureField(ITerrainField Field, TerrainDomain Domain);

/// <summary>
/// 高度合成器：特征目标 lerp 链 + 变化场 → 最终地表（连续 SampleSurface + 逐格 HeightM）。
/// </summary>
public sealed class HeightComposer
{
	public const float RegionalNoiseM = 80f;    // H_detail：小尺度细节（米）
	public const float MediumVariationM = 130f; // H_medium：丘陵/山谷起伏（300-900 km 波长）
	public const float LargeVariationM = 260f;  // H_large：大地形起伏（5000 km 波长；尺度纪律：large > medium > detail）
	public const float SmoothSelf = 0.6f;      // 平滑自权重（1 pass；0.4 给邻居均值）
	public const float MinLandElevationM = 30f; // 陆格海拔保底（放置期陆地不因变化场淹死）

	readonly int _seed;
	readonly SphericalFbmNoise _large, _medium, _detail;   // 三档 variation（连续场；种子派生同前）

	public HeightComposer(int seed)
	{
		_seed = seed;
		_large = new SphericalFbmNoise(SeedDerivation.Derive(seed, SeedDerivation.Composer_Large), 5000f, 2);
		_medium = new SphericalFbmNoise(SeedDerivation.Derive(seed, SeedDerivation.Composer_Medium), 600f, 3);
		_detail = new SphericalFbmNoise(SeedDerivation.Derive(seed, SeedDerivation.Composer_Detail), 90f, 2);
	}

	static float Lerp(float a, float b, float t) => a + (b - a) * t;

	/// <summary>最终逐格海拔（米；SampleSurface 的 H3 采样 + 图上平滑；渲染/信息卡唯一来源）。</summary>
	public float[] HeightM { get; private set; } = Array.Empty<float>();

	// ── 分支口（唯一实现；SampleSurface 表现层与 FinalGeography 世界事实层共用）──

	/// <summary>陆分支：基座 + 特征 lerp 链 + 三档变化 + MinLand 钳。</summary>
	public float SampleLandBranch(Vector3 dir, GeologicalRegions regions, IReadOnlyList<FeatureField> features)
	{
		float h = regions.BaseElevationAt(dir, regions.RegionIndexAt(dir));
		for (int i = 0; i < features.Count; i++)
		{
			var (inf, tgt) = features[i].Field.SampleAt(dir);
			h = Lerp(h, tgt, inf);
		}
		h += LargeVariationM * _large.Sample(dir)
			+ MediumVariationM * _medium.Sample(dir)
			+ RegionalNoiseM * _detail.Sample(dir);
		return MathF.Max(h, MinLandElevationM);
	}

	/// <summary>海分支：深海剖面 + LandAndSea 特征目标 lerp（海底脊/海山/岛链）。</summary>
	public float SampleSeaBranch(Vector3 dir, IReadOnlyList<FeatureField> features)
	{
		float sea = surface_BathymetryFallback(dir);
		for (int i = 0; i < features.Count; i++)
		{
			if (features[i].Domain != TerrainDomain.LandAndSea) continue;
			var (inf, tgt) = features[i].Field.SampleAt(dir);
			sea = Lerp(sea, tgt, inf);
		}
		return sea;
	}

	private Func<Vector3, float> surface_BathymetryFallback = _ => 0f;   // 由 resolver 注入（见 SetBathymetry）

	/// <summary>注入深海剖面函数（Generate 前由宿主调用；分支口无 resolver 依赖）。</summary>
	public void SetBathymetry(Func<Vector3, float> bathymetry) => surface_BathymetryFallback = bathymetry;

	/// <summary>
	/// **表现层单点口**（--cont 逐像素等）：单点海陆分支。世界逻辑（FinalGeography）
	/// **不走本口**——按格掩码分支调 SampleLandBranch/SampleSeaBranch（决策 07 v2：
	/// 单点口径只属表现层，不进世界事实）。
	/// </summary>
	public float SampleSurface(Vector3 dir, GeologicalRegions regions, SurfaceResolver surface,
		IReadOnlyList<FeatureField> features)
	{
		return surface.IsPlacementLand(dir)
			? SampleLandBranch(dir, regions, features)
			: SampleSeaBranch(dir, features);
	}

	public void Generate(Ball ball, SurfaceResolver surface, GeologicalRegions regions,
		IReadOnlyList<FeatureField> features)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (surface == null) throw new ArgumentNullException(nameof(surface));
		if (regions == null) throw new ArgumentNullException(nameof(regions));
		if (features == null) throw new ArgumentNullException(nameof(features));
		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		SetBathymetry(surface.BathymetryAt);

		// ── ① 逐格最终高度：分支判据 = **放置区域掩码**（多点口径，与区域/选址同源——
		//    单点口径在海岸与掩码不一致会造成陆格海值/海格陆值，决策 07 步骤③的教训）。
		//    SampleSurface 保留连续口径供表现层（--cont 逐像素）——两口径的差异仅限
		//    掩码边缘格，且各自内部自洽。
		//    ★逐格并行（2026-10-07 启动优化②）：每格只读共享只读态（区域表/特征场/纯噪声）、
		//    只写 h[i] ⇒ 结果与线程调度无关（逐位确定性保持）。基座帽表先预构建（防懒加载竞态）。
		regions.EnsureBaseCaps();
		var h = new float[n];
		Parallel.For(0, n, i =>
		{
			if (regions.RegionOfCell[i] >= 0)
				h[i] = SampleSurface(dirs[i], regions, surface, features);
			else
			{
				h[i] = SampleSeaBranch(dirs[i], features);
			}
		});

		// ── ② 1 pass 图上平滑（离散化后处理/抗混叠；仅放置期陆格互为邻居——海陆边界不模糊）──
		//    ★同样逐格并行：只读 h[]，只写 smoothed[i]。
		var neighbors = ball.CellNeighbors;
		var smoothed = new float[n];
		Array.Copy(h, smoothed, n);
		Parallel.For(0, n, i =>
		{
			if (regions.RegionOfCell[i] < 0) return;
			float sum = 0f; int cnt = 0;
			foreach (int j in neighbors[i])
			{
				if (regions.RegionOfCell[j] < 0) continue;
				sum += h[j]; cnt++;
			}
			if (cnt > 0) smoothed[i] = SmoothSelf * h[i] + (1f - SmoothSelf) * (sum / cnt);
			if (smoothed[i] < MinLandElevationM) smoothed[i] = MinLandElevationM;
		});

		HeightM = smoothed;
	}
}
