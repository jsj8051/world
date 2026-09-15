using System;
using World.Tectonics;          // MaterialDensity + Units —— 03 §8：沿用老常量表，不新造

namespace World.NewHexWorld.Plate
{
	// 地表过程（2026-09-15 自旧项目 `TectonicsSimulation.Erosion` / 老 `Crust.Model*` 逐行移植；
	// 原版出处 = Crust.js model_*，M3）：侵蚀 / 风化 / 成岩 / 变质，四步合一份 delta 统一应用。
	//
	//   侵蚀 —— 沿格边把高处的守恒组物质搬到低处（扩散式削高填低）：每条下坡边搬运量 =
	//     max(0, h差) × 降水 × 秒 × 系数 × 强度 × ρ；各池按存量占比依次分担（clamp 到可给量），
	//     均摊到该格全部下坡邻。只动**守恒组 5 场**（sediment/sedimentary/metamorphic/felsic×2），
	//     mafic 不参与（洋壳不靠降水侵蚀）。
	//   风化 —— 出露基岩 → sediment：速率 = 全图平均高差 × 系数 × 强度 × 降水 × 秒 × ρ_felsic
	//     × 基岩暴露度（sediment 盖 < 1 m 才暴露，盖住了就停风化）；从 sedimentary/metamorphic/
	//     felsic×2 四池按存量比例抽。
	//   成岩 —— sediment 的上覆压力 > 2.2 MPa（≈ 150 m 沉积物柱）的部分压实成 sedimentary。
	//   变质 —— sediment + sedimentary 柱压 > 300 MPa（≈ 11 km 沉积岩柱）的 sedimentary → metamorphic。
	//
	// 口径与守恒：全部走 **delta 制**（先算净变化量、四步跑完统一应用）——搬运与转化全部发生在
	// 守恒组 5 场内部 ⇒ 组内总量严格不变，动质量审计（CrustCreatedTotal/CrustDestroyedTotal）
	// 零记账。海拔/海平面由调用方在应用后重算（老实现同款两遍：侵蚀后再跑一次均衡+水量守恒）。
	//
	// 与老实现的三处差异：
	//   ① 邻居 = H3（五边格 5 邻 / 六边格 6 邻；老 SphereGrid 均匀度）——搬运均摊按实际邻数除；
	//   ② delta / 地表高度缓冲每步复用（res5 = 2M 格 × 6 数组，逐步 new 是几十 MB 级垃圾——
	//      老实现 2026-08-02 同款性能修正的延续）；
	//   ③ 老的矿藏事件累积（MineralSed/MineralMeta）不移植——新项目还没有矿藏层。
	public sealed class H3SurfaceProcesses
	{
		/// <summary>全球陆地平均降水（m/s；1.05 m/年）。与老 `Crust.PrecipMS` 同源同值。</summary>
		public const float PrecipMS = 1.05f / 365.25f / 24f / 3600f;
		/// <summary>侵蚀/风化效率系数。与老 `Crust.ErosiveFactor` 同源同值（JS 原版 1.8e-7）。</summary>
		public const float ErosiveFactor = 1.8e-7f;
		/// <summary>成岩门槛（Pa）：sediment 柱压超过它 → 压实成 sedimentary。</summary>
		public const float LithificationPressurePa = 2.2e6f;
		/// <summary>变质门槛（Pa）：sediment+sedimentary 柱压超过它 → sedimentary 变 metamorphic。</summary>
		public const float MetamorphismPressurePa = 300e6f;

		readonly Ball _ball;
		readonly float[] _surfaceHeight;
		readonly float[][] _delta;             // 守恒组 5 场的净变化量（顺序 = ConservedPools）
		readonly float[] _erosionMoved;        // 侵蚀诊断：每格净出站搬运量（判读用）

		public H3SurfaceProcesses(Ball ball)
		{
			_ball = ball;
			int n = ball.CellIds.Length;
			_surfaceHeight = new float[n];
			_delta = new float[5][];
			for (int k = 0; k < 5; k++) _delta[k] = new float[n];
			_erosionMoved = new float[n];
		}

		// ── 每步判读口（Apply 时更新）──
		public double ErosionMovedMassLastStep { get; private set; }   // 侵蚀沿边搬运总质量（kg/m²·格 求和）
		public double WeatheredMassLastStep { get; private set; }      // 风化 岩石 → sediment
		public double LithifiedMassLastStep { get; private set; }      // 成岩 sediment → sedimentary
		public double MetamorphosedMassLastStep { get; private set; }  // 变质 sedimentary → metamorphic
		/// <summary>逐格侵蚀出站搬运量（诊断/出图）。**复用缓冲**：每步覆盖，跨步持有无效。</summary>
		public float[] ErosionMovedPerCell => _erosionMoved;

		/// <summary>地表高度（m，相对海平面，水下截 0）——侵蚀/风化的地形输入。每步复用同一缓冲。</summary>
		public float[] ComputeSurfaceHeight(H3Isostasy isostasy)
		{
			int n = _surfaceHeight.Length;
			float seaLevel = isostasy.SeaLevel;
			var displacement = isostasy.Displacement;
			for (int i = 0; i < n; i++)
				_surfaceHeight[i] = MathF.Max(displacement[i] - seaLevel, 0f);
			return _surfaceHeight;
		}

		/// <summary>跑四步地表过程并把 delta 应用进守恒组。质量守恒组总量不变（搬运/转化都在组内）。
		/// <paramref name="erosionScale"/> = 侵蚀/风化强度倍率（持有方 H3DynamicTectonics.ErosionScale）。</summary>
		public void Apply(H3PlateFields fields, float[] surfaceHeight, MaterialDensity material,
			float stepMy, float erosionScale)
		{
			int n = fields.Count;
			for (int k = 0; k < 5; k++) Array.Clear(_delta[k], 0, n);
			ErosionMovedMassLastStep = 0;
			WeatheredMassLastStep = 0;
			LithifiedMassLastStep = 0;
			MetamorphosedMassLastStep = 0;

			float seconds = stepMy * Units.MEGAYEAR;               // My → 秒（老 Units 口径）
			ModelErosion(fields, surfaceHeight, seconds, material, erosionScale);
			ModelWeathering(fields, surfaceHeight, seconds, material, erosionScale);
			ModelLithification(fields, material);
			ModelMetamorphosis(fields);

			// delta 应用（老 Crust.AddDelta 的守恒组版）：搬运/转化全部组内，组总量不变
			var conserved = fields.ConservedPools();
			for (int k = 0; k < 5; k++)
			{
				var pool = conserved[k];
				var d = _delta[k];
				for (int i = 0; i < n; i++) pool[i] += d[i];
			}
		}

		// ── ① 侵蚀（老 Crust.ModelErosion 逐行移植；share 计算下沉到逐格局部变量，免 frac 缓冲）──
		// 出站总量 = Σ_下坡邻 max(0, h差) × 降水 × 秒 × 系数 × 强度 × ρ_felsic；
		// 五池按存量占比依次分担（f = 存量/剩余出站量 clamp 到 [0,1]，剩余量递减），均摊到各下坡邻。
		void ModelErosion(H3PlateFields fields, float[] surfaceHeight, float seconds, MaterialDensity material,
			float erosionScale)
		{
			int n = fields.Count;
			var neighbors = _ball.CellNeighbors;
			var delta = _delta;
			var pools = fields.ConservedPools();
			float rho = material.FelsicPlutonic;                   // 老实现硬编码 2600 = 密度表同值
			float edgeScale = PrecipMS * seconds * ErosiveFactor * erosionScale * rho;
			Array.Clear(_erosionMoved, 0, n);
			Span<float> share = stackalloc float[5];

			double moved = 0;
			for (int i = 0; i < n; i++)
			{
				float hi = surfaceHeight[i];
				var nb = neighbors[i];

				// ① 出站搬运总量（先扫一遍下坡邻）
				float outbound = 0;
				for (int k = 0; k < nb.Length; k++)
				{
					float diff = hi - surfaceHeight[nb[k]];
					if (diff > 0) outbound += diff * edgeScale;
				}
				if (outbound <= 0) continue;

				// ② 各池分担份额（按存量占比依次扣减；顺序 = ConservedPools：sediment, sedi, meta, felsicP, felsicV）
				float remain = outbound;
				float shareSum = 0;
				for (int p = 0; p < 5; p++)
				{
					float f = remain > 1e-9f ? pools[p][i] / remain : 0f;
					f = Math.Clamp(f, 0f, 1f);
					share[p] = f / nb.Length;                      // 均摊到全部下坡邻
					remain *= 1f - f;
					shareSum += share[p];
				}

				// ③ 沿边搬运：from 减、to 加（共享边两侧各处理一次由 from 视角独占，不判重）
				float cellMoved = 0;
				for (int k = 0; k < nb.Length; k++)
				{
					int j = nb[k];
					float diff = hi - surfaceHeight[j];
					if (diff <= 0) continue;
					float transfer = diff * edgeScale;
					cellMoved += transfer;
					for (int p = 0; p < 5; p++)
					{
						float t = transfer * share[p];
						if (t == 0) continue;
						delta[p][i] -= t;
						delta[p][j] += t;
					}
				}
				_erosionMoved[i] = cellMoved * shareSum;
				moved += _erosionMoved[i];
			}
			ErosionMovedMassLastStep = moved;
		}

		// ── ② 风化（老 Crust.ModelWeathering 逐行移植）：出露基岩 → sediment ──
		void ModelWeathering(H3PlateFields fields, float[] surfaceHeight, float seconds, MaterialDensity material,
			float erosionScale)
		{
			const float criticalSedimentMass = 1f * 1500f;         // 1 m 沉积物盖 × ρ_sed：盖住就停风化
			int n = fields.Count;
			var neighbors = _ball.CellNeighbors;

			// 全图平均高差（粗糙度输入；老实现同款全边扫描）
			double avgDiffSum = 0;
			long cnt = 0;
			for (int i = 0; i < n; i++)
			{
				var nb = neighbors[i];
				for (int k = 0; k < nb.Length; k++)
				{
					avgDiffSum += MathF.Abs(surfaceHeight[i] - surfaceHeight[nb[k]]);
					cnt++;
				}
			}
			float avgDiff = cnt > 0 ? (float)(avgDiffSum / cnt) : 0f;

			var sediment = fields.Sediment;
			var sedimentary = fields.Sedimentary;
			var metamorphic = fields.Metamorphic;
			var felsicPlutonic = fields.FelsicPlutonic;
			var felsicVolcanic = fields.FelsicVolcanic;
			double weathered = 0;
			for (int i = 0; i < n; i++)
			{
				float exposure = Math.Clamp(1f - sediment[i] / criticalSedimentMass, 0f, 1f);
				if (exposure <= 0) continue;

				float weathering = avgDiff * ErosiveFactor * erosionScale * PrecipMS * seconds
					* material.FelsicPlutonic * exposure;
				if (weathering <= 0) continue;

				float bedrock = sedimentary[i] + metamorphic[i] + felsicPlutonic[i] + felsicVolcanic[i];
				if (bedrock <= 0) continue;
				weathering = MathF.Min(weathering, bedrock);
				float ratio = weathering / bedrock;

				_delta[0][i] += weathering;                        // → sediment
				_delta[1][i] -= sedimentary[i] * ratio;
				_delta[2][i] -= metamorphic[i] * ratio;
				_delta[3][i] -= felsicPlutonic[i] * ratio;
				_delta[4][i] -= felsicVolcanic[i] * ratio;
				weathered += weathering;
			}
			WeatheredMassLastStep = weathered;
		}

		// ── ③ 成岩（老 Crust.ModelLithification 移植）：sediment 柱压超 2.2 MPa → sedimentary ──
		void ModelLithification(H3PlateFields fields, MaterialDensity material)
		{
			var sediment = fields.Sediment;
			int n = fields.Count;
			double lithified = 0;
			for (int i = 0; i < n; i++)
			{
				float overpressure = sediment[i] * 9.8f;           // kg/m² × m/s² = Pa
				float excess = overpressure - LithificationPressurePa;
				if (excess <= 0) continue;
				float amount = Math.Clamp(excess / 9.8f, 0f, sediment[i]);
				_delta[0][i] -= amount;
				_delta[1][i] += amount;
				lithified += amount;
			}
			LithifiedMassLastStep = lithified;
		}

		// ── ④ 变质（老 Crust.ModelMetamorphosis 移植）：sediment+sedimentary 柱压超 300 MPa → metamorphic ──
		void ModelMetamorphosis(H3PlateFields fields)
		{
			var sediment = fields.Sediment;
			var sedimentary = fields.Sedimentary;
			int n = fields.Count;
			double metamorphosed = 0;
			for (int i = 0; i < n; i++)
			{
				float overpressure = (sediment[i] + sedimentary[i]) * 9.8f;
				float excess = overpressure - MetamorphismPressurePa;
				if (excess <= 0) continue;
				float amount = Math.Clamp(excess / 9.8f, 0f, sedimentary[i]);
				_delta[1][i] -= amount;
				_delta[2][i] += amount;
				metamorphosed += amount;
			}
			MetamorphosedMassLastStep = metamorphosed;
		}
	}
}
