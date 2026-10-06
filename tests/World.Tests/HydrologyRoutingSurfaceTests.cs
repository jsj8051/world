using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.Spatial;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 水文路由表面（D-11 Batch A）测试。
/// ★按"底层类固定门槛"（§5.3）：不仅有公式自洽，还要**与独立实现逐点对照**
/// （priority-flood vs Planchon-Darboux），以及**退化解**验证。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class HydrologyRoutingSurfaceTests
{
	const int Seed = 42;

	/// <summary>装配一份世界（与 ResolutionConvergenceTests 同口径），返回原始高度与 Ball。</summary>
	static (Ball ball, float[] height, FinalGeography final, HeightComposer composer) Build(int res)
	{
		var ball = new Ball(res, 1f);
		var layout = new ContinentLayout(Seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = Seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var regions = new GeologicalRegions(Seed);
		regions.Generate(ball, proj, 8_000_000f);
		var mountains = new MountainSkeleton(Seed);
		mountains.Generate(ball, regions, surface);
		var landforms = new RegionalLandforms(Seed);
		landforms.Generate(ball, regions);
		var features = new List<FeatureField>
		{
			new(landforms, TerrainDomain.LandOnly),
			new(mountains, TerrainDomain.LandAndSea),
		};
		var composer = new HeightComposer(Seed);
		composer.Generate(ball, surface, regions, features);
		var final = new FinalGeography();
		final.Generate(ball, composer, regions);
		return (ball, composer.HeightM, final, composer);
	}

	/// <summary>
	/// ★钉住**生产默认事实**（用户 2026-10-04 拍板）：`DefaultUseDepressionFill == true`。
	/// 这条不是"配置快照"，而是把"填洼已是生产默认路径"写成可执行事实——
	/// 将来有人为了"调试方便"把默认改回 false，这里会**直接变红**，
	/// 迫使他先回答"为什么回退"再改测试，而不是悄悄退回旧行为。
	/// </summary>
	[Test]
	public void DefaultUseDepressionFill_IsTrue()
	{
		Assert.That(HydrologyRoutingSurface.DefaultUseDepressionFill, Is.True,
			"生产默认必须开启填洼（§13.7 反事实验收已达标：湖事实不变 + 收敛 13.8×→2.43×）。" +
			"要改回 false 必须先说明为什么回退，并同步更新本测试与 §13。");

		// 默认调用（不传 useDepressionFill）⇒ 必须真的走了填洼路径
		var (ball, h, final, composer) = Build(2);
		var r = new RiverNetwork();
		r.Generate(ball, final, composer, annualPrecipMm: null);
		Assert.That(r.Routing.UsedFill, Is.True, "默认调用必须启用填洼");
		Assert.That(r.SinkCount, Is.Zero, "默认路径下路由侧不应再有内流洼地");
	}

	/// <summary>
	/// ★钉住**显式退化解**仍然成立（用户要求）：显式 `useDepressionFill: false`
	/// ⇒ 路由回到填洼前的行为，而 `LakeState` 的事实**保持不变**。
	/// 用途：证明无填洼时的行为、隔离填洼引入的影响、做 A/B——不是生产调参。
	/// </summary>
	[Test]
	public void ExplicitlyDisabledFill_PreservesPreFillRouting()
	{
		var (ball, h, final, composer) = Build(3);
		var precip = new PrecipitationModel();
		precip.Generate(ball, final);

		// 基准：把**原始高度**直接交给 H3Hydrology（= 填洼前的既有行为）
		var rawHydro = new H3Hydrology();
		rawHydro.Generate(ball, h, 0f, precip.AnnualMm);
		var depBasins = new BasinGraph();
		depBasins.Generate(ball, final, rawHydro.Downstream, rawHydro.WeightedAccum);

		// 显式关闭填洼
		var off = new RiverNetwork();
		off.Generate(ball, final, composer, annualPrecipMm: precip.AnnualMm,
			riverThresholdAreaKm2: 70_800.0, useDepressionFill: false);
		Assert.That(off.Routing.UsedFill, Is.False);

		// ① 路由必须**逐格回到**原始高度上的既有行为（退化解）
		for (int i = 0; i < h.Length; i++)
		{
			Assert.That(off.Downstream[i], Is.EqualTo(rawHydro.Downstream[i]),
				$"格 {i}：显式关闭填洼 ⇒ Downstream 必须逐格等于原始高度上的结果");
			Assert.That(off.FlowAccum[i], Is.EqualTo(rawHydro.FlowAccum[i]),
				$"格 {i}：显式关闭填洼 ⇒ FlowAccum 必须逐格等于原始高度上的结果");
		}
		Assert.That(off.SinkCount, Is.EqualTo(rawHydro.SinkCount), "洼地数须回到原始值");
		Assert.That(off.SinkCount, Is.GreaterThan(0), "原始地形确有洼地（否则本测试无意义）");

		// ② LakeState 事实：关闭与开启**一致**（填洼不改变"湖是否存在"）
		var on = new RiverNetwork();
		on.Generate(ball, final, composer, annualPrecipMm: precip.AnnualMm,
			riverThresholdAreaKm2: 70_800.0, useDepressionFill: true);
		var lkOff = new LakeState();
		lkOff.Generate(ball, final, h, depBasins, precip.AnnualMm);
		var lkOn = new LakeState();
		lkOn.Generate(ball, final, h, depBasins, precip.AnnualMm);
		Assert.That(lkOn.LakeCount, Is.EqualTo(lkOff.LakeCount), "湖数不随开关变化");
		Assert.That(lkOn.LakeCount, Is.GreaterThan(0), "两侧都应有湖");

		// ③ 且开启侧确实改变了路由（否则本测试无法区分两条路径）
		Assert.That(on.SinkCount, Is.Zero, "开启侧路由洼地应清零");
		Assert.That(on.Routing.FilledCellCount, Is.GreaterThan(0), "开启侧应有填洼格");
	}

	[Test]
	public void SwitchOff_IsExactIdentity()
	{
		// ★退化解（用户拍板的核心要求）：开关关闭 ⇒ RoutingHeightM 与源高度**逐点相同**，
		// 且与"直接把源高度交给 H3Hydrology"的汇流结果**完全一致**。
		var (ball, h, _, composer2) = Build(2);
		var surf = new HydrologyRoutingSurface();
		surf.Generate(ball, h, 0f, useFill: false);

		Assert.That(surf.UsedFill, Is.False, "记录事实：本次未启用填洼");
		Assert.That(surf.RoutingHeightM.Length, Is.EqualTo(h.Length));
		for (int i = 0; i < h.Length; i++)
			Assert.That(surf.RoutingHeightM[i], Is.EqualTo(h[i]),
				$"格 {i}：开关关闭时路由高度必须**逐点**等于源高度（零漂移）");
		Assert.That(surf.FillDepthM, Is.All.EqualTo(0f), "未填洼 ⇒ 填深全 0");
		Assert.That(surf.FilledCellCount, Is.Zero);
		Assert.That(surf.MaxFillDepthM, Is.Zero);

		// 汇流侧也必须一致（不是只有高度数组一样）
		var a = new H3Hydrology(); a.Generate(ball, h, 0f, null);
		var b = new H3Hydrology(); b.Generate(ball, surf.RoutingHeightM, 0f, null);
		Assert.That(b.SinkCount, Is.EqualTo(a.SinkCount), "关闭开关 ⇒ 洼地数不变");
		for (int i = 0; i < h.Length; i++)
			Assert.That(b.FlowAccum[i], Is.EqualTo(a.FlowAccum[i]), $"格 {i}：FlowAccum 须逐点一致");
	}

	[Test]
	public void RawHeightIsNeverMutated()
	{
		// ★语义关键：**填洼不得回写源高度**——Final 地形事实必须保持不动
		//（否则"这里存在一个内流盆地"会变成"根本没有这个盆地"）。
		var (ball, h, _, composer2) = Build(2);
		var before = (float[])h.Clone();
		var surf = new HydrologyRoutingSurface();
		surf.Generate(ball, h, 0f, useFill: true);
		for (int i = 0; i < h.Length; i++)
			Assert.That(h[i], Is.EqualTo(before[i]), $"格 {i}：源高度（Final 地形）被修改了——绝不允许");
	}

	[Test]
	public void FillOnlyRaises_NeverLowers()
	{
		var (ball, h, _, composer2) = Build(2);
		var surf = new HydrologyRoutingSurface();
		surf.Generate(ball, h, 0f, useFill: true);
		Assert.That(surf.UsedFill, Is.True);
		for (int i = 0; i < h.Length; i++)
			Assert.That(surf.FillDepthM[i], Is.GreaterThanOrEqualTo(0f), $"格 {i}：填洼只准抬升");
		Assert.That(surf.FilledCellCount, Is.GreaterThan(0), "本次世界应存在洼地");
	}

	[Test]
	public void FillEliminatesSinks_AndEveryLandCellReachesSea()
	{
		// 填洼的**定义性质**：所有陆格沿严格下降都能到达出口，且出口必是海格
		//（内流洼地在路由侧消失，但 `DepressionCount` 仍报告原始事实）。
		var (ball, h, _, _c) = Build(3);
		var raw = new H3Hydrology(); raw.Generate(ball, h, 0f, null);
		var surf = new HydrologyRoutingSurface();
		surf.Generate(ball, h, 0f, useFill: true);
		var filled = new H3Hydrology(); filled.Generate(ball, surf.RoutingHeightM, 0f, null);

		Assert.That(raw.SinkCount, Is.GreaterThan(0), "原始地形应有洼地（否则本测试无意义）");
		Assert.That(filled.SinkCount, Is.Zero, "填洼后路由侧不应再有内流洼地");

		// 每个陆格的汇流终点必须是**海格**
		int n = h.Length;
		for (int i = 0; i < n; i++)
		{
			if (surf.RoutingHeightM[i] <= 0f) continue;      // 海格跳过
			int cur = i, steps = 0;
			while (filled.Downstream[cur] >= 0 && steps++ < n) cur = filled.Downstream[cur];
			Assert.That(surf.RoutingHeightM[cur], Is.LessThanOrEqualTo(0f),
				$"格 {i} 的汇流终点 {cur} 不是海格——填洼未达成可达性");
		}
	}

	[Test]
	public void DepressionCount_IsRawFact_AndSwitchIndependent()
	{
		// ★不要让填洼抹掉内流语义：`DepressionCount` 始终基于**原始高度**，与开关无关。
		var (ball, h, _, _c) = Build(3);
		var off = new HydrologyRoutingSurface(); off.Generate(ball, h, 0f, useFill: false);
		var on = new HydrologyRoutingSurface(); on.Generate(ball, h, 0f, useFill: true);
		var raw = new H3Hydrology(); raw.Generate(ball, h, 0f, null);

		Assert.That(off.DepressionCount, Is.EqualTo(raw.SinkCount),
			"关闭开关时，本类报告的洼地数 = H3Hydrology 在原始高度上的 SinkCount");
		Assert.That(on.DepressionCount, Is.EqualTo(off.DepressionCount),
			"★填洼不得改变 DepressionCount——原始洼地是**世界事实**，不是路由产物");
		Assert.That(on.DepressionCount, Is.EqualTo(raw.SinkCount));
	}

	[Test]
	public void MatchesIndependentPlanchonDarboux()
	{
		// ★底层类固定门槛（§5.3）：与**独立实现**逐点对照。
		// 参考实现 = Planchon & Darboux 迭代扫描（与 priority-flood 完全不同的算法路径）。
		// 两者应给出同一个"最小填洼面"；差异只应来自 ε 注入次数（链长 × 1e-3 m）。
		var (ball, h, _, composer2) = Build(2);
		var surf = new HydrologyRoutingSurface();
		surf.Generate(ball, h, 0f, useFill: true);

		var reference = PlanchonDarboux(ball, h, 0f);

		double maxDiff = 0;
		int n = h.Length;
		for (int i = 0; i < n; i++)
			maxDiff = Math.Max(maxDiff, Math.Abs(surf.RoutingHeightM[i] - reference[i]));

		double range = Max(h) - Min(h);
		// ★2026-10-04 实测：res2 上两算法逐点差 = **0.0000 m**（完全一致），
		//   故容差取 ε 量级（1e-3 m = 一次 ε 注入），远小于地形量程 ~5.6 km。
		//   若将来这里变红，说明两条算法路径之一被改坏了——不要只是放宽容差。
		Assert.That(maxDiff, Is.LessThan(1e-3),
			$"priority-flood 与 Planchon-Darboux 逐点最大差 {maxDiff:F6} m " +
			$"（地形量程 {range:F0} m；容差 = 一次 ε = {HydrologyRoutingSurface.DescentEpsilonM} m）");
	}

	/// <summary>
	/// ★D-16 反事实验收（用户约束 3）：**路由表面与世界事实解耦**的证明。
	/// 填洼 OFF → ON：
	///   · `LakeState` 的湖泊事实必须**逐对象完全一致**（湖是否存在属于世界事实）；
	///   · FlowDirection / BasinGraph / FlowAccum **允许**发生预期变化（水怎么走属于水文）。
	/// 只钉住其中一边都不算证明——两边同时钉住，才是"解耦"。
	/// </summary>
	[Test]
	public void LakeFactsAreInvariantToFill_WhileRoutingChanges()
	{
		var (ball, h, final, composer) = Build(3);
		var precip = new PrecipitationModel();
		precip.Generate(ball, final);

		// ── 原始地形的内流洼地分区（D-16）：A/B 两侧共用**同一个** ──
		var rawHydro = new H3Hydrology();
		rawHydro.Generate(ball, h, 0f, precip.AnnualMm);
		var depBasins = new BasinGraph();
		depBasins.Generate(ball, final, rawHydro.Downstream, rawHydro.WeightedAccum);

		// ── A：填洼 OFF ──
		var riversOff = new RiverNetwork();
		riversOff.Generate(ball, final, composer, annualPrecipMm: precip.AnnualMm,
			riverThresholdAreaKm2: 70_800.0, useDepressionFill: false);
		var basinsOff = new BasinGraph();
		basinsOff.Generate(ball, final, riversOff);
		var lakesOff = new LakeState();
		lakesOff.Generate(ball, final, h, depBasins, precip.AnnualMm);

		// ── B：填洼 ON ──
		var riversOn = new RiverNetwork();
		riversOn.Generate(ball, final, composer, annualPrecipMm: precip.AnnualMm,
			riverThresholdAreaKm2: 70_800.0, useDepressionFill: true);
		var basinsOn = new BasinGraph();
		basinsOn.Generate(ball, final, riversOn);
		var lakesOn = new LakeState();
		lakesOn.Generate(ball, final, h, depBasins, precip.AnnualMm);

		// ① 路由侧**必须**发生变化（否则说明填洼根本没生效，本测试无意义）
		Assert.That(riversOff.SinkCount, Is.GreaterThan(0), "OFF 时应有内流洼地");
		Assert.That(riversOn.SinkCount, Is.Zero, "ON 时路由侧内流洼地应清零");
		Assert.That(basinsOn.EndorheicBasinCount, Is.Zero);
		Assert.That(basinsOff.EndorheicBasinCount, Is.GreaterThan(0));

		// ② 湖泊事实**必须**逐对象不变（★这才是 D-16 的核心断言）
		Assert.That(lakesOn.LakeCount, Is.EqualTo(lakesOff.LakeCount),
			"★填洼不得改变'世界里有多少湖'——湖是世界事实，不是路由产物");
		Assert.That(lakesOn.LakeCount, Is.GreaterThan(0), "ON 时湖不得归零（D-16 修复前这里是 0）");
		Assert.That(lakesOn.AreaKm2, Is.EqualTo(lakesOff.AreaKm2), "湖面积须逐湖一致");
		Assert.That(lakesOn.VolumeKm3, Is.EqualTo(lakesOff.VolumeKm3), "湖体积须逐湖一致");
		Assert.That(lakesOn.SurfaceElevationM, Is.EqualTo(lakesOff.SurfaceElevationM), "水位须逐湖一致");
		Assert.That(lakesOn.BasinIdOf, Is.EqualTo(lakesOff.BasinIdOf), "湖→洼地绑定须一致");
		Assert.That(lakesOn.LakeOfBasin, Is.EqualTo(lakesOff.LakeOfBasin), "洼地→湖反查须一致");
		Assert.That(lakesOn.DepressionIdOf, Is.EqualTo(lakesOff.DepressionIdOf), "逐格洼地分区须一致");
	}

	// ── 独立参考实现（Planchon & Darboux，1976）：迭代扫描，与 priority-flood 无共享代码 ──
	static float[] PlanchonDarboux(Ball ball, float[] h, float seaLevelM)
	{
		int n = h.Length;
		var w = new float[n];
		for (int i = 0; i < n; i++) w[i] = h[i] <= seaLevelM ? h[i] : float.MaxValue;

		const float eps = HydrologyRoutingSurface.DescentEpsilonM;
		bool changed = true;
		while (changed)
		{
			changed = false;
			for (int c = 0; c < n; c++)
			{
				if (h[c] <= seaLevelM) continue;
				foreach (int nb in ball.CellNeighbors[c])
				{
					if (w[nb] >= float.MaxValue) continue;
					float cand = Math.Max(h[c], w[nb] + eps);
					if (cand < w[c]) { w[c] = cand; changed = true; }
				}
			}
		}
		for (int i = 0; i < n; i++) if (w[i] >= float.MaxValue) w[i] = h[i];
		return w;
	}

	static float Max(float[] a) { float m = float.MinValue; foreach (var v in a) if (v > m) m = v; return m; }
	static float Min(float[] a) { float m = float.MaxValue; foreach (var v in a) if (v < m) m = v; return m; }
}
