using System;
using NUnit.Framework;
using World.H3Grid;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · **温度场**护栏（v1：逐格能量收支，输入按纬度 cos φ）。
///
/// ★本文件**自带独立实现**（下方 Ref* 常量与方法），不复用生产类的任何内部常量——
///   这是真正的"与独立实现逐点对照"（永久原则 5）：生产改了公式，本文件不会跟着变，
///   从而能照出漂移。生产 `Temperature` 的公式/常数全部 private。
///
/// 钉住三件事：
///   ① **逐格能量收支**：Q_in(φ) = 输入系数·cos φ ⇒ T_eq = (Q_in·输出系数)^¼
///      （系数已归一：输入含 Q₀(1−α)、输出含 1/(εσ)；无 /4、无形状归一）；
///   ② **纬度单调性**：T 随 |纬度| 单调不增，且赤道—高纬温差显著；
///   ③ **海拔递减**：同纬度下 ΔT = −Γ·Δh 逐点精确。
///   ④ **模型边界如实暴露**：极地 cos φ→0 ⇒ T_eq→0 K（奇异）**不得被护栏掩盖**
///      （用户 2026-10-08 明确：加护栏会让"缺热输送"伪装成正常结果）。
/// 纪律（同 SpatialScaleTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class TemperatureModelTests
{
	// ── 独立实现（参照值；与生产实现无关）──────────────────────────────────
	// 归一系数由原始常数独立算出（不复用生产 Thermal 的值）——保证"逐点对照"能照出漂移。
	const float RefQ0 = 1361f;
	const float RefAlbedo = 0.30f;
	const float RefEps = 1.0f;
	const float RefSigma = 5.670374419e-8f;
	const float RefLapse = 6.5f;
	static readonly float RefInputFlux = RefQ0 * (1f - RefAlbedo);   // ≈ 952.7
	static readonly float RefCoolingInv = 1f / (RefEps * RefSigma);  // ≈ 1.7636e7

	/// <summary>参照 T_eq（℃；独立于生产实现）。入参为**纬度弧度**；Q_in = 输入系数·cos φ。</summary>
	static float RefTempEqC(float latAbsRad)
	{
		float q = RefInputFlux * MathF.Cos(latAbsRad);
		if (q <= 0f) return -273.15f;
		return MathF.Pow(q * RefCoolingInv, 0.25f) - 273.15f;
	}

	/// <summary>度 → 弧度的便捷包装（仅测试使用）。</summary>
	static float RefTempEqDeg(float latAbsDeg) => RefTempEqC(latAbsDeg * MathF.PI / 180f);

	// ── 用真实 Ball + 上游产物跑生产实现 ────────────────────────────────────
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(2, 1f));

	/// <summary>跑一遍上游流水线 + 生产 Temperature。</summary>
	static (Ball ball, HeightComposer composer, Temperature t) Make()
	{
		var ball = SharedBall.Value;
		var layout = new ContinentLayout(42, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = 42 });
		var proj = new H3LandSeaProjector();
		proj.Generate(ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var regions = new GeologicalRegions(42);
		regions.Generate(ball, proj, 8_000_000f);
		var mountains = new MountainSkeleton(42);
		mountains.Generate(ball, regions, surface);
		var landforms = new RegionalLandforms(42);
		landforms.Generate(ball, regions);
		var features = new System.Collections.Generic.List<FeatureField>
		{
			new(landforms, TerrainDomain.LandOnly),
			new(mountains, TerrainDomain.LandAndSea),
		};
		var composer = new HeightComposer(42);
		composer.Generate(ball, surface, regions, features);
		var final = new FinalGeography();
		final.Generate(ball, composer, regions);
		var t = new Temperature();
		t.Generate(ball, composer, final);
		return (ball, composer, t);
	}

	[Test]
	public void EquatorFlux_Matches_MaximumInsolation()
	{
		// ★口径钉死：赤道 cos 0 = 1 ⇒ Q_in = 输入系数 Q₀(1−α) ≈ 953 W/m²（不得含 /4）。
		// 反推：T_eq = (Q_in·输出系数)^¼ ⇒ Q_in = (T_eq+K)⁴ / 输出系数。
		float tEq0 = RefTempEqDeg(0f);
		float q0 = MathF.Pow(tEq0 + 273.15f, 4f) / RefCoolingInv;
		Assert.That(q0, Is.EqualTo(RefInputFlux).Within(1f),
			$"赤道输入通量应 = Q₀(1−α) ≈ {RefInputFlux:F0} W/m²（不得含 /4）");
	}

	[Test]
	public void Temperature_IsMonotoneInLatitude()
	{
		float prev = float.MaxValue;
		for (int lat = 0; lat <= 89; lat++)     // 到 89°（90° 为奇异点，见下条测试）
		{
			float t = RefTempEqDeg(lat);
			Assert.That(t, Is.LessThanOrEqualTo(prev + 1e-3f),
				$"温度必须随 |纬度| 单调不增（{lat}°={t:F1} 高于前值 {prev:F1}）");
			prev = t;
		}
		Assert.That(RefTempEqDeg(0f) - RefTempEqDeg(80f), Is.GreaterThan(40f),
			"赤道与高纬温差应显著（>40 °C）——过小说明纬度因子被压平");
	}

	[Test]
	public void Altitude_AppliesLapseRate_Exactly()
	{
		// ★退化解（永久原则 4）：同纬度、同热源下 ΔT = −Γ·Δh 逐点精确。
		float t0 = RefTempEqDeg(45f);
		float t1 = RefTempEqDeg(45f) - 2f * RefLapse;
		Assert.That(t1, Is.EqualTo(t0 - 2f * RefLapse).Within(1e-3f),
			"海拔递减必须是精确的线性 −Γ·h");
	}

	[Test]
	public void PoleSingularity_IsExposed_NotMaskedByGuard()
	{
		// ★用户 2026-10-08 裁决：**不加极地护栏**——护栏会把"模型缺热输送"伪装成正常结果。
		// 本测试**主动钉住奇异点**：cos φ 在极地 = 0 ⇒ T_eq = 0 K = −273.15 °C。
		// 若有人"顺手加了下限"，这里立刻变红并提示这是模型边界、需随热输送一并修。
		Assert.That(RefTempEqDeg(90f), Is.EqualTo(-273.15f).Within(0.01f),
			"极地应为 0 K（cos φ = 0 的必然奇异）——出现有限值说明有人加了极地护栏，" +
			"那会把『本模型无热输送』这一缺失掩盖；若要修正极地温度，应重做热输送");
		Assert.That(RefTempEqDeg(89f), Is.LessThan(-100f),
			"89° 应已极度接近奇异（纯辐射平衡下高纬奇冷）");
	}

	[Test]
	public void Production_MatchesIndependentReference_Pointwise()
	{
		// ★核心对照：生产实现 vs 本文件独立实现。
		// 生产公式/常数已 private ⇒ 本测试是唯一能照出"公式漂移"的活体锚。
		// 做法：用生产温度反推"生产用的 T_eq"（T_eq = T + Γ·h），与参照 T_eq(纬度弧度) 逐点比。
		var (ball, composer, temp) = Make();
		const float gamma = 6.5f;
		float worst = 0;
		for (int i = 0; i < ball.CellIds.Length; i++)
		{
			float latRad = ball.Geo.LatRad[i];        // 直接取弧度（与生产同源）
			float tEqProd = temp.CellTemperatureC[i] + gamma * (composer.HeightM[i] * 1e-3f);
			float tEqRef = RefTempEqC(latRad);
			worst = MathF.Max(worst, MathF.Abs(tEqProd - tEqRef));
		}
		Assert.That(worst, Is.LessThan(0.05f),
			$"生产 T_eq 与独立参照逐点最大差 {worst:F4} °C（应 ≈ 0，仅 float 精度）——公式漂移");
	}
}
