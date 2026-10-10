namespace World.Constants;

/// <summary>
/// **星球几何标定**（世界生成空间的尺度常数）：球半径的**唯一家**。
///
/// ★为什么独立成类（2026-10-11 用户拍板"ResLevel / Radius 不需要了，固定就好"）：
///   原先球半径有**三个副本各自填**——`WorldGenPlanet.Radius`（`[Export]`）、
///   `Ball`（构造时拷贝，顶点/格心坐标同尺度）、`OrbitalCamera._planetRadius`（`[Export]`），
///   三者一致纯属手工维护，只有"相机 vs 星球"一处有运行期校验。
///   收成常量后：改一个值即三方同步，`WorldManager.RadiusMismatchWarnings` 退化为**防手改的守卫**。
///
/// ★归属判据：与 `Geology`（设计标定）/ `Thermal`（物理常数）/ `BiomeType`（词表）同族 ——
///   常量族（`static class`）是这类值的既定归属，同类裁决见 `docs/裁决-数据层World.Data.md` §三。
/// </summary>
public static class PlanetGeometry
{
	/// <summary>
	/// **生产球半径（单位尺度，非 km）** = 世界几何的尺度因子。
	/// <para>消费方：`WorldGenPlanet.Initialize`（构造 `Ball`）＋ `OrbitalCamera` 的取景半径缺省。
	/// 取景与裁剪都 ∝ R ⇒ 两处必须同值（原 `[Export]` 手填时代靠校验兜着，现在同源）。</para>
	/// <para>★2.0 是**单位球**尺度：格几何（顶点/格心/高亮环）全部按它铺。</para>
	/// </summary>
	public const float ProductionRadius = 2.0f;

	/// <summary>
	/// **参考地球半径（km）** = 物理量换算用的基准（`Ball` 的几何与它无关）。
	/// <para>★**注意分工**：本世界模拟里的 km 一律以此为基准（降水 mm、面积 km² 等），
	/// 真正的换算口是 `SphericalFbmNoise.EarthRadiusKm`（逻辑层同一数值的既有常量，22 处消费）
	/// 与 `SpatialScale`（面积/距离口径）。本常量只服务**非逻辑层**（如相机缺省）需要该值的场合。</para>
	/// </summary>
	public const float EarthRadiusKm = 6371f;
}
