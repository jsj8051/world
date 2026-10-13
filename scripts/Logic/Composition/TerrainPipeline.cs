using System;
using World.Data;                 // TerrainSpec（本阶段配置）/ Plate（板块载体）
using World.H3Grid;               // Ball

namespace World.Logic;

/// <summary>
/// **① 区域与地形阶段**：**地质区域图** → 高度合成。编排层，实现全在 <see cref="TerrainManager"/>。
///
/// · `Regions` = 一张**八类区域图**（平原/盆地/海岸线/山脉/裂谷 + 浅海/深海/洋中脊）——**只是图**；
/// · `Composer` = 陆面常量基线 + 三档变化（海面平坦 0 m）。
/// </summary>
public sealed class TerrainPipeline
{
	readonly TerrainManager _impl = new();

	/// <summary>地质区域图（八类划分 + 同类连通区域编号；只读，不驱动任何内容）。</summary>
	public GeologicalRegionManager Regions => _impl.Regions;
	/// <summary>高度合成（常量基线 + 三档变化；最终高度 `HeightM` 单一事实源）。</summary>
	public HeightComposer Composer => _impl.Composer;
	/// <summary>显示海拔（= `Composer.HeightM`；信息卡/判读口）。</summary>
	public float[] DisplayElevation { get; private set; } = Array.Empty<float>();

	/// <param name="seed">世界种子（**全局层**：按值展开成 `int`，本阶段收不到 `WorldSpec`）。</param>
	/// <param name="spec">本阶段配置**实例**（**阶段层**）。</param>
	/// <param name="placementLand">上游板块阶段的陆地掩码。</param>
	/// <param name="plateOfCell">上游板块阶段的逐格板块号（区域图定缝合带用）。</param>
	/// <param name="plates">上游板块阶段的板块表（区域图算边界相对运动用）。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, bool[] placementLand,
		int[] plateOfCell, Plate[] plates)
	{
		_impl.Run(ball, seed, spec, placementLand, plateOfCell, plates);
		DisplayElevation = _impl.Composer.HeightM;
	}
}
