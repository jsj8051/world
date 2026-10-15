using World.Data;            // TerrainSpec（本阶段配置）
using World.H3Grid;          // Ball（H3 球壳数据层）

namespace World.Logic;

/// <summary>
/// ① 地形实现：高度合成。
/// 步骤由 <see cref="TerrainPipeline"/> 编排，参数从 <see cref="TerrainSpec"/> 传入。
/// </summary>
public sealed class TerrainManager
{
	/// <summary>高度合成（常量基线 + 三档变化；最终高度 <c>Composer.HeightM</c> 是单一事实源）。</summary>
	public HeightComposer Composer { get; private set; }

	/// <summary>
	/// 跑高度合成。逐格数组归 <see cref="HeightComposer"/> 自己持有（SoA），本类只聚合引用。
	/// </summary>
	/// <param name="ball">球壳数据层。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">本阶段配置（当前无字段——地形内容未上线）。</param>
	/// <param name="placementLand">上游板块阶段的陆地掩码。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, bool[] placementLand)
	{
		Composer = new HeightComposer(seed);
		Composer.Generate(ball, placementLand);
	}
}
