namespace World.WorldGen;

// 世界生成空间 · **气候参数**（Phase 4 · **P4-5b**；契约 §二.2）。
//
//   ★为什么单独一个类：契约冻结条款要求参数**集中管理**，且——
//     ★★**口径教训（P4-5a 明确登记）**：`TemperatureModel.ContinentalityScaleKm = 1200`
//     （年均温海陆差）与 `SeasonalClimateModel.SeasonalContinentalityScaleKm = 500`
//     （年较差）已被实测证明是**两个不同响应函数**，却曾共用"大陆性"这一个直觉概念。
//     ⇒ 本类的每个参数必须**显式命名它所服务的响应函数**；**不允许**用笼统概念命名。
//
//   ── 参数分类（承永久原则 2）─────────────────────────────────────────────────
//   · **物理常数**：不随 res 变、不随世界变（如黄赤交角）；放在 `ClimateForcing`。
//   · **工程标定**：本类成员，一律是**几何 B（km）或物理 C（°C / 月 / W·m⁻²）**量纲，
//     **跨 res 不变**；每一个都必须有**关断值**（承永久原则 4：退化解）。
//
//   ── P4-5b 的三个参数（本阶段**全部**参数，刻意极少）─────────────────────────
//   ① `SeasonalGainCPerWm2`      辐射年循环异常 → **平衡温度**异常的标定系数
//                                （吸收了大气衰减、长波反馈等一系列过程 ⇒ 是**标定值不是常数**）
//   ② `LandResponseTimeMonths`   陆面一阶热容的**响应时间** τ（月）
//   ③ `OceanResponseTimeMonths`  海洋混合层一阶热容的响应时间 τ（月）
//   ★**本阶段不含任何"离岸距离 / 大陆性"参数**——海陆差异只由 τ 的差别产生，
//     大陆性（海岸↔内陆的季节差异）属后续阶段（P4-5a 契约已把该数值的拍板推迟）。
public static class ClimateParameters
{
	/// <summary>
	/// 季节增益（°C per W/m²）：`Teq(m) = T_ann + Gain · (S(lat,m) − S̄(lat))`。
	///
	/// 标定依据（`scratch/p4_5b_solar_calib.py` 反解，**不是拟合 Legacy 分布**）：
	///   目标 = **45°N 陆地的年较差 = 30.0 °C**（地球实测：北京 39.9°N **30.4**、芝加哥 41.8°N **28**）；
	///   在 `τ_land = 1.0` 月下，Gain = 1 时的响应振幅 = **381.5 °C**（= 381.5 W/m² 的强迫振幅）
	///   ⇒ `Gain = 30.0 / 381.5 = 0.0786 °C/(W/m²)`。
	///
	/// ★**关断值 = 0** ⇒ `Teq(m) ≡ T_ann` ⇒ 12 个月**逐点精确**退化到年均温度事实（永久原则 4）。
	/// </summary>
	public const float SeasonalGainCPerWm2 = 0.0786f;

	/// <summary>
	/// **陆面**热响应时间 τ（月）：一阶热容 `C dT/dt = (Teq − T)` 的时间尺度。
	/// 标定依据（同脚本）：取 1.0 月 ⇒ 相位滞后 `atan(2πτ/12) ≈ 0.92` 月
	///   ⇒ **45°N 陆地最热月 = 7 月**（与北京/芝加哥一致；夏至在 6 月）。
	/// </summary>
	public const float LandResponseTimeMonths = 1.0f;

	/// <summary>
	/// **海洋**热响应时间 τ（月）——海陆热惯性差异的**唯一**来源（本阶段）。
	/// 标定依据（同脚本）：`τ_ocean = 8.0` 月 ⇒ 45°N 海洋年较差 **7.5 °C**、最热月 **9 月**，
	///   陆/海振幅比 **4.01**（地球 45–51°N 实测比 ≈ **4.0**：Valentia 51.9°N amp 8.4、
	///   中太平洋同纬 ~6–9，对陆地 30）。
	/// ★取 6/7/9/10 月的对照也在脚本里（比值 3.00 / 3.49 / 4.53 / 5.05）⇒ 8.0 落在实测正中。
	/// </summary>
	public const float OceanResponseTimeMonths = 8.0f;

	/// <summary>
	/// 季节强迫的**关断值**（承永久原则 4）：Gain = 0 ⇒ 无季节 ⇒ 12 个月恒等于年均温度。
	/// </summary>
	public const float SeasonalGainOff = 0f;

	/// <summary>
	/// 热响应的**关断值**：τ → 0⁺ ⇒ 温度**瞬时跟随**平衡温度（无惯性、无滞后）。
	/// 实现上取一个足够小的正数（1e-3 月 ⇒ 滞后 < 0.01°），不取 0（会导致除零）。
	/// </summary>
	public const float InstantaneousResponseMonths = 1e-3f;

	// ── P4-5c · 最小水循环（Coupled Solver 第一条耦合链；2026-10-07）──────────────
	// ★定性（契约 §九）：P4-5c 是**架构升级阶段**（多分量 State + 迭代骨架 + 耦合成立），
	//   **不是**气候学参数大标定 ⇒ 本组参数刻意极少、每个都有明确占位依据与关断值。

	/// <summary>
	/// **蒸发增益**（mm/月/°C）：海面潜在蒸发 `E = Gain · max(0, T)`（线性、冰点为 0）。
	/// 占位依据（地球量级，**P4-5e 对齐时再校**）：海面年均温 18~25 °C ⇒ 蒸发 1.0~1.3 m/年
	///   ⇒ 5~6 mm/月/°C（热带 27 °C ⇒ ~1.9 m/年，地球实测 1.8~2.0 ✓）。
	/// ★**陆格蒸发 ≡ 0**（本阶段无陆面水文/土壤水/植被 —— **登记的能力边界**，
	///   不是缺陷；全球水汽只由海面供给）。
	/// ★**关断值 = 0** ⇒ 水循环分量整体关闭 ⇒ `ClimateState` 精确退化到 P4-5b 形态。
	/// </summary>
	public const float EvapGainMmPerMonthC = 6f;

	/// <summary>
	/// **水汽滞留时间** τ_p（月）：降水 = `W / τ_p`（一阶凝结移除）。
	/// 依据（物理量级，非标定）：地球可降水量 ~25 kg/m²、降水 ~2.6 mm/日
	///   ⇒ 滞留 ~9-10 天 ≈ **0.3 月**。
	/// </summary>
	public const float WaterVaporResidenceMonths = 0.3f;

	/// <summary>
	/// **水汽扩散时间尺度** τ_mix（月）：通量 `F_ij = (W_j − W_i) / (6·τ_mix)`
	///   （六邻接，通量形式 ⇒ **逐格求和精确相消** ⇒ 全球水量严格守恒）。
	/// ★占位：这是"输送"的**极简具象**（真实风场平流 = P4-5d+ 课题）；
	///   取 2 月 ⇒ 每步通量系数 1/12，扩散特征值稳定（dt·k·λ_max = 1 ≤ 2）。
	/// </summary>
	public const float VaporDiffusionTimescaleMonths = 2f;

	// ── P4-5d · 洋流温度影响（D3 降级版；docs/裁决-D3降级-洋流温度影响.md，2026-10-07）──
	// ★定性：洋流作为**方向性温度影响**把上游海区的温度**季节结构**向下游传播
	//   （月度场一次性后处理），**不是**热量守恒输运——边通量/CFL/零和机制已删除。
	//   依赖方向单向不可逆：`Ocean circulation field → Temperature`；
	//   **禁止 Ocean Current 重定义 T_ann / 太阳强迫**（条款 10）——影响逐格扣 12 月均值，
	//   `T_ann` 年均锚（annualMeanC 输入）**逐格精确**不动（强形式恒等式 Σ_m ΔT = 0）。

	/// <summary>
	/// **洋流温度影响增益**（无量纲，生产值 = 1）：
	/// `ΔT_i(m) = Gain · Speed/(Speed+v_half) · (T_up(m) − T_i(m))`，随后逐格扣 12 月均值。
	/// ★**关断值 = 0**（承永久原则 4）⇒ 温度分量精确回到 P4-5b/P4-5c 闭式形态
	///   （`CoupledClimateSolver.Solve` 的 `currentInfluenceGain` 参数默认 0 = P4-5c 冻结基线；
	///   生产接线在 <see cref="WorldGenPlanet"/> 显式传本值）。
	/// </summary>
	public const float OceanCurrentInfluenceGain = 1f;

	/// <summary>
	/// **影响饱和半速** v_half（km/月）：`Influence = Speed / (Speed + v_half)`（饱和函数，
	/// 弱流 ≈ Speed/v_half → 0、强流 → 1；随 Speed 单调，取代已删除的 CFL 夹子口径）。
	/// ★占位 10 km/月（量级锚：1× τ₀ 下流动格 p98 ≈ 8.7 km/月）；P4-5e/O-O2 标定旋钮；
	///   v_half = ∞ ⇒ Influence ≡ 0（数学关断）。
	/// </summary>
	public const float CurrentInfluenceHalfSpeedKmPerMonth = 10f;
}
