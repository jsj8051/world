namespace World.Application.Loading;

/// <summary>
/// 生成参数：分辨率、是否带演化、初始演化步数。与持久化配置无关——这些值不写进档案格式，
/// 也不再从世界页场景导出读取（页面不该决定世界多大）。
/// </summary>
internal readonly record struct WorldGenerationOptions(int Resolution, bool EnableEvolution, int EvolutionSteps);
