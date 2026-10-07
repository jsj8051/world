namespace World.WorldGen;

// 世界生成空间 · **风场参数**（Phase 4 · ⑯ WindField **批次 1**；契约 `docs/裁决-WindField架构契约.md`、
// 设计 `docs/设计-WindField实现设计.md` §六）。
//
//   ★纪律（与 `ClimateParameters` 完全同款）：
//     ① 集中管理；② 每个参数**显式命名它所服务的响应函数**（禁笼统概念名，P4-5a 口径教训）；
//     ③ 一律**几何 B（度/km）或物理 C（m/s）**量纲，**跨 res 不变**；④ 每个"行为"参数必配**关断值**（永久原则 4）。
//   ★批次纪律：本批只登记批次 1（带边界/带分类/Coriolis/基速）消费的参数；
//     地形（设计 §4.4）与季风（§4.5）参数随各自实现批次进入，**不预付**（防膨胀三问）。

/// <summary>三圈环流带分类（批次 1；带符号纬度 + 月度带边界判定）。</summary>
public enum WindBelt
{
	/// <summary>信风带（Hadley 低层支；基矢朝 ITCZ 一侧，Coriolis 偏转后为信风）。</summary>
	Trade,
	/// <summary>中纬西风带（Ferrel 低层支；基矢向极）。</summary>
	Westerly,
	/// <summary>极地东风带（Polar 低层支；基矢向赤道）。</summary>
	PolarEasterly,
	/// <summary>赤道无风带（ITCZ ±<see cref="WindParameters.DoldrumHalfWidthDeg"/>；无经向基矢）。</summary>
	Doldrums,
}

// 世界生成空间 · 风场常量（⑯ 批次 1）。
public static class WindParameters
{
	// ── 带边界（B 类：度；设计 §4.1）──────────────────────────────────────────

	/// <summary>
	/// ITCZ 气候态平均纬度（地球 ~5°N）。
	/// </summary>
	public const float ItczMeanDeg = 5f;

	/// <summary>
	/// ITCZ **季节位移振幅**：`φ_ITCZ(m) = ItczMeanDeg + Amp·cos(2π(m−6)/12)`。
	/// 依据（地球）：7 月北跳 / 1 月南压至 ~2–5°S，单曲线振幅取 10°。
	/// ★**关断值 = 0** ⇒ φ_ITCZ 全年恒 5°N（退化矩阵 D1 的日历半边）。
	/// </summary>
	public const float ItczSeasonalAmpDeg = 10f;

	/// <summary>
	/// ITCZ **半球热异常修正**斜率（°/°C）：Δφ = Gain·(T̄′_NH − T̄′_SH)。
	/// 承契约 W-M2"有限幅度修正"——用 P4-5b 既有异常事实，**不另造热模型**。
	/// ★**关断值 = 0** ⇒ 无异常修正（D1 的异常半边；异常关闭还可由调用方传 (0,0) 实现）。
	/// </summary>
	public const float ItczAnomalyGainDegPerC = 1f;

	/// <summary>
	/// 半球热异常修正的**幅度上限**（度；契约 W-M2"有限幅度"的量化）。
	/// </summary>
	public const float ItczAnomalyCorrectionMaxDeg = 5f;

	/// <summary>
	/// φ_ITCZ 的**总钳位**（度；±15）：日历项 + 异常修正之和不得超过，防季风性北跳越界到 v1 语义外。
	/// </summary>
	public const float ItczLatClampDeg = 15f;

	/// <summary>北半球副热带高压脊气候态纬度（地球 ~30°N）。SH 取镜像 −30° + 同号漂移。</summary>
	public const float RidgeMeanDeg = 30f;

	/// <summary>北半球极锋气候态纬度（地球 ~60°N）。SH 取镜像 −60° + 同号漂移。</summary>
	public const float PolarFrontMeanDeg = 60f;

	/// <summary>
	/// 副热带脊/极锋随 ITCZ 位移的**同向漂移系数**：φ_ridge(m) = ±30 + f·(φ_ITCZ(m) − 5)。
	/// 依据：7 月 ITCZ 北跳时 SH 副高脊向赤道方向（北）退——同号漂移两个半球都物理正确。
	/// ★**关断值 = 0** ⇒ 脊/锋全年固定（D1 的边界半边）。
	/// </summary>
	public const float RidgeShiftFactor = 0.3f;

	/// <summary>赤道无风带半宽（度）：|φ − φ_ITCZ| &lt; 半宽 ⇒ Doldrums。</summary>
	public const float DoldrumHalfWidthDeg = 3f;

	// ── Coriolis 偏转（B 类：度；设计 §4.3，确定性规则，不进时间积分）────────────

	/// <summary>
	/// 偏转角上限：δ(φ) = MaxDeg·tanh(ShapeK·sin|φ|)。
	/// ★**关断值 = 0** ⇒ 无 Coriolis（纯经向基矢，测试锚）。
	/// </summary>
	public const float DeflectionMaxDeg = 75f;

	/// <summary>tanh 形状系数（地球锚三点：δ20°≈44.6° / δ45°≈66.6° / δ70°≈71.6°；O-W2 精化）。</summary>
	public const float CoriolisShapeK = 2f;

	// ── 带基础风速（C 类：m/s；设计 §4.6；★拍板 ③：速度与方向解耦）──────────────

	/// <summary>
	/// 信风带基速（m/s，海洋上；地球地面风气候态 5–8）。
	/// ★**拍板 ③（2026-10-07）**：速度只由带分类决定；季风/地形批次**只改方向或乘衰减因子，
	/// 永不改写本基值**——保证 W5 退化、统计量口径与 P4-5d 风应力接口干净。
	/// ★**关断值 = 全部基速为 0** ⇒ 静风球（极端退化锚）。
	/// </summary>
	public const float BaseTradeSpeedMs = 6f;

	/// <summary>西风带基速（m/s；地球 8–11 取偏保守 9）。</summary>
	public const float BaseWesterlySpeedMs = 9f;

	/// <summary>极地东风带基速（m/s；地球 3–5）。</summary>
	public const float BasePolarSpeedMs = 4f;

	/// <summary>赤道无风带速度衰减（相对信风基速；无风带量级 ~2–3 m/s）。</summary>
	public const float DoldrumSpeedFactor = 0.4f;
}
