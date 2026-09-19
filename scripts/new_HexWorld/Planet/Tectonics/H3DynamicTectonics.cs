using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using World.Tectonics;                 // MaterialDensity
using World.Utils;

namespace World.NewHexWorld.Plate
{
// 动态板块运动编排（设计-03 §2.2 流水线 / §6）。归属随物质走：PlateId = 顶层物质的板号，
// 平流本身就是全局场对全局场，没有老实现"每板一份网格副本 + 逐步合并"的手续。每步顺序：
//   1 洋壳热年龄累积 → 1b 地幔热状态 → 2 运动学（力平衡速度 + 刚体旋转拟合 + CFL 钳制）
//   → 3 平流（物质层阻挡：碰撞堆厚/俯冲/让开）→ 4 裂谷/洋底扩张 → 4b 碎片归属清理
//   → 5 屈服流（超帽柱把超出量向邻格流动）→ 5b 陆壳收支伺服（弧回流补守恒组销毁缺口）
//   → 6-7 均衡位移 + 挠曲 → 8 水量守恒海平面
//   → 8b 地表过程（坡面侵蚀/风化 + 方向性风沙 + 河流输沙）→ 9-10 缝合/裂解/重启
	public sealed class H3DynamicTectonics
	{
		// ── 参数表（03 §8 初值；判读后可调）──
		public int Seed;
		public float StepMy = 4f;                    // 时间步（My）
		/// <summary>默认总时长（My）——生产/编辑器/诊断共用的单一出处（H3Plate、BallManager 旋钮、
		/// HexDynamicDiag 都从这里取默认）。600 = 一个 platec 式重启周期。**可以拉长**（2026-09-19 起）：
		/// 陆壳收支伺服（5b 步）保证陆壳存量只增不减，数 Ga 长跑不再走向水世界；仍登记两点——
		/// 热引擎按 70 K/Ga 降温，数 Ga 后黏度上升板速变慢（发动机缓熄火是真实物理）；
		/// hypsometry（海盆垫浅/大陆削低的相对海平面漂移）稳态器尚未立项，长跑海岸线仍会缓慢漂移。</summary>
		public const float DefaultRunMy = 600f;
		public float RunMy = DefaultRunMy;           // 总时长（My）
		public int NumContinents = 6;                // 大陆块数 = 初始陆洋斑块的团心数
		public float OceanFraction = 0.6f;           // 海洋占比（逐格分位数阈值钉住；blend=0 时退化为板数配比）
		public float OceanScale = 1f;                // 水量系数（× 全球等效水层；>1 = 水多陆少）
		public bool EnableRifting = true;            // 裂谷（离散空洞填新洋壳）
		/// <summary>地表过程开关（8b：侵蚀/风化/成岩/变质）。关掉 = 质量只进不出，造山单调饱和到厚度帽。</summary>
		public bool EnableErosion = true;
		/// <summary>侵蚀/风化强度倍率（0.5 温和 ~ 2 剧烈）。</summary>
		public float ErosionScale = 1f;
		/// <summary>河流输沙开关（随 EnableErosion 一起走 8b 块）。</summary>
		public bool EnableFluvial = true;
		/// <summary>河流输沙强度倍率（乘挟沙力）。</summary>
		public float FluvialScale = 1f;
		/// <summary>方向性风沙搬运开关：侵蚀→搬运→沉积沿风向走，只啃松散沉积；
		/// 风弱落沙、山挡沙、入海落大陆架。</summary>
		public bool EnableAeolian = true;
		/// <summary>风沙搬运强度倍率（乘输沙能力）。</summary>
		public float AeolianScale = 1f;
		/// <summary>降水倍率 = 水量总闸：乘在洋面蒸发总量上，全球 Σ 降水 = 倍率 × 蒸发。
		/// 0 = 无水世界（水介质/风化门归零、河流输沙归零、无雪），侵蚀只剩风 + 重力。</summary>
		public float PrecipitationScale = 1f;
		/// <summary>行星档（每步降水与终态气候共用，H3Plate 下行）。</summary>
		public float AxialTiltDeg = H3Climate.DefaultAxialTiltDeg;
		public float Insolation = H3Climate.DefaultInsolation;
		/// <summary>盛行风参数：每步降水的海洋湿润度/雨影按此偏转（与终态气候同源）。</summary>
		public bool ProgradeRotation = H3Wind.DefaultPrograde;
		public float RotationSpeed = H3Wind.DefaultRotationSpeed;
		/// <summary>洋底扩张：离散边界空洞注 age=0 脊轴新洋壳——洋中脊持续造洋、陆内裂谷长出窄洋
		/// （威尔逊旋回）。关掉 = 空洞一律按邻格多数相加权平均。</summary>
		public bool EnableSpreading = true;
		/// <summary>离散发育阈值（km/My）：空洞处两板相对速度的法向分离分量超过它才注新洋壳。</summary>
		public float SpreadingSpeedKmPerMy = 0.05f;
		/// <summary>俯冲再循环：埋入层沉积类（sediment/sedimentary）随俯冲回地幔的比例。
		/// 1 = 全额回收；0 = 守恒组永远埋进上盘。只作用于有上盘顶层的真混合格。</summary>
		public float RecycleSedimentFraction = 1f;
		/// <summary>俯冲再循环：被俯冲板驮着的薄层长英质的刮削比例。陆壳本体浮力大、罕被俯冲，碰不塌大陆。</summary>
		public float RecycleFelsicFraction = 0.2f;
		/// <summary>地壳屈服阈值（m）：超过它的柱每步把超出量 × YieldFlowRate 向邻格流动（屈服流），
		/// 接收格同样不得越过它——碰撞带堆厚的软上界，取地球碰撞带壳厚 60–75 km 量级；
		/// 不是海拔设计目标（定值水量下的 Airy 当量海拔 ≈ +9 km 是剥蚀前的机械上限读数，
		/// 实际峰体由屈服流与 8b 地表过程共同塑形）。</summary>
		public float MaxCrustThicknessM = 65000f;
		/// <summary>屈服流速率：超帽柱每步把超出量的这一比例流向邻格（造山带"先堆后摊"的"摊"）。
		/// 默认 0.05 ⇒ 碰撞堆叠的山峰可维持 ~20 步再被流平/侵蚀（真实造山的瞬态）；设 1 = 严格硬帽
		/// （一步回到阈值，无瞬态）。0 = 关闭（质量只进不出，造山单调饱和到阈值）。</summary>
		public float YieldFlowRate = 0.05f;
		/// <summary>弧岩浆回流系数（陆壳收支的**基础**入口）：洋→陆俯冲每埋入 1 kg 洋壳，向上盘陆格从地幔
		/// 新生长英质火山岩的比例（记创建账）。只对冲刮削那一户出口；三户出口（刮削+沉积全额+帽溢流守恒
		/// 组份额）的合计缺口由 <see cref="ApplyContinentalBudgetServo"/> 的积分伺服兜底
		///（不变量：弧回流累计 ≥ 守恒组销毁累计）。</summary>
		public float ArcFelsicReturnFraction = 0.5f;
		/// <summary>归属拓扑量测门控：false 跳过孤立/强少数/连通分量统计（省 O(N·6)+BFS）；
		/// 无主格与逐板格数/边界边数仍每步统计（守恒与缝合/裂解/重启触发器依赖）。</summary>
		public bool EnableTopologyMeasurement = true;

		/// <summary>P2 薄席驱动（设计-07 §5）：true = 运动定义权交给 GPE 驱动的板级刚体运动
		/// （板级 GPE 净力 → 底层拖曳平衡板速），旧力平衡连同幻影力退位。过渡语义：跳格搬运与
		/// 俯冲裁决仍走旧平流（P3 退休），方向与节拍已全部来自薄席力源。默认 false（行为基线不变）。</summary>
		public bool EnableThinSheetDriving = false;
		/// <summary>P3 通量化运输（设计-07 §5）：true = 物质运输改走 H3FluxTransport（速度场欧拉
		/// upwind 通量；俯冲 = 通量汇、加厚 = 收敛堆积、薄柱注壳内建）。跳格平流/起跳相位/空洞填充
		/// 退位。须与 EnableThinSheetDriving 同开（通量吃驱动速度场）。默认 false。</summary>
		public bool EnableFluxTransport = false;

		// ── 板块生命周期旋钮（缝合/裂解/重启循环；借鉴 platec P1/P2/P4）──
		/// <summary>缝合触发：板对顶死接触数 / 该对边界边数 ≥ 此比例（platec aggr_overlap_rel 口径）。</summary>
		public float SutureContactFraction = 1f / 3f;
		/// <summary>缝合触发：上述比例持续此步数（防瞬时接触误并）。</summary>
		public int SutureStreakSteps = 5;
		/// <summary>缝合资格的最低顶死格数（零星接触不算持续碰撞）。</summary>
		public int SutureMinPairJamCells = 2;
		/// <summary>裂解触发：最大板占比超过此值且（失速 或 超过硬顶）。</summary>
		public float SupercontinentFraction = 0.25f;
		/// <summary>裂解硬顶：最大板占比超过此值直接触发（不论速度）。</summary>
		public float SupercontinentHardFraction = 0.35f;
		/// <summary>裂解失速阈值（km/My = 0.1 cm/yr）：全球板均速低于此值视为失速。</summary>
		public float SplitStallSpeedKmPerMy = 0.01f;
		/// <summary>裂解冷却步数（刚裂完不许再裂）。</summary>
		public int SplitCooldownSteps = 10;
		/// <summary>裂解最小份额格数（每份至少这么多格才裂）。</summary>
		public int SplitMinCells = 3;
		/// <summary>裂解离散踢强度（× 洋壳模板质量面密度）：裂解时给子板账户注入背离种子的板片拉力，
		/// 让裂谷真的张开；随板片记忆 `SlabMemoryMy` 衰减 ⇒ 裂解后有 ~30 My 离散窗口。</summary>
		public float RiftKickStrength = 1f;
		/// <summary>重启触发①（"世界冻住了"的兜底）：全球板均**实到**速度持续低于此值
		/// （km/My = 0.25 cm/yr）⇒ 全部重分板（场保留）。"发动机还剩多少"那一维由
		/// <see cref="RestartEnergyRatio"/> 管（规定速度口径）。</summary>
		public float RestartStallSpeedKmPerMy = 0.025f;
		/// <summary>重启的超大陆占比阈值。</summary>
		public float RestartSupercontinentFraction = 0.5f;
		/// <summary>速度/能量条件要求的连续成立步数（抖动门）。</summary>
		public int RestartStallSteps = 8;

		// ── platec 式周期重启参数（05 §6.3 对齐 platec src/lithosphere.cpp）──
		/// <summary>触发②（主触发）：系统能量 / 历史峰值低于此比值 ⇒ 发动机耗尽 ⇒ 重启
		/// （platec `RESTART_ENERGY_RATIO`；峰值不随重启复位，跨周期后该条件越来越难满足）。
		/// 能量口径 = 板质量 × 规定速度（见 MeasureCycleState）。</summary>
		public float RestartEnergyRatio = 0.15f;
		/// <summary>触发③：连续这么多步没有陆-陆碰撞 ⇒ 重启。0 = 关（platec 的 10 迭代照搬过来
		/// 会在初始陆块碰撞前就误触发；要"无聊就重启"再打开）。</summary>
		public int RestartNoCollisionSteps;
		/// <summary>触发④：一个周期最多这么多步（一步 4 My ⇒ 150 步 = 600 My 一个周期）。</summary>
		public int RestartCycleSteps = 150;
		/// <summary>周期数上限（0 = 无限）。</summary>
		public int MaxRestartCycles;

		/// <summary>重分板委托（重启循环）：参数 = 目标板数，返回逐格新归属（0..P-1）。
		/// 由持有分板器的调用方（H3Plate.CreatePlates）注入；测试直构的 sim 不注入 → 重启自动旁路。</summary>
		public Func<int, int[]> Repartition;

		// ── 生命周期判读口 ──
		public int SutureCount { get; private set; }
		public int SplitCount { get; private set; }
		public int RestartCount { get; private set; }
		/// <summary>本步缝合的板对（判读；空 = 无）。</summary>
		public (int small, int big)? SuturedPairLastStep { get; private set; }
		/// <summary>本步裂解的板（判读；-1 = 无）。</summary>
		public int SplitPlateLastStep { get; private set; } = -1;
		/// <summary>本步全球板均速（km/My；失速判据输入）。</summary>
		public float MeanPlateSpeedKmPerMyLastStep { get; private set; }

		// ── platec 式周期量测口（05 §6.3 判读用）──
		/// <summary>**最近一次**重启的理由（空 = 从未重启；判读口，持久不解——不随步清零）。</summary>
		public string LastRestartReason { get; private set; } = "";
		/// <summary>距上次重启的步数（platec `iter_count`）。</summary>
		public int CycleStepCount { get; private set; }
		/// <summary>距上次陆-陆碰撞的步数（platec `last_coll_count`）。</summary>
		public int StepsSinceContinentalCollision { get; private set; }
		/// <summary>本步系统动量 / 历史峰值（platec `systemKineticEnergy / peak_Ek`；1 = 正在最活跃）。</summary>
		public float MomentumRatioLastStep { get; private set; } = 1f;
		/// <summary>本步**实到**板均速（km/My）——重启判据用的是它（不是拟合 ω、也不是被截断前的规定速度）。</summary>
		public float MeanRealizedSpeedKmPerMyForRestart => MeanRealizedSpeedKmPerMyLastStep;
		double _systemMomentumLastStep;
		double _peakMomentum;

		int _initialPlateCount;
		int _nextPlateId;
		int _splitCooldownUntil;
		int _stallStreak;

		// ── 初始地壳（逐格陆洋混合 + fBm 初始起伏）──
		// 陆/洋基准海拔不在这里另立旋钮：陆基准 = H3Isostasy.LandReferenceThicknessM 经 Airy 反解，
		// 洋底基准 = H3Isostasy 的热沉降律——两者都是均衡侧的既有常量，本类只写厚度。
		/// <summary>初始陆洋混合系数：0 = 纯板属性（海岸线 = 板块边界）；1 = 纯噪声斑块（与板块完全解耦）；
		/// 中间值 = 板块给大势、噪声撕海岸线。海洋占比仍由 <see cref="OceanFraction"/> 钉住，本旋钮只改形态。</summary>
		public float LandOceanNoiseBlend = 0.7f;
		/// <summary>大陆斑块上海岸细节 fBm 的基波长（km）：撕出锯齿海岸与岛链的尺度。</summary>
		public float CoastDetailWavelengthKm = 1600f;
		/// <summary>海岸细节的相对幅度（× fBm 值域 [−1,1]）：越大海岸线越碎、离岸小岛越多。</summary>
		public float CoastDetailAmplitude = 0.6f;
		/// <summary>陆地初始起伏半幅（m）——fBm 场的标称幅度，陆海拔 ≈（均衡阶跃 − 海平面）± 起伏。
		/// 大陆内部真实起伏量级 ~±1.5 km；低地低于海平面是合法的陆表海/大陆架（海平面按全球水量解）。</summary>
		public float LandReliefAmplitudeM = 1800f;
		/// <summary>洋底初始起伏半幅（m，海拔口径）——按镁铁质 kAiry 反解成洋壳厚度起伏
		/// （写端与均衡读端必须同源，否则起伏缩水）。真实洋底起伏 100–500 m。</summary>
		public float OceanReliefAmplitudeM = 350f;
		/// <summary>初始起伏的 fBm 基波长（km）= 最大一层特征尺寸（5 层倍频最细一层 150 km）。</summary>
		public float ReliefBaseWavelengthKm = 2400f;
		/// <summary>初始起伏的 fBm 倍频层数（振幅每层折半 ⇒ 5 层已含 97% 能量）。</summary>
		public int ReliefOctaves = 5;

		readonly Ball _ball;
		readonly MaterialDensity _material;
		readonly H3PlateMotion _motion;
		readonly H3PlateAdvection _advection;
		readonly H3Isostasy _isostasy;
		readonly H3SurfaceProcesses _surface;    // 坡面侵蚀/风化/成岩/变质（8b）
		readonly H3FluvialTransport _fluvial;    // 河流输沙（8b-2）
		readonly H3AeolianTransport _aeolian;    // 方向性风沙（8b-1d）
		readonly bool[] _fluvialMask;            // 输沙热路径复用缓冲（每步 new 是几十 MB 级垃圾）
		readonly float[] _fluvialPrecip;
		readonly float[] _surfaceTempC;          // 地表过程的温度介质（每步按当时海拔重算）
		readonly float[] _erosionPrecip;         // 侵蚀用的闭合降水（结构场 × λ，见 Step 的 8b-1b）
		readonly float[] _windSpeedMS;           // 地表过程的风介质（逐格 |u| m/s，随海拔每步重算）
		H3Wind.UpwindStencil _fluvialStencil;    // 盛行风模板（懒建：行星档构造后才定；改档重建）
		bool _fluvialStencilPrograde;
		float _fluvialStencilSpeed;
		H3PlateFields _fields;
		H3PlateFields _scratch;
		float[]? _sheetGpe, _sheetElev;                       // 薄席工作缓冲（GPE / 海拔）
		Vector3[]? _sheetDriveRadPerMy;                       // 板级刚体驱动场（rad/My，喂运动学外部覆写口）
		H3FluxTransport? _flux;                               // 通量化运输（P3，懒建）
		readonly bool[] _topologyVisited;               // 连通分量 BFS 用（复用，避免每步分配）
		readonly int[] _topologyStack;
		readonly int[] _topologyComp;                   // 每格连通分量号（只记号+尺寸，免逐分量 List）
		int[] _compSizeScratch;                         // 分量 → 格数（按需扩容，复用）
		int[] _compPlateScratch;                        // 分量 → 板号（复用）
		bool[] _compMergeMark;                          // 分量 → 是否待并入（复用）
		readonly Dictionary<int, List<int>> _fragmentCells = new();   // 待并分量 → 格表（≤2 格，复用）
		readonly List<(int plate, int firstComp)> _plateOrderScratch = new();   // 板迭代序（= 字典插入序）
		readonly float[] _yieldRemovedScratch = new float[7];   // 屈服流：当前超帽柱的逐池流出量（复用）
		readonly List<(int tier, float negWeight, int nb)> _yieldNeighbors = new(); // 接收者排序暂存（复用）
		readonly int[] _yieldVisitedStamp;                      // 屈服流档③：同相环带 BFS 已访代数戳（复用；
															// 免每个超帽格一次 O(N) Array.Clear——超帽格上千时即毫秒级）
		int _yieldVisitedGeneration;
		readonly List<int> _yieldRing = new();                  // 屈服流档③：当前环（复用）
		readonly List<int> _yieldNextRing = new();              // 屈服流档③：下一环（复用）
		readonly List<(float negWeight, int nb)> _yieldRingWeights = new(); // 屈服流档③：环内排序暂存（复用）
		/// <summary>屈服流档③的同相 BFS 最大扩环数：放置半径 = 1 + 本值格（环 2..1+本值）。</summary>
		public const int YieldFlowMaxRings = 4;
		readonly bool[] _jamFront;                              // 本步顶死前缘格标记（屈服流档①目标）
		readonly List<(int a, int b)> _firedPairs = new();            // 本步触发的缝合板对（复用）
		readonly List<int> _absorbFragmentPlates = new();             // 待吸收碎片板（复用）
		readonly int[] _neighborPlateScratch = new int[6];    // 逐格邻居按板分类（定长，不分配）
		readonly int[] _neighborCountScratch = new int[6];
		// 板号 → 格数：代数戳数组 + 首现序表（原为 HashSet+Dictionary 双哈希，每格 2 次哈希是
		// 拓扑量测的常数大头；板号小而稠密 ⇒ 数组下标直查）。_platePresentOrder 保持首现序 =
		// 原字典插入序，消费口的遍历序与浮点累加序逐位不变。
		int[] _plateCellCountArr;
		int[] _plateCellCountStamp;
		int _plateCellCountGeneration;
		readonly List<int> _platePresentOrder = new();
		readonly Dictionary<(int a, int b), int> _pairEdges = new();            // 逐对边界边数（两侧各一，缝合触发器）
		readonly Dictionary<int, int> _plateBoundaryEdges = new();              // 逐板边界边数（两侧各一）
		readonly Dictionary<(int a, int b), int> _sutureStreak = new();         // 缝合资格连击步数
		readonly Dictionary<int, int> _edgesPerPlate = new();                   // 碎片并入的共享边计数（复用）

		// ── 输出（供渲染/诊断）──
		public H3PlateFields Fields => _fields;
		public H3PlateMotion Motion => _motion;
		public H3PlateAdvection Advection => _advection;   // 平流本体（诊断/调试取数口）
		public H3SurfaceProcesses Surface => _surface;     // 坡面侵蚀判读口
		public H3FluvialTransport Fluvial => _fluvial;     // 河流输沙判读口
		public H3AeolianTransport Aeolian => _aeolian;     // 风沙搬运判读口
		/// <summary>地幔热状态：势温 Tp(t) + 热收支诊断；每步 1b 推进，黏度经 Arrhenius 注入
		/// `_motion`（"发动机缓慢熄火"的源项）。</summary>
		public H3ThermalState Thermal { get; private set; } = new H3ThermalState();
		public float[] Displacement => _isostasy.Displacement;
		public float SeaLevel => _isostasy.SeaLevel;
		/// <summary>本步挠曲抬升/下压的最大幅度（m）——前隆的实测上限；板支撑的合法抬高
		/// 会突破"海拔 ≤ 厚度帽 Airy 当量"这条局部界（见 `OrogenCeiling` 测试）。</summary>
		public float FlexurePeakUpliftM => _isostasy.FlexurePeakUpliftM;
		/// <summary>本步挠曲削低的最大幅度（m）= 区域支撑强度。</summary>
		public float FlexurePeakReductionM => _isostasy.FlexurePeakReductionM;
		/// <summary>本步挠曲 CG 的最大迭代残差（m；大 = 未收敛，板偏软）。</summary>
		public float FlexureResidualM => _isostasy.FlexureResidualM;
		/// <summary>挠曲求解迭代次数。设 0 = 关闭挠曲（位移 = 纯 Airy），A/B 对照用。</summary>
		public int FlexureIterations
		{
			get => _isostasy.FlexureIterations;
			set => _isostasy.FlexureIterations = value;
		}
		/// <summary>全球水量（按洋壳面积平均的等效水深 m）：初始化摊派一次后守恒，
		/// 水量账本（海+云+河）与气候层的预留都以它为准。</summary>
		public double TotalOceanDepth => _isostasy.TotalOceanDepth;
		public int StepCount { get; private set; }
		public int PlateCount { get; private set; }

		// ── 质量守恒对账：TotalCrustMass() ≈ InitialCrustMass + CrustCreatedTotal − CrustDestroyedTotal。
		// 进出仅有的四户：裂谷/兜底注壳与陆内重采样（创建）、俯冲+顶层裁决回地幔（消减）、
		// 厚度帽溢流（消减；堆回前缘的部分计创建，净额自动归零）、陆壳收支伺服（创建；
		// 5b 步把守恒组销毁的账面缺口从地幔补回弧格）。
		public double InitialCrustMass { get; private set; }
		/// <summary>长英质刮削累计（俯冲再循环的陆壳出口）——陆壳收支的出口之一（另两户：埋入沉积全额、
		/// 厚度帽溢流的守恒组份额，见 <see cref="RecycledConservedTotal"/>/<see cref="ConservedYieldToMantleCum"/>）。</summary>
		public double FelsicScrapedCum { get; private set; }
		/// <summary>弧回流长英质累计（自地幔新生的陆壳入口，弧岩浆通道）——含基础系数回流与
		/// <see cref="ApplyContinentalBudgetServo"/> 的缺口补偿。陆壳收支不变量：本值 ≥ 守恒组销毁累计
		/// ⇒ 陆壳存量只增不减（长跑不走向水世界的保证）。</summary>
		public double FelsicArcReturnedCum { get; private set; }
		/// <summary>埋入层守恒组回地幔累计（沉积类全额 + 长英质刮削，俯冲再循环通道）。</summary>
		public double RecycledConservedTotal { get; private set; }
		/// <summary>厚度帽溢流回地幔的守恒组份额累计（溢流按成分比例，放不下的残余里
		/// 长英质/沉积类所占部分——陆壳收支的出口之一，由伺服补回）。</summary>
		public double ConservedYieldToMantleCum { get; private set; }
		/// <summary>本步伺服实际补进弧格的长英质质量（判读口；0 = 预算无缺口或本步无弧）。</summary>
		public double ContinentalServoPlacedLastStep { get; private set; }
		public double CrustCreatedTotal { get; private set; }
		public double CrustDestroyedTotal { get; private set; }

		/// <summary>重定审计基线（测试手工改场后调用：以当前总量为"初始"）。</summary>
		public void ResetMassAuditBaseline()
		{
			InitialCrustMass = TotalCrustMass();
			CrustCreatedTotal = 0;
			CrustDestroyedTotal = 0;
		}

		/// <summary>当前全球地壳总质量（七个质量场之和，Age 不计；double 累加防大数漂移）。</summary>
		public double TotalCrustMass()
		{
			double sum = 0;
			int n = _fields.Count;
			for (int i = 0; i < n; i++) sum += _fields.TotalMass(i);
			return sum;
		}

		// ── 每步记账（判读口）──
		public int HoleCellsLastStep { get; private set; }
		public int MixedCellsLastStep { get; private set; }
		public int OverflowCellsLastStep { get; private set; }
		public int ClampedCellsLastStep { get; private set; }
		public int ResampledCellsLastStep { get; private set; }        // 陆内拉伸重采样补料格数
		public double RecycledToMantleLastStep { get; private set; }
		public int JammedCellsLastStep { get; private set; }           // 平流实测：链头顶死在异板物质上的格数（真碰撞接触）
		/// <summary>本步**陆-陆碰撞**格数（顶死且两侧皆陆；platec `continental_collisions` 口径）——
		/// 周期重启触发③"久无陆-陆碰撞"的输入。</summary>
		public int ContinentalJamCellsLastStep { get; private set; }
		/// <summary>本步通量化运输的俯冲边数（P3 判读口；跳格路径恒 0）。</summary>
		public int FluxSubductEdgesLastStep { get; private set; }
		public IReadOnlyDictionary<int, int> JamCellsPerPlateLastStep => _advection.JamCellsPerPlate;
		public int JamContactCellsLastStep { get; private set; }       // 运动学预测：顶死接触剔除出净驱动的板缘格数
		public float PhantomForceFractionLastStep { get; private set;}// 幻影驱动力占比 = 被剔除 F / 毛 F（0 = 无顶死接触）

		/// <summary>换色事件日志（诊断/溯源）：每格归属变更 = (步号, 格, 原板, 新板, 路径)。
		/// kind：0 = arrival 翻色，1 = 空洞填新壳（被异板完全包围），2 = 陆内重采样（复制陆邻居），
		/// 3 = 海底连续填充（复制洋邻居），4 = 碎片并入。</summary>
		public List<(int step, int cell, int from, int to, int kind)> ChangeEvents => _advection.ChangeEvents;
		public int RiftInjectedCellsLastStep { get; private set; }
		public float LandFractionLastStep { get; private set; }

		// ── 归属拓扑量测（判读口，非机制；MeasurePlateTopology 每步更新）──
		public int OwnerlessCellsLastStep { get; private set; }        // 无主格（PlateId = -1）
		public int IsolatedPlateCellsLastStep { get; private set; }    // 孤立格：邻居里一个同板都没有
		public int LocalMinorityCellsLastStep { get; private set; }    // 强少数格：本板邻居数 < 任一他板邻居数
		public int PlateComponentCountLastStep { get; private set; }   // 全球连通分量数（同板相邻才算连通）
		public float LargestComponentFractionLastStep { get; private set; }
		public float LargestPlateFractionLastStep { get; private set; }  // 最大板占全球格数比（超大陆讨论输入）

		public H3DynamicTectonics(Ball ball, MaterialDensity material = null)
		{
			_ball = ball;
			_material = material ?? new MaterialDensity();
			int n = ball.CellIds.Length;
			_fields = new H3PlateFields(n);
			_scratch = new H3PlateFields(n);
			_motion = new H3PlateMotion(n);
			_advection = new H3PlateAdvection(n);
			_isostasy = new H3Isostasy(n);
			_surface = new H3SurfaceProcesses(ball);
			_fluvial = new H3FluvialTransport(ball);
			_aeolian = new H3AeolianTransport(ball);
			_fluvialMask = new bool[n];
			_fluvialPrecip = new float[n];
			_surfaceTempC = new float[n];
			_erosionPrecip = new float[n];
			_windSpeedMS = new float[n];
			_topologyVisited = new bool[n];
			_topologyStack = new int[n];
			_topologyComp = new int[n];
			_jamFront = new bool[n];
			_yieldVisitedStamp = new int[n];
			_yieldThicknessCache = new float[n];
			_yieldThicknessValid = new bool[n];
		}

		/// <summary>盛行风模板（懒建一次：ProgradeRotation/RotationSpeed 是构造后由持有方对象
		/// 初始化器写入的——H3Plate 下行；与缓存指纹不一致 = 改档，重建）。</summary>
		H3Wind.UpwindStencil FluvialStencil()
		{
			if (_fluvialStencil == null || _fluvialStencilPrograde != ProgradeRotation
				|| _fluvialStencilSpeed != RotationSpeed)
			{
				_fluvialStencil = H3Wind.BuildStencil(_ball, ProgradeRotation, RotationSpeed);
				_fluvialStencilPrograde = ProgradeRotation;
				_fluvialStencilSpeed = RotationSpeed;
			}
			return _fluvialStencil;
		}

		// ═══════════════════════════════════════════════
		// 初始化：初始地壳 + 初始分板（外部给定）
		// ═══════════════════════════════════════════════

		/// <summary>初始化：建初始地壳 + 归属。归属只在初始分板定这一次（`plateOfCell` 写进物质场），
		/// 此后 `PlateId` 随物质搬运，不再有独立的领土场。</summary>
		public void Initialize(int[] plateOfCell, int seed)
		{
			Seed = seed;
			StepCount = 0;
			CrustCreatedTotal = 0;
			CrustDestroyedTotal = 0;
			FelsicScrapedCum = 0;
			FelsicArcReturnedCum = 0;
			RecycledConservedTotal = 0;
			ConservedYieldToMantleCum = 0;
			ContinentalServoPlacedLastStep = 0;
			SutureCount = 0;
			SplitCount = 0;
			RestartCount = 0;
			_splitCooldownUntil = 0;
			_stallStreak = 0;
			CycleStepCount = 0;
			StepsSinceContinentalCollision = 0;
			_systemMomentumLastStep = 0;
			_peakMomentum = 0;
			MomentumRatioLastStep = 1f;
			LastRestartReason = "";
			_sutureStreak.Clear();
			int maxPlate = -1;
			var distinct = new HashSet<int>();
			foreach (int p in plateOfCell)
				if (p >= 0 && distinct.Add(p) && p > maxPlate) maxPlate = p;
			_initialPlateCount = Math.Max(distinct.Count, 2);
			_nextPlateId = maxPlate + 1;
			BuildInitialCrust(plateOfCell, seed);
			InitialCrustMass = TotalCrustMass();
			// 水量：全球等效水层（GlobalWaterLayerM，行星参数）按洋盆格数摊派成 TOD，
			// OceanScale 对水层整体缩放。此后守恒；海平面/海岸线/陆高/洋深全部由容积守恒 + 均衡涌现。
			_isostasy.TotalOceanDepth = _isostasy.DistributeGlobalWaterLayer(_ball, _fields, _material) * OceanScale;
			_isostasy.ComputeDisplacement(_ball, _fields, _material);
			// 地幔热状态复位（t = 0 = 地球现值）
			Thermal.Reset();
			_motion.MantleViscosityPaS = Thermal.MantleViscosityPaS;
		}

		// 初始地壳：逐格大陆性场（低频大陆斑块 + fBm 海岸细节，与板属性按 LandOceanNoiseBlend 混合）
		// 定陆洋；海洋占比由 OceanFraction 的分位数阈值钉住——混合系数只改空间形态，不改海陆配比。
		// 物质模板：陆格全 felsic / 洋格全 mafic；起伏一律写厚度（Airy 反解），海拔由均衡派生。
		void BuildInitialCrust(int[] plateOfCell, int seed)
		{
			int n = _ball.CellIds.Length;
			var centers = _ball.CellCenters;
			var plates = _fields;

			// ① 板级陆/洋倾向：洗牌后前 k 板为陆板（同种子同结果）——作大陆性场的偏置项
			var rng = new DeterministicRandom(seed + 999);
			var plateIds = new List<int>();
			var seen = new HashSet<int>();
			foreach (int p in plateOfCell)
				if (p >= 0 && seen.Add(p)) plateIds.Add(p);
			plateIds.Sort();
			for (int k = plateIds.Count - 1; k > 0; k--)       // Fisher–Yates 洗牌（确定性）
			{
				int j = (int)(rng.NextDouble() * (k + 1));
				(plateIds[k], plateIds[j]) = (plateIds[j], plateIds[k]);
			}
			int landPlateCount = Math.Clamp(
				(int)MathF.Round(plateIds.Count * (1f - OceanFraction)), 0, plateIds.Count);
			var landPlates = new HashSet<int>();
			for (int k = 0; k < landPlateCount; k++) landPlates.Add(plateIds[k]);

			// ② 逐格大陆性场：大陆斑块 + 海岸细节按混合系数并入板倾向。blend = 0 时不建场，
			//    陆洋完全由板属性定（也免掉 O(N log N) 排序）。
			float landCut = float.MaxValue;
			float[] continentality = null;
			if (LandOceanNoiseBlend > 0f)
			{
				var continentBlobs = new SphericalBlobNoise(seed + 271, NumContinents);
				var coastDetail = new SphericalFbmNoise(seed + 733, CoastDetailWavelengthKm, 4);
				var dirs = new Vector3[n];
				for (int i = 0; i < n; i++) dirs[i] = centers[i].Normalized();   // 团场吃单位方向
				float[] blobRank = continentBlobs.SampleAll(dirs);               // 0..1（团场排名归一）
				continentality = new float[n];
				for (int i = 0; i < n; i++)
				{
					int plate = plateOfCell != null && i < plateOfCell.Length ? plateOfCell[i] : 0;
					float bias = landPlates.Contains(plate) ? 1f : -1f;
					float field = blobRank[i] * 2f - 1f
						+ CoastDetailAmplitude * coastDetail.Sample(centers[i]);
					continentality[i] = (1f - LandOceanNoiseBlend) * bias + LandOceanNoiseBlend * field;
				}
				var sorted = (float[])continentality.Clone();
				Array.Sort(sorted);
				landCut = sorted[Math.Clamp((int)(n * OceanFraction), 0, n - 1)];
			}

			var ageNoise = new SphericalBlobNoise(seed + 100, NumContinents * 2);   // 洋壳年龄梯度场
			// 初始起伏场（3D fBm）：陆/洋共用同一张分形场、各自缩放
			var reliefNoise = new SphericalFbmNoise(seed + 313, ReliefBaseWavelengthKm, ReliefOctaves);
			// Airy 反解按物质各用系数（陆长英质 / 洋镁铁质）：写端必须与均衡的读端同源，否则起伏缩水。
			float airyFelsic = H3Isostasy.AiryFactorFor(_material.FelsicPlutonic, _material);
			float airyMafic = H3Isostasy.AiryFactorMafic(_material);

			// ③ 物质模板（质量面密度 kg/m² = ρ × 厚度）
			for (int i = 0; i < n; i++)
			{
				int plate = plateOfCell != null && i < plateOfCell.Length ? plateOfCell[i] : 0;
				bool land = continentality != null ? continentality[i] > landCut : landPlates.Contains(plate);
				Vector3 p = centers[i];
				float relief = reliefNoise.Sample(p) * (land ? LandReliefAmplitudeM : OceanReliefAmplitudeM);
				plates.ClearCell(i);
				if (land)
				{
					// 陆壳：参考厚 + 起伏/kAiry（Airy 反解，与洋壳同一式）——fBm 场即初始海拔增量。
					// 质量用密度表同款 ρ 写，厚度才与读端精确往返。
					float landThickness = H3Isostasy.LandReferenceThicknessM + relief / airyFelsic;
					plates.FelsicPlutonic[i] = _material.FelsicPlutonic * 0.85f * landThickness;
					plates.FelsicVolcanic[i] = _material.FelsicPlutonic * 0.15f * landThickness;
				}
				else
				{
					// 洋壳：参考厚 + 起伏/kAiry（Airy 反解）——洋底起伏经"厚度 → 均衡位移" 1:1 还原。
					plates.MaficVolcanic[i] = _material.MaficVolcanicMin * (H3Isostasy.OceanReferenceThicknessM + relief / airyMafic);
				}
				// 沉积盖层：陆格恒有薄沉积（12500 kg/m² ≈ 8 m 厚），洋格 0
				plates.Sediment[i] = land ? 12500f : 0f;
				// Age = 岩石圈热年龄（热沉降的唯一输入）：陆壳写 0（大陆岩石圈热稳态、不冷却沉降）；
				// 洋壳写 0–200 My 噪声梯度 = 离脊年龄的初始梯度 ⇒ 洋底初始热沉降剖面。
				plates.Age[i] = land ? 0f : Math.Clamp(ageNoise.Sample(p), 0f, 1f) * 200f;
				plates.PlateId[i] = plate;
			}
		}

		// ═══════════════════════════════════════════════
		// 主循环
		// ═══════════════════════════════════════════════

		public void RunWithProgress(Action<float> onProgress = null)
		{
			int steps = Math.Max(1, (int)(RunMy / StepMy));
			for (int s = 0; s < steps; s++)
			{
				Step();
				onProgress?.Invoke((s + 1f) / steps);
			}
		}


		/// <summary>走一个时间步（流水线 1–8b，再接 9-10 生命周期）。</summary>
		public void Step()
		{
			// 1 热年龄累积：洋壳按离脊年龄累积；盖厚陆壳的格冻结（大陆岩石圈热稳态，不冷却沉降）。
			//   地图型并行：写只落 Age[i] 本格，逐元素算式与串行逐位一致。
			ParallelLoops.For(_fields.Count, i =>
			{
				if (!_fields.IsLand(i)) _fields.Age[i] += StepMy;
			});

			// 1b 地幔热状态：势温按热收支下降 → Arrhenius 黏度注入运动学；本步力平衡用的就是更新后的 η。
			Thermal.Step(_fields, StepMy);
			_motion.MantleViscosityPaS = Thermal.MantleViscosityPaS;
			_motion.PotentialTemperatureK = Thermal.PotentialTemperatureK;

			// 2 运动学：浮力 → 力平衡速度（顶死接触格不计净驱动）→ 每板旋转增量 + CFL 钳制。
			// P2 薄席驱动开启时：先解薄席并经外部覆写口喂数，力平衡在 motion 内部跳过（设计-07 §5）。
			if (EnableThinSheetDriving) DriveByThinSheet();
			_motion.Step(_ball, _fields, _material, 9.8f, StepMy);
			ClampedCellsLastStep = _motion.ClampedCellCount;
			JamContactCellsLastStep = _motion.JamContactCellCount;
			PhantomForceFractionLastStep = _motion.PhantomForceFraction;
			MeasureSpeedTiers();

			// 3 物质运输：两套机制按 EnableFluxTransport 切换（设计-07 P3）。
			//   · 跳格平流（旧）：起跳相位 + 链头裁决（俯冲/顶死）+ 空洞填充；
			//   · 通量化运输（P3 新）：速度场的欧拉 upwind 通量——俯冲 = 通量汇、加厚 = 收敛堆积、
			//     薄柱注壳内建（创建账）。台账两式同构：Created/Recycled/SlabInflow/弧回流。
			if (EnableFluxTransport)
			{
				_flux ??= new H3FluxTransport(_ball)
				{
					RecycleSedimentFraction = RecycleSedimentFraction,
					RecycleFelsicFraction = RecycleFelsicFraction,
					ArcFelsicReturnFraction = ArcFelsicReturnFraction,
				};
				_flux.Step(_fields, _scratch, _motion.Velocity, _material, StepMy, StepCount);
				// 创建账两户：薄柱注壳 + 弧回流（"从地幔新生长英质"= 净增质量，必须记创建——
				// 漏记则质量审计逐步多出弧回流量，实测 1.8e9 kg/步）。
				CrustCreatedTotal += _flux.CreatedMassLastStep + _flux.ArcFelsicReturnedMassLastStep;
				FelsicArcReturnedCum += _flux.ArcFelsicReturnedMassLastStep;  // 弧回流（伺服账同源）
				foreach (var entry in _flux.SlabInflow)
					_motion.AddSlab(entry.plate, entry.massPerArea, entry.dir);
				(_fields, _scratch) = (_scratch, _fields);
				RecycledToMantleLastStep = _flux.RecycledToMantleLastStep;
				FluxSubductEdgesLastStep = _flux.SubductEdgesLastStep;
				HoleCellsLastStep = 0; MixedCellsLastStep = 0; OverflowCellsLastStep = 0;
				ResampledCellsLastStep = 0; JammedCellsLastStep = 0; ContinentalJamCellsLastStep = 0;
			}
			else
			{
				_advection.RecycleSedimentFraction = RecycleSedimentFraction;
				_advection.RecycleFelsicFraction = RecycleFelsicFraction;
				_advection.ArcFelsicReturnFraction = ArcFelsicReturnFraction;
				_advection.Step(_ball, _fields, _motion, _scratch, _material, StepCount);
				FelsicScrapedCum += _advection.FelsicScrapedMassLastStep;
				FelsicArcReturnedCum += _advection.ArcFelsicReturnedMassLastStep;
				RecycledConservedTotal += _advection.RecycledConservedMass;
				foreach (var kv in _advection.SlabInflow)
					_motion.AddSlab(kv.Key, kv.Value.mass, kv.Value.dir);
				(_fields, _scratch) = (_scratch, _fields);
				HoleCellsLastStep = _advection.HoleCount;
				MixedCellsLastStep = _advection.MixedCellCount;
				OverflowCellsLastStep = _advection.OverflowCellCount;
				ResampledCellsLastStep = _advection.ResampledCellCount;
				RecycledToMantleLastStep = _advection.RecycledToMantleMass;
				JammedCellsLastStep = _advection.JamCellCount;
				ContinentalJamCellsLastStep = _advection.ContinentalJamCellCount;
			}

			// 4 裂谷/洋底扩张：跳格路径的空洞注壳（通量化路径无空洞概念——注壳已内建于 ③ 薄柱阈值）。
			if (!EnableFluxTransport)
			{
				_advection.EnableSpreading = EnableSpreading;
				_advection.SpreadingSpeedKmPerMy = SpreadingSpeedKmPerMy;
				if (EnableRifting)
					_advection.FillHolesWithNewOceanicCrust(_ball, _fields, _motion, _material.MaficVolcanicMin * H3Isostasy.OceanReferenceThicknessM);
				CrustCreatedTotal += _advection.CrustCreatedMass;
			}
			CrustDestroyedTotal += RecycledToMantleLastStep;

			// 4b 归属清理：平流绕出的小残块终身带异板色（渲染毛刺），并入包围板——只改标签、不动物质。
			MergeStrayFragments();

			// 5 俯冲体积记账：超出厚度上限的部分进增生楔堆到上盘；被埋板的 mafic 已在平流顶层裁决里记账回地幔。
			ApplyCrustYieldFlow();
			CrustDestroyedTotal += YieldToMantleMassLastStep;   // 只有流不出去的残余是消减；落邻格 = 纯搬运

			// 5b 陆壳收支伺服：守恒组销毁累计超出弧回流累计的缺口，按本步洋→陆俯冲通量
			//     从地幔补回弧格（记创建账）——陆壳存量的保底（长跑不走向水世界）。
			ApplyContinentalBudgetServo();

			// 6–7 均衡位移 + 挠曲：传入势温比例 ⇒ 热沉降与驱动力同步随地幔降温变弱。
			//   水量只在 Initialize 定一次、此后守恒，海平面每步由水量守恒逐步解出；
			//   ComputeDisplacement 内部已含区域挠曲 ⇒ 不再单独调 ApplyFlexure（否则做两遍）。
			_isostasy.ComputeDisplacement(_ball, _fields, _material, TemperatureScale());

			// 8 水量守恒海平面：挠曲改变洋底体积后重解（位移与海平面解耦，无需 rebase）。
			_isostasy.SolveSeaLevelByVolume();
			LandFractionLastStep = _isostasy.LandFraction();

			// 8b 地表过程：坡面侵蚀 + 风化/成岩/变质（搬运与转化全在守恒组内部，组内质量守恒零记账）。
			//   侵蚀改变载荷 ⇒ 位移/海平面重算一遍（挠曲不重跑，下一步 6-7 自动跟上）。
			if (EnableErosion)
			{
				var surfaceHeight = _surface.ComputeSurfaceHeight(_isostasy);

				// 8b-1 介质输入：降水结构场 + 按当时海拔重算的年均温（与终态气候共用 H3Climate 同一骨架，
				//   演化期冰川与出图同一处）。坡面风化门也要降水，故不只 EnableFluvial 时才算。
				ParallelLoops.For(_fields.Count, i => _fluvialMask[i] = _fields.IsLand(i));
				H3Precipitation.ComputeInto(_ball, _fluvialMask, surfaceHeight, Seed,
					AxialTiltDeg, Insolation, ProgradeRotation, RotationSpeed,
					_fluvialPrecip, FluvialStencil());
				if (PrecipitationScale != 1f)
					ParallelLoops.For(_fluvialPrecip.Length, i => _fluvialPrecip[i] *= PrecipitationScale);

				// 8b-1b 闭合降水：结构场只定"雨下在哪"，总量由水源定——λ = 蒸发总量/结构场总量
				//   （PrecipitationScale 乘在蒸发上 = 水量总闸）。水介质/风化门/河流都吃闭合场：
				//   水力几何的绝对流量对水量尺度敏感，不能锚在任意结构场上。
				//   洋格计数走整型线程局部合并（整数加法精确可结合）；Σ结构场是浮点 Σ 保持串行累加序。
				double evapTotalMm = H3WaterCycle.EvapRefMmPerYear * PrecipitationScale * (double)CountOceanCells();
				double rawTotalMm = 0;
				for (int i = 0; i < _fluvialPrecip.Length; i++) rawTotalMm += _fluvialPrecip[i];
				PrecipitationClosureFactor = rawTotalMm > 0 ? (float)(evapTotalMm / rawTotalMm) : 1f;
				ParallelLoops.For(_erosionPrecip.Length,
					i => _erosionPrecip[i] = _fluvialPrecip[i] * PrecipitationClosureFactor);

				H3Climate.ComputeInto(_ball, surfaceHeight, Seed, AxialTiltDeg, Insolation, _surfaceTempC);
				// 8b-1c 风速度场：|u| 随当步海拔重算（方向步不变，走风模板缓存），高山风大顺势增强。
				H3Wind.SpeedField(FluvialStencil(), surfaceHeight, RotationSpeed, _windSpeedMS);
				_surface.BindFieldsForK(_fields);
				_surface.Apply(_fields, surfaceHeight, _material, StepMy, ErosionScale,
					new H3SurfaceProcesses.ErosionMedium(_erosionPrecip, _surfaceTempC, _windSpeedMS));

				// 8b-1d 方向性风沙：侵蚀→搬运→沉积沿风向走。三通道执行序 = 坡面（风/重力/冰川）→ 风 → 河
				//   （河最后改地形最多；河流向用本步步首地形）。
				if (EnableAeolian)
					_aeolian.Apply(_fields, surfaceHeight, _windSpeedMS, FluvialStencil(),
						_erosionPrecip, _surfaceTempC, AeolianScale);

				// 8b-2 河流输沙：侵蚀→搬运→沉积三段，负载沿汇流链下行、洼地/入海落底（组内守恒零记账）；
				//   海拔变化由下面既有的均衡+海平面重算跟上。
				if (EnableFluvial)
					_fluvial.Apply(_fields, surfaceHeight, _erosionPrecip, FluvialScale);

				_isostasy.ComputeDisplacement(_ball, _fields, _material, TemperatureScale());   // 内含 SolveSeaLevelByVolume
				LandFractionLastStep = _isostasy.LandFraction();
			}

			// 9–10 板块生命周期：缝合 / 裂解 / 重启（场保留）。每步恰一次，且必须在拓扑量测之前（改归属）。
			CycleStep();

			// 板数 + 归属拓扑量测（无主/孤立格、连通分量、最大板占比）
			MeasurePlateTopology();
			StepCount++;
		}

		// ═══ P2 薄席驱动（设计-07 §5）═══
		// 板级 GPE 净力 → 底层拖曳平衡速度：v_p = |F_p|/(c_b·A_p)（与旧模型 v = F·δ/(η·A) 同构，
		// 力源换成 GPE 梯度净力——幻影力/板片通道/洋脊通道退役，洋脊推力作为 GPE 差自然包含）。
		// ⚠️ 不走逐格薄席解算器：球面切面投影使全球性驱动模式（平移类）在边网络上锁死
		// （线形坡实测收敛到 ~0；res1/res2 同病）——逐格变形解算器留作 P3 应变场输入，不进热路径。
		// O(N·6) 逐格力聚合，每步全价。

		/// <summary>底层拖曳系数（N·yr/m³）：单位面积的地幔黏滞阻力——板速 = 板级 GPE 净力/(c_b·A_p)。
		/// res3 标定 7e6 ⇒ 地球档 cm/yr。</summary>
		public float BasalDragNyrPerM3 = 7e6f;

		void DriveByThinSheet()
		{
			int n = _fields.Count;
			_sheetElev ??= new float[n];
			_sheetGpe ??= new float[n];
			_sheetDriveRadPerMy ??= new Vector3[n];

			for (int i = 0; i < n; i++)
				_sheetElev[i] = _isostasy.Displacement[i] - _isostasy.SeaLevel;
			H3Gpe.ComputeInto(_fields, _sheetElev, _material, _sheetGpe);

			// 逐格 GPE 梯度体力（六边格梯度恢复，与 H3ThinSheet 同式）+ 板级聚合
			var plateId = _fields.PlateId;
			int maxPlate = 0;
			for (int i = 0; i < n; i++) if (plateId[i] > maxPlate) maxPlate = plateId[i];
			var force = new Vector3[maxPlate + 1];
			var area = new float[maxPlate + 1];
			var dirSum = new Vector3[maxPlate + 1];
			var count = new int[maxPlate + 1];
			var neighbors = _ball.CellNeighbors;
			var centers = _ball.CellCenters;
			var dirs = _ball.CellDirs;
			float radiusM = H3PlateMotion.EarthRadiusKm * 1000f;
			float sceneToM = radiusM / _ball.Radius;
			float cellAreaM2 = 4f * MathF.PI * radiusM * radiusM / n;
			for (int i = 0; i < n; i++)
			{
				Vector3 grad = Vector3.Zero;
				foreach (int nb in neighbors[i])
				{
					Vector3 dir = centers[nb] - centers[i];
					float dPhys = dir.Length() * sceneToM;
					Vector3 mid = (centers[i] + centers[nb]) * 0.5f;
					Vector3 radial = mid.Normalized();
					Vector3 t = dir - radial * dir.Dot(radial);
					grad += (float)(_sheetGpe[nb] - _sheetGpe[i]) * t.Normalized() / dPhys;
				}
				grad *= 2f / 3f;
				int p = plateId[i];
				if (p < 0) continue;
				force[p] += -grad * cellAreaM2;
				area[p] += cellAreaM2;
				dirSum[p] += dirs[i];
				count[p]++;
			}

			// 板片牵引（P2 力清单补全）：俯冲板片经账户持续拉板——真实地球的主驱动力。
			// 力 = w·g·|账户方向和|·格面积（与旧 ③ 板片通道同式），方向 = 账户方向在板心切面的投影。
			for (int p = 0; p <= maxPlate; p++)
			{
				if (count[p] == 0) continue;
				Vector3 cHat = dirSum[p].Normalized();
				Vector3 slabDir = _motion.SlabDirOf(p);
				float slabMag = slabDir.Length();
				if (slabMag <= 0f) continue;
				Vector3 d = slabDir - cHat * slabDir.Dot(cHat);       // 账户方向投到板心切平面
				float dl = d.Length();
				if (dl <= 1e-9f) continue;
				float fSlab = H3PlateMotion.SlabPullWeightFraction * 9.8f * slabMag * cellAreaM2;
				force[p] += d / dl * fSlab;
			}

			// 板级刚体驱动：净力方向 → 旋转轴（携质心沿净力方向走）→ 角速度 = 板速/R
			float toRadPerMy = 1e6f / radiusM;                                 // m/yr → rad/My
			// ⚠️ 符号约定（测试钉住 ThinSheetDriving_FlowsTowardLowerGpe）：拟合+写出链路
			// 对驱动场整体取反——外部驱动场按 −物理角速度 喂入，平流才沿净力方向跳格。
			double speedSumKmPerMy = 0;
			int speedCount = 0;
			for (int p = 0; p <= maxPlate; p++)
			{
				Vector3 omega = Vector3.Zero;
				// ⚠️ 力模长必须走 double：板级合力 ~1e19–1e21 N（物理量级），float 平方即溢出
				//（1e19² = 1e38 ≈ float.MaxValue 3.4e38）⇒ Length() = ∞ ⇒ 板速/驱动场全废。
				double fx = force[p].X, fy = force[p].Y, fz = force[p].Z;
				double fLen = Math.Sqrt(fx * fx + fy * fy + fz * fz);
				if (count[p] > 0 && fLen > 0)
				{
					Vector3 cHat = dirSum[p].Normalized();
					Vector3 fHat = new Vector3((float)(fx / fLen), (float)(fy / fLen), (float)(fz / fLen));
					Vector3 axis = cHat.Cross(fHat);
					if (axis.LengthSquared() > 1e-12f)
					{
						double vP = fLen / ((double)BasalDragNyrPerM3 * area[p]);        // m/yr
						omega = axis.Normalized() * (float)(vP * toRadPerMy);
					}
				}
				for (int i = 0; i < n; i++)
				{
					if (plateId[i] != p) continue;
					_sheetDriveRadPerMy[i] = -omega.Cross(dirs[i]);
				}
				if (count[p] > 0)
				{
					speedSumKmPerMy += omega.Length() * H3PlateMotion.EarthRadiusKm;
					speedCount++;
				}
			}

			_motion.ThinSheetDriving = true;
			_motion.ExternalDriveVelocity = _sheetDriveRadPerMy;
			// 拟合门槛自适应：薄席驱动的板速可低至 mm/yr 量级——固定 1 km/My 门槛会把拟合
			// 全部滤空（速度场恒零）。门槛 = 当步板均速的 20%，钳到 [0.02, 1] km/My。
			if (speedCount > 0)
				_motion.MinFitSpeedKmPerMy = Math.Clamp(
					0.2f * (float)(speedSumKmPerMy / speedCount), 0.02f, 1f);
		}

		/// <summary>本步的降水闭合系数 λ：全球 Σ 降水 = 洋面蒸发总量。
		/// = EvapRefMmPerYear × 洋格数 / Σ结构场；侵蚀的水介质与河流都吃 λ×结构场（见 Step 的 8b-1b）。</summary>
		public float PrecipitationClosureFactor { get; private set; } = 1f;

		/// <summary>洋格数（海拔 ≤ 海平面，蒸发域同口径）——λ 的分母来自它。
		/// 整型计数并行精确可结合（线程局部 + 合并），与串行同值。</summary>
		int CountOceanCells()
		{
			float seaLevel = _isostasy.SeaLevel;
			var displacement = _isostasy.Displacement;
			int count = 0;
			ParallelLoops.ForWithLocal(displacement.Length,
				() => 0,
				(i, _, local) => displacement[i] <= seaLevel ? local + 1 : local,
				local => Interlocked.Add(ref count, local));
			return count;
		}

		/// <summary>地幔势温比例（当前/参考）——热沉降与板片驱动力共用的热状态缩放。</summary>
		public float TemperatureScale()
			=> Thermal.PotentialTemperatureK / H3ThermalState.ReferencePotentialTemperatureK;

		/// <summary>某板最后一步的平均角速度（rad/My；板表显示用；无该板 → 零向量）。</summary>
		public Vector3 LastRotationVector(int plate)
		{
			var table = _motion.PlateOmega;
			return table != null && plate >= 0 && plate < table.Length ? table[plate] : Vector3.Zero;
		}

		// ── 速度量测：规定（力平衡解出）与实到（CFL 截断后实际搬运）必须分开报 ──
		/// <summary>板均规定速度（km/My）。</summary>
		public float MeanPrescribedSpeedKmPerMyLastStep { get; private set; }
		/// <summary>板均实到速度（km/My；被"每步 ≤ CflMaxStepCellWidths 格"截断后的实际搬运速度）。</summary>
		public float MeanRealizedSpeedKmPerMyLastStep { get; private set; }

		void MeasureSpeedTiers()
		{
			var table = _motion.PlateSpeedRadPerMy;
			if (table == null) { MeanPrescribedSpeedKmPerMyLastStep = 0f; MeanRealizedSpeedKmPerMyLastStep = 0f; return; }
			float cellWidthKm = MathF.Sqrt(4f * MathF.PI
				* H3PlateMotion.EarthRadiusKm * H3PlateMotion.EarthRadiusKm / _fields.Count);
			double prescribed = 0, realized = 0;
			int count = 0;
			foreach (int p in _motion.PlateIds)
			{
				if (p < 0 || p >= table.Length) continue;
				float kmPerMy = table[p] * H3PlateMotion.EarthRadiusKm;
				prescribed += kmPerMy;
				float cells = MathF.Min(kmPerMy * StepMy / cellWidthKm, H3PlateMotion.CflMaxStepCellWidths);
				realized += cells * cellWidthKm / StepMy;
				count++;
			}
			MeanPrescribedSpeedKmPerMyLastStep = count > 0 ? (float)(prescribed / count) : 0f;
			MeanRealizedSpeedKmPerMyLastStep = count > 0 ? (float)(realized / count) : 0f;
		}

		// 归属清理：把非主体的小同板连通块（< FragmentMinCells 格）并入共享边最多的邻板——
		// 只改标签、不动物质。归属随物质走、永不重算，被平流绕过的残块会终身保留异板色；
		// 任何板的最大连通分量一律保留（整板级碎片由 AbsorbFragments 兜底）。
		/// <summary>本步被并入邻板的碎片格数（判读口）。</summary>
		public int StrayFragmentCellsLastStep { get; private set; }

		void MergeStrayFragments()
		{
			int n = _fields.Count;
			var neighbors = _ball.CellNeighbors;
			var plateId = _fields.PlateId;
			Array.Clear(_topologyVisited, 0, n);
			StrayFragmentCellsLastStep = 0;

			// ① 同板连通分量标号（BFS，只记号+尺寸；格表只对待并碎片单遍补收集）
			int compCount = 0;
			for (int i = 0; i < n; i++)
			{
				if (_topologyVisited[i] || plateId[i] < 0) continue;
				int plate = plateId[i];
				int comp = compCount++;
				if (_compSizeScratch == null || _compSizeScratch.Length < compCount)
				{
					int size = _compSizeScratch?.Length ?? 0;
					Array.Resize(ref _compSizeScratch, Math.Max(compCount, size * 2));
					Array.Resize(ref _compPlateScratch, _compSizeScratch.Length);
					Array.Resize(ref _compMergeMark, _compSizeScratch.Length);
				}
				_topologyComp[i] = comp;
				_topologyVisited[i] = true;
				int stackTop = 0;
				_topologyStack[stackTop++] = i;
				int cellsCount = 1;
				while (stackTop > 0)
				{
					int current = _topologyStack[--stackTop];
					foreach (int nb in neighbors[current])
					{
						if (_topologyVisited[nb] || plateId[nb] != plate) continue;
						_topologyVisited[nb] = true;
						_topologyComp[nb] = comp;
						_topologyStack[stackTop++] = nb;
						cellsCount++;
					}
				}
				_compSizeScratch[comp] = cellsCount;
				_compPlateScratch[comp] = plate;
			}

			// ② 记每板主分量（首个严格最大）；遍历序固定 ⇒ 确定性
			var mainOf = new Dictionary<int, int>();
			_plateOrderScratch.Clear();
			for (int c = 0; c < compCount; c++)
			{
				int plate = _compPlateScratch[c];
				if (mainOf.TryGetValue(plate, out int best))
				{
					if (_compSizeScratch[c] > _compSizeScratch[best]) mainOf[plate] = c;
				}
				else
				{
					mainOf[plate] = c;
					_plateOrderScratch.Add((plate, c));
				}
			}

			// ③ 标记待并分量（非主体且 < FragmentMinCells）→ 单遍收集它们的格表（每份 ≤2 格）
			const int FragmentMinCells = 3;
			_fragmentCells.Clear();
			bool anyFragment = false;
			for (int c = 0; c < compCount; c++)
			{
				bool merge = c != mainOf[_compPlateScratch[c]] && _compSizeScratch[c] < FragmentMinCells;
				_compMergeMark[c] = merge;
				anyFragment |= merge;
			}
			if (anyFragment)
			{
				for (int i = 0; i < n; i++)
				{
					int c = _topologyComp[i];
					if (!_compMergeMark[c]) continue;
					if (!_fragmentCells.TryGetValue(c, out var list))
						_fragmentCells[c] = list = new List<int>(2);
					list.Add(i);
				}
			}

			// ④ 非主体小块并入共享边最多的邻板（板序/分量序固定 ⇒ 确定性）
			foreach (var (plate, _) in _plateOrderScratch)
			{
				int mainComponent = mainOf[plate];
				int plateCells = 0;
				for (int c = 0; c < compCount; c++)
					if (_compPlateScratch[c] == plate) plateCells += _compSizeScratch[c];

				for (int c = 0; c < compCount; c++)
				{
					if (_compPlateScratch[c] != plate || c == mainComponent) continue;
					if (_compSizeScratch[c] >= FragmentMinCells) continue;
					var cells = _fragmentCells[c];
					// 共享边计数：逐格数异板邻边（每条边只数一次，分量内自相邻不计）
					_edgesPerPlate.Clear();
					foreach (int cell in cells)
						foreach (int nb in neighbors[cell])
						{
							int other = plateId[nb];
							if (other == plate || other < 0) continue;
							_edgesPerPlate[other] = _edgesPerPlate.GetValueOrDefault(other) + 1;
						}
					int owner = -1, bestEdges = 0;
					foreach (var pair in _edgesPerPlate)
						if (pair.Value > bestEdges || (pair.Value == bestEdges && pair.Key < owner))
						{ bestEdges = pair.Value; owner = pair.Key; }
					if (owner < 0) continue;                        // 无邻板（理论上不发生：非主分量必邻异板）
					foreach (int cell in cells) plateId[cell] = owner;
					if (plateCells > 0) _motion.TransferSlab(plate, owner, cells.Count / (double)plateCells);
					_advection.ChangeEvents.Add((StepCount, cells[0], plate, owner, 4));
					StrayFragmentCellsLastStep += cells.Count;
				}
			}
		}

		// 屈服流（造山带"先堆后摊"的"摊"）：厚过 MaxCrustThicknessM 的柱，每步把超出量 × YieldFlowRate
		// 按原成分流出——接收三档：①顶死前缘格（造山带内再分配）②同相邻居 ③同相环带 BFS（环 2..1+
		// YieldFlowMaxRings，邻格也吃不下时向区域找余量），全部受接收格帽余量约束、永不改相
		//（陆→陆/洋→洋）。收不下的残余记回地幔（消减账）——帽销毁是造山带唯一的质量出口阀，其中
		// 守恒组份额由 5b 陆壳收支伺服补回（浮力体不该净进地幔）。遍历序/邻居序固定 ⇒ 确定性。
		public double YieldBledMassLastStep { get; private set; }     // 本步从超帽柱流出的总质量
		public double YieldPlacedMassLastStep { get; private set; }   // 其中落到邻居的部分（纯搬运，不进账）
		public double YieldToMantleMassLastStep { get; private set; } // 无处可放、记回地幔的部分（消减账）
		/// <summary>本步溢流回地幔的守恒组份额（陆壳收支账；累计见 <see cref="ConservedYieldToMantleCum"/>）。</summary>
		public double ConservedYieldToMantleLastStep { get; private set; }
		public int YieldCellsLastStep { get; private set; }           // 本步超帽格数

		void ApplyCrustYieldFlow()
		{
			int n = _fields.Count;
			var pools = _fields.AllPools();
			var neighbors = _ball.CellNeighbors;
			var centers = _ball.CellCenters;
			YieldBledMassLastStep = 0; YieldPlacedMassLastStep = 0;
			YieldToMantleMassLastStep = 0; YieldCellsLastStep = 0;
			ConservedYieldToMantleLastStep = 0;
			Array.Clear(_jamFront, 0, n);
			Array.Clear(_yieldThicknessValid, 0, n);       // 厚度缓存按步失效（写穿透，见 YieldThickness）
			bool jamFrontBuilt = false;

			for (int i = 0; i < n; i++)
			{
				float thickness = YieldThickness(i);
				if (thickness <= MaxCrustThicknessM) continue;
				YieldCellsLastStep++;
				if (!jamFrontBuilt)
				{
					foreach (var (jamCell, _) in _advection.JamContacts) _jamFront[jamCell] = true;
					jamFrontBuilt = true;
				}
				float bleed = (thickness - MaxCrustThicknessM) * YieldFlowRate;
				float scale = (thickness - bleed) / thickness;
				double totalMass = 0;
				for (int k = 0; k < 7; k++)
				{
					_yieldRemovedScratch[k] = pools[k][i] * (1f - scale);
					pools[k][i] *= scale;
					totalMass += _yieldRemovedScratch[k];
				}
				_yieldThicknessValid[i] = false;           // 本格柱已缩 ⇒ 缓存失效
				// 流动量按厚度当量记账（接收格的余量是厚度口径；mafic 用年轻洋壳密度，与写入口径同源）
				float totalThickness =
					_yieldRemovedScratch[0] / _material.Sediment
					+ _yieldRemovedScratch[1] / _material.Sedimentary
					+ _yieldRemovedScratch[2] / _material.Metamorphic
					+ _yieldRemovedScratch[3] / _material.FelsicPlutonic
					+ _yieldRemovedScratch[4] / _material.FelsicVolcanic
					+ (_yieldRemovedScratch[5] + _yieldRemovedScratch[6]) / _material.MaficVolcanicMin;
				YieldBledMassLastStep += totalMass;
				if (totalThickness <= 1e-9f) continue;
				bool land = _fields.IsLand(i);

				// 两档接收者按 1/d² 权重降序（同权重按格号）：档①顶死前缘、档②同相邻居
				_yieldNeighbors.Clear();
				foreach (int nb in neighbors[i])
				{
					if (_fields.TotalMass(nb) <= 0f || _fields.IsLand(nb) != land) continue;
					float weight = InverseDistanceSquared(centers, nb, i);
					_yieldNeighbors.Add((_jamFront[nb] ? 0 : 1, -weight, nb));
				}
				_yieldNeighbors.Sort();
				float placedFraction = 0f;
				foreach (var (_, _, nb) in _yieldNeighbors)
				{
					if (placedFraction >= 1f) break;
					float headroom = MathF.Max(MaxCrustThicknessM - YieldThickness(nb), 0f);
					if (headroom <= 0f) continue;
					float take = MathF.Min(totalThickness * (1f - placedFraction), headroom);
					float phi = take / totalThickness;
					for (int k = 0; k < 7; k++) pools[k][nb] += _yieldRemovedScratch[k] * phi;
					_yieldThicknessValid[nb] = false;      // 接收格柱已变 ⇒ 缓存失效
					placedFraction += phi;
				}

				// 档③ 同相环带（环 2..1+YieldFlowMaxRings）：邻格也吃不下时沿同相格 BFS 向外找余量
				//（造山带的"摊"是区域行为，不止一格格宽）。
				if (placedFraction < 1f)
					placedFraction = MathF.Min(1f, placedFraction
						+ PlaceYieldIntoPhaseRings(i, land, totalThickness, 1f - placedFraction));

				double placed = totalMass * placedFraction;
				YieldPlacedMassLastStep += placed;
				double toMantle = totalMass - placed;
				if (toMantle > 0)
				{
					YieldToMantleMassLastStep += toMantle;
					RecycledToMantleLastStep += toMantle;
					// 守恒组份额进陆壳收支账（伺服的补偿对象）：溢流按成分比例，放不下的残余里
					// 长英质/沉积类所占部分 = 被销毁的陆壳物质。帽销毁是造山带唯一的质量出口阀
					//（退回实验 2026-09-19：拆掉它则创建（弧回流+裂谷填充）只进不出，柱失控长高），
					// 浮力体不该进地幔的部分由 5b 步的陆壳收支伺服从弧回流账补回。
					double conservedShare = (_yieldRemovedScratch[0] + _yieldRemovedScratch[1] + _yieldRemovedScratch[2]
						+ _yieldRemovedScratch[3] + _yieldRemovedScratch[4]) * (toMantle / totalMass);
					ConservedYieldToMantleLastStep += conservedShare;
					ConservedYieldToMantleCum += conservedShare;
				}
			}
		}

		// 屈服流档③：同相环带放置。从 source 的同相邻居（环 1 = 档②已试过，只作 visited 种子）逐环
		// BFS 向外，每环按 1/d² 权重降序（同权重格号升序，确定性）受料，受料上限 = 本格帽余量。
		// 返回新放置的厚度份额（≤ remainingFraction）。
		float PlaceYieldIntoPhaseRings(int source, bool land, float totalThickness, float remainingFraction)
		{
			var neighbors = _ball.CellNeighbors;
			var centers = _ball.CellCenters;
			var pools = _fields.AllPools();
			int stamp = ++_yieldVisitedGeneration;         // 代数戳复位（免 O(N) Array.Clear）
			_yieldRing.Clear();
			_yieldNextRing.Clear();
			foreach (int nb in neighbors[source])
			{
				if (_fields.TotalMass(nb) <= 0f || _fields.IsLand(nb) != land) continue;
				_yieldVisitedStamp[nb] = stamp;
				_yieldRing.Add(nb);
			}
			float placedFraction = 0f;
			for (int ring = 0; ring < YieldFlowMaxRings && placedFraction < remainingFraction; ring++)
			{
				// 扩环：当前环的同相未访邻居 = 下一轮候选
				foreach (int cell in _yieldRing)
					foreach (int nb in neighbors[cell])
					{
						if (_yieldVisitedStamp[nb] == stamp || _fields.TotalMass(nb) <= 0f || _fields.IsLand(nb) != land) continue;
						_yieldVisitedStamp[nb] = stamp;
						_yieldNextRing.Add(nb);
					}
				_yieldRing.Clear();
				foreach (int nb in _yieldNextRing) _yieldRing.Add(nb);
				_yieldNextRing.Clear();
				if (_yieldRing.Count == 0) break;

				_yieldRingWeights.Clear();
				foreach (int nb in _yieldRing)
					_yieldRingWeights.Add((-InverseDistanceSquared(centers, nb, source), nb));
				_yieldRingWeights.Sort();
				foreach (var (_, nb) in _yieldRingWeights)
				{
					float headroom = MathF.Max(MaxCrustThicknessM - YieldThickness(nb), 0f);
					if (headroom <= 0f) continue;
					float take = MathF.Min(totalThickness * (remainingFraction - placedFraction), headroom);
					if (take <= 0f) break;
					float phi = take / totalThickness;
					for (int k = 0; k < 7; k++) pools[k][nb] += _yieldRemovedScratch[k] * phi;
					_yieldThicknessValid[nb] = false;          // 接收格柱已变 ⇒ 缓存失效
					placedFraction += phi;
					if (placedFraction >= remainingFraction) break;
				}
			}
			return placedFraction;
		}

		// 权重 = 1/d²（球面弦距平方；重合防御同 H3PlateAdvection 的 InverseDistance）。
		static float InverseDistanceSquared(Vector3[] centers, int from, int to)
		{
			float d2 = (centers[from] - centers[to]).LengthSquared();
			return d2 > 1e-12f ? 1f / d2 : 1e12f;
		}

		// ── 屈服流的写失效式厚度缓存：超帽格数千时，同一格的 Thickness（7 次除法）会被
		//    邻居源/环带 BFS 反复查 —— 未被写入的格直接吃缓存，写入（源柱缩放/接收格增料）
		//    即失效重算。数值 = 现场算同一公式 ⇒ 逐位一致；每步入口整表失效一次。──
		float[] _yieldThicknessCache;
		bool[] _yieldThicknessValid;

		float YieldThickness(int cell)
		{
			if (_yieldThicknessValid[cell]) return _yieldThicknessCache[cell];
			float t = _fields.Thickness(cell, _material);
			_yieldThicknessCache[cell] = t;
			_yieldThicknessValid[cell] = true;
			return t;
		}

		// ═══ 陆壳收支伺服（DefaultRunMy 头注登记的"陆壳收支的稳态器"，2026-09-19 立项落地）═══
		// 陆壳（守恒组）的出口有三户：俯冲埋入层刮削、沉积全额（平流）、帽溢流的守恒组份额（5 步，
		// 帽销毁是造山带唯一的质量出口阀，见 ApplyCrustYieldFlow）；入口只有弧岩浆回流，且基础系数
		//（ArcFelsicReturnFraction）只对冲刮削一户 ⇒ 长跑三户合计悄悄跑赢入口：res1/3000 My 实测
		// 陆占比 36% → 9%（大陆消失、全星海洋+岛屿）。
		// 伺服 = 累计账本上的积分控制器：缺口 = 守恒组销毁累计 − 弧回流累计，每步按各陆格的**帽余量
		// 比例**从地幔补生长英质火山岩（底侵式增生；份额 ∝ 各自余量 ⇒ 天然不越帽）。落在弧上还是
		// 陆内一视同仁——底侵不受弧位置约束（实验：只补弧格时弧格普遍顶帽，容量枯竭、预算闭不上）。
		// 本步容量不足（全球陆壳顶帽）则缺口留在账上滚入下一步（控制器无状态，只认账本）。
		// 不变量：FelsicArcReturnedCum ≥ 守恒组销毁累计 ⇒ 陆壳存量只增不减（长跑不走向水世界的保证）。
		void ApplyContinentalBudgetServo()
		{
			ContinentalServoPlacedLastStep = 0;
			double destroyed = RecycledConservedTotal + ConservedYieldToMantleCum;
			double deficit = destroyed - FelsicArcReturnedCum;
			if (deficit <= 0) return;

			// 单遍缓存逐格帽余量（<0 = 非陆格/无余量）：原两遍各算一次 Thickness（7 次除法/格），
			// 全球陆格 × 2 是本步的常数大头。累加序与逐格数值同原两遍版逐位一致。
			int n = _fields.Count;
			if (_servoHeadroom == null || _servoHeadroom.Length < n) _servoHeadroom = new float[n];
			double capacity = 0;                       // 全球陆壳帽余量（按补料池密度换算的容量）
			for (int i = 0; i < n; i++)
			{
				if (_fields.PlateId[i] < 0 || !_fields.IsLand(i)) { _servoHeadroom[i] = -1f; continue; }
				float headroomM = MaxCrustThicknessM - _fields.Thickness(i, _material);
				if (headroomM > 0f)
				{
					_servoHeadroom[i] = headroomM;
					capacity += headroomM * (double)_material.FelsicVolcanic;
				}
				else _servoHeadroom[i] = -1f;
			}
			if (capacity <= 0) return;                 // 全球陆壳顶帽：缺口滚入下一步

			for (int i = 0; i < n; i++)
			{
				float headroomM = _servoHeadroom[i];
				if (headroomM <= 0f) continue;
				float headroomMass = headroomM * _material.FelsicVolcanic;
				float share = (float)Math.Min(deficit * (headroomMass / capacity), (double)headroomMass);
				if (share <= 0f) continue;
				_fields.FelsicVolcanic[i] += share;
				FelsicArcReturnedCum += share;
				CrustCreatedTotal += share;
				ContinentalServoPlacedLastStep += share;
			}
		}
		float[] _servoHeadroom;                        // 伺服：逐格帽余量缓存（复用；<0 = 本步不参与）

		// ── 板格数表的访问口（代数戳语义：未在本步量测中出现的板 = 0 = 原字典缺键）──
		int PlateCellCount(int plate)
			=> plate >= 0 && plate < _plateCellCountStamp?.Length
				&& _plateCellCountStamp[plate] == _plateCellCountGeneration ? _plateCellCountArr[plate] : 0;

		// 当前活板数（格数 > 0 的板；对应原 Dictionary.Count——Remove 后即不计）。
		int LivePlateCount()
		{
			int count = 0;
			for (int k = 0; k < _platePresentOrder.Count; k++)
				if (PlateCellCount(_platePresentOrder[k]) > 0) count++;
			return count;
		}

		/// <summary>碰撞缝合 / 超大陆裂解 / 重启循环。缝合只搬实际接触该边界的连通分量（不整板合并）；
		/// 裂解 = 继承式三分，物质不动；重启 = 全部重分板、物质场保留。</summary>
		void CycleStep()
		{
			SuturedPairLastStep = null;
			SplitPlateLastStep = -1;

			// 全球板均速（km/My；失速判据）
			double speedSum = 0;
			int plateCount = 0;
			var omega = _motion.PlateOmega;
			for (int k = 0; k < _platePresentOrder.Count; k++)
			{
				int p = _platePresentOrder[k];
				if (PlateCellCount(p) <= 0) continue;          // 已并入他板（原字典 Remove 语义）
				if (omega == null || p >= omega.Length) continue;
				speedSum += omega[p].Length() * H3PlateMotion.EarthRadiusKm;
				plateCount++;
			}
			MeanPlateSpeedKmPerMyLastStep = plateCount > 0 ? (float)(speedSum / plateCount) : 0f;

			// ① 缝合：逐对顶死接触比例 ≥ 阈值持续 N 步 → 小板的接触分量并入大板
			TrySuture();

			// ② 裂解：最大板超阈值且（失速或超硬顶）且不在冷却期
			if (StepCount >= _splitCooldownUntil)
				TrySplit();

			// ③ 周期重启（platec 式四条件 + 本模型超大陆条件）：量测在前、判定在后
			MeasureCycleState();
			TryRestart();

			// ④ 碎片小板吸收：生命周期会留下 1–2 格的碎片板，并入共享边界最多的邻板；保底板数 ≥ 2。
			AbsorbFragments();
		}

		// 碎片小板（< FragmentMinCells 格）并入共享边界最多的邻板。遍历序固定 ⇒ 确定性。
		// 格收集单遍化：一次 O(N) 把全部待并板的格桶齐（原逐板全表扫 = k 个碎片 k×O(N)）；
		// 前面板并入后面待并板时把桶追过去（等价于原版后扫时看到的"已改判格"）。
		void AbsorbFragments()
		{
			const int FragmentMinCells = 3;
			if (LivePlateCount() <= 2) return;
			_absorbFragmentPlates.Clear();
			for (int k = 0; k < _platePresentOrder.Count; k++)
			{
				int plate = _platePresentOrder[k];
				int count = PlateCellCount(plate);
				if (count > 0 && count < FragmentMinCells) _absorbFragmentPlates.Add(plate);
			}
			if (_absorbFragmentPlates.Count == 0) return;
			_absorbFragmentPlates.Sort();

			// 单遍收集（升序 = 原逐板全表扫的逐格处理序；桶复用 _fragmentCells）
			_fragmentCells.Clear();
			var plateId = _fields.PlateId;
			for (int i = 0; i < plateId.Length; i++)
			{
				int p = plateId[i];
				if (!_absorbFragmentPlates.Contains(p)) continue;   // 待并板至多数个，线性扫免哈希
				if (!_fragmentCells.TryGetValue(p, out var list))
					_fragmentCells[p] = list = new List<int>(2);
				list.Add(i);
			}

			foreach (int plate in _absorbFragmentPlates)
			{
				if (LivePlateCount() <= 2) break;
				if (PlateCellCount(plate) <= 0) continue;               // 已被并入别处
				int owner = -1, best = 0;
				foreach (var pair in _pairEdges)
				{
					if (pair.Key.a != plate && pair.Key.b != plate) continue;
					int other = pair.Key.a == plate ? pair.Key.b : pair.Key.a;
					if (PlateCellCount(other) <= 0) continue;
					if (pair.Value > best) { best = pair.Value; owner = other; }
				}
				if (owner < 0 || PlateCellCount(owner) <= 0) continue;
				if (!_fragmentCells.TryGetValue(plate, out var cells)) continue;   // 无格（不发生）
				int moved = cells.Count;
				foreach (int cell in cells) plateId[cell] = owner;
				if (_absorbFragmentPlates.BinarySearch(owner) >= 0 && _fragmentCells.TryGetValue(owner, out var ownerCells))
					ownerCells.AddRange(cells);                         // 并入的也是待并板 ⇒ 桶随走（等价原版后扫所见）
				// 原实现 moved/total 同增同计恒等 1.0 ⇒ 整户划转（语义不变，改为直写）
				_motion.TransferSlab(plate, owner, 1.0);
				_plateCellCountArr[owner] += moved;
				_plateCellCountArr[plate] = 0;                  // Remove 语义（活板 = 格数 > 0）
			}
		}

		// 缝合触发与执行：逐对顶死数累加（不因非起跳步清零——碰撞只在起跳步出现），
		// 累计 ≥ SutureContactFraction × 该对边界边数 × SutureStreakSteps 即触发；
		// 执行只把小板侧实际接触该边界的连通分量改判给大板，物质不动、板片账户按比例划转。
		void TrySuture()
		{
			if (_advection.JamPairCounts.Count == 0) return;
			_firedPairs.Clear();
			foreach (var pair in _advection.JamPairCounts)
			{
				int jam = pair.Value;
				if (jam < SutureMinPairJamCells) continue;
				if (!_pairEdges.TryGetValue(pair.Key, out int edges) || edges <= 0) continue;
				int progress = _sutureStreak.GetValueOrDefault(pair.Key) + jam;
				_sutureStreak[pair.Key] = progress;
				if (progress >= SutureContactFraction * edges * SutureStreakSteps) _firedPairs.Add(pair.Key);
			}

			for (int k = 0; k < _firedPairs.Count; k++)
			{
				var key = _firedPairs[k];
				int small = PlateCellCount(key.a) <= PlateCellCount(key.b)
					? key.a : key.b;
				int big = small == key.a ? key.b : key.a;
				if (SutureComponent(small, big))
				{
					SutureCount++;
					SuturedPairLastStep = (small, big);
				}
				_sutureStreak.Remove(key);
			}
		}

		// 执行缝合：小板侧接触该边界的连通分量改判给大板（惰性 BFS；岛/远端陆块留在原板）。
		// 返回是否有格被搬。
		bool SutureComponent(int small, int big)
		{
			int n = _fields.Count;
			var neighbors = _ball.CellNeighbors;
			var plateId = _fields.PlateId;
			Array.Clear(_topologyVisited, 0, n);
			int smallTotal = 0;
			var component = new List<int>();
			for (int i = 0; i < n; i++)
			{
				if (plateId[i] != small) continue;
				smallTotal++;
				foreach (int nb in neighbors[i])
					if (plateId[nb] == big) { _topologyVisited[i] = true; component.Add(i); break; }
			}
			if (component.Count == 0) return false;
			for (int k = 0; k < component.Count; k++)
				foreach (int nb in neighbors[component[k]])
				{
					if (plateId[nb] != small || _topologyVisited[nb]) continue;
					_topologyVisited[nb] = true;
					component.Add(nb);
				}
			foreach (int cell in component) plateId[cell] = big;
			if (smallTotal > 0) _motion.TransferSlab(small, big, component.Count / (double)smallTotal);
			return true;
		}

		// 裂解触发与执行：最大板占比 > SupercontinentFraction 且（失速 或 > 硬顶）→ 继承式三分。
		// ⚠️ 板数 ≥ 3 才谈"超大陆"——两板世界必有一板 ≥50%（几何必然），那是板块少、不是超大陆；
		// 两板锁死的出路是重启循环，不是裂解。
		void TrySplit()
		{
			if (PlateCount < 3) return;
			int largest = -1, largestCount = 0, totalCells = 0;
			for (int k = 0; k < _platePresentOrder.Count; k++)
			{
				int p = _platePresentOrder[k];
				int count = PlateCellCount(p);
				if (count <= 0) continue;                          // 已并入他板
				totalCells += count;
				if (count > largestCount) { largestCount = count; largest = p; }
			}
			if (largest < 0 || totalCells == 0) return;
			float fraction = (float)largestCount / totalCells;
			bool stalled = MeanPlateSpeedKmPerMyLastStep < SplitStallSpeedKmPerMy;
			if (fraction <= SupercontinentFraction || (!stalled && fraction <= SupercontinentHardFraction)) return;
			if (largestCount < SplitMinCells * 3) return;
			if (PlateCount >= _initialPlateCount * 2) return;      // 板数上限守卫（防裂解爆炸）

			int n = _fields.Count;
			var plateId = _fields.PlateId;
			var cells = new List<int>(largestCount);
			for (int i = 0; i < n; i++) if (plateId[i] == largest) cells.Add(i);

			// 远点采样三种子（确定性：首种子 = 最小格号；逐轮取"距已选种子最小距离"最大者）
			var centers = _ball.CellCenters;
			int s0 = cells[0];
			int s1 = FarthestFrom(cells, centers, s0, -1);
			int s2 = FarthestFrom(cells, centers, s0, s1);

			// 分配 = 多源 BFS 生长：三源同层推进、每格归最近种子的图距离——保证每块新板连通
			// （距离 Voronoi 会切出碎散格）；遍历序固定 ⇒ 确定性。
			int id1 = _nextPlateId++;
			int id2 = _nextPlateId++;
			var region = new int[n];                       // -1 未分配；0/1/2 = 三种子
			for (int i = 0; i < cells.Count; i++) region[cells[i]] = -1;
			region[s0] = 0;
			region[s1] = 1;
			region[s2] = 2;
			var frontier = new Queue<(int cell, int region)>();
			frontier.Enqueue((s0, 0));
			frontier.Enqueue((s1, 1));
			frontier.Enqueue((s2, 2));
			int c0 = 1, c1 = 1, c2 = 1;
			while (frontier.Count > 0)
			{
				var (current, owner) = frontier.Dequeue();
				foreach (int nb in _ball.CellNeighbors[current])
				{
					if (plateId[nb] != largest || region[nb] != -1) continue;
					region[nb] = owner;
					if (owner == 0) c0++;
					else if (owner == 1) c1++;
					else c2++;
					frontier.Enqueue((nb, owner));
				}
			}
			for (int i = 0; i < cells.Count; i++)
			{
				int cell = cells[i];
				if (region[cell] == 1) plateId[cell] = id1;
				else if (region[cell] == 2) plateId[cell] = id2;
			}
			if (c1 < SplitMinCells || c2 < SplitMinCells)
			{
				// 退化（某份太小）：回滚，不裂
				foreach (int cell in cells) plateId[cell] = largest;
				_nextPlateId -= 2;
				return;
			}
			_motion.SplitPlateState(largest, new[] { id1, id2 }, new[] { c1 / (double)cells.Count, c2 / (double)cells.Count });

			// 裂谷带热年龄清零：新旧归属的邻接格 Age = 0 = 新生裂谷洋壳。
			// 否则裂谷两侧是继承母板的老洋壳，板缘通道把子板互相推回 ⇒ 裂完立刻顶死再缝合；
			// 清零后离散由下面的板片踢承担，洋中脊注壳通道随后接手。
			var centersK = centers;
			foreach (int cell in cells)
			{
				int owner = plateId[cell];
				foreach (int nb in _ball.CellNeighbors[cell])
				{
					if (plateId[nb] == owner) continue;
					if (plateId[nb] != largest && plateId[nb] != id1 && plateId[nb] != id2) continue;
					_fields.Age[cell] = 0f;
					break;
				}
			}

			// 板片离散踢：子板各注入背离他种子质心的板片拉力（随板片记忆衰减）；
			// 切向投影保证是球面上可执行的刚体旋转方向（均匀平移场拟合为零旋转）。
			float kick = RiftKickStrength * _material.MaficVolcanicMin * H3Isostasy.OceanReferenceThicknessM;
			Vector3 s0p = centersK[s0], s1p = centersK[s1], s2p = centersK[s2];
			Vector3 Kick(Vector3 self, Vector3 otherA, Vector3 otherB, int plate)
			{
				Vector3 away = self - 0.5f * (otherA + otherB);
				Vector3 radial = self.Normalized();
				Vector3 tangential = away - radial * away.Dot(radial);
				if (tangential.LengthSquared() <= 1e-12f) return Vector3.Zero;
				_motion.AddSlab(plate, kick, tangential);
				return tangential;
			}
			Kick(s0p, s1p, s2p, largest);
			Kick(s1p, s0p, s2p, id1);
			Kick(s2p, s0p, s1p, id2);
			SplitCount++;
			SplitPlateLastStep = largest;
			_splitCooldownUntil = StepCount + SplitCooldownSteps;
		}

		static int FarthestFrom(List<int> cells, Vector3[] centers, int a, int b)
		{
			int best = cells[0];
			float bestDistance = -1f;
			foreach (int cell in cells)
			{
				float distance = MathF.Min(DistanceSquared(centers[cell], centers[a]),
					b < 0 ? float.MaxValue : DistanceSquared(centers[cell], centers[b]));
				if (distance > bestDistance) { bestDistance = distance; best = cell; }
			}
			return best;
		}

		static float DistanceSquared(Vector3 a, Vector3 b) => (a - b).LengthSquared();

		// 重启触发与执行（对齐 platec lithosphere::update/restart）：
		//   ① 板均实到速度 < RestartStallSpeedKmPerMy（连续 N 步）
		//   ② 系统动量/峰值 < RestartEnergyRatio（连续 N 步；峰值不复位）
		//   ③ 距上次陆-陆碰撞 > RestartNoCollisionSteps 步
		//   ④ 周期步数 > RestartCycleSteps
		//   ⑤ 最大板占比 > RestartSupercontinentFraction（本模型自有）
		// 执行 = 全部重分板，物质场与年龄保留（地壳是单份全局场，"保留"天然成立）。
		void TryRestart()
		{
			if (Repartition == null) return;

			bool speedStalled = MeanRealizedSpeedKmPerMyLastStep < RestartStallSpeedKmPerMy;
			bool energyDecayed = _peakMomentum > 0 && _systemMomentumLastStep / _peakMomentum < RestartEnergyRatio;
			bool quiet = RestartNoCollisionSteps > 0 && StepsSinceContinentalCollision > RestartNoCollisionSteps;
			bool cycleLong = CycleStepCount > RestartCycleSteps;
			bool supercontinent = LargestPlateFractionLastStep > RestartSupercontinentFraction;

			// 速度/能量/超大陆条件要连续成立 N 步（抖动门）
			_stallStreak = speedStalled || energyDecayed || supercontinent ? _stallStreak + 1 : 0;
			bool streakSatisfied = _stallStreak >= Math.Max(1, RestartStallSteps);
			if (!((streakSatisfied && (speedStalled || energyDecayed || supercontinent)) || quiet || cycleLong)) return;
			if (MaxRestartCycles > 0 && RestartCount >= MaxRestartCycles) return;      // 周期数上限

			int[] fresh = Repartition(_initialPlateCount);
			if (fresh == null || fresh.Length != _fields.Count) return;
			Array.Copy(fresh, _fields.PlateId, _fields.Count);
			_motion.ResetPlateState();
			_sutureStreak.Clear();
			_nextPlateId = _initialPlateCount;
			_stallStreak = 0;
			_splitCooldownUntil = StepCount + SplitCooldownSteps;
			RestartCount++;
			CycleStepCount = 0;                       // 重启后周期步数归零
			StepsSinceContinentalCollision = 0;
			// 峰值动量不复位（platec 语义：峰值 = 有史以来最活跃时刻）⇒ 跨周期后能量条件越来越难满足。
			LastRestartReason = quiet ? "久无陆碰撞" : cycleLong ? "周期届满"
				: speedStalled ? "总速度过低" : energyDecayed ? "动能衰减" : "超大陆";
		}

		// 周期量测：系统能量 = Σ 板地壳质量 × 规定速度。必须用规定速度——实到速度被
		// "一格一步"上限钉平（全板同值）⇒ 能量恒定 ⇒ 能量条件永不触发；规定速度才是
		// "发动机还剩多少"的读数。峰值只增不清、不随重启复位；陆-陆碰撞计数有碰撞清零、无碰撞自增。
		void MeasureCycleState()
		{
			CycleStepCount++;
			StepsSinceContinentalCollision = _advection.ContinentalJamCellCount > 0
				? 0 : StepsSinceContinentalCollision + 1;

			var speeds = _motion.PlateSpeedRadPerMy;
			var masses = _motion.PlateCrustMass;
			if (speeds == null || masses == null)
			{
				_systemMomentumLastStep = 0;
				MomentumRatioLastStep = _peakMomentum > 0 ? 0f : 1f;
				return;
			}
			double momentum = 0;
			foreach (int p in _motion.PlateIds)
			{
				if (p < 0 || p >= speeds.Length || p >= masses.Length) continue;
				momentum += masses[p] * (speeds[p] * H3PlateMotion.EarthRadiusKm);
			}
			_systemMomentumLastStep = momentum;
			if (momentum > _peakMomentum) _peakMomentum = momentum;
			MomentumRatioLastStep = _peakMomentum > 0 ? (float)(momentum / _peakMomentum) : 1f;
		}

		// ═══════════════════════════════════════════════
		// 归属拓扑量测（判读口，非机制）
		// ═══════════════════════════════════════════════

		/// <summary>量归属场的形状：无主格 / 孤立格 / 连通分量数 / 最大分量占比 / 最大板占比 / 板数。
		/// 回归验收器：归属随物质走后，孤立格 = 0、连通分量 = 板数、强少数格 ≈ 0 应常态成立。
		/// O(N)，复用缓冲不分配。</summary>
		void MeasurePlateTopology()
		{
			int n = _fields.Count;
			var neighbors = _ball.CellNeighbors;
			var plateId = _fields.PlateId;

			Array.Clear(_topologyVisited, 0, n);
			// 板格数表按代数戳翻新（免清零；首现序表重建 = 原字典插入序）
			int generation = ++_plateCellCountGeneration;
			int stampCapacity = _plateCellCountStamp?.Length ?? 0;
			if (stampCapacity == 0)
			{
				_plateCellCountArr = new int[16];
				_plateCellCountStamp = new int[16];
			}
			_platePresentOrder.Clear();

			int ownerless = 0, isolated = 0, localMinority = 0, components = 0, largestComponent = 0, stackTop = 0;
			_pairEdges.Clear();
			_plateBoundaryEdges.Clear();

			// ① 逐格：无主 + 逐板格数 + 逐对边界边数（触发器输入，恒统计）；孤立/强少数（可门控）。
			//    每条边界边两侧各记一次（比例分子分母同尺度）。
			for (int i = 0; i < n; i++)
			{
				int p = plateId[i];
				if (p < 0) { ownerless++; continue; }
				if (p >= _plateCellCountArr.Length)
				{
					int size = Math.Max(p + 1, _plateCellCountArr.Length * 2);
					Array.Resize(ref _plateCellCountArr, size);
					Array.Resize(ref _plateCellCountStamp, size);
				}
				if (_plateCellCountStamp[p] != generation)
				{
					_plateCellCountStamp[p] = generation;
					_plateCellCountArr[p] = 0;
					_platePresentOrder.Add(p);
				}
				_plateCellCountArr[p]++;

				int distinct = 0;
				int ownCount = 0, otherMaxCount = 0;
				{
					for (int k = 0; k < 6; k++) { _neighborPlateScratch[k] = -1; _neighborCountScratch[k] = 0; }
					foreach (int nb in neighbors[i])
					{
						int neighborPlate = plateId[nb];
						if (neighborPlate < 0) continue;
						if (neighborPlate == p) { if (EnableTopologyMeasurement) ownCount++; continue; }
						if (nb > i)                                             // 唯一边计数（每条只记一次）
						{
							var pairKey = (Math.Min(p, neighborPlate), Math.Max(p, neighborPlate));
							_pairEdges[pairKey] = _pairEdges.GetValueOrDefault(pairKey) + 1;
							_plateBoundaryEdges[p] = _plateBoundaryEdges.GetValueOrDefault(p) + 1;
							_plateBoundaryEdges[neighborPlate] = _plateBoundaryEdges.GetValueOrDefault(neighborPlate) + 1;
						}
						if (!EnableTopologyMeasurement) continue;
						int slot = -1;
						for (int k = 0; k < distinct; k++)
							if (_neighborPlateScratch[k] == neighborPlate) { slot = k; break; }
						if (slot < 0) { slot = distinct++; _neighborPlateScratch[slot] = neighborPlate; }
						_neighborCountScratch[slot]++;
					}
					if (EnableTopologyMeasurement)
					{
						for (int k = 0; k < distinct; k++)
							if (_neighborCountScratch[k] > otherMaxCount) otherMaxCount = _neighborCountScratch[k];
						if (ownCount == 0) isolated++;
						if (ownCount < otherMaxCount) localMinority++;
					}
				}
			}

			// ② 同板连通分量（BFS；可门控；种子与邻居遍历顺序固定 ⇒ 确定性）
			if (EnableTopologyMeasurement)
			{
				for (int i = 0; i < n; i++)
				{
					if (plateId[i] < 0 || _topologyVisited[i]) continue;
					int plate = plateId[i];
					components++;
					int size = 0;
					_topologyVisited[i] = true;
					_topologyStack[stackTop++] = i;
					while (stackTop > 0)
					{
						int current = _topologyStack[--stackTop];
						size++;
						foreach (int nb in neighbors[current])
						{
							if (_topologyVisited[nb] || plateId[nb] != plate) continue;
							_topologyVisited[nb] = true;
							_topologyStack[stackTop++] = nb;
						}
					}
					if (size > largestComponent) largestComponent = size;
				}
			}

			int largestPlate = 0;
			for (int k = 0; k < _platePresentOrder.Count; k++)
			{
				int count = _plateCellCountArr[_platePresentOrder[k]];
				if (count > largestPlate) largestPlate = count;
			}

			OwnerlessCellsLastStep = ownerless;
			IsolatedPlateCellsLastStep = isolated;
			LocalMinorityCellsLastStep = localMinority;
			PlateComponentCountLastStep = EnableTopologyMeasurement ? components : -1;
			LargestComponentFractionLastStep = EnableTopologyMeasurement && n > 0 ? (float)largestComponent / n : -1f;
			LargestPlateFractionLastStep = n > 0 ? (float)largestPlate / n : 0f;
			PlateCount = _platePresentOrder.Count;   // 本步量测到的板（首现序表，条目格数恒 ≥ 1）
		}

		// ═══════════════════════════════════════════════
		// 输出：写回 new_HexWorld 渲染/地图模式用的六场 Crust
		// ═══════════════════════════════════════════════

		// ── 构造地形带：终态一次施加（海沟下挖 + 弧火山质量 → 均衡/海平面重算）──
		public H3BoundaryRelief Relief { get; private set; }

		/// <summary>终态施加构造地形带（海沟/弧；调用方先用终态速度场 Build 好 H3PlateBoundary 再来）。
		/// 弧新增质量记入创建账。</summary>
		public H3BoundaryRelief ApplyBoundaryRelief()
		{
			var relief = new H3BoundaryRelief();
			relief.Apply(_ball, _fields, _motion.Velocity, _isostasy, _material);
			CrustCreatedTotal += relief.ArcAddedMass;
			Relief = relief;
			return relief;
		}

		/// <summary>终态水量预留重解海平面：云/河库存（H3WaterCycle 的 ReservedVolumeM，米×格）从总水量
		/// 账本划出后再解——目标水量 = TotalOceanDepth × 洋壳格数 − 预留体积，海+云+河之和恒等于总水量；
		/// 调用后直接重写渲染海拔即可（位移与海平面解耦，无需 rebase）。</summary>
		public void ApplyEndStateWaterReservation(double reservedVolumeM)
		{
			_isostasy.SolveSeaLevelByVolume(reservedVolumeM);
		}

		/// <summary>把动态模拟结果写进 new_HexWorld 的 `Crust`（渲染与地图模式只认这六场）。
		/// FelsicThick/MaficThick = 质量 ÷ 密度（→ 米）；Age = My；
		/// Elevation = 均衡位移 − 海平面（> 0 = 陆）。</summary>
		public void WriteToCrust(Crust target)
		{
			int n = _fields.Count;
			for (int i = 0; i < n; i++)
			{
				target.PlateId[i] = Math.Max(_fields.PlateId[i], 0);
				target.FelsicThick[i] = (_fields.FelsicPlutonic[i] + _fields.FelsicVolcanic[i]) / _material.FelsicPlutonic;
				target.MaficThick[i] = (_fields.MaficVolcanic[i] + _fields.MaficPlutonic[i]) / _material.MaficVolcanicMin;
				target.SedimentThick[i] = _fields.Sediment[i] / _material.Sediment;
				target.Age[i] = _fields.Age[i];
				target.Elevation[i] = _isostasy.Displacement[i] - _isostasy.SeaLevel;
			}
		}

	}
}
