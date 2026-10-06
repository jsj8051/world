using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Godot;
using World.Spatial;
using World.WorldGen;
using World.Utils.H3;    // H3.GetNumCells（纯标量换算，不需要 Ball）

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 降水模型护栏（River 2A + ★D-14 修复）。
/// D-14（2026-10-04）修的是**两处独立缺陷**，本文件各自钉死，防止其中任一处被单独回退：
///   ① **字段接线**：原来读 `FinalDistToLand`（BFS from 陆格 ⇒ 陆格**恒 0**）⇒
///      coastFactor 恒 = 1.0，"临海多雨、内陆衰减"从未生效，降水实为**纯纬度带函数**。
///      陆格的离岸距离是 `FinalDistToCoast`（BFS from 海格）。
///   ② **尺度口径**：原来 `exp(-hops/10)` 把 **hop 当物理距离**用 ⇒ 同一个"10 跳"
///      在 res4 = 452 km、res1 = 8,364 km（差 18×），违反永久原则"res 只决定一个格子
///      多大，物理参数决定现实世界多大"。衰减长度现按 **km** 冻结。
/// 退化解（永久原则 4）：全陆临海 ⇒ 内陆深度 ≡ 0 ⇒ coastFactor ≡ 1.0 ⇒
///   逐点退化到修复前的行为（那时因字段选错恰好也恒为 1.0）。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class PrecipitationModelTests
{
	// res2：5,882 格 / 1,733 陆格，内陆深度中位 2 跳、最大 8 跳 ⇒ 海岸与内陆**都有样本**。
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(2, 1f));
	static Ball Ball => SharedBall.Value;

	static (FinalGeography f, PrecipitationModel p) Make(int seed = 42)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var gr = new GeologicalRegions(seed);
		gr.Generate(Ball, proj, 8_000_000f);
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);
		m.Generate(Ball, gr, surface);
		var l = new RegionalLandforms(seed);
		l.Generate(Ball, gr);
		var features = new List<FeatureField>
		{
			new(l, TerrainDomain.LandOnly),
			new(m, TerrainDomain.LandAndSea),
		};
		var c = new HeightComposer(seed);
		c.Generate(Ball, surface, gr, features);
		var f = new FinalGeography();
		f.Generate(Ball, c, gr);
		var p = new PrecipitationModel();
		p.Generate(Ball, f);
		return (f, p);
	}

	static double KmPerHopOf(int res)
	{
		var sc = SpatialScale.ForRes(res, (int)H3.GetNumCells(res));
		return Math.Sqrt(3.0) * sc.CellEdgeKm;   // 正六边形蜂窝的相邻格中心距
	}

	static float LatAbsDeg(int i)
		=> MathF.Abs(MathF.Asin(Math.Clamp(Ball.CellDirs[i].Y, -1f, 1f)) * 180f / MathF.PI);

	// ── ① 字段接线：海岸加权必须真的生效 ─────────────────────────────────────

	[Test]
	public void CoastWeighting_ActuallyApplies_AndTracksDistanceToCoast()
	{
		var (f, p) = Make();
		// 剥离纬度因子 ⇒ 剩下的就是海岸加权（AnnualMm = MaxMm · latFactor · coastFactor）
		double near = 0, far = 0; int nNear = 0, nFar = 0;
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (!f.FinalLand[i]) continue;
			double isolated = p.AnnualMm[i] / PrecipitationModel.LatFactor(LatAbsDeg(i));
			int d = f.FinalDistToCoast[i];
			if (d <= 1) { near += isolated; nNear++; }
			else if (d >= 4) { far += isolated; nFar++; }
		}
		Assert.That(nNear, Is.GreaterThan(0), "测试世界需要临海陆格");
		Assert.That(nFar, Is.GreaterThan(0), "测试世界需要真正的内陆陆格（否则本测试无判别力）");
		Assert.That(near / nNear, Is.GreaterThan(far / nFar),
			"★D-14 回归钉：临海陆格降水须高于内陆。若两者相等 ⇒ 海岸加权又退化成常数 "
			+ "（典型原因：又把 FinalDistToLand 接回来了——该字段对陆格恒 0）");
	}

	[Test]
	public void CoastWeighting_HasSpreadOverLand()
	{
		var (f, p) = Make();
		var distinct = new HashSet<float>();
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (!f.FinalLand[i]) continue;
			distinct.Add(p.AnnualMm[i] / PrecipitationModel.LatFactor(LatAbsDeg(i)));
		}
		Assert.That(distinct.Count, Is.GreaterThan(1),
			"陆格的海岸加权必须**有分布**；只有 1 个取值 ⇒ 权重恒为常数（D-14 回退）");
	}

	[Test]
	public void Ocean_UsesFullLatitudeFactor()
	{
		var (f, p) = Make();
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (f.FinalLand[i]) continue;
			double isolated = p.AnnualMm[i] / PrecipitationModel.LatFactor(LatAbsDeg(i));
			Assert.That(isolated, Is.EqualTo(PrecipitationModel.MaxMm).Within(1e-3),
				$"海格 {i}：无离岸距离概念，应按纬度带满额（coastFactor = 1.0）");
		}
	}

	// ── ② 尺度口径：衰减长度是 km，不是 hop ──────────────────────────────────

	[Test]
	public void CoastDecayLength_IsKm_NotHops()
	{
		double k3 = KmPerHopOf(3), k4 = KmPerHopOf(4);
		Assert.That(k4, Is.LessThan(k3), "res 越高一格越小 ⇒ 每跳的物理长度必须更短");

		// ① 同一**物理**内陆深度（≈450 km）在两档 res 应给出**同一**系数
		//    （差只能来自跳数的整数取整，量级 ≪ 一跳的效应）
		int h3 = (int)Math.Round(PrecipitationModel.CoastDecayKm / k3);
		int h4 = (int)Math.Round(PrecipitationModel.CoastDecayKm / k4);
		Assert.That(h3, Is.Not.EqualTo(h4),
			"两档 res 的『一跳』物理长度不同 ⇒ 同样的 450 km 对应的跳数**必须**不同；"
			+ "若相同，说明衰减长度又被写成了跳数");
		float f3 = PrecipitationModel.CoastFactor(h3 + 1, k3);
		float f4 = PrecipitationModel.CoastFactor(h4 + 1, k4);
		Assert.That(MathF.Abs(f3 - f4), Is.LessThan(0.05f),
			$"同一物理内陆深度（≈450 km）在 res3/res4 应给出同一系数（实测 {f3:F4} / {f4:F4}）");

		// ② 反证：写死跳数（D-14 修复前的做法）会让同一"10 跳"在两档 res 的物理含义差 2.6×
		Assert.That(MathF.Abs(10f * (float)k3 - 10f * (float)k4), Is.GreaterThan(700f),
			"反证：res3 的 10 跳 ≈1,196 km，res4 的 10 跳 ≈452 km ⇒ 差 2.6×");
		Assert.That(MathF.Abs(PrecipitationModel.CoastFactor(11, k3)
							- PrecipitationModel.CoastFactor(11, k4)), Is.GreaterThan(0.1f),
			"反证：写死 10 跳时两档 res 的系数显著不同 ⇒ 物理含义随 res 漂移（被禁做法）");
	}

	// ── ③ CoastFactor 本身：单调、有界、退化解 ───────────────────────────────

	[Test]
	public void CoastFactor_Monotone_AndBounded()
	{
		double k = KmPerHopOf(4);
		float prev = float.MaxValue;
		for (int hops = 1; hops <= 60; hops++)
		{
			float v = PrecipitationModel.CoastFactor(hops, k);
			Assert.That(v, Is.LessThanOrEqualTo(prev + 1e-6f), $"内陆深度 {hops} 跳：系数须单调不增");
			Assert.That(v, Is.InRange(PrecipitationModel.ContinentalFloor - 1e-5f, 1f + 1e-5f),
				$"内陆深度 {hops} 跳：系数须落在 [{PrecipitationModel.ContinentalFloor}, 1.0]");
			prev = v;
		}
	}

	[Test]
	public void CoastFactor_DegeneratesToUnity_WhenAllLandIsCoastal()
	{
		// ★退化解（永久原则 4）：全陆临海（离岸 1 跳 ⇒ 内陆深度 0 km）⇒ 系数 ≡ 1.0
		//   ⇒ **逐点退化到 D-14 修复前的行为**（那时因字段选错恰好也恒为 1.0）。
		//   这条同时说明：修复前的"恒 1.0"不是设计意图，而是"全世界都成了海岸"的巧合。
		Assert.That(PrecipitationModel.CoastFactor(1, KmPerHopOf(1)), Is.EqualTo(1f).Within(1e-6));
		Assert.That(PrecipitationModel.CoastFactor(1, KmPerHopOf(4)), Is.EqualTo(1f).Within(1e-6));
		Assert.That(PrecipitationModel.CoastFactor(0, KmPerHopOf(4)), Is.EqualTo(1f).Within(1e-6),
			"离岸 0 跳（防御：海格/异常值）同样取 1.0，不产生 >1 的系数");
	}

	[Test]
	public void CoastFactor_RejectsNonPositiveKmPerHop()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => PrecipitationModel.CoastFactor(3, 0.0));
		Assert.Throws<ArgumentOutOfRangeException>(() => PrecipitationModel.CoastFactor(3, -1.0));
	}

	// ── ④ 基本不变量 ────────────────────────────────────────────────────────

	[Test]
	public void AnnualMm_InRange_AndMeanPositive()
	{
		var (_, p) = Make();
		Assert.That(p.AnnualMm, Has.All.InRange(0f, PrecipitationModel.MaxMm + 1e-3f));
		Assert.That(p.MeanMm, Is.GreaterThan(0f));
		int n = p.AnnualMm.Length;
		double sum = 0;
		for (int i = 0; i < n; i++) sum += p.AnnualMm[i];
		Assert.That(p.MeanMm, Is.EqualTo(sum / n).Within(1e-2f), "MeanMm 须等于逐格均值");
	}

	[Test]
	public void EquatorialBand_IsWetterThanSubtropicalDryBand()
	{
		var (_, p) = Make();
		double eq = 0, sub = 0; int nEq = 0, nSub = 0;
		for (int i = 0; i < p.AnnualMm.Length; i++)
		{
			float lat = LatAbsDeg(i);
			if (lat < 8f) { eq += p.AnnualMm[i]; nEq++; }
			else if (lat > 22f && lat < 33f) { sub += p.AnnualMm[i]; nSub++; }
		}
		Assert.That(nEq, Is.GreaterThan(0));
		Assert.That(nSub, Is.GreaterThan(0));
		Assert.That(eq / nEq, Is.GreaterThan(sub / nSub),
			"赤道带（ITCZ）均雨须高于副热带干燥带——纬度带语义，与海岸加权叠加后仍须成立");
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.p.AnnualMm, b.p.AnnualMm, "同种子降水场须逐位同");
	}
}
