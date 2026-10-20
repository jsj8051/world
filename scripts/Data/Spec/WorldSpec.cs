using System.Collections.Generic;

namespace World.Data;

/// <summary>
/// 世界定义（身份 + 分阶段配置）：可变参数实例，由 <c>World.Params.WorldParams</c> 持有唯一一份。
/// 不变量：<c>Plate</c> / <c>Terrain</c> 恒非空，且不整体替换（要改走 <see cref="CopyTo"/> 逐字段写进去）。
/// </summary>
public sealed class WorldSpec
{
	/// <summary>世界种子。出厂值 42。</summary>
	public int Seed { get; set; } = 42;

	/// <summary>陆块布局档：<see cref="ContinentLayout.Auto"/> = 按下面四档权重用种子抽一档（出厂值 Auto，也是默认），
	/// 指定具体档则强制该档（调试单形态用）。它决定哪些板块当陆壳、怎么排，即大陆连片还是分散。</summary>
	public ContinentLayout Layout { get; set; } = ContinentLayout.Auto;

	/// <summary>地球样的权重（几块分开的大陆，中间夹大洋）。出厂值 0.5。</summary>
	public float EarthlikeWeight { get; set; } = 0.5f;

	/// <summary>盘古样的权重（一整块超级大陆，陆壳彼此相邻）。出厂值 0.2。</summary>
	public float PangeaWeight { get; set; } = 0.2f;

	/// <summary>群岛样的权重（陆壳块最多 + 陆面基线压低 ⇒ 浅海与成串岛）。出厂值 0.1。</summary>
	public float ArchipelagoWeight { get; set; } = 0.1f;

	/// <summary>多大陆样的权重（比地球档更多块、分得更均匀）。出厂值 0.2。</summary>
	public float ManyContinentsWeight { get; set; } = 0.2f;

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
		target.Layout = Layout;
		target.EarthlikeWeight = EarthlikeWeight;
		target.PangeaWeight = PangeaWeight;
		target.ArchipelagoWeight = ArchipelagoWeight;
		target.ManyContinentsWeight = ManyContinentsWeight;
		if (Plate == null) problems?.Add("世界定义缺 Plate 段 ⇒ 该段保持目标现值（不覆盖）。");
		else if (target.Plate == null) problems?.Add("目标世界定义的 Plate 段为 null ⇒ 跳过（不变量破坏）。");
		else Plate.CopyTo(target.Plate);

		if (Terrain == null) problems?.Add("世界定义缺 Terrain 段 ⇒ 该段保持目标现值（不覆盖）。");
		else if (target.Terrain == null) problems?.Add("目标世界定义的 Terrain 段为 null ⇒ 跳过（不变量破坏）。");
		else Terrain.CopyTo(target.Terrain);
	}
}
