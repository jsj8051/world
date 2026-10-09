using System;
using NUnit.Framework;
using World.Data;                 // WorldSpec / LandSeaSpec / TerrainSpec（纯数据形状）
using World.WorldGen;             // WorldSpecDefaults（退化档）/ WorldGenSimulation
using World.Constants;            // Geology（区域粒度默认值）；Thermal 同族

namespace World.Tests;

/// <summary>
/// 世界定义 `WorldSpec` · **退化档 + 参数确实到位**的护栏（永久原则 4/5）。
///
/// 背景（2026-10-10）：把 4 个位置参数（`int seed, int continentCount, float landFraction,
/// float targetRegionAreaKm2`）收成 `WorldSpec`，**公式与数值一字未动**。
/// 所以本文件不测"算得对不对"（那是各子系统测试的事），只钉两件本次改造**新引入**的风险：
///   ① **退化档漂移**：`WorldSpecDefaults.Earth` 被改 ⇒ "默认世界"悄悄换掉，
///      而默认世界是全部诊断读数与跨 seed 验收的参照物（改了没人会立刻发现）；
///   ② **参数没到位**：解包时把 `spec.Seed` 或 `spec.LandSea.LandFraction` 接错/接漏 ——
///      这类错误**编译通过、既有 281 条测试全绿**（它们不走 `WorldGenSimulation.Run` 这条链），
///      只能靠一条真正跑 `Run(WorldSpec)` 的端到端断言照出来。
///
/// 纪律（同 TemperatureModelTests）：只用 `[Test]`；不写文件；不触碰 `GD.*`。
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
	/// **退化档 = 参数化之前的默认值**。三个值就是 2026-10-10 之前装配层 `[Export]` 的初值
	/// （除 `TargetRegionAreaKm2` 本来就是常量引用）。改动本档 = 改动"默认世界"，请先改本测试。
	/// </summary>
	[Test]
	public void EarthPresets_PinThePreParameterizationDefaults()
	{
		Assert.That(WorldSpecDefaults.Earth.LandSea.ContinentCount, Is.EqualTo(7),
			"退化档陆块数应为参数化之前的默认值 7 —— 改它等于换掉默认世界");
		Assert.That(WorldSpecDefaults.Earth.LandSea.LandFraction, Is.EqualTo(0.29f).Within(1e-6f),
			"退化档陆海比应为 0.29 —— 改它会让所有跨 seed 验收的参照物失效");

		// ★① 阶段的另 10 个世界参数（2026-10-10 第二批）：原以**字段初值**形式住在 `LandSeaParams` 里
		//   （内容装在形状里），现逐字搬入本档、`LandSeaParams` 已删除。它们同样是"默认世界"的一部分
		//   ——改任何一个都会让既有诊断读数与跨 seed 验收失去参照，故一并在本测试钉住。
		//   ★它们留在本档（而非进 `World.Constants`）的口径 = **消费者个数**：各自只有本档一个消费者；
		//     反例是 `Geology.TargetRegionAreaKm2`（两个消费者）故成了具名常量。
		var ls = WorldSpecDefaults.Earth.LandSea;
		// 域扭曲
		Assert.That(ls.WarpWavelengthKm, Is.EqualTo(3000f), "退化档域扭曲波长");
		Assert.That(ls.WarpOctaves, Is.EqualTo(3), "退化档域扭曲八度");
		Assert.That(ls.WarpAmplitudeKm, Is.EqualTo(600f), "退化档域扭曲幅度");
		// 三尺度轮廓调制
		Assert.That(ls.LowWavelengthKm, Is.EqualTo(5000f), "退化档大尺度波长");
		Assert.That(ls.LowOctaves, Is.EqualTo(2), "退化档大尺度八度");
		Assert.That(ls.LowAmplitude, Is.EqualTo(0.35f), "退化档大尺度幅度");
		Assert.That(ls.MediumWavelengthKm, Is.EqualTo(1200f), "退化档中尺度波长");
		Assert.That(ls.MediumAmplitude, Is.EqualTo(0.25f), "退化档中尺度幅度");
		Assert.That(ls.SmallWavelengthKm, Is.EqualTo(300f), "退化档小尺度波长");
		Assert.That(ls.SmallAmplitude, Is.EqualTo(0.12f), "退化档小尺度幅度");

		Assert.That(WorldSpecDefaults.Earth.Terrain.TargetRegionAreaKm2,
			Is.EqualTo(Geology.TargetRegionAreaKm2),
			"退化档区域粒度应**逐字等于** `World.Constants.Geology.TargetRegionAreaKm2`" +
			"（不得改成字面量——那样常量与默认档会分叉）");
		Assert.That(WorldSpecDefaults.Earth.Seed, Is.EqualTo(42),
			"退化档种子应为 42 —— 改它会让所有既有诊断读数失去参照");
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
		sim.Run(WorldSpecDefaults.Earth);

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
	/// </summary>
	[Test]
	public void Run_SeedParam_ReachesStages_AndStaysDeterministic()
	{
		var a = NewSim();
		a.Run(WorldSpecDefaults.Earth);
		var b = NewSim();
		b.Run(WorldSpecDefaults.Earth with { Seed = 7 });
		var a2 = NewSim();
		a2.Run(WorldSpecDefaults.Earth);

		float ha = Sum(a.Terrain.Composer.HeightM);
		float hb = Sum(b.Terrain.Composer.HeightM);
		float ha2 = Sum(a2.Terrain.Composer.HeightM);

		Assert.That(ha2, Is.EqualTo(ha).Within(1e-3f),
			"同 seed 两次 Run 必须逐位一致（确定性是存档/复现的前提）");
		Assert.That(MathF.Abs(hb - ha), Is.GreaterThan(1e-3f),
			"不同 seed 应产出不同的地形（相同说明 `spec.Seed` 没被展开到 ② 地形阶段）");
	}
}
