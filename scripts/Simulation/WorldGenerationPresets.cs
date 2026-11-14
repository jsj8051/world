using World.Application.Loading;

namespace World.Simulation;

/// <summary>
/// 世界生产档：分辨率取世界生产档（res4）、开演化、不额外跑初始步。
/// 生产策略留在世界侧；生成参数类型本身不认识"生产"这个概念。
/// </summary>
internal static class WorldGenerationPresets
{
	internal static readonly WorldGenerationOptions Production =
		new(WorldManager.ProductionRes, EnableEvolution: true, EvolutionSteps: 0);
}
