using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // TerrainSpec（本阶段配置）/ Plate（板块载体）
using World.H3Grid;          // Ball（H3 球壳数据层）

namespace World.Logic;

/// <summary>
/// ① 区域与地形实现：地质区域图 + 高度合成两条子链。
/// 步骤由 <see cref="TerrainPipeline"/> 编排，参数从 <see cref="TerrainSpec"/> 传入。
/// </summary>
public sealed class TerrainManager
{
	/// <summary>地质区域图（八类划分 + 同类连通区域编号；只读，不驱动任何内容）。</summary>
	public GeologicalRegionManager Regions { get; private set; }
	/// <summary>高度合成（常量基线 + 三档变化；最终高度 <c>Composer.HeightM</c> 是单一事实源）。</summary>
	public HeightComposer Composer { get; private set; }

	/// <summary>
	/// 按序跑两条子链：地质区域图 → 高度合成。
	/// 逐格数组归各子系统类自己持有（SoA），本类只聚合引用。
	/// </summary>
	/// <param name="ball">球壳数据层。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">本阶段配置（当前无字段——地形内容未上线）。</param>
	/// <param name="placementLand">上游板块阶段的陆地掩码。</param>
	/// <param name="plateOfCell">上游板块阶段的逐格板块号（区域图定缝合带用）。</param>
	/// <param name="plates">上游板块阶段的板块表（区域图算边界相对运动用）。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, bool[] placementLand,
		int[] plateOfCell, Plate[] plates)
	{
		Regions = new GeologicalRegionManager(seed);
		Regions.Generate(ball, placementLand, plateOfCell, plates);

		Composer = new HeightComposer(seed);
		Composer.Generate(ball, placementLand);
	}
}
