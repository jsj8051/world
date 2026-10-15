using System.Collections.Generic;

namespace World.Data;

/// <summary>
/// 世界定义（身份 + 分阶段配置）—— 参数实例（可变类），由 `World.Params.WorldParams` 持有唯一一份。
/// 不变量：`Plate` / `Terrain` 恒非空，且不整体替换（要改走 `CopyTo` 逐字段写进去）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>Seed</c>：世界种子（全局层，各阶段经 `SeedDerivation` 派生 salt）。</item>
/// <item><c>Plate</c>：板块阶段配置（含板块划分与陆地场）。</item>
/// <item><c>Terrain</c>：地形阶段配置。</item>
/// </list>
/// </remarks>
public sealed class WorldSpec
{
	/// <summary>世界种子。出厂值 42。</summary>
	public int Seed { get; set; } = 42;

	/// <summary>板块阶段配置。恒非空；生效期要改就 <see cref="CopyTo"/> 逐字段写进去，别换实例。</summary>
	public PlateSpec Plate { get; init; } = new();

	/// <summary>地形阶段配置。恒非空；生效期要改就 <see cref="CopyTo"/> 逐字段写进去。</summary>
	public TerrainSpec Terrain { get; init; } = new();

	/// <summary>
	/// 逐字段复制到 <paramref name="target"/>（不动实例身份）。
	/// 失败项（段缺失 / 目标段为 null）不写并记入 <paramref name="problems"/>。
	/// </summary>
	public void CopyTo(WorldSpec target, List<string> problems = null)
	{
		target.Seed = Seed;
		if (Plate == null) problems?.Add("世界定义缺 Plate 段 ⇒ 该段保持目标现值（不覆盖）。");
		else if (target.Plate == null) problems?.Add("目标世界定义的 Plate 段为 null ⇒ 跳过（不变量破坏）。");
		else Plate.CopyTo(target.Plate);

		if (Terrain == null) problems?.Add("世界定义缺 Terrain 段 ⇒ 该段保持目标现值（不覆盖）。");
		else if (target.Terrain == null) problems?.Add("目标世界定义的 Terrain 段为 null ⇒ 跳过（不变量破坏）。");
		else Terrain.CopyTo(target.Terrain);
	}

	/// <summary>独立副本（改一个字段、其余不动时用它）。子实例一并深拷。</summary>
	public WorldSpec Clone() => new()
	{
		Seed = Seed,
		Plate = Plate.Clone(),
		Terrain = Terrain.Clone(),
	};
}
