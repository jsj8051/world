namespace World.Application.Loading;

/// <summary>
/// 生成参数：分辨率与进入地图前完成的雕刻轮数，至少完成一轮。
/// </summary>
internal readonly record struct WorldGenerationOptions(int Resolution, int InitialSculptingRounds);
