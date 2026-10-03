using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 高度合成器（决策 07 数据语义链的 ③ FinalHeight + ④/⑤ 的采样源）：
//   **SampleSurface(dir) = 最终地表的连续单一事实源**——陆/海两分支、特征 lerp 链、
//   变化场、保底全在其中；逐格数组 = 它在格心的采样（构造上不打架）。
//   合成语义（决策 07 步骤⑤，目标绝对高度替代"+N 米增量"——峰高只有一个语义来源）：
//     h = BaseElevation（区域基础高度，背景）
//     h = lerp(h, PlateauTarget, PlateauInfluence)      ← 高原"这里倾向 2000m 台地"
//     h = lerp(h, BasinTarget,   BasinInfluence)        ← 盆地"这里倾向 350m 洼地"
//     h = lerp(h, MountainTarget, MountainInfluence)    ← 山脉"这里倾向 4200m×profile 山"
//     h += Large + Medium + Detail（三档变化；large > medium > detail）
//   陆格钳 ≥ MinLandElevationM（放置期陆地不因变化场淹死；内流洼地语义由 relief 承载）。
//   海侧 = Bathymetry（放置期深海剖面）+ 山脉目标 lerp（海底脊/岛链，海洋表达式在 resolver）。
// 1 pass 图上平滑 = 离散化后处理（抗混叠），仅放置期陆格参与——连续场本身不含。
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
		_large = new SphericalFbmNoise(seed ^ 0x1A6E, 5000f, 2);
		_medium = new SphericalFbmNoise(seed ^ 0x4E02, 600f, 3);
		_detail = new SphericalFbmNoise(seed ^ 0x4E01, 90f, 2);
	}

	/// <summary>
	static float Lerp(float a, float b, float t) => a + (b - a) * t;

	/// <summary>最终逐格海拔（米；SampleSurface 的 H3 采样 + 图上平滑；渲染/信息卡唯一来源）。</summary>
	public float[] HeightM { get; private set; } = Array.Empty<float>();

	/// **最终地表连续单一事实源**（决策 07：世界逻辑只从这里拿最终高度）。
	/// 表现层（--cont 等逐像素渲染）也读这里——不同分辨率、同一个世界真相。
	/// </summary>
	public float SampleSurface(Vector3 dir, GeologicalRegions regions, MountainSkeleton mountains,
		RegionalLandforms landforms, SurfaceResolver surface)
	{
		if (surface.IsPlacementLand(dir))
		{
			float h = regions.BaseElevationAt(dir, regions.RegionIndexAt(dir));
			var (pI, pT) = landforms.PlateauAt(dir);
			h = Lerp(h, pT, pI);
			var (bI, bT) = landforms.BasinAt(dir);
			h = Lerp(h, bT, bI);
			var (mI, mT) = mountains.InfluenceAt(dir);
			h = Lerp(h, mT, mI);
			h += LargeVariationM * _large.Sample(dir)
				+ MediumVariationM * _medium.Sample(dir)
				+ RegionalNoiseM * _detail.Sample(dir);
			return MathF.Max(h, MinLandElevationM);
		}
		// 海侧：放置期深海剖面 + 山脉目标 lerp（海底脊/岛链；海洋表达式在山脉口内）
		float sea = surface.BathymetryAt(dir);
		var (mi, mt) = mountains.InfluenceAt(dir);
		sea = Lerp(sea, mt, mi);
		return sea;
	}

	public void Generate(Ball ball, SurfaceResolver surface, GeologicalRegions regions,
		MountainSkeleton mountains, RegionalLandforms landforms)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (surface == null) throw new ArgumentNullException(nameof(surface));
		if (regions == null) throw new ArgumentNullException(nameof(regions));
		if (mountains == null) throw new ArgumentNullException(nameof(mountains));
		if (landforms == null) throw new ArgumentNullException(nameof(landforms));
		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;

		// ── ① 逐格最终高度：分支判据 = **放置区域掩码**（多点口径，与区域/选址同源——
		//    单点口径在海岸与掩码不一致会造成陆格海值/海格陆值，决策 07 步骤③的教训）。
		//    SampleSurface 保留连续口径供表现层（--cont 逐像素）——两口径的差异仅限
		//    掩码边缘格，且各自内部自洽。
		var h = new float[n];
		for (int i = 0; i < n; i++)
		{
			if (regions.RegionOfCell[i] >= 0)
				h[i] = SampleSurface(dirs[i], regions, mountains, landforms, surface);
			else
			{
				float sea = surface.BathymetryAt(dirs[i]);
				var (mi, mt) = mountains.InfluenceAt(dirs[i]);
				h[i] = MathF.Max(Lerp(sea, mt, mi), sea);   // 海侧只向目标拉（不沉底）
			}
		}

		// ── ② 1 pass 图上平滑（离散化后处理/抗混叠；仅放置期陆格互为邻居——海陆边界不模糊）──
		var neighbors = ball.CellNeighbors;
		var smoothed = new float[n];
		Array.Copy(h, smoothed, n);
		for (int i = 0; i < n; i++)
		{
			if (regions.RegionOfCell[i] < 0) continue;
			float sum = 0f; int cnt = 0;
			foreach (int j in neighbors[i])
			{
				if (regions.RegionOfCell[j] < 0) continue;
				sum += h[j]; cnt++;
			}
			if (cnt > 0) smoothed[i] = SmoothSelf * h[i] + (1f - SmoothSelf) * (sum / cnt);
			if (smoothed[i] < MinLandElevationM) smoothed[i] = MinLandElevationM;
		}

		HeightM = smoothed;
	}
}
