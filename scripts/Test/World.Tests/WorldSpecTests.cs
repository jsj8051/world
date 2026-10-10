using System;
using NUnit.Framework;
using World.Data;                 // WorldSpec / LandSeaSpec / TerrainSpec（世界定义参数实例）
using World.Logic;             // WorldGenSimulation（纯逻辑入口）
using World.Constants;            // Geology（区域粒度常量）；Thermal 同族

namespace World.Tests;

/// <summary>
/// 世界定义 `WorldSpec` · **默认档内容 + 参数确实到位**的护栏（永久原则 4/5）。
///
/// 背景（2026-10-10）：把 4 个位置参数（`int seed, int continentCount, float landFraction,
/// float targetRegionAreaKm2`）收成 `WorldSpec`，**公式与数值一字未动**。
/// 所以本文件不测"算得对不对"（那是各子系统测试的事），只钉两件改造**新引入**的风险：
///   ① **默认世界漂移**：默认档 `res/params/world_params.json` 被改 ⇒ "默认世界"悄悄换掉，
///      而默认世界是全部诊断读数与跨 seed 验收的参照物（改了没人会立刻发现）。
///      ★2026-10-10 三次定型后，默认值**只**存在于那份数据文件里（代码里没有默认值常量，
///        原 `WorldSpecDefaults` 已删除）⇒ **本测试的字面量就是它的守卫**，改档必先改这里；
///   ② **参数没到位**：解包时把 `spec.Seed` 或 `spec.LandSea.LandFraction` 接错/接漏 ——
///      这类错误**编译通过、既有测试全绿**（它们不走 `WorldGenSimulation.Run` 这条链），
///      只能靠一条真正跑 `Run(WorldSpec)` 的端到端断言照出来。
///
/// 纪律（同 TemperatureModelTests）：只用 `[Test]`；不写文件；不触碰 `GD.*`
///   （读仓库里的默认档由 `WorldPreset` 负责，那是**读**、且读的正是本文件的被测对象本身）。
/// </summary>
public class WorldSpecTests
{
	/// <summary>res2（5,882 格）——够跑通六阶段，又快到能放进单测。</summary>
	static WorldGenSimulation NewSim() => new(2, 1f);

	static float Sum(float[] a)
	{
		float s = 0f;
		foreach (var v in a) s += v;
		return s;
	}

	/// <summary>
	/// **默认档 = 参数化之前的默认值**。这里每个字面量都是对 `res/params/world_params.json` 的守卫：
	/// 它们就是 2026-10-10 参数化之前装配层 `[Export]` 的初值（除 `TargetRegionAreaKm2` 本来就是常量引用）。
	/// 改动默认档 = 改动"默认世界"，请先改本测试。
	/// ★2026-10-11 起它还兼守**第二份拷贝**：spec 属性初值（= 出厂档，默认档文件读不到时生效）
	///   ——`WorldSpec` / `LandSeaSpec` / `TerrainSpec` 的初值必须与本档同值，
	///   否则"删掉 JSON"与"留着 JSON"会产出两个不同的世界（`FactoryDefaults_MatchThePresetFile` 钉住这条）。
	/// </summary>
	[Test]
	public void EarthPresets_PinThePreParameterizationDefaults()
	{
		Assert.That(WorldPreset.Earth.LandSea.ContinentCount, Is.EqualTo(7),
			"默认档陆块数应为参数化之前的默认值 7 —— 改它等于换掉默认世界");
		Assert.That(WorldPreset.Earth.LandSea.LandFraction, Is.EqualTo(0.29f).Within(1e-6f),
			"默认档陆海比应为 0.29 —— 改它会让所有跨 seed 验收的参照物失效");

		// ★① 阶段的另 10 个世界参数（2026-10-10 第二批）：原以**字段初值**形式住在 `LandSeaParams` 里
		//   （内容装在形状里），后逐字搬入默认档、`LandSeaParams` 已删除。它们同样是"默认世界"的一部分
		//   ——改任何一个都会让既有诊断读数与跨 seed 验收失去参照，故一并在本测试钉住。
		var ls = WorldPreset.Earth.LandSea;
		// 域扭曲
		Assert.That(ls.WarpWavelengthKm, Is.EqualTo(3000f), "默认档域扭曲波长");
		Assert.That(ls.WarpOctaves, Is.EqualTo(3), "默认档域扭曲八度");
		Assert.That(ls.WarpAmplitudeKm, Is.EqualTo(600f), "默认档域扭曲幅度");
		// 三尺度轮廓调制
		Assert.That(ls.LowWavelengthKm, Is.EqualTo(5000f), "默认档大尺度波长");
		Assert.That(ls.LowOctaves, Is.EqualTo(2), "默认档大尺度八度");
		Assert.That(ls.LowAmplitude, Is.EqualTo(0.35f), "默认档大尺度幅度");
		Assert.That(ls.MediumWavelengthKm, Is.EqualTo(1200f), "默认档中尺度波长");
		Assert.That(ls.MediumAmplitude, Is.EqualTo(0.25f), "默认档中尺度幅度");
		Assert.That(ls.SmallWavelengthKm, Is.EqualTo(300f), "默认档小尺度波长");
		Assert.That(ls.SmallAmplitude, Is.EqualTo(0.12f), "默认档小尺度幅度");

		// ★JSON 是数据、写不了符号引用 ⇒ 区域粒度在档里必然是字面量 `4000000`；
		//   本条把它与 `World.Constants.Geology.TargetRegionAreaKm2` 钉在一起，防两者分叉。
		Assert.That(WorldPreset.Earth.Terrain.TargetRegionAreaKm2,
			Is.EqualTo(Geology.TargetRegionAreaKm2),
			"默认档区域粒度应**逐字等于** `World.Constants.Geology.TargetRegionAreaKm2`" +
			"（JSON 里写不了符号引用，只能靠本条防分叉）");
		Assert.That(WorldPreset.Earth.Seed, Is.EqualTo(42),
			"默认档种子应为 42 —— 改它会让所有既有诊断读数失去参照");
	}

	/// <summary>
	/// **出厂档（spec 属性初值）必须与正库默认档同值**——2026-10-11 新增的第二份拷贝，故必须钉住。
	///
	/// 为什么代码里会再有一份：`WorldParamManager` 的基底是"出厂档实例值"
	/// （`new WorldSpec()`），默认档文件读不到时世界就按它生成。若两者分叉：
	/// **删掉 / 挪走 JSON 就会换一个世界**，而且不会有任何编译或运行期提示。
	/// 本测试就是那道提示（逐字段比，不只看几个）。
	/// </summary>
	[Test]
	public void FactoryDefaults_MatchThePresetFile()
	{
		var factory = new WorldSpec();
		var preset = WorldPreset.Earth;

		Assert.That(factory.Seed, Is.EqualTo(preset.Seed), "出厂档 Seed 必须与默认档同值");
		var f = factory.LandSea;
		var p = preset.LandSea;
		Assert.That(f.ContinentCount, Is.EqualTo(p.ContinentCount), "出厂档 ContinentCount");
		Assert.That(f.LandFraction, Is.EqualTo(p.LandFraction), "出厂档 LandFraction");
		Assert.That(f.WarpWavelengthKm, Is.EqualTo(p.WarpWavelengthKm), "出厂档 WarpWavelengthKm");
		Assert.That(f.WarpOctaves, Is.EqualTo(p.WarpOctaves), "出厂档 WarpOctaves");
		Assert.That(f.WarpAmplitudeKm, Is.EqualTo(p.WarpAmplitudeKm), "出厂档 WarpAmplitudeKm");
		Assert.That(f.LowWavelengthKm, Is.EqualTo(p.LowWavelengthKm), "出厂档 LowWavelengthKm");
		Assert.That(f.LowOctaves, Is.EqualTo(p.LowOctaves), "出厂档 LowOctaves");
		Assert.That(f.LowAmplitude, Is.EqualTo(p.LowAmplitude), "出厂档 LowAmplitude");
		Assert.That(f.MediumWavelengthKm, Is.EqualTo(p.MediumWavelengthKm), "出厂档 MediumWavelengthKm");
		Assert.That(f.MediumAmplitude, Is.EqualTo(p.MediumAmplitude), "出厂档 MediumAmplitude");
		Assert.That(f.SmallWavelengthKm, Is.EqualTo(p.SmallWavelengthKm), "出厂档 SmallWavelengthKm");
		Assert.That(f.SmallAmplitude, Is.EqualTo(p.SmallAmplitude), "出厂档 SmallAmplitude");
		Assert.That(factory.Terrain.TargetRegionAreaKm2, Is.EqualTo(preset.Terrain.TargetRegionAreaKm2),
			"出厂档 TargetRegionAreaKm2");
	}

	/// <summary>
	/// **端到端：`Run(WorldSpec)` 这条链真的通电**。
	/// 本文件之前没有任何测试碰过 `WorldGenSimulation.Run`（唯一调用点是场景装配层），
	/// 所以"解包接错"是纯盲区。这里跑一遍真实链路并检查 `landFraction` 确实到达了 ① 阶段
	/// （陆格占比 ≈ 目标值——参数接错会显著偏离）。
	/// </summary>
	[Test]
	public void Run_LandFractionParam_ReachesLandSeaStage()
	{
		var sim = NewSim();
		sim.Run(WorldPreset.Earth);

		Assert.That(sim.Ball.CellIds.Length, Is.EqualTo(2 + 120 * 49),
			"res2 格数应为 2+120·7² —— 不匹配说明 Ball 构造参数被换掉");

		var f = sim.Facts.Final;
		int land = 0;
		foreach (var isLand in f.FinalLand) if (isLand) land++;
		float actual = land / (float)f.FinalLand.Length;
		Assert.That(actual, Is.EqualTo(0.29f).Within(0.06f),
			$"实际陆地占比 {actual:P1} 应落在目标 29% 附近（±6%）——" +
			"明显偏离说明 `spec.LandSea.LandFraction` 没被传到 ① 海陆阶段（解包接错）");
	}

	/// <summary>
	/// **全局层 `spec.Seed` 确实逐阶段生效**（这是两层结构里"按值展开"那一半）。
	/// 判据用"不同 seed ⇒ 世界不同"+"同 seed ⇒ 逐位可复现"两条，避开对某个具体读数的脆弱依赖。
	///
	/// ★2026-10-11：spec 由 `readonly record struct` 改为**可变类实例** ⇒ 原来的
	///   `WorldPreset.Earth with { Seed = 7 }`（值拷贝构造）不成立，改用
	///   <see cref="WorldSpec.Clone"/> 造独立副本再改字段——**绝不在共享的那份上改**
	///   （那会把改动泄漏给同进程内其它用例）。
	/// </summary>
	[Test]
	public void Run_SeedParam_ReachesStages_AndStaysDeterministic()
	{
		var a = NewSim();
		a.Run(WorldPreset.Earth);
		var seed7 = WorldPreset.Earth.Clone();
		seed7.Seed = 7;
		var b = NewSim();
		b.Run(seed7);
		var a2 = NewSim();
		a2.Run(WorldPreset.Earth);

		float ha = Sum(a.Terrain.Composer.HeightM);
		float hb = Sum(b.Terrain.Composer.HeightM);
		float ha2 = Sum(a2.Terrain.Composer.HeightM);

		Assert.That(ha2, Is.EqualTo(ha).Within(1e-3f),
			"同 seed 两次 Run 必须逐位一致（确定性是存档/复现的前提）");
		Assert.That(MathF.Abs(hb - ha), Is.GreaterThan(1e-3f),
			"不同 seed 应产出不同的地形（相同说明 `spec.Seed` 没被展开到 ② 地形阶段）");
	}
}
