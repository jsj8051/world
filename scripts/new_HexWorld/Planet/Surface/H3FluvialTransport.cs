using System;
using World.Utils;                             // SphericalFbmNoise（EarthRadiusKm 物理半径常量）

namespace World.NewHexWorld.Plate
{
// 河流输沙：侵蚀 → 搬运 → 沉积三段式水系模型，
// 补齐 H3SurfaceProcesses 扩散侵蚀只有"搬一格"的缺口——山上的物质现在能沿汇流链被搬到几十格外
// 的洼地/海边落淤（内流盆地、三角洲、大陆架沉积自然涌现），搬运距离不再是一格。
//
// 每步一个离散回合（与步长解耦）：
//   ① 流向：每陆格（height > 0）取最陡下降邻为下游；无更低位 = 内流洼地（汇）；height ≤ 0
//      （水面下）= 沉积汇（海）。
//   ② 水：径流深 run[i]（m/yr）= 自产降水 + 上游汇入，降序一遍累积——均匀格面口径下
//      "累积径流深"即汇水面积 × 降水（H3WaterCycle 同款面积消去）。
//   ③ 水力几何：径流深 × 格面积 = 绝对流量 Q（m³/s），侵蚀力度 = 床面剪切力（按水量和水速计算）：
//        流速 v = kv·Q^0.1·S^0.2（Leopold-Maddock 水力几何：v 随 Q 弱增；kv = 2.5 标定
//          Q=100 m³/s、S=1e-3 → v≈1 m/s，Amazon 档 Q=2e5、S=5e-5 → v≈1.1 m/s）
//        河宽 w = kw·√Q（kw = 2；水深 d = Q/(w·v) 自洽）
//        床面剪切力 τ = ρw·g·d·S（Pa）——**侵蚀的判据**：τ 只有超过池的**临界剪切力 τc** 才能啃动
//          （松散沉积 ~1 Pa 一冲就走，基岩 30–80 Pa 只有山区大河才啃得动 ⇒ 侵蚀选择性来自水力学）
//   ④ 挟沙力 C = Kc × v × 径流（"水量 × 水速"直译；v 把坡度带进来）：
//        负载 L &lt; C → **侵蚀**：啃 ErodeEfficiency × (C − L)，**只从 τ > τc 的池**按存量比例抽；
//        负载 L &gt; C → **沉积**：超载部分落 sediment 池；海拔 &gt; DepositCapM 的山腰不淤
//        （载续走——防高山被自家输沙埋掉）；汇（洼地/海）必淤。
//   ⑤ 河道判据：汇流面积 ≥ ChannelMinAreaKm2 才是河（水蚀只有河流的兜底）：
//      坡面（源区小汇流）负载原样通过、不啃不淤，坡面供给走 8b 地表过程。
//   ⑥ 搬运：L_out = L − 淤 + 冲，全部推给下游；降序处理 ⇒ 到格时负载必已集齐。
//   ⑦ 终局：所有负载在本步内落定（下游链尽头必是洼地或海）→ Σ侵蚀 = Σ沉积，组内守恒、
//      动质量审计零记账；海拔变化由调用方侵蚀后重跑均衡+海平面（Step 8b 既有两遍）。
//
// 侵蚀/沉积的单位 = kg/m²（守恒组口径）；水 = m/yr。传入的降水必须是**闭合后的**绝对量
// （结构场 × λ）：τ ∝ Q^0.4 对水量尺度敏感，不能锚在任意尺度上。
// 分辨率依赖：坡度/格宽用平均格宽折算（res 间有标度差，判读档调好后按需缩放）。
public sealed class H3FluvialTransport
{
	// ── 水力几何旋钮（地球档标定，判读后按需调）──
	/// <summary>流速系数：v = 2.5 · Q^0.1 · S^0.2（m/s）。锚点：Q=100 m³/s、S=1e-3 → v≈1；
	/// Q=2e5（Amazon）、S=5e-5 → v≈1.1（真实大河 1–2 m/s 量级）。</summary>
	public const float VelocityCoefMs = 2.5f;
	public const float VelocityQExponent = 0.1f;      // Leopold-Maddock：流速随流量弱增
	public const float VelocitySlopeExponent = 0.2f;
	/// <summary>河宽系数：w = 2·√Q（m；Q=100 → 20 m，Q=2e5 → ~900 m——水力几何经验律）。</summary>
	public const float WidthCoef = 2f;
	public const float WaterDensityKgM3 = 1000f;
	public const float GravityMs2 = 9.8f;
/// <summary>挟沙力系数（kg/m² per m/s per m/yr）：C = 330 × v × 径流。</summary>
	public const float CapacityCoefKgPerM2 = 330f;
/// <summary>河道判据：汇流面积 ≥ 300 km² 才算河（水蚀只有河流的兜底）。</summary>
	public const float ChannelMinAreaKm2 = 300f;
	/// <summary>侵蚀效率：每步啃容量缺口的份额（0.1 = 十步逼近携载平衡）。</summary>
	public const float ErodeEfficiency = 0.10f;
/// <summary>沉积海拔帽（m）：更高的坡面不淤，负载继续下行；
/// 洼地汇不受帽限（守恒优先：负载到汇必须落底）。</summary>
	public const float DepositCapM = 300f;
	// ── 临界床面剪切力 τc（Pa，池序 = ConservedPools；侵蚀选择性所在）──
	// Shields 量级：松散细沙起动 ~1 Pa；固结沉积岩 ~30；火山岩 ~40；深成岩 ~60；变质岩最硬 ~80。
	/// <summary>松散沉积（sediment 池）：一冲就走。</summary>
	public const float TauCriticalSedimentPa = 1f;
	/// <summary>沉积岩（sedimentary 池）。</summary>
	public const float TauCriticalSedimentaryPa = 30f;
	/// <summary>变质岩（metamorphic 池）：最硬，只有山区大河啃得动。</summary>
	public const float TauCriticalMetamorphicPa = 80f;
	/// <summary>长英质深成岩（felsic plutonic 池）。</summary>
	public const float TauCriticalFelsicPlutonicPa = 60f;
	/// <summary>长英质火山岩（felsic volcanic 池）。</summary>
	public const float TauCriticalFelsicVolcanicPa = 40f;

	const float SecondsPerYear = 365.25f * 24f * 3600f;
	const float ChannelMinAreaM2 = ChannelMinAreaKm2 * 1e6f;

	readonly Ball _ball;
	readonly int[] _order;              // 陆格降序处理表（高度 ↓，下标 ↑ 破并列 → 确定性）
	readonly long[] _sortKeys;          // 打包排序键（高 32 = 高度键，低 32 = 下标；免委托比较器）
	readonly int[] _target;             // 陡降流向（-1 = 洼地汇）
	readonly int[] _contrib;            // 汇流格数（自产 + 上游；×格面积 = 汇流面积，河道判据用）
	readonly float[] _runoff;           // 累积径流深（m/yr）
	readonly float[] _load;             // 输沙负载（kg/m²）
	readonly float[] _velocity;         // 诊断：本步流速（m/s；非河道 = 0）
	readonly float[] _shear;            // 诊断：本步床面剪切力（Pa；非河道 = 0）
	readonly float[] _erodedPerCell;    // 诊断：本步冲刷量
	readonly float[] _depositedPerCell; // 诊断：本步落淤量
	readonly float[][] _delta;          // 守恒组 5 场净变化量（顺序 = ConservedPools）

	public H3FluvialTransport(Ball ball)
	{
		_ball = ball;
		int n = ball.CellIds.Length;
		_order = new int[n];
		_sortKeys = new long[n];
		_target = new int[n];
		_contrib = new int[n];
		_runoff = new float[n];
		_load = new float[n];
		_velocity = new float[n];
		_shear = new float[n];
		_erodedPerCell = new float[n];
		_depositedPerCell = new float[n];
		_delta = new float[5][];
		for (int k = 0; k < 5; k++) _delta[k] = new float[n];
	}

	// ── 每步判读口（Apply 时更新）──
	public double ErodedMassLastStep { get; private set; }      // 本步冲刷总质量（kg/m²·格 求和）
	public double DepositedMassLastStep { get; private set; }   // 本步落淤总质量（含入海）
	public double DeliveredToOceanLastStep { get; private set; } // 其中淤到水面以下（三角洲/大陆架料）
	/// <summary>逐格冲刷/落淤量（诊断/出图）。**复用缓冲**：每步覆盖，跨步持有无效。</summary>
	public float[] ErodedPerCell => _erodedPerCell;
	public float[] DepositedPerCell => _depositedPerCell;
	/// <summary>逐格流速（m/s；水力几何判读口——"水速"落地在哪一格一眼可见）。</summary>
	public float[] VelocityPerCell => _velocity;
	/// <summary>逐格床面剪切力（Pa；τ &gt; τc 才有侵蚀——侵蚀选择性判读口）。</summary>
	public float[] ShearPerCell => _shear;

		/// <summary>跑三段输沙并应用进守恒组。组总量不变（侵蚀=沉积）；<paramref name="precipMPerYear"/>
		/// = 本步**闭合**降水（m/yr = 结构场 × λ）；<paramref name="scale"/> = 强度倍率（乘挟沙力）。</summary>
		public void Apply(H3PlateFields fields, float[] height, float[] precipMPerYear, float scale)
		{
			int n = fields.Count;
			for (int k = 0; k < 5; k++) Array.Clear(_delta[k], 0, n);
			Array.Clear(_runoff, 0, n);
			Array.Clear(_contrib, 0, n);
			Array.Clear(_load, 0, n);
			Array.Clear(_velocity, 0, n);
			Array.Clear(_shear, 0, n);
			Array.Clear(_erodedPerCell, 0, n);
			Array.Clear(_depositedPerCell, 0, n);
			ErodedMassLastStep = 0;
			DepositedMassLastStep = 0;
			DeliveredToOceanLastStep = 0;

			var pools = fields.ConservedPools();
			var neighbors = _ball.CellNeighbors;
			float cellWidthM = MathF.Sqrt(4f * MathF.PI * SphericalFbmNoise.EarthRadiusKm
				* SphericalFbmNoise.EarthRadiusKm / n) * 1000f;
			float cellAreaM2 = cellWidthM * cellWidthM;
			float qPerRunoff = cellAreaM2 / SecondsPerYear;      // (m³/s) per (m/yr 径流深)

			// ① 流向：陆格取最陡下降邻（严格 >：并列取最先邻居 = id 升序，确定性）；水面下 = 汇。
			//   地图型并行：写只落 _target[i] 本格，逐元素算式与串行逐位一致。
			ParallelLoops.For(n, i =>
			{
				_target[i] = -1;
				if (height[i] <= 0f) return;
				float bestDrop = 0f;
				int best = -1;
				foreach (int nb in neighbors[i])
				{
					float drop = height[i] - height[nb];
					if (drop > bestDrop) { bestDrop = drop; best = nb; }
				}
				_target[i] = best;
			});
			int landCount = 0;
			for (int i = 0; i < n; i++)
				if (height[i] > 0f) _order[landCount++] = i;
			// 高 → 低（并列下标升序）排序：float 位型映射成有序 32 位键、与下标打包进 long
			// （高 32 = 高度键取反 = 降序，低 32 = 下标 = 并列升序），Array.Sort(keys, items) 走
			// 无委托快路径——Comparer<int>.Create 的逐比较委托回调在 res5（~百万陆格 × 20 余次
			// 比较）是每步百毫秒级常数。键构造与旧比较器给出同一全序（height ≥ 0、无 NaN）。
			for (int k = 0; k < landCount; k++)
			{
				int i = _order[k];
				int bits = BitConverter.SingleToInt32Bits(height[i]);
				int ordered = bits ^ ((bits >> 31) & 0x7FFFFFFF);   // float 全序 → int 全序（升序）
				_sortKeys[k] = ((long)(uint)~ordered << 32) | (uint)i;
			}
			Array.Sort(_sortKeys, _order, 0, landCount);

			// ②③④⑤⑥ 单遍降序：径流/汇流累积 → 水力几何 → 挟沙力判据（τ-τc 侵蚀门）→ 负载搬运
			double eroded = 0, deposited = 0;
			for (int k = 0; k < landCount; k++)
			{
				int i = _order[k];
				_runoff[i] += precipMPerYear[i];          // 自产 + 上游汇入（上游已处理）
				_contrib[i] += 1;                          // 本格自产汇流（上游格数已随径流传入）
				float h = height[i];
				int t = _target[i];
				float slope = t >= 0 ? (h - height[t]) / cellWidthM : 0f;
				float lin = _load[i];

				// 水力几何：绝对流量 Q → 流速/河宽/水深/床面剪切力。
				// 洼地汇（t < 0）坡度 0 ⇒ v = τ = C = 0 ⇒ 只淤不冲。
				bool isChannel = _contrib[i] * cellAreaM2 >= ChannelMinAreaM2;
				float velocity = 0f, shear = 0f, capacity = 0f;
				if (isChannel && slope > 0f && _runoff[i] > 0f)
				{
					float q = _runoff[i] * qPerRunoff;     // m³/s
					velocity = VelocityCoefMs * MathF.Pow(q, VelocityQExponent)
						* MathF.Pow(slope, VelocitySlopeExponent);
					float width = WidthCoef * MathF.Sqrt(q);
					float depth = q / (width * velocity);
					shear = WaterDensityKgM3 * GravityMs2 * depth * slope;      // Pa
					capacity = CapacityCoefKgPerM2 * velocity * _runoff[i] * scale;
				}
				_velocity[i] = velocity;
				_shear[i] = shear;

				float lout;
				if (!isChannel)
				{
					// 非河道坡面（汇流面积不足）：负载原样通过——侵蚀只属于河道，
					// 坡面供给走 8b 地表过程，河在这里不啃也不淤。
					lout = lin;
				}
				else if (lin >= capacity)
				{
					// 沉积：超载落底。山腰帽内不淤（载续走）；洼地汇必淤（守恒优先）
					float deposit = lin - capacity;
					if (h > DepositCapM && t >= 0) deposit = 0f;
					if (deposit > 0f)
					{
						_delta[0][i] += deposit;          // 落成 sediment（埋藏后走成岩/变质链）
						_depositedPerCell[i] = deposit;
						deposited += deposit;
					}
					lout = lin - deposit;
				}
				else
				{
					// 侵蚀：容量富余 → 啃床。⚠️ 只啃 τ > τc 的池（水力学选择性：松散沉积一冲就走，
					// 基岩要山区大河）；从可蚀池按存量比例抽（clamp 到可蚀总存量，防扣成负质量）。
					float erodibleStock = 0f;
					if (shear > TauCriticalSedimentPa) erodibleStock += pools[0][i];
					if (shear > TauCriticalSedimentaryPa) erodibleStock += pools[1][i];
					if (shear > TauCriticalMetamorphicPa) erodibleStock += pools[2][i];
					if (shear > TauCriticalFelsicPlutonicPa) erodibleStock += pools[3][i];
					if (shear > TauCriticalFelsicVolcanicPa) erodibleStock += pools[4][i];
					float erode = MathF.Min(ErodeEfficiency * (capacity - lin), erodibleStock);
					if (erode > 0f)
					{
						float ratio = erode / erodibleStock;
						if (shear > TauCriticalSedimentPa) _delta[0][i] -= pools[0][i] * ratio;
						if (shear > TauCriticalSedimentaryPa) _delta[1][i] -= pools[1][i] * ratio;
						if (shear > TauCriticalMetamorphicPa) _delta[2][i] -= pools[2][i] * ratio;
						if (shear > TauCriticalFelsicPlutonicPa) _delta[3][i] -= pools[3][i] * ratio;
						if (shear > TauCriticalFelsicVolcanicPa) _delta[4][i] -= pools[4][i] * ratio;
						_erodedPerCell[i] = erode;
						eroded += erode;
					}
					lout = lin + erode;
				}

				if (t >= 0)
				{
					_runoff[t] += _runoff[i];
					_contrib[t] += _contrib[i];
					_load[t] += lout;
				}
				// t < 0：洼地汇已在上面淤光（capacity = 0 → deposit = 载），负载终止
			}

			// ⑦ 入海口：水面下格不在处理表里，攒到的负载全部落底（三角洲/大陆架料；
			//   近海沉积之后随俯冲再循环回地幔）
			double toOcean = 0;
			for (int i = 0; i < n; i++)
			{
				if (height[i] > 0f || _load[i] <= 0f) continue;
				_delta[0][i] += _load[i];
				_depositedPerCell[i] = _load[i];
				deposited += _load[i];
				toOcean += _load[i];
			}

			// delta 应用（搬运全在守恒组内 → 组总量不变）。地图型并行：写只落 pool[i] 本格。
			for (int k = 0; k < 5; k++)
			{
				var pool = pools[k];
				var d = _delta[k];
				ParallelLoops.For(n, i => pool[i] += d[i]);
			}
			ErodedMassLastStep = eroded;
			DepositedMassLastStep = deposited;
			DeliveredToOceanLastStep = toOcean;
		}
	}
}
