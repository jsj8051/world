using System;
using World.Spatial;        // Ball（H3 球壳数据层）
using World.Utils.H3;       // H3.CellToLatLng（I1 权威纬度口径，弧度）

namespace World.WorldGen;

// 世界生成空间 · **风场模型（批次 2：事实容器 + 全流水线 + D1/D3/D5 退化矩阵）**
// （⑯ WindField；契约 `docs/裁决-WindField架构契约.md` W1–W5，设计 `docs/设计-WindField实现设计.md` §三–§五）。
//
//   ★性质：与 `ClimateForcing` 同款**无状态纯函数**——无记忆、无迭代、无随机项；
//     相同输入逐位同（契约验收 E）。月 m 只是输入维度（m = 0 = 北半球 1 月，与 MonthlyTemperature 同约定）。
//   ★**方向语义（拍板 ②，永久）**：一切 bearing 都是 **BearingTo = 气流去向方位角**
//     （正北顺时针 0–360°）——与气象学"风来自哪"**相反**。
//   ★**赤道基矢（拍板 ①，永久）**：Trade 基矢 = **朝 φ_ITCZ 一侧**（解析侧向）——
//     无零向量、无 normalize(0)、无赤道特殊分支（W5 逐点测试安全）。
//   ★**速度解耦（拍板 ③，永久）**：基速只由带分类决定；季风（批次 4）/地形（批次 3）
//     只改方向或乘衰减因子，**永不改写带基速**。
//   ★批次边界：本类**不含**季风、地形与判读读数（批次 3/4；设计 §十一）。
//     `Generate` 输出的 `MonsoonIndex` 恒 0（季风关闭 = D2 的天然实现态）。
public static class WindFieldModel
{
	const double Pi = Math.PI;
	const float Deg2RadF = MathF.PI / 180f;
	const double Rad2Deg = 180.0 / Math.PI;

	// ── 可注入参数束（批次 2；退化矩阵 D1–D5 的载体）────────────────────────────

	/// <summary>
	/// Wind v1 可注入参数束：字段默认值 = `WindParameters` 冻结常量。
	/// ★**只用于退化矩阵（D1–D5）的注入式关断与测试对照**——生产路径一律 <see cref="Tuning.Default"/>；
	/// 不是运行时调参入口（不得从 UI/存档读取；契约 §7 纯函数 + 永久原则 4 关断值纪律）。
	/// </summary>
	public sealed class Tuning
	{
		public float ItczMeanDeg = WindParameters.ItczMeanDeg;
		public float ItczSeasonalAmpDeg = WindParameters.ItczSeasonalAmpDeg;         // 关断 = 0（D1）
		public float ItczAnomalyGainDegPerC = WindParameters.ItczAnomalyGainDegPerC; // 关断 = 0（D1）
		public float ItczAnomalyCorrectionMaxDeg = WindParameters.ItczAnomalyCorrectionMaxDeg;
		public float ItczLatClampDeg = WindParameters.ItczLatClampDeg;
		public float RidgeMeanDeg = WindParameters.RidgeMeanDeg;
		public float PolarFrontMeanDeg = WindParameters.PolarFrontMeanDeg;
		public float RidgeShiftFactor = WindParameters.RidgeShiftFactor;             // 关断 = 0（D1 边界半边）
		public float DoldrumHalfWidthDeg = WindParameters.DoldrumHalfWidthDeg;
		public float DeflectionMaxDeg = WindParameters.DeflectionMaxDeg;             // 关断 = 0
		public float CoriolisShapeK = WindParameters.CoriolisShapeK;
		public float BaseTradeSpeedMs = WindParameters.BaseTradeSpeedMs;             // 关断 = 全 0（静风球）
		public float BaseWesterlySpeedMs = WindParameters.BaseWesterlySpeedMs;
		public float BasePolarSpeedMs = WindParameters.BasePolarSpeedMs;
		public float DoldrumSpeedFactor = WindParameters.DoldrumSpeedFactor;
		public float CalmSpeedMs = WindParameters.CalmSpeedMs;
		public float MaxSpeedMs = WindParameters.MaxSpeedMs;
		public float PrevailingMinVectorLength = WindParameters.PrevailingMinVectorLength;

		/// <summary>生产默认（= `WindParameters` 冻结常量）。</summary>
		public static Tuning Default { get; } = new();
	}

	// ── 带边界（设计 §4.1；全部 B 类：度）────────────────────────────────────────

	/// <summary>
	/// 半球热异常 → ITCZ 修正量（度）：`clamp(Gain·(T̄′_NH − T̄′_SH), ±MaxDeg)`。
	/// 输入 = 两半球的**月均温度异常**（标量；由 <see cref="HemisphericAnomalyMeansC"/> 从
	/// `MonthlyTemperature` 聚合，或测试直接构造）。
	/// </summary>
	public static float ItczAnomalyShiftDeg(float tAnomNhC, float tAnomShC, Tuning tuning = null)
	{
		var t = tuning ?? Tuning.Default;
		double raw = t.ItczAnomalyGainDegPerC * (tAnomNhC - tAnomShC);
		double max = t.ItczAnomalyCorrectionMaxDeg;
		return (float)Math.Clamp(raw, -max, max);
	}

	/// <summary>
	/// ITCZ 纬度（度，带符号）：`clamp(5 + 10·cos(2π(m−6)/12) + Δφ_anom, ±15)`。
	/// m = 6 ⇒ 7 月 ⇒ 最北；m = 0 ⇒ 1 月 ⇒ 最南。
	/// ★异常关闭路径：传 `tAnomNhC = tAnomShC = 0` ⇒ 纯日历曲线（退化矩阵 D1 的异常半边）。
	/// </summary>
	public static float ItczLatDeg(int month, float tAnomNhC = 0f, float tAnomShC = 0f, Tuning tuning = null)
	{
		ValidateMonth(month);
		var t = tuning ?? Tuning.Default;
		double calendar = t.ItczMeanDeg + t.ItczSeasonalAmpDeg * Math.Cos(2.0 * Pi * (month - 6) / 12.0);
		double total = calendar + ItczAnomalyShiftDeg(tAnomNhC, tAnomShC, t);
		return (float)Math.Clamp(total, -t.ItczLatClampDeg, t.ItczLatClampDeg);
	}

	/// <summary>北半球副热带高压脊纬度（度）：`30 + 0.3·(φ_ITCZ(m) − 5)`。</summary>
	public static float RidgeLatDeg(int month, float tAnomNhC = 0f, float tAnomShC = 0f, Tuning tuning = null)
		=> ShiftedBoundaryDeg((tuning ?? Tuning.Default).RidgeMeanDeg, month, tAnomNhC, tAnomShC, tuning);

	/// <summary>北半球极锋纬度（度）：`60 + 0.3·(φ_ITCZ(m) − 5)`。</summary>
	public static float PolarFrontLatDeg(int month, float tAnomNhC = 0f, float tAnomShC = 0f, Tuning tuning = null)
		=> ShiftedBoundaryDeg((tuning ?? Tuning.Default).PolarFrontMeanDeg, month, tAnomNhC, tAnomShC, tuning);

	static float ShiftedBoundaryDeg(float meanDeg, int month, float tAnomNhC, float tAnomShC, Tuning tuning)
	{
		var t = tuning ?? Tuning.Default;
		return (float)(meanDeg + t.RidgeShiftFactor * (ItczLatDeg(month, tAnomNhC, tAnomShC, t) - t.ItczMeanDeg));
	}

	// ── 月度带边界束（流水线内层按月预计算一次，避免逐格重算）────────────────────

	/// <summary>某月的五条带边界（度）：ITCZ、NH 脊/锋、SH 脊/锋（SH = 镜像均值 + 同号漂移）。</summary>
	public readonly struct MonthBoundaries
	{
		public readonly double Itcz, RidgeN, PolarN, RidgeS, PolarS, DoldrumHalf;
		public MonthBoundaries(double itcz, double ridgeN, double polarN, double ridgeS, double polarS, double doldrumHalf)
		{
			Itcz = itcz; RidgeN = ridgeN; PolarN = polarN; RidgeS = ridgeS; PolarS = polarS; DoldrumHalf = doldrumHalf;
		}
	}

	/// <summary>按月预计算带边界（`tAnomNh/ShByMonth` 为 null ⇒ 纯日历曲线，D1 异常关闭路径）。</summary>
	public static MonthBoundaries BoundariesFor(int month, float[] tAnomNhByMonth, float[] tAnomShByMonth, Tuning tuning = null)
	{
		ValidateMonth(month);
		var t = tuning ?? Tuning.Default;
		float nh = tAnomNhByMonth?[month] ?? 0f;
		float sh = tAnomShByMonth?[month] ?? 0f;
		double itcz = ItczLatDeg(month, nh, sh, t);
		double shift = t.RidgeShiftFactor * (itcz - t.ItczMeanDeg);
		return new MonthBoundaries(itcz,
			t.RidgeMeanDeg + shift, t.PolarFrontMeanDeg + shift,
			-t.RidgeMeanDeg + shift, -t.PolarFrontMeanDeg + shift,
			t.DoldrumHalfWidthDeg);
	}

	// ── 带分类（设计 §4.2；★必须用带符号纬度——赤道两侧带边界不对称）──────────────

	/// <summary>带分类（便利重载：按月 + 可选异常标量）。</summary>
	public static WindBelt ClassifyBelt(float latRad, int month, float tAnomNhC = 0f, float tAnomShC = 0f, Tuning tuning = null)
	{
		bool hasAnom = tAnomNhC != 0f || tAnomShC != 0f;
		var b = BoundariesFor(month,
			hasAnom ? new[] { tAnomNhC } : null,
			hasAnom ? new[] { tAnomShC } : null, tuning);
		return ClassifyBeltCore(latRad / Deg2RadF, in b);
	}

	/// <summary>
	/// 带分类核心：先判 Doldrums（|φ − φ_ITCZ| &lt; 半宽），再按带符号 φ 与该半球边界分带。
	/// </summary>
	public static WindBelt ClassifyBeltCore(double latDeg, in MonthBoundaries b)
	{
		if (Math.Abs(latDeg - b.Itcz) < b.DoldrumHalf)
			return WindBelt.Doldrums;
		if (latDeg >= 0)
		{
			if (latDeg <= b.RidgeN) return WindBelt.Trade;
			if (latDeg <= b.PolarN) return WindBelt.Westerly;
			return WindBelt.PolarEasterly;
		}
		if (latDeg >= b.RidgeS) return WindBelt.Trade;
		if (latDeg >= b.PolarS) return WindBelt.Westerly;
		return WindBelt.PolarEasterly;
	}

	// ── 经向基矢（设计 §4.2 + 拍板 ①；BearingTo 语义）────────────────────────────

	/// <summary>
	/// 带的**经向基矢**去向方位角（度；旋转前）。★拍板 ①：Trade 基矢 = 朝 φ_ITCZ 一侧
	/// （φ &gt; φ_ITCZ ⇒ 正南 180°，否则正北 0°）——赤道点永远有确定方位，无零向量分支。
	/// Doldrums 无基矢（本核心不接收 Doldrums；便利重载对 Doldrums 抛错）。
	/// </summary>
	public static float MeridionalBaseBearingToDegCore(WindBelt belt, double latDeg, double itczDeg)
		=> belt switch
		{
			WindBelt.Trade => latDeg > itczDeg ? 180f : 0f,
			WindBelt.PolarEasterly => latDeg > 0 ? 180f : 0f,
			WindBelt.Westerly => latDeg > 0 ? 0f : 180f,
			_ => throw new InvalidOperationException($"Doldrums 无经向基矢（格 φ={latDeg:F2}°）"),
		};

	/// <summary>便利重载（批次 1 兼容；月 + 异常标量口径）。</summary>
	public static float MeridionalBaseBearingToDeg(WindBelt belt, float latRad, int month,
		float tAnomNhC = 0f, float tAnomShC = 0f, Tuning tuning = null)
	{
		ValidateMonth(month);
		if (belt == WindBelt.Doldrums)
			throw new InvalidOperationException($"Doldrums 无经向基矢（月 m={month}）");
		var t = tuning ?? Tuning.Default;
		double itcz = ItczLatDeg(month, tAnomNhC, tAnomShC, t);
		return MeridionalBaseBearingToDegCore(belt, latRad / Deg2RadF, itcz);
	}

	// ── Coriolis 偏转（设计 §4.3；确定性规则，W-M3：不进时间积分）─────────────────

	/// <summary>
	/// Coriolis 偏转角（度）：`δ(φ) = 75°·tanh(2·sin|φ|)`。
	/// 锚：δ(0)=0（赤道不偏）、δ(20°)≈44.6°、δ(45°)≈66.6°、δ(70°)≈71.6°；单调；半球镜像。
	/// </summary>
	public static float CoriolisDeflectionDeg(float latRad, Tuning tuning = null)
	{
		var t = tuning ?? Tuning.Default;
		double s = Math.Sin(Math.Abs((double)latRad));
		return (float)(t.DeflectionMaxDeg * Math.Tanh(t.CoriolisShapeK * s));
	}

	/// <summary>
	/// 基矢经 Coriolis 偏转后的**去向方位角**（度，归一化 [0,360)）：
	/// NH 顺时针（az + δ，运动方向右偏）；SH 逆时针（az − δ，左偏）。δ(0)=0 ⇒ 赤道无分支。
	/// </summary>
	public static float DeflectedBearingToDeg(float baseBearingToDeg, float latRad, Tuning tuning = null)
	{
		double delta = CoriolisDeflectionDeg(latRad, tuning);
		double az = baseBearingToDeg + (latRad >= 0 ? delta : -delta);
		az %= 360.0;
		if (az < 0) az += 360.0;
		return (float)az;
	}

	// ── 输出量化（设计 §4.6）────────────────────────────────────────────────────

	/// <summary>静风哨兵扇区值（Direction 专用哨兵；其余风场数组无 sentinel）。</summary>
	public const int CalmSector = 0;

	/// <summary>
	/// 去向方位角 → 16 扇区（1..16；扇区 s 覆盖 [(s−1)·22.5°, s·22.5°)）。
	/// ★输入必须是 <b>BearingTo</b>（去向）。静风哨兵 0 由聚合层写入，本函数**不产生** 0。
	/// </summary>
	public static int SectorFromBearingToDeg(float bearingToDeg)
	{
		double az = ((double)bearingToDeg % 360.0 + 360.0) % 360.0;
		int sector = (int)Math.Floor(az / 22.5) + 1;
		return Math.Clamp(sector, 1, WindField.SectorCount);
	}

	// ── 带基础风速（设计 §4.6；★拍板 ③：与方向解耦）─────────────────────────────

	/// <summary>
	/// 带基速（m/s，海洋基准）：Trade 6｜Westerly 9｜PolarEasterly 4｜Doldrums = Trade×0.4。
	/// ★季风/地形批次对速度的上限 = **乘衰减因子**，本基值不可被改写（拍板 ③）。
	/// 陆地摩擦因子随地形批次进入（陆 ×0.7，设计 §4.6）。
	/// </summary>
	public static float BeltBaseSpeedMs(WindBelt belt, Tuning tuning = null)
	{
		var t = tuning ?? Tuning.Default;
		return belt switch
		{
			WindBelt.Trade => t.BaseTradeSpeedMs,
			WindBelt.Westerly => t.BaseWesterlySpeedMs,
			WindBelt.PolarEasterly => t.BasePolarSpeedMs,
			WindBelt.Doldrums => t.BaseTradeSpeedMs * t.DoldrumSpeedFactor,
			_ => throw new ArgumentOutOfRangeException(nameof(belt), belt, null),
		};
	}

	// ── 输入聚合（W2 允许：读 Climate 既有事实，不重算）──────────────────────────

	/// <summary>
	/// 两半球的逐月温度异常均值（°C；ITCZ 异常修正的输入）。
	/// T′(i,m) = `temp.At(m,i) − temp.AnnualMeanAt(i)`（★直接复用 P4-5b 输出，**不重算 Gain/τ**）；
	/// 半球归属 = latRad 符号；等积格 ⇒ 算术平均 = 面积加权（同 `MonthlyTemperature.MeanC` 口径）。
	/// 年均值先逐格缓存一次（O(12n)，避免 12 遍重算）。
	/// </summary>
	public static (float[] Nh, float[] Sh) HemisphericAnomalyMeansC(MonthlyTemperature temp, float[] latRad)
	{
		ValidateInputs(temp, latRad);
		int n = temp.CellCount;
		var annual = new float[n];
		for (int i = 0; i < n; i++) annual[i] = temp.AnnualMeanAt(i);

		var nh = new float[MonthlyTemperature.Months];
		var sh = new float[MonthlyTemperature.Months];
		for (int m = 0; m < MonthlyTemperature.Months; m++)
		{
			double sumN = 0, sumS = 0;
			int cntN = 0, cntS = 0;
			int off = m * n;
			for (int i = 0; i < n; i++)
			{
				double anom = temp.C[off + i] - annual[i];
				if (latRad[i] >= 0) { sumN += anom; cntN++; }
				else { sumS += anom; cntS++; }
			}
			nh[m] = cntN > 0 ? (float)(sumN / cntN) : 0f;
			sh[m] = cntS > 0 ? (float)(sumS / cntS) : 0f;
		}
		return (nh, sh);
	}

	// ── 全流水线（设计 §三 步骤 ①②③⑥；④地形/⑤季风属批次 3/4）─────────────────

	/// <summary>
	/// **球面入口**（生产接线用）：纬度取 I1 权威口径 `H3.CellToLatLng`（弧度、带符号）。
	/// </summary>
	public static WindField Generate(Ball ball, MonthlyTemperature temp = null, Tuning tuning = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		int n = ball.CellIds.Length;
		var latRad = new float[n];
		for (int i = 0; i < n; i++) latRad[i] = (float)H3.CellToLatLng(ball.CellIds[i]).Lat;
		return Generate(latRad, temp, tuning);
	}

	/// <summary>
	/// **纯函数入口**：给定逐格纬度（弧度）与可选的 P4-5b 月度温度事实，产出三统计量。
	///
	/// 聚合口径（钉死）：
	///  · **SpeedMs** = 12 个月带基速的算术平均（Doldrums 月按 Trade×0.4 计；月级无其它速度修正）；
	///  · **DirectionTo** = 12 个月去向单位矢量的**圆均值**再扇区化——Doldrums 月**不参与**
	///    （无基矢）；合成矢量均值长度 &lt; `PrevailingMinVectorLength`（两季对吹抵消）或
	///    代表速度 &lt; `CalmSpeedMs` ⇒ 静风哨兵 0；全年全 Doldrums ⇒ 静风哨兵；
	///  · **MonsoonIndex** ≡ 0（批次 2 = 季风关闭态；机制属批次 4）。
	/// ★月度逐格明细**不落盘**（W-S1）；本函数输出即契约 W-O1~O3 三统计量。
	/// </summary>
	public static WindField Generate(float[] latRad, MonthlyTemperature temp = null, Tuning tuning = null)
	{
		ValidateInputs(temp, latRad);
		var t = tuning ?? Tuning.Default;
		int n = latRad.Length;
		var wind = WindField.Create(n);

		(float[] nh, float[] sh) = temp != null
			? HemisphericAnomalyMeansC(temp, latRad)
			: (null, null);

		var bounds = new MonthBoundaries[MonthlyTemperature.Months];
		float doldrumSpeed = BeltBaseSpeedMs(WindBelt.Doldrums, t);
		for (int m = 0; m < MonthlyTemperature.Months; m++)
			bounds[m] = BoundariesFor(m, nh, sh, t);

		for (int i = 0; i < n; i++)
		{
			double latDeg = latRad[i] * Rad2Deg;
			double delta = CoriolisDeflectionDeg(latRad[i], t);   // 只依赖纬度 ⇒ 逐格一次
			double cosSum = 0, sinSum = 0, speedSum = 0;
			int vecMonths = 0;
			for (int m = 0; m < MonthlyTemperature.Months; m++)
			{
				var b = bounds[m];
				var belt = ClassifyBeltCore(latDeg, in b);
				if (belt == WindBelt.Doldrums)
				{
					speedSum += doldrumSpeed;                     // 无风带月：有速度无方向
					continue;
				}
				double baseAz = MeridionalBaseBearingToDegCore(belt, latDeg, b.Itcz);
				double az = (baseAz + (latRad[i] >= 0 ? delta : -delta)) * Pi / 180.0;
				cosSum += Math.Cos(az);
				sinSum += Math.Sin(az);
				vecMonths++;
				speedSum += BeltBaseSpeedMs(belt, t);
			}

			float speed = (float)(speedSum / MonthlyTemperature.Months);
			wind.SpeedMs[i] = Math.Clamp(speed, 0f, t.MaxSpeedMs);
			wind.MonsoonIndex[i] = 0f;                            // 批次 4 接管

			bool calm = speed < t.CalmSpeedMs;
			if (!calm && vecMonths > 0)
			{
				double meanLen = Math.Sqrt(cosSum * cosSum + sinSum * sinSum) / vecMonths;
				calm = meanLen < t.PrevailingMinVectorLength;     // 两季对吹抵消 ⇒ 无盛行方向
			}
			wind.DirectionTo[i] = calm || vecMonths == 0
				? (byte)CalmSector
				: (byte)SectorFromBearingToDeg((float)(Math.Atan2(sinSum, cosSum) * Rad2Deg));
		}
		return wind;
	}

	static void ValidateInputs(MonthlyTemperature temp, float[] latRad)
	{
		if (latRad == null) throw new ArgumentNullException(nameof(latRad));
		if (latRad.Length == 0) throw new ArgumentException("latRad 不得为空", nameof(latRad));
		if (temp != null && temp.CellCount != latRad.Length)
			throw new ArgumentException($"temp.CellCount({temp.CellCount}) 与 latRad.Length({latRad.Length}) 不一致", nameof(temp));
	}

	static void ValidateMonth(int month)
	{
		if ((uint)month >= ClimateForcing.MonthsPerYear)
			throw new ArgumentOutOfRangeException(nameof(month), month, "月索引须为 0..11（m=0 = 北半球 1 月）");
	}
}
