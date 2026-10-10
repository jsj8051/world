using System.Collections.Generic;

namespace World.Data;

/// <summary>
/// **世界定义**（身份 + 分阶段配置）—— 参数实例（可变类），由 `World.Params.WorldParams` 持有唯一一份：
/// 读参数读它、UI 改参数改它也写回文件；实例身份不变（`Reload` / `Set` 都就地改字段）。
/// 属性初值 = **出厂档**（默认档读不到时世界按它生成，不会退化成全 0）。
/// 不变量：`LandSea` / `Terrain` 恒非空，且**不整体替换**（要改走 `CopyTo` 逐字段写进去）。
/// </summary>
/// <remarks>
/// 两层参数结构（沿用 2026-10-10 定型，未变）：
/// <list type="bullet">
/// <item><c>Seed</c>：世界种子（**全局层**：各阶段经 `SeedDerivation` 派生 salt）。</item>
/// <item><c>LandSea</c>：① 海陆骨架阶段配置。</item>
/// <item><c>Terrain</c>：② 区域与地形阶段配置。</item>
/// </list>
/// </remarks>
public sealed class WorldSpec
{
	/// <summary>世界种子（**全局层**：各阶段经 `SeedDerivation` 派生 salt）。出厂值 42。</summary>
	public int Seed { get; set; } = 42;

	/// <summary>
	/// ① 海陆骨架阶段配置。恒非空；整体替换**只准在建档时**（<c>init</c>）——
	/// 生效期要改就 <see cref="CopyTo"/> 逐字段写进去，别换实例（换掉 = 持引用者看成旧值）。
	/// </summary>
	public LandSeaSpec LandSea { get; init; } = new();

	/// <summary>
	/// ② 区域与地形阶段配置。恒非空；整体替换**只准在建档时**（<c>init</c>）——
	/// 生效期要改就 <see cref="CopyTo"/> 逐字段写进去。
	/// </summary>
	public TerrainSpec Terrain { get; init; } = new();

	/// <summary>
	/// 逐字段复制到 <paramref name="target"/>（**不动实例身份**：目标对象还是原来那个）。
	/// 失败项（段缺失 / 目标段为 null）**不写**并记入 <paramref name="problems"/>——
	/// "缺段"是坏档，必须可见，不能悄悄把世界换成另一种形状（不变量：段恒非空）。
	/// </summary>
	public void CopyTo(WorldSpec target, List<string> problems = null)
	{
		target.Seed = Seed;
		if (LandSea == null) problems?.Add("世界定义缺 LandSea 段 ⇒ 该段保持目标现值（不覆盖）。");
		else if (target.LandSea == null) problems?.Add("目标世界定义的 LandSea 段为 null ⇒ 跳过（不变量破坏）。");
		else LandSea.CopyTo(target.LandSea);

		if (Terrain == null) problems?.Add("世界定义缺 Terrain 段 ⇒ 该段保持目标现值（不覆盖）。");
		else if (target.Terrain == null) problems?.Add("目标世界定义的 Terrain 段为 null ⇒ 跳过（不变量破坏）。");
		else Terrain.CopyTo(target.Terrain);
	}

	/// <summary>
	/// 独立副本（测试要"改一个字段、其余不动"时用它——**绝不**拿管理器持有的那份去改）。
	/// 子实例一并深拷，副本与本体此后互不影响。
	/// </summary>
	public WorldSpec Clone() => new()
	{
		Seed = Seed,
		LandSea = LandSea.Clone(),
		Terrain = Terrain.Clone(),
	};
}
