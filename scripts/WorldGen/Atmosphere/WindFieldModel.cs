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
//     **只改方向，永不改写带基速**。
//   ★批次 4 季风（设计 §4.5）：对**已完成地形修正的基础去向**做矢量混合
//     `v = (1−w)·v_belt + w·v_monsoon`（方向-only）；MonsoonIndex 只表达**全年季节性风向
//     翻转强度**（MRI），与静风哨兵（DirectionTo=0）语义独立、永不互替。
//     D2：`MonsoonStrength=0`（或门控 ∞ / reach 0）⇒ 输出与批次 3 **逐位**相等。
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
		public float TerrainBarrierHeightM = WindParameters.TerrainBarrierHeightM;   // 关断 = 1e9（D4）
		public float TerrainRelativeRiseM = WindParameters.TerrainRelativeRiseM;
		public int TerrainLookaheadHops = WindParameters.TerrainLookaheadHops;
		public float TerrainDeflectionDeg = WindParameters.TerrainDeflectionDeg;
		public float TerrainWedgeHalfDeg = WindParameters.TerrainWedgeHalfDeg;
		public float MonsoonSeaBandDeg = WindParameters.MonsoonSeaBandDeg;
		public float MonsoonContrastThreshC = WindParameters.MonsoonContrastThreshC; // 关断 = ∞（D2 门控半边）
		public float MonsoonDecayKm = WindParameters.MonsoonDecayKm;
		public float MonsoonReachKm = WindParameters.MonsoonReachKm;                 // 关断 = 0（D2 距离半边）
		public float MonsoonStrength = WindParameters.MonsoonStrength;               // 关断 = 0（D2 总开关）

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

	// ── 地形阻挡/绕流（批次 3；设计 §4.4；★拍板①在基础风方向之后｜★拍板②只改方向不改速度）──

	/// <summary>
	/// **地形绕流决策核心**（纯函数，独立可测）：由三楔屏障极大值决定修正后去向方位角。
	/// 输入 = 原去向方位角 + 三楔内**屏障格**（HeightM ≥ 门槛）的最大高度（无屏障 = 0）。
	/// 决策：中心无屏障 ⇒ 原向｜两侧皆阻 ⇒ 原向（★拍板②：不减速、不第三向）｜
	/// 平局（含双侧皆通）⇒ 逆时针｜否则取屏障较低侧。单步偏转 ±45°，**不级联复查**。
	/// </summary>
	public static double TerrainDeflectDeg(double azDeg, double centerMax, double cwMax, double ccwMax,
		Tuning tuning = null)
	{
		var t = tuning ?? Tuning.Default;
		if (centerMax <= 0) return azDeg;                       // 前向视域无屏障
		if (cwMax > 0 && ccwMax > 0) return azDeg;              // 两侧皆阻 ⇒ 原向（拍板②）
		if (cwMax == ccwMax) return azDeg - t.TerrainDeflectionDeg;   // 平局 ⇒ 逆时针
		return cwMax < ccwMax
			? azDeg + t.TerrainDeflectionDeg                    // 右（顺时针）侧更低
			: azDeg - t.TerrainDeflectionDeg;                   // 左（逆时针）侧更低
	}

	/// <summary>
	/// 格 i → 格 j 的罗盘方位角（度，0–360，**去向**语义；内部/测试用）。
	/// 北切向 = normalize(N − (N·d)d)，N = (0,1,0)；东切向 = normalize(cross(d, 北切向))；
	/// 用弦向量 v = d_j − d_i 近似切向位移（res4 单步 ≤135 km，弦切差可忽略；确定性一致）。
	/// </summary>
	internal static double BearingToDeg(int i, int j, WindTerrain ter)
	{
		double dx = ter.DirX[j] - ter.DirX[i];
		double dy = ter.DirY[j] - ter.DirY[i];
		double dz = ter.DirZ[j] - ter.DirZ[i];
		double dix = ter.DirX[i], diy = ter.DirY[i], diz = ter.DirZ[i];
		// 北切向（未归一化亦可：只比方向）N=(0,1,0)：N − (N·d)d = (−diy·di, 1−diy², −diy·di_z)
		double nx = -diy * dix, ny = 1.0 - diy * diy, nz = -diy * diz;
		// 东切向 = cross(d, 北切向)
		// 东切向 = cross(d, 北切向)（λ 增 = +Z = 正东；叉积顺序镜像会让东西翻转——合成图测试照出）
		double ex = diy * nz - diz * ny;
		double ey = diz * nx - dix * nz;
		double ez = dix * ny - diy * nx;
		double e = dx * ex + dy * ey + dz * ez;
		double n = dx * nx + dy * ny + dz * nz;
		double az = Math.Atan2(e, n) * Rad2Deg;
		return az < 0 ? az + 360.0 : az;
	}

	static double AngDiff(double a, double b)
	{
		double d = Math.Abs(a - b) % 360.0;
		return d > 180.0 ? 360.0 - d : d;
	}

	/// <summary>
	/// 三楔屏障极大值（内部热路径：调用方提供复用缓冲）。屏障 = HeightM ≥ max(BarrierHeightM, 自身高+RelativeRise)。
	/// 楔形（设计 §4.4 + §十三钉）：中心 = az±WedgeHalf；右（顺时针）= az+45±WedgeHalf；左（逆时针）= az−45±WedgeHalf；
	/// 边界归属优先级 中心 &gt; 右 &gt; 左。无屏障侧返回 0。
	/// </summary>
	internal static void WedgeBarrierMaxima(int i, double azDeg, WindTerrain ter,
		int[] stamp, int stampGen, int[] frontier, int[] nextF, double[] bearBuf, float[] hBuf,
		Tuning t, out double centerMax, out double cwMax, out double ccwMax)
	{
		var tt = t ?? Tuning.Default;
		double threshold = Math.Max(tt.TerrainBarrierHeightM, (double)ter.HeightM[i] + tt.TerrainRelativeRiseM);
		// BFS 深度 ≤ LookaheadHops，收集（bearing, height）
		stamp[i] = stampGen;
		frontier[0] = i;
		int fCnt = 1, cnt = 0;
		for (int hop = 0; hop < tt.TerrainLookaheadHops; hop++)
		{
			int nCnt = 0;
			for (int k = 0; k < fCnt; k++)
			{
				foreach (var j in ter.Neighbors[frontier[k]])
				{
					if (stamp[j] == stampGen) continue;
					stamp[j] = stampGen;
					nextF[nCnt++] = j;
					bearBuf[cnt] = BearingToDeg(i, j, ter);
					hBuf[cnt] = ter.HeightM[j];
					cnt++;
				}
			}
			(frontier, nextF) = (nextF, frontier);
			fCnt = nCnt;
		}
		double half = tt.TerrainWedgeHalfDeg;
		double defl = tt.TerrainDeflectionDeg;
		centerMax = 0; cwMax = 0; ccwMax = 0;
		for (int k = 0; k < cnt; k++)
		{
			if (hBuf[k] < threshold) continue;                    // 屏障判定（海格 ≤0 永不触发）
			double b = bearBuf[k];
			if (AngDiff(b, azDeg) <= half) centerMax = Math.Max(centerMax, hBuf[k]);
			else if (AngDiff(b, azDeg + defl) <= half) cwMax = Math.Max(cwMax, hBuf[k]);
			else if (AngDiff(b, azDeg - defl) <= half) ccwMax = Math.Max(ccwMax, hBuf[k]);
		}
	}

	// ── 季风符号反转（批次 4；设计 §4.5；★只改方向不改速度｜复用既有事实不造新距离场）──

	/// <summary>
	/// **季风权重**（纯函数，独立可测；设计 §4.5 门控 + 衰减）：
	/// `Cmax &lt; 门控` ⇒ 0｜`d_km &gt; Reach` ⇒ 0｜否则 `Strength·exp(−d_km/DecayKm)`。
	/// ★三重关断：`MonsoonStrength=0`（D2 主关断）/ `MonsoonContrastThreshC=∞` / `MonsoonReachKm=0`。
	/// </summary>
	public static float MonsoonWeight(float cMaxC, float distKm, Tuning tuning = null)
	{
		var t = tuning ?? Tuning.Default;
		if (cMaxC < t.MonsoonContrastThreshC) return 0f;
		if (t.MonsoonStrength <= 0 || distKm > t.MonsoonReachKm) return 0f;
		double decay = t.MonsoonDecayKm > 0 ? t.MonsoonDecayKm : 1e-6f;
		return (float)(t.MonsoonStrength * Math.Exp(-distKm / decay));
	}

	/// <summary>16 扇区中心角（度）：扇区 s 覆盖 [(s−1)·22.5°, s·22.5°) ⇒ 中心 (s−1)·22.5+11.25。</summary>
	public static double SectorCenterDeg(int sector) => (sector - 1) * 22.5 + 11.25;

	/// <summary>
	/// **MonsoonReversalIndex**（设计 §4.6；∈ [0,1]，只表达**全年季节性风向翻转强度**）：
	/// Δθ = 两极端月 az_final 按 16 扇区中心角的夹角 ∈ [0,180°]；MRI = (1−cos Δθ)/2。
	/// ★与静风哨兵语义独立：哨兵由聚合层判定（本函数不读也不写 DirectionTo）。
	/// </summary>
	public static float MonsoonReversalIndexOf(float azWarmToDeg, float azCoolToDeg)
	{
		double sw = SectorCenterDeg(SectorFromBearingToDeg(azWarmToDeg));
		double sc = SectorCenterDeg(SectorFromBearingToDeg(azCoolToDeg));
		double dth = Math.Abs(sw - sc) % 360.0;
		if (dth > 180.0) dth = 360.0 - dth;
		return (float)((1.0 - Math.Cos(dth * Pi / 180.0)) / 2.0);
	}

	/// <summary>
	/// 沿既有 BFS 距离场**严格递减回溯**到源（dist=0 的格；陆格→最近海格 / 海格→最近陆格）。
	/// 平局取**格索引最小**邻居（设计 §4.5"天然确定"）。找不到递减邻居 ⇒ −1（调用方关该格季风）。
	/// ★只消费既有距离事实——不为 Wind 生成任何新距离场（批次 4 硬线④）。
	/// </summary>
	internal static int BacktrackSource(int i, int[] distHops, WindTerrain ter)
	{
		int d = distHops[i];
		while (d > 0)
		{
			int best = -1;
			foreach (var j in ter.Neighbors[i])
				if (distHops[j] == d - 1 && (best < 0 || j < best)) best = j;
			if (best < 0) return -1;
			i = best;
			d--;
		}
		return i;
	}

	static int LowerBound(double[] a, double v)
	{
		int lo = 0, hi = a.Length;
		while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] < v) lo = mid + 1; else hi = mid; }
		return lo;
	}

	static int UpperBound(double[] a, double v)
	{
		int lo = 0, hi = a.Length;
		while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] <= v) lo = mid + 1; else hi = mid; }
		return lo;
	}

	/// <summary>
	/// 逐月陆海对比信号 `C(i,m)`（设计 §4.5；月主序 `cAll[m·n+i]`）：
	/// 陆格 = T′(i,m) − 海侧纬带均值｜海格 = 陆侧纬带均值 − T′(i,m)；纬差 ≤ `MonsoonSeaBandDeg`。
	/// T′ 直接复用 `MonthlyTemperature`（不重算 Gain/τ，同 I5）；带均值按**纬度排序 + 前缀和**
	/// 精确计算（O(12·n log n)，无近似分桶）。带内对侧为空 ⇒ C = 0（⇒ Cmax=0 ⇒ 门控自然关断）。
	/// </summary>
	internal static float[] MonsoonContrastSignals(MonthlyTemperature temp, float[] latRad,
		WindTerrain terrain, Tuning t)
	{
		int n = latRad.Length;
		var annual = new float[n];
		for (int i = 0; i < n; i++) annual[i] = temp.AnnualMeanAt(i);

		var order = new int[n];
		var sortedLat = new double[n];
		for (int i = 0; i < n; i++) { order[i] = i; sortedLat[i] = latRad[i]; }
		Array.Sort(sortedLat, order);

		var preLand = new double[n + 1];
		var preSea = new double[n + 1];
		var cntLand = new int[n + 1];
		var cntSea = new int[n + 1];
		var tPrim = new float[n];
		var cAll = new float[MonthlyTemperature.Months * n];
		double bandRad = (double)t.MonsoonSeaBandDeg * (Math.PI / 180.0);

		for (int m = 0; m < MonthlyTemperature.Months; m++)
		{
			int off = m * n;
			for (int i = 0; i < n; i++) tPrim[i] = temp.C[off + i] - annual[i];
			preLand[0] = 0; preSea[0] = 0; cntLand[0] = 0; cntSea[0] = 0;
			for (int k = 0; k < n; k++)
			{
				int c = order[k];
				bool land = terrain.HeightM[c] > 0;
				preLand[k + 1] = preLand[k] + (land ? tPrim[c] : 0);
				preSea[k + 1] = preSea[k] + (land ? 0 : tPrim[c]);
				cntLand[k + 1] = cntLand[k] + (land ? 1 : 0);
				cntSea[k + 1] = cntSea[k] + (land ? 0 : 1);
			}
			for (int i = 0; i < n; i++)
			{
				int a = LowerBound(sortedLat, latRad[i] - bandRad);
				int b = UpperBound(sortedLat, latRad[i] + bandRad);
				if (terrain.HeightM[i] > 0)
				{
					int cnt = cntSea[b] - cntSea[a];
					cAll[off + i] = cnt > 0
						? (float)(tPrim[i] - (preSea[b] - preSea[a]) / cnt)
						: 0f;
				}
				else
				{
					int cnt = cntLand[b] - cntLand[a];
					cAll[off + i] = cnt > 0
						? (float)((preLand[b] - preLand[a]) / cnt - tPrim[i])
						: 0f;
				}
			}
		}
		return cAll;
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

	// ── 全流水线（设计 §三 步骤 ①②③④⑥；⑤季风属批次 4）─────────────────────────

	/// <summary>
	/// **球面入口**（生产接线用）：纬度取 I1 权威口径 `H3.CellToLatLng`（弧度、带符号）。
	/// `heightM` 非 null ⇒ 启用地形阻挡/绕流（批次 3）；null ⇒ 与批次 2 逐位等价。
	/// `distToCoast/LandHops` 非 null ⇒ 启用季风（批次 4；KmPerHop 由 I6 口径自动换算）。
	/// </summary>
	public static WindField Generate(Ball ball, float[] heightM, MonthlyTemperature temp = null, Tuning tuning = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		return Generate(latRadOf(ball), temp, tuning,
			heightM != null ? WindTerrain.FromBall(ball, heightM) : null);
	}

	/// <summary>
	/// **球面入口 + 季风距离事实**（批次 4；dist* = `FinalGeography.FinalDistToCoast/Land` 原数组引用）。
	/// ★传 null 距离 ⇒ 与上一重载逐位等价（季风关闭）。
	/// </summary>
	public static WindField Generate(Ball ball, float[] heightM, int[] distToCoastHops, int[] distToLandHops,
		MonthlyTemperature temp = null, Tuning tuning = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		return Generate(latRadOf(ball), temp, tuning,
			WindTerrain.FromBall(ball, heightM, distToCoastHops, distToLandHops));
	}

	/// <summary>无地形便捷重载（批次 2 兼容；等价 heightM = null）。</summary>
	public static WindField Generate(Ball ball, MonthlyTemperature temp = null, Tuning tuning = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		return Generate(latRadOf(ball), temp, tuning, null);
	}

	static float[] latRadOf(Ball ball)
	{
		int n = ball.CellIds.Length;
		var latRad = new float[n];
		for (int i = 0; i < n; i++) latRad[i] = (float)H3.CellToLatLng(ball.CellIds[i]).Lat;
		return latRad;
	}

	/// <summary>
	/// **纯函数入口**：给定逐格纬度（弧度）、可选的 P4-5b 月度温度事实与可选地形/海岸视图，产出三统计量。
	///
	/// 聚合口径（钉死）：
	///  · **SpeedMs** = 12 个月带基速的算术平均（Doldrums 月按 Trade×0.4 计；
	///    ★地形/季风**都不改速度**，批次 3 拍板②）；
	///  · **DirectionTo** = 12 个月去向单位矢量的**圆均值**再扇区化——Doldrums 月**不参与**（无基矢）；
	///    地形修正发生在**每月基础风方向之后**（批次 3 拍板①，单步 ±45° 不级联）；
	///    季风混合在**地形之后**（批次 4：`v = (1−w)·v_belt + w·v_monsoon`，方向-only）；
	///    合成矢量均值长度 &lt; `PrevailingMinVectorLength`（两季对吹抵消）或速度 &lt; `CalmSpeedMs`
	///    ⇒ 静风哨兵 0；
	///  · **MonsoonIndex** = 两极端月（C 信号 argmax/argmin，平局取小索引）az_final 按 16 扇区
	///    中心角的 `(1−cos Δθ)/2` ∈ [0,1]——只表达**全年季节性风向翻转强度**；
	///    静风格 / w=0 / 极端月无方向 ⇒ 0（★与静风哨兵语义独立、永不互替）。
	/// ★月度逐格明细**不落盘**（W-S1）；本函数输出即契约 W-O1~O3 三统计量。
	/// ★D4：地形关断（terrain = null 或 `TerrainBarrierHeightM = 1e9`）⇒ 逐位回批次 2。
	/// ★D2：季风关断（`MonsoonStrength=0` / 门控 ∞ / reach=0 / 不传距离事实）
	///   ⇒ 输出与批次 3 **逐位**相等（`monoReady` 短路 + else 分支表达式与批次 3 逐字一致）。
	/// </summary>
	public static WindField Generate(float[] latRad, MonthlyTemperature temp = null, Tuning tuning = null,
		WindTerrain terrain = null)
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

		// 季风就绪判定（批次 4）：温度事实 + 海岸距离事实 + hop→km 口径 + 未总关断。
		// ★只消费既有事实（FinalDistToCoast/Land 引用 + MonthlyTemperature），不生成新距离场（硬线④）。
		bool monoReady = temp != null && terrain != null
			&& terrain.DistToCoastHops != null && terrain.DistToLandHops != null
			&& terrain.KmPerHop > 0
			&& t.MonsoonStrength > 0 && t.MonsoonReachKm > 0;
		float[] cAll = monoReady ? MonsoonContrastSignals(temp, latRad, terrain, t) : null;

		// 地形扫描缓冲（一次分配，全程复用；stamp 代际法免清零）
		int[] stamp = null, frontier = null, nextF = null;
		double[] bearBuf = null;
		float[] hBuf = null;
		int stampGen = 0;
		if (terrain != null)
		{
			if (terrain.HeightM.Length != n)
				throw new ArgumentException($"terrain.HeightM 长度({terrain.HeightM.Length}) 与 latRad.Length({n}) 不一致", nameof(terrain));
			stamp = new int[n];
			frontier = new int[128];
			nextF = new int[128];
			bearBuf = new double[128];
			hBuf = new float[128];
		}

		for (int i = 0; i < n; i++)
		{
			double latDeg = latRad[i] * Rad2Deg;
			double delta = CoriolisDeflectionDeg(latRad[i], t);   // 只依赖纬度 ⇒ 逐格一次

			// 季风逐格前置（设计 §4.5）：回溯源 → 目标方位 → 极端月 → 权重
			float wMono = 0f;
			double azMono = 0;
			int mWarm = -1, mCool = -1;
			if (monoReady)
			{
				bool land = terrain.HeightM[i] > 0;
				int[] dist = land ? terrain.DistToCoastHops : terrain.DistToLandHops;
				int d = dist[i];
				if (d > 0)
				{
					int src = BacktrackSource(i, dist, terrain);
					if (src >= 0)
					{
						// 陆格：海→陆（源→本格）；海格：指向陆（本格→源）——两岸自动衔接同一条气流线
						azMono = land ? BearingToDeg(src, i, terrain) : BearingToDeg(i, src, terrain);
						float cW = float.NegativeInfinity, cC = float.PositiveInfinity;
						for (int m = 0; m < MonthlyTemperature.Months; m++)
						{
							float cm = cAll[m * n + i];
							if (cm > cW) { cW = cm; mWarm = m; }
							if (cm < cC) { cC = cm; mCool = m; }
						}
						float cmax = Math.Max(cW, -cC);
						wMono = MonsoonWeight(cmax, (float)(d * terrain.KmPerHop), t);
					}
				}
			}

			double cosSum = 0, sinSum = 0, speedSum = 0;
			int vecMonths = 0;
			double azWarm = 0, azCool = 0;
			bool hasWarm = false, hasCool = false;
			// 地形修正记忆化：一格的月度基方位只有 ≤2 种（基矢 0/180 + 固定 δ）⇒ 双槽缓存
			double azKey1 = double.NaN, azOut1 = 0, azKey2 = double.NaN, azOut2 = 0;
			for (int m = 0; m < MonthlyTemperature.Months; m++)
			{
				var b = bounds[m];
				var belt = ClassifyBeltCore(latDeg, in b);
				if (belt == WindBelt.Doldrums)
				{
					speedSum += doldrumSpeed;                     // 无风带月：有速度无方向
					// Doldrums 月不参与圆均值；w>0 时极端月可由纯季风方向供 MRI（无基矢 ⇒ v = w·v_monsoon）
					if (wMono > 0)
					{
						float cm0 = cAll[m * n + i];
						if (cm0 > 0 && m == mWarm) { azWarm = azMono; hasWarm = true; }
						else if (cm0 < 0 && m == mCool) { azCool = azMono + 180.0; hasCool = true; }
					}
					continue;
				}
				double baseAz = MeridionalBaseBearingToDegCore(belt, latDeg, b.Itcz);
				double azDeg = baseAz + (latRad[i] >= 0 ? delta : -delta);
				if (terrain != null)
				{
					double azOut;
					if (azDeg == azKey1) azOut = azOut1;
					else if (azDeg == azKey2) azOut = azOut2;
					else
					{
						stampGen++;
						WedgeBarrierMaxima(i, azDeg, terrain, stamp, stampGen, frontier, nextF, bearBuf, hBuf, t,
							out var cMax, out var cwMax, out var ccwMax);
						azOut = TerrainDeflectDeg(azDeg, cMax, cwMax, ccwMax, t);
						azKey2 = azKey1; azOut2 = azOut1;
						azKey1 = azDeg; azOut1 = azOut;
					}
					azDeg = azOut;
				}
				// 季风混合（批次 4；在地形之后）：C 符号定向（C>0 吹向陆侧 / C<0 反向），方向-only
				float cm = wMono > 0 ? cAll[m * n + i] : 0f;
				int sgn = cm > 0 ? 1 : cm < 0 ? -1 : 0;
				if (sgn != 0)
				{
					double azM = sgn > 0 ? azMono : azMono + 180.0;
					double r1 = azDeg * Pi / 180.0, r2 = azM * Pi / 180.0;
					double vx = (1.0 - wMono) * Math.Cos(r1) + wMono * Math.Cos(r2);
					double vy = (1.0 - wMono) * Math.Sin(r1) + wMono * Math.Sin(r2);
					cosSum += vx;
					sinSum += vy;
					double azFinal = Math.Atan2(vy, vx) * Rad2Deg;
					if (m == mWarm) { azWarm = azFinal; hasWarm = true; }
					if (m == mCool) { azCool = azFinal; hasCool = true; }
				}
				else
				{
					double az = azDeg * Pi / 180.0;
					cosSum += Math.Cos(az);
					sinSum += Math.Sin(az);
					if (m == mWarm) { azWarm = azDeg; hasWarm = true; }
					if (m == mCool) { azCool = azDeg; hasCool = true; }
				}
				vecMonths++;
				speedSum += BeltBaseSpeedMs(belt, t);
			}

			float speed = Math.Clamp((float)(speedSum / MonthlyTemperature.Months), 0f, t.MaxSpeedMs);
			wind.SpeedMs[i] = speed;                              // ★地形/季风都不改速度（批次 3 拍板②）
			wind.MonsoonIndex[i] = 0f;                            // ↓ 批次 4 在 calm 判定后接管

			bool calm = speed < t.CalmSpeedMs;
			if (!calm && vecMonths > 0)
			{
				double meanLen = Math.Sqrt(cosSum * cosSum + sinSum * sinSum) / vecMonths;
				calm = meanLen < t.PrevailingMinVectorLength;     // 两季对吹抵消 ⇒ 无盛行方向
			}
			wind.DirectionTo[i] = calm || vecMonths == 0
				? (byte)CalmSector
				: (byte)SectorFromBearingToDeg((float)(Math.Atan2(sinSum, cosSum) * Rad2Deg));

			// MonsoonIndex：只表达全年季节性风向翻转强度；静风哨兵 ⇒ 0（语义独立、永不互替）
			float monoIdx = wMono > 0 && hasWarm && hasCool && !calm
				? MonsoonReversalIndexOf((float)azWarm, (float)azCool)
				: 0f;
			wind.MonsoonIndex[i] = Math.Clamp(monoIdx, 0f, 1f);
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
