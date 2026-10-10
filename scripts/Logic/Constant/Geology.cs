namespace World.Constants;

/// <summary>
/// **地质 / 区域划分的标定常数**（世界生成的设计默认值——**不是**物理常数）。
///
/// ★为什么独立成类、归常量族（2026-10-10 用户拍板"迁移"）：
///   `TargetRegionAreaKm2` 原以 `public const` **寄生在生成器类** `GeologicalRegions` 上
///   （与 `ContinentAnchor` 原先寄生在 `ContinentLayout` 里同类）。
///   它的真实身份是"**世界定义 ② 地形阶段的默认值**"——默认世界（退化档）现由
///   正库参数表 `data/world_params.json` 的 `Terrain.TargetRegionAreaKm2` 给出；
///   本常量是那个 JSON 数值的**具名对照**（JSON 写不了符号引用）⇒ 消费方 = `GeologicalRegions.Generate`
///   的缺省参数 + `WorldSpecTests` 的等值守卫。
///   挂在生成器上，会让"默认世界由谁定义"在代码里看不出来（要翻到生成器才能看到默认值）。
///
/// ★归属判据：常量族（`static class`）是这类值的既定归属——
///   `Thermal`（物理常数）/ `BiomeType`（气候型词表）/ 本类（设计标定）。
///   同类归属裁决见 `docs/裁决-数据层World.Data.md` §三。
///
/// ★命名空间保持 `World.Constants`（与 `Thermal` / `BiomeType` 同族）；
///   本目录下的子目录（如 `Planet/`）只做**自由分组**，不参与命名空间。
/// </summary>
public static class Geology
{
	/// <summary>
	/// 地质区域粒度：**目标区域面积**（km²/区域）。地球档 ≈ 400 万 km²/区域。
	///
	/// 消费方：正库参数表 `data/world_params.json`（`Terrain.TargetRegionAreaKm2`，
	/// 默认世界=退化档）与 `GeologicalRegions.Generate`（缺省参数）。改本值 = 改"默认世界"：
	/// 须同步改 JSON，且先过
	/// `WorldSpecTests.EarthPresets_PinThePreParameterizationDefaults`（逐字钉住该值）。
	/// </summary>
	public const float TargetRegionAreaKm2 = 4_000_000f;
}
