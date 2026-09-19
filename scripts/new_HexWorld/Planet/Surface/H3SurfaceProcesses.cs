using System;
using World.Tectonics;          // MaterialDensity + Units —— 沿用老常量表
using World.Utils;              // ParallelLoops（确定性并行地图循环）

namespace World.NewHexWorld.Plate
{
	// 地表过程：坡面侵蚀 / 风化 / 成岩 / 变质，四步合一份 delta 统一应用。
	//
	// 侵蚀按介质开合（介质不存在则该介质贡献 0）：
	//   · 风：风强 × 干旱度 × 裸露度（方向性搬运在 H3AeolianTransport，这里是坡面强度门）；
	//   · 重力：格间落差超门槛才滑，门槛按柱内松散占比在 300–1500 m 间插值（松散柱先滑）；
	//   · 冰川：年均温 < 0 °C 且有降雪。
	//   · 水：坡面水蚀退役——水蚀只有河流（H3FluvialTransport）；水保留在风化门
	//     （化学水解要水，风化是就地转化不是搬运）。
	//
	// 四步全走 delta 制（先算净变化量、跑完统一应用），搬运与转化都在守恒组 5 场内部
	// ⇒ 组内总量严格不变，动质量审计零记账；海拔/海平面由调用方在应用后重算。
	public sealed class H3SurfaceProcesses
	{
		/// <summary>全球陆地平均降水（m/s；1.05 m/年）。参考态（无降水场时）的降水速率，
		/// 也是搬运基准系数 baseCoef 的来源——"参考态 = 单一常量口径"逐位成立。</summary>
		public const float PrecipMS = 1.05f / 365.25f / 24f / 3600f;
		/// <summary>参考降水（mm/yr；= 1.05 m/yr）——逐格降水进"权重"时的对照值。</summary>
		public const float PrecipRefMmYear = 1050f;
		/// <summary>侵蚀/风化效率系数。</summary>
		public const float ErosiveFactor = 1.8e-7f;
		/// <summary>成岩门槛（Pa）：sediment 柱压超过它 → 压实成 sedimentary。</summary>
		public const float LithificationPressurePa = 2.2e6f;
		/// <summary>变质门槛（Pa）：sediment+sedimentary 柱压超过它 → sedimentary 变 metamorphic。</summary>
		public const float MetamorphismPressurePa = 300e6f;

		// ── 介质门旋钮（参考态下全部不生效 ⇒ 参考标定不变）──
		/// <summary>水介质：权重下限。0 = 无水就完全不被"水蚀"。</summary>
		public const float WaterWeightFloor = 0f;
		/// <summary>水介质：权重上限（雨林比参考档强一半）。</summary>
		public const float WaterWeightCap = 1.5f;
		/// <summary>风介质系数：rate_风 = 风强 × 干旱度 × 裸露度 × 它（Earth 干旱区的量级）。</summary>
		public const float WindErodibility = 0.5f;
		/// <summary>重力介质：格间落差超过它才发生块体运动——固结档（基岩柱；m）。
		/// 真实休止角在几十 km 的格上不可达 ⇒ 用"格间落差"当网格尺度代理；
		/// 实际门槛按柱内松散占比向松散档插值。</summary>
		public const float GravityReliefSolidM = 1500f;
		/// <summary>重力介质：松散档（纯沉积物柱；m）——松散堆积体休止角浅，300 m 落差就滑。</summary>
		public const float GravityReliefLooseM = 300f;
		/// <summary>重力介质系数：超过门槛的那部分落差按它的倍率搬运（块体运动比坡面径流快）。</summary>
		public const float GravityErodibility = 3f;
		/// <summary>冰川介质：门限温度（°C）——年均温低于它才算"有冰川"。</summary>
		public const float GlacierTempC = 0f;
		/// <summary>冰川介质系数：冰蚀比同条件的水蚀强（冰下磨蚀 + 拔蚀）。</summary>
		public const float GlacierErodibility = 2f;
		/// <summary>参考态年均温（°C；无温度场时用）——温带，冰川门关闭。</summary>
		public const float ReferenceTempC = 15f;

		/// <summary>水介质口径的参考产流（mm/yr）：参考气候（P = 1050 mm/yr、T = 10 °C）档下
		/// 产流 ÷ 本值 = 权重 1。没有归一化——这颗行星实际有多少水真的进风化门。</summary>
		public const float RunoffRefMmPerYear = 467.5f;

		/// <summary>介质的逐格输入（演化步由 H3DynamicTectonics 每步算好传入）。
		/// 数组为 null = 该介质按参考态处理（水 = 参考产流、温带、风 = 环流带档位兜底）。</summary>
		public readonly struct ErosionMedium
		{
			/// <summary>逐格年降水（mm/yr）——必须是闭合后的（全球 Σ = 洋面蒸发总量，见 H3WaterCycle 的 λ）。
			/// 未闭合结构场只定"雨下在哪"，直接当绝对值 = 把侵蚀强度锚在任意尺度上。</summary>
			public readonly float[] PrecipMmYear;
			/// <summary>逐格年均温（°C）——PET 与冰川门的输入。</summary>
			public readonly float[] TempC;
			/// <summary>逐格风速 |u|（m/s）。null = 退回环流带档位兜底；
			/// 风介质权重 = |u| / H3Wind.ReferenceWindSpeedMS（Ferrel 海平面 = 1.0）。</summary>
			public readonly float[] WindMS;
			public ErosionMedium(float[] precipMmYear, float[] tempC)
			{
				PrecipMmYear = precipMmYear;
				TempC = tempC;
				WindMS = null;
			}
			public ErosionMedium(float[] precipMmYear, float[] tempC, float[] windMS)
			{
				PrecipMmYear = precipMmYear;
				TempC = tempC;
				WindMS = windMS;
			}
			/// <summary>参考态（水 = 权重 1、温带：无风蚀、无冰蚀）。</summary>
			public static ErosionMedium Reference => default;
		}

		// ── 岩性可蚀性 K ──
		/// <summary>软物质（沉积物/沉积岩）使 K 上升、硬物质（变质岩）使 K 下降的增益。
		/// K = clamp(1 + KSoftGain·f软 − KHardGain·f硬, KFloor, KCeil)，f = 柱内厚度占比
		/// （池是整柱混合，用柱内比例当代理：盆地充填后变软、变质盾地变硬）。</summary>
		public const float KSoftGain = 1.5f;
		public const float KHardGain = 0.6f;
		public const float KFloor = 0.5f;
		public const float KCeil = 2f;

		readonly Ball _ball;
		readonly float[] _surfaceHeight;
		readonly float[][] _delta;             // 守恒组 5 场的净变化量（顺序 = ConservedPools）
		readonly float[] _erosionMoved;        // 侵蚀诊断：每格净出站搬运量（判读用）
		readonly float[] _beltWindiness;       // 逐格环流带风强（步不变量：只依赖 Ball 的纬度带；无风速场时的兜底）
		readonly float[] _mediumRate;          // 逐格四介质率之和（水+风+冰川；风化门与坡面搬运门共用）
		readonly float[] _waterRate;           // 逐格水介质率（诊断/判读）
		readonly float[] _windRate;            // 逐格风介质率（诊断/判读）
		readonly float[] _glacierRate;         // 逐格冰川介质率（诊断/判读）

		public H3SurfaceProcesses(Ball ball)
		{
			_ball = ball;
			int n = ball.CellIds.Length;
			_surfaceHeight = new float[n];
			_delta = new float[5][];
			for (int k = 0; k < 5; k++) _delta[k] = new float[n];
			_erosionMoved = new float[n];
			_mediumRate = new float[n];
			_waterRate = new float[n];
			_windRate = new float[n];
			_glacierRate = new float[n];
			_rockErodibility = new float[n];
			for (int i = 0; i < n; i++) _rockErodibility[i] = 1f;

			// 环流带风强（步不变量）：`H3Wind.WindAt` 只出方向不出风速 ⇒ 按带取档位（见头注 ②）。
			_beltWindiness = new float[n];
			var centers = ball.CellCenters;
			for (int i = 0; i < n; i++)
			{
				float latDeg = MathF.Asin(Math.Clamp(centers[i].Normalized().Y, -1f, 1f)) * (180f / MathF.PI);
				_beltWindiness[i] = latDeg switch
				{
					var a when MathF.Abs(a) < 30f => HadleyWindiness,
					var a when MathF.Abs(a) < 60f => FerrelWindiness,
					_ => PolarWindiness,
				};
			}
		}

		/// <summary>环流带风强档位（无逐格风速场时的兜底）。</summary>
		public const float HadleyWindiness = 0.6f;    // 信风带（含赤道无风带，故低于中纬）
		public const float FerrelWindiness = 1.0f;    // 西风带（最强，参考档）
		public const float PolarWindiness = 0.8f;     // 极地东风

		// ── 每步判读口（Apply 时更新）──
		public double ErosionMovedMassLastStep { get; private set; }   // 侵蚀沿边搬运总质量（kg/m²·格 求和）
		public double WeatheredMassLastStep { get; private set; }      // 风化 岩石 → sediment
		public double LithifiedMassLastStep { get; private set; }      // 成岩 sediment → sedimentary
		public double MetamorphosedMassLastStep { get; private set; }  // 变质 sedimentary → metamorphic
		/// <summary>逐格侵蚀出站搬运量（诊断/出图）。**复用缓冲**：每步覆盖，跨步持有无效。</summary>
		public float[] ErosionMovedPerCell => _erosionMoved;

		// ── 侵蚀介质判读口：本步各介质各自搬运了多少（三者之和 = 总搬运；水率只进风化门）──
		public double WindMovedMassLastStep { get; private set; }
		public double GravityMovedMassLastStep { get; private set; }
		public double GlacierMovedMassLastStep { get; private set; }
		/// <summary>逐格介质率之和（水+风+冰川；不含重力——重力是逐边的）。判读用。</summary>
		public float[] MediumRatePerCell => _mediumRate;
		/// <summary>逐格水 / 风 / 冰川介质率（判读"某地是谁在蚀"）。</summary>
		public float[] WaterRatePerCell => _waterRate;
		public float[] WindRatePerCell => _windRate;
		public float[] GlacierRatePerCell => _glacierRate;
		/// <summary>本步实测的侵蚀域（出露陆格）平均降水（mm/yr）——"这个世界有多少水"的读数。
		/// 与 PrecipRefMmYear（1050）差得多 ⇒ 结构场的绝对尺度与参考气候不在一档（须用闭合后降水）。</summary>
		public float PrecipDomainMeanMmYearLastStep { get; private set; }
		/// <summary>本步侵蚀域平均产流（mm/yr）——水介质的直接输入（÷ RunoffRefMmPerYear 后即权重）。</summary>
		public float RunoffDomainMeanMmYearLastStep { get; private set; }
		/// <summary>本步侵蚀域平均湿润指数（0 干 ~ 1 湿）。</summary>
		public float AridityIndexDomainMeanLastStep { get; private set; }
		/// <summary>本步侵蚀域中产流为 0（干旱：PET ≥ P）的格占比。</summary>
		public float ZeroRunoffCellFractionLastStep { get; private set; }
		/// <summary>逐格岩性可蚀性 K（判读：盆地软 / 盾地硬）。</summary>
		public float[] RockErodibilityPerCell => _rockErodibility;

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
		/// <paramref name="erosionScale"/> = 侵蚀/风化强度倍率。
		/// 本重载 = 参考态介质（水 = 参考降水、温带），测试与"无气候层的裸调用"走它。</summary>
		public void Apply(H3PlateFields fields, float[] surfaceHeight, MaterialDensity material,
			float stepMy, float erosionScale)
			=> Apply(fields, surfaceHeight, material, stepMy, erosionScale, ErosionMedium.Reference);

		/// <summary>介质版（演化步走它）：<paramref name="medium"/> 提供逐格降水/温度/风速 ⇒
		/// 水/风/冰川各自按介质是否存在开合（重力不需要介质，只看格间落差）。</summary>
		public void Apply(H3PlateFields fields, float[] surfaceHeight, MaterialDensity material,
			float stepMy, float erosionScale, ErosionMedium medium)
		{
			int n = fields.Count;
			for (int k = 0; k < 5; k++) Array.Clear(_delta[k], 0, n);
			ErosionMovedMassLastStep = 0;
			WeatheredMassLastStep = 0;
			LithifiedMassLastStep = 0;
			MetamorphosedMassLastStep = 0;
			WindMovedMassLastStep = 0;
			GravityMovedMassLastStep = 0;
			GlacierMovedMassLastStep = 0;

			float seconds = stepMy * Units.MEGAYEAR;               // My → 秒
			ComputeMediumRates(surfaceHeight, medium);
			ModelErosion(fields, surfaceHeight, seconds, material, erosionScale);
			ModelWeathering(fields, surfaceHeight, seconds, material, erosionScale);
			ModelLithification(fields, material);
			ModelMetamorphosis(fields);

			// delta 应用：搬运/转化全部组内，组总量不变。地图型并行：写只落 pool[i] 本格。
			var conserved = fields.ConservedPools();
			for (int k = 0; k < 5; k++)
			{
				var pool = conserved[k];
				var d = _delta[k];
				ParallelLoops.For(n, i => pool[i] += d[i]);
			}
		}

		/// <summary>逐格介质率。"介质不存在则该介质为 0"是物理结论，不是人工下限：
		///
		/// | 介质 | 门 / 强度 | 侵蚀 | 风化门 |
		/// |---|---|---|---|
		/// | 水 | 产流 ÷ RunoffRef（P→0 或 PET≫P ⇒ 严格 0），再 × K | —（水蚀归 H3FluvialTransport） | ✅ 化学水解要水 |
		/// | 风 | 风强 × 干燥度 × 裸露度 × WindErodibility × K | ✅ 坡面强度门 | ✅ 风沙崩解 |
		/// | 冰川 | [T < 0] × min(P/PrecipRef, 1) × GlacierErodibility | ✅ | ✅ 冻融 |
		/// | 重力 | 逐边 GravityErodibility × max(0, Δh − 门槛)（不乘 K；门槛按松散占比插值） | ✅ | ❌ 块体运动不风化基岩 |
		///
		/// 传入的降水必须是闭合后的（Σ = 洋面蒸发总量）：未闭合结构场只定雨下在哪。</summary>
		void ComputeMediumRates(float[] surfaceHeight, ErosionMedium medium)
		{
			int n = _mediumRate.Length;
			var precip = medium.PrecipMmYear;
			var temp = medium.TempC;

			// 参考态（无降水场）：水 = 1、无风蚀、温带（冰川关）。
			// 必须显式短路：落进下面的产流分支时 P = 0 会给出"干旱世界"的读数。
			if (precip == null)
			{
				for (int i = 0; i < n; i++)
				{
					_waterRate[i] = 1f;
					_windRate[i] = 0f;
					_glacierRate[i] = 0f;
					_mediumRate[i] = 1f;
				}
				return;
			}

			ComputeRockErodibility();

			double precipSum = 0;
			int precipCount = 0, aridCells = 0;
			float runoffSum = 0f, aiSum = 0f;
			for (int i = 0; i < n; i++)
			{
				float p = precip[i];
				float t = temp != null ? temp[i] : ReferenceTempC;
				float pet = H3Climate.PetMmPerYear(t);             // 一次 PET 两处用（免每格 2 次 Pow）
				float ai = H3Climate.AridityIndexWithPet(p, pet);
				float runoff = H3Climate.RunoffMmPerYearWithPet(p, pet);
				float water = Math.Clamp(runoff / RunoffRefMmPerYear, WaterWeightFloor, WaterWeightCap)
					* _rockErodibility[i];
				float bareness = H3Climate.BarenessFromAridityIndex(ai);
				float aridity = 1f - ai;
				// 风强：传了速度场直读 |u|（÷ 参考风速锚到 Ferrel 海平面 = 1.0），没传退环流带档位兜底
				float windiness = medium.WindMS != null
					? medium.WindMS[i] / H3Wind.ReferenceWindSpeedMS
					: _beltWindiness[i];
				float wind = windiness * aridity * bareness * WindErodibility * _rockErodibility[i];
				float snow = t < GlacierTempC ? Math.Clamp(p / PrecipRefMmYear, 0f, 1f) : 0f;
				float glacier = snow * GlacierErodibility;

				_waterRate[i] = water;
				_windRate[i] = wind;
				_glacierRate[i] = glacier;
				_mediumRate[i] = water + wind + glacier;           // 风化门 + 坡面搬运门（不含重力）

				if (surfaceHeight[i] > 0f)
				{
					precipSum += p;
					precipCount++;
					runoffSum += runoff;
					aiSum += ai;
					if (runoff <= 1e-3f) aridCells++;
				}
			}
			PrecipDomainMeanMmYearLastStep = precipCount > 0 ? (float)(precipSum / precipCount) : 0f;
			RunoffDomainMeanMmYearLastStep = precipCount > 0 ? runoffSum / precipCount : 0f;
			AridityIndexDomainMeanLastStep = precipCount > 0 ? aiSum / precipCount : 0f;
			ZeroRunoffCellFractionLastStep = precipCount > 0 ? aridCells / (float)precipCount : 0f;
		}

		/// <summary>逐格岩性可蚀性 K：柱内软/硬物质厚度占比的线性组合。
		/// 软 = 沉积物 + 沉积岩（易被冲开、盆地自我冲刷），硬 = 变质岩（盾地留住高地）。</summary>
		void ComputeRockErodibility()
		{
			var fields = _fieldsForK;
			if (fields == null) return;
			int n = _rockErodibility.Length;
			for (int i = 0; i < n; i++)
			{
				float soft = fields.Sediment[i] / _materialForK.Sediment + fields.Sedimentary[i] / _materialForK.Sedimentary;
				float hard = fields.Metamorphic[i] / _materialForK.Metamorphic;
				float total = soft + hard
					+ fields.FelsicPlutonic[i] / _materialForK.FelsicPlutonic + fields.FelsicVolcanic[i] / _materialForK.FelsicVolcanic
					+ fields.MaficVolcanic[i] / _materialForK.MaficVolcanicMin + fields.MaficPlutonic[i] / _materialForK.MaficVolcanicMin;
				float k = 1f;
				if (total > 1f)
					k = 1f + KSoftGain * (soft / total) - KHardGain * (hard / total);
				_rockErodibility[i] = Math.Clamp(k, KFloor, KCeil);
			}
		}

		/// <summary>注入 K 用的物质场（持有方每步设；null = K 恒 1）。</summary>
		public void BindFieldsForK(H3PlateFields fields, MaterialDensity material = null)
		{
			_fieldsForK = fields;
			_materialForK = material ?? _materialForK;
		}
		H3PlateFields _fieldsForK;
		/// <summary>K 场厚度换算用的密度表（默认表值实例；持有方经 <see cref="BindFieldsForK"/> 注入
		/// 模拟的同一张表，密度表改动时换算自动跟随——不再散落硬编码）。</summary>
		MaterialDensity _materialForK = new MaterialDensity();
		readonly float[] _rockErodibility;

		// ── ① 侵蚀：风 / 重力 / 冰川三介质（水蚀归河道 H3FluvialTransport）──
		// 出站总量 = Σ_下坡邻 [ 落差 × (风+冰川)率 + 重力系数 × max(0, 落差 − 门槛) ] × baseCoef；
		// 门槛按柱内松散质量占比在固结档/松散档间插值（松散柱先滑）。
		// 五池按存量占比依次分担，均摊到各下坡邻。参考态（风/冰川 = 0）⇒ 只剩重力通道。
		void ModelErosion(H3PlateFields fields, float[] surfaceHeight, float seconds, MaterialDensity material,
			float erosionScale)
		{
			int n = fields.Count;
			var neighbors = _ball.CellNeighbors;
			var delta = _delta;
			var pools = fields.ConservedPools();
			float rho = material.FelsicPlutonic;                   // 与密度表同值
			float baseCoef = PrecipMS * seconds * ErosiveFactor * erosionScale * rho;
			Array.Clear(_erosionMoved, 0, n);
			Span<float> share = stackalloc float[5];

			double moved = 0, movedWind = 0, movedGravity = 0, movedGlacier = 0;
			Span<float> edgeTransfer = stackalloc float[8];    // 逐边出站量缓存（邻居 ≤6：六边格 6/五边格 5，
																// 单遍算好二遍复用，免逐边重算 diff/落差项）
			for (int i = 0; i < n; i++)
			{
				float hi = surfaceHeight[i];
				var nb = neighbors[i];

				// 重力门槛：纯沉积柱 300 m 就滑、基岩柱要 1500 m 悬崖，按松散占比插值
				float totalColumn = 0f;
				for (int p = 0; p < 5; p++) totalColumn += MathF.Max(pools[p][i], 0f);
				float looseFrac = totalColumn > 1e-9f ? MathF.Max(pools[0][i], 0f) / totalColumn : 0f;
				float gravThreshold = GravityReliefSolidM - (GravityReliefSolidM - GravityReliefLooseM) * looseFrac;

				// ① 出站量（单遍扫下坡邻，逐边 transfer 进缓冲）：逐介质分开累计，便于按介质记账。
				//    侵蚀气候门 = 风 + 冰川（水率只进风化门）。
				float rate = _windRate[i] + _glacierRate[i];
				float outbound = 0, edgeWind = 0, edgeGlacier = 0, edgeGravity = 0;
				for (int k = 0; k < nb.Length; k++)
				{
					float diff = hi - surfaceHeight[nb[k]];
					if (diff <= 0) { edgeTransfer[k] = 0f; continue; }
					float climate = diff * rate;                   // 坡面（风+冰川，按介质率缩放）
					float gravity = GravityErodibility * MathF.Max(0f, diff - gravThreshold);
					outbound += climate + gravity;
					edgeTransfer[k] = (climate + gravity) * baseCoef;
					// 气候项按两介质率**各自**分摊（和 = climate，因为 rate = 两者之和）
					if (rate > 0f)
					{
						edgeWind += climate * (_windRate[i] / rate);
						edgeGlacier += climate * (_glacierRate[i] / rate);
					}
					edgeGravity += gravity;
				}
				if (outbound <= 0) continue;

				// ② 各池分担份额（按存量占比依次扣减）。
				//    份额必须带存量上界：本池损失 ≈ cellMovedEq × share[p]，存量小于本格出站量的池
				//    若按"clamp 到满"会扣出负质量；截断后 from 减/to 加用同一个 share ⇒ 仍严格守恒。
				float cellMovedEq = outbound * baseCoef;
				float remain = outbound;
				float shareSum = 0;
				for (int p = 0; p < 5; p++)
				{
					float f = remain > 1e-9f ? pools[p][i] / remain : 0f;
					f = Math.Clamp(f, 0f, 1f);
					float shareCap = cellMovedEq > 1e-9f
						? MathF.Max(pools[p][i], 0f) / cellMovedEq
						: 0f;
					share[p] = MathF.Min(f, shareCap) / nb.Length;  // 均摊到全部下坡邻
					remain *= 1f - f;
					shareSum += share[p];
				}

				// ③ 沿边搬运：from 减、to 加（共享边两侧各处理一次由 from 视角独占，不判重）。
				//    transfer = 0 的边与原"diff ≤ 0 跳过"等价（零量加减不改账）。
				float cellMoved = 0;
				for (int k = 0; k < nb.Length; k++)
				{
					float transfer = edgeTransfer[k];
					if (transfer == 0f) continue;
					int j = nb[k];
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
				// 逐介质记账（同一 shareSum 口径 ⇒ 三者之和 = 总搬运）
				float attribution = baseCoef * shareSum;
				movedWind += edgeWind * attribution;
				movedGlacier += edgeGlacier * attribution;
				movedGravity += edgeGravity * attribution;
			}
			ErosionMovedMassLastStep = moved;
			WindMovedMassLastStep = movedWind;
			GlacierMovedMassLastStep = movedGlacier;
			GravityMovedMassLastStep = movedGravity;
		}

		// ── ② 风化：出露基岩 → sediment。门 = 水 + 风 + 冰川（不含重力：块体运动搬运碎屑、不风化基岩），
		//    参考态下门 = 1。速率 ∝ 全图平均高差 × 基岩暴露度（sediment 盖 ≥ 1 m 即停）。
		void ModelWeathering(H3PlateFields fields, float[] surfaceHeight, float seconds, MaterialDensity material,
			float erosionScale)
		{
			float criticalSedimentMass = 1f * material.Sediment;   // 1 m 沉积物盖 × ρ_sed：盖住就停风化
			int n = fields.Count;
			var neighbors = _ball.CellNeighbors;

			// 全图平均高差（粗糙度输入，全边扫描）
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
				// 原位基岩口径：本步之前就在的基岩才"可风化"——不能把本步刚落下的碎屑当基岩，
				// 否则同一批物质一步内被"落料 → 风化 → 落料"转两遍。
				float bedrockInPlace = sedimentary[i] + metamorphic[i]
					+ felsicPlutonic[i] + felsicVolcanic[i];
				if (bedrockInPlace <= 0) continue;

				float exposure = Math.Clamp(1f - sediment[i] / criticalSedimentMass, 0f, 1f);
				if (exposure <= 0) continue;

				float weathering = avgDiff * ErosiveFactor * erosionScale * PrecipMS * seconds
					* material.FelsicPlutonic * exposure * _mediumRate[i];      // 介质门（水+风+冰川）
				if (weathering <= 0) continue;

				// 扣减走本步剩余存量（侵蚀已先扣过一轮）：四步共用一份 delta 就必须看到彼此的账，
				// 读池原值会重复支取、把池子扣成负值。
				float sediNow = MathF.Max(sedimentary[i] + _delta[1][i], 0f);
				float metaNow = MathF.Max(metamorphic[i] + _delta[2][i], 0f);
				float felsicPNow = MathF.Max(felsicPlutonic[i] + _delta[3][i], 0f);
				float felsicVNow = MathF.Max(felsicVolcanic[i] + _delta[4][i], 0f);
				float bedrock = sediNow + metaNow + felsicPNow + felsicVNow;
				if (bedrock <= 0) continue;
				weathering = MathF.Min(weathering, bedrock);
				float ratio = weathering / bedrock;

				_delta[0][i] += weathering;                        // → sediment
				_delta[1][i] -= sediNow * ratio;
				_delta[2][i] -= metaNow * ratio;
				_delta[3][i] -= felsicPNow * ratio;
				_delta[4][i] -= felsicVNow * ratio;
				weathered += weathering;
			}
			WeatheredMassLastStep = weathered;
		}

		// ── ③ 成岩：sediment 柱压超 2.2 MPa → sedimentary ──
		void ModelLithification(H3PlateFields fields, MaterialDensity material)
		{
			var sediment = fields.Sediment;
			int n = fields.Count;
			double lithified = 0;
			for (int i = 0; i < n; i++)
			{
				// 柱压按本步之前的柱（成岩发生在埋深处，本步刚落下的薄尘不该立刻压实）；
				// 可压实量按本步剩余存量（侵蚀已扣过一轮）
				float overpressure = sediment[i] * 9.8f;           // kg/m² × m/s² = Pa
				float excess = overpressure - LithificationPressurePa;
				if (excess <= 0) continue;
				float sedimentNow = MathF.Max(sediment[i] + _delta[0][i], 0f);
				if (sedimentNow <= 0f) continue;
				float amount = Math.Clamp(excess / 9.8f, 0f, sedimentNow);
				_delta[0][i] -= amount;
				_delta[1][i] += amount;
				lithified += amount;
			}
			LithifiedMassLastStep = lithified;
		}

		// ── ④ 变质：sediment+sedimentary 柱压超 300 MPa → metamorphic ──
		void ModelMetamorphosis(H3PlateFields fields)
		{
			var sediment = fields.Sediment;
			var sedimentary = fields.Sedimentary;
			int n = fields.Count;
			double metamorphosed = 0;
			for (int i = 0; i < n; i++)
			{
				// 柱压按本步之前的柱；可变质量按本步剩余沉积岩——成岩刚给它加过料（_delta[1] 为正），
				// 这里必须看得见，这是"成岩与变质在同一格上串联"的一环
				float overpressure = (sediment[i] + sedimentary[i]) * 9.8f;
				float excess = overpressure - MetamorphismPressurePa;
				if (excess <= 0) continue;
				float sedimentaryNow = MathF.Max(sedimentary[i] + _delta[1][i], 0f);
				if (sedimentaryNow <= 0f) continue;
				float amount = Math.Clamp(excess / 9.8f, 0f, sedimentaryNow);
				_delta[1][i] -= amount;
				_delta[2][i] += amount;
				metamorphosed += amount;
			}
			MetamorphosedMassLastStep = metamorphosed;
		}
	}
}
