using System;
using World.Spatial;    // Ball（H3 球壳数据层）
using Godot;            // 仅 Vector3 结构体（纯值类型；D3 降级版上游格点积用）

namespace World.WorldGen;

// 世界生成空间 · **耦合气候求解器**（Phase 4 · **P4-5c/P4-5d**；契约 §二.4 的 Solver 正体）。
//
//   ── 在契约里的位置（P4-5c 三问 ②③ + P4-5d §十二）────────────────────────────
//   通用迭代骨架：
//     ```
//     State₀ → evaluate tendencies → State₁ → converged?
//                └─ no → iterate ─┘   └─ yes → final ClimateState
//     ```
//   P4-5b 的 `ThermalResponseSolver`（闭式周期解）**原样复用**为温度分量——
//   不重新设计、不重新审查（用户开工纪律；无耦合 ⇒ 闭式解仍精确）。
//   P4-5c 新增**第一条耦合链**：`Temperature → Evaporation → Moisture → Precipitation`。
//   P4-5d 的第二条链在 **D3 降级裁决（docs/裁决-D3降级-洋流温度影响.md，2026-10-07）**后改为：
//   `Ocean circulation field → Current Influence → Temperature`——洋流作为**方向性温度影响**
//   把上游海区的温度**季节结构**向下游传播（月度场一次性后处理），**不是**热量守恒输运。
//   原边通量（q_ij = −q_ji）/ 迎风平流 / CFL 夹子 / 全球零和机制**全部删除**。
//
//   ── 洋流温度影响（D3 降级版；裁决 §二/§四）────────────────────────────────
//   `ΔT_i(m) = Gain · Influence(Speed_i) · (T_up(m) − T_i(m))`，随后逐格扣 12 月均值
//   ⇒ `Σ_m ΔT_i(m) = 0` **逐格精确**（T_ann 锚 = 条款 10 的强形式恒等式）。
//   ★上游格 up(i) = 海格邻居中 `−Direction_i · t̂_k` 最大且 > 0 者（水沿 Direction 流入 i
//     ⇒ 上游在 −Direction 侧）；无合格邻居 / Speed = 0 ⇒ 不修改（逐位不动）。
//   ★Influence = Speed/(Speed + v_half)（饱和函数，随 Speed 单调；不再被 CFL 钉死）。
//   ★非守恒登记：趋近不是输运，上游不被冷却；扣均值后只剩月内结构重排（裁决 §四.3，
//     未来不得叠加第二个非守恒项而不重新过契约）。
//   ★陆格：不参与影响（海-陆热影响 = 0）；陆格温度保持闭式解逐位不动。
//   ★退化（原则 4）：`currentInfluenceGain = 0` ⇒ 整条链短路 ⇒ 输出**逐位**等于 P4-5c 冻结基线。
//
//   ── 最小水循环（P4-5c，刻意极简；架构验证，不是气候学终局）────────────────────
//   状态：`W(i, m)` 逐格逐月大气水汽（mm）。
//   tendencies（每月一步，dt = 1 月）：
//     · 蒸发源   `E(i) = Gain · max(0, T(i,m))`  —— **仅海格**（陆格无水源 = 登记的能力边界）
//     · 凝结移除 `P(i) = W(i) / τ_p`              —— 一阶凝结（衰减项**解析精确**积分）
//     · 输送     `Flux_ij = (W_j − W_i) / (6·τ_mix)` —— 通量形式，**逐格求和精确相消**
//   ⇒ 周期强迫（T 年循环）下的**周期稳态**由 spin-up 迭代求得。
//
//   ── 数值稳定性（为什么衰减项要解析积分）────────────────────────────────────
//   显式欧拉对 `−W/τ_p` 项要求 dt/τ_p < 2，而 dt = 1 月、τ_p = 0.3 月 ⇒ 3.3 > 2 ⇒ 发散。
//   ⇒ 对库源 `(E + Flux) − W/τ_p` 在步内**解析积分**（源项步内冻结，即"衰减精确 + 源显式"）：
//       `W' = W·e^{−dt/τ_p} + (1 − e^{−dt/τ_p})·τ_p·Src`
//   无条件稳定；本步移除量（= 降水）按收支记账：`P = W + Src·dt − W'`。
//   ★W 非负保护：`W' < 0` 时夹零（强扩散流出的罕见格）；此时收支出现微量残差，
//     全球守恒验收用**相对容差**（1e-3）而非逐位——登记，不是隐藏。
public sealed class CoupledClimateSolver
{
	/// <summary>spin-up 年数上限（超限 = 未收敛；测试会变红）。O-C2 的落地形态。</summary>
	public const int MaxSpinupYears = 60;

	/// <summary>周期稳态收敛判据：相邻两年**年末水汽场**的最大绝对差（mm）。</summary>
	public const float ConvergenceTolMm = 1e-3f;

	/// <summary>
	/// 求解多分量气候状态。
	/// 输入只来自世界事实层 + 冻结的温度基座：`FinalGeography.FinalLand` +
	/// `Ball` 邻接/纬度 + 年均温度事实 `annualMeanC`。
	/// ★**不读** CivSim / Legacy；★**不回写**任何世界事实层（条款 9）；
	/// ★水循环**不触碰**温度分量（条款 8/10 —— v1 无雪冰、无潜热回授，登记的能力边界）。
	/// </summary>
	/// <param name="evapGainMmPerMonthC">
	/// 蒸发增益（默认取 <see cref="ClimateParameters.EvapGainMmPerMonthC"/>）；
	/// **传 0 = 水循环关断** ⇒ 精确退化到 P4-5b 状态（水分量全零）。
	/// </param>
	/// <param name="currentInfluenceGain">
	/// **P4-5d · D3 降级版**：洋流温度影响增益（关断值 = 0，**默认 0 = P4-5c 冻结基线**）。
	/// &gt; 0 时须提供 <paramref name="oceanCurrents"/>；温度月度场做**一次性后处理**
	/// （上游趋近 + 扣 12 月均值 ⇒ T_ann 逐格锚不变），陆格温度保持闭式解逐位不动。
	/// </param>
	/// <param name="oceanCurrents">
	/// **P4-5d**：稳态表层环流事实场（D2 产物）。_solver 把它当**输入**读取
	/// （与 `ClimateForcing` 同地位；不迭代、不回写——⑮ 条款 13）。
	/// </param>
	public static ClimateState Solve(Ball ball, FinalGeography final, float[] annualMeanC,
		float evapGainMmPerMonthC = ClimateParameters.EvapGainMmPerMonthC,
		float currentInfluenceGain = 0f,
		OceanCurrentField oceanCurrents = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		if (annualMeanC == null) throw new ArgumentNullException(nameof(annualMeanC));
		if (currentInfluenceGain > 0f && oceanCurrents == null)
			throw new ArgumentException("currentInfluenceGain > 0 须提供 oceanCurrents（D2 产物）", nameof(oceanCurrents));
		var dirs = ball.CellDirs;
		int n = dirs.Length;
		if (final.FinalLand == null || final.FinalLand.Length != n)
			throw new ArgumentException("FinalLand 与 Ball 格数不一致", nameof(final));
		if (annualMeanC.Length != n)
			throw new ArgumentException("年均温度与 Ball 格数不一致", nameof(annualMeanC));

		var state = new ClimateState
		{
			CellCount = n,
			// ① 温度分量 = P4-5b 冻结闭式解（复用基座）。
			//    currentInfluenceGain = 0 ⇒ 即最终值（逐位 = P4-5c 基线）；
			//    > 0 ⇒ 先做洋流温度影响后处理（海格月度结构被调制；陆格逐位不动）。
			Temperature = ThermalResponseSolver.Solve(ball, final, annualMeanC),
		};
		state.WaterVapor = MonthlyField.Create(n);
		state.Precipitation = MonthlyField.Create(n);
		var tempC = state.Temperature.C;

		// ② 洋流温度影响（D3 降级版）：**月度场一次性后处理**（裁决 §四.1——不进 spinup 迭代）。
		//    水循环随后读调整后的温度（气候态内部一致）；与蒸发关断正交（裁决：evapGain=0 时影响仍生效）。
		if (currentInfluenceGain > 0f)
		{
			ApplyCurrentInfluence(ball, final, oceanCurrents, currentInfluenceGain, tempC);
			state.Temperature.FinishReadouts();     // 读数重算（MeanC/MeanAmplitudeC 反映影响后场）
		}

		// ③ 蒸发增益关断 ⇒ 水分量全零，精确退化到 P4-5b 形态（永久原则 4；不留浮点残差）。
		//    （洋流影响与此正交：水分量退化时温度影响仍已生效——由 currentInfluenceGain 独立控制。）
		if (evapGainMmPerMonthC == 0f)
		{
			state.GlobalAnnualEvapMm = 0f;
			state.GlobalAnnualPrecipMm = 0f;
			state.SpinupYearsUsed = 0;
			state.Converged = true;
			return state;
		}

		float tauP = ClimateParameters.WaterVaporResidenceMonths;
		float k = 1f / (6f * ClimateParameters.VaporDiffusionTimescaleMonths);   // 通量系数
		float decay = MathF.Exp(-1f / tauP);                                     // 库项解析因子
		var neighbors = ball.CellNeighbors;

		var w = new float[n];             // 状态：逐格水汽（mm）
		var flux = new float[n];          // 本步扩散通量（步初 W 上计算 ⇒ Jacobi ⇒ 守恒）
		var evap = new float[n];          // 本月蒸发源
		var precip = new float[n];        // 本步移除量（= 降水，mm）

		// ④ spin-up：周期稳态迭代（State(t) → tendencies → State(t+1) → converged?）——P4-5c 原样
		int yearsUsed = 0;
		bool converged = false;
		for (int year = 0; year < MaxSpinupYears; year++)
		{
			yearsUsed = year + 1;
			var yearStartW = (float[])w.Clone();
			for (int m = 0; m < MonthlyField.Months; m++)
				StepMonth(w, flux, evap, precip, tempC, m * n,
					neighbors, final.FinalLand, n, m, evapGainMmPerMonthC, k, decay, tauP);
			float maxDelta = 0f;
			for (int i = 0; i < n; i++)
				maxDelta = MathF.Max(maxDelta, MathF.Abs(w[i] - yearStartW[i]));
			if (maxDelta < ConvergenceTolMm) { converged = true; break; }
		}
		state.SpinupYearsUsed = yearsUsed;
		state.Converged = converged;

		// ⑤ 记录年：收敛后再走一个完整年循环，把月度场写入 ClimateState（水分量）。
		double evapSum = 0.0, precipSum = 0.0;
		for (int m = 0; m < MonthlyField.Months; m++)
		{
			StepMonth(w, flux, evap, precip, tempC, m * n,
				neighbors, final.FinalLand, n, m, evapGainMmPerMonthC, k, decay, tauP);
			int offW = m * n;
			for (int i = 0; i < n; i++)
			{
				state.WaterVapor.V[offW + i] = w[i];
				state.Precipitation.V[offW + i] = precip[i];
			}
		}
		// 记录年年末的水汽场与下一年年初衔接由周期性保证（err ≤ tol）；年蒸发/降水总量
		// 用**整个记录年**的累计（逐格逐月求和 ⇒ 年总量口径，等积格算术平均）。
		// 温度分量自 ② 起未再变动 ⇒ 读数（MeanC/MeanAmplitudeC）已反映影响后场，无需重算。
		for (int i = 0; i < n; i++)
		{
			for (int m = 0; m < MonthlyField.Months; m++)
			{
				// 蒸发由温度决定，与 W 无关 ⇒ 记录年重算即可（纯函数；用记录月温度）
				float t = state.Temperature.C[m * n + i];
				if (!final.FinalLand[i] && t > 0f)
					evapSum += evapGainMmPerMonthC * t;
			}
			for (int m = 0; m < MonthlyField.Months; m++)
				precipSum += state.Precipitation.V[m * n + i];
		}
		state.GlobalAnnualEvapMm = (float)(evapSum / n);
		state.GlobalAnnualPrecipMm = (float)(precipSum / n);
		state.GlobalMeanWaterVaporMm = (float)state.WaterVapor.GlobalAnnualMean();
		return state;
	}

	// ── P4-5d · D3 降级版 · 内部实现 ────────────────────────────────────────────

	/// <summary>
	/// **洋流温度影响**（月度场一次性后处理；只动海格，陆格逐位不动）：
	/// 对每个有流海格 i，取上游格 up = argmax(−Direction_i · t̂_k)（海格邻居，对齐 &gt; 0），
	/// `ΔT_i(m) = gain · Speed/(Speed+v_half) · (T_up(m) − T_i(m))`，
	/// 再逐格扣除 12 月均值 ⇒ `Σ_m ΔT_i(m) = 0`（T_ann 逐格锚，条款 10 强形式）。
	/// ★Jacobi 读法：全部从步初快照场读取 ⇒ 与格处理顺序无关；固定扫描 ⇒ 确定性。
	/// ★静风格（Speed=0）/无合格上游格：不修改（逐位不动）。
	/// </summary>
	internal static void ApplyCurrentInfluence(Ball ball, FinalGeography final, OceanCurrentField currents,
		float gain, float[] tempC)
	{
		if (currents == null) throw new ArgumentNullException(nameof(currents));
		var isLand = final.FinalLand;
		int n = isLand.Length;
		if (currents.CellCount != n)
			throw new ArgumentException("OceanCurrentField 与 Ball 格数不一致", nameof(currents));
		if (tempC.Length != MonthlyField.Months * n)
			throw new ArgumentException("温度月度场长度与格数不一致", nameof(tempC));

		var neighbors = ball.CellNeighbors;
		var tans = ball.CellNeighborDirs;
		float vHalf = ClimateParameters.CurrentInfluenceHalfSpeedKmPerMonth;
		var baseT = (float[])tempC.Clone();          // Jacobi：全读步初场
		var dT = new float[MonthlyField.Months];     // 本格 12 月趋近量（复用缓冲）

		for (int i = 0; i < n; i++)
		{
			if (isLand[i]) continue;
			float speed = currents.SpeedKmPerMonth[i];
			if (speed <= 0f) continue;               // 停滞/静风格：无影响（逐位不动）

			// 上游格 = −Direction 侧对齐最好的海格邻居（水沿 Direction 流入 i）
			var d = currents.Direction[i];
			var nb = neighbors[i];
			var tn = tans[i];
			int up = -1;
			float bestDot = 1e-6f;                   // 要求正向对齐（确定性阈值）
			for (int k = 0; k < nb.Length; k++)
			{
				int j = nb[k];
				if (isLand[j]) continue;
				var t = tn[k];
				if (t == Vector3.Zero) continue;     // 退化邻居（Ball 约定）
				float dot = -(d.X * t.X + d.Y * t.Y + d.Z * t.Z);
				if (dot > bestDot) { bestDot = dot; up = j; }
			}
			if (up < 0) continue;                    // 无上游（全陆/背向）⇒ 不修改

			float influence = gain * (speed / (speed + vHalf));
			float sum = 0f;
			for (int m = 0; m < MonthlyField.Months; m++)
			{
				dT[m] = influence * (baseT[m * n + up] - baseT[m * n + i]);
				sum += dT[m];
			}
			float mean = sum / MonthlyField.Months;  // 扣年均偏移 ⇒ ΣdT = 0（逐格恒等式）
			for (int m = 0; m < MonthlyField.Months; m++)
				tempC[m * n + i] = baseT[m * n + i] + dT[m] - mean;
		}
	}

	/// <summary>
	/// 单月步进（契约迭代骨架的 "evaluate tendencies → State(t+1)" 段；水循环部分）。
	/// 通量在**步初 W** 上整体计算（Jacobi）⇒ 配对相消 ⇒ 输送项严格守恒；
	/// 库项（`−W/τ_p`）解析积分 ⇒ 无条件稳定；`P = W + Src − W'` 按收支记账。
	/// `tempOffset`：本月蒸发读取的温度槽位（闭式路径 = `m·n`；输送路径 = 0，标量状态段）。
	/// </summary>
	internal static void StepMonth(
		float[] w, float[] flux, float[] evap, float[] precip,
		float[] tempC, int tempOffset, int[][] neighbors, bool[] isLand,
		int n, int month, float evapGain, float k, float decay, float tauP)
	{
		// pass 1：源项与通量（全用步初 w）
		for (int i = 0; i < n; i++)
		{
			// 蒸发：仅海格、仅正温（陆格无水源 = 能力边界；冰点以下 = 0）
			evap[i] = (!isLand[i] && tempC[tempOffset + i] > 0f) ? evapGain * tempC[tempOffset + i] : 0f;
			float sum = 0f;
			var nb = neighbors[i];
			for (int j = 0; j < nb.Length; j++)
				sum += w[nb[j]] - w[i];
			flux[i] = k * sum;
		}

		// pass 2：库项解析积分 + 收支记账
		for (int i = 0; i < n; i++)
		{
			float src = evap[i] + flux[i];
			float wNew = w[i] * decay + (1f - decay) * tauP * src;
			if (wNew < 0f) wNew = 0f;                    // 非负保护（罕见：强扩散流出的干格）
			precip[i] = w[i] + src - wNew;               // 本步移除量 = 降水（收支口径）
			if (precip[i] < 0f) precip[i] = 0f;
			w[i] = wNew;
		}
	}
}
