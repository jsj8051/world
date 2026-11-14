using World.Application.Loading;

namespace World.Scene;

/// <summary>
/// 应用的生产生成档：分辨率取世界生产档（res4）、开演化、不额外跑初始步。
/// 数值留在组合根一侧，生成模型本身不认识"生产"这个概念。
/// </summary>
internal static class WorldGenerationPresets
{
	internal static readonly WorldGenerationOptions Production =
		new(WorldManager.ProductionRes, EnableEvolution: true, EvolutionSteps: 0);
}
