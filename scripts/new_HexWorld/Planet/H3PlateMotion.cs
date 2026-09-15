using System;
using System.Collections.Generic;
using Godot;
using World.Tectonics;                    // MaterialDensity / Units / MatrixOps（复用，不重写矩阵与密度表）
using World.Utils;

namespace World.NewHexWorld.Plate
{
	// 板块运动学（设计-03 §2.2 第 2 步 / §6；v1.4 改**动力学速度标定**）：
	// 浮力 → 板缘拉力合力 → 力平衡解速度 → 刚体旋转拟合 → CFL 钳制。
	//
	// 物理结构 = 板片拉拽（slab pull）的力平衡：
	//   驱动 F_板 = Σ_板缘致密格 (−浮力)·厚度·格面积        [N]（负浮力 = 密度超地幔的下拽）
	//   阻力   = 地幔对板底的黏滞拖曳：τ = η·v/δ → F = η·v·A/δ
	//   平衡   ⇒ v_板 = F_板·δ / (η·A_板)                   [m/s]
	// η、δ 都取地球真值（不做成调参旋钮——它们只是"力换速度"的量纲桥，不是标定魔数）；
	// 地球参数代入解出 1–10 cm/yr（观测区间）。**速度由动力学给出**——v1.3 及以前的
	// ReferenceSpeedKmPerMy = 32 标定常数已删，板间快慢与绝对速度都随演化自己变。
	//
	// 方向符号在此**钉死**（2026-09-11 讨论"浮力是径向的怎么会成为转动动力"的结论）：
	//   径向的竖直部分不在此模拟（走 H3Isostasy 的均衡位移）；水平结果直接写出——
	//   驱动速度 = 边界法线（指向对方板的切向）× 板速，即**致密板缘把整板朝海沟方向拖**
	//   （桌布过桌沿：垂下部分被径向往下拽，桌面部分水平滑向桌沿）。
	//
	// 刚体旋转拟合（绕质心 ω_c + 绕世界中心 ω_w 各自平均后合成）照旧：球面上刚体没有"平移"，
	// 任何整体漂移都等价于绕过球心某轴的旋转（欧拉位移定理）。合成式的双重负号约定不变——
	// 拟合出的刚体运动在驱动格处复现驱动方向（数值已验）。
	//
	// **CFL 钳制**（v1.4 新增）：每步位移 ≤ 0.8 格宽。动力学解出的速度不设上下限，超速的板
	// 整体缩放本步旋转量——物质层阻挡/裂谷注壳的落地规则按"一步一格"设计（H3PlateAdvection），
	// 动力学照算、钳制只管离散安全。被钳板的格数上报 ClampedCellCount。
	//
	// 接触阻挡**已删**（v1.4）：碰撞语义移到物质层（H3PlateAdvection 落地阻挡：堆厚/俯冲/让开），
	// 旋转向量投影那条板级近似退役——它挡不住刚性旋转在别的边界段推进去（实测每步仍剩 148
	// 异板混合格），物质层阻挡没有这个漏洞。
	//
	// **顶死接触零功修正**（v1.8）：v1.4 删的是手段（板级硬约束），但"碰撞让板减速"这个目的
	// 也跟着丢了——顶死只回答"这一格归谁"，不回答"这板还转不转"：被顶死的致密板缘格仍按满额
	// 计入 F，全顶死的板照自由解起跳、往造山带无限进料。补的是**力级**修正不是约束（不复活
	// v1.3）：驱动格沿边界法线的落点是异板物质、且来料不比目标密（= 平流落地规则必顶死）→
	// 该格不进净驱动力 F——堵住不动的接触不做功，功率 = 力 × 实际位移。力全边界可加、不存在
	// "绕过"，位置语义仍全归物质层。判据与平流 Pass C 共用 H3PlateContact（防口径漂移）；
	// ⚠️ 只认 foreign 接触：链头撞同板未起跳格是相位停驻（下一步就走），不是碰撞，不计。
	// JamContactCellCount / PhantomForceFraction 是缺口量测口；板级碰撞阻力项（F − R 的
	// 接触阻力，让顶死段拖慢整板）待这组量测拍板后再议。
	//
	// 单位纪律：速度一律 **rad/My**（内部）；对外判读用 km/My 与 cm/yr（× R_earthKm、÷ 10）。
	public sealed class H3PlateMotion
	{
		// ── 常量 ──
		public const float EarthRadiusKm = 6371f;             // 换算用地球半径（与渲染半径无关，01 §3.3）
		public const float AsthenosphereThicknessM = 1e5f;    // 软流圈剪切层厚度 δ（~100 km，地球真值）
		public const float CflMaxStepCellWidths = 0.8f;       // 每步最大位移（格宽倍数；离散安全余量）

		/// <summary>参与刚体旋转拟合的最低速度（改进 2）：0.1 cm/yr = 1 km/My。
		/// 速度极小但非零的格（板内部浮力噪声）会把 ω 拉向随机方向。</summary>
		public const float MinFitSpeedKmPerMy = 1f;

		/// <summary>平流阻挡的汇聚判定门限（km/My；0.1 cm/yr）：相对速度沿边法向低于此值视为
		/// "没在相向"，不阻挡（避免数值抖动让所有边界格反复触发堆叠）。</summary>
		public const float ConvergenceThresholdKmPerMy = 1f;

		// ── 力项常量（04 批次 2 动力学补强；借鉴 testproject 的力平衡口径。默认 = 物理量级，
		//    量测判读后可调——本批不做拖曳标定（用户拍板：真值 η + 完整力项先行））──
		/// <summary>板片账户记忆（My）：账户每步 ×(1 − StepMy/此值) ≈ 板片断离时标
		/// （testproject 同值 33 Myr——已俯冲物质继续拉板块的持续时间）。</summary>
		public const float SlabMemoryMy = 33f;
		/// <summary>板片拉力系数：F = 此系数 × g × 账户质量。冷板片密度盈余约 1–2%，取 0.05
		/// 量级（含几何效率；判读口 PlateSlabPullN 直接可量）。</summary>
		public const float SlabPullWeightFraction = 0.05f;
		/// <summary>板片账户上限（× 板内地壳总量）：防俯冲通量无界累积（testproject SLAB_CAP 同意图）。</summary>
		public const float SlabCapFractionOfPlateCrust = 0.25f;
		/// <summary>洋脊推力应力（N/m²）：F = 此值 × sqrt(平均洋龄/80My) × 洋区面积
		/// （testproject T2：脊-翼高差随洋龄 sqrt 增长 → 推力饱和）。</summary>
		public const float RidgePushStressPa = 4e5f;
		/// <summary>洋脊推力的年龄饱和参考（My）。</summary>
		public const float RidgeAgeReferenceMy = 80f;
		/// <summary>造山负载阻力系数（03 §3.6 的 R 项落地）：R = μ × 顶死幻影力。
		/// 幻影力随造山带厚度增长（顶死格的 pull ∝ 厚度）⇒ 阻力自限 = 造山自限（testproject T3）。
		/// 取 0.3：碰撞显著减速但不禁锢（μ=1 实测让全锁定世界速度归零——缝合无从发生）。</summary>
		public const float OrogenFrictionMu = 0.3f;

		// ── 输出 ──
		/// <summary>地幔黏度（Pa·s）——**由热状态逐帧注入**（`H3DynamicTectonics` 每步写
		/// `H3ThermalState.MantleViscosityPaS`：Arrhenius η(Tp) 随势温上升 ⇒ "发动机缓慢熄火"的响应半边）。
		/// 默认 = 密度表的 `MantleViscosity`（1.57e20；直接构造的运动学状态与单测走默认 ⇒ 行为与
		/// 引入热状态前逐位一致，05 §5 迁移纪律）。</summary>
		public float MantleViscosityPaS = new MaterialDensity().MantleViscosity;
		/// <summary>当前地幔势温（K）——由热状态逐帧注入；驱动力的深度积分亏损 N ∝ α·T_m 随它线性缩。</summary>
		public float PotentialTemperatureK = H3ThermalState.ReferencePotentialTemperatureK;
		/// <summary>板片**下垂长度**（m）：已俯冲板片保持温度异常的下潜长度（真实 500–700 km；取 600）。
		/// **它就是"逐长度口径"的物理带宽度**：板片拉力 = N·g·海沟长度·本常量（N = 深度积分密度亏损）。
		/// ⚠️ 旧写法用【格面积】累加（F = Δρ·g·壳厚·格面积）⇒ 板缘格数 ≈ 周长/格宽 ⇒ **F ∝ 格宽**：
		/// res3→res5（格宽 120→17 km）驱动力降 7× —— 那是分辨率伪影（res3 的 120 km 恰好接近真实
		/// 俯冲带尺度，把伪影掩盖了）。逐长度口径下 F = N·g·海沟长度·本常量，与格宽无关。</summary>
		public const float SlabDipLengthM = 6e5f;
		/// <summary>每格**实际运动**速度（rad/My）= 拟合后的刚体运动（不是驱动速度）。
		/// 板缘分类（H3PlateBoundary）、离散/汇聚判定、平流落格都读它。</summary>
		public Vector3[] Velocity;
		public float[][] PlateDeltaRotation;       // 每板本步旋转增量（3×3 列主序 9 元素）
		public Vector3[] PlateOmega;               // 每板平均角速度（rad/My；诊断/UI 判读用）
		public int ClampedCellCount;               // 本步被 CFL 钳制的板的格数（离散安全上报口）

		// ── 顶死接触量测（v1.8 判读口；零功修正的缺口大小从这组读）──
		/// <summary>本步被判顶死接触而剔除出净驱动的板缘格数（全球）。</summary>
		public int JamContactCellCount { get; private set; }
		/// <summary>幻影驱动力占比 = 被剔除 F / 含顶死格的毛 F（0 = 无顶死接触；1 = 全顶死冻结）。</summary>
		public float PhantomForceFraction { get; private set; }
		public int[] PlateJamContacts;             // 每板顶死接触格数
		public float[] PlatePhantomForceN;         // 每板被剔除的驱动力（N；= 堵住不动的幻影拉力）
		public float[] PlateGrossForceN;           // 每板毛驱动力（N；含顶死格。净 = 毛 − 幻影）

		/// <summary>板级起跳相位（格数，∈[0,1)）：每步累加 板速×步长/格宽，满 1 格本板起跳
		/// （全部格沿各自流向走一格）。起跳频率 = 动力学速度；一次起跳 = 恰好一格。</summary>
		public float[] PlatePhase;
		public bool[] PlateFired;                  // 本板本步是否起跳
		/// <summary>每板**规定速度**（rad/My）= 力平衡解出的通道速度之和（相位用它定起跳频率）。
		/// ⚠️ 它可能与 `PlateOmega`（刚体拟合结果）差很远：驱动速度场取逐格边界法线，其**刚体拟合有损**
		/// （03 头注已记：均匀平移场在球面上拟合成零旋转）——判读必须用本表，别用 ω。</summary>
		public float[] PlateSpeedRadPerMy => _plateSpeedRadPerMy;
		/// <summary>本步存在的板 id（升序；量测/诊断用）。</summary>
		public IReadOnlyList<int> PlateIds => _plateIds;
		/// <summary>逐板地壳质量（每步 Pass ① 累计；周期重启的动量量测用）。</summary>
		public double[] PlateCrustMass => _plateCrustMass;

		// ── 板片账户（04 批次 2）：平流俯冲裁决时入账（质量面密度累计 + 质量加权流向），
		//    每步按 SlabMemoryMy 衰减、按板内地壳总量封顶。物理意图：已俯冲的板片继续拉板块
		//    （真实地球的主驱动），补"裁决回地幔后拉力即消失"的缺口（03 §3.6 板速过低的根因之一）。
		public double[] SlabMass;                  // 账户质量（kg/m² 口径累计；× 格面积 → kg）
		public Vector3[] SlabDir;                  // 账户方向（质量加权流向和；|SlabDir| ≤ SlabMass）

		// ── 力分解判读口（04 批次 2；HexDynamicDiag 逐板打印）──
		public float[] PlateSlabPullN;             // 板片账户拉力（N）
		public float[] PlateRidgePushN;            // 洋脊推力（N）
		public float[] PlateOrogenResistN;         // 造山负载阻力（N；= μ × 顶死幻影力）

		readonly Vector3[] _driveVelocity;         // 每格驱动速度（拟合的输入，不外传）
		readonly bool[] _drives;                   // 每格是否为驱动态（洋格 + age>0 + 处板缘；05-B 逐长度口径）
		readonly Vector3[] _normal;                // 每格边界法线（板内部 = 0）
		// ── 派生量缓存（04 批次 1）：每步第 ① 遍算一次 Thickness/Density，力平衡与顶死判定读缓存 ──
		// （此前同一格一步内 Thickness 被 SolvePlateSpeeds 再算、Density 被 IsJamContact 再算 2 次，
		//  每次都是七场除法求和——res5 是硬约束）。公式与 H3PlateFields 逐字相同 ⇒ 逐位一致。
		readonly float[] _thickness;
		readonly float[] _density;
		H3PlateFields _cacheFields;                // 缓存归属的场对象（平流读缓存前核对身份，防串场）
		float[] _plateForceN;                      // 每板**净**板缘拉力合力（N；顶死接触格剔除后）
		float[] _plateAreaM2;                      // 每板面积（m²）
		float[] _plateSpeedRadPerMy;               // 每板**总**速度（rad/My；相位/CFL 用 = 各通道之和）
		float[] _plateEdgeSpeedRadPerMy;           // 板缘+洋脊通道速度（驱动方向 = 板缘法线）
		float[] _plateSlabSpeedRadPerMy;           // 板片账户通道速度（驱动方向 = 账户流向）
		bool[] _plateClamped;                      // CFL 钳制旗（下标 = 板 id；格数统计一次 O(N) 用）
		readonly List<int> _plateIds = new();      // 本步存在的板 id（升序，确定性）
		List<int>[] _plateCells;                   // 逐板格桶（性能）：一次遍历分桶，取代"每板全表扫"的 2P·n
	int[] _plateOceanCells;                    // 逐板洋格数（洋脊推力用）
	double[] _oceanAgeSum;                     // 逐板洋龄总和（平均洋龄 → 洋脊推力）
	double[] _plateCrustMass;                  // 逐板地壳总量（kg/m² 口径；板片账户封顶基准）
	Vector3[] _plateComSum;                    // 逐板质量加权格心和（板片驱动轴用）
	double[] _plateComMass;                    // 逐板质量权和
	Vector3[] _plateSlabAxis;                  // 逐板板片驱动旋转轴（单位；携板心朝账户方向）

		public H3PlateMotion(int cellCount)
		{
			Velocity = new Vector3[cellCount];
			_driveVelocity = new Vector3[cellCount];
			_drives = new bool[cellCount];
			_normal = new Vector3[cellCount];
			_thickness = new float[cellCount];
			_density = new float[cellCount];
		}

		/// <summary>走一步运动学：算浮力/法线 → 力平衡解每板速度 → 驱动速度场 → 拟合刚体旋转
		/// → CFL 钳制 → 写实际速度场。不搬物质（搬物质是 H3PlateAdvection）。</summary>
		public void Step(Ball ball, H3PlateFields fields, MaterialDensity material, float surfaceGravity, float stepMy)
		{
			int n = ball.CellIds.Length;
			var centers = ball.CellCenters;
			var neighbors = ball.CellNeighbors;
			var plateId = fields.PlateId;

			// ⓪ 板表收集 + 容量 + 逐板累加器清零（04 批次 2 提前到缓存遍历前——洋格/地壳量在 ① 里累计）
			CollectPlateIds(plateId);
			EnsureRotationTables();
			foreach (int p in _plateIds)
			{
				_plateOceanCells[p] = 0;
				_oceanAgeSum[p] = 0;
				_plateCrustMass[p] = 0;
				_plateComSum[p] = Vector3.Zero;
				_plateComMass[p] = 0;
				// 板片账户衰减（记忆 My；= 板片断离后拉力消退）
				float decay = MathF.Min(stepMy / SlabMemoryMy, 1f);
				SlabMass[p] *= 1.0 - decay;
				SlabDir[p] *= 1f - decay;
			}

		// ① 浮力 + 边界法线（逐格）：**驱动力 = 板片拉力（逐长度口径，05-B）**——
		//    洋格（age > 0）且处板缘 ⇒ 该格代表的【海沟长度 = 格宽】上有深度积分密度亏损 N(age, Tp)，
		//    拉力 = N·g·格宽·SlabDipLengthM（与格面积无关 ⇒ 换 res 不改驱动力）。
		//    陆壳不下插（长英质浮）不产拉力；脊轴格（age = 0 ⇒ N ∝ √t = 0）不产拉力。
		//    老口径（Δρ·g·壳厚·格面积，密度链 2890→3300）已被取代：它把负浮力挂在 7 km 洋壳上、
		//    量级低 ~28×、且 age < 113 My 恒为 0（阈值型）、还带 F ∝ 格宽的分辨率伪影。
		//    Thickness/Density 仍入缓存（**物质侧**判据：顶层裁决 / 顶死 / 俯冲由密度比较定，不变）。
		_cacheFields = fields;
		for (int i = 0; i < n; i++)
		{
			float thickness = fields.Thickness(i, material);
			float totalMass = fields.TotalMass(i);
			float density = thickness <= 1e-6f ? material.MaficVolcanicMin : totalMass / thickness;
			_thickness[i] = thickness;
			_density[i] = density;
			_normal[i] = BoundaryNormal(i, centers, neighbors, plateId);
			_drives[i] = false;
			if (_normal[i].LengthSquared() > 1e-12f && !fields.IsLand(i) && fields.Age[i] > 0f)
				_drives[i] = true;
			int plate = plateId[i];
			if (plate >= 0)
			{
				float totalMassF = totalMass;
				_plateCrustMass[plate] += totalMass;
				_plateComSum[plate] += centers[i] * totalMassF;
				_plateComMass[plate] += totalMass;
				if (!fields.IsLand(i))
				{
					_plateOceanCells[plate]++;
					_oceanAgeSum[plate] += fields.Age[i];
				}
			}
		}

			// ② 力平衡解每板速度（净力 = 板缘负浮力(非顶死) + 板片账户拉力 + 洋脊推力 − 造山负载阻力）
			SolvePlateSpeeds(fields, material, ball, plateId, surfaceGravity);

			// ③ 驱动速度场：两个通道方向合成（04 批次 2）——
		//    板缘+洋脊通道：致密板缘格沿边界法线拖（原有口径，浮力<0 且处板缘才驱动）；
		//    板片通道：**旋转场** v = (ĉ×d̂)×r̂ · s——携板心 ĉ 朝账户方向 d̂ 走的刚体旋转。
		//    ⚠️ 不能用"逐格朝 d̂ 平移"：均匀平移场在球面上半球互抵，最优刚体拟合 = 零旋转
		//    （实测只剩格子分布噪声 1e-6 rad/My）——"整板朝海沟拖"在球面上本就没有平移。
			foreach (int p in _plateIds)
			{
				Vector3 axis = Vector3.Zero;
				float slabSpeed = _plateSlabSpeedRadPerMy[p];
				if (slabSpeed > 0f && p < SlabDir.Length && SlabDir[p].LengthSquared() > 1e-18f
					&& _plateComMass[p] > 1e-9)
				{
					Vector3 centerDir = (_plateComSum[p] / (float)_plateComMass[p]).Normalized();
					Vector3 toward = SlabDir[p] - centerDir * SlabDir[p].Dot(centerDir);   // 账户方向投到板心切平面
					float length = toward.Length();
					if (length > 1e-9f) axis = centerDir.Cross(toward / length);          // 单位轴：携板心朝 toward
				}
				_plateSlabAxis[p] = axis;
			}
			for (int i = 0; i < n; i++)
			{
				int p = plateId[i];
				if (p < 0 || p >= _plateSpeedRadPerMy.Length)
				{
					_driveVelocity[i] = Vector3.Zero;
					continue;
				}
				Vector3 drive = Vector3.Zero;
				if (_drives[i])
					drive += _normal[i] * _plateEdgeSpeedRadPerMy[p];
				Vector3 slabAxis = _plateSlabAxis[p];
				if (slabAxis.LengthSquared() > 0f)
					drive += slabAxis.Cross(centers[i].Normalized()) * _plateSlabSpeedRadPerMy[p];
				_driveVelocity[i] = drive;
			}

			// ④ 逐板拟合刚体旋转（输入 = 驱动速度场）
			FitPlateRotations(fields, centers, plateId, stepMy);

			// ⑤ CFL 钳制：每步位移 ≤ 0.8 格宽（动力学照算，钳制只管离散安全）
			ClampRotationsToCfl(ball, plateId, stepMy);

			// ⑤b 板级起跳相位：起跳频率 = 动力学速度（inc = 板速×步长/格宽，钳到 CFL 上限），
			//   满 1 格本板起跳；起跳即沿流向整链移动一格。
			float paceCellWidthKm = MathF.Sqrt(4f * MathF.PI * EarthRadiusKm * EarthRadiusKm / n);
			foreach (int p in _plateIds)
			{
				float incrementCells = MathF.Min(
					_plateSpeedRadPerMy[p] * EarthRadiusKm * stepMy / paceCellWidthKm,
					CflMaxStepCellWidths);
				PlatePhase[p] += incrementCells;
				if (PlatePhase[p] >= 1f) { PlatePhase[p] -= 1f; PlateFired[p] = true; }
				else PlateFired[p] = false;
			}

			// ⑥ 实际运动速度场 = 拟合后的刚体运动。板缘分类 / 离散汇聚判定 / 平流流向都读它。
			WriteRigidVelocities(plateId, centers, stepMy);
		}

		// ── 力平衡：v = F_net·δ / (η·A) ──
		// F_net = Σ_致密板缘格 (−浮力)·厚度·格面积（**扣除顶死接触格**，v1.8 零功修正）
		//       + F_板片账户 + F_洋脊推力 − R_造山负载（04 批次 2 三力项）
		// 浮力 N/m³ × 厚度 m × 面积 m² = N。地球真值代入（η ≈ 1.6e20 Pa·s，δ = 100 km）。
		void SolvePlateSpeeds(H3PlateFields fields, MaterialDensity material, Ball ball, int[] plateId, float surfaceGravity)
		{
			int n = plateId.Length;
			float radiusM = EarthRadiusKm * 1000f;
			float cellAreaM2 = 4f * MathF.PI * radiusM * radiusM / n;
			var centers = ball.CellCenters;
			var neighbors = ball.CellNeighbors;
			foreach (int p in _plateIds)
			{
				_plateForceN[p] = 0f;
				PlateGrossForceN[p] = 0f;
				PlatePhantomForceN[p] = 0f;
				PlateJamContacts[p] = 0;
				PlateSlabPullN[p] = 0f;
				PlateRidgePushN[p] = 0f;
				PlateOrogenResistN[p] = 0f;
				_plateAreaM2[p] = 0f;
			}
			int jamContacts = 0;
			double phantomForceN = 0, grossForceN = 0;
			float cellWidthM = MathF.Sqrt(cellAreaM2);
			float temperatureScale = PotentialTemperatureK / H3ThermalState.ReferencePotentialTemperatureK;
			for (int i = 0; i < n; i++)
			{
				int p = plateId[i];
				if (p < 0) continue;
				_plateAreaM2[p] += cellAreaM2;
				if (!_drives[i]) continue;
				// 板片拉力（逐长度口径，05-B）：N(age, Tp)·g·该格代表的海沟长度（格宽）·板片下垂长度。
				// 量级锚：N(100 My)·g·600 km ≈ 4.8e13 N/m，与文献板片拉力 ~3.3e13 N/m 同量级（单测钉住）。
				float deficitKgPerM2 = H3ThermalColumn.NegativeBuoyancyKgPerM2(fields.Age[i], temperatureScale);
				float pull = deficitKgPerM2 * surfaceGravity * cellWidthM * SlabDipLengthM;
				PlateGrossForceN[p] += pull;
				grossForceN += pull;
				if (IsJamContact(i, p, centers, neighbors, plateId, fields, material))
				{
					PlateJamContacts[p]++;
					PlatePhantomForceN[p] += pull;
					phantomForceN += pull;
					jamContacts++;
					continue;                              // 顶死接触：不进净驱动
				}
				_plateForceN[p] += pull;
			}
			JamContactCellCount = jamContacts;
			PhantomForceFraction = grossForceN > 0f ? (float)(phantomForceN / grossForceN) : 0f;
			foreach (int p in _plateIds)
			{
				// 04 批次 2 三力项（量纲：账户 kg/m² × 格面积 m² × g × 系数 = N；洋脊 = 应力 × 面积）
				float slabPullN = SlabPullWeightFraction * surfaceGravity * SlabDir[p].Length() * cellAreaM2;
				float meanOceanAge = _plateOceanCells[p] > 0 ? (float)(_oceanAgeSum[p] / _plateOceanCells[p]) : 0f;
				float ridgePushN = RidgePushStressPa
					* MathF.Sqrt(Math.Clamp(meanOceanAge / RidgeAgeReferenceMy, 0f, 1f))
					* _plateOceanCells[p] * cellAreaM2;
				float orogenResistN = OrogenFrictionMu * PlatePhantomForceN[p];
				PlateSlabPullN[p] = slabPullN;
				PlateRidgePushN[p] = ridgePushN;
				PlateOrogenResistN[p] = orogenResistN;

				// 通道分解：板缘+洋脊−阻力（方向 = 板缘法线）与板片（方向 = 账户流向）。
				// 洋脊推力折进板缘通道——无独立逐格方向（离脊方向需另追踪边界，量测后再议）。
				float edgeNetN = MathF.Max(_plateForceN[p] + ridgePushN - orogenResistN, 0f);
				float drag = AsthenosphereThicknessM
					/ MathF.Max(MantleViscosityPaS * _plateAreaM2[p], 1e-6f);   // v/F = δ/(ηA)（η 由热状态注入）
				_plateEdgeSpeedRadPerMy[p] = edgeNetN * drag * Units.MEGAYEAR / 1000f / EarthRadiusKm;
				_plateSlabSpeedRadPerMy[p] = slabPullN * drag * Units.MEGAYEAR / 1000f / EarthRadiusKm;
				// v[m/s] = F·δ/(η·A) → ×MEGAYEAR = m/My → ÷1000 = km/My → ÷R = rad/My（总速 = 通道和）
				_plateSpeedRadPerMy[p] = _plateEdgeSpeedRadPerMy[p] + _plateSlabSpeedRadPerMy[p];
			}
		}

		// 顶死接触判定（v1.8）：驱动格沿边界法线（= 驱动方向，头注已钉死）的落点邻居是异板物质、
		// 且来料不比目标密 → 本步一旦起跳必被顶死 → 剔除其驱动力。判据与 H3PlateAdvection Pass C
		// 共用（H3PlateContact，防口径漂移）；密度走本步缓存（与现场算逐位一致）。⚠️ 只认 foreign
		// 接触——链头撞同板未起跳格是相位停驻（下一步就走），撞无主格不是接触，都不算碰撞。
		bool IsJamContact(int i, int p, Vector3[] centers, int[][] neighbors, int[] plateId,
			H3PlateFields fields, MaterialDensity material)
		{
			int target = H3PlateContact.FlowNeighbor(centers, neighbors, i, _normal[i]);
			if (target < 0) return false;
			int pt = plateId[target];
			if (pt < 0 || pt == p) return false;
			return H3PlateContact.JamsInto(_density[i], _density[target]);
		}

		/// <summary>读格密度（04 批次 1）：本步缓存命中则读缓存，否则现场算（手工构造的运动学状态
		/// 没跑过 Step——测试直调平流的路径走这条）。与 fields.Density 逐位一致。</summary>
		public float DensityFor(H3PlateFields fields, int cell, MaterialDensity material)
			=> ReferenceEquals(_cacheFields, fields) ? _density[cell] : fields.Density(cell, material);

		/// <summary>板片账户入账（04 批次 2）：平流俯冲裁决后调用（质量面密度 kg/m² + 流向单位向量）。
		/// 方向 = 俯冲发生时来料格→目标格的切向（质量加权累计进 SlabDir；下步力平衡用它定向拉力）。
		/// 账户按板内地壳总量封顶（SlabCapFractionOfPlateCrust）。</summary>
		public void AddSlab(int plate, double massPerArea, Vector3 flowDir)
		{
			if (plate < 0 || massPerArea <= 0 || float.IsNaN(flowDir.X)) return;
			EnsureSlabTableFor(plate);
			// 方向归一后加权（调用方传的是"质量加权流向和"——本步俯冲的质量加权平均方向）；
			// ⚠️ 不能再乘一次质量：|SlabDir| 必须 ≤ SlabMass（首跑实测双重加权 |d|∝m²，板片力爆 1e29 N）。
			Vector3 unit = flowDir.LengthSquared() > 1e-18f ? flowDir.Normalized() : Vector3.Zero;
			SlabMass[plate] += massPerArea;
			SlabDir[plate] += unit * (float)massPerArea;
			double cap = SlabCapFractionOfPlateCrust * _plateCrustMass[plate];
			if (cap > 0 && SlabMass[plate] > cap)
			{
				float scale = (float)(cap / SlabMass[plate]);
				SlabMass[plate] = cap;
				SlabDir[plate] *= scale;
			}
		}

		// ── 板块生命周期操作（04 批次 4：缝合划转 / 裂解分账 / 重启复位）──

		/// <summary>公开容量口：裂解等外部改归属的操作先确保新板号有表位。</summary>
		public void EnsurePlateCapacity(int plate) => EnsureSlabTableFor(plate);

		/// <summary>缝合划转（04 批次 4）：from 板的账户按比例划给 to（物质归属改判 ⇒ 拉力随走）。</summary>
		public void TransferSlab(int from, int to, double fraction)
		{
			EnsurePlateCapacity(Math.Max(from, to));
			fraction = Math.Clamp(fraction, 0.0, 1.0);
			double mass = SlabMass[from] * fraction;
			SlabMass[to] += mass;
			SlabDir[to] += SlabDir[from] * (float)fraction;
			SlabMass[from] -= mass;
			SlabDir[from] *= 1f - (float)fraction;
		}

		/// <summary>裂解分账（04 批次 4）：源板账户按格数占比分给新板（源板保留首份）；
		/// 新板相位/起跳清零（速度下步重解）。</summary>
		public void SplitPlateState(int source, int[] newPlates, double[] fractions)
		{
			foreach (int p in newPlates) EnsurePlateCapacity(p);
			for (int k = 0; k < newPlates.Length; k++)
			{
				double mass = SlabMass[source] * fractions[k];
				SlabMass[newPlates[k]] += mass;
				SlabDir[newPlates[k]] += SlabDir[source] * (float)fractions[k];
				SlabMass[source] -= mass;
				SlabDir[source] *= 1f - (float)fractions[k];
				PlatePhase[newPlates[k]] = 0f;
				PlateFired[newPlates[k]] = false;
			}
		}

		/// <summary>重启复位（04 批次 4）：全部重分板后，板级历史（账户/相位/起跳）清零重来。</summary>
		public void ResetPlateState()
		{
			if (SlabMass == null) return;
			Array.Clear(SlabMass, 0, SlabMass.Length);
			Array.Clear(SlabDir, 0, SlabDir.Length);
			if (PlatePhase != null) Array.Clear(PlatePhase, 0, PlatePhase.Length);
			if (PlateFired != null) Array.Clear(PlateFired, 0, PlateFired.Length);
		}

		// 账户表按板 id 扩容（AddSlab 可能发生在 Step 之外——板号来自平流当步归属，
		// 而容量在 Step 的 EnsureRotationTables 里只按当步板表长）。
		void EnsureSlabTableFor(int plate)		{
			EnsureRotationTables();                          // 先按当步板表长兜底（可能仍为 null/更短）
			int required = plate + 1;
			if (SlabMass != null && SlabMass.Length >= required) return;
			int size = Math.Max(required, SlabMass?.Length ?? 0);
			Array.Resize(ref SlabMass, size);
			Array.Resize(ref SlabDir, size);
			Array.Resize(ref _plateOceanCells, size);
			Array.Resize(ref _oceanAgeSum, size);
			Array.Resize(ref _plateCrustMass, size);
			Array.Resize(ref _plateComSum, size);
			Array.Resize(ref _plateComMass, size);
			Array.Resize(ref _plateSlabAxis, size);
			Array.Resize(ref _plateSpeedRadPerMy, size);
			Array.Resize(ref _plateEdgeSpeedRadPerMy, size);
			Array.Resize(ref _plateSlabSpeedRadPerMy, size);
			Array.Resize(ref PlateSlabPullN, size);
			Array.Resize(ref PlateRidgePushN, size);
			Array.Resize(ref PlateOrogenResistN, size);
			Array.Resize(ref PlatePhase, size);          // SplitPlateState/ResetPlateState 直写的表
			Array.Resize(ref PlateFired, size);
		}

		// 旋转表容量按最大板 id 扩容（板数只增不减）。
		void EnsureRotationTables()
		{
			int required = _plateIds.Count == 0 ? 0 : _plateIds[^1] + 1;
			if (PlateDeltaRotation == null || PlateDeltaRotation.Length < required)
			{
				Array.Resize(ref PlateDeltaRotation, required);
				Array.Resize(ref PlateOmega, required);
				Array.Resize(ref _plateForceN, required);
				Array.Resize(ref PlateGrossForceN, required);
				Array.Resize(ref PlatePhantomForceN, required);
				Array.Resize(ref PlateJamContacts, required);
				Array.Resize(ref _plateAreaM2, required);
				Array.Resize(ref _plateSpeedRadPerMy, required);
				Array.Resize(ref _plateEdgeSpeedRadPerMy, required);
				Array.Resize(ref _plateSlabSpeedRadPerMy, required);
				Array.Resize(ref _plateClamped, required);
				Array.Resize(ref _plateOceanCells, required);
				Array.Resize(ref _oceanAgeSum, required);
				Array.Resize(ref _plateCrustMass, required);
				Array.Resize(ref _plateComSum, required);
				Array.Resize(ref _plateComMass, required);
				Array.Resize(ref _plateSlabAxis, required);
				Array.Resize(ref SlabMass, required);
				Array.Resize(ref SlabDir, required);
				Array.Resize(ref PlateSlabPullN, required);
				Array.Resize(ref PlateRidgePushN, required);
				Array.Resize(ref PlateOrogenResistN, required);
			}
			if (PlatePhase == null || PlatePhase.Length < required)
			{
				int oldLength = PlatePhase?.Length ?? 0;
				Array.Resize(ref PlatePhase, required);
				Array.Resize(ref PlateFired, required);
				for (int p = oldLength; p < required; p++) PlatePhase[p] = 0f;
			}
			if (_plateCells == null || _plateCells.Length < required)
			{
				int oldLength = _plateCells?.Length ?? 0;
				Array.Resize(ref _plateCells, required);
				for (int p = oldLength; p < required; p++) _plateCells[p] = new List<int>();
			}
		}

		// 逐板拟合刚体旋转（输入 = 驱动速度场）。
		// 性能（v1.9.2）：旧实现每板全表扫两遍（质心 + 拟合）= 2P·n；现一次遍历分桶到 _plateCells
		// 再逐桶算，桶内升序 = 格序——两处浮点累加（质心/sumC/sumW）的顺序与旧实现逐位一致，确定性不变。
		void FitPlateRotations(H3PlateFields fields, Vector3[] centers, int[] plateId, float stepMy)
		{
			int n = centers.Length;
			float minFit = MinFitSpeedKmPerMy / EarthRadiusKm;

			foreach (int p in _plateIds)
				_plateCells[p].Clear();
			for (int i = 0; i < n; i++)
			{
				int p = plateId[i];
				if (p >= 0) _plateCells[p].Add(i);
			}

			foreach (int p in _plateIds)
			{
				var bucket = _plateCells[p];

				// 质心（质量加权，球面；位置取当前格心——刚体旋转不改变相对几何）
				Vector3 comSum = Vector3.Zero;
				double weightSum = 0;
				for (int k = 0; k < bucket.Count; k++)
				{
					int i = bucket[k];
					float mass = fields.TotalMass(i);
					comSum += centers[i] * mass;
					weightSum += mass;
				}
				Vector3 centerOfMass = weightSum > 1e-9 ? comSum / (float)weightSum : Vector3.Zero;

				Vector3 sumC = Vector3.Zero, sumW = Vector3.Zero;
				int participating = 0;
				for (int k = 0; k < bucket.Count; k++)
				{
					int i = bucket[k];
					Vector3 v = _driveVelocity[i];
					if (v.Length() < minFit) continue;                 // 改进 2：显式阈值
					Vector3 offset = centers[i] - centerOfMass;
					float distanceSquared = offset.LengthSquared();
					if (distanceSquared < 1e-12f) continue;
					sumC += v.Cross(offset) / distanceSquared;          // 绕质心角速度
					sumW += v.Cross(centers[i]);                        // 绕世界中心（单位球尺度）
					participating++;
				}
				if (participating == 0)
				{
					PlateDeltaRotation[p] = MatrixOps.Identity();
					PlateOmega[p] = Vector3.Zero;
				}
				else
				{
					Vector3 meanCentroidOmega = sumC / participating;
					Vector3 meanWorldOmega = sumW / participating;
					PlateDeltaRotation[p] = ComposeRotation(meanCentroidOmega, meanWorldOmega, stepMy);
					PlateOmega[p] = meanCentroidOmega + meanWorldOmega;
				}
			}
		}

		// CFL 钳制：每步旋转角 ≤ 0.8 格宽对应的圆心角。超速板整体缩放旋转向量并重建矩阵；
		// 被钳板的格数一次 O(N) 统计（旗标先记，遍历后计数）。
		void ClampRotationsToCfl(Ball ball, int[] plateId, float stepMy)
		{
			int n = plateId.Length;
			float cellWidthKm = MathF.Sqrt(4f * MathF.PI * EarthRadiusKm * EarthRadiusKm / n);
			float maxAngleRad = CflMaxStepCellWidths * cellWidthKm / EarthRadiusKm;
			foreach (int p in _plateIds) _plateClamped[p] = false;
			foreach (int p in _plateIds)
			{
				float[] m = PlateDeltaRotation[p];
				if (m == null) continue;
				Vector3 theta = RotationVector(m);
				float angle = theta.Length();
				if (angle <= maxAngleRad || angle < 1e-12f) continue;
				Vector3 scaled = theta * (maxAngleRad / angle);
				PlateDeltaRotation[p] = MatrixOps.FromRotationVector(scaled);
				PlateOmega[p] = scaled / stepMy;
				_plateClamped[p] = true;
			}
			ClampedCellCount = 0;
			// 04 批次 1：被钳格数改从 FitPlateRotations 的分桶累加（O(P) 取代第二次 O(N) 全表扫；
			// 桶内格数与本步归属一致，计数值不变）。
			foreach (int p in _plateIds)
				if (_plateClamped[p]) ClampedCellCount += _plateCells[p].Count;
		}

		// 实际运动速度场（rad/My）= 每格按本板旋转位移 ÷ 步长。
		void WriteRigidVelocities(int[] plateId, Vector3[] centers, float stepMy)
		{
			for (int i = 0; i < plateId.Length; i++)
			{
				float[] rotation = RotationOf(plateId[i]);
				Velocity[i] = (MatrixOps.MultVector(rotation, centers[i]) - centers[i]) / stepMy;
			}
		}

		float[] RotationOf(int plate)
			=> PlateDeltaRotation != null && plate >= 0 && plate < PlateDeltaRotation.Length && PlateDeltaRotation[plate] != null
				? PlateDeltaRotation[plate] : MatrixOps.Identity();

		// 旋转矩阵 → 旋转向量（axis×angle，rad）。列主序 9 元素（m[col*3 + row]）：
		// 反对称部分 v = (m32−m23, m13−m31, m21−m12)/2，|v| = sin(angle)，迹给出 cos(angle)。
		static Vector3 RotationVector(float[] m)
		{
			var antisymmetric = new Vector3(
				(m[7] - m[5]) * 0.5f,
				(m[2] - m[6]) * 0.5f,
				(m[3] - m[1]) * 0.5f);
			float sin = antisymmetric.Length();
			float cos = (m[0] + m[4] + m[8] - 1f) * 0.5f;
			if (sin < 1e-9f) return Vector3.Zero;
			return antisymmetric * (MathF.Atan2(sin, cos) / sin);
		}

		// 本步旋转增量 = 绕质心旋转 × 绕世界中心旋转（老实现同式：两个 FromRotationVector 相乘，取负号）。
		// 双重负号与拟合求和的叉积方向恰好抵消——拟合出的刚体运动在驱动格处复现驱动方向。
		static float[] ComposeRotation(Vector3 meanCentroidOmega, Vector3 meanWorldOmega, float stepMy)
		{
			var aroundCenter = MatrixOps.FromRotationVector(-(meanCentroidOmega * stepMy));
			var aroundWorld = MatrixOps.FromRotationVector(-(meanWorldOmega * stepMy));
			var composed = MatrixOps.MultMatrix(aroundCenter, aroundWorld);
			return float.IsNaN(composed[0]) ? MatrixOps.Identity() : composed;
		}

		// 边界法线：异板邻居的（邻居格心 − 本格心）之和归一化。板内部（全同板邻居）= 零向量。
		static Vector3 BoundaryNormal(int cell, Vector3[] centers, int[][] neighbors, int[] plateId)
		{
			Vector3 sum = Vector3.Zero;
			foreach (int nb in neighbors[cell])
			{
				if (plateId[nb] == plateId[cell]) continue;
				Vector3 delta = centers[nb] - centers[cell];
				// 投影到切平面（去掉径向分量）——纯径向的差值在球面上不代表"朝外"。
				Vector3 radial = centers[cell].Normalized();
				sum += delta - radial * delta.Dot(radial);
			}
			float length = sum.Length();
			return length > 1e-9f ? sum / length : Vector3.Zero;
		}

		// 收集本步存在的板 id（升序 → 逐板处理顺序确定，浮点累加顺序可复现）。
		void CollectPlateIds(int[] plateId)
		{
			_plateIds.Clear();
			var seen = new HashSet<int>();
			foreach (int p in plateId)
			{
				if (p < 0 || !seen.Add(p)) continue;
				_plateIds.Add(p);
			}
			_plateIds.Sort();
		}
	}
}
