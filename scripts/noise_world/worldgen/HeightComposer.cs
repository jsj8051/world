using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 高度合成器（阶段 6，决策 05 §六/§八）：
//   Height = continent（投影大陆基线）
//          + mountain（山脉骨架，阶段 4）
//          + plateau（高原帽，阶段 5）
//          − basin（盆地下挖，阶段 5）
//          + regionalNoise（区域细节，±80 m——区域内部不是平坦色块）
//   合成后 1 pass 图上高斯平滑（决策 §八 Smooth：h' = 0.6·h + 0.4·邻居均值；仅陆格参与——
//   海岸线不被模糊），得到连续地形。
// 海格 = 投影海侧输出原样（深度剖面不参与陆上合成）。
// 语义：这是**唯一**的逐格海拔出处（单一事实源）——视图/判读/存档读这里，不读各层的中间场。
/// <summary>
/// 高度合成：大陆基线 + 山脉骨架 + 高原帽 − 盆地下挖 + 区域细节，图上平滑后输出。
/// </summary>
public sealed class HeightComposer
{
	/// <summary>最终逐格海拔（米；海负陆正）。</summary>
	public float[] HeightM { get; private set; } = Array.Empty<float>();

	public const float RegionalNoiseM = 80f;    // H_detail：小尺度细节（米）
	public const float MediumVariationM = 130f; // H_medium：丘陵/山谷起伏（300-900 km 波长）
	public const float LargeVariationM = 260f;  // H_large：大地形起伏（5000 km 波长；尺度纪律：large > medium > detail）
	public const float SmoothSelf = 0.6f;      // 平滑自权重（1 pass；0.4 给邻居均值）
	public const float MinLandElevationM = 30f; // 陆格海拔保底（盆地挖穿防护——内流洼地语义由 relief 承载）

	readonly int _seed;

	public HeightComposer(int seed) => _seed = seed;

	public void Generate(Ball ball, H3LandSeaProjector proj, GeologicalRegions regions,
		MountainSkeleton mountains, RegionalLandforms landforms)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (proj == null) throw new ArgumentNullException(nameof(proj));
		if (regions == null) throw new ArgumentNullException(nameof(regions));
		if (mountains == null) throw new ArgumentNullException(nameof(mountains));
		if (landforms == null) throw new ArgumentNullException(nameof(landforms));
		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;

		// ── ① 线性合成（决策 05v2 §十二公式；海格 = 投影海侧原样）──
		//   尺度纪律（§十一）：大尺度 > 中尺度 > 小尺度——层间振幅严格递减。
		var large = new SphericalFbmNoise(_seed ^ 0x1A6E, 5000f, 2);   // H_large：大地形起伏（大陆尺度走势）
		var medium = new SphericalFbmNoise(_seed ^ 0x4E02, 600f, 3);   // H_medium：丘陵/山谷局部起伏
		var detail = new SphericalFbmNoise(_seed ^ 0x4E01, 90f, 2);    // H_detail：小山包/沟壑
		var h = new float[n];
		for (int i = 0; i < n; i++)
		{
			if (proj.Land[i])
				h[i] = regions.BaseElevationField[i]           // H_region：区域基础高度插值场（大陆内部梯度）
					+ mountains.ElevationAddM[i]               // H_mountain（System：Range+Ridge）
					+ landforms.PlateauAddM[i]                 // H_plateau
					- landforms.BasinDipM[i]                   // − H_basin
					+ LargeVariationM * large.Sample(dirs[i])  // + H_large_variation
					+ MediumVariationM * medium.Sample(dirs[i])// + H_medium_variation
					+ RegionalNoiseM * detail.Sample(dirs[i]); // + H_detail
			else
				h[i] = proj.ElevationM[i];
		}

		// ── ② 海平面保底 + 1 pass 图上平滑（仅陆格互为邻居；海陆边界不模糊）──
		//   陆格钳 ≥ MinLandElevationM：盆地挖穿 + large variation 会使内陆出现大片负海拔
		//   （视觉=内陆"海斑"，物理不成立——地球内流洼地低于海平面的面积极小；
		//   盆地"低洼"语义由 relief 相对高度承载，不靠绝对负值）
		var neighbors = ball.CellNeighbors;
		var smoothed = new float[n];
		Array.Copy(h, smoothed, n);
		for (int i = 0; i < n; i++)
		{
			if (!proj.Land[i]) continue;
			if (smoothed[i] < MinLandElevationM) smoothed[i] = MinLandElevationM;
			float sum = 0f; int cnt = 0;
			foreach (int j in neighbors[i])
			{
				if (!proj.Land[j]) continue;
				sum += h[j]; cnt++;
			}
			if (cnt > 0) smoothed[i] = SmoothSelf * h[i] + (1f - SmoothSelf) * (sum / cnt);
			if (smoothed[i] < MinLandElevationM) smoothed[i] = MinLandElevationM;
		}

		HeightM = smoothed;
	}
}
