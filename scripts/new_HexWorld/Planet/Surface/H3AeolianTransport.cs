using System;

namespace World.NewHexWorld.Plate
{
	// 方向性风沙搬运：侵蚀 → 搬运 → 沉积沿风向走——与 H3FluvialTransport（沿下坡走）同构的
	// 第二条搬运链（沙丘/黄土机制）：
	//
	//   力度 = 风场大小 × 海拔 × 干燥度：
	//     输沙能力 C_a = Ka × max(0, |u| − u_t)³ × ρ空气项 × 干燥度 × 裸露度
	//     · |u| = H3Wind.SpeedField 的逐格风速（带基速 × 自转档 × **海拔项**——高山风大）；
	//     · (u − u_t)³ 是 Bagnold 风成输沙的立方律——必须有绝对风速；
	//     · u_t ≈ 5 m/s 流体起动阈值：弱风不卷沙；
	//     · ρ空气项 = exp(−z/8500)：高海拔空气薄、动量小（与海拔项反着走，净效应标定）；
	//     · 干燥度 = 1 − AI、裸露度 = H3Climate.BarenessFromAridityIndex（湿润区风蚀≈0，与水蚀互补）。
	//   侵蚀：**只啃松散 Sediment 池**（基岩风蚀磨蚀可忽略，登记）；supply-limited：有沙才搬。
	//   方向 = 风场方向：落格邻居 = 模板 AeolianDownwind（与风向最对齐的邻居；
	//     过切/极点 = −1 ⇒ 沙没有去处，本格不起动）。
	//   搬运/沉积（一回合内沿风下行到落定，与河流的"链尾必是洼地/海"对应）：
	//     · 沿途沉降率 = 基率 + max(0, 1 − C_下风/C_本格)——**风变弱就落沙**（背风坡、盆地）；
	//     · 落差 > `MaxClimbM` ⇒ 沙爬不上山，就地落（山前堆积/山挡沙）；
	//     · 下风是海格 ⇒ 全部落底（陆源尘上大陆架，与河流三角洲同款汇）。
	//
	// 沉积/侵蚀单位 = kg/m²（守恒组口径），全部只动 sediment 池 ⇒ 组内守恒、零审计记账。
	// 风链可成环（纬向风绕星球一圈）而河流链必终止——回合制用"逐 pass 沉降"收敛：基率 > 0 保证
	// 有限 pass 内全部落定；pass 上限兜底后把残余就地落底 ⇒ Σ侵蚀 = Σ沉积 严格成立。
	public sealed class H3AeolianTransport
	{
		// ── 旋钮（地球档标定，判读后按需调）──
		/// <summary>输沙能力系数：C_a = 2.3 × (|u|−u_t)³ × …（kg/m²）。锚点：Ferrel 海平面 |u|=12 ⇒
		/// (12−5)³ = 343 ⇒ 干燥全裸格 C_a ≈ 790 kg/m²，×侵蚀效率 0.05 ≈ 40 kg/m²/步——与河流啃床同量级。</summary>
		public const float CapacityCoefKgPerM2 = 2.3f;
		/// <summary>流体起动阈值（m/s）：|u| 低于它风不卷沙（干石英沙流体起动 ~5 m/s 量级）。</summary>
		public const float WindStartThresholdMS = 5f;
		/// <summary>空气密度海拔项的标高（m）：ρ 因子 = exp(−z/8500)（高海拔动量小，与海拔风增项对冲）。</summary>
		public const float AirDensityScaleHeightM = 8500f;
		/// <summary>侵蚀效率：每步啃能力 × 此值（supply-limited：上限是本格松散存量）。</summary>
		public const float ErodeEfficiency = 0.05f;
		/// <summary>沿途沉降基率：每过一个格至少落这比例（风链成环也能收敛；沙粒随机受阻沉降）。</summary>
		public const float SettleBase = 0.15f;
		/// <summary>风沙爬升上限（m）：下风格比本格高过多 ⇒ 沙爬不上山，就地落（山挡沙、山前堆积）。</summary>
		public const float MaxClimbM = 200f;
		/// <summary>回合收敛 pass 上限（兜底；超限残余就地落底——守恒优先）。</summary>
		public const int MaxPasses = 128;

		readonly Ball _ball;
		readonly float[] _load;         // 在途负载（kg/m²）
		readonly float[] _capacity;     // 逐格输沙能力（含干燥度/裸露度门；海格 = 0 = 全落汇）
		readonly float[] _erodedPerCell;
		readonly float[] _depositedPerCell;

		public H3AeolianTransport(Ball ball)
		{
			_ball = ball;
			int n = ball.CellIds.Length;
			_load = new float[n];
			_capacity = new float[n];
			_erodedPerCell = new float[n];
			_depositedPerCell = new float[n];
		}

		// ── 每步判读口（Apply 时更新）──
		public double ErodedMassLastStep { get; private set; }
		public double DepositedMassLastStep { get; private set; }
		public double DeliveredToOceanLastStep { get; private set; }   // 淤到水面以下（陆源尘上大陆架）
		/// <summary>逐格风蚀/落沙量（诊断/出图）。**复用缓冲**：每步覆盖，跨步持有无效。</summary>
		public float[] ErodedPerCell => _erodedPerCell;
		public float[] DepositedPerCell => _depositedPerCell;

		/// <summary>跑方向性风沙三段并应用进守恒组（只动 sediment 池，组总量不变）。
		/// <paramref name="windMS"/> = 逐格风速（H3Wind.SpeedField）；<paramref name="stencil"/> = 风模板
		///（落格邻居 + 步不变方向）；<paramref name="precipMmYear"/>/**tempC** = 闭合降水/温度（干燥度门）。</summary>
		public void Apply(H3PlateFields fields, float[] surfaceHeight, float[] windMS,
			H3Wind.UpwindStencil stencil, float[] precipMmYear, float[] tempC, float scale)
		{
			int n = fields.Count;
			Array.Clear(_load, 0, n);
			Array.Clear(_capacity, 0, n);
			Array.Clear(_erodedPerCell, 0, n);
			Array.Clear(_depositedPerCell, 0, n);
			ErodedMassLastStep = 0;
			DepositedMassLastStep = 0;
			DeliveredToOceanLastStep = 0;
			if (windMS == null || stencil == null || precipMmYear == null) return;   // 参考态 = 无风无沙

			var sediment = fields.Sediment;
			var downwind = stencil.AeolianDownwind;

			// ① 能力场 + ② 侵蚀（supply-limited，只啃松散池；水下不起动、无落格方向的格不起动）
			double eroded = 0;
			for (int i = 0; i < n; i++)
			{
				if (downwind[i] < 0 || surfaceHeight[i] <= 0f || sediment[i] <= 0f) continue;
				float u = MathF.Max(windMS[i], 0f);
				if (u <= WindStartThresholdMS) continue;
				float ai = H3Climate.AridityIndex(precipMmYear[i], tempC != null ? tempC[i] : H3SurfaceProcesses.ReferenceTempC);
				float aridity = 1f - ai;
				float bareness = H3Climate.BarenessFromAridityIndex(ai);
				float rhoAir = MathF.Exp(-MathF.Max(surfaceHeight[i], 0f) / AirDensityScaleHeightM);
				float excess = u - WindStartThresholdMS;
				float capacity = CapacityCoefKgPerM2 * excess * excess * excess * rhoAir * aridity * bareness * scale;
				if (capacity <= 0f) continue;
				_capacity[i] = capacity;
				float erode = MathF.Min(ErodeEfficiency * capacity, sediment[i]);
				if (erode <= 0f) continue;
				sediment[i] -= erode;                   // 先出账（deposition 就地入账 ⇒ 两侧同池直接扣加）
				_load[i] = erode;                       // 进在途池，③统一搬运
				_erodedPerCell[i] = erode;
				eroded += erode;
			}

			// ③ 搬运：逐 pass = 沿风走一格，**落在到达的格**；风弱沿途沉降、爬不动留原地、到海全落。
			//    基率 > 0 保证有限 pass 收敛（风链可成环，沉降逐圈削存量）。海格不持有在途负载
			//   （水下不起动 ⇒ 只收不发）。
			double deposited = 0, toOcean = 0;
			double inFlight = eroded;
			float seaLevel = 0f;                        // surfaceHeight 已是"相对海平面、水下截 0"口径 ⇒ 0 即海
			for (int pass = 0; pass < MaxPasses && inFlight > eroded * 1e-6; pass++)
			{
				double landed = 0;
				for (int i = 0; i < n; i++)
				{
					float total = _load[i];
					if (total <= 0f) continue;
					_load[i] = 0f;
					int d = downwind[i];
					float here = surfaceHeight[i];

					float deposit;
					int depositCell;
					if (surfaceHeight[d] <= seaLevel)
					{
						deposit = total;                // 下风是海 ⇒ 全落大陆架（海格汇）
						depositCell = d;
					}
					else if (surfaceHeight[d] - here > MaxClimbM)
					{
						deposit = total;                // 爬不上山 ⇒ 留在原地（山挡沙、山前堆积）
						depositCell = i;
					}
					else
					{
						// 本 hop 到达 d：沿途沉降 = 基率 + 风变弱的份额（能力降 → 落沙），余量续行
						float settle = SettleBase + MathF.Max(0f, 1f - _capacity[d] / _capacity[i]);
						deposit = total * Math.Clamp(settle, 0f, 1f);
						depositCell = d;
						_load[d] += total - deposit;
					}
					if (deposit > 0f)
					{
						sediment[depositCell] += deposit;   // 只动 pool 0（就地应用，无跨池转化）
						_depositedPerCell[depositCell] += deposit;
						deposited += deposit;
						landed += deposit;
						if (surfaceHeight[depositCell] <= seaLevel) toOcean += deposit;
					}
				}
				inFlight -= landed;
			}
			// 兜底：超 pass 上限的残余就地落底（守恒优先——账本不许漏）
			for (int i = 0; i < n; i++)
			{
				if (_load[i] <= 0f) continue;
				sediment[i] += _load[i];
				_depositedPerCell[i] += _load[i];
				deposited += _load[i];
				if (surfaceHeight[i] <= seaLevel) toOcean += _load[i];
				_load[i] = 0f;
			}

			ErodedMassLastStep = eroded;
			DepositedMassLastStep = deposited;
			DeliveredToOceanLastStep = toOcean;
		}
	}
}
