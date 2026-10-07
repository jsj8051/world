using System;

namespace World.WorldGen;

// 世界生成空间 · **风场模型（批次 1：带边界 / 带分类 / Coriolis / 基速）**
// （⑯ WindField；契约 `docs/裁决-WindField架构契约.md` W1–W5，设计 `docs/设计-WindField实现设计.md` §三–§四）。
//
//   ★性质：与 `ClimateForcing` 同款**无状态纯函数**——无 Cell 数组、无记忆、无迭代、无随机项；
//     相同输入逐位同（契约验收 E）。月 m 只是输入维度（m = 0 = 北半球 1 月，与 MonthlyTemperature 同约定）。
//   ★**方向语义（拍板 ②，永久）**：本类一切 bearing 都是 **BearingTo = 气流去向方位角**
//     （正北顺时针 0–360°，即运动方向）——与气象学"风来自哪"**相反**。命名与测试名直接携带
//     BearingTo/Destination 语义，防止被改回气象学口径（契约 W-O1 + 设计 §4.6）。
//   ★**赤道基矢（拍板 ①，永久）**：Trade 基矢定义为**朝 φ_ITCZ 一侧**（解析侧向），赤道点
//     永远有确定方位——**不存在零向量，不做 normalize(0)，无赤道特殊分支**（W5 逐点测试安全）。
//   ★**速度解耦（拍板 ③，永久）**：基速只由带分类决定；后续季风/地形批次只改方向或乘衰减因子，
//     **永不改写带基速**（W5 / 统计量 / P4-5d 接口干净）。
//   ★批次边界：本类**不含**季风、地形、静风哨兵聚合与事实容器（后续批次；设计 §十一）。
public static class WindFieldModel
{
	const double Pi = Math.PI;
	const float Deg2RadF = MathF.PI / 180f;

	// ── 带边界（设计 §4.1；全部 B 类：度）────────────────────────────────────────

	/// <summary>
	/// 半球热异常 → ITCZ 修正量（度）：`clamp(Gain·(T̄′_NH − T̄′_SH), ±MaxDeg)`。
	/// 输入 = 两半球的**月均温度异常**（标量；由调用方从 `MonthlyTemperature` 聚合——
	/// 聚合属后续批次，本函数只吃标量以保持零事实层依赖）。
	/// </summary>
	public static float ItczAnomalyShiftDeg(float tAnomNhC, float tAnomShC)
	{
		double raw = WindParameters.ItczAnomalyGainDegPerC * (tAnomNhC - tAnomShC);
		double max = WindParameters.ItczAnomalyCorrectionMaxDeg;
		return (float)Math.Clamp(raw, -max, max);
	}

	/// <summary>
	/// ITCZ 纬度（度，带符号）：`clamp(5 + 10·cos(2π(m−6)/12) + Δφ_anom, ±15)`。
	/// m = 6 ⇒ 7 月 ⇒ 最北（cos(0)=1）；m = 0 ⇒ 1 月 ⇒ 最南。
	/// ★异常关闭路径：传 `tAnomNhC = tAnomShC = 0` ⇒ 纯日历曲线（退化矩阵 D1 的异常半边）。
	/// </summary>
	public static float ItczLatDeg(int month, float tAnomNhC = 0f, float tAnomShC = 0f)
	{
		ValidateMonth(month);
		double calendar = WindParameters.ItczMeanDeg +
						  WindParameters.ItczSeasonalAmpDeg * Math.Cos(2.0 * Pi * (month - 6) / 12.0);
		double total = calendar + ItczAnomalyShiftDeg(tAnomNhC, tAnomShC);
		double clamp = WindParameters.ItczLatClampDeg;
		return (float)Math.Clamp(total, -clamp, clamp);
	}

	/// <summary>北半球副热带高压脊纬度（度）：`30 + 0.3·(φ_ITCZ(m) − 5)`。</summary>
	public static float RidgeLatDeg(int month, float tAnomNhC = 0f, float tAnomShC = 0f)
		=> ShiftedBoundaryDeg(WindParameters.RidgeMeanDeg, month, tAnomNhC, tAnomShC);

	/// <summary>北半球极锋纬度（度）：`60 + 0.3·(φ_ITCZ(m) − 5)`。</summary>
	public static float PolarFrontLatDeg(int month, float tAnomNhC = 0f, float tAnomShC = 0f)
		=> ShiftedBoundaryDeg(WindParameters.PolarFrontMeanDeg, month, tAnomNhC, tAnomShC);

	static float ShiftedBoundaryDeg(float meanDeg, int month, float tAnomNhC, float tAnomShC)
		=> (float)(meanDeg + WindParameters.RidgeShiftFactor *
				   (ItczLatDeg(month, tAnomNhC, tAnomShC) - WindParameters.ItczMeanDeg));

	// ── 带分类（设计 §4.2；★必须用带符号纬度——赤道两侧带边界不对称）──────────────

	/// <summary>
	/// 带分类：先判 Doldrums（|φ − φ_ITCZ| &lt; 半宽），再按带符号 φ 与该半球的脊/锋边界分带。
	/// SH 边界 = 镜像均值 + **同号**漂移（7 月 SH 副高脊向赤道退——两半球同号漂移均物理正确）。
	/// </summary>
	public static WindBelt ClassifyBelt(float latRad, int month, float tAnomNhC = 0f, float tAnomShC = 0f)
	{
		ValidateMonth(month);
		double latDeg = latRad / Deg2RadF;
		double itcz = ItczLatDeg(month, tAnomNhC, tAnomShC);
		if (Math.Abs(latDeg - itcz) < WindParameters.DoldrumHalfWidthDeg)
			return WindBelt.Doldrums;
		if (latDeg >= 0)
		{
			if (latDeg <= RidgeLatDeg(month, tAnomNhC, tAnomShC)) return WindBelt.Trade;
			if (latDeg <= PolarFrontLatDeg(month, tAnomNhC, tAnomShC)) return WindBelt.Westerly;
			return WindBelt.PolarEasterly;
		}
		double shift = WindParameters.RidgeShiftFactor * (itcz - WindParameters.ItczMeanDeg);
		double ridgeS = -WindParameters.RidgeMeanDeg + shift;
		double polarS = -WindParameters.PolarFrontMeanDeg + shift;
		if (latDeg >= ridgeS) return WindBelt.Trade;
		if (latDeg >= polarS) return WindBelt.Westerly;
		return WindBelt.PolarEasterly;
	}

	// ── 经向基矢（设计 §4.2 + 拍板 ①；BearingTo 语义）────────────────────────────

	/// <summary>
	/// 带的**经向基矢**去向方位角（度，0–360；旋转前，Coriolis 前的纯经向方向）。
	///
	/// ★**拍板 ①（赤道解析极限）**：Trade 基矢 = **朝 φ_ITCZ 一侧**
	/// （`φ &gt; φ_ITCZ ⇒ 正南 180°，否则正北 0°`）——ITCZ 越过赤道的月份里，
	/// 赤道点仍取确定侧向（朝对半球深处的 ITCZ），**无零向量、无 normalize、无特殊分支**。
	/// Westerly/PolarEasterly 的带区间天然不含赤道（|φ| ≥ 半宽之外且跨脊/锋才换带）。
	/// ★Doldrums **无基矢**（静风候选；方向哨兵由输出批次写入）。
	/// </summary>
	public static float MeridionalBaseBearingToDeg(WindBelt belt, float latRad, int month,
		float tAnomNhC = 0f, float tAnomShC = 0f)
	{
		ValidateMonth(month);
		double latDeg = latRad / Deg2RadF;
		switch (belt)
		{
			case WindBelt.Trade:
				return latDeg > ItczLatDeg(month, tAnomNhC, tAnomShC) ? 180f : 0f;
			case WindBelt.PolarEasterly:
				return latDeg > 0 ? 180f : 0f;
			case WindBelt.Westerly:
				return latDeg > 0 ? 0f : 180f;
			case WindBelt.Doldrums:
			default:
				throw new InvalidOperationException($"Doldrums 无经向基矢（格 φ={latDeg:F2}°, m={month}）");
		}
	}

	// ── Coriolis 偏转（设计 §4.3；确定性规则，W-M3：不进时间积分）─────────────────

	/// <summary>
	/// Coriolis 偏转角（度）：`δ(φ) = 75°·tanh(2·sin|φ|)`。
	/// 锚：δ(0)=0（赤道不偏——退化锚）、δ(20°)≈44.6°、δ(45°)≈66.6°、δ(70°)≈71.6°；单调；半球镜像。
	/// </summary>
	public static float CoriolisDeflectionDeg(float latRad)
	{
		double s = Math.Sin(Math.Abs((double)latRad));
		return (float)(WindParameters.DeflectionMaxDeg * Math.Tanh(WindParameters.CoriolisShapeK * s));
	}

	/// <summary>
	/// 基矢经 Coriolis 偏转后的**去向方位角**（度，归一化 [0,360)）：
	/// NH 顺时针（az + δ，运动方向右偏）；SH 逆时针（az − δ，左偏）。δ(0)=0 ⇒ 赤道无分支。
	/// </summary>
	public static float DeflectedBearingToDeg(float baseBearingToDeg, float latRad)
	{
		double delta = CoriolisDeflectionDeg(latRad);
		double az = baseBearingToDeg + (latRad >= 0 ? delta : -delta);
		az %= 360.0;
		if (az < 0) az += 360.0;
		return (float)az;
	}

	// ── 输出量化（设计 §4.6；扇区口径）────────────────────────────────────────────

	/// <summary>静风哨兵扇区值（<see cref="MonthlyTemperature"/> 无 sentinel 纪律的例外：Direction 专用）。</summary>
	public const int CalmSector = 0;

	/// <summary>
	/// 去向方位角 → 16 扇区（1..16；扇区 s 覆盖 [(s−1)·22.5°, s·22.5°)，中心 (s−0.5)·22.5°）。
	/// ★输入必须是 <b>BearingTo</b>（去向）。静风哨兵 0 由聚合层写入，本函数**不产生** 0。
	/// </summary>
	public static int SectorFromBearingToDeg(float bearingToDeg)
	{
		double az = ((double)bearingToDeg % 360.0 + 360.0) % 360.0;
		int sector = (int)Math.Floor(az / 22.5) + 1;
		return Math.Clamp(sector, 1, 16);
	}

	// ── 带基础风速（设计 §4.6；★拍板 ③：与方向解耦）─────────────────────────────

	/// <summary>
	/// 带基速（m/s，海洋基准）：Trade 6｜Westerly 9｜PolarEasterly 4｜Doldrums = Trade×0.4。
	/// ★季风/地形批次对速度的上限 = **乘衰减因子**，本基值不可被改写（拍板 ③）。
	/// 陆地摩擦因子随地形批次进入（陆 ×0.7，设计 §4.6）。
	/// </summary>
	public static float BeltBaseSpeedMs(WindBelt belt)
		=> belt switch
		{
			WindBelt.Trade => WindParameters.BaseTradeSpeedMs,
			WindBelt.Westerly => WindParameters.BaseWesterlySpeedMs,
			WindBelt.PolarEasterly => WindParameters.BasePolarSpeedMs,
			WindBelt.Doldrums => WindParameters.BaseTradeSpeedMs * WindParameters.DoldrumSpeedFactor,
			_ => throw new ArgumentOutOfRangeException(nameof(belt), belt, null),
		};

	static void ValidateMonth(int month)
	{
		if ((uint)month >= ClimateForcing.MonthsPerYear)
			throw new ArgumentOutOfRangeException(nameof(month), month, "月索引须为 0..11（m=0 = 北半球 1 月）");
	}
}
