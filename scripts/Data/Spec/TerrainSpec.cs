namespace World.Data;

/// <summary>
/// ② 区域与地形阶段配置 —— **参数实例**（2026-10-11 由 `readonly record struct` 改为可变类，
/// 理由见 <see cref="LandSeaSpec"/> 头注释：实例有身份、可改，由参数管理器持有唯一一份）。
///
/// ★**默认值 = 出厂档**：`new TerrainSpec()` 必须等于正库默认档
///   `res/params/world_params.json` 的 `Terrain` 段（逐值守卫见 `WorldSpecTests`）。
/// </summary>
/// <remarks>
/// <c>TargetRegionAreaKm2</c>：地质区域粒度（km²/区域）。出厂值 4,000,000 ——
/// 与 <c>World.Constants.Geology.TargetRegionAreaKm2</c> **逐字等值**（JSON 写不了符号引用，
/// 只能靠 `WorldSpecTests` 防两者分叉）。
/// </remarks>
public sealed class TerrainSpec
{
	/// <summary>地质区域粒度（km²/区域）。出厂值 4,000,000（= `World.Constants.Geology.TargetRegionAreaKm2`）。</summary>
	public float TargetRegionAreaKm2 { get; set; } = 4_000_000f;

	/// <summary>逐字段复制到 <paramref name="target"/>（**不动实例身份**）。</summary>
	public void CopyTo(TerrainSpec target) => target.TargetRegionAreaKm2 = TargetRegionAreaKm2;

	/// <summary>独立副本（测试用；**绝不**拿管理器那份去改）。</summary>
	public TerrainSpec Clone() => new() { TargetRegionAreaKm2 = TargetRegionAreaKm2 };
}
