namespace World.Data;

/// <summary>
/// 星球几何标定：渲染球半径的唯一出处（世界生成空间的尺度常数）。
/// </summary>
public static class PlanetGeometry
{
	/// <summary>
	/// 生产球半径（单位尺度，非 km）= 世界几何的尺度因子（取 2.0）：格几何（顶点 / 格心 / 高亮环）按它铺。
	/// 消费方：WorldGenSimulation 构造（<c>new Ball(resLevel, radius)</c>）与 OrbitalCamera 的取景半径缺省；
	/// 取景与裁剪都 ∝ R ⇒ 两处必须同值，改这里即同源。
	/// </summary>
	public const float ProductionRadius = 2.0f;

	// 物理半径（km）不在这里：由"res4 每格 5 km²"推出，唯一家是 SphericalFbmNoise.WorldRadiusKm
	// （噪声波长与 SpatialScale 的面积 / 距离都经它换算）；本类的半径只决定画多大。
}
