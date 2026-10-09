using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Godot;
using World.H3Grid;
using World.WorldGen;
using World.Utils.H3;    // H3.GetNumCells（纯标量换算，不需要 Ball）

namespace World.Tests;

/// <summary>
/// 世界生成空间 · **降水事实**护栏。
/// 本文件有三层职责，分别对应历史上两次改动：
///   ① **D-14**（2026-10-04）——两处独立缺陷：
///      a) **字段接线**：原来读 `FinalDistToLand`（BFS from 陆格 ⇒ 陆格**恒 0**）⇒ 海岸加权从未生效。
///      b) **尺度口径**：原来 `exp(-hops/10)` 把 **hop 当物理距离**。
///   ② **P4-2**（2026-10-06）——占位语义 → **绝对量纲**：`MaxMm(2400) × 无量纲系数`
///      ⇒ `ZonalMm(lat)[mm/年] × 海洋性系数`。旧场的陆地下限 588.9 mm（= 无荒漠），
///      新场须出现**真实干旱量级**的陆格。
/// 纪律（同 `TemperatureModelTests` / `NoiseTerrainTests`）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class PrecipitationModelTests
{
	// res2：5,882 格 / 1,744 陆格，内陆深度中位 2 跳、最大 8 跳 ⇒ 海岸与内陆**都有样本**。
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(2, 1f));
	static Ball Ball => SharedBall.Value;

	static (FinalGeography f, PrecipitationModel p) Make(int seed = 42)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, seed, WorldSpecDefaults.Earth.LandSea);
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

	/// <summary>线性插值分位数（须先排序）。</summary>
	static float Pct(List<float> sorted, double q)
	{
		double idx = q * (sorted.Count - 1);
		int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
		return sorted[lo] + (sorted[hi] - sorted[lo]) * (float)(idx - lo);
	}

	static List<float> Cells(bool landOnly)
	{
		var (f, p) = Make();
		var v = new List<float>();
		for (int i = 0; i < p.AnnualMm.Length; i++)
			if (f.FinalLand[i] == landOnly) v.Add(p.AnnualMm[i]);
		v.Sort();
		return v;
	}

	// ── ① 绝对量纲（P4-2 边界 ①：不使用"归一化降水量"冒充绝对量）────────────────

	[Test]
	public void ZonalProfile_IsAbsoluteMm_EarthLike()
	{
		// 量级自检锚：赤道纬向年均 ~2,200 mm/年、副热带干槽 ~850、极地 ~250（地球量级）
		Assert.That(PrecipitationModel.ZonalMm(0f), Is.EqualTo(2200f).Within(1f));
		Assert.That(PrecipitationModel.ZonalMm(25f), Is.InRange(700f, 1000f),
			"副热带干槽须落在真实量级（数百 mm），否则纬度带又变成无量纲系数");
		Assert.That(PrecipitationModel.ZonalMm(90f), Is.InRange(100f, 400f),
			"极地纬向年降水须是真实小量，不得归零");
	}

	[Test]
	public void Field_IsAbsoluteMm_NotNormalizedIndex()
	{
		var (f, p) = Make();
		// ★判据：若这是"归一化量/相对湿润度"，取值范围会被压成 [0,1] 或 [0.6,1]×常数。
		//   **绝对 mm 事实**必须同时满足：干端出现成百 mm 以下、湿端达到两千 mm 以上。
		var land = new List<float>();
		for (int i = 0; i < p.AnnualMm.Length; i++) if (f.FinalLand[i]) land.Add(p.AnnualMm[i]);
		land.Sort();
		Assert.That(land[0], Is.LessThan(400f),
			$"陆格最小年降水须进入真实干旱量级（实测 {land[0]:F1} mm）；"
			+ "旧占位场下限 588.9 mm ⇒ 世界上没有荒漠，属于「归一化掩盖物理」");
		Assert.That(land[^1], Is.GreaterThan(1800f),
			$"陆格最大年降水须达到湿润量级（实测 {land[^1]:F1} mm）");
		Assert.That(land[^1] / Math.Max(1f, land[0]), Is.GreaterThan(8f),
			"干湿比须显著 > 1（地球陆地干湿比 ~100×）；比值过小 ⇒ 场被压平");
	}

	[Test]
	public void GlobalMean_IsEarthLike_ArithmeticMean()
	{
		var (_, p) = Make();
		int n = p.AnnualMm.Length;
		double sum = 0;
		for (int i = 0; i < n; i++) sum += p.AnnualMm[i];
		Assert.That(p.MeanMm, Is.EqualTo(sum / n).Within(1e-2f),
			"★口径：H3 是**等积格** ⇒ MeanMm 必须是**算术平均**，不得再乘 cos(lat)（那是二次加权）");
		Assert.That(p.MeanMm, Is.InRange(800f, 1400f),
			$"全球（海陆合计）均值须是地球量级 ~1,000 mm/年（实测 {p.MeanMm:F0}）");
	}

	[Test]
	public void Ocean_HasRealPrecipitation_NotZeroFilled()
	{
		var (f, p) = Make();
		// ★与 Legacy 有意不同：Legacy `PREC` 把海格（64.88% 的格）**填 0**——那是建模产物。
		//   本事实给海格按物理取纬向真值，且**逐格严格等于** `ZonalMm(lat)`（无大陆性）。
		int checkedCells = 0;
		for (int i = 0; i < p.AnnualMm.Length; i++)
		{
			if (f.FinalLand[i]) continue;
			Assert.That(p.AnnualMm[i], Is.EqualTo(PrecipitationModel.ZonalMm(LatAbsDeg(i))).Within(1e-3f),
				$"海格 {i}：海面无离岸距离概念，须取纬向基准满额");
			checkedCells++;
		}
		Assert.That(checkedCells, Is.GreaterThan(0));
	}

	[Test]
	public void Land_IsNeverWetterThanOcean_AtSameLatitude()
	{
		var (f, p) = Make();
		for (int i = 0; i < p.AnnualMm.Length; i++)
		{
			if (!f.FinalLand[i]) continue;
			Assert.That(p.AnnualMm[i], Is.LessThanOrEqualTo(PrecipitationModel.ZonalMm(LatAbsDeg(i)) + 1e-3f),
				$"陆格 {i}：海洋性系数 ≤ 1 ⇒ 同纬陆格不得比海面更湿");
		}
	}

	// ── ② 干湿尾部（P4-2 验收检查 C：不只看 mean/max）─────────────────────────

	[Test]
	public void DryTail_ReachesArid_AndWetTail_ReachesHumid()
	{
		var land = Cells(landOnly: true);
		Assert.That(land.Count, Is.GreaterThan(100));
		float p1 = Pct(land, 0.01), p5 = Pct(land, 0.05), p50 = Pct(land, 0.50),
			  p95 = Pct(land, 0.95), p99 = Pct(land, 0.99);
		TestContext.Progress.WriteLine(
			$"[P4-2 陆格年降水分位] min={land[0]:F0} P1={p1:F0} P5={p5:F0} P25={Pct(land, 0.25):F0} "
			+ $"P50={p50:F0} P75={Pct(land, 0.75):F0} P95={p95:F0} P99={p99:F0} max={land[^1]:F0} mean={land.Average():F0}");

		Assert.That(p1, Is.LessThan(300f), $"陆格 P1 须进入干旱量级（实测 {p1:F0} mm）");
		Assert.That(p5, Is.LessThan(500f), $"陆格 P5 须为半干旱量级（实测 {p5:F0} mm）");
		Assert.That(p50, Is.InRange(300f, 1400f), $"陆格中位数须是地球陆地的合理量级（实测 {p50:F0} mm）");
		Assert.That(p95, Is.GreaterThan(1500f), $"陆格 P95 须为湿润量级（实测 {p95:F0} mm）");
		// 尾部**拉开**（不是被底板截断的窄带）
		Assert.That(p95, Is.GreaterThan(3f * Math.Max(1f, p5)),
			$"P95/P5 须显著拉开（实测 {p95:F0}/{p5:F0}）；被「单一底板」截断的场不会有这个跨度");
	}

	[Test]
	public void AridLand_Exists_AndIsSubtropicalInterior()
	{
		// ★物理归因（不只是"尾部有数"）：干旱陆格应当出现在**副热带 × 深内陆**，
		//   而不是随机撒在极地或赤道。
		var (f, p) = Make();
		double aridLatSum = 0; int arid = 0; int aridInterior = 0;
		double wetLatSum = 0; int wet = 0;
		for (int i = 0; i < p.AnnualMm.Length; i++)
		{
			if (!f.FinalLand[i]) continue;
			float lat = LatAbsDeg(i);
			if (p.AnnualMm[i] < 250f) { arid++; aridLatSum += lat; if (f.FinalDistToCoast[i] >= 3) aridInterior++; }
			if (p.AnnualMm[i] > 1800f) { wet++; wetLatSum += lat; }
		}
		Assert.That(arid, Is.GreaterThan(0), "测试世界须存在干旱陆格（否则 P4-2 的干端未落地）");
		Assert.That(aridInterior, Is.GreaterThan(0), "干旱陆格必须来自内陆（离岸 ≥ 3 跳）");
		Assert.That(aridLatSum / arid, Is.GreaterThan(wetLatSum / Math.Max(1, wet)),
			"★物理归因：干旱格平均纬度须**高于**湿润格——干来自**副热带下沉气流（±25°）**，"
			+ $"湿来自 **ITCZ（~5°）**（实测 干旱 {aridLatSum / arid:F1}° vs 湿润 {wetLatSum / Math.Max(1, wet):F1}°）；"
			+ "若干旱格随机散布（纬度与湿润格无异）⇒ 干不是由纬度机制产生的，只是被截断的噪声");
	}

	// ── ③ 字段接线：海岸加权必须真的生效（D-14 回归钉）────────────────────────

	[Test]
	public void CoastWeighting_ActuallyApplies_AndTracksDistanceToCoast()
	{
		var (f, p) = Make();
		// 剥离纬向基准 ⇒ 剩下的就是海洋性系数（AnnualMm = ZonalMm(lat) × CoastFactor）
		double near = 0, far = 0; int nNear = 0, nFar = 0;
		for (int i = 0; i < f.FinalLand.Length; i++)
		{
			if (!f.FinalLand[i]) continue;
			double isolated = p.AnnualMm[i] / PrecipitationModel.ZonalMm(LatAbsDeg(i));
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

	// ── ④ 尺度口径：衰减长度是 km，不是 hop（D-14 回归钉）─────────────────────

	[Test]
	public void CoastDecayLength_IsKm_NotHops()
	{
		const float lat = 45f;   // 固定纬度，隔离出"衰减长度"这一项
		double k3 = KmPerHopOf(3), k4 = KmPerHopOf(4);
		Assert.That(k4, Is.LessThan(k3), "res 越高一格越小 ⇒ 每跳的物理长度必须更短");

		// ① 同一**物理**内陆深度（≈450 km）在两档 res 应给出**同一**系数
		int h3 = (int)Math.Round(PrecipitationModel.CoastDecayKm / k3);
		int h4 = (int)Math.Round(PrecipitationModel.CoastDecayKm / k4);
		Assert.That(h3, Is.Not.EqualTo(h4),
			"两档 res 的『一跳』物理长度不同 ⇒ 同样的 450 km 对应的跳数**必须**不同；"
			+ "若相同，说明衰减长度又被写成了跳数");
		float f3 = PrecipitationModel.CoastFactor(h3 + 1, k3, lat);
		float f4 = PrecipitationModel.CoastFactor(h4 + 1, k4, lat);
		Assert.That(MathF.Abs(f3 - f4), Is.LessThan(0.05f),
			$"同一物理内陆深度（≈450 km）在 res3/res4 应给出同一系数（实测 {f3:F4} / {f4:F4}）");

		// ② 反证：写死跳数（D-14 修复前的做法）会让同一"10 跳"在两档 res 的物理含义差 2.6×
		Assert.That(MathF.Abs(10f * (float)k3 - 10f * (float)k4), Is.GreaterThan(700f),
			"反证：res3 的 10 跳 ≈1,196 km，res4 的 10 跳 ≈452 km ⇒ 差 2.6×");
		Assert.That(MathF.Abs(PrecipitationModel.CoastFactor(11, k3, lat)
							- PrecipitationModel.CoastFactor(11, k4, lat)), Is.GreaterThan(0.05f),
			"反证：写死 10 跳时两档 res 的系数显著不同 ⇒ 物理含义随 res 漂移（被禁做法）");
	}

	// ── ⑤ 系数本身：单调、有界、退化解、纬度语义 ──────────────────────────────

	[Test]
	public void CoastFactor_Monotone_AndBoundedByInteriorRatio()
	{
		double k = KmPerHopOf(4);
		foreach (float lat in new[] { 0f, 25f, 45f, 75f })
		{
			float floor = PrecipitationModel.InteriorRatio(lat);
			float prev = float.MaxValue;
			for (int hops = 1; hops <= 60; hops++)
			{
				float v = PrecipitationModel.CoastFactor(hops, k, lat);
				Assert.That(v, Is.LessThanOrEqualTo(prev + 1e-6f), $"lat={lat} 深度 {hops} 跳：系数须单调不增");
				Assert.That(v, Is.InRange(floor - 1e-5f, 1f + 1e-5f),
					$"lat={lat} 深度 {hops} 跳：系数须落在 [{floor:F2}, 1.0]（E2：下限 = 该纬度的深内陆渐近比）");
				prev = v;
			}
		}
	}

	[Test]
	public void CoastalPenetration_IsPureExponential_AndLatitudeIndependent()
	{
		double k = KmPerHopOf(4);
		// 闭式重算：penetration(d, k) ≡ exp(−(d−1)·k / CoastDecayKm)
		foreach (int d in new[] { 1, 2, 5, 10, 30 })
		{
			double expected = Math.Exp(-Math.Max(0, d - 1) * k / PrecipitationModel.CoastDecayKm);
			Assert.That(PrecipitationModel.CoastalPenetration(d, k), Is.EqualTo(expected).Within(1e-5),
				$"d={d}：渗透系数须严格等于闭式 exp(−内陆km/CoastDecayKm)");
		}
	}

	[Test]
	public void InteriorRatio_IsLatitudeDependent_SubtropicalDriest()
	{
		float eq = PrecipitationModel.InteriorRatio(0f);
		float sub = PrecipitationModel.InteriorRatio(25f);
		float mid = PrecipitationModel.InteriorRatio(45f);
		float pol = PrecipitationModel.InteriorRatio(90f);
		Assert.That(sub, Is.LessThan(mid), "副热带深内陆须比中纬深内陆更干（下沉气流）");
		Assert.That(sub, Is.LessThan(eq), "副热带深内陆须比赤道深内陆更干（对流性降水）");
		Assert.That(pol, Is.GreaterThan(sub));
		Assert.That(sub, Is.InRange(0.02f, 0.30f),
			$"副热带渐近比须低到能造出荒漠（实测 {sub:F2}）；旧单一底板 0.6 正是「无荒漠」的根因");
		foreach (float lat in new[] { 0f, 25f, 45f, 90f })
			Assert.That(PrecipitationModel.InteriorRatio(lat), Is.InRange(0f, 1f));
	}

	[Test]
	public void CoastFactor_DegeneratesToUnity_WhenAllLandIsCoastal()
	{
		// ★退化解（永久原则 4）：全陆临海（离岸 1 跳 ⇒ 内陆深度 0 km）⇒ 系数 ≡ 1.0
		//   ⇒ **逐点退化到 D-14 修复前的行为**（那时因字段选错恰好也恒为 1.0），
		//      也等价于"没有大陆性、全纬向场"。这条同时说明：修复前的"恒 1.0"
		//      不是设计意图，而是"全世界都成了海岸"的巧合。
		foreach (float lat in new[] { 0f, 25f, 45f, 90f })
		{
			Assert.That(PrecipitationModel.CoastFactor(1, KmPerHopOf(1), lat), Is.EqualTo(1f).Within(1e-6));
			Assert.That(PrecipitationModel.CoastFactor(1, KmPerHopOf(4), lat), Is.EqualTo(1f).Within(1e-6));
			Assert.That(PrecipitationModel.CoastFactor(0, KmPerHopOf(4), lat), Is.EqualTo(1f).Within(1e-6),
				"离岸 0 跳（防御：海格/异常值）同样取 1.0，不产生 >1 的系数");
		}
	}

	[Test]
	public void CoastFactor_RejectsNonPositiveKmPerHop()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => PrecipitationModel.CoastFactor(3, 0.0, 45f));
		Assert.Throws<ArgumentOutOfRangeException>(() => PrecipitationModel.CoastFactor(3, -1.0, 45f));
		Assert.Throws<ArgumentOutOfRangeException>(() => PrecipitationModel.CoastalPenetration(3, 0.0));
	}

	// ── ⑥ 基本不变量 ────────────────────────────────────────────────────────

	[Test]
	public void AnnualMm_InAbsoluteRange_NoNaN_AndMeanPositive()
	{
		var (_, p) = Make();
		float hi = PrecipitationModel.ZonalMm(0f);   // 赤道纬向基准 = 场上界（海洋性系数 ≤ 1）
		Assert.That(p.AnnualMm, Has.All.InRange(0f, hi + 1e-3f));
		Assert.That(p.AnnualMm, Has.None.Matches<float>(float.IsNaN), "降水事实不得含 NaN");
		Assert.That(p.MeanMm, Is.GreaterThan(0f));
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
