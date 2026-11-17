namespace World.Data;

/// <summary>
/// 星球几何标定：渲染球半径的唯一出处（世界生成空间的尺度常数）。
/// </summary>
public static class PlanetGeometry
{
	/// <summary>
	/// 生产球半径（单位尺度，非 km）= 世界几何的尺度因子（取 2.0）：格几何（顶点 / 格心 / 高亮环）按它铺。
	/// 世界生成时用于构造球体，页面装配时从实际球体读取半径并配置相机。
	/// </summary>
	public const float ProductionRadius = 2.0f;

	// 物理半径（km）不在这里：唯一家是 SphericalFbmNoise.WorldRadiusKm（地球尺度 6,371 km，
	// res4 每格 1,770 km²）；噪声波长与 SpatialScale 的面积 / 距离都经它换算。本类的半径只决定画多大。
}
