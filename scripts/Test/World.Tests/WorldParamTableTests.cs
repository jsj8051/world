using NUnit.Framework;
using World.Data;               // WorldSpec
using World.WorldGen;           // WorldParamTable（参数管理器）

namespace World.Tests;

/// <summary>
/// **参数管理器（`WorldParamTable`）契约**——「默认档 ＋ 玩家档 → `WorldSpec`」这一段的护栏。
///
/// 背景（2026-10-10 四次定型）：默认值从**代码常量**搬到**数据文件**（`data/world_params.json`），
/// 文件读写也归到管理器（场景层不碰磁盘）后，要守的性质收敛成四条：
///   ① **缺省补齐**：玩家档没写的字段必须取默认档，**绝不能**变成 `0`——这是最坏的失败模式
///      （世界看起来正常生成，数值全被清零）。spec 日后新增字段时同样由默认档补齐（老档兼容）；
///   ② **精度一致**：`float` 字段读 / 比较都只在 float 精度上（`0.29` 必须逐位等于 `0.29f`）；
///   ③ **坏数据不静默**：语法错 / 根不是对象 / 字段名拼错 / 类型不匹配 ⇒ 报问题 + 回落默认档；
///      **默认档坏掉是致命的**（无基底可落）⇒ 返回全零并报问题，不许偷偷用内置值；
///   ④ **正库默认档本身干净**：它是世界默认定义的唯一来源，必须能被完整装配。
///
/// ★①②③ 用一份**测试夹具 JSON**（<see cref="Fixture"/>）而不是真档：合并是纯函数，用受控夹具
///   测算法比耦合真实数据更稳（真档的逐值守卫在 `WorldSpecTests`）。夹具只写被断言到的字段，
///   **故意**不完整——"缺的字段取默认档"正是 ① 要测的行为。
/// 纪律（同 WorldSpecTests）：只用 `[Test]`；不写文件；不触碰 `GD.*`。
/// </summary>
public class WorldParamTableTests
{
	/// <summary>合并算法的受控夹具（故意的**不完整**默认档：只写被断言到的字段）。</summary>
	const string Fixture =
		"{\"Seed\":42,\"LandSea\":{\"ContinentCount\":7,\"LandFraction\":0.29}," +
		"\"Terrain\":{\"TargetRegionAreaKm2\":4000000}}";

	/// <summary>以夹具为基底装配玩家档；本用例的输入本应无问题。</summary>
	static WorldSpec MergeOrFail(string userJson)
	{
		var spec = WorldParamTable.Merge(userJson, Fixture, out var problems);
		Assert.That(problems, Is.Empty, "本用例的输入本应无问题，实得：" + string.Join(" | ", problems));
		return spec;
	}

	/// <summary>**正库默认档干净可用**：能完整装配、零问题，且关键值就是"参数化之前的默认值"。</summary>
	[Test]
	public void RealPreset_LoadsCleanly()
	{
		var spec = WorldParamTable.Preset(out var problems);

		Assert.That(problems, Is.Empty, "正库默认档必须零问题，实得：" + string.Join(" | ", problems));
		Assert.That(spec.Seed, Is.EqualTo(42), "默认档种子应为 42（逐值守卫见 WorldSpecTests）");
		Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.29f), "默认档陆海比应为 0.29");
	}

	/// <summary>**空玩家档 = "没有任何覆盖"** 的正常状态 ⇒ 等于默认档，且不报问题。</summary>
	[Test]
	public void EmptyUserTable_YieldsThePreset_WithoutProblems()
	{
		var spec = WorldParamTable.Merge("", Fixture, out var problems);

		Assert.That(problems, Is.Empty, "空玩家档是正常状态，不应报成问题");
		Assert.That(spec.Seed, Is.EqualTo(42));
		Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.29f));
	}

	/// <summary>
	/// **缺省补齐（最要紧的一条）**：玩家档缺字段 / 缺整段 ⇒ 一律取默认档，**不是 0**。
	/// 若哪天反序列化绕过合并、直接吃玩家档，这条会立刻变红。
	/// </summary>
	[Test]
	public void MissingFields_FallBackToPreset_NotZero()
	{
		foreach (var userJson in new[] { "{}", "{\"LandSea\":{}}", "{\"Terrain\":{}}" })
		{
			var spec = MergeOrFail(userJson);
			Assert.That(spec.Seed, Is.EqualTo(42), $"输入 `{userJson}` 缺省的种子必须来自默认档，不能是 0");
			Assert.That(spec.LandSea.ContinentCount, Is.EqualTo(7),
				$"输入 `{userJson}` 缺省的大陆锚点数必须来自默认档，不能是 0");
			Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.29f), $"输入 `{userJson}` 缺省的陆占比不能是 0");
		}
	}

	/// <summary>**只写想改的项**：覆盖一个字段，其余（同层与邻层）仍取默认档。</summary>
	[Test]
	public void Patch_TouchesOnlyTheNamedField()
	{
		var spec = MergeOrFail("{\"LandSea\":{\"LandFraction\":0.5}}");

		Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.5f), "覆盖值应到达 LandSeaSpec");
		Assert.That(spec.LandSea.ContinentCount, Is.EqualTo(7), "同层未覆盖的字段应仍取默认档");
		Assert.That(spec.Seed, Is.EqualTo(42), "邻层未覆盖的字段应仍取默认档");
	}

	/// <summary>**注释与尾逗号可写**：参数表是给人改的，允许 `//` / `/* */` 与尾逗号。</summary>
	[Test]
	public void CommentsAndTrailingCommas_AreAccepted()
	{
		var spec = MergeOrFail(
			"{\n" +
			"  // 陆占比\n" +
			"  \"LandSea\": { \"LandFraction\": 0.5, },\n" +
			"}\n");
		Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.5f));
	}

	/// <summary>**精度一致**：JSON 里的 `0.29` 必须逐位读出为 `0.29f`（而不是 double 的 `0.28999999999999998`）。</summary>
	[Test]
	public void FloatValue_IsReadAtFloatPrecision()
	{
		Assert.That(MergeOrFail("{\"LandSea\":{\"LandFraction\":0.29}}").LandSea.LandFraction,
			Is.EqualTo(0.29f), "0.29 必须按 float 读出（bit 级等于 0.29f）");
	}

	/// <summary>**字段名拼错不静默**：报问题 + 回落默认档（由 `UnmappedMemberHandling.Disallow` 提供）。</summary>
	[Test]
	public void UnknownField_IsReported_AndRejected()
	{
		var spec = WorldParamTable.Merge("{\"LandSea\":{\"NotAField\":1}}", Fixture, out var problems);

		Assert.That(problems, Is.Not.Empty, "未知字段必须被报出来（不能静默忽略）");
		Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.29f), "未知字段不应改变世界定义");
		Assert.That(string.Join(" ", problems), Does.Contain("NotAField"), "报错信息应点名字段");
	}

	/// <summary>**类型 / 结构不匹配不静默**：报问题 + 回落默认档。</summary>
	[Test]
	public void TypeOrShapeMismatch_IsReported_AndRejected()
	{
		foreach (var userJson in new[] { "{\"LandSea\":{\"LandFraction\":\"abc\"}}", "{\"LandSea\":5}" })
		{
			var spec = WorldParamTable.Merge(userJson, Fixture, out var problems);
			Assert.That(problems, Is.Not.Empty, $"输入 `{userJson}` 必须被报出来");
			Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.29f), $"输入 `{userJson}` 应回落到默认档");
		}
	}

	/// <summary>**玩家档语法错 / 根不是对象 ⇒ 回落默认档**，不抛异常、不带病生成。</summary>
	[Test]
	public void BrokenUserJson_FallsBackToPreset()
	{
		foreach (var userJson in new[] { "{ 这不是 JSON", "[]", "42" })
		{
			var spec = WorldParamTable.Merge(userJson, Fixture, out var problems);
			Assert.That(problems, Is.Not.Empty, $"输入 `{userJson}` 必须被报出来");
			Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.29f), $"输入 `{userJson}` 应回落到默认档世界");
		}
	}

	/// <summary>
	/// **默认档坏掉是致命配置错误**：没有可回落的基底 ⇒ 返回全零 spec 并报问题。
	/// 这条**故意**不是"悄悄用某种内置默认值"——那就等于把默认值又搬回代码里了。
	/// </summary>
	[Test]
	public void BrokenPreset_IsFatal_NotSilentlyDefaulted()
	{
		var spec = WorldParamTable.Merge("{}", "{ 这不是 JSON", out var problems);

		Assert.That(problems, Is.Not.Empty, "默认档坏掉必须被报出来");
		Assert.That(spec, Is.EqualTo(default(WorldSpec)), "默认档不可用时返回全零 spec（而非某种内置值）");
	}
}
