using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using World.Data;               // WorldSpec / LandSeaSpec / TerrainSpec（参数实例）
using World.Params;             // WorldParams（参数管理器**实例**：实例 + JSON + 改参数）＋ WorldParamStore（文件 I/O）

namespace World.Tests;

/// <summary>
/// **参数管理器契约**——「`res/params/` 下的 JSON 文本 → <see cref="WorldSpec"/> **实例** → 使用」
/// 这条链的护栏。2026-10-11 结构定型：**JSON 的读写与转换全在 <see cref="WorldParams"/> 里**
/// （原独立的编解码内核 `WorldSpecCodec` 已删除；同日 `WorldParams` 又由静态类改为**实例**，
/// 由根管理器 `WorldRoot` 构造并持有）。覆盖四层：
///   · **实例化**：`Active` 唯一且身份不变（`Reload` / `Set` 都就地改字段）；
///   · **改参数**：`Set` 一个动作两半 = 改实例 ＋ 整份写回用户档；
///   · **JSON 语义**：直接反序列化（**无字段合并、无逐字段校验**）+ 三条"能救命的"错要报出来；
///   · **宿主**：正库默认档干净可用、两个文件就在 `res/params/`。
///
/// ★**实例化后的用例纪律**（本轮新增）：大多数用例用**自己 new 的 `WorldParams`**
///   （不调 `Reload()` ⇒ 不读盘、不碰用户档 ⇒ 用例之间零污染、可任意并行）；
///   只有真需要读写真档的用例（<see cref="RealPreset_LoadsCleanly"/> /
///   <see cref="Set_SyncsBothTheInstanceAndTheFile"/> / <see cref="BrokenUserFile_FallsBackToThePreset"/>）
///   才建"已加载"的实例，并用 `try/finally` 还原用户档。
///
/// ★**被删的两条旧护栏**（留名，免得日后当成"漏了"）：
///   ① `PartialFile_IsRejectedWholesale_MissingFieldsNamed`（残档整份拒绝）——用户明确不要字段级校验
///      ⇒ 改为 <see cref="PartialFile_FillsTheGapFromTheCodeDefaults"/>，把**实际行为**钉住并记录风险；
///   ② `WorldSpecCodec` 的全部用例——入口改为 `WorldParams` 的 `ReadJson` / `ReadJsonInto` / `ToJson`，
///      **能力一条没少**（只是搬了家）。
/// 纪律（同 WorldSpecTests）：只用 `[Test]`；不触碰 `GD.*`。
/// </summary>
public class WorldParamManagerTests
{
	/// <summary>一份**故意不完整**的档（只写被断言到的字段）——新语义下缺的字段 = spec 属性初值。</summary>
	const string PartialFixture =
		"{\"Seed\":42,\"LandSea\":{\"ContinentCount\":7,\"LandFraction\":0.29}," +
		"\"Terrain\":{\"TargetRegionAreaKm2\":4000000}}";

	/// <summary>完整档（三个段的所有字段都写全）——往返用例用它。</summary>
	const string FullFixture =
		"{\"Seed\":7,\"LandSea\":{" +
		"\"ContinentCount\":5,\"LandFraction\":0.4,\"WarpWavelengthKm\":2000,\"WarpOctaves\":2," +
		"\"WarpAmplitudeKm\":300,\"LowWavelengthKm\":4000,\"LowOctaves\":1,\"LowAmplitude\":0.2," +
		"\"MediumWavelengthKm\":900,\"MediumAmplitude\":0.15,\"SmallWavelengthKm\":200," +
		"\"SmallAmplitude\":0.05},\"Terrain\":{\"TargetRegionAreaKm2\":1000000}}";

	/// <summary>**不读盘**的参数管理器（出厂档实例值）——大多数用例用它，零磁盘、零全局状态。</summary>
	static WorldParams Fresh() => new WorldParams();

	/// <summary>**已加载**的参数管理器（读 res/params/ 两个档）——只有需要真档的用例用它。</summary>
	static WorldParams Loaded()
	{
		var p = new WorldParams();
		p.Reload();
		return p;
	}

	static WorldSpec ReadOrFail(string json)
	{
		var spec = WorldParams.ReadJson(json, out var problems);
		Assert.That(spec, Is.Not.Null, "本用例的输入本应能装配，实得：" + string.Join(" | ", problems));
		Assert.That(problems, Is.Empty, "本用例的输入本应无问题，实得：" + string.Join(" | ", problems));
		return spec;
	}

	// ─────────────────────────────────────────────────────────────────────
	// ① 实例化：实例唯一（本对象持有）+ 身份不变
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>
	/// **生效实例恒唯一、恒非空、段恒非空**：`Active` 是**这个管理器实例**持有的那一个对象
	/// （注意 `Is.SameAs`——若哪天有人把它换成"每次取一份新副本"，这里立刻红）。
	/// </summary>
	[Test]
	public void Active_IsTheSameInstanceEveryTime()
	{
		var p = Fresh();
		var a = p.Active;
		var b = p.Active;

		Assert.That(a, Is.Not.Null, "生效实例必须存在（构造时即建）");
		Assert.That(b, Is.SameAs(a), "生效的世界定义必须是**同一个实例**（实例化口径：不每次造副本）");
		Assert.That(a.LandSea, Is.Not.Null.And.SameAs(b.LandSea),
			"LandSea 段必须恒非空且是同一实例（持引用者如 LandSeaField 靠这条现取新值）");
		Assert.That(a.Terrain, Is.Not.Null.And.SameAs(b.Terrain), "Terrain 段必须恒非空且是同一实例");
	}

	/// <summary>
	/// **两个管理器实例互不干扰**（本轮由静态类改实例的**核心收益**）：
	/// 各自持有自己的 `Active`，`Set` 改的是一个不动另一个。
	/// </summary>
	[Test]
	public void TwoManagerInstances_AreIndependent()
	{
		var a = Fresh();
		var b = Fresh();

		Assert.That(a.Active, Is.Not.SameAs(b.Active), "两个实例的世界定义必须是不同对象");
		// 不落盘地直接改字段（Set 会写文件，这里只验"实例隔离"这一条）
		a.Active.LandSea.ContinentCount = 11;
		Assert.That(b.Active.LandSea.ContinentCount, Is.Not.EqualTo(11),
			"改一个管理器的实例不得影响另一个（静态类时代做不到这一点）");
	}

	/// <summary>
	/// **`Reload` 不换实例身份**（就地重填）：重新读盘之后，`Active` 还是原来那个对象——
	/// 这是"用参数时从实例取"能成立的前提（否则持引用者又要重新取一遍）。
	/// </summary>
	[Test]
	public void Reload_KeepsTheInstanceIdentity()
	{
		var p = Loaded();
		var before = p.Active;
		var landSea = before.LandSea;
		var terrain = before.Terrain;

		p.Reload();

		Assert.That(p.Active, Is.SameAs(before), "Reload 后生效实例必须还是同一个对象");
		Assert.That(p.Active.LandSea, Is.SameAs(landSea), "LandSea 段实例不得被替换");
		Assert.That(p.Active.Terrain, Is.SameAs(terrain), "Terrain 段实例不得被替换");
	}

	/// <summary>
	/// **已经持有段引用的人，改完参数就看得到新值**——这条把"实例身份"与"改参数"扣在一起：
	/// 旧的字段值语义（struct 拷贝）下，持引用者永远看旧值。
	/// </summary>
	[Test]
	public void ParameterChange_IsVisibleToHoldersOfTheSameInstance()
	{
		var p = Loaded();
		var held = p.Active.LandSea;   // 模拟 LandSeaField 那样"先拿到手"
		int original = held.ContinentCount;
		try
		{
			Assert.That(p.Set("LandSea.ContinentCount", "9"), Is.Empty,
				"改参数本应成功（实例 + 文件）");
			Assert.That(held.ContinentCount, Is.EqualTo(9),
				"持有段引用的消费方必须立刻看到新值（这就是实例化的意义）");
		}
		finally
		{
			p.Set("LandSea.ContinentCount", original.ToString(CultureInfo.InvariantCulture));
			p.Reload();
		}
	}

	// ─────────────────────────────────────────────────────────────────────
	// ② 改参数 = 改实例 + 改文件
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>
	/// **`Set` 的两半一起成立**：内存实例改了，**用户档文件也改了**——把内存现值清掉再
	/// `Reload()` 读回来验证"文件里真的是新值"（只在文件里才可能读回 0.5）。
	/// </summary>
	[Test]
	public void Set_SyncsBothTheInstanceAndTheFile()
	{
		var p = Loaded();
		float original = p.Active.LandSea.LandFraction;
		Assert.That(original, Is.Not.EqualTo(0.5f), "本用例需要与目标值不同的起点");

		try
		{
			Assert.That(p.Set("LandSea.LandFraction", "0.5"), Is.Empty,
				"改参数本应写入成功（编辑器 / headless 下用户档可写）");

			// 内存那一半
			Assert.That(p.Active.LandSea.LandFraction, Is.EqualTo(0.5f), "实例必须立刻是新值");

			// 文件那一半：清掉内存现值再 Reload（证明值确实来自文件，而不是内存残留）
			p.Active.LandSea.LandFraction = 0f;
			p.Reload();
			Assert.That(p.Active.LandSea.LandFraction, Is.EqualTo(0.5f),
				"Reload 后仍是 0.5 ⇒ 用户档文件里真的写进了新值（否则读回的就是清掉的 0）");
		}
		finally
		{
			p.Set("LandSea.LandFraction", original.ToString(CultureInfo.InvariantCulture));
			p.Reload();
		}
	}

	/// <summary>
	/// **参数名不认识 ⇒ 实例与文件都不动**，并报出可用参数清单（UI 报错 / 建面板都靠它）。
	/// </summary>
	[Test]
	public void UnknownParameterName_ChangesNothing_AndListsNames()
	{
		var p = Fresh();
		int before = p.Active.LandSea.ContinentCount;

		var problems = p.Set("LandSea.NotAField", "3");

		Assert.That(problems, Is.Not.Empty, "不存在的参数名必须报出来");
		Assert.That(string.Join(" ", problems), Does.Contain("ContinentCount"),
			"报错应附带可用参数清单（UI 靠它建面板 / 提示）");
		Assert.That(p.Active.LandSea.ContinentCount, Is.EqualTo(before),
			"改名失败时实例不得被动过");
		Assert.That(p.LastProblems, Is.Not.Empty, "失败也应留痕在 LastProblems");
	}

	/// <summary>**值解析不了 ⇒ 实例与文件都不动**（UI 输入是文本，按目标属性类型解析）。</summary>
	[Test]
	public void UnparsableValue_ChangesNothing()
	{
		var p = Fresh();
		float before = p.Active.LandSea.LandFraction;

		Assert.That(p.Set("LandSea.LandFraction", "abc"), Is.Not.Empty, "非数字必须报错");
		Assert.That(p.Active.LandSea.LandFraction, Is.EqualTo(before),
			"值解析失败时实例不得被动过");

		Assert.That(p.Set("Seed", "1.5"), Is.Not.Empty, "int 参数收小数必须报错");
	}

	/// <summary>**段本身不是可改的单个参数**（`LandSea` 是段，不是字段）——报错而不是静默当没事。</summary>
	[Test]
	public void SectionName_IsNotASettableParameter()
	{
		Assert.That(Fresh().Set("LandSea", "5"), Is.Not.Empty,
			"段名不是参数，必须报错（改段 = 整段替换，绕开逐字段写入口）");
	}

	/// <summary>**可用参数名清单 = spec 类型自身**（三个段的字段都在、段名不在、无重复）。</summary>
	[Test]
	public void ParameterNames_ComeFromTheSpecTypes()
	{
		var names = Fresh().ParameterNames().ToList();

		Assert.That(names, Does.Contain("Seed"));
		Assert.That(names, Does.Contain("LandSea.LandFraction"));
		Assert.That(names, Does.Contain("Terrain.TargetRegionAreaKm2"));
		Assert.That(names, Does.Not.Contain("LandSea"), "段名不应出现在「可改的单个参数」清单里");
		Assert.That(names.Distinct().Count(), Is.EqualTo(names.Count), "参数名不得重复");
	}

	// ─────────────────────────────────────────────────────────────────────
	// ③ JSON 语义：直接反序列化 + 三条"能救命"的错
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>**完整档可以原样读出**（含每个字段），且读出的是"那一类实例"而不是值。</summary>
	[Test]
	public void CompleteFile_IsReadIntoAnInstance()
	{
		var spec = ReadOrFail(FullFixture);

		Assert.That(spec, Is.InstanceOf<WorldSpec>(), "读到的必须是 spec 类型的实例");
		Assert.That(spec.Seed, Is.EqualTo(7));
		Assert.That(spec.LandSea, Is.Not.Null.And.InstanceOf<LandSeaSpec>());
		Assert.That(spec.Terrain, Is.Not.Null.And.InstanceOf<TerrainSpec>());
		Assert.That(spec.LandSea.ContinentCount, Is.EqualTo(5));
		Assert.That(spec.LandSea.SmallAmplitude, Is.EqualTo(0.05f));
		Assert.That(spec.Terrain.TargetRegionAreaKm2, Is.EqualTo(1_000_000f));
	}

	/// <summary>
	/// **引出可往返**：`ToJson` 写出的文本能被 `ReadJson` 原样读回（逐字段相等）。
	/// 这条守住"改参数落盘"的可靠性下限——写出去了、读不回来等于没写。
	/// </summary>
	[Test]
	public void ToJson_RoundTripsThroughReadJson()
	{
		var original = ReadOrFail(FullFixture);

		string written = WorldParams.ToJson(original, out var writeProblems);
		Assert.That(writeProblems, Is.Empty, "序列化不应有问题：" + string.Join(" | ", writeProblems));

		var readBack = ReadOrFail(written);
		AssertSameSpec(original, readBack);
	}

	/// <summary>
	/// **漏写的字段 ⇒ 用 spec 属性初值（= 出厂档值），不报错**——这是"不做逐字段校验"的**直接后果**，
	/// 本条把它**钉成已知行为**而不是当成漏测：读到 3000（`WarpWavelengthKm` 的出厂值）而不是 0。
	/// ⚠️ 风险照实说：给 spec **加字段 / 改字段名**时若忘了同步 `res/params/` 下两个档，
	/// 新字段会**永远停在出厂值且没有任何提示**（见 `WorldParams` 类头"代价照实记"）。
	/// </summary>
	[Test]
	public void PartialFile_FillsTheGapFromTheCodeDefaults()
	{
		var factory = new LandSeaSpec();
		var spec = ReadOrFail(PartialFixture);

		Assert.That(spec.LandSea.ContinentCount, Is.EqualTo(7), "档里写了的字段照读");
		Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.29f), "档里写了的字段照读");
		Assert.That(spec.LandSea.WarpWavelengthKm, Is.EqualTo(factory.WarpWavelengthKm),
			"档里**没写**的字段 = spec 属性初值（出厂档值），不报错");
		Assert.That(spec.LandSea.SmallAmplitude, Is.EqualTo(factory.SmallAmplitude),
			"同上；注意它不是 0（那才是「缺字段被读成 0」的最坏失败模式）");
	}

	/// <summary>
	/// **档里缺一整段 ⇒ 该段保持"出厂档值"，且不报错**——同样是"不做逐字段校验"的后果，
	/// 但它**安全**：段属性带初值且 `init` ⇒ 段恒非空、内容 = 出厂档值（不是 null、不是 0）。
	/// ★管理器里那道"段是不是 null"的兜底**正常情况下走不到**——它是防"有人把段改成可空"的护栏。
	/// </summary>
	[Test]
	public void MissingSection_KeepsTheFactorySection()
	{
		var target = ReadOrFail(FullFixture);
		var factoryTerrain = new TerrainSpec();

		bool ok = WorldParams.ReadJsonInto("{\"Seed\":1,\"LandSea\":{\"ContinentCount\":3}}",
			target, out var problems);

		Assert.That(ok, Is.True, "缺段不是致命错");
		Assert.That(target.Seed, Is.EqualTo(1), "写了的字段照读");
		Assert.That(target.LandSea.ContinentCount, Is.EqualTo(3), "写了的字段照读");
		Assert.That(target.Terrain, Is.Not.Null, "段的不变量：恒非空（缺席 ⇒ 属性初值兜住，不是 null）");
		Assert.That(target.Terrain.TargetRegionAreaKm2, Is.EqualTo(factoryTerrain.TargetRegionAreaKm2),
			"缺的段 = 出厂档值；若这条红了，说明 spec 的段属性被动过：" + string.Join(" | ", problems));
	}

	/// <summary>**注释与尾逗号可写**：参数表是给人改的，允许 `//` / `/* */` 与尾逗号。</summary>
	[Test]
	public void CommentsAndTrailingCommas_AreAccepted()
	{
		var spec = ReadOrFail(
			"{\n" +
			"  // 种子\n" +
			"  \"Seed\": 42,\n" +
			"  /* 海陆段：只改陆占比 */\n" +
			"  \"LandSea\": {\n" +
			"    \"ContinentCount\": 7,\n" +
			"    \"LandFraction\": 0.5,\n" +
			"    \"WarpWavelengthKm\": 3000,\n" +
			"    \"WarpOctaves\": 3,\n" +
			"    \"WarpAmplitudeKm\": 600,\n" +
			"    \"LowWavelengthKm\": 5000,\n" +
			"    \"LowOctaves\": 2,\n" +
			"    \"LowAmplitude\": 0.35,\n" +
			"    \"MediumWavelengthKm\": 1200,\n" +
			"    \"MediumAmplitude\": 0.25,\n" +
			"    \"SmallWavelengthKm\": 300,\n" +
			"    \"SmallAmplitude\": 0.12,\n" +      // 尾逗号
			"  },\n" +
			"  \"Terrain\": { \"TargetRegionAreaKm2\": 4000000 },\n" +
			"}\n");
		Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.5f));
	}

	/// <summary>**精度一致**：JSON 里的 `0.29` 必须逐位读出为 `0.29f`（而不是 double 的 `0.28999999999999998`）。</summary>
	[Test]
	public void FloatValue_IsReadAtFloatPrecision()
	{
		var spec = ReadOrFail(FullFixture.Replace("0.4", "0.29"));
		Assert.That(spec.LandSea.LandFraction, Is.EqualTo(0.29f),
			"0.29 必须按 float 读出（bit 级等于 0.29f）");
	}

	/// <summary>**字段名拼错不静默**：报问题（由 `UnmappedMemberHandling.Disallow` 提供），且不返回半份实例。</summary>
	[Test]
	public void UnknownField_IsReported_AndRejected()
	{
		string withTypo = FullFixture.Replace("\"ContinentCount\":5", "\"ContinentCount\":5,\"NotAField\":1");

		var spec = WorldParams.ReadJson(withTypo, out var problems);

		Assert.That(problems, Is.Not.Empty, "未知字段必须被报出来（不能静默忽略）");
		Assert.That(spec, Is.Null, "坏档不得返回半份实例（调用方据此保留现值）");
		Assert.That(string.Join(" ", problems), Does.Contain("NotAField"), "报错信息应点名字段");
	}

	/// <summary>**类型 / 结构不匹配不静默**：报问题且不返回实例。</summary>
	[Test]
	public void TypeOrShapeMismatch_IsReported_AndRejected()
	{
		foreach (var json in new[]
		{
			FullFixture.Replace("\"LandFraction\":0.4", "\"LandFraction\":\"abc\""),
			FullFixture.Replace("\"Terrain\":{\"TargetRegionAreaKm2\":1000000}", "\"Terrain\":5"),
		})
		{
			var spec = WorldParams.ReadJson(json, out var problems);
			Assert.That(problems, Is.Not.Empty, $"输入 `{json}` 必须被报出来");
			Assert.That(spec, Is.Null, $"输入 `{json}` 不得返回半份实例");
		}
	}

	/// <summary>**语法错 / 根不是对象 / 空文本 ⇒ 报问题且不返回实例**，不抛异常、不带病生成。</summary>
	[Test]
	public void BrokenJson_IsReported_AndRejected()
	{
		foreach (var json in new[] { "{ 这不是 JSON", "[]", "42", "" })
		{
			var spec = WorldParams.ReadJson(json, out var problems);
			Assert.That(problems, Is.Not.Empty, $"输入 `{json}` 必须被报出来");
			Assert.That(spec, Is.Null, $"输入 `{json}` 不得返回实例");
		}
	}

	/// <summary>
	/// **坏档读进实例 = 一个字段都不改**（`ReadJsonInto` 的失败语义）：
	/// 这是 `Reload` 能"坏的那一层跳过、保留上一层值"的依据。
	/// </summary>
	[Test]
	public void ReadJsonInto_KeepsTheTargetUntouched_WhenTheJsonIsBroken()
	{
		var target = ReadOrFail(FullFixture);

		bool ok = WorldParams.ReadJsonInto("{ 这不是 JSON", target, out var problems);

		Assert.That(ok, Is.False, "坏档必须返回 false");
		Assert.That(problems, Is.Not.Empty, "坏档必须报问题");
		Assert.That(target.Seed, Is.EqualTo(7), "坏档不得改动目标实例的任何字段");
		Assert.That(target.LandSea.ContinentCount, Is.EqualTo(5), "段内字段也不得被动过");
	}

	// ─────────────────────────────────────────────────────────────────────
	// ④ 宿主：正库默认档干净可用 + 位置在 res/params/
	// ─────────────────────────────────────────────────────────────────────

	/// <summary>**正库默认档干净可用**：能完整装配、零问题，且关键值就是"参数化之前的默认值"。</summary>
	[Test]
	public void RealPreset_LoadsCleanly()
	{
		var p = Loaded();

		Assert.That(p.Active, Is.Not.Null, "正库默认档必须能装配：" + string.Join(" | ", p.LoadProblems));
		Assert.That(p.LoadProblems, Is.Empty,
			"正库默认档必须零问题，实得：" + string.Join(" | ", p.LoadProblems));
		Assert.That(p.Active.Seed, Is.EqualTo(42), "默认档种子应为 42（逐值守卫见 WorldSpecTests）");
		Assert.That(p.Active.LandSea.LandFraction, Is.EqualTo(0.29f), "默认档陆海比应为 0.29");
	}

	/// <summary>
	/// **参数目录就在 `res/params/` 下**：两个档的磁盘路径都必须落在那里，
	/// 且路径解析得到（解析不到 = 内容根找不到 ⇒ 读不到参数）。
	/// </summary>
	[Test]
	public void ParamFiles_LiveUnderResParams()
	{
		string dir = WorldParamStore.ResolveParamsDir();
		Assert.That(dir, Is.Not.Null, "参数目录必须能在磁盘上定位（含 res/params/ 的内容根）");
		Assert.That(dir.Replace('\\', '/'), Does.EndWith("res/params"), "参数目录必须是 res/params/");

		Assert.That(WorldParamStore.ResolvePresetPath().Replace('\\', '/'),
			Does.EndWith("res/params/world_params.json"), "默认档必须是 res/params/world_params.json");
		Assert.That(WorldParamStore.ResolveUserPath().Replace('\\', '/'),
			Does.EndWith("res/params/world_params.user.json"), "用户档必须是 res/params/world_params.user.json");

		// ★路径只由**存储层**回答（2026-10-11 P1）：管理器不再转手暴露 `PresetPath` / `UserPath`——
		//   那是 I/O 的职责，管理器只管"实例 + 改参数"。这里钉住"管理器上不应再有路径属性"。
		Assert.That(typeof(WorldParams).GetProperty("PresetPath"), Is.Null,
			"WorldParams 不应再有 PresetPath：路径归 WorldParamStore");
		Assert.That(typeof(WorldParams).GetProperty("UserPath"), Is.Null,
			"WorldParams 不应再有 UserPath：路径归 WorldParamStore");
	}

	/// <summary>**加载标记**：构造后未调 `Reload` ⇒ `IsLoaded` 为假；调过 ⇒ 为真（幂等语义的可见面）。</summary>
	[Test]
	public void IsLoaded_ReflectsWhetherReloadWasCalled()
	{
		Assert.That(Fresh().IsLoaded, Is.False, "刚构造、未读盘 ⇒ 还没加载");
		Assert.That(Loaded().IsLoaded, Is.True, "调过 Reload ⇒ 已加载");
	}

	/// <summary>
	/// **用户档被写坏时，世界仍按默认档跑**（`Reload` 的容错面，端到端）：写一份残档进用户档，
	/// `Reload` 后生效值必须仍等于默认档那份，而不是被打成半份。
	/// ⚠️ 会真的改写用户档 ⇒ `try/finally` 还原。
	/// </summary>
	[Test]
	public void BrokenUserFile_FallsBackToThePreset()
	{
		var preset = Loaded().Active.Clone();

		try
		{
			Assert.That(WorldParamStore.WriteUserText("{ 这不是 JSON", out var writeProblems), Is.True,
				"写用户档本应成功：" + string.Join(" | ", writeProblems));
			var p = Loaded();

			Assert.That(string.Join(" ", p.LoadProblems), Does.Contain("用户档"),
				"坏用户档必须被报出来（不静默）");
			Assert.That(p.Active.Seed, Is.EqualTo(preset.Seed), "坏用户档 ⇒ 生效值仍是默认档那份");
			Assert.That(p.Active.LandSea.LandFraction, Is.EqualTo(preset.LandSea.LandFraction),
				"段内字段同样未被改坏");
		}
		finally
		{
			string presetJson = WorldParams.ToJson(preset, out _);
			Assert.That(WorldParamStore.WriteUserText(presetJson, out _), Is.True, "还原用户档失败");
			Loaded();   // 还原后再读一次，确保盘上状态干净（不依赖任何全局静态）
		}
	}

	/// <summary>逐字段比较两份世界定义（类是引用类型，没有值相等 ⇒ 手写比较，字段与 spec 一一对应）。</summary>
	static void AssertSameSpec(WorldSpec a, WorldSpec b)
	{
		const string because = "写出→读回必须逐字段相等（引出可靠性的下限）";
		Assert.That(b.Seed, Is.EqualTo(a.Seed), because + "（Seed）");
		var x = a.LandSea;
		var y = b.LandSea;
		Assert.That(y.ContinentCount, Is.EqualTo(x.ContinentCount), because + "（ContinentCount）");
		Assert.That(y.LandFraction, Is.EqualTo(x.LandFraction), because + "（LandFraction）");
		Assert.That(y.WarpWavelengthKm, Is.EqualTo(x.WarpWavelengthKm), because + "（WarpWavelengthKm）");
		Assert.That(y.WarpOctaves, Is.EqualTo(x.WarpOctaves), because + "（WarpOctaves）");
		Assert.That(y.WarpAmplitudeKm, Is.EqualTo(x.WarpAmplitudeKm), because + "（WarpAmplitudeKm）");
		Assert.That(y.LowWavelengthKm, Is.EqualTo(x.LowWavelengthKm), because + "（LowWavelengthKm）");
		Assert.That(y.LowOctaves, Is.EqualTo(x.LowOctaves), because + "（LowOctaves）");
		Assert.That(y.LowAmplitude, Is.EqualTo(x.LowAmplitude), because + "（LowAmplitude）");
		Assert.That(y.MediumWavelengthKm, Is.EqualTo(x.MediumWavelengthKm), because + "（MediumWavelengthKm）");
		Assert.That(y.MediumAmplitude, Is.EqualTo(x.MediumAmplitude), because + "（MediumAmplitude）");
		Assert.That(y.SmallWavelengthKm, Is.EqualTo(x.SmallWavelengthKm), because + "（SmallWavelengthKm）");
		Assert.That(y.SmallAmplitude, Is.EqualTo(x.SmallAmplitude), because + "（SmallAmplitude）");
		Assert.That(b.Terrain.TargetRegionAreaKm2, Is.EqualTo(a.Terrain.TargetRegionAreaKm2),
			because + "（TargetRegionAreaKm2）");
	}
}
