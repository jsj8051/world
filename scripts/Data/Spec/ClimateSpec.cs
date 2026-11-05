namespace World.Data;

/// <summary>
/// ② 气候阶段配置：行星几何 + 年基温曲线 + 月度气候 + 洋流四组旋钮。
///
/// 数值口径 = 旧气候链（`docs/气候反馈环.md`，2026-08 封版）的标定值，单位写在各属性上；
/// 每个旋钮都对应公式里的一处显式常数，没有隐式魔数（物理常数留在 <see cref="Thermal"/>）。
///
/// @example
/// <code>
/// var spec = new ClimateSpec { ObliquityDeg = 0f };   // 零倾角 ⇒ 无季节、无季风
/// var spec2 = new ClimateSpec { Insolation = 0.8f };  // 近恒星 ⇒ 全球升温且温差缩小
/// </code>
/// </summary>
public sealed class ClimateSpec
{
	// ── 行星 / 几何 ─────────────────────────────────────────────────────────

	/// <summary>一年几个月（月循环长度；当前模型按 12 个月标定，改它需同时重标定季节相位）。</summary>
	public int Months { get; set; } = 12;

	/// <summary>转轴倾角（°）：决定太阳直射点摆幅 ±倾角，是季节与季风的唯一来源。出厂 23.44（地球）。</summary>
	public float ObliquityDeg { get; set; } = 23.44f;

	/// <summary>自转速度（相对地球 24h = 1）：科里奥利偏转 ∝ 此值，决定信风/西风的倾角。</summary>
	public float RotationSpeed { get; set; } = 1.0f;

	/// <summary>自转方向：true = 自西向东（地球式），false = 逆转（金星式，风带镜像）。</summary>
	public bool Prograde { get; set; } = true;

	/// <summary>恒星辐照度倍率（1 = 地球 1 AU；接收能量 ∝ 1/d²）：0.8 AU ≈ 1.56 倍。</summary>
	public float Insolation { get; set; } = 1.0f;

	/// <summary>海拔递减率（°C/km；地球对流层实测 ~6.5，旧链按 6.0 标定）。</summary>
	public float LapseRateCPerKm { get; set; } = 6.0f;

	// ── 年基温（纬度曲线，无季节项）────────────────────────────────────────

	/// <summary>赤道纬度基准的升温幅度（°C）：`baseT = 本值·cos^纬度指数 − 极地冷值`。</summary>
	public float EquatorBaseC { get; set; } = 52f;

	/// <summary>极地纬度基准（°C；cos→0 时 baseT 收敛到它）。</summary>
	public float PolarBaseC { get; set; } = -22f;

	/// <summary>纬度衰减指数：&gt;1 让高纬降温更快（出厂 1.1 → 赤道 +30、极地 −22）。</summary>
	public float LatitudeExponent { get; set; } = 1.1f;

	/// <summary>倾角对高纬年均温的降温幅度（°C）：|lat|&gt;45° 起线性生效，90° 满幅。</summary>
	public float TiltCoolingC { get; set; } = 25f;

	/// <summary>辐照度偏离 1 AU 时的全球平移幅度（°C/倍率）。</summary>
	public float InsolationGlobalC { get; set; } = 24f;

	/// <summary>辐照度偏离时额外的赤道加权幅度（°C/倍率，权重 cos lat）——近恒星时温差增大。</summary>
	public float InsolationLatitudeC { get; set; } = 12f;

	/// <summary>大陆性噪声幅度（°C）：大尺度海陆热力差异的随机分量。</summary>
	public float ContinentalityNoiseC { get; set; } = 7f;

	/// <summary>大陆性噪声基波长（km；≈ 万 km 级）。</summary>
	public float ContinentalityWavelengthKm { get; set; } = 9000f;

	/// <summary>洋流对沿岸气温的修幅（°C，冷暖 × 强度 满值时；旧链 ±5）。</summary>
	public float OceanCurrentWarmthC { get; set; } = 5f;

	// ── 月温度（季节项 + 大陆性 + 反照率）──────────────────────────────────

	/// <summary>海洋季节半幅（°C，在 23.4° 倾角、|sin lat| = 1 时；实际 = 本值 × (倾角/23.4) × |sin lat|^1.5）。</summary>
	public float SeasonalBaseC { get; set; } = 25f;

	/// <summary>大陆热力响应系数：`1 + 本值 × 大陆性 × 月辐射` 放大季节摆幅（内陆冬冷夏热）。</summary>
	public float ContinentalResponse { get; set; } = 3.5f;

	/// <summary>大陆性特征长度（km）：`continent = sqrt(d/(d+Dc))`，d = 到海岸距离，渐近不饱和。</summary>
	public float ContinentalityLengthKm { get; set; } = 1000f;

	/// <summary>反照率全额反射时的降温标定（°C）：月温减去 `反照率 × 月辐射 × 本值`。</summary>
	public float AlbedoCoolingC { get; set; } = 10f;

	/// <summary>海洋反照率（吸收多，反射损失小）。</summary>
	public float AlbedoOcean { get; set; } = 0.06f;

	/// <summary>陆地默认反照率。</summary>
	public float AlbedoLand { get; set; } = 0.15f;

	/// <summary>高山积雪反照率（归一化海拔 &gt; <see cref="AlpineElevNorm"/> 的陆地）。</summary>
	public float AlbedoSnow { get; set; } = 0.45f;

	/// <summary>极地冰盖反照率（冰雪覆盖满格时；冰的覆盖比例由月温度决定，不由纬度决定）。</summary>
	public float AlbedoIceCap { get; set; } = 0.65f;

	/// <summary>开始结冰的月温度（°C）：高于它无冰。</summary>
	public float IceMeltC { get; set; } = 0f;

	/// <summary>完全结冰的月温度（°C）：低于它冰比例 = 1，中间平滑过渡（避免纬度圈上的硬边）。</summary>
	public float IceFullC { get; set; } = -8f;

	/// <summary>高山判据的归一化海拔阈值（0 = 海平面、1 = 本世界最高点）。</summary>
	public float AlpineElevNorm { get; set; } = 0.5f;

	// ── 湿润降温（温度 → 风 → 降水 → 温度 的唯一闭环）──────────────────────

	/// <summary>湿润降温系数（°C/mm 年降水）：1000 mm ≈ −4°C 量级。</summary>
	public float WetCoolingCPerMm { get; set; } = 0.004f;

	/// <summary>负反馈增益：修正量 = `k·P/(1+本值)`，自我抑制 30% 防止过冷。</summary>
	public float WetCoolingFeedback { get; set; } = 0.3f;

	// ── 气压（诊断场：纬向带 + 热力异常 + 副高东西不对称）────────────────

	/// <summary>陆海温差 → 气压异常的增益（hPa/°C）：陆暖则减压、陆冷则加压。</summary>
	public float PressureThermalGain { get; set; } = 1.2f;

	/// <summary>热力异常的长波平滑尺度（km）：让异常铺满一块大陆，而不是只贴海岸一条带。</summary>
	public float PressureThermalSmoothKm { get; set; } = 650f;

	/// <summary>副高东西不对称的幅度（hPa）：把副高中心推到大洋东侧。</summary>
	public float PressureAsymGain { get; set; } = 6f;

	/// <summary>副高东西不对称的平滑尺度（km）。</summary>
	public float PressureAsymSmoothKm { get; set; } = 300f;

	/// <summary>气压带随太阳的季移比例：`Δc = 本值 × 太阳赤纬`（0.3 ≈ ±7°，参考模型 6° 级）。
	/// 不直接用"热赤道"——本世界温度场的湿冷却与大陆放大把它推到 ±25° 以上，整条 p_z 会跟着跑。</summary>
	public float PressureBeltShiftFactor { get; set; } = 0.3f;

	/// <summary>季风强度的标定尺度（hPa）：冬夏 Δp 的极差达到它记 1（只在 Δp 反号时成立）。</summary>
	public float MonsoonStrengthScale { get; set; } = 30f;

	// ── 风（气压 → 摩擦定常解；三套闭合共用同一梯度）──────────────────────

	/// <summary>海面线性摩擦系数 r（s⁻¹）：越小越接近地转风、风速越大；陆上乘 <see cref="FrictionLandFactor"/>。</summary>
	public float FrictionSurface { get; set; } = 4e-5f;

	/// <summary>陆地摩擦倍数：陆上风速更小、风向更偏向低压。</summary>
	public float FrictionLandFactor { get; set; } = 2f;

	/// <summary>低空风摩擦倍数（乘海面 r）：水汽输送层，摩擦小于地面。</summary>
	public float FrictionLowFactor { get; set; } = 0.5f;

	/// <summary>自由低层风摩擦倍数（乘海面 r）：洋流风应力层，三套里摩擦最小。</summary>
	public float FrictionFreeFactor { get; set; } = 0.3f;

	/// <summary>显示风速软饱和的特征风速（m/s）：`1 − exp(−|v| / 本值)`。</summary>
	public float WindSpeedRefMs { get; set; } = 12f;

	/// <summary>同纬度基准分桶数（36 = 5° 一带），用于算海陆温差相对同纬海洋均值的偏差。</summary>
	public int LatitudeBuckets { get; set; } = 36;

	// ── 水汽 / 降水 ────────────────────────────────────────────────────────

	/// <summary>水汽上游追踪的最大步数（迎风水汽来源判定）。</summary>
	public int MoistureTraceSteps { get; set; } = 25;

	/// <summary>每步水汽衰减率（exp(−本值·步数)）：越大水汽越难深入内陆。</summary>
	public float MoistureDecayPerStep { get; set; } = 0.08f;

	/// <summary>季风对月降水的放大系数（降水 × (1 + 本值 × 水汽 × 月辐射)）。</summary>
	public float MonsoonPrecipBoost { get; set; } = 1.2f;

	/// <summary>月降水骨架的全球底值（mm/月；极地 / 副极地的少雨底）。</summary>
	public float PrecipBaseMmPerMonth { get; set; } = 30f;

	/// <summary>月 ITCZ 带峰值（mm/月）：赤道辐合带的对流降水。</summary>
	public float PrecipItczMmPerMonth { get; set; } = 235f;

	/// <summary>月极锋带峰值（mm/月，60° 附近）。</summary>
	public float PrecipPolarFrontMmPerMonth { get; set; } = 100f;

	/// <summary>副热带高压下沉带的降水压制强度 ∈[0,1]（0 = 无压制，1 = 压到零）。</summary>
	public float PrecipSubtropicalSuppression { get; set; } = 0.85f;

	/// <summary>降水噪声幅度（相对量，±）：只作小扰动，不得盖过风带/雨影信号。</summary>
	public float PrecipNoiseAmount { get; set; } = 0.12f;

	/// <summary>降水噪声基波长（km）。</summary>
	public float PrecipNoiseWavelengthKm { get; set; } = 4000f;

	/// <summary>地形雨影增益：`1 + clamp(风·∇h × 本值, −0.65, +0.65)`（迎风增雨 / 背风减雨）。</summary>
	public float OrographicGain { get; set; } = 0.5f;

	// ── 洋流（流函数法）─────────────────────────────────────────────────────

	/// <summary>流函数求解的基准迭代次数（按网格规模自动放大）。</summary>
	public int OceanIterations { get; set; } = 300;

	/// <summary>SOR 松弛因子（1.7 时收敛比 Gauss-Seidel 快 3–5 倍）。</summary>
	public float OceanSorOmega { get; set; } = 1.7f;

	/// <summary>科里奥利 β 效应强度（Sverdrup：ψ ∝ curl/β；0 = 关断）。</summary>
	public float OceanBetaScale { get; set; } = 1f;

	/// <summary>热成风（温度梯度驱动）在风应力旋度里的权重。</summary>
	public float OceanThermalWindWeight { get; set; } = 0.15f;

	/// <summary>流函数梯度 → 洋流强度的增益（越大越容易达到满强度）。</summary>
	public float OceanStrengthGain { get; set; } = 8f;

	/// <summary>复制全部公开可写属性到 <paramref name="target"/>（不动实例身份）。</summary>
	public void CopyTo(ClimateSpec target) => SpecCopy.CopyProperties(this, target);
}
