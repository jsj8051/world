using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using World.Spatial;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · **分辨率收敛**护栏（收口 §07 D-10，2026-10-04）。
///
/// ── 为什么有这个文件 ──────────────────────────────────────────────────────
/// res 只是**离散化精度**。它的目标**不是**"每个格一模一样"（那不可能，也无意义），
/// 而是：**提高 res ⇒ 宏观统计量趋于稳定**。这是判断"物理参数是否真的与 res 解耦"的
/// 唯一手段——如果某个量随 res 漂移，要么是它本身是拓扑量（该标 cell/hop），
/// 要么是它的物理参数写成了格数（D-10 要消灭的那类）。
///
/// ── 为什么阈值必须用物理口径 ──────────────────────────────────────────────
/// 本文件全链路用 `riverThresholdAreaKm2 = 70,800 km²`（黄河量级），**不用格数阈值**。
/// 否则测到的"不收敛"只是参数口径造成的假象：40 格在 res1 = 2,431 万 km²、
/// 在 res4 = 7.08 万 km²，差 34 倍。
///
/// ── 纪律 ─────────────────────────────────────────────────────────────────
/// 硬断言只放**恒等式与守恒**（可证明的）；演化趋势用**宽区间**；
/// 当前已知的**缺陷行为**用特征测试钉住（characterization test，注释标明是缺陷跟踪
/// 还是期望行为——修好后该测试会红并提醒改写）。
/// 只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class ResolutionConvergenceTests
{
	const int Seed = 42;
	const double ThresholdKm2 = 70_800.0;   // 黄河流域量级，跨 res 语义一致

	// 只跑 res1..res3：res4 单档 4.2 s，测试套件已有 1 分钟，不宜再翻倍。
	// res4 档由 `PerfBench --converge 1 2 3 4` 做人工判读（文档 §4.5 收录读数）。
	static readonly int[] ResSeq = { 1, 2, 3 };

	sealed class Stats
	{
		public int Res;
		public int Cells;
		public double CellAreaKm2;
		public int LandCells;
		public double LandFractionPct;
		public int BasinCount;
		public double MaxBasinKm2;
		public double MedianBasinKm2;
		public double EndorheicPct;
		public double RiverCellPct;
		public int LakeCount;
		public double LakeAreaKm2;
		public double LakeVolumeKm3;
		public double SumBasinKm2;
	}

	static Stats Measure(int res)
	{
		var ball = new Ball(res, 1f);
		var sc = SpatialScale.Of(ball);
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
		var precip = new PrecipitationModel();
		precip.Generate(ball, final);
		var rivers = new RiverNetwork();
		rivers.Generate(ball, final, composer, annualPrecipMm: precip.AnnualMm,
			riverThresholdAreaKm2: ThresholdKm2);
		var basins = new BasinGraph();
		basins.Generate(ball, final, rivers);
		// D-16：LakeState 消费**原始地形**的内流洼地分区（不是上面那个路由 basins）
		var rawHydro = new H3Hydrology();
		rawHydro.Generate(ball, composer.HeightM, 0f, precip.AnnualMm);
		var depBasinsRaw = new BasinGraph();
		depBasinsRaw.Generate(ball, final, rawHydro.Downstream, rawHydro.WeightedAccum);
		var lakes = new LakeState();
		lakes.Generate(ball, final, composer.HeightM, depBasinsRaw, precip.AnnualMm);

		int land = 0;
		for (int i = 0; i < final.FinalLand.Length; i++) if (final.FinalLand[i]) land++;
		var sortedKm2 = basins.AreaKm2.OrderBy(x => x).ToArray();

		return new Stats
		{
			Res = res,
			Cells = ball.CellIds.Length,
			CellAreaKm2 = sc.CellAreaKm2,
			LandCells = land,
			LandFractionPct = 100.0 * land / ball.CellDirs.Length,
			BasinCount = basins.BasinCount,
			MaxBasinKm2 = basins.AreaKm2.Length > 0 ? basins.AreaKm2.Max() : 0,
			MedianBasinKm2 = sortedKm2.Length > 0 ? sortedKm2[sortedKm2.Length / 2] : 0,
			EndorheicPct = basins.BasinCount > 0 ? 100.0 * basins.EndorheicBasinCount / basins.BasinCount : 0,
			RiverCellPct = 100.0 * rivers.IsRiver.Count(x => x) / Math.Max(1, land),
			LakeCount = lakes.LakeCount,
			LakeAreaKm2 = lakes.AreaKm2.Sum(),
			LakeVolumeKm3 = lakes.VolumeKm3.Sum(),
			SumBasinKm2 = basins.AreaKm2.Sum(),
		};
	}

	[Test]
	public void LandFraction_ConvergesAcrossResolution()
	{
		// ★这是全套统计量里**唯一真正收敛**的量：实测 29.22 / 29.46 / 29.13 / 29.02 %
		//（res1..res4 极差 0.44 个百分点）。它证明"海陆判定"已经是 res 无关的物理事实，
		// 而不依赖"每格多少"的格数口径。
		foreach (int res in ResSeq)
		{
			var s = Measure(res);
			Assert.That(s.LandFractionPct, Is.InRange(28.0, 31.0),
				$"res{res}：陆地占比 {s.LandFractionPct:F2}% 应收敛在 28~31%（海陆判定的分辨率无关性）");
		}
	}

	[Test]
	public void AreaIdentities_HoldAtEveryResolution()
	{
		// 守恒恒等式：与 res 无关，任何档位都必须成立（这是尺度层的地基）。
		foreach (int res in ResSeq)
		{
			var s = Measure(res);
			Assert.That(s.SumBasinKm2, Is.EqualTo(s.LandCells * s.CellAreaKm2).Within(1e-3),
				$"res{res}：Σ 流域面积必须等于陆地总面积");
			Assert.That(s.LakeAreaKm2, Is.LessThanOrEqualTo(s.LandCells * s.CellAreaKm2 + 1e-6),
				$"res{res}：湖泊总面积不得超过陆地总面积");
			Assert.That(s.LakeVolumeKm3, Is.GreaterThanOrEqualTo(0));
			Assert.That(double.IsNaN(s.LakeAreaKm2) || double.IsInfinity(s.LakeAreaKm2), Is.False,
				$"res{res}：湖泊面积出现 NaN/Inf（单位或口径出错）");
		}
	}

	[Test]
	public void MedianBasin_IsSingleCell_ScalesWithCellArea()
	{
		// ★★ 缺陷跟踪（**不是**期望行为）—— 2026-10-04 实测：
		//   res1 中位流域 605,777 km²  vs 格面积 605,777 km²（比值 1.000）
		//   res2 = 86,716  vs 86,716（1.000）  res3 = 12,392 vs 12,392（1.000）
		//   res4 = 1,770   vs 1,770（1.000）
		// ⇒ **超过一半的流域是"单格流域"**（面积 = 一个格子）。这些流域没有物理意义
		//   （一条河至少应有若干格汇水），它们是把内流占比推到 35% 的直接原因。
		// 之所以仍钉住它：这是"当前状态"的诚实记录——修好后（提高最小流域面积门槛）
		// 本测试会红，提醒把断言改写成"中位流域面积 ≥ k × 格面积"的收敛断言。
		foreach (int res in ResSeq)
		{
			var s = Measure(res);
			Assert.That(s.MedianBasinKm2, Is.EqualTo(s.CellAreaKm2).Within(1.0),
				$"res{res}：中位流域面积应仍等于格面积（单格流域占多数）——若已修复，请改写为收敛断言");
		}
	}

	[Test]
	public void RiverDensity_DropsWithResolution_KnownDefect()
	{
		// ★★ 缺陷跟踪（**不是**期望行为）—— 2026-10-04 实测（物理阈值 70,800 km²），
		// 河流格数占**陆地格**的比例：
		//   res1 100.0%（246/246）→ res2 81.8%（1417/1733）→ res3 12.9%（1544/11991）
		//   → res4 0.98%（819/83613）。跨 ~100 倍。
		// 两个独立根因（都已诊断，见 §8.3）：
		//   ① **阈值在粗网格下失效**：res1 每格 605,777 km² ≫ 70,800 km² ⇒ 一格就达标
		//      ⇒ 整块陆地都是河。这不是"阈值太小"，是**粗网格没有足够空间让阈值生效**。
		//   ② **噪声相关长度 vs 采样密度不匹配**：HeightComposer 三档波长
		//      5000/600/90 km 本身正确，但 res4 每格 26 km（90 km 噪声仅 3.5 格）
		//      ⇒ 汇流累积攒不到阈值 ⇒ 河网断链。
		// ⇒ **水文模型的有效工作尺度集中在 res2 附近**，但**生产分辨率 = res4 冻结**。
		// ⚠️ 该差异被认定为**水文模型的尺度适配缺陷（D-11）**，
		//    **不得通过降低生产分辨率规避**（用户二次拍板，§07 §5.5）——
		//    正确方向是让水文适配 res4，而不是把世界降到 res2。
		//    res1~res4 是**诊断实验档**，不是从中挑生产档。
		// 钉住"当前状态"：修好后本测试会红，提醒改写为"河流密度随 res 收敛"的断言。
		var seq = ResSeq.Select(Measure).ToList();
		for (int i = 1; i < seq.Count; i++)
			Assert.That(seq[i].RiverCellPct, Is.LessThan(seq[i - 1].RiverCellPct),
				$"res{seq[i].Res} 河流密度应低于 res{seq[i - 1].Res}（当前缺陷行为；"
				+ "若已修复请改写为'河流总长随 res 收敛'的收敛断言）");
		// 粗网格不应出现"整块陆地都是河"（res1 实测 100%，是阈值失效的信号）
		foreach (var s in seq.Where(x => x.Res >= 2))
			Assert.That(s.RiverCellPct, Is.LessThan(90.0),
				$"res{s.Res}：{s.RiverCellPct:F1}% 的陆地格是河，明显不合理（阈值口径回归？）");
	}

	[Test]
	public void EndorheicFraction_RisesWithResolution_KnownDefect()
	{
		// ★ 观测（已登记于 §4.4 / D-11）：内流占比 res1 4.4% → res4 35.0%（地球约 20%）。
		// 格越细保留越多微小洼地，与"单格流域占多数"（上一条）同源。
		// 这里只钉"物理上界"（内流不可能超过 100%，且不能全内流），不做趋势断言——
		// 趋势由 `RiverDensity_DropsWithResolution_KnownDefect` 间接反映。
		foreach (int res in ResSeq)
		{
			var s = Measure(res);
			Assert.That(s.EndorheicPct, Is.InRange(0.0, 100.0));
			Assert.That(s.EndorheicPct, Is.LessThan(99.0),
				$"res{res}：内流占比 {s.EndorheicPct:F1}% 几乎全内流，物理上不合理");
		}
	}
}
