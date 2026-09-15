using System;

namespace World.NewHexWorld.Plate
{
	// 地幔热状态（设计-05 §3）：**势温 Tp 随时间的演化** + 热收支诊断口。
	//
	// 这是"发动机缓慢熄火"的源项：地幔势温单调下降 ⇒ ① 热柱的 α·T_m 项变小（驱动变弱）
	// ② Arrhenius 黏度上升（同样力的响应变慢）。在此之前的模型里 Tp 不存在——地幔密度、
	// 黏度、年龄-密度链全是常量，所以驱动只能"饱和"、不能"衰减"（用户 2026-09-15 判读提出的质疑）。
	//
	// 演化律：净散热随放射性生热同比衰减（同一批燃料）：
	//   生热   H_rad(t)  = 13 TW × Σ f_i·2^(−t/τ_i)        （U238/U235/Th232/K40，现值份额归一）
	//   净散热 NetCooling = C·(TargetDeclineKPerGa/Ga→s) × Σ f_i·2^(−t/τ_i)   ← 钉到**地质约束**
	//   dTp/dt = −NetCooling/C                             （C = ρ_m·c_p·V_mantle）
	// t = 0 取**地球现值状态**（Tp = 1623 K、生热 13 TW、黏度 = 1.57e20 Pa·s）——所以本世界
	// 的 600 My 就是"中年地球往后 600 My"：势温降约 39 K，黏度 ×2.1，板速约减半。
	//
	// ⚠️ **诚实声明（Urey 比悖论）**：真实的"岩石圈散热（本类逐格算出来的那个数，~20 TW 量级）
	// 减放射性生热（13 TW）"远大于观测到的势温降温（~5 TW 当量）——这是著名的 Urey 比悖论
	// （地幔散热与观测降温差 ~5×，通常靠"对流效率/隐储库"解释）。本模型**不复现**这个悖论：
	// 把净降温率钉到地质约束（50–100 K/Ga 的观测代理记录），把差额作为 `HeatBalanceGapTW`
	// **显式诊断口暴露**——即本模型的热预算是"地质约束驱动"的，不是"地表热流守恒驱动"的。
	// 判读时看这一项：它就是这个模型对"热机效率"的无知程度（不是一个可调参数，是一个登记在案的缺口）。
	public sealed class H3ThermalState
	{
		// ── 常量（地球现值 + 地质约束；05 §4）──
		/// <summary>地幔势温（t = 0；1350 ℃）。</summary>
		public const float ReferencePotentialTemperatureK = 1623f;
		/// <summary>地幔热容 C = ρ_m·c_p·V_mantle（4.18e20 m³ × 4400 × 1250）。</summary>
		public const double MantleHeatCapacityJPerK = 2.3e27;
		/// <summary>地幔放射性生热现值（TW；U/Th/K）。</summary>
		public const float PresentRadiogenicTW = 13f;
		/// <summary>势温降温率标定值（K/Ga）——地质约束（太古宙以来 ~200 K 量级 / 观测代理记录 50–100 K/Ga）。</summary>
		public const float TargetDeclineKPerGa = 70f;
		/// <summary>Arrhenius 激活能（J/mol）：橄榄石蠕变实测 300–500 kJ/mol。</summary>
		public const float ActivationEnergyJPerMol = 400e3f;
		/// <summary>参考黏度（t = 0 时；= `MaterialDensity.MantleViscosity`，保证初态与旧口径逐位一致）。</summary>
		public const float ReferenceViscosityPaS = 1.57e20f;
		/// <summary>陆壳地表热流（W/m²）：地球实测 ~65 mW/m²（陆壳自身生热 + 地幔供给）。只作收支审计的常数项。</summary>
		public const float ContinentalHeatFlowWPerM2 = 0.065f;

		const float SecondsPerGa = 3.1558e16f;

		// 同位素：现值生热份额 + 半衰期（My）
		static readonly float[] IsotopeShare = { 0.356f, 0.035f, 0.425f, 0.184f };
		static readonly float[] IsotopeHalfLifeMy = { 4468f, 704f, 14050f, 1248f };

		// ── 状态 ──
		public float PotentialTemperatureK { get; private set; }
		public float ElapsedMy { get; private set; }
		/// <summary>放射性生热（TW，随时间衰减）。</summary>
		public float RadiogenicHeatTW { get; private set; }
		/// <summary>瞬时降温率（K/Ga）——判读口：这个数随时间缓慢变小（燃料衰减）。</summary>
		public double DeclineKPerGa { get; private set; }
		/// <summary>洋岩石圈散热（TW，由逐格年龄经热柱解析式算出）——诊断用。</summary>
		public float OceanHeatFlowTW { get; private set; }
		/// <summary>陆壳散热（TW，常数通量 × 陆面积）。</summary>
		public float ContinentalHeatFlowTW { get; private set; }
		/// <summary>地表总散热（TW）= 洋 + 陆。</summary>
		public float HeatLossTW => OceanHeatFlowTW + ContinentalHeatFlowTW;
		/// <summary>热源合计（TW）= 放射性生热 + 标定净散热。</summary>
		public double HeatSourceTW => RadiogenicHeatTW + _netCoolingTW;
		/// <summary>收支缺口（TW）= 地表散热 − 热源合计。**登记在案的口径差，不是可调参数**：
		/// 它衡量"本模型的热机功率"与真实地球的差距——本模型能供出的总功率 ≈ 18 TW（生热 13 + 标定净散热 5），
		/// 而真实地球地表散热 47 TW。缺口 ≈ −TW 说明模型的散热与源是自洽的（审计通过），
		/// 而"总功率只有地球的 ~40%"这件事**就是板速长期低于地球的原因**，也是 05 §开放点的那个自由度
		/// （想要地球级板速，就必须提高热机功率，而那会与"地质观测的慢降温"冲突——Urey 比悖论）。</summary>
		public double HeatBalanceGapTW { get; private set; }
		/// <summary>当前地幔黏度（Pa·s；由 Tp 经 Arrhenius 算出，t = 0 时 = 参考值）。</summary>
		public float MantleViscosityPaS { get; private set; }

		public H3ThermalState() => Reset();

		public void Reset()
		{
			ElapsedMy = 0f;
			PotentialTemperatureK = ReferencePotentialTemperatureK;
			Refresh(0f);
		}

		/// <summary>推进一个时间步：势温按热收支下降，随后刷新诊断口（生热/散热/黏度/缺口）。
		/// `fields` 只用于**诊断**（洋岩石圈散热需要逐格年龄）——势温演化与场无关（见类注释的标定口径）。</summary>
		public void Step(H3PlateFields fields, float stepMy)
		{
			ElapsedMy += stepMy;
			Refresh(stepMy);
			UpdateHeatFlowDiagnostics(fields);
		}

		// 势温推进 + 与场无关的诊断项（生热/降温率/黏度）
		void Refresh(float stepMy)
		{
			float decayFactor = IsotopeDecayFactor(ElapsedMy);
			RadiogenicHeatTW = PresentRadiogenicTW * decayFactor;

			// 净散热：钉到地质约束的降温率，且随燃料同比衰减（同一批同位素撑起两个项）
			double netCoolingTW = MantleHeatCapacityJPerK * (TargetDeclineKPerGa / SecondsPerGa) * decayFactor / 1e12;
			if (stepMy > 0f)
			{
				double deltaK = -netCoolingTW * 1e12 * (stepMy * 3.1558e13) / MantleHeatCapacityJPerK;
				PotentialTemperatureK += (float)deltaK;
			}
			// 瞬时降温率（K/Ga，**正数 = 在变冷**；判读口：这个数随时间缓慢变小，因为燃料在衰减）
			DeclineKPerGa = netCoolingTW * 1e12 / MantleHeatCapacityJPerK * SecondsPerGa;
			MantleViscosityPaS = H3ThermalColumn.ViscosityPaS(PotentialTemperatureK,
				ReferencePotentialTemperatureK, ReferenceViscosityPaS, ActivationEnergyJPerMol);
			_netCoolingTW = netCoolingTW;
		}

		double _netCoolingTW;

		// 地表散热诊断：洋格走热柱 q(age)，陆格走常数通量（只算面积，不算陆壳生热细节）
		void UpdateHeatFlowDiagnostics(H3PlateFields fields)
		{
			int n = fields.Count;
			double cellAreaM2 = 4.0 * Math.PI
				* (H3PlateMotion.EarthRadiusKm * 1000.0) * (H3PlateMotion.EarthRadiusKm * 1000.0) / n;
			double oceanWatts = 0, landCells = 0;
			for (int i = 0; i < n; i++)
			{
				if (fields.IsLand(i)) { landCells++; continue; }
				oceanWatts += H3ThermalColumn.HeatFlowWPerM2(fields.Age[i]) * cellAreaM2;
			}
			OceanHeatFlowTW = (float)(oceanWatts / 1e12);
			ContinentalHeatFlowTW = (float)(landCells * cellAreaM2 * ContinentalHeatFlowWPerM2 / 1e12);
			HeatBalanceGapTW = HeatLossTW - HeatSourceTW;
		}

		/// <summary>同位素衰变因子 Σ f_i·2^(−t/τ_i)（t 距今 My；t = 0 时为 1 = 现值）。</summary>
		public static float IsotopeDecayFactor(float elapsedMy)
		{
			float sum = 0f;
			for (int k = 0; k < IsotopeShare.Length; k++)
				sum += IsotopeShare[k] * MathF.Pow(2f, -MathF.Max(elapsedMy, 0f) / IsotopeHalfLifeMy[k]);
			return sum;
		}
	}
}
