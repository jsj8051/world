using System;

namespace World.NewHexWorld.Plate
{
	// 岩石圈热柱（设计-05 §2）：把**洋壳冷却 → 洋底沉降 / 驱动浮力 / 地表热流**三件事收进**同一个**
	// 半空间冷却解析解，取代此前三套互不相干的参数化（`H3Isostasy` 的 350 m/√My 常数、
	// `H3PlateFields.MaficDensityAtAge` 的 2890→3300 经验链、`H3PlateMotion` 的常数黏度）。
	//
	// 物理（Turcotte & Schubert §4.3 半空间冷却）：T(z,t) = T_m·erf(z/(2√(κt)))
	//   ① 洋底沉降    d(t) = (2ρ_m α T_m/(ρ_m − ρ_w))·√(κt/π)          [m]
	//   ② 深度积分密度亏损 N(t) = ρ_m α T_m·2√(κt/π) = (ρ_m − ρ_w)·d(t)  [kg/m²] ← **驱动的物理量**
	//   ③ 地表热流    q(t) = k T_m/√(πκt)                              [W/m²]  ← 地幔热收支的支出项
	//   ④ 黏度        η(Tp) = η_ref·exp(E/R·(1/Tp − 1/T_ref))（Arrhenius）
	// 地幔势温 Tp 一降：α·T_m 项变小（驱动变弱）、η 指数变大（同样力的响应变慢）——"发动机缓慢熄火"
	// 在模型里就是这么长出来的（见 05 §3 的热收支）。
	//
	// ⚠️ 与老口径的差（05 §2，**这是本模块存在的理由**）：老实现把负浮力挂在 **7 km 洋壳**上，
	// 于是必须把"洋壳密度"从 2890 抬到 3300（+14%！真实洋壳永远到不了，模型自己的地幔才 3075）
	// 才凑得出量级。真实负浮力在 **~100 km 冷岩石圈地幔**上，密度盈余只有 1–2%：
	// 实测对比 N（250 My）——老口径 225 kg/m³ × 7000 m = 1.6e6；热柱 1.29e7 → **低 8×**；
	// 而且老口径在 age < 113 My 时恒为 0（阈值型），热柱从脊轴起按 √t 连续增长。
	//
	// 单位纪律：本类只做**物理量换算**（m / kg/m² / W/m² / Pa·s），不碰场、不碰板号、无状态 ⇒
	// 纯函数、零引擎依赖、可单测（与 SphericalFbmNoise 同款约束）。
	public static class H3ThermalColumn
	{
		// ── 物理常量（地球真值；改这里必须同步 05 §4 参数表与 H3Isostasy 的锚点单测）──
		public const float ThermalExpansivity = 3e-5f;        // α 体积热膨胀系数（1/K）
		public const float ThermalDiffusivityM2PerS = 1e-6f;  // κ 热扩散率（m²/s）
		public const float MantleDensityKgM3 = 3300f;         // ρ_m
		public const float WaterDensityKgM3 = 1000f;          // ρ_w
		public const float AsthenosphereTempK = 1300f;        // T_m（软流圈与地表的温差，K）
		public const float ConductivityWPerMK = 3f;           // k 热导率（W/(m·K)）
		public const float SecondsPerMy = 3.1558e13f;         // 1 My（与 Units.MEGAYEAR 同源）
		/// <summary>年龄下限（My）：半空间解在 t→0 发散（q→∞、h→0），取 1 My 封顶——
		/// 脊轴格（age = 0）按 1 My 计。</summary>
		public const float MinAgeMy = 1f;

		// √(κt)：**不设下限**——沉降与密度亏损在 t→0 本就有界（→0：脊轴格不产生负浮力，物理正确）；
		// 只有地表热流 1/√t 在 t→0 发散，那一条单独封顶（见 HeatFlowWPerM2）。
		static float SqrtKappaT(float ageMy)
			=> MathF.Sqrt(ThermalDiffusivityM2PerS * SecondsPerMy * MathF.Max(ageMy, 0f));

		/// <summary>洋底沉降（m，相对脊轴基准）——与 `H3Isostasy.SubsidenceCoefM`(350 m/√My) 同源。</summary>
		public static float SubsidenceM(float ageMy)
			=> 2f * MantleDensityKgM3 * ThermalExpansivity * AsthenosphereTempK
			   / (MantleDensityKgM3 - WaterDensityKgM3) * SqrtKappaT(ageMy) / MathF.Sqrt(MathF.PI);

		/// <summary>沉降律系数（m/√My）——解析值 ≈ 354.6，代码常量取整为 350（差 1.3%）；
		/// 单测把两者钉在一起（改动任一必须同步，防"两条沉降律漂移"）。</summary>
		public static float SubsidenceCoefMPerSqrtMy()
			=> 2f * MantleDensityKgM3 * ThermalExpansivity * AsthenosphereTempK
			   / (MantleDensityKgM3 - WaterDensityKgM3)
			   * MathF.Sqrt(ThermalDiffusivityM2PerS * SecondsPerMy / MathF.PI);

		/// <summary>深度积分密度亏损 N（kg/m²）——**驱动力**的物理量（= ρ_m·α·∫温度亏损 dz）。
		/// 与 `(ρ_m − ρ_w)·SubsidenceM(age)` 严格相等（同一解的两种写法，单测互检）。
		/// <paramref name="temperatureScale"/> = 当前势温/参考势温（α·T_m 项随势温线性缩，见 05 §3）。</summary>
		public static float NegativeBuoyancyKgPerM2(float ageMy, float temperatureScale = 1f)
			=> MantleDensityKgM3 * ThermalExpansivity * AsthenosphereTempK * temperatureScale
			   * 2f * SqrtKappaT(ageMy) / MathF.Sqrt(MathF.PI);

		/// <summary>热岩石圈厚度（m）：1300 ℃ 等温线深度 ≈ 2.32·√(κt)（erf 剖面 0.85·T_m 处）。</summary>
		public static float LithosphereThicknessM(float ageMy) => 2.32f * SqrtKappaT(ageMy);

		/// <summary>地表热流（W/m²）：k·T_m/√(πκt)（半空间冷却的地表热流；10 My ≈ 100 mW/m²）。
		/// ⚠️ 唯一需要年龄下限的一条（t→0 发散）；脊轴格按 `MinAgeMy` 计。</summary>
		public static float HeatFlowWPerM2(float ageMy)
			=> ConductivityWPerMK * AsthenosphereTempK
			   / (MathF.Sqrt(MathF.PI) * SqrtKappaT(MathF.Max(ageMy, MinAgeMy)));

		/// <summary>Arrhenius 黏度（Pa·s）：η = η_ref·exp(E/R·(1/T − 1/T_ref))。
		/// Tp 降 → η 升 → 同样驱动力下板速降（"发动机缓慢熄火"的响应半边）。
		/// E 是旋钮：橄榄石蠕变实测 300–500 kJ/mol；取 400 时 40 K 降温 ⇒ η ×2.1。</summary>
		public static float ViscosityPaS(float potentialTemperatureK, float referenceTemperatureK,
			float referenceViscosityPaS, float activationEnergyJPerMol)
		{
			const float GasConstant = 8.314f;
			if (potentialTemperatureK <= 1f) return referenceViscosityPaS;
			float exponent = activationEnergyJPerMol / GasConstant
				* (1f / potentialTemperatureK - 1f / referenceTemperatureK);
			return referenceViscosityPaS * MathF.Exp(Math.Clamp(exponent, -20f, 20f));
		}
	}
}
