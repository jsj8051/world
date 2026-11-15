using World.Application.Loading;

namespace World.Application.Generation;

/// <summary>
/// 世界生产档：分辨率取世界生产档（res4）、开演化、不额外跑初始步。
/// 生成参数类型本身不认识"生产"这个概念，因此生产策略由应用层决定；
/// 它不依附场景节点，故与运行时宿主 <c>World.Runtime</c> 分开。
/// </summary>
internal static class WorldGenerationPresets
{
	/// <summary>
	/// 世界生产分辨率 = res4（冻结）：res4 是世界本身的空间离散尺度
	/// （288,122 格、每格 1,770 km²、格边长 ≈26.1 km），各模拟系统都应适配这个尺度。
	/// 不得靠降低生产分辨率规避子系统的尺度适配缺陷——正确处置是让子系统适配 res4，
	/// 否则滑向"不工作就换档"的架构倒退。res1~res4 同时用作诊断实验档，不是从中挑生产档。
	/// </summary>
	internal const int ProductionRes = 4;

	internal static readonly WorldGenerationOptions Production =
		new(ProductionRes, EnableEvolution: true, EvolutionSteps: 0);
}
