using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using World.Tectonics;                 // MaterialDensity
using World.Utils;                     // ParallelLoops（确定性并行地图循环）

namespace World.NewHexWorld.Plate
{
	// 均衡位移 / 岩石圈挠曲 / 水量守恒海平面（设计-03 §2.2 第 9–10 步 / §6）。
	//
	// 单一 datum：逐格位移 = 柱的绝对函数 h = Σ_类 kAiry,类·(厚_类 − 参考厚_类) − 热沉降，
	// 以参考洋柱（7.1 km 年轻洋壳，age=0）为零点——位移与海平面**解耦**，没有跨判据台阶，
	// 水位真的管海陆（海平面涨 → 低地被淹成大陆架/内陆海）。35 km 纯陆柱 = +4980 m（陆洋均衡阶跃）。
	//
	// 水量：唯一输入 = GlobalWaterLayerM（地球实测全球等效水层，行星参数而非海拔标定），
	// 摊到洋盆格数 = TotalOceanDepth，此后守恒；海平面每步由容积守恒解出。
	// 海岸线、陆高、洋深、大陆露出全部由（壳厚模板 + 密度表 + 热沉降律 + 水量）涌现，无海拔目标值。
	public sealed class H3Isostasy
	{
		// ── 参考厚度与校准常量（口径 = 初始地壳生成用的那套厚度）──
		/// <summary>陆壳参考厚（m）= 初始陆壳模板基准厚，也是初始起伏写厚的中心。</summary>
		public const float LandReferenceThicknessM = 35000f;
		/// <summary>洋壳参考厚（m）= 唯一 datum 的零点（参考洋柱 age=0 处 h ≡ 0）。</summary>
		public const float OceanReferenceThicknessM = 7100f;
		/// <summary>脊轴水深（m，Parsons–Sclater 拟合：depth(t) = 本值 + 350√t）；不参与水量标定，
		/// 保留为热沉降负载化的数值锚与判读参考。</summary>
		public const float RidgeAxisDepthM = 2500f;
		/// <summary>热沉降系数（m/√My；Parsons &amp; Sclater 半空间冷却），直接写入洋格位移。</summary>
		public const float SubsidenceCoefM = 350f;
		/// <summary>热沉降渐近深度（m，相对脊轴）——板模型（Parsons–Sclater / Stein–Stein）
		/// 老洋底沉降渐近 ≈ −3.6 km rel 脊轴；半空间律在封顶前与板模型重合、之后走常量。</summary>
		public const float SubsidenceAsymptoteM = 3600f;
		/// <summary>年龄封顶（My）= (渐近深度/沉降系数)²，由 <see cref="SubsidenceAsymptoteM"/> 反推，不是独立旋钮。</summary>
		public const float AgeCapMy = 106f;

		// ── 弹性挠曲：薄板弯曲刚度 → 载荷的区域响应 ──
		/// <summary>杨氏模量（Pa）；岩石圈标准值。</summary>
		public const float YoungModulusPa = 7e10f;
		/// <summary>泊松比。</summary>
		public const float PoissonRatio = 0.25f;
		/// <summary>洋侧弹性厚度系数（km/√My）：`Te = 2.7·√age`（半空间冷却的标准拟合）。</summary>
		public const float TeOceanicKmPerSqrtMy = 2.7f;
		/// <summary>弹性厚度上限（km；洋侧随年龄增长到此为止）。</summary>
		public const float TeMaxKm = 40f;
		/// <summary>陆侧（克拉通）弹性厚度（km）。</summary>
		public const float TeContinentalKm = 35f;
		/// <summary>挠曲求解的 CG 迭代次数（0 = 关闭挠曲，位移 = 纯 Airy，A/B 对照用）。
		/// 初值取 Airy 场 ⇒ 长波已是正确解，迭代只修短波；条件数 κ ≈ 数百 ⇒ √κ ≈ 16，
		/// 24 次迭代实测残差 ≤ 0.5 m（极端载荷场 ~100 m）。分辨率越高同样次数下欠收敛越重。</summary>
		public int FlexureIterations = 24;
		/// <summary>海水密度（kg/m³；载荷密度差 Δρ = ρ_m − ρ_w 用）。</summary>
		public const float WaterDensityKgM3 = 1026f;
		/// <summary>脆性上地壳屈服强度（Pa）。没有它挠曲会把山压平：宽山带的弯曲应力远超岩石圈强度
		/// ⇒ 板屈服破裂 ⇒ 有效 Te 塌到几 km ⇒ 载荷几乎完全补偿（山保住），而边缘/邻区仍弹性 ⇒ 前陆盆地。
		/// "刚性克拉通内部 + 破碎边缘"由同一条规则自然涌现，无分支。</summary>
		public const float YieldStressPa = 2.5e8f;
		/// <summary>屈服限 Te 的下限（m）：避免 κ→0 处除零 / Te 塌到 0（α→0 ⇒ 纯 Airy = 完全破碎）。</summary>
		public const float TeYieldFloorM = 1000f;

		/// <summary>全球等效水层（m）——全模型唯一的水量输入：地球实测值（与"半径 6371 km"同类的
		/// 行星实测参数，不是地形/海拔标定）。摊到洋盆格数后记入 <see cref="TotalOceanDepth"/>。</summary>
		public const float GlobalWaterLayerM = 2700f;
		/// <summary>水量守恒常量（按洋壳面积平均的等效水深 m）= GlobalWaterLayerM × 全星格数 / 洋盆格数。
		/// 此处默认值只覆盖"直构 H3Isostasy 未摊派"的测试路径；生产路径由 DistributeGlobalWaterLayer 写入。</summary>
		public double TotalOceanDepth = GlobalWaterLayerM;

		public float[] Displacement;                          // 均衡位移（m，单一 datum：参考洋柱 = 0）
		/// <summary>海平面（m，同一 datum）——由**全球水量**二分求出（容器 = 全体可淹格）。</summary>
		public float SeaLevel { get; private set; }

		int _oceanBasinCellCount;                             // 洋盆（地质学洋壳）格数 = 水量分母；ComputeRaw 里数

		readonly float[] _flex;                          // 挠曲负载场（预分配复用）
		readonly float[] _flexBuffer;

		public H3Isostasy(int cellCount)
		{
			Displacement = new float[cellCount];
			_flex = new float[cellCount];
			_flexBuffer = new float[cellCount];
		}

		/// <summary>Airy 系数 = 1 − ρ/ρ_mantle（按物质各取各的：长英质 0.1545、年轻洋壳 0.0602）。</summary>
		public static float AiryFactorFor(float density, MaterialDensity material) => 1f - density / material.Mantle;

		/// <summary>长英质 Airy 系数（快捷方式）。</summary>
		public static float AiryFactor(MaterialDensity material) => AiryFactorFor(material.FelsicPlutonic, material);

		/// <summary>镁铁质（洋壳）Airy 系数：参考密度取年轻洋壳 `MaficVolcanicMin`。
		/// 有意不随年龄走：年龄带来的密度上升就是热沉降（350√age 那一项），两者都算 = 同一过程记两遍账。</summary>
		public static float AiryFactorMafic(MaterialDensity material)
			=> AiryFactorFor(material.MaficVolcanicMin, material);

		/// <summary>唯一 datum 的逐格位移（模拟热路径与测试/判读共用，防口径漂移）：
		/// `h = Σ_类 kAiry,类·(厚_类 − 参考厚_类) − 350·min(√age, √AgeCap)`（热沉降只作用于洋格）。
		/// 参考厚：长英质 0、镁铁质 7100、沉积类 0。不含海平面项（位移与海平面完全解耦）。
		/// `age > 0` 且无长英质的格才减热沉降（陆壳不参与热沉降链）。</summary>
		public static float ComputeCellDisplacement(H3PlateFields fields, int cell, MaterialDensity material,
			float temperatureScale = 1f)
		{
			float airyFelsic = AiryFactorFor(material.FelsicPlutonic, material);
			float airyMafic = AiryFactorMafic(material);
			float airySediment = AiryFactorFor(material.Sediment, material);
			float airySedimentary = AiryFactorFor(material.Sedimentary, material);
			float airyMetamorphic = AiryFactorFor(material.Metamorphic, material);

			float felsicThickness = fields.FelsicPlutonic[cell] / material.FelsicPlutonic
				+ fields.FelsicVolcanic[cell] / material.FelsicVolcanic;
			// 洋壳厚用年轻洋壳密度换算（与写入口径同源，见 AiryFactorMafic）
			float maficThickness = (fields.MaficVolcanic[cell] + fields.MaficPlutonic[cell]) / material.MaficVolcanicMin;

			float displacement = airyFelsic * felsicThickness
				+ airyMafic * (maficThickness - OceanReferenceThicknessM)
				+ airySediment * (fields.Sediment[cell] / material.Sediment)
				+ airySedimentary * (fields.Sedimentary[cell] / material.Sedimentary)
				+ airyMetamorphic * (fields.Metamorphic[cell] / material.Metamorphic);

			// 热沉降：冷却岩石圈地幔的深度积分密度亏损 ÷ ρ_m。纯地壳 Airy 给不出真实洋底落差
			// （真实洋底随年龄落 2.5→6 km，全靠冷却岩石圈的 1–2% 密度盈余）。Age 场 = 岩石圈热年龄：
			// 洋中脊新生 = 0、盖上厚陆壳后冻结（大陆岩石圈热稳态、不再冷却沉降）。
			// temperatureScale = 当前地幔势温/参考势温：势温一降，沉降与驱动同步变弱。
			float thermalAge = MathF.Min(fields.Age[cell], AgeCapMy);
			displacement -= H3ThermalColumn.SubsidenceM(thermalAge) * temperatureScale;
			return displacement;
		}

		/// <summary>逐格均衡位移（全表）→ Displacement，并解海平面（03 §2.2 第 6/8 步）。</summary>
		public float[] ComputeDisplacement(Ball ball, H3PlateFields fields, MaterialDensity material,
			float temperatureScale = 1f)
		{
			ComputeRaw(ball, fields, material, temperatureScale);   // 逐格 Airy（等静压响应）
			BindFlexureInputs(fields, material);
			ApplyFlexure(ball);                                     // 区域响应：载荷经板刚度滤波
			SolveSeaLevelByVolume();
			return Displacement;
		}

		// 单遍位移。Airy 系数只依赖密度表（步内恒量）——提到循环外，每格免 5 次除法；
		// 长英质/镁铁质厚度算一次两用（位移 + 洋盆格数判据）。逐格算式与
		// <see cref="ComputeCellDisplacement"/> 逐项同序同式 ⇒ 逐位一致。
		void ComputeRaw(Ball ball, H3PlateFields fields, MaterialDensity material, float temperatureScale)
		{
			int n = ball.CellIds.Length;
			float airyFelsic = AiryFactorFor(material.FelsicPlutonic, material);
			float airyMafic = AiryFactorMafic(material);
			float airySediment = AiryFactorFor(material.Sediment, material);
			float airySedimentary = AiryFactorFor(material.Sedimentary, material);
			float airyMetamorphic = AiryFactorFor(material.Metamorphic, material);
			// 逐格地图型循环（写只落 Displacement[i]，洋盆计数走整型线程局部合并——精确可结合）
			int oceanBasin = 0;
			ParallelLoops.ForWithLocal(n,
				() => 0,
				(i, _, localOcean) =>
				{
					float felsicThickness = fields.FelsicPlutonic[i] / material.FelsicPlutonic
						+ fields.FelsicVolcanic[i] / material.FelsicVolcanic;
					float maficThickness = (fields.MaficVolcanic[i] + fields.MaficPlutonic[i]) / material.MaficVolcanicMin;
					float displacement = airyFelsic * felsicThickness
						+ airyMafic * (maficThickness - OceanReferenceThicknessM)
						+ airySediment * (fields.Sediment[i] / material.Sediment)
						+ airySedimentary * (fields.Sedimentary[i] / material.Sedimentary)
						+ airyMetamorphic * (fields.Metamorphic[i] / material.Metamorphic);
					// 热沉降：冷却岩石圈地幔的深度积分密度亏损 ÷ ρ_m（口径见 ComputeCellDisplacement）。
					float thermalAge = MathF.Min(fields.Age[i], AgeCapMy);
					displacement -= H3ThermalColumn.SubsidenceM(thermalAge) * temperatureScale;
					Displacement[i] = displacement;
					// 水量分母 = 地质学洋壳格，判据用"厚薄主导"而非"长英质 == 0"：
					// 侵蚀会把陆源薄尘搬上海底，逐零判据会把这些格踢出分母 ⇒ 海平面骤塌。
					return maficThickness > felsicThickness ? localOcean + 1 : localOcean;
				},
				localOcean => Interlocked.Add(ref oceanBasin, localOcean));
			_oceanBasinCellCount = oceanBasin;
		}

		/// <summary>洋盆格数（水量分母；判读口）。</summary>
		public int OceanBasinCellCount => _oceanBasinCellCount;

		/// <summary>按全球等效水层摊派水量：TOD = GlobalWaterLayerM × 全星格数 / 洋盆格数。
		/// 不含任何海拔目标——海平面/海岸线/陆高/洋深由随后的容积守恒 + 均衡涌现。
		/// 确定性：纯遍历 + 浮点顺序固定，无 rng。</summary>
		public double DistributeGlobalWaterLayer(Ball ball, H3PlateFields fields, MaterialDensity material)
		{
			int n = ball.CellIds.Length;
			ComputeRaw(ball, fields, material, 1f);
			if (_oceanBasinCellCount == 0) return TotalOceanDepth;      // 全陆退化：水量无意义
			TotalOceanDepth = (double)GlobalWaterLayerM * n / _oceanBasinCellCount;
			return TotalOceanDepth;
		}

		/// <summary>岩石圈弹性挠曲：薄板方程 `∇²(D∇²w) + Δρ·g·w = q` 的无量纲形式
		/// `w + L(D·Lw) = w_airy`（D_i = α_i⁴ 逐格弯曲柔度、L ≈ ∇² 图 Laplacian）。
		/// 板有弯曲刚度 ⇒ 载荷的区域响应：长波载荷完全补偿（DC 增益精确为 1，海平面解/水量守恒不受影响）；
		/// 短波载荷被板强度撑住 ⇒ 峰被削低、邻域被抬起——前陆盆地/前隆/海山 moat 自然长出来。
		/// 解法 = 共轭梯度（系统对称正定：L 写成对称形且 D ≥ 0）。
		/// 三个必须守住的实现约束：L 必须对称形（见 Laplacian）、α 场必须平滑（4 阶算子系数在
		/// 网格尺度跳变会长出假起伏）、屈服判据前曲率必须先平滑到 ~30 km（否则处处屈服、挠曲恒等于 Airy）。
		/// 成本：每次 CG 迭代 2 遍 Laplacian（12N 浮点）。</summary>
		public void ApplyFlexure(Ball ball)
		{
			int n = ball.CellIds.Length;
			if (n == 0) return;
			var neighbors = ball.CellNeighbors;
			if (_airy == null || _airy.Length < n)
			{
				_airy = new float[n];
				_laplacian = new float[n];
				_laplacian2 = new float[n];
				_alpha4 = new float[n];
				_alpha = new float[n];
				_teEffective = new float[n];
				_cgR = new float[n];
				_cgP = new float[n];
				_cgAp = new float[n];
			}
			// 物理格宽（m）：必须用地球真实半径——ball.Radius 是渲染球半径（无量纲），
			// 用它会让 Laplacian 的尺度差 6 个数量级。
			float radiusKm = H3PlateMotion.EarthRadiusKm;
			float cellWidthM = MathF.Sqrt(4f * MathF.PI * radiusKm * radiusKm * 1e6f / n);
			float c = 4f / (cellWidthM * cellWidthM);

			Array.Copy(Displacement, _airy, n);

			// 屈服限 Te：弯曲应力 σ = E·Te·κ/(2(1−ν²))，κ = 载荷的曲率。
			// 曲率必须先平滑到屈服相关尺度（~30 km）再求：本模型特征都在网格尺度上，
			// 直接取逐格曲率 ⇒ Te 处处塌到下限 ⇒ 挠曲恒等于 Airy（整层失效）；
			// 平滑后单格尖峰曲率 ≈ 峰高/30 km² ⇒ 板在此屈服（消掉单格假山），
			// 千公里级高原在该尺度上几乎平 ⇒ Te 保持弹性（长波照样完全补偿）。
			int smoothRounds = Math.Clamp((int)((30000f / (0.65f * cellWidthM)) * (30000f / (0.65f * cellWidthM))), 1, 12);
			Array.Copy(_airy, _laplacian2, n);
			// 乒乓平滑（免每轮一次全场 Array.Copy）；轮数奇偶决定结果落在哪个缓冲，末尾归位 _laplacian2。
			// dst[i] 只由 src 邻域决定 ⇒ 地图型并行，逐格算式与串行逐位一致。
			for (int round = 0; round < smoothRounds; round++)
			{
				float[] src = (round & 1) == 0 ? _laplacian2 : _laplacian;
				float[] dst = (round & 1) == 0 ? _laplacian : _laplacian2;
				ParallelLoops.For(n, i =>
				{
					var nb = neighbors[i];
					if (nb.Length == 0) { dst[i] = src[i]; return; }
					float sum = src[i];
					for (int k = 0; k < nb.Length; k++) sum += src[nb[k]];
					dst[i] = sum / (nb.Length + 1);
				});
			}
			if ((smoothRounds & 1) == 1) Array.Copy(_laplacian, _laplacian2, n);   // 归位（读端固定 _laplacian2）
			Laplacian(neighbors, c, _laplacian2, _laplacian);
			float maxCurvature = 0f;
			float rigidityScale = (MaterialForFlexure.Mantle - WaterDensityKgM3) * 9.8f;
			float bending = 12f * (1f - PoissonRatio * PoissonRatio);
			// 逐格 Te/α（含 Pow）地图型并行；max 曲率精确可结合 ⇒ 逐位一致。
			ParallelLoops.ForWithLocal(n,
				() => 0f,
				(i, _, localMax) =>
				{
					float te = ElasticThicknessM(_fieldsForFlexure, i);
					float curvature = MathF.Abs(_laplacian[i]);
					if (curvature > localMax) localMax = curvature;
					float teYield = 2f * YieldStressPa * (1f - PoissonRatio * PoissonRatio)
						/ MathF.Max(YoungModulusPa * curvature, 1e-12f);
					if (teYield < te) te = MathF.Max(teYield, TeYieldFloorM);
					_teEffective[i] = te;
					_alpha[i] = MathF.Pow(YoungModulusPa * te * te * te / (bending * rigidityScale), 0.25f);
					return localMax;
				},
				localMax => { if (localMax > maxCurvature) maxCurvature = localMax; });
			MaxSmoothedCurvaturePerM = maxCurvature;

			// α 场平滑：4 阶算子的系数在网格尺度跳变会长出网格尺度假起伏（Te/α 是岩石圈的
			// 有效性质，只在构造尺度上变化，网格尺度反差是离散噪声）。必须平滑 α 本身而不是 α⁴
			// （α⁴ 跨 4 个数量级，平均会被最大值主导）。平滑是常数保真的 ⇒ DC 增益仍精确为 1；
			// 与屈服判据用同一个平滑尺度，避免两套口径打架。乒乓缓冲同上，结果归位 _alpha。
			for (int round = 0; round < smoothRounds; round++)
			{
				float[] src = (round & 1) == 0 ? _alpha : _laplacian2;
				float[] dst = (round & 1) == 0 ? _laplacian2 : _alpha;
				ParallelLoops.For(n, i =>
				{
					var nb = neighbors[i];
					if (nb.Length == 0) { dst[i] = src[i]; return; }
					float sum = src[i];
					for (int k = 0; k < nb.Length; k++) sum += src[nb[k]];
					dst[i] = sum / (nb.Length + 1);
				});
			}
			if ((smoothRounds & 1) == 1) Array.Copy(_laplacian2, _alpha, n);   // 归位（读端固定 _alpha）
			ParallelLoops.For(n, i =>
			{
				float a2 = _alpha[i] * _alpha[i];
				_alpha4[i] = a2 * a2;                  // D_i/Δρg = α⁴
			});

			// CG：解 A w = f，A w = w + L(D·Lw)（SPD）。初值 w = f（长波已在解上 ⇒ 只需修短波）；
			// 每次迭代整场算 A·p（两遍 Laplacian），按格逐个算 A 是 O(n²)。
			ApplyFlexureOperator(neighbors, c, _airy, _cgAp);          // A·f
			double rr = 0;
			for (int i = 0; i < n; i++)
			{
				_cgR[i] = _airy[i] - _cgAp[i];
				_cgP[i] = _cgR[i];
				rr += (double)_cgR[i] * _cgR[i];
			}
			FlexureResidualM = 0f;
			for (int iter = 0; iter < FlexureIterations; iter++)
			{
				if (rr <= 1e-12 || double.IsNaN(rr)) break;
				ApplyFlexureOperator(neighbors, c, _cgP, _cgAp);
				double pAp = 0;
				for (int i = 0; i < n; i++) pAp += (double)_cgP[i] * _cgAp[i];
				// A 对称正定 ⇒ pAp > 0 恒成立；负/零/NaN 只可能来自浮点退化——停在当前解，不写 NaN 进位移场。
				if (!(pAp > 1e-20)) break;
				double step = rr / pAp;
				// 更新 + 本轮 max 残差：地图型并行，max 精确可结合可交换 ⇒ 与串行逐位一致。
				float maxChange = 0f;
				float[] displacement = Displacement, cgR = _cgR, cgP = _cgP, cgAp = _cgAp;
				ParallelLoops.ForWithLocal(n,
					() => 0f,
					(i, _, localMax) =>
					{
						float delta = (float)(step * cgP[i]);
						float change = MathF.Abs(delta);
						if (change > localMax) localMax = change;
						displacement[i] += delta;
						cgR[i] -= (float)(step * cgAp[i]);
						return localMax;
					},
					localMax => { if (localMax > maxChange) maxChange = localMax; });
				FlexureResidualM = maxChange;
				double rrNew = 0;
				for (int i = 0; i < n; i++) rrNew += (double)_cgR[i] * _cgR[i];
				double beta = rrNew / rr;
				for (int i = 0; i < n; i++) _cgP[i] = _cgR[i] + (float)(beta * _cgP[i]);
				rr = rrNew;
			}
			// 削低/抬升峰值：max 精确可结合 ⇒ 线程局部合并与串行逐位一致。
			_flexurePeakReduction = 0f;
			_flexurePeakUplift = 0f;
			float peakReduction = 0f, peakUplift = 0f;
			ParallelLoops.ForWithLocal(n,
				() => 0f,
				(i, _, localMax) =>
				{
					float drop = _airy[i] - Displacement[i];
					if (drop > localMax) localMax = drop;
					return localMax;
				},
				localMax => { if (localMax > peakReduction) peakReduction = localMax; });
			ParallelLoops.ForWithLocal(n,
				() => 0f,
				(i, _, localMax) =>
				{
					float uplift = Displacement[i] - _airy[i];
					if (uplift > localMax) localMax = uplift;
					return localMax;
				},
				localMax => { if (localMax > peakUplift) peakUplift = localMax; });
			_flexurePeakReduction = peakReduction;
			_flexurePeakUplift = peakUplift;
		}

		// 整场算 `A w = w + L(D·Lw)`（D_i = α_i⁴）；复用缓冲、零分配、累加序固定（确定性）。
		// 第二遍 Laplacian 与"加回源场"融合成单遍（<see cref="LaplacianAdd"/>），免一次全场读写。
		void ApplyFlexureOperator(int[][] neighbors, float c, float[] source, float[] target)
		{
			Laplacian(neighbors, c, source, _laplacian);               // L w
			float[] laplacian = _laplacian, alpha4 = _alpha4, laplacian2 = _laplacian2;
			ParallelLoops.For(source.Length, k => laplacian2[k] = laplacian[k] * alpha4[k]);
			LaplacianAdd(neighbors, c, _laplacian2, source, target);    // target = source + L D L w
		}

		float[] _cgR;
		float[] _cgP;
		float[] _cgAp;

		// 图 Laplacian（对称形式）：Lw_i = (c/6)·(Σ_nb w − deg_i·w_i)。按固定参考度 6 归一 ⇒ L = Lᵀ
		// ⇒ A = I + L·D·L 对称正定，CG 才有收敛保证（按局部度归一的均值式在 deg=5 顶点处破坏对称性）。
		// deg=6 处与均值式逐位同值；常数场 Lw = 0 ⇒ 长波完全补偿性质不变。
		// 地图型并行：target[i] 只由第 i 个迭代写，逐元素算式与串行逐位一致。
		static void Laplacian(int[][] neighbors, float c, float[] source, float[] target)
		{
			float k = c / 6f;
			int n = source.Length;
			ParallelLoops.For(n, i =>
			{
				var nb = neighbors[i];
				if (nb.Length == 0) { target[i] = 0f; return; }
				float sum = 0f;
				for (int j = 0; j < nb.Length; j++) sum += source[nb[j]];
				target[i] = k * (sum - nb.Length * source[i]);
			});
		}

		// Laplacian 的"加回"融合版：target_i = add_i + L(source)_i。逐元素算式与
		// "先 L 后逐格相加"两遍版逐位一致（target = add + k·(Σ − deg·src)），少一次全场读写。
		static void LaplacianAdd(int[][] neighbors, float c, float[] source, float[] add, float[] target)
		{
			float k = c / 6f;
			int n = source.Length;
			ParallelLoops.For(n, i =>
			{
				var nb = neighbors[i];
				if (nb.Length == 0) { target[i] = add[i]; return; }
				float sum = 0f;
				for (int j = 0; j < nb.Length; j++) sum += source[nb[j]];
				target[i] = add[i] + k * (sum - nb.Length * source[i]);
			});
		}

		/// <summary>弹性厚度 Te（m）——决定该格所在岩石圈的弯曲刚度。
		/// 洋侧（镁铁质主导）：`Te = 2.7·√(热年龄)` km 封顶 40；陆侧：克拉通 35 km。</summary>
		public float ElasticThicknessM(H3PlateFields fields, int cell)
		{
			if (fields == null) return TeContinentalKm * 1000f;
			float felsic = fields.FelsicPlutonic[cell] / MaterialForFlexure.FelsicPlutonic
				+ fields.FelsicVolcanic[cell] / MaterialForFlexure.FelsicPlutonic;
			float mafic = (fields.MaficVolcanic[cell] + fields.MaficPlutonic[cell]) / MaterialForFlexure.MaficVolcanicMin;
			if (mafic <= felsic) return TeContinentalKm * 1000f;
			float te = MathF.Min(TeOceanicKmPerSqrtMy * MathF.Sqrt(MathF.Max(fields.Age[cell], 0f)), TeMaxKm);
			return te * 1000f;
		}

		/// <summary>本步挠曲求解的最大迭代残差（m；大 = 欠收敛、板偏软，见 <see cref="FlexureIterations"/>）。</summary>
		public float FlexureResidualM { get; private set; }
		/// <summary>本步挠曲把最高的格削低了多少（m；区域支撑的直接读数，0 = 纯 Airy）。</summary>
		public float FlexurePeakReductionM => _flexurePeakReduction;
		float _flexurePeakReduction;
		/// <summary>本步挠曲把某格抬高的最大幅度（m = 前隆幅度）。这是板支撑的合法抬高（不来自本格壳厚），
		/// 会突破"海拔 ≤ 本格厚度帽 Airy 当量"的局部界（`OrogenCeiling` 测试的余量就是这个量）。</summary>
		public float FlexurePeakUpliftM => _flexurePeakUplift;
		float _flexurePeakUplift;

		/// <summary>逐格有效弹性厚度（m；= min(热年龄 Te, 屈服限 Te) 且不低于下限）——诊断"板在这里破了没有"。</summary>
		public float[] EffectiveElasticThicknessM => _teEffective;
		/// <summary>本步平滑后的载荷最大曲率（1/m）。屈服限 Te ∝ 1/κ：κ 越大板越"破"；
		/// 特征尺度普遍在网格尺度上 ⇒ 必须平滑到 ~30 km 再看 κ，否则处处屈服。</summary>
		public float MaxSmoothedCurvaturePerM { get; private set; }
		float[] _teEffective;

		/// <summary>挠曲求解所需的物质场与密度表（由持有方在调用前注入；不注入则按全陆克拉通处理）。</summary>
		public void BindFlexureInputs(H3PlateFields fields, MaterialDensity material)
		{
			_fieldsForFlexure = fields;
			MaterialForFlexure = material ?? MaterialForFlexure;
		}
		H3PlateFields _fieldsForFlexure;
		MaterialDensity MaterialForFlexure = new MaterialDensity();
		float[] _airy;
		float[] _laplacian;
		float[] _laplacian2;
		float[] _alpha4;
		float[] _alpha;                    // 平滑后的弯曲柔度（长度尺度 m；算子系数 = α⁴）

		/// <summary>水量守恒海平面：二分找 s，使被淹没容器装下的总水量 = 全球水量。
		/// 容器 = 全体格（含被淹没的陆壳：大陆架/内陆浅海）；水量 = TotalOceanDepth × 洋盆格数
		/// （镁铁质主导格，分母对陆源撒尘稳健）；`reservedVolumeM` = 云/河库存的预留体积（米×格）。
		/// 位移不随 s 变化 ⇒ V(s) = Σ max(0, s − h_i) 严格单调增 ⇒ 二分唯一解。
		/// 搜索区间必须双向包住解：盆地深（−3~−8 km）而海平面在正千米量级。</summary>
		public float SolveSeaLevelByVolume(double reservedVolumeM = 0)
		{
			if (TotalOceanDepth <= 0) return SeaLevel;
			int n = Displacement.Length;
			if (n == 0 || _oceanBasinCellCount == 0) return SeaLevel;   // 全陆世界：无水量约束（退化）

			double target = Math.Max(TotalOceanDepth * (double)_oceanBasinCellCount - reservedVolumeM, 0);
			double sum = 0;
			float minH = float.MaxValue;
			for (int i = 0; i < n; i++)
			{
				sum += Displacement[i];
				if (Displacement[i] < minH) minH = Displacement[i];
			}
			double lo = minH - 1.0;                                     // V(lo) = 0 ≤ target（恒真）
			double hi = (sum + target) / n;                              // 全湿线性根：V(hi) ≥ target（恒真）
			if (!(hi > lo)) hi = lo + TotalOceanDepth + 1.0;             // 退化防御

			int iters = 0;
			for (double width = hi - lo; width > 1e-3; width *= 0.5) iters++;
			if (iters > 40) iters = 40;
			for (int iter = 0; iter < iters; iter++)
			{
				float mid = (float)((lo + hi) * 0.5);
				double filled = 0;
				for (int i = 0; i < n; i++)
				{
					float depth = mid - Displacement[i];
					if (depth > 0f) filled += depth;
				}
				if (filled < target) lo = mid; else hi = mid;
			}
			SeaLevel = (float)((lo + hi) * 0.5);
			return SeaLevel;
		}

		/// <summary>陆占比（**海拔高于海平面**的格占比）。整型计数并行精确可结合。</summary>
		public float LandFraction()
		{
			int land = 0;
			var displacement = Displacement;
			ParallelLoops.ForWithLocal(displacement.Length,
				() => 0,
				(i, _, local) => displacement[i] > SeaLevel ? local + 1 : local,
				local => Interlocked.Add(ref land, local));
			return displacement.Length > 0 ? (float)land / displacement.Length : 0f;
		}
	}
}
