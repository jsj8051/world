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

	public const float RegionalNoiseM = 80f;   // 区域细节噪声幅度（米）
	public const float SmoothSelf = 0.6f;      // 平滑自权重（1 pass；0.4 给邻居均值）

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

		// ── ① 线性合成（决策 §六公式；海格 = 投影海侧原样）──
		var noise = new SphericalFbmNoise(_seed ^ 0x4E01, 90f, 2);   // 区域细节：山体内部纹理同量级
		var h = new float[n];
		for (int i = 0; i < n; i++)
		{
			if (proj.Land[i])
				h[i] = proj.ElevationM[i]                      // continent
					+ mountains.ElevationAddM[i]               // mountain
					+ landforms.PlateauAddM[i]                 // plateau
					- landforms.BasinDipM[i]                   // − basin
					+ RegionalNoiseM * noise.Sample(dirs[i]);  // + regionalNoise
			else
				h[i] = proj.ElevationM[i];
		}

		// ── ② 1 pass 图上平滑（仅陆格互为邻居；海陆边界不模糊）──
		var neighbors = ball.CellNeighbors;
		var smoothed = new float[n];
		Array.Copy(h, smoothed, n);
		for (int i = 0; i < n; i++)
		{
			if (!proj.Land[i]) continue;
			float sum = 0f; int cnt = 0;
			foreach (int j in neighbors[i])
			{
				if (!proj.Land[j]) continue;
				sum += h[j]; cnt++;
			}
			if (cnt > 0) smoothed[i] = SmoothSelf * h[i] + (1f - SmoothSelf) * (sum / cnt);
		}

		HeightM = smoothed;
	}
}
