using System;
using System.Collections.Generic;
using Godot;
using World.Tectonics;                 // MaterialDensity
using World.Utils;

namespace World.NewHexWorld.Plate
{
// 动态板块运动编排（设计-03 §2.2 流水线 / §6；v1.4 起**归属随物质走**）。
// 每步（老实现的顺序；侵蚀 2026-09-15 自老项目 M3 移植回来）：
//   1 洋壳老化 → 1b 热状态 → 2 运动学（力平衡速度 + 刚体旋转拟合 + CFL 钳制）→ 3 平流（物质层阻挡：
//   碰撞堆厚 / 俯冲 / 让开；归属 = 顶层物质的板号，内建于搬运）→ 4 裂谷（洋中脊轴注新壳
//   + 空洞并入邻板）→ 5 俯冲/体积记账 → 6 均衡位移 → 7 挠曲 → 8 水量守恒海平面
//   → 8b 地表过程（侵蚀/风化/成岩/变质；`H3SurfaceProcesses`）→（9 缝合 / 10 超大陆裂解）
//
// v1.4 删除的两件东西（"想象的方法不需要归属判断"——用户 2026-09-11）：
//   · H3PlateTerritory（种子旋转加权 Voronoi 领土场）——归属改为物质自身的板号；
//   · H3PlateMotion 的旋转向量接触阻挡——碰撞语义移到物质层（H3PlateAdvection 落地阻挡）。
// v1.8：补回 v1.4 顺手丢掉的"碰撞让板减速"半边——顶死接触格不计净驱动力（力级零功修正，
//   非约束、不复活 v1.3 的板级硬阻挡）；缺口大小看 JammedCells/JamContactCells/PhantomForceFraction。
// v1.9：初始地壳改**板级陆洋属性**（用户拍板）——每板一个陆/洋属性，弃团噪声排名 + hypsography 逐格采样；
//   大陆 = 整板（v1.5 紧凑板的形状直接给出），Age 随板属性走。
// v1.10：初始起伏改**3D 噪声 + fBm 分形**（`SphericalFbmNoise`，纯 C# 零引擎依赖）——v1.9 的"板内 ±250 m
//   均匀随机"是逐格白噪声（相邻格毫不相关 = 屏幕噪点，且与"海拔"这个连续量同名却毫无空间结构）；
//   现改为连续、各向同性的分形场：陆地 ±LandReliefAmplitudeM（山脉/盆地）、洋底
//   ±OceanReliefAmplitudeM（深海丘陵）。**陆洋同一 Airy 反解**（壳厚 = 参考厚 + 起伏/kAiry）= 基准 ± 起伏
//   就是初始海拔；顺带修掉"d 既当海拔又当老山地模板输入"的双重计数（陆均值 +2316 m → +800 m，见 01 §3.3）。
//   板级纯相与陆洋归属口径不变。
// v1.12：初始陆洋改**逐格大陆性场混合**（用户 2026-09-15 拍板"陆壳洋壳分得太死"）——v1.9 的板级纯相
//   把海岸线钉死在板块边界上，世界格局随板数定型。现改为：大陆斑块（团噪声，`NumContinents` 回归
//   "大陆块数"本义）+ fBm 海岸细节，与板属性按 `LandOceanNoiseBlend` 混合；海洋占比由 OceanFraction
//   的分位数阈值逐格钉住。blend=0 严格退回 v1.9 板级纯相。
// v1.13：**侵蚀/沉积回归**（2026-09-15 自旧项目 TectonicsSimulation.Erosion / 老 Crust.Model* 逐行移植，
//   原版 = Crust.js model_*）——`H3SurfaceProcesses` 四步：侵蚀（沿边下坡搬运守恒组）/ 风化（基岩→沉积物）/
//   成岩（>2.2 MPa）/ 变质（>300 MPa）；delta 制、组内守恒、零审计记账。流水线插 8b（均衡+海平面之后，
//   侵蚀后重算位移与海平面——老实现同款两遍）。
// v1.14：**俯冲再循环**（2026-09-15 用户拍板方案②，陆增棘轮对冲）——埋入层守恒组按比例随俯冲回地幔
//   （沉积类全额 + 长英质类刮削比例，`RecycleSedimentFraction`/`RecycleFelsicFraction`），只作用于
//   真混合格；质量并入既有消减账（RecycledToMantle），判读口 = `H3PlateAdvection.RecycledConservedMass`。
//   动机：长英质"只进不出"（俯冲只回收 mafic + 厚度帽溢流变性成长英质）⇒ 陆占比 600 My 棘轮到 95%+。
// v1.15：**洋底扩张**（2026-09-15 用户拍板方案①，威尔逊旋回引擎）——离散边界上的空洞直接注 age=0
//   脊轴新洋壳（`EnableSpreading`/`SpreadingSpeedKmPerMy`，判读口 = SpreadingFilledCount）：
//   洋中脊持续造洋更新洋底年龄剖面、陆内裂谷长出窄洋稀释陆占比。v1.12 治乱的"假浅滩"顾虑已随
//   海平面分母修正消失（0 龄壳 −2500 m 在海平面 −2.2 km 下位于水下）。
// 与老实现的结构差异（03 §3）：没有"每板一份网格副本 + Voronoi 重采样"，也没有老实现每步 3 次
// MergePlatesToMaster —— 在老模型里"合并"是把 N 份局部场收敛成一份全局场的手续；在 H3 原生模型里
// 平流本身就是全局场对全局场，合并语义（守恒组相加 / 顶层裁决）内建在 H3PlateAdvection 的单遍里。
	public sealed class H3DynamicTectonics
	{
		// ── 参数表（03 §8 初值；判读后可调）──
		public int Seed;
		public float StepMy = 4f;                    // 时间步（My）
		public float RunMy = 600f;                   // 总时长（My）
		public int NumContinents = 6;                // 大陆块数（v1.12 起 = 初始陆洋斑块的团心数，旋钮回归本义；洋壳年龄梯度场仍 ×2 倍频复用）
		public float OceanFraction = 0.6f;           // 海洋占比旋钮：blend>0 = 逐格分位数精确；blend=0 = 陆板数 round(板数×(1−此值)) 板粒度近似（v1.9 口径）
		public float OceanScale = 1f;                // 水量系数（× H3Isostasy.BaseMeanOceanDepthM 3700 m 基准平均海洋深度）
		public bool EnableRifting = true;            // 裂谷（空洞填新洋壳）
		/// <summary>地表过程开关（8b：侵蚀/风化/成岩/变质，2026-09-15 自老项目 M3 移植）。
		/// 关掉即回到"质量只进不出"的旧口径（造山单调饱和到帽高）。</summary>
		public bool EnableErosion = true;
		/// <summary>侵蚀/风化强度倍率（老 TectonicsSimulation.ErosionScale 口径：0.5 温和 ~ 2 剧烈）。</summary>
		public float ErosionScale = 1f;
		/// <summary>洋底扩张开关（v1.15 陆增棘轮的结构性对冲，2026-09-15 用户拍板方案①）：离散边界上
		/// 的空洞直接注 age=0 脊轴新洋壳——洋中脊持续造洋、陆内裂谷长出窄洋（威尔逊旋回引擎）。
		/// 关掉即回到"空洞一律多数相加权平均"的 v1.12 治乱口径。</summary>
		public bool EnableSpreading = true;
		/// <summary>离散发育阈值（km/My）：空洞处两板相对速度的法向分离分量超过它才注新洋壳。
		/// 真实慢速洋脊半速率 ~0.05 km/My 量级；调高 = 只有撕得更快的边界才出洋。</summary>
		public float SpreadingSpeedKmPerMy = 0.05f;
		/// <summary>俯冲再循环（v1.14 陆增棘轮对冲，2026-09-15 用户拍板方案②）：埋入层**沉积类**
		/// （sediment/sedimentary）随俯冲回地幔的比例。1 = 全额——侵蚀搬进海沟的沉积物随板回收，
		/// 地球沉积物再循环主通道；0 = 老口径（守恒组永远埋进上盘，陆化只进不出）。只作用于真混合格
		/// （有上盘顶层）；全埋入格整柱保留。</summary>
		public float RecycleSedimentFraction = 1f;
		/// <summary>俯冲再循环：埋入层**长英质类**（metamorphic/felsic×2）的刮削比例。只作用于被
		/// 俯冲板驮着的薄层长英质（陆源尘）；陆壳本体浮力大、罕被俯冲（密度裁决天然豁免），
		/// 所以碰不塌大陆。0 = 老口径。</summary>
		public float RecycleFelsicFraction = 0.2f;
		/// <summary>地壳厚度上限（超出部分按体积记账进增生楔，改进 4）。**它直接决定碰撞造山的高原面高度**：
		/// 陆格海拔 = `LandBaselineM` + kAiry×(厚 − `LandReferenceThicknessM`) ⇒ 帽 65 km → 高原面 ≈ +5.4 km
		/// （= 02 §5.2 用户拍板的设计峰值 +5.3 km / 峰值壳厚 60 km；知识库碰撞带 60–75 km 落在区间内）。
			/// ⚠️ v1.11 由 70000 降到 65000：当时本路线**没有侵蚀**（03 §1 明确不做），碰撞带质量只进不出 ⇒
			/// 造山必然**单调饱和到帽高**（实测 304 My：陆海拔 p90 = 6.0 km、13.5% 陆地 >3 km、12.3% >5 km，
			/// 即"整片高原"而非几座高峰）。⚠️ v1.13 起侵蚀/沉积已移植回来（8b 地表过程）——峰体被剥蚀，
			/// 帽从"唯一高度旋钮"退回"碰撞堆厚的安全上限"。</summary>
		public float MaxCrustThicknessM = 65000f;
		public float AccretionFractionToFelsic = 0.85f;  // 增生楔质量分配（felsic 深成 : 火山 = 85 : 15）
		/// <summary>归属拓扑量测门控（04 批次 1，默认开）：false 时跳过孤立/强少数/连通分量 BFS
		/// 与逐格邻居板分类（省 O(N·6)+BFS，res5 判读用）；无主格与逐板格数/板数/逐对边界边数
		/// 仍每步统计（质量守恒与缝合/裂解/重启触发器依赖）。</summary>
		public bool EnableTopologyMeasurement = true;

		// ── 板块生命周期旋钮（04 批次 4；缝合/裂解/重启循环，借鉴 platec P1/P2/P4 + testproject T7）──
		/// <summary>缝合触发：板对顶死接触数 / 该对边界边数 ≥ 此比例（platec aggr_overlap_rel 口径）。</summary>
		public float SutureContactFraction = 1f / 3f;
		/// <summary>缝合触发：上述比例持续此步数（防瞬时接触误并）。</summary>
		public int SutureStreakSteps = 5;
		/// <summary>缝合资格的最低顶死格数（噪声门：零星接触不算持续碰撞）。</summary>
		public int SutureMinPairJamCells = 2;
		/// <summary>裂解触发：最大板占比超过此值且（失速 或 超过硬顶）。</summary>
		public float SupercontinentFraction = 0.25f;
		/// <summary>裂解硬顶：最大板占比超过此值直接触发（不论速度）。</summary>
		public float SupercontinentHardFraction = 0.35f;
		/// <summary>裂解失速阈值（km/My = 0.1 cm/yr）：全球板均速低于此值视为失速。</summary>
		public float SplitStallSpeedKmPerMy = 0.01f;
		/// <summary>裂解冷却步数（防振荡：刚裂完不许再裂）。</summary>
		public int SplitCooldownSteps = 10;
		/// <summary>裂解最小份额格数（每份至少这么多格才裂）。</summary>
		public int SplitMinCells = 3;
		/// <summary>重启触发①（"世界冻住了"的兜底）：全球板均**实到**速度持续低于此值
		/// （km/My = 0.25 cm/yr）⇒ 全部重分板（场保留）。口径用**实到**速度 = 物质真的不动了；
		/// "发动机还剩多少"那一维由 <see cref="RestartEnergyRatio"/> 管（规定速度口径）。</summary>
		public float RestartStallSpeedKmPerMy = 0.025f;
		/// <summary>重启的超大陆占比阈值。</summary>
		public float RestartSupercontinentFraction = 0.5f;
		/// <summary>速度/能量条件要求的**连续成立步数**（抖动门）。platec 无此要求（瞬时成立即重启）——
		/// 本模型的步长与速度尺度与 platec 不同、瞬时抖动会误触发，故加门（差异登记在 05 §6.3）。</summary>
		public int RestartStallSteps = 8;

		// ── platec 式周期重启参数（05 §6.3 对齐 Mindwerks/plate-tectonics src/lithosphere.cpp）──
		/// <summary>触发②（**主触发**，用户 2026-09-15 拍板："当能量耗得差不多了重启"）：系统能量 / 历史**峰值**
		/// 低于此比值 ⇒ 发动机耗尽 ⇒ 重启（platec `RESTART_ENERGY_RATIO = 0.15`；峰值 `peak_Ek` 同样
		/// **不随重启复位**——platec 语义如此，故跨周期后该条件越来越难满足，这符合"有史以来最活跃时刻"的原意）。
		/// 能量口径 = 板质量 × **规定速度**（见 MeasureCycleState）。</summary>
		public float RestartEnergyRatio = 0.15f;
		/// <summary>触发③：连续这么多步没有陆-陆碰撞 ⇒ 重启（platec `NO_COLLISION_TIME_LIMIT = 10` 迭代）。
		/// ⚠️ **默认关闭（0 = 关）**：platec 的 10 迭代 ≈ 10 My，而本模型一步 = 4 My ⇒ 照搬会在**第 11 步
		/// （44 My）就重启**，那时初始陆块还没碰撞（实测 `距陆碰撞=26 步` 一路涨到第 26 步）——是误触发。
		/// 用户 2026-09-15 定的语义是"能量耗尽才重启"，故本条件退居旋钮（要贴 platec 的"无聊就重启"再打开）。</summary>
		public int RestartNoCollisionSteps;
		/// <summary>触发④：一个**周期**最多这么多步（platec `RESTART_ITERATIONS = 600` 迭代 ≈ 600 My；
		/// 本模型一步 4 My ⇒ 150 步 = 600 My = **恰好一个周期**）。</summary>
		public int RestartCycleSteps = 150;
		/// <summary>周期数上限（platec `num_cycles`/`max_cycles`；**0 = 无限**，同 platec 的 ETERNITY 语义）。</summary>
		public int MaxRestartCycles;

		/// <summary>重分板委托（04 批次 4 重启循环）：参数 = 目标板数，返回逐格新归属（0..P-1）。
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

		// ── 初始地壳（v1.12 逐格陆洋混合；v1.10 初始起伏改 3D 噪声 + fBm）──
		// 陆/洋**基准海拔**不在这里另立旋钮：陆自由板高度 = `H3Isostasy.LandBaselineM`(800 m)、
		// 洋底基准 = `H3Isostasy` 的热沉降律（两者都是均衡侧的既有常量，本类只写厚度）。
		/// <summary>初始陆洋**混合系数**（v1.12；用户 2026-09-15 拍板"陆壳洋壳分得太死"）：
		/// 0 = 纯板属性（整板陆/整板洋，v1.9 口径，海岸线 = 板块边界）；1 = 纯噪声斑块
		/// （陆洋与板块完全解耦）；中间值 = 板块给大势、噪声撕海岸线（混生板/离岸岛链）。
		/// 海洋占比仍由 <see cref="OceanFraction"/> 钉住（大陆性场的分位数阈值），本旋钮只改形态。</summary>
		public float LandOceanNoiseBlend = 0.7f;
		/// <summary>大陆斑块上海岸细节 fBm 的基波长（km）：撕出锯齿海岸与岛链的尺度。</summary>
		public float CoastDetailWavelengthKm = 1600f;
		/// <summary>海岸细节的相对幅度（× fBm 值域 [−1,1]）：越大海岸线越碎、离岸小岛越多。</summary>
		public float CoastDetailAmplitude = 0.6f;
		/// <summary>陆地初始起伏半幅（m）——fBm 场的标称幅度。陆海拔 = `H3Isostasy.LandBaselineM` ± 起伏。
		/// ⚠️ **上限 ≈ `LandBaselineM`(800) / 1.45 ≈ 550 m**（1.45 = fBm 各倍频峰值叠加的硬上界）：
		/// 再大陆格就会掉到海平面以下——本模型没有"被淹没的陆地"这一维（海平面求解假定陆格恒高于海平面，
		/// 02 §11A 已登记该空洞，须等水位/水量守恒立项）。实测极值比 ≈ ±0.8~1.0（res3/seed42，10 469 陆格）。
		/// 想要更强的陆起伏 ⇒ 抬高 `H3Isostasy.LandBaselineM`（自由板高度换起伏幅度），或补上水位那一维。</summary>
		public float LandReliefAmplitudeM = 550f;
		/// <summary>洋底初始起伏半幅（m）——经 Airy 反解成洋壳厚度起伏（±幅度 / kAiry ≈ ±2.3 km 厚），
		/// 洋壳厚度保持 4.8–9.4 km 量级（真实洋底起伏 100–500 m）。</summary>
		public float OceanReliefAmplitudeM = 350f;
		/// <summary>初始起伏的 fBm 基波长（km）= 最大一层特征尺寸（大陆级 2400 km；5 层倍频最细一层 150 km）。</summary>
		public float ReliefBaseWavelengthKm = 2400f;
		/// <summary>初始起伏的 fBm 倍频层数（越多细节越碎；振幅每层折半 ⇒ 5 层已含 97% 能量）。</summary>
		public int ReliefOctaves = 5;

	readonly Ball _ball;
	readonly MaterialDensity _material;
	readonly H3PlateMotion _motion;
	readonly H3PlateAdvection _advection;
	readonly H3Isostasy _isostasy;
	readonly H3SurfaceProcesses _surface;    // 地表过程（侵蚀/风化/成岩/变质；8b）
	H3PlateFields _fields;
		H3PlateFields _scratch;
		readonly bool[] _topologyVisited;               // 连通分量 BFS 用（复用，避免每步分配）
		readonly int[] _topologyStack;
		readonly int[] _neighborPlateScratch = new int[6];    // 逐格邻居按板分类（定长，不分配）
		readonly int[] _neighborCountScratch = new int[6];
		readonly HashSet<int> _plateSeen = new HashSet<int>();
		readonly Dictionary<int, int> _plateCellCounts = new Dictionary<int, int>();
		readonly Dictionary<(int a, int b), int> _pairEdges = new();            // 逐对边界边数（两侧各一，缝合触发器）
		readonly Dictionary<int, int> _plateBoundaryEdges = new();              // 逐板边界边数（两侧各一）
		readonly Dictionary<(int a, int b), int> _sutureStreak = new();         // 缝合资格连击步数
		readonly Dictionary<int, int> _edgesPerPlate = new();                   // 碎片并入的共享边计数（复用，不每步分配）

		// ── 输出（供渲染/诊断/后续批次）──
		public H3PlateFields Fields => _fields;
		public H3PlateMotion Motion => _motion;
		public H3PlateAdvection Advection => _advection;   // 平流本体（诊断/调试取数口）
		public H3SurfaceProcesses Surface => _surface;     // 地表过程（侵蚀判读口；诊断/出图）
		/// <summary>地幔热状态（设计-05）：势温 Tp(t) + 热收支诊断；每步第 1b 步推进，
		/// 黏度经 Arrhenius 注入 `_motion`（"发动机缓慢熄火"的源项）。</summary>
		public H3ThermalState Thermal { get; private set; } = new H3ThermalState();
		public float[] Displacement => _isostasy.Displacement;
		public float SeaLevel => _isostasy.SeaLevel;
		public int StepCount { get; private set; }
		public int PlateCount { get; private set; }

		// ── 质量守恒对账（04 批次 0；03 §3.2 提出的"每步校验守恒组总量"落成口）──
		// 不变量：TotalCrustMass() ≈ InitialCrustMass + CrustCreatedTotal − CrustDestroyedTotal。
		// 进出仅有的三户：裂谷/兜底注壳与陆内重采样（创建，H3PlateAdvection 记账）、
		// 俯冲+顶层裁决回地幔（消减）、厚度帽溢流缩掉（消减；后续批次若把增生楔堆回上盘，
		// 堆回部分计创建，净额自动归零）。
		public double InitialCrustMass { get; private set; }
		public double CrustCreatedTotal { get; private set; }
		public double CrustDestroyedTotal { get; private set; }

		/// <summary>重定审计基线（测试手工改场后调用：以当前总量为"初始"——审计口径防基线失真）。</summary>
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
		public IReadOnlyDictionary<int, int> JamCellsPerPlateLastStep => _advection.JamCellsPerPlate;
		public int JamContactCellsLastStep { get; private set; }       // 运动学预测：判顶死接触剔除出净驱动的板缘格数（v1.8）
		public float PhantomForceFractionLastStep { get; private set; }// 幻影驱动力占比 = 被剔除 F / 毛 F（0 = 无顶死接触）

		/// <summary>换色事件日志（诊断/溯源）：每格归属变更 = (步号, 格, 原板, 新板, 路径)。
		/// kind：0 = arrival 翻色，1 = 空洞填新壳（兜底：被异板完全包围），2 = 陆内重采样（复制陆邻居），
		/// 3 = 海底连续填充（复制洋邻居；04 批次 6 起洋空洞的正常路径）。</summary>
		public List<(int step, int cell, int from, int to, int kind)> ChangeEvents => _advection.ChangeEvents;
		public double AccretionMassLastStep { get; private set; }
		public int AccretionCellsLastStep { get; private set; }
		public int RiftInjectedCellsLastStep { get; private set; }
		public float LandFractionLastStep { get; private set; }

		// ── 归属拓扑量测（判读口，非机制；MeasurePlateTopology 每步更新）──
		//  用户 2026-09-11 报"有些格子嵌入别的板块里面"：这是**形状**问题，
		//  必须先把形状量化，才能判读归属规则的改动是否对症。
		public int OwnerlessCellsLastStep { get; private set; }        // 无主格（PlateId = -1）
		public int IsolatedPlateCellsLastStep { get; private set; }    // 孤立格：邻居里一个同板都没有
		public int LocalMinorityCellsLastStep { get; private set; }    // 强少数格：本板邻居数 < 任一他板邻居数（= 肉眼看到的"嵌在别板里的毛刺"）
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
			_topologyVisited = new bool[n];
			_topologyStack = new int[n];
		}

		// ═══════════════════════════════════════════════
		// 初始化：初始地壳（老 Init 的 hypsography 口径，H3 原生）+ 初始分板（外部给定）
		// ═══════════════════════════════════════════════

		/// <summary>初始化：初始地壳（v1.9 板级陆洋属性：陆板/洋板 + 板内小海拔随机）+ 初始分板。
		/// 归属只在初始分板定这一次（`plateOfCell` 写进物质场），此后 `PlateId` 随物质搬运
		/// （H3PlateAdvection），不再有独立的领土/归属场。</summary>
		public void Initialize(int[] plateOfCell, int seed)
		{
			Seed = seed;
			StepCount = 0;
			CrustCreatedTotal = 0;
			CrustDestroyedTotal = 0;
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
			_isostasy.TotalOceanDepth = H3Isostasy.BaseMeanOceanDepthM * OceanScale;
			_isostasy.ComputeDisplacement(_ball, _fields, _material);
			// 地幔热状态复位（t = 0 = 地球现值：Tp 1623 K、η = 1.57e20 ⇒ 初态与引入热状态前逐位一致）
			Thermal.Reset();
			_motion.MantleViscosityPaS = Thermal.MantleViscosityPaS;
		}

		// 初始地壳（**v1.12 逐格陆洋混合** + **v1.10 fBm 初始起伏**）：
		// 陆/洋不再整板二值（v1.9 的板级纯相把海岸线钉死在板块边界上——用户 2026-09-15 拍板
		// "陆壳洋壳分得太死"）。改为**逐格大陆性场**：低频大陆斑块（`SphericalBlobNoise`，
		// 团心数 = `NumContinents`，"大陆块数"旋钮回归本义）+ fBm 海岸细节（撕出锯齿海岸/岛链），
		// 再与板属性倾向按 `LandOceanNoiseBlend` 混合——0 = 纯板属性（v1.9 口径，海岸线=板块边界），
		// 1 = 纯噪声（陆洋与板块完全解耦），中间值 = 板块给大势、噪声改海岸。
		// 海洋占比由 `OceanFraction` 钉住：取大陆性场的**分位数**当阈值（最低 OceanFraction 比例的格
		// 为洋）——混合系数只改空间形态，不改海陆配比。物质模板不变：陆格全 felsic / 洋格全 mafic
		// （Airy 反解：**起伏一律写厚度、海拔由均衡反解派生**，"基准 ± 起伏"就是初始海拔本身），
		// 沉积/年龄随逐格陆洋走。行星标度（sqrt(R)）本批不做（H3 世界半径由渲染层定，01 §3）。
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
			for (int k = plateIds.Count - 1; k > 0; k--)       // Fisher–Yates（邻居序固定 ⇒ 确定性）
			{
				int j = (int)(rng.NextDouble() * (k + 1));
				(plateIds[k], plateIds[j]) = (plateIds[j], plateIds[k]);
			}
			int landPlateCount = Math.Clamp(
				(int)MathF.Round(plateIds.Count * (1f - OceanFraction)), 0, plateIds.Count);
			var landPlates = new HashSet<int>();
			for (int k = 0; k < landPlateCount; k++) landPlates.Add(plateIds[k]);

			// ② 逐格大陆性场（v1.12）：大陆斑块 + 海岸细节，按混合系数并入板倾向；分位数阈值
			//    钉住海洋占比（逐格精确；float 连续场几乎无并列，量化误差 ≤ 1 格）。blend = 0
			//    时不建场，陆洋完全由板属性定（严格回到 v1.9 口径，也免掉 O(N log N) 排序）。
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

			var ageNoise = new SphericalBlobNoise(seed + 100, NumContinents * 2);   // 洋壳年龄梯度场（纯 C#，零引擎依赖）
			// 初始起伏场（3D 噪声 + fBm）：陆/洋共用同一张分形场、各自缩放（幅度差 = 大陆起伏 vs 洋底丘陵）
			var reliefNoise = new SphericalFbmNoise(seed + 313, ReliefBaseWavelengthKm, ReliefOctaves);
			float airy = H3Isostasy.AiryFactor(_material);

			// ③ 物质模板（质量面密度 kg/m²；数值 = ρ × 厚度，与老 Init 模板一致）
			for (int i = 0; i < n; i++)
			{
				int plate = plateOfCell != null && i < plateOfCell.Length ? plateOfCell[i] : 0;
				bool land = continentality != null ? continentality[i] > landCut : landPlates.Contains(plate);
				Vector3 p = centers[i];
				float relief = reliefNoise.Sample(p) * (land ? LandReliefAmplitudeM : OceanReliefAmplitudeM);
				plates.ClearCell(i);
				if (land)
				{
					// 陆壳：参考厚 + 起伏 / kAiry（**Airy 反解**，与洋壳同一式）——于是 fBm 场就是初始海拔本身：
					// 渲染海拔 = `H3Isostasy.LandBaselineM`(800) ± 起伏（自由板高度由均衡侧给，本类不另立旋钮）。
					// ⚠️ 旧写法（v1.10 前）把 d 同时当"海拔"与"老 Init 山地模板曲线的输入"：那条曲线在
					// d = 797 处已经编码了 36.7 km 陆壳（比均衡参考厚 28.3 km 厚 8.4 km），经 Airy 又多抬
					// +1.3 km，叠上 800 m 基准 ⇒ 陆均值 **+2316 m**（01 §3.3 的目标是 ~+800 m，实测偏 2.9 倍）。
					// 反解后基准即海拔：fBm 幅度 = 陆高度的真实半幅。
					// 质量用**密度表**的 ρ_felsic 写（旧模板写死 2700、而 Thickness 用表值 2600 ⇒ 全陆壳厚度
					// 虚高 3.85% = 再抬 168 m；写与读同一个密度，厚度才精确往返）。
					float landThickness = H3Isostasy.LandReferenceThicknessM + relief / airy;
					plates.FelsicPlutonic[i] = _material.FelsicPlutonic * 0.85f * landThickness;
					plates.FelsicVolcanic[i] = _material.FelsicPlutonic * 0.15f * landThickness;
				}
				else
				{
					// 洋壳：参考厚 + 起伏 / kAiry（**Airy 反解**）——fBm 的洋底起伏（±OceanReliefAmplitudeM 米）
					// 经"厚度 → 均衡位移"原样 1:1 还原为洋底起伏（增量项见 H3Isostasy.ComputeRaw）。
					plates.MaficVolcanic[i] = _material.MaficVolcanicMin * (H3Isostasy.OceanReferenceThicknessM + relief / airy);
				}
				// 沉积盖层（老 Init 模板口径）：陆格恒有薄沉积（12500 kg/m² ≈ 8 m 厚），洋格 0
				plates.Sediment[i] = land ? 12500f : 0f;
				// 年龄随板属性走（不随海拔）：陆壳 1000 My；洋壳 0–200 My 低频噪声梯度（密度链初始梯度）
				plates.Age[i] = land ? 1000f : Math.Clamp(ageNoise.Sample(p), 0f, 1f) * 200f;
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


		/// <summary>走一个时间步（流水线 1–8b；缝合/裂解见下方 TODO）。</summary>
		public void Step()
		{
			// 1 洋壳老化（陆壳年龄不变——陆壳不是热沉降链的输入）
			for (int i = 0; i < _fields.Count; i++)
				if (!_fields.IsLand(i)) _fields.Age[i] += StepMy;

			// 1b 地幔热状态（设计-05）：势温按热收支下降 → Arrhenius 黏度注入运动学。
			//    "发动机缓慢熄火"的源项在这里；本步力平衡用的就是本步更新后的 η。
			Thermal.Step(_fields, StepMy);
			_motion.MantleViscosityPaS = Thermal.MantleViscosityPaS;
			_motion.PotentialTemperatureK = Thermal.PotentialTemperatureK;

			// 2 运动学（浮力 → 力平衡速度（顶死接触格不计净驱动，v1.8 零功修正）→ 每板旋转增量 + CFL 钳制）
			_motion.Step(_ball, _fields, _material, 9.8f, StepMy);
			ClampedCellsLastStep = _motion.ClampedCellCount;
			JamContactCellsLastStep = _motion.JamContactCellCount;
			PhantomForceFractionLastStep = _motion.PhantomForceFraction;
			MeasureSpeedTiers();

			// 3 平流（搬**物质**：累积旋转精确落格；一格多源 = 汇聚堆叠/俯冲，一格无源 = 离散；
			//    归属 = 顶层物质的板号）+ 板片账户入账（04 批次 2：俯冲质量×流向记给来料板，
			//    下步力平衡把"已俯冲的板片"算成持续拉力）
			//    v1.14：俯冲再循环随板推入——埋入层沉积/长英质按比例回地幔（陆增棘轮对冲）。
			_advection.RecycleSedimentFraction = RecycleSedimentFraction;
			_advection.RecycleFelsicFraction = RecycleFelsicFraction;
			_advection.Step(_ball, _fields, _motion, _scratch, _material, StepCount);
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

			// 4 裂谷：离散链尾空洞恒等填充（洋链尾 = 本板新洋壳 = 洋中脊；陆链尾 = 重采样接续）。
			// v1.15 洋底扩张（方案①）：正在撕开的板块边界上的空洞改注 age=0 脊轴新洋壳——
			// 洋中脊持续造洋、陆内裂谷长出窄洋（威尔逊旋回引擎，陆占比的结构性稀释通道）。
			_advection.EnableSpreading = EnableSpreading;
			_advection.SpreadingSpeedKmPerMy = SpreadingSpeedKmPerMy;
			if (EnableRifting)
				_advection.FillHolesWithNewOceanicCrust(_ball, _fields, _motion, 2890f * 7100f);
			CrustCreatedTotal += _advection.CrustCreatedMass;
			CrustDestroyedTotal += RecycledToMantleLastStep;

			// 4b 归属一致性清理（04 批次 6 治乱）：非主体的小连通块并入包围板（只改标签、不动物质）。
			// "有些格子嵌在别的板块里面"（用户 2026-09-11 报告）在动态路线下不是形状问题而是**碎片**：
			// 被后来者的平流绕过去的单格/两格残块，终身带异板色 ⇒ 板块图层与描边层上全是毛刺。
			MergeStrayFragments();

			// 5 俯冲体积记账（改进 4）：超出厚度上限的部分不是"按比例抹掉"，而是**进增生楔**
			//   （felsic 85/15）堆到上盘；被埋板的 mafic 已在平流的顶层裁决里记账为"回地幔"。
			ApplyThicknessCapToAccretion();
			CrustDestroyedTotal += AccretionMassLastStep;
			CrustCreatedTotal += AccretionDepositedLastStep;   // 堆回前缘的部分 = 创建（审计净额守恒）

			// 6–7 均衡位移 + 挠曲
			_isostasy.ComputeDisplacement(_ball, _fields, _material);
			_isostasy.ApplyFlexure(_ball);

			// 8 水量守恒海平面：挠曲改变洋底体积后重解 ⇒ 陆格位移须跟到新海平面（否则渲染海拔整体偏掉
			//   这个差值——2026-09-14 实测单步 ~118 m；见 H3Isostasy.RebaseLandToSeaLevel）
			float previousSeaLevel = _isostasy.SeaLevel;
			_isostasy.SolveSeaLevelByVolume();
			_isostasy.RebaseLandToSeaLevel(_ball, _fields, previousSeaLevel);
			LandFractionLastStep = _isostasy.LandFraction();

			// 8b 地表过程（2026-09-15 自老项目 M3 移植，插回老实现流水线原位——均衡+海平面之后）：
			//    侵蚀沿边削高填低 + 风化/成岩/变质（搬运与转化全在守恒组 5 场内部 → 组内质量守恒，
			//    动质量审计零记账）。侵蚀改变载荷 ⇒ 位移/海平面重算一遍（老实现同款"侵蚀后再跑
			//    均衡+水量守恒"；挠曲不重跑，下一步 6-7 自动跟上）。
			if (EnableErosion)
			{
				var surfaceHeight = _surface.ComputeSurfaceHeight(_isostasy);
				_surface.Apply(_fields, surfaceHeight, _material, StepMy, ErosionScale);
				float seaLevelBeforeRecompute = _isostasy.SeaLevel;
				_isostasy.ComputeDisplacement(_ball, _fields, _material);   // 内含 SolveSeaLevelByVolume
				_isostasy.RebaseLandToSeaLevel(_ball, _fields, seaLevelBeforeRecompute);
				LandFractionLastStep = _isostasy.LandFraction();
			}

			// 9–10 板块生命周期：缝合（segments 粒度）/ 裂解（继承式三分）/ 重启循环（场保留）
			//    —— 04 批次 4 落地（03 §9-10 空实现补齐）。⚠️ 每步**恰一次**：生命周期改归属，
			//    量测必须在它之后（判读口/板数/最大板占比都取当步结果）。
			CycleStep();

			// 板数 + 归属拓扑量测（无主/孤立格、连通分量、最大板占比）
			MeasurePlateTopology();
			StepCount++;
		}

		/// <summary>某板最后一步的平均角速度（rad/My；板表显示用；无该板 → 零向量）。</summary>
		public Vector3 LastRotationVector(int plate)
		{
			var table = _motion.PlateOmega;
			return table != null && plate >= 0 && plate < table.Length ? table[plate] : Vector3.Zero;
		}

		// ── 速度三档量测（05-B）：**规定**（力平衡）/ **实到**（CFL 截断后实际搬运）/ **拟合 ω** ──
		// 三者必须分开报：只报 ω 会低估实际搬运（驱动场取逐格边界法线，刚体拟合有损 ~50×）；
		// 只报规定速度会掩盖"一格一步上限把动力学截断"的事实（05-B 要解决的就是这一档）。
		/// <summary>板均**规定速度**（km/My；力平衡解出、相位用它定起跳频率）。</summary>
		public float MeanPrescribedSpeedKmPerMyLastStep { get; private set; }
		/// <summary>板均**实到速度**（km/My；被"每步 ≤ CflMaxStepCellWidths 格"截断后的实际搬运速度）。</summary>
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

		// 归属一致性清理（04 批次 6 治乱）：每步末把"非主体的小同板连通块"（< FragmentMinCells 格）
		// 并入包围它的板——**只改标签、不动任何物质**（质量守恒与厚度场逐位不变），与初始分板的
		// 板级碎片吸收 `AbsorbFragments` 同一口径（生长式分板天然连通，初始分板已无需清理），只是粒度从"整板"
		// 下沉到"板内局部连通块"。
		// 为什么必要：动态路线的归属 = 顶层物质的板号（随物质走、永不重算），于是被后来者的平流
		// 绕过去的残块会**终身保留异板色**（诊断实测 600 My 后 29 个嵌入簇 / 30 强少数格，溯源显示
		// 绝大多数"从未换色"）。它们既无地质含义（是离散化残差不是地体拼贴），又直接显示成毛刺。
		// 主体永不被碰：任何板的**最大**连通分量一律保留（碎片板由 AbsorbFragments 兜底）。
		/// <summary>本步被并入邻板的碎片格数（04 批次 6 治乱；判读口——"单格毛刺"清零的验收数据）。</summary>
		public int StrayFragmentCellsLastStep { get; private set; }

		void MergeStrayFragments()
		{
			int n = _fields.Count;
			var neighbors = _ball.CellNeighbors;
			var plateId = _fields.PlateId;
			Array.Clear(_topologyVisited, 0, n);
			StrayFragmentCellsLastStep = 0;

			// ① 同板连通分量标号（BFS；种子与邻居序固定 ⇒ 确定性）；② 记每板最大分量
			var compCells = new List<List<int>>();
			var compPlate = new List<int>();
			for (int i = 0; i < n; i++)
			{
				if (_topologyVisited[i] || plateId[i] < 0) continue;
				int plate = plateId[i];
				var cells = new List<int> { i };
				_topologyVisited[i] = true;
				int stackTop = 0;
				_topologyStack[stackTop++] = i;
				while (stackTop > 0)
				{
					int current = _topologyStack[--stackTop];
					foreach (int nb in neighbors[current])
					{
						if (_topologyVisited[nb] || plateId[nb] != plate) continue;
						_topologyVisited[nb] = true;
						_topologyStack[stackTop++] = nb;
						cells.Add(nb);
					}
				}
				compCells.Add(cells);
				compPlate.Add(plate);
			}
			var mainIndex = new Dictionary<int, int>();
			for (int c = 0; c < compCells.Count; c++)
			{
				if (!mainIndex.TryGetValue(compPlate[c], out int best) || compCells[c].Count > compCells[best].Count)
					mainIndex[compPlate[c]] = c;
			}

			// ③ 非主体的小块（< FragmentMinCells）并入"共享边最多"的邻板
			const int FragmentMinCells = 3;
			foreach (var entry in mainIndex)
			{
				int plate = entry.Key;
				int mainComponent = entry.Value;
				int plateCells = 0;
				for (int c = 0; c < compCells.Count; c++) if (compPlate[c] == plate) plateCells += compCells[c].Count;

				for (int c = 0; c < compCells.Count; c++)
				{
					if (compPlate[c] != plate || c == mainComponent) continue;
					var cells = compCells[c];
					if (cells.Count >= FragmentMinCells) continue;
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

		// 俯冲体积记账 + 增生楔落地（改进 4 + 04 批次 4）：超出厚度上限的部分不是"按比例抹掉"，而是**进增生楔**
		// 顶死接触格**（造山前缘，platec P3 的"增生落在接收板"思想）——felsic 深成 85 / 火山 15
		// （AccretionFractionToFelsic）。本板无顶死格时只记账（质量消减，进审计）；堆回的部分
		// 计创建（审计净额归零）。被埋板的 mafic 已在平流的顶层裁决里记账为"回地幔"。
		public double AccretionDepositedLastStep { get; private set; }

		void ApplyThicknessCapToAccretion()
		{
			int n = _fields.Count;
			var pools = _fields.AllPools();
			double removedTotal = 0, depositedTotal = 0;
			int cells = 0;
			var overflow = new List<(int cell, double mass)>();
			for (int i = 0; i < n; i++)
			{
				float thickness = _fields.Thickness(i, _material);
				if (thickness <= MaxCrustThicknessM) continue;
				float scale = MaxCrustThicknessM / thickness;
				cells++;
				double removed = 0;
				for (int k = 0; k < 8; k++)
				{
					if (k == 7) continue;                     // Age 不是质量，不缩
					removed += pools[k][i] * (1f - scale);
					}
				for (int k = 0; k < 8; k++)
				{
					if (k == 7) continue;
					pools[k][i] -= pools[k][i] * (1f - scale);
				}
				removedTotal += removed;
				overflow.Add((i, removed));
			}

			// 逐板前缘清单（本步顶死格）→ 溢流质量均摊过去（felsic 85/15 ⇒ 大陆生长通道）
			if (overflow.Count > 0 && _advection.JamContacts.Count > 0)
			{
				var frontByPlate = new Dictionary<int, List<int>>();
				foreach (var (jamCell, jamPlate) in _advection.JamContacts)
				{
					if (!frontByPlate.TryGetValue(jamPlate, out var list)) frontByPlate[jamPlate] = list = new List<int>();
					list.Add(jamCell);
				}
				// 04 批次 4 修：AccretionFractionToFelsic = 深成:火山的 85:15 分配，**全额落场**。
				// 旧写法 0.85×0.85 + 0.85×0.15 = 85% 落场——每次帽活动蒸发 15%（账面净额 0 但场掉质量，
				// 实测缝合活跃期每步丢格级质量）。
				float plutonicShare = AccretionFractionToFelsic;
				float volcanicShare = 1f - AccretionFractionToFelsic;
				float densityRef = _material.FelsicPlutonic;          // 厚度↔质量换算参考密度
				foreach (var (cell, removed) in overflow)
				{
					int plate = _fields.PlateId[cell];
					if (plate < 0 || !frontByPlate.TryGetValue(plate, out var front) || front.Count == 0) continue;
					float perCell = (float)(removed / front.Count);
					foreach (int frontCell in front)
					{
						// 余量帽：接收格不得越过厚度上限（否则单格瞬间堆出上百 km 位移尖峰——
						// 前缘只有 1–2 格时分摊量会远超该格自身质量）。放不下的部分留在楔里
						// （只记消减、不落场）——保守口径，下一步再造。
						float headroomM = MathF.Max(MaxCrustThicknessM - _fields.Thickness(frontCell, _material), 0f);
						float allowMass = headroomM * densityRef;
						float mass = MathF.Min(perCell, allowMass);
						if (mass <= 0f) continue;
						_fields.FelsicPlutonic[frontCell] += mass * plutonicShare;
						_fields.FelsicVolcanic[frontCell] += mass * volcanicShare;
						depositedTotal += mass;
					}
				}
			}

			AccretionMassLastStep = removedTotal;
			AccretionCellsLastStep = cells;
			AccretionDepositedLastStep = depositedTotal;
		}

		/// <summary>碰撞缝合 / 超大陆裂解 / 重启循环（04 批次 4 落地；03 §9-10 的空实现补齐）。
		/// 缝合粒度 = segments（platec P1）：只搬**实际接触该边界的连通分量**，不整板合并；
		/// 裂解 = 继承式三分（远点采样种子 + 板内 Voronoi），物质不动；
		/// 重启 = 全部重分板、物质场保留（platec restart / testproject restart_cycle）。</summary>
		void CycleStep()
		{
			SuturedPairLastStep = null;
			SplitPlateLastStep = -1;

			// 全球板均速（km/My；失速判据）
			double speedSum = 0;
			int plateCount = 0;
			var omega = _motion.PlateOmega;
			foreach (var entry in _plateCellCounts)
			{
				int p = entry.Key;
				if (omega == null || p >= omega.Length) continue;
				speedSum += omega[p].Length() * H3PlateMotion.EarthRadiusKm;
				plateCount++;
			}
			MeanPlateSpeedKmPerMyLastStep = plateCount > 0 ? (float)(speedSum / plateCount) : 0f;

			// ① 缝合：逐对顶死接触比例 ≥ 阈值持续 N 步 → 小板的接触分量并入大板 ──
			TrySuture();

			// ── ② 裂解：最大板超阈值且（失速或超硬顶）且不在冷却期 ──
			if (StepCount >= _splitCooldownUntil)
				TrySplit();

			// ── ③ 周期重启（platec 式四条件 + 本模型超大陆条件）：量测在前、判定在后 ──
			MeasureCycleState();
			TryRestart();

			// ── ④ 碎片小板吸收（platec removeEmptyPlates 对应物）：生命周期会留下 1–2 格的
			//    碎片板（缝合/裂解的残余，实测出现过 1 格、质量 1e5 的空壳板）。并入共享边界
			//    最多的邻板，账户划转；保底板数 ≥ 2。
			AbsorbFragments();
		}

		// 碎片小板（< FragmentMinCells 格）并入共享边界最多的邻板。遍历序固定 ⇒ 确定性。
		void AbsorbFragments()
		{
			const int FragmentMinCells = 3;
			if (_plateCellCounts.Count <= 2) return;
			var fragments = new List<int>();
			foreach (var entry in _plateCellCounts)
				if (entry.Value > 0 && entry.Value < FragmentMinCells) fragments.Add(entry.Key);
			fragments.Sort();
			foreach (int plate in fragments)
			{
				if (_plateCellCounts.Count <= 2) break;
				if (!_plateCellCounts.ContainsKey(plate)) continue;      // 已被并入别处
				int owner = -1, best = 0;
				foreach (var pair in _pairEdges)
				{
					if (pair.Key.a != plate && pair.Key.b != plate) continue;
					int other = pair.Key.a == plate ? pair.Key.b : pair.Key.a;
					if (!_plateCellCounts.ContainsKey(other)) continue;
					if (pair.Value > best) { best = pair.Value; owner = other; }
				}
				if (owner < 0 || !_plateCellCounts.ContainsKey(owner)) continue;
				int moved = 0, total = 0;
				for (int i = 0; i < _fields.Count; i++)
				{
					if (_fields.PlateId[i] != plate) continue;
					total++;
					_fields.PlateId[i] = owner;
					moved++;
				}
				if (moved > 0 && total > 0) _motion.TransferSlab(plate, owner, moved / (double)total);
				_plateCellCounts[owner] += moved;
				_plateCellCounts.Remove(plate);
				_plateSeen.Remove(plate);
			}
		}

		// 缝合触发与执行（04 批次 4；platec P1/P2 的 CA 适配）：
		//   累计：本对顶死数累加（≥ SutureMinPairJamCells 才计——噪声门；**不因非起跳步清零**，
		//         CA 世界的碰撞只在起跳步出现，逐步快照比例太苛刻）；
		//   触发：累计 ≥ SutureContactFraction × 该对边界边数 × SutureStreakSteps
		//         （platec aggr_overlap_abs/rel 的合并语义：碰撞累积量 vs 接触面规模）；
		//   执行：segments 粒度——只把小板侧**实际接触该边界的连通分量**改判给大板（惰性 BFS，
		//         无持久分段表）；物质不动、板片账户按搬移格数比例划转。
		void TrySuture()
		{
			if (_advection.JamPairCounts.Count == 0) return;
			var firedPairs = new List<(int a, int b)>();
			foreach (var pair in _advection.JamPairCounts)
			{
				int jam = pair.Value;
				if (jam < SutureMinPairJamCells) continue;
				if (!_pairEdges.TryGetValue(pair.Key, out int edges) || edges <= 0) continue;
				int progress = _sutureStreak.GetValueOrDefault(pair.Key) + jam;
				_sutureStreak[pair.Key] = progress;
				if (progress >= SutureContactFraction * edges * SutureStreakSteps) firedPairs.Add(pair.Key);
			}

			foreach (var key in firedPairs)
			{
				int small = _plateCellCounts.GetValueOrDefault(key.a) <= _plateCellCounts.GetValueOrDefault(key.b)
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

		// 执行一次缝合：小板侧接触该边界的连通分量改判给大板（segments 粒度；platec P1）。
		// 返回是否有格被搬。惰性 BFS：种子 = 小板侧贴大板的格，泛洪只走小板格——不搬小板
		// 未接触该边界的其他分段（岛/远端陆块留在原板）。
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
			foreach (var entry in _plateCellCounts)
			{
				totalCells += entry.Value;
				if (entry.Value > largestCount) { largestCount = entry.Value; largest = entry.Key; }
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

			// 分配 = **多源 BFS 生长**（platec growPlates 同构；04 批次 4）：三源同层推进，
			// 每格归属最近种子的**图距离**——保证每块新板连通（距离 Voronoi 会切出碎散格）。
			// 遍历序固定（队列 FIFO + 邻居序固定）⇒ 确定性。
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

		// 重启触发与执行：失速或超大陆持续 RestartStallSteps 步 → Repartition 全部重分板（场保留）。
		// 重启触发与执行（05 §6.3 对齐 platec `lithosphere::update/restart`）：
		//   ① 板均**实到**速度 < RestartStallSpeedKmPerMy（连续 N 步）——对应 platec `totalVelocity < 2.0`
		//   ② 系统动量/峰值 < RestartEnergyRatio（连续 N 步）——platec `systemKineticEnergy/peak_Ek < 0.15`（峰值不复位）
		//   ③ 距上次陆-陆碰撞 > RestartNoCollisionSteps 步——platec `last_coll_count > NO_COLLISION_TIME_LIMIT`
		//   ④ 周期步数 > RestartCycleSteps——platec `iter_count > RESTART_ITERATIONS`（一个周期 600 My）
		//   ⑤ 最大板占比 > RestartSupercontinentFraction——**本模型自有**补充触发（platec 无此项）
		// 执行 = platec `restart()` 的本模型对应物：重分板（新边界/新撒点）而**物质场与年龄保留**
		// （platec 是把各板的(高度,年龄)铺回世界图再重建板块、并回灌年龄；本模型地壳是单份全局场 ⇒
		// "保留"天然成立，见 05 §6.3）。
		void TryRestart()
		{
			if (Repartition == null) return;

			bool speedStalled = MeanRealizedSpeedKmPerMyLastStep < RestartStallSpeedKmPerMy;
			bool energyDecayed = _peakMomentum > 0 && _systemMomentumLastStep / _peakMomentum < RestartEnergyRatio;
			bool quiet = RestartNoCollisionSteps > 0 && StepsSinceContinentalCollision > RestartNoCollisionSteps;
			bool cycleLong = CycleStepCount > RestartCycleSteps;
			bool supercontinent = LargestPlateFractionLastStep > RestartSupercontinentFraction;

			// 速度/能量条件要连续成立 N 步（抖动门；platec 无此要求，差异登记在 05 §6.3）
			_stallStreak = speedStalled || energyDecayed || supercontinent ? _stallStreak + 1 : 0;
			bool streakSatisfied = _stallStreak >= Math.Max(1, RestartStallSteps);
			if (!((streakSatisfied && (speedStalled || energyDecayed || supercontinent)) || quiet || cycleLong)) return;
			if (MaxRestartCycles > 0 && RestartCount >= MaxRestartCycles) return;      // 周期数上限（platec max_cycles）

			int[] fresh = Repartition(_initialPlateCount);
			if (fresh == null || fresh.Length != _fields.Count) return;
			Array.Copy(fresh, _fields.PlateId, _fields.Count);
			_motion.ResetPlateState();
			_sutureStreak.Clear();
			_nextPlateId = _initialPlateCount;
			_stallStreak = 0;
			_splitCooldownUntil = StepCount + SplitCooldownSteps;
			RestartCount++;
			CycleStepCount = 0;                       // platec：重启后 iter_count 归零
			StepsSinceContinentalCollision = 0;
			// ⚠️ 峰值动量**不**复位（platec 的 peak_Ek 是成员、restart() 不清零）——故跨周期后
			// "活动衰减"条件会越来越难满足，这是 platec 的原意（峰值 = 有史以来最活跃的时刻）。
			LastRestartReason = quiet ? "久无陆碰撞" : cycleLong ? "周期届满"
				: speedStalled ? "总速度过低" : energyDecayed ? "动能衰减" : "超大陆";
		}

		// platec 式周期量测（每步一次，供 TryRestart 与判读口用）：
		//   系统能量 = Σ_板 (板地壳质量 kg × **规定**速度 km/My)（platec `momentum = mass × velocity` 口径）。
		//   ⚠️ 用**规定速度**（力平衡解出）而不是实到速度：实到被"一格一步"上限钉平（全板同值）
		//   ⇒ 能量恒定 ⇒ 能量条件永不触发（② 未落地时尤其明显）。规定速度才是"发动机还剩多少"的读数。
		//   峰值只增不清（platec `peak_Ek` 语义，且**不随重启复位**）；陆-陆碰撞计数按 platec 写法
		//   "有碰撞清零、无碰撞自增"。
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

		/// <summary>量归属场的**形状**：无主格 / 孤立格 / 连通分量数 / 最大分量占比 / 最大板占比 / 板数。
		/// v1.4 起归属 = 顶层物质的板号（随物质搬运），理论上"板块的格嵌进别板"结构性消失——
		/// 本量测保留为**回归验收器**：孤立格 = 0、连通分量 = 板数、强少数格 ≈ 0 应常态成立。
		/// （O(N)，每步一次；复用缓冲，不分配——res5 = 2,016,842 格也不怕。）</summary>
		void MeasurePlateTopology()
		{
			int n = _fields.Count;
			var neighbors = _ball.CellNeighbors;
			var plateId = _fields.PlateId;

			Array.Clear(_topologyVisited, 0, n);
			_plateSeen.Clear();
			_plateCellCounts.Clear();

			int ownerless = 0, isolated = 0, localMinority = 0, components = 0, largestComponent = 0, stackTop = 0;
			_pairEdges.Clear();
			_plateBoundaryEdges.Clear();

			// ① 逐格：无主（与逐板格数——缝合/裂解/重启触发器的输入，恒统计）+ 逐对边界边数
			//    （缝合触发器输入，恒统计）+ 孤立/强少数（可门控）。每条边界边两侧各记一次
			//    （口径一致，比例计算分子分母同尺度）。
			for (int i = 0; i < n; i++)
			{
				int p = plateId[i];
				if (p < 0) { ownerless++; continue; }
				if (_plateSeen.Add(p)) _plateCellCounts[p] = 0;
				_plateCellCounts[p]++;

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
			foreach (var entry in _plateCellCounts)
				if (entry.Value > largestPlate) largestPlate = entry.Value;

			OwnerlessCellsLastStep = ownerless;
			IsolatedPlateCellsLastStep = isolated;
			LocalMinorityCellsLastStep = localMinority;
			PlateComponentCountLastStep = EnableTopologyMeasurement ? components : -1;
			LargestComponentFractionLastStep = EnableTopologyMeasurement && n > 0 ? (float)largestComponent / n : -1f;
			LargestPlateFractionLastStep = n > 0 ? (float)largestPlate / n : 0f;
			PlateCount = _plateSeen.Count;
		}

		// ═══════════════════════════════════════════════
		// 输出：写回 new_HexWorld 渲染/地图模式用的六场 Crust
		// ═══════════════════════════════════════════════

		// TEMP 阶段对账（用完删）
		public double[] DbgStageMass = new double[6];
		public double[] DbgStageLedger = new double[6];
		void DbgSnap(int k) { DbgStageMass[k] = TotalCrustMass(); DbgStageLedger[k] = CrustCreatedTotal - CrustDestroyedTotal; }

		// ── 构造地形带（04 批次 3）：终态一次施加（海沟下挖 + 弧火山质量 → 均衡/海平面重算）──
		public H3BoundaryRelief Relief { get; private set; }

		/// <summary>终态施加构造地形带（海沟/弧；调用方先用终态速度场 Build 好 H3PlateBoundary 再来）。
		/// 弧新增质量记入创建账（守恒对账口径不变）。</summary>
		public H3BoundaryRelief ApplyBoundaryRelief()
		{
			var relief = new H3BoundaryRelief();
			relief.Apply(_ball, _fields, _motion.Velocity, _isostasy, _material);
			CrustCreatedTotal += relief.ArcAddedMass;
			Relief = relief;
			return relief;
		}

		/// <summary>把动态模拟结果写进 new_HexWorld 的 `Crust`（渲染与地图模式只认这六场）。
		/// 口径：FelsicThick/MaficThick = 对应物质的质量 ÷ 密度（→ 米）；Age = My；
		/// Elevation = 均衡位移 − 海平面（相对海平面高度，> 0 = 陆）。</summary>
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
