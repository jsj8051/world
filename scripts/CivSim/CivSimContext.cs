using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

namespace World.CivSim;

/// <summary>
/// 文明演化上下文（一次演化的全部状态；机制在此读写）。
/// 自然层（GameGrid）全程只读；确定性：同 seed 同网格同结果。
/// 参数惯例：量级标定（"合理科学即可，不硬标定"），验证看涌现结果再校准；★ = 已定稿常数。
/// </summary>
public sealed class CivSimContext
{
    public GameGrid Grid;
    public Tribe[] CellTribes;   // 每格唯一驻留部落（一格一实体；null=空格；阶段2 由 List<Tribe>[] 简化而来）
    public List<Tribe> Tribes;       // 全部存活实体
    public int Tick;
    public int Seed;
    public int OriginCount = 3;
    public Random Rng;

    // ── 层1 空间生产力（静态：Miami NPP × 水因子 × k；人/km² 密度量级；两层模型 2026-08-17）──
    public float[] R;        // 每格空间生产力密度（人/km²；0=海洋/不可居）
    // ── 层2 食物流（每 tick 刷新：产出 vs 消耗 D=P×c，c=1）──
    public float[] CellF;        // 每格当 tick 总产出（Σ 实体 F_i，人当量）
    public float[] CellPop;      // 每格总人口
    public float[] CellFarmPop;  // 每格农业部落总人口（劳动因子用；RefreshCellState 缓存）

    // ── 影响力场模型（2026-08-10 定稿，5 km² 口径：每格归属 = argmax(P×M×w(d))）──
    // ⚠️ 2026-08-17 土地挂钩（用户拍板）：砍存量/再生——采集 = 静态丰度 × 可用土地 × 劳动力；
    //   农田开垦率 Cultivation 是土地竞争载体（采集 ×(1−开垦)，农业 ×开垦）。
    public float[] Cultivation;      // 每格开垦率 0~1（农田占用土地；0=未开垦）
    public int[] CellOwner;          // 格归属 band id（-1=无主；归属唯一，其他 band 禁入）
    public int[] CellBestOwner;      // 影响力场重算暂存：每格当前最强影响力 band
    public float[] CellBestInf;      // 影响力场重算暂存：最强影响力值
    public float[] CellOwnerInf;     // 现 owner 的影响力（粘性比较基准；本 tick 开头）
    public List<int>[] TerritoryCells;   // 每 band 领地格列表（CellOwner 反查派生，RebuildTerritory 重建）
    public List<byte>[] TerritoryDists;  // 每 band 领地格到驻扎点距离（0-3，w 加权用）

    // ── 酋邦层（2026-08-17）：第二层并查集——部落联盟（跨领地政治整合）──
    public List<int>[] ChiefdomCells;    // 酋邦 id → 成员 band Id（ChiefdomModel.Rebuild 填充）
    public int ChiefdomLastEval = -100;  // 凝聚评估频率守卫（与领地同频）
    public int AbsorptionLastEval = -100; // 吞并评估频率守卫（2026-08-17；10 tick 同频，不入档）
    private HashSet<int> _liveIdSet;      // 活实体 Id 集（RebuildInfluence 死残留清理——2026-08-17）
    private Queue<int> _bfsQ;            // BFS 复用队列（GC 优化）
    private Queue<int> _bfsDQ;

    // ── 自然层派生缓存（确定性重建，不存档）──
    public byte[] WildCrops;   // WildCrops 位（grid.EnsureWildCrops 惰性）
    public float[,] Suit;      // 每格每种子适宜度 φ（WildCropsSystem.Suitability 缓存）
    public float RMax = 1f;    // 层1 R 最大值（BuildLayer1 计算——殖民落点分数归一化用；不存档）

    // ── 演化统计（诊断/输出）──
    public int Fissions;
    public int Migrations;
    public int Conflicts;   // 冲突计数（2026-08-10 冲突机制）
    public float TradeVolume;   // 贸易总流量（人当量·年；2026-08-19 演化级观测——TradeModel 累计）
    public int TradeEvents;     // 贸易转移次数（每次单商品转移记 1）
    public int CultureKeyCount;         // 文化标签 key 计数器（分裂分化分配新 key，如 "cult_12"）
    public int CultureGroupKeyCount;    // 文化群 key 计数器（2026-08-07 与文化标签分开——语言大群独立 key 空间，防标签挤占）
    public int ReligionKeyCount;        // 宗教派别 key 计数器（起源/分裂分化分配新 key，如 "relig_3"）
    public int FirstFarmTick = -1;      // 首转农 tick（终止条件锚点）
    public int TerritoryLastRebuild = -1;   // 最近凝聚重算 tick（TerritoryModel 频率守卫）
    public int[] BfsStamp;
    public int BfsStampValue;
    public int NextTribeId;   // 实体 Id 分配计数器（2026-08-10：独立于 Tribes.Count——存档只存活实体，Count 会分叉）
    public string[] KeyBuf;    // 科技遍历排序缓冲（SpreadTech 复用，无分配；2026-08-10 确定性）
    public int[] LockedUntil;  // 武力夺取格锁定到期 tick（-1=无锁定；2026-08-10 冲突机制——锁定内场不重算）

    // ── 聚落实体（2026-08-19 阶段3 聚落设计，docs/阶段3设计-聚落实体.md）──
    public List<Settlement> Settlements = new();   // 全部聚落（存活 + 废墟；场所比人长寿）
    public int NextSettlementId;                   // 聚落 Id 分配器（确定性；读档恢复）

    // ── 战争状态（2026-08-19 阶段5 军事征服，docs/阶段5设计-军事征服.md）──
    // 外交状态：多 tick 持续，**v14 存档段**（过程状态不可派生重建——用户拍板 P3）。
    public List<War> Wars = new();                 // 进行中的战争（交战 + 朝贡期）
    public int WarsDeclared;   // 演化统计：宣战场次（不入档——同 Conflicts 模式，诊断/日志）
    public int WarsAnnexed;    // 演化统计：吞并场次

    /// <summary>部落占据的聚落（PlaceId → 实体；无/废墟 = null）。O(S)——S=聚落数（农业定居，规模小）。</summary>
    public Settlement SettlementOf(Tribe e)
    {
        if (e == null || e.PlaceId < 0 || Settlements == null) return null;
        for (int i = 0; i < Settlements.Count; i++)
            if (Settlements[i].Id == e.PlaceId) return Settlements[i];
        return null;
    }

    /// <summary>分配新文化 key（确定性递增；与科技 key 同风格——字符串可读）。</summary>
    public string NextCultureKey() => $"cult_{CultureKeyCount++}";

    /// <summary>分配新文化群 key（独立计数 + "cultg_" 前缀——与标签 "cult_" 空间隔离，防冲突；KeyNum 双前缀解析兼容旧档，2026-08-07）。</summary>
    public string NextCultureGroupKey() => $"cultg_{CultureGroupKeyCount++}";

    /// <summary>分配新宗教派别 key（起源/分裂分化；图腾漂变）。</summary>
    public string NextReligionKey() => $"relig_{ReligionKeyCount++}";

    // ═══════════════════════ 参数（★ 定稿，docs/石器时代设计.md）═══════════════════

    public const float GrowthRatePerYear = 0.005f;   // r_eff 年率（0.5%/年）；tick=100 年 → 0.5/tick
    public const int TickYears = 100;
    public const float W = 0.2f;                      // 耕作劳动成本差（Sahlins；稳态论证 0.8 > 0.77）
    public const float HRel = 0.3f;                   // 狩猎耗竭项 h = 0.3·Y_猎（随产量缩放）
    public const float Hysteresis = 0.02f;            // 滞回带（必须 < 农业稳态差 0.03，否则锁死切换）
    public const float SplitPop = 25f;               // 分裂阈值（2026-08 史实标定：band 25-50 人[考古群规模]→长到史实下限才分裂殖民；原 12 偏早致部落过小）
    public const int MaxTribesPerCell = 8;            // 格内实体上限（遗留：一格一实体后恒 1，保留兼容诊断显示）
    public const float SplitShare = 0.45f;            // 分裂新实体带走比例
    public const float FissionTensionStart = SplitPop;   // 规模张力起算点（跟随 SplitPop——band 量级，2026-08-17 土地挂钩；原硬编码 12 与 SplitPop 断链）
    public const float FissionTensionSpan = 8f;    // 张力封顶跨度（12+8=20 → 张力 1.0）
    public const int ColonizeRadius = 6;             // 殖民/迁移搜索最大跳数（阶段2 扩大：原 3 跳致部落扩张停滞，改 6 跳 BFS）
    public const float ColonizeFertilityBias = 0.3f; // 殖民落点肥度偏好（2026-08-19 扩散修正：score=cost×(1+bias×R/RMax)——
                                                     //   距离主导、肥度微偏好（×1.3 封顶）；旧 R×cost 只挑最肥格 → 富饶区独占、贫瘠空置；
                                                     //   1.0→0.3 校准：bias=1 时富饶近邻 2.0 仍碾压贫瘠近邻 1.05——溢出太慢（n128 实测仅 +3% 覆盖））
    public const int TerritoryRebuildEvery = 10;     // 凝聚重算间隔 tick（Union-Find，~35 万边/次）
    public const float TerritorySpreadMult = 1.5f;   // 同领地传播乘数（领地整合加成）
    public const float CrossBorderSpreadMult = 0.5f; // 跨领地边界传播乘数（软冲突）
    public const float TerritoryDriftDiv = 0.5f;     // 领地内分裂漂变概率减半（凝聚自稳）
    public const float SettleGrowthMult = 1.5f;      // 定居生育跃迁：人口增长 r ×1.5（史实：定居密度 10-50× 游群；★ 标定）

    // ── 酋邦层（2026-08-17：Sahlins 1963 声望 / Earle 1997 贡赋 / Kirch 1984 联盟锚定）──
    public const float PrestigeGainRate = 0.02f;       // **绝对盈余**（人当量）→ 声望/tick——宴席是绝对食物量
                                                       //   （能喂多少人，Sahlins）——★ 标定：分裂后盈余窗口
                                                       //   （P=6/F=14 → 盈余 8 人 × 0.02 = 0.16/tick，
                                                       //   增长闭合前 ~10-25 tick → 1.6-4.0 声望 → BigMan）
    public const float PrestigeDecay = 0.001f;         // 无盈余衰减/tick（声望可逆——Big Man 个人化，Sahlins）
    public const float BigManPrestigeThreshold = 1.0f; // 声望阈值 → BigMan
    public const int ChiefdomEvalEvery = TerritoryRebuildEvery;   // 凝聚评估频率（跟随领地重建同频；原硬编码 10 与 TerritoryRebuildEvery 断链）
    public const int ChiefdomMinTribes = 2;            // 酋邦最小部落数（<2 → 解散）
    public const float TributeRate = 0.1f;             // 盈余贡赋率（实物税——夏威夷 ahupua'a 土地分区，Earle）
    public const float TributeRelief = 0.5f;           // 灾年开仓缓冲（互惠：贡献过才受赈，Halstead-O'Shea）
    public const float EliteFrac = 0.1f;               // 酋长 band 精英比例（非生产者——祭司/战士/亲信，贡赋供养）
    public const float InternalConflictMult = 0.5f;    // 同酋邦冲突概率 ×0.5（酋长仲裁——非消除，pax 不存在）
    public const float SuccessionConflictMult = 2.0f;  // 继承窗口冲突概率 ×2（继承战争，Kirch：Polynesia 常态）
    public const int SuccessionWindowTicks = 20;       // 继承窗口时长
    // ── 酋邦庇护机制（2026-08-19 重构：至尊酋长个人再分配圈——无硬上限，规模从半径涌现）──
    public const int ChiefReach = 12;   // 至尊酋长个人再分配半径（格步；酋长只能庇护半径内成员——史实：再分配有物理半径，
                                        //   Earle ahupua'a；语言网络照常大，政治体只能长到个人声望够得着的大小；
                                        //   史实对照：酋邦数万人口量级；★ 待校准）
                                        // ── 国家涌现参数（2026-08-16 阶段4，docs/阶段4设计-国家涌现.md；用户拍板 1A2A3A4A）──
                                        //   国家 = 酋邦的制度化：都城（权力中心）+ 决策层级 + 贡赋盈余 + 存续 → 涌现；
                                        //   机制差异：税制化（贡赋率×2）、官僚供养↑（精英比例）、继承制度化（王朝豁免危机）、
                                        //   内部秩序（Weber 强制力垄断——同邦冲突概率减半）。全部条件用已入档字段 → 纯派生不存档。
    public const float StateTributeRate = 0.2f;        // 国家贡赋率（酋邦 TributeRate=0.1 翻倍——税制化，Earle）
    public const float StateEliteFrac = 0.25f;         // 国家官僚/精英比例（酋邦 EliteFrac=0.1——官僚化）
    public const float StateInternalConflictMult = 0.25f; // 国家内部冲突概率倍率（酋邦 0.5——Weber 强制力垄断）
    public const int StateCapitalLevel = 2;            // 都城最低聚落等级（城镇+；都城判定本就放宽一档——首都更易达标，Childe 权力中心）
    public const int StateSubCenterLevel = 1;          // 次级中心最低聚落等级（村庄+；Wright-Johnson 决策层级第 2 级）
    public const float StateTributePerCap = 0.01f;     // 贡赋盈余线：贡赋池 ≥ 酋邦总人口×此值（剩余集中，Childe）
                                                       //   ★ 校准（0.1→0.01，2026-08-16 探针）：Contributed 是互惠记录且被
                                                       //   精英供养持续消耗（酋长 P×0.1/tick）——n128 实测最大邦池/人口 ≈ 0.014~0.03，
                                                       //   0.1 线下全图 0 国家；0.01 线匹配"少数国家涌现"（史实：早期国家稀少）
    public const int StateDwellTicks = 20;             // 都城实体存续时长（制度化需要时间；对应城市阈值 Dwell 20 tick 量级；★ 待校准）
    // ── 军事征服参数（2026-08-19 阶段5，docs/阶段5设计-军事征服.md；用户拍板 P1-P6）──
    public const int WarBattleInterval = 5;        // 会战节奏（每 N tick 一场；5 tick = 500 年）
    public const float WarMinPoolPerCap = 0.02f;   // 宣战门槛：贡赋池 ≥ 总人口×此值（防穷兵黩武；比国家维持线 0.01 高——战争是余力行为）
    public const float WarDeclareChance = 0.002f;  // 候选国家对/tick 宣战概率（低频——战争偶发，n128 全演化约几场）
    public const int WarCooldownTicks = 30;        // 参战冷却（宣战/被宣战后 N tick 不参与新战争）
    public const int WarMaxTicks = 60;             // 战争最长持续（超时停战——6000 年防死锁；战争多会战累计在窗口内）
    public const int WarAnnexWins = 3;             // 吞并线：累计胜场 ≥ 3 且当前军力比 ≥ WarPowerRatio（碾压）
    public const float WarPowerRatio = 1.5f;       // 吞并力量比（军力对比——碾压才吞并）
    public const int WarTributeWins = 2;           // 朝贡线：胜场 ≥ 2（低于吞并线——险胜）
    public const float WarLoss = 0.03f;            // 会战败方损耗（成员人口 + 贡赋池 ×此值/场；3 场 ≈ 9%——消耗战可承受）
    public const float WarCapitalBonus = 0.1f;     // 都城加成：军力 ×(1+0.1×都城Level)（权力中心集结——Childe）
    public const float WarCityDefenseBonus = 0.1f; // 城墙加成（P6）：防御方（被宣战国）军力 ×(1+0.1×城市数)（城市=要塞）
    public const float WarTributeRate = 0.005f;    // 朝贡：每 tick 转移 战败国总人口×此值 入战胜国贡赋池（对比 TributeRate 0.1/tick——战败重负）
    public const int WarTributeTicks = 40;         // 朝贡持续 tick（4000 年——一代人的重负）
    public const float WarPlunderRate = 0.5f;      // 吞并战利品：战胜国池 += 战败国池×此值（Tilly 战争养战争）
    public const float WarConflictMult = 2.0f;     // 交战国边境冲突概率 ×2（战争中的治安战更凶——外交断交的格级表现）
    public const int WarCedeCells = 3;             // 朝贡割地格数（战败国边境格易主）
    // ── 聚落实体参数（2026-08-19 阶段3 聚落设计；docs/阶段3设计-聚落实体.md §2.3）──
    public const int SettlementLevelTicks1 = 3;    // 新村→村庄 Dwell ticks（★ 待校准）
    public const int SettlementLevelTicks2 = 8;    // 村庄→城镇
    public const int SettlementLevelTicks3 = 20;   // 城镇→城市
    public const float SettlementPop1 = 200f;      // 村庄人口阈值
    public const float SettlementPop2 = 800f;      // 城镇人口阈值
    public const float SettlementPop3 = 3000f;     // 城市人口阈值
    public const float SettlementStoragePerLevel = 0.5f;   // 每级存储容量加成（×0.5/级——村庄 1.5×、城市 2.5×）
    public const float SettlementGrowthPerLevel = 0.25f;   // 每级增长倍率加成（×0.25/级——城市化集聚）
    public const int SettlementLevelCooldown = 2;  // 升级冷却 ticks（防跳级抖动；须 < 最小时限阈值 3）
    // 随身池（Tribe.Stocks 新语义 2026-08-19：正式存储迁聚落，部落只剩随身）——
    //   容量沿用原游群档（游群即随身；定居部落也随身基础量——行囊/工具/日粮）
    public const float CarryFoodCap = 0.06f;   // 随身食物容量（×P）
    public const float CarryMatCap = 0.02f;    // 随身材料容量（×P）
    public const float SettleFoodCap = 0.5f;   // 粮仓食物容量（×P，无等级；×levelMult 后）
    public const float SettleMatCap = 0.2f;    // 粮仓材料容量（×P，无等级；×levelMult 后）

    // ── 商品副产率（2026-08-09：生产方式副产品；2026-08-18 阶段3 并入 CommodityTable 存储体系）──
    public const float LeatherRate = 0.10f;   // 狩猎产出 → 皮革（★ 标定）
    public const float WoolRate = 0.15f;      // 畜牧产出 → 羊毛（★ 标定）
    public const float StrawRate = 0.05f;     // 农业产出 → 秸秆（★ 标定）
    public const float HerdMult = 2.0f;       // 畜牧单位土地产出倍率（"少许土地产生食物"；★ 标定）

    // ── 贸易机制（2026-08-18 阶段3 贸易期物物交换；docs/阶段3设计-贸易机制.md）──
    public const float TradeRate = 0.1f;        // 接触对/tick 交换比例（人均库存差的 10%——低频集市语义，物物交换非每日；★ 待校准）
    public const float TradeDistanceRate = 0.5f; // 运输成本：边界格距 d → ×(1/(1+0.5d))（接触对 d=1 → ×0.667；黑曜石随距衰减史实）
    public const float TradeMinGap = 0.01f;     // 人均差 < 0.01 不换（防噪声抖动——需求匹配不足）
    public const float TradeFoodFloor = 0.05f;  // 食物出口保底：出口后人均 Food ≥ 5%×P（≈5 年存粮——饥荒最后防线，防贸易拆粮仓；
                                                //   2026-08-18 用户拍板"Food+Material 全开放"配保底；当前 TradeRate 下为防御性不触发）
    public const float MigrateThreshold = 0.75f;      // 饱和迁徙阈值（格 P_格/F_格）
    public const float MigrateShare = 0.5f;           // 饱和迁徙分出比例
    public const float ScoutChance = 0.02f;           // 探路迁徙概率/tick
    public const float ScoutMinPop = 100f;            // 探路最小实体人口
    public const float ScoutShare = 0.3f;             // 探路迁出比例
    // ── 两层模型（2026-08-17 定稿）──
    public const float IrrigMult = 5f;                // 灌溉因子：近水格农业 ×5（河谷尖峰来源；★ 待校准）
    public const float LaborFrac = 0.1f;              // 采集劳动力需求（P_劳动 = LF×潜在：粗放经济，Sahlins 每周 ~15-20h）
    public const float LaborFracFarm = 0.2f;          // 农田劳动力需求（2026-08-17 凹化+等边际：农业劳动密集 ~2×采集——Sahlins 每周 40h+；非同构参数使等边际分配有区分度）
    public const float TargetMedianDensity = 0.1f;    // k 标定锚：陆地 R 中位数 ≈ 0.1 人/km²（2026-08 史实标定：前农业时代狩猎采集密度 0.1人/km²；原 0.3 为史实3倍→领地受压挤、部落聚居）
    public const float AssimilateRate = 0.03f;        // 格级同化速率（文化/宗教；2026-08-07 0.3→0.1→0.03：同化放缓 → 弱文化/新派别有存活窗口）
    public const float CultureSpreadRate = 0.05f;     // 相邻部落文化横向传播速率/tick（2026-08-19 修复死代码：同语言群异文化接触转移；
                                                      //   旧 Axelrod 门槛把唯一有效组合挡死 → 文化永不混合 → 地图大片单色；★ 待校准——慢：弱文化有存活窗口）
    public const float ReligionSpreadRate = 0.02f;    // 宗教传播速率/tick/接触（只向高阶）
    public const float ReligionUpgradeRate = 0.05f;   // 泛灵→萨满升级速率/tick
    public const float CultureDriftChance = 0.05f;    // 分裂时新文化群/派别概率（5%；2026-08-07 0.5%→1%→2%→5%：分化加强 → 区域多样性）
    public const float SeedPressure = 0.7f;           // 种子压力触发阈值（格人口 P_格/K_格）
    public const float SeedInvProb = 0.005f;          // 种子基础发明概率/tick（起源区少数）
    public const float EnvMismatchFactor = 0.3f;      // 发明 env_i：环境不匹配但非硬门槛
    public const int OriginDistMin = 12;              // 起源两两最小球面格距（≈1300 km）
    public const float OriginPop = 10f;   // 起源 band 人口（2026-08 史实标定：密度降 0.1 后起源小群随之调小，防起源即超载饿死——原 15 匹配 0.3 密度）
    public const int TerminateAfterAgri = 100;        // 首转农 +100 ticks 结束
    public const int MaxTicksNoAgri = 500;            // 兜底：无农 500 ticks 停止（天然灭绝星球）
    // ── 影响力场模型常量（2026-08-10 定稿）──
    public const int InfluenceRadius = 6;        // 影响范围（格步数；2026-08 史实标定：密度0.1下养25人band需~300km²=~60格，3步仅24格不足→扩到6步~110格对应5km+利用半径）
    public const float Stickiness = 1.15f;       // 归属粘性：非 owner 需超现 owner 影响力 ×1.15 才易主
    // ── 土地挂钩（2026-08-17 用户拍板：砍存量再生——采集=建筑×可用土地×劳动力；农田占用土地）──
    public const float CultivateRate = 0.05f;    // 开垦速率/tick（农田占用增长：20 tick=2000 年满开垦；★ 待校准）
    public const float PreyHabitatLoss = 0.5f;   // 猎物栖息地破碎系数（开垦对猎物间接削减；浆果被直接替代）
    // ── 冲突机制（2026-08-10 定稿 §十五）：归属两条途径——和平（场 argmax+粘性）/ 武力（冲突强制易主+实控锁定）──
    public const int ConflictLockTicks = 8;      // 实控锁定：武力夺取格 N tick 内场不重算（胜者持续产粮→人口增长窗口）
    public const float ConflictChance = 0.01f;   // 每僵持格/tick 触发概率（低频——旧石器战争是偶发事件，全演化 0~十几次）
    public const float ConflictLossChallenger = 0.08f;  // 胜者（挑战者）损耗比例（胜者损失小）
    public const float ConflictLossOwner = 0.20f;       // 败者（owner）损耗比例（败者损失大）
    public const float ConflictExpelChance = 0.6f;      // 败者被驱逐概率（损耗后强制迁移）
    public const int ConflictCooldown = 12;      // 冲突冷却 tick（实体级，防连续刷）
    public const int MigrateCooldown = 8;        // 迁移冷却 tick（防抖动）
    public const int SplitCooldown = 4;          // 分裂冷却 tick（2026-08-10 殖民式分裂：防每 tick 指数爆炸）

    public float TickFactor => GrowthRatePerYear * TickYears;   // 0.5/tick ★

    // ── 层1 空间生产力 R（两层模型 2026-08-17；Miami 模型 Lieth 1975，NPP 净初级生产力）──

    /// <summary>Miami NPP（g 干物质/m²/年）；干旱/寒冷最小因子律（Liebig）。</summary>
    public static float MiamiNpp(float tempC, float precipMm)
    {
        float nppT = 3000f / (1f + Mathf.Exp(1.315f - 0.119f * tempC));
        float nppP = 3000f * (1f - Mathf.Exp(-0.000664f * precipMm));
        return Mathf.Min(nppT, nppP);
    }

    /// <summary>水因子（×1.5）：Riparian 或 LakeLevel>0 或邻湿地（原邻水加成保留）。</summary>
    public bool WaterRich(int cell)
    {
        if (Grid.Biome[cell] == (byte)BiomeType.Riparian || Grid.LakeLevel[cell] > 0) return true;
        foreach (int nb in Grid.Neighbors[cell])
            if (Grid.Biome[nb] == (byte)BiomeType.Riparian || Grid.LakeLevel[nb] > 0)
                return true;
        return false;
    }

    /// <summary>灌溉因子（农业空间选择性）：近水格 ×IrrigMult=5（河谷尖峰来源）。</summary>
    public float IrrigFactor(int cell) => WaterRich(cell) ? IrrigMult : 1f;

    /// <summary>冲积土因子：Soil5 ×3、Soil4 ×2、≤3 ×1（冲积平原富集；★ 待校准）。</summary>
    public static float AlluvFactor(byte soil) => soil switch { 4 => 2f, 5 => 3f, _ => 1f };

    /// <summary>寒冷区判定（§4.5：火/皮毛解锁对象）。</summary>
    public static bool IsColdZone(BiomeType b) =>
        b is BiomeType.IceCap or BiomeType.Tundra or BiomeType.Subarctic or BiomeType.Alpine;

    // ── 闭塞区域（2026-08-07 用户拍板：方案 A 调参 + 动态障碍系数 + 气候相似度）──
    //    现实文化演化参考（Diamond）：障碍不是二值而是成本梯度、技术可突破（火/皮毛/独木舟）、
    //    同气候带传播快（轴向效应）、文化边界停在障碍处（涌现，不硬编码"塔里木盆地是闭塞区"）。

    /// <summary>地形通行成本（单格侧，0 = 不可穿）。技术突破障碍：火/皮毛解锁冰原、canoe 解锁海洋。</summary>
    public float TerrainCost(int cell, HashSet<string> keys)
    {
        var b = (BiomeType)Grid.Biome[cell];
        if (!Grid.IsLandCell(cell))               // 海洋
            return keys.Contains(TechTable.Canoe) ? 0.3f : 0f;
        switch (b)
        {
            case BiomeType.IceCap:
                if (!keys.Contains(TechTable.Fire)) return 0f;              // 无火不可穿
                return keys.Contains(TechTable.Clothing) ? 0.3f : 0.1f;     // 火 0.1 / 火+皮毛 0.3
            case BiomeType.Alpine: return 0.2f;    // 山脉难翻越
            case BiomeType.HotDesert:
            case BiomeType.ColdDesertKoppen: return 0.3f;   // 沙漠难穿越
            default: return 1f;                      // 平原/草原/森林/湿地畅通
        }
    }

    /// <summary>气候相似度（0.15~1）：同气候带≈1、跨带低——Diamond 轴向效应（东西向传播快、南北向慢）。</summary>
    public float ClimateSim(int a, int b)
    {
        float dT = Mathf.Abs(Grid.Temp[a] - Grid.Temp[b]);
        float dP = Mathf.Abs(Grid.Precip[a] - Grid.Precip[b]);
        float sT = Mathf.Clamp(1f - dT / 25f, 0f, 1f);      // 温差 25°C 满衰减
        float sP = Mathf.Clamp(1f - dP / 800f, 0f, 1f);     // 降水差 800mm 满衰减
        return Mathf.Clamp(0.6f * sT + 0.4f * sP, 0.15f, 1f);
    }

    /// <summary>跨格传播/迁徙系数 = min(两端地形成本) × 气候相似度（闭塞区域涌现：山脉/沙漠/冰原/海 → 传播弱 → 独立演化）。</summary>
    public float BorderCost(int a, int b, HashSet<string> keys)
    {
        float terr = Mathf.Min(TerrainCost(a, keys), TerrainCost(b, keys));
        if (terr <= 0f) return 0f;
        return terr * ClimateSim(a, b);
    }

    // ── 产量与能量（§4.2-4.4 定稿公式；两层模型 2026-08-17）──

    /// <summary>实体狩猎产出 = 土地份额 × 劳动力（2026-08-09 用户拍板：采集也是"劳动力维持的获取方式"，与农业同构）。
    /// 土地份额 A_i = 格面积 ÷ 格内活部落数（部落拥有的土地，均分——人多地少，格总承载 = R×面积×m 与部落数无关，
    ///   修复此前每部落各拿整格产出 → 8 部落同格 8 倍人口超载）；
    /// 潜在产出 Y_pot = R × A_i × 工具乘数链 m（猎物再生率恒定近似，种群动态二期）；
    /// 实际产出 = Y_pot × min(1, P_i / P_劳动)，P_劳动 = LaborFrac × Y_pot（劳动力爬坡，同农业 Boserup——
    ///   新分裂的小部落劳动不足产出受限，需长大）。
    /// CarryMult 走实体缓存（RefreshCellState 每 tick 算；测试构造未算时 fallback 实时）。</summary>
    public float FHunt(Tribe e)
    {
        float m = e.CarryMult > 0f ? e.CarryMult : TechTable.HuntingCarry(e.TechKeys);
        // 一格一实体：格产出归其唯一驻留部落独占（不再按同格部落数均分）
        float yPot = R[e.Cell] * Grid.CellAreaKm2 * m;
        if (yPot <= 0f) return 0f;
        float plabor = LaborFrac * yPot;
        return yPot * Mathf.Min(1f, e.P / Mathf.Max(1f, plabor));
    }

    /// <summary>农业潜在产出（单格，驻扎点；生产方式选择用——防小部落开垦不足永不转农死锁）。
    /// ⚠️ 2026-08-17 决策领地化：ModeModel 已改用 FFarmPotentialTerritory（领地版），本方法保留供测试/单格语义。</summary>
    public float FFarmPotential(Tribe e)
    {
        float rAgri = R[e.Cell] * IrrigFactor(e.Cell) * AlluvFactor(Grid.SoilLevel[e.Cell]);
        if (rAgri <= 0f) return 0f;
        float area = Grid.CellAreaKm2;
        float best = 0f;
        foreach (var s in TechTable.SeedKeys)
        {
            if (!e.TechKeys.Contains(s)) continue;
            var def = TechTable.Get(s);
            float phi = Phi(e.Cell, def.SeedIndex);
            best = Mathf.Max(best, def.AgriBase * phi * rAgri * area);
        }
        return best;
    }

    /// <summary>领地采集潜在（2026-08-17 决策领地化：ModeModel 转农判据与产出层同口径——
    /// band 决定"种不种"看整个领地，不是驻扎点单格。不含开垦（决策时田还没开垦，
    /// 比较"原始土地条件"值不值得种；猎物+浆果占比合计 1）。</summary>
    public float FHuntTerritory(Tribe e)
    {
        var terr = TerritoryOf(e);
        if (terr == null || terr.Count == 0) return 0f;
        var dists = TerritoryDistsOf(e);
        float A = Grid.CellAreaKm2, sum = 0f;
        for (int k = 0; k < terr.Count; k++)
        {
            int c = terr[k];
            if (R[c] <= 0f) continue;
            sum += R[c] * A * ProductionWeight(dists[k]);
        }
        return sum;
    }

    /// <summary>领地农业潜在（2026-08-17 决策领地化，劳动因子=1——防小部落开垦不足死锁；
    /// Σ 领地格 max种子(AgriBase×φ)×R×A×Irrig×Alluv×w，不含开垦）。</summary>
    public float FFarmPotentialTerritory(Tribe e)
    {
        var terr = TerritoryOf(e);
        if (terr == null || terr.Count == 0) return 0f;
        var dists = TerritoryDistsOf(e);
        float A = Grid.CellAreaKm2, sum = 0f;
        for (int k = 0; k < terr.Count; k++)
        {
            int c = terr[k];
            float rAgri = R[c] * IrrigFactor(c) * AlluvFactor(Grid.SoilLevel[c]);
            if (rAgri <= 0f) continue;
            float best = 0f;
            foreach (var s in TechTable.SeedKeys)
            {
                if (!e.TechKeys.Contains(s)) continue;
                var def = TechTable.Get(s);
                best = Mathf.Max(best, def.AgriBase * Phi(c, def.SeedIndex));
            }
            if (best > 0f) sum += best * rAgri * A * ProductionWeight(dists[k]);
        }
        return sum;
    }

    /// <summary>领地牧场潜在（2026-08-17 畜牧落地：草原格 WildLivestock 位 + livestock 能力 →
    /// 牧场潜在 = R×A×HerdMult×w（HerdMult=2：草原牧畜单位土地产出 2×采集）。
    /// 决策用——草原畜牧抬高狩猎收益 → 抑制转农（史实：草原游牧不种地）。</summary>
    public float FHerdTerritory(Tribe e)
    {
        if (!CapabilityTable.Has(this, e, CapabilityTable.Livestock)) return 0f;
        var wild = Grid.EnsureWildLivestock();
        var terr = TerritoryOf(e);
        if (terr == null || terr.Count == 0) return 0f;
        var dists = TerritoryDistsOf(e);
        float A = Grid.CellAreaKm2, sum = 0f;
        for (int k = 0; k < terr.Count; k++)
        {
            int c = terr[k];
            if (R[c] <= 0f || wild[c] == 0) continue;
            sum += R[c] * HerdMult * A * ProductionWeight(dists[k]);
        }
        return sum;
    }

    /// <summary>农业实际产出（含劳动因子 Boserup 集约化：P_农_格/P_劳动 爬坡，顶到单产上限；
    /// ×开垦率 Cultivation——2026-08-17 土地挂钩：田是逐步开垦的，0 开垦 0 农业产出，转农当 tick 靠领地采集兜底）。</summary>
    public float FFarmActual(Tribe e)
    {
        float potential = FFarmPotential(e);
        if (potential <= 0f) return 0f;
        float cult = Cultivation != null ? Cultivation[e.Cell] : 0f;
        float farmPop = CellFarmPop != null ? CellFarmPop[e.Cell] : e.P;
        float plabor = LaborFrac * potential;
        return potential * cult * Mathf.Min(1f, farmPop / Mathf.Max(1f, plabor));
    }

    /// <summary>格/种子适宜度 φ（缓存矩阵）。</summary>
    public float Phi(int cell, int seedIdx) => Suit != null ? Suit[cell, seedIdx] : 0f;

    /// <summary>狩猎人均收益（选择比较用；含耗竭项，非核算 e）。</summary>
    public static float EHunt(float yHunt, float pop) =>
        yHunt > 0f ? yHunt / (pop + HRel * yHunt) : 0f;

    /// <summary>农业人均收益（w 已含，单扣；yFarm 用潜在产出）。</summary>
    public static float EFarm(float yFarm, float pop) =>
        yFarm > 0f ? yFarm / Mathf.Max(0.001f, pop) - W : 0f;

    /// <summary>寒冷区 F 下限（§4.5：火 → 0.05·面积×3；皮毛 → 再 ×3——空间层被技术解锁；能力查询 2026-08-09）。</summary>
    public float ColdFloor(Tribe e)
    {
        if (!IsColdZone((BiomeType)Grid.Biome[e.Cell])) return 0f;
        if (!CapabilityTable.Has(this, e, CapabilityTable.Fire)) return 0f;
        float area = Grid.CellAreaKm2;
        float floor = 0.05f * area * 3f;
        if (CapabilityTable.Has(this, e, CapabilityTable.Clothing)) floor *= 3f;
        return floor;
    }

    /// <summary>格内独驻部落数（一格一实体恒 1：格产出归唯一驻留部落独占，无均分）。</summary>
    private int NTribes(int cell) => 1;

    /// <summary>生产方式并行产出（2026-08-09 用户拍板：混合经济 + 收益权重土地分配，Vic3/EU5 PM 参考）：
    /// 部落方式集 M = {hunt} ∪ {herd if livestock能力+生态位} ∪ {farm if IsFarming}；
    /// 权重 w_k = 方式潜在全地产出（R_k×A×m_k）；土地份额 s_k = w_k/Σw；
    /// 实际 F_k = w_k×s_k×min(1, P/(LaborFrac×w_k×s_k))（份额劳动爬坡）；
    /// 总产出 = ΣF_k。单方式时退化为原公式（纯猎含劳动 ✓ 兼容）。
    /// 分量缓存 FHuntLast/FHerdLast/FFarmLast（货物分解用）。</summary>
    public float FOf(Tribe e)
    {
        float m = e.CarryMult > 0f ? e.CarryMult : TechTable.HuntingCarry(e.TechKeys);
        float A = Grid.CellAreaKm2 / NTribes(e.Cell);
        float pHunt = R[e.Cell] * A * m;
        float pHerd = CapabilityTable.Has(this, e, CapabilityTable.Livestock) ? R[e.Cell] * HerdMult * A * m : 0f;
        float pFarm = e.IsFarming ? FFarmPotential(e) : 0f;
        float sw = pHunt + pHerd + pFarm;
        float floor = ColdFloor(e);
        if (sw <= 0f) return floor;
        float sHunt = pHunt / sw, sHerd = pHerd / sw, sFarm = pFarm / sw;
        float fHunt = pHunt * sHunt * Mathf.Min(1f, e.P / Mathf.Max(1f, LaborFrac * pHunt * sHunt));
        float fHerd = pHerd * sHerd * Mathf.Min(1f, e.P / Mathf.Max(1f, LaborFrac * pHerd * sHerd));
        float fFarm = pFarm * sFarm * Mathf.Min(1f, e.P / Mathf.Max(1f, LaborFrac * pFarm * sFarm));
        e.FHuntLast = fHunt; e.FHerdLast = fHerd; e.FFarmLast = fFarm;   // 分量缓存（货物分解）
        return Mathf.Max(fHunt + fHerd + fFarm, floor);
    }

    // ── 环境判定（§6.1 硬门槛判定函数）──

    public bool EnvMatches(int cell, string[] env)
    {
        if (env == null || env.Length == 0) return true;
        var b = (BiomeType)Grid.Biome[cell];
        foreach (var e in env)
        {
            switch (e)
            {
                case "coast": if (Grid.IsCoast(cell)) return true; break;
                case "river": if (b == BiomeType.Riparian) return true; break;
                case "grass": if (b is BiomeType.HotSteppe or BiomeType.ColdSteppe or BiomeType.TropicalSavanna) return true; break;
                case "plain":
                    if (b is BiomeType.ContinentalHot or BiomeType.ContinentalWarm or BiomeType.ContinentalDry
                        or BiomeType.HotSteppe or BiomeType.ColdSteppe) return true; break;
                case "mediterranean": if (b is BiomeType.MediterraneanHot or BiomeType.MediterraneanCool) return true; break;
                case "monsoon": if (b is BiomeType.TropicalMonsoon or BiomeType.MonsoonSubtropical) return true; break;
                case "humidsubtrop": if (b is BiomeType.HumidSubtropical or BiomeType.Oceanic or BiomeType.TropicalRainforest) return true; break;
                case "coldtemperate":
                    if (b is BiomeType.ContinentalHot or BiomeType.ContinentalWarm or BiomeType.ContinentalDry
                        or BiomeType.Subarctic or BiomeType.Alpine) return true; break;
                case "coldzone": if (IsColdZone(b)) return true; break;
                case "irrigation": if (b == BiomeType.Riparian || Grid.LakeLevel[cell] > 0) return true; break;
            }
        }
        return false;
    }

    /// <summary>发明环境修正 env_i：匹配 1.0 / 不匹配非硬门槛 0.3；皮毛衣物寒冷区 ×2。</summary>
    public float EnvFactor(int cell, TechDef def)
    {
        if (def.InvEnv.Length == 0 || EnvMatches(cell, def.InvEnv)) return 1f;
        if (def.Key == TechTable.Clothing && IsColdZone((BiomeType)Grid.Biome[cell])) return 2f;
        return EnvMismatchFactor;
    }

    /// <summary>全球总人口（Σ 存活实体）。</summary>
    public float TotalPopulation()
    {
        float s = 0f;
        for (int i = 0; i < Tribes.Count; i++)
            if (!Tribes[i].Dead) s += Tribes[i].P;
        return s;
    }

    /// <summary>运行时不变量校验（2026-08-19 防隐晦 bug：把"读档分叉才发现"提前到"运行即报"）。
    /// 数组长度一致性、值域、归属索引、一格一实体、确定性纪律；返回错误列表（空 = 通过）。
    /// 供诊断/测试路径调用（O(n)，非演化热路径）。</summary>
    public List<string> ValidateInvariants()
    {
        var errs = new List<string>();
        int n = Grid?.N ?? -1;
        if (n < 0) { errs.Add("Grid 未初始化"); return errs; }
        void CheckLen(string name, Array a)
        {
            if (a != null && a.Length != n) errs.Add($"{name}.Length={a.Length} != N={n}");
        }
        CheckLen("R", R); CheckLen("CellF", CellF); CheckLen("CellPop", CellPop); CheckLen("CellFarmPop", CellFarmPop);
        CheckLen("Cultivation", Cultivation); CheckLen("CellOwner", CellOwner); CheckLen("CellBestOwner", CellBestOwner);
        CheckLen("CellBestInf", CellBestInf); CheckLen("CellOwnerInf", CellOwnerInf); CheckLen("LockedUntil", LockedUntil);
        CheckLen("BfsStamp", BfsStamp);
        if (R != null) for (int i = 0; i < n; i++) if (R[i] < 0f) { errs.Add($"R[{i}]<0"); break; }
        if (Cultivation != null) for (int i = 0; i < n; i++) if (Cultivation[i] < 0f || Cultivation[i] > 1f) { errs.Add($"Cultivation[{i}]={Cultivation[i]} 超出[0,1]"); break; }
        if (CellOwner != null) for (int i = 0; i < n; i++) if (CellOwner[i] < -1 || CellOwner[i] >= NextTribeId) { errs.Add($"CellOwner[{i}]={CellOwner[i]} 越界(NextTribeId={NextTribeId})"); break; }
        // 一格一实体：CellTribes 与 Tribe.Cell 双向一致（存活实体）
        if (CellTribes != null) for (int i = 0; i < n; i++) { var e = CellTribes[i]; if (e != null && (e.Cell != i || e.Dead)) errs.Add($"CellTribes[{i}] 与实体不一致(e.Cell={e.Cell},Dead={e.Dead})"); }
        if (Tribes != null) foreach (var e in Tribes)
        {
            if (e.Dead) continue;
            if (e.Cell < 0 || e.Cell >= n) { errs.Add($"实体{e.Id} Cell={e.Cell} 越界"); continue; }
            if (CellTribes != null && CellTribes[e.Cell] != e) errs.Add($"实体{e.Id} 不在 CellTribes[{e.Cell}]");
        }
        if (Rng != null && Rng is not DeterministicRandom) errs.Add("Rng 非 DeterministicRandom（确定性纪律被破坏）");
        return errs;
    }

    // ══════════════════════════════════════════════════════════════════
    // 影响力场模型（2026-08-10 定稿，5 km² 口径）
    //   归属 = argmax(P×CarryMult×w(d))，w = 紧支撑平滑核，粘性 1.15；
    //   领地 = 归属格集合；F = Σ 领地格 min(需求份额, Cap×w)；存量耗竭→饿→迁移。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>紧支撑平滑核：w(d) = (1−d/R)^1.5（d=格步数，d≥R 严格 0；2026-08-17 陡化修正）。
    /// ⚠️ 2026-08-17 陡化：d=0 权重 1（家）、d=1 半衰 0.54、d=2 保留 0.19——驻扎格覆盖需邻 P×M ≥ 2.1×自己
    ///   （含粘性）——家门口稳定（foraging site catchment，Binford）；d≥R 严格 0（紧支撑不变）。
    /// ⚠️ 2026-08-19 曾扩展远格弱权重（d=3-5）→ 领地产出去中心化 → 采集经济增强、农业门槛被压
    ///   （n128 实测 farm 412→74 崩溃）——回退（领地大小保持 3 跳；扩散靠殖民机制，不靠领地核）。</summary>
    private static readonly float[] InfluenceWeightLUT = { 1f, 0.544f, 0.192f, 0f };   // (1−d/3)^1.5 查表
    public static float InfluenceWeight(float d)
    {
        // ⚠️ 2026-08-17 修正：衰减陡化（用户质疑：弱 band 驻扎格不该被轻易覆盖——家门口应优势明显）。
        //   旧版 (1−d²/r²)² 在 d=1 仍 0.79（邻居仅需大 1.27 倍就覆盖——粘性 1.15 后 1.46 倍）。
        //   改 (1−d/r)^1.5：d=0 权重 1（家）、d=1 半衰 0.54、d=2 保留 0.19——驻扎格覆盖需
        //   邻 P×M ≥ 2.1×自己（含粘性）——家门口稳定（foraging site catchment 衰减，Binford）；
        //   d=2 不过度削弱（远格仍贡献——世界密度不塌）。d≥R 严格 0（紧支撑不变）。
        int di = (int)d;
        return di >= 0 && di < InfluenceWeightLUT.Length ? InfluenceWeightLUT[di] : 0f;
    }

    /// <summary>领地**产出**距离权重（2026-08-18 用户拍板方案 B：环形面积加权）。
    /// ⚠️ 与 ownership 用的 InfluenceWeight（距离衰减）**分离**——根因：旧产出层复用
    /// 归属用的紧支撑核 (1−d/3)^1.5，d≥3 归零，导致"领地大但远格产能=0"→ 大领地喂不饱
    /// band → P=5 死锁（split 永不触发）。史实修正：中央营地可食用环面随距离增大
    /// （hex ring d=6d 格，∝(2d+1)），远缘贡献总产出大头；仅以微旅行折减 (1−d/Rmax) 收边。
    /// wProd(d)=(2d+1)·(1−d/Rmax)，Rmax=5（~史实 5km 利用半径边缘）→ 权重 1,2.4,3,2.8,1.8,0。
    /// ⚠️ 2026-08-19 曾扩到 Rmax=6 → 产出去中心化 → 农业门槛被压（farm 崩溃）——回退。
    /// 只用于 采集/畜牧/农田 的领地潜在与实际产出；归属/影响力场仍用 InfluenceWeight（保家门口稳定）。</summary>
    private static readonly float[] ProductionWeightLUT = { 1f, 2.4f, 3f, 2.8f, 1.8f, 0f };   // (2d+1)(1−d/5)
    public static float ProductionWeight(float d)
    {
        int di = (int)d;
        return di >= 0 && di < ProductionWeightLUT.Length ? ProductionWeightLUT[di] : 0f;
    }

    /// <summary>猎物占比（biome 相关；浆果 = 1−占比）。草地/萨瓦纳猎物多（草食动物），密林/湿润浆果采集为主。
    /// 2026-08-17 采集拆分（用户拍板：采集量 = 猎物 + 浆果）。</summary>
    public static float PreyFrac(BiomeType b) => b switch
    {
        BiomeType.HotSteppe or BiomeType.ColdSteppe or BiomeType.TropicalSavanna => 0.7f,
        BiomeType.TropicalRainforest or BiomeType.TropicalMonsoon or BiomeType.MonsoonSubtropical
            or BiomeType.HumidSubtropical or BiomeType.Oceanic => 0.35f,   // 密林/湿润：浆果采集为主
        _ => 0.5f,
    };

    /// <summary>影响力场重算：每格归属 = argmax(P×M×w(d))；粘性：非 owner 需超现 owner×1.15 才易主。
    /// band 驱动（每 band 写半径 R 内格，O(band×28)）；确定性（固定遍历顺序）。</summary>
    public void RebuildInfluence()
    {
        int n = Grid.N;
        Array.Fill(CellBestOwner, -1);
        Array.Clear(CellBestInf, 0, n);
        Array.Clear(CellOwnerInf, 0, n);
        // ⚠️ 2026-08-17 修复：活实体 Id 集（死残留清理的正确映射——旧版 Tribes[CellOwner[c]]
        //   用 Id 当索引——读档后 Id 有空洞（Tribes 只含存活）→ 错位访问 → 死 band 的影响力
        //   残留 → 幽灵势力色块（用户怀疑成立：band 消失但影响力没清）
        _liveIdSet ??= new HashSet<int>();
        _liveIdSet.Clear();
        for (int i = 0; i < Tribes.Count; i++)
            if (!Tribes[i].Dead) _liveIdSet.Add(Tribes[i].Id);
        for (int i = 0; i < Tribes.Count; i++)
        {
            var e = Tribes[i];
            if (e.Dead) continue;
            float M = e.CarryMult > 0f ? e.CarryMult : TechTable.HuntingCarry(e.TechKeys);
            float strength = e.P * M;
            if (strength <= 0f) continue;
            BfsRadius(e.Cell, InfluenceRadius, (c, d) =>
            {
                float w = InfluenceWeight(d);
                if (w <= 0f) return;
                float inf = strength * w;
                if (inf > CellBestInf[c]) { CellBestInf[c] = inf; CellBestOwner[c] = e.Id; }
                if (CellOwner[c] == e.Id && inf > CellOwnerInf[c]) CellOwnerInf[c] = inf;
            }, landOnly: true);   // 影响圈只走陆地可居格（R>0）——海洋不进领地，2026-08-10
        }
        for (int c = 0; c < n; c++)
        {
            // ⚠️ 2026-08-17：死残留清理前置（含锁定格——锁定格不重算，但归属已死仍要清——
            //   幽灵势力 = 归属不存在的 band）
            if (CellOwner[c] >= 0 && !_liveIdSet.Contains(CellOwner[c]))
            {
                CellOwner[c] = -1;
                continue;
            }
            if (LockedUntil != null && LockedUntil[c] > Tick) continue;   // 实控锁定格：武力既成事实，场不重算（2026-08-10 冲突机制）
            int best = CellBestOwner[c];
            if (best < 0)
            {
                continue;
            }
            int cur = CellOwner[c];
            if (cur == best) continue;
            if (cur < 0 || CellBestInf[c] > CellOwnerInf[c] * Stickiness)
                CellOwner[c] = best;
        }
        RebuildTerritory();
    }

    /// <summary>领地格数组安全访问（Id 索引——按 MaxId 动态扩容；2026-08-17 索引体系修复：
    /// 读档/分裂后实体 Id 递增有空洞——固定 4096 容量在 Id 超限时越界，统一走安全访问）。</summary>
    public List<int> TerritoryOf(Tribe e)
    {
        if (TerritoryCells == null) EnsureTerritory();
        if (e.Id >= TerritoryCells.Length) EnsureTerritoryCapacity(e.Id + 256);
        return TerritoryCells[e.Id];
    }

    /// <summary>领地距离数组安全访问（同 TerritoryOf 扩容语义）。</summary>
    public List<byte> TerritoryDistsOf(Tribe e)
    {
        if (TerritoryDists == null) EnsureTerritory();
        if (e.Id >= TerritoryDists.Length) EnsureTerritoryCapacity(e.Id + 256);
        return TerritoryDists[e.Id];
    }

    /// <summary>两部落领地**最小边界格距**（格步数近似：大圆 km ÷ 胞边长；不相接触返回 int.MaxValue）。
    /// 确定性：固定遍历序（领地格列表序 × 邻格表序）。贸易运输成本用（黑曜石随距衰减史实）。</summary>
    public static int BoundaryDist(CivSimContext ctx, Tribe a, Tribe b)
    {
        var terrA = ctx.TerritoryOf(a);
        var terrB = ctx.TerritoryOf(b);
        if (terrA == null || terrB == null || terrA.Count == 0 || terrB.Count == 0) return int.MaxValue;
        float cellKm = Mathf.Sqrt(ctx.Grid.CellAreaKm2);
        if (cellKm <= 0f) return 1;
        int best = int.MaxValue;
        foreach (var ca in terrA)
            foreach (var cb in terrB)
            {
                int d = Mathf.Max(1, (int)Mathf.Round(ctx.Grid.DistKm(ca, cb) / cellKm));
                if (d < best) best = d;
            }
        return best;
    }

    /// <summary>领地边界接触（边界格距 == 1：A 领地格有**邻格**属 B 领地——直接邻接）。
    /// ⚠️ 2026-08-18 共享判定：酋邦凝聚（ChiefdomModel）与贸易（TradeModel）同用——防"两套实现分叉"
    ///   （T04 类缺陷根治惯例）。确定性：固定遍历序；HashSet 仅做 Contains 查询（遍历序无关）。</summary>
    public static bool TerritoryTouches(CivSimContext ctx, Tribe a, Tribe b)
    {
        var terrA = ctx.TerritoryOf(a);
        var terrB = ctx.TerritoryOf(b);
        if (terrA == null || terrB == null || terrA.Count == 0 || terrB.Count == 0) return false;
        var setB = new HashSet<int>(terrB);
        foreach (var c in terrA)
            foreach (int nb in ctx.Grid.Neighbors[c])
                if (setB.Contains(nb)) return true;
        return false;
    }

    /// <summary>惰性确保领地索引数组存在（构造场景/读档路径可能未初始化）。</summary>
    public void EnsureTerritory()
    {
        if (TerritoryCells != null) return;
        int cap = Math.Max(4096, Tribes.Count + 256);
        TerritoryCells = new List<int>[cap];
        TerritoryDists = new List<byte>[cap];
        for (int i = 0; i < cap; i++)
        {
            TerritoryCells[i] = new List<int>();
            TerritoryDists[i] = new List<byte>();
        }
    }

    /// <summary>领地索引重建：每 band 的领地格 = 归属格 ∩ 其影响圈（BFS 半径 R 内）。距离入 TerritoryDists。
    /// ⚠️ 2026-08-17 修：索引统一用 **e.Id**（分配器/开垦/采集全按 Id 取）——旧版按列表索引填，
    ///   演化中 Id==索引（连续分配无 Remove）正确，但读档后列表只含存活实体、Id 不连续 → 错位
    ///   （T04 读档续跑分叉的可能帮凶之一）。</summary>
    public void RebuildTerritory()
    {
        EnsureTerritory();
        for (int i = 0; i < Tribes.Count; i++)
        {
            var e = Tribes[i];
            if (e.Dead) continue;
            if (e.Id >= TerritoryCells.Length) EnsureTerritoryCapacity(e.Id + 256);
            TerritoryCells[e.Id].Clear();
            TerritoryDists[e.Id].Clear();
        }
        for (int i = 0; i < Tribes.Count; i++)
        {
            var e = Tribes[i];
            if (e.Dead) continue;
            var terr = TerritoryOf(e);
            var dists = TerritoryDistsOf(e);
            BfsRadius(e.Cell, InfluenceRadius, (c, d) =>
            {
                if (CellOwner[c] == e.Id)
                {
                    terr.Add(c);
                    dists.Add((byte)d);
                }
            }, landOnly: true);   // 领地 = 陆地可达域（2026-08-10）
        }
    }

    private void EnsureTerritoryCapacity(int size)
    {
        int old = TerritoryCells.Length;
        if (size <= old) return;
        var tc = new List<int>[size];
        var td = new List<byte>[size];
        Array.Copy(TerritoryCells, tc, old);
        Array.Copy(TerritoryDists, td, old);
        for (int i = old; i < size; i++)
        {
            tc[i] = new List<int>();
            td[i] = new List<byte>();
        }
        TerritoryCells = tc;
        TerritoryDists = td;
    }

    /// <summary>领地建筑分配产出（2026-08-17 用户拍板：凹化 + 等边际；2026-08-17 畜牧接入）。
    /// 建筑产出 F_i(n) = P_i·n/(D_i+n)，D_i = LF_类型·P_i——需求与潜在成正比但类型参数不同：
    /// 采集/牧场 LF=0.1（粗放，Sahlins ~15-20h/周）+ 农田 LF=0.2（劳动密集 ~40h+/周）。
    /// 牧场 = 草原格（WildLivestock 位）的"高潜在采集"（HerdMult=2）——同 LF 档内按潜在比例
    /// 自动多投（草原牧畜比采集划算 → 工人自然流向，无需 IsHerding 状态）。
    /// 等边际闭式解（LF 两档分段 water-filling，O(k) 无排序、无迭代）：
    ///   段 A（仅采集档激活，μ∈(5,10]）：√μ = √0.1·ΣPc / (N + 0.1·ΣPc)
    ///   段 B（采集+农田激活，μ≤5）：  √μ = (√0.1·ΣPc + √0.2·ΣPf) / (N + 0.1·ΣPc + 0.2·ΣPf)
    /// 每格 n_i = √LF·P_i/√μ − LF·P_i（max(0,·) 截断——未激活建筑 0 工人）；
    /// FBerryLast 按浆果占比拆分（仅采集部分）；FHerdLast 独立缓存（羊毛副产）。
    /// 每 tick 派生重算、不入档、无 Rng——读档续跑无分叉。
    /// 采集潜在 = R·A·w·[(1−0.5·开垦)·猎物占比 + (1−开垦)·浆果占比]；
    /// 牧场潜在 = R·A·HerdMult·w·(1−开垦)（草原位——草场被农田直接替代，与浆果同敏感度；
    ///   2026-08-17 用户拍板：畜牧也是占用土地的建筑，无"游牧与田不冲突"豁免）；
    /// 农田潜在 = max种子(AgriBase·φ)·R·A·Irrig·Alluv·开垦·w。</summary>
    public float AllocateAndProduce(Tribe e)
    {
        var terr = TerritoryOf(e);
        if (terr == null || terr.Count == 0) return 0f;
        var dists = TerritoryDistsOf(e);
        float A = Grid.CellAreaKm2;
        bool isFarm = e.IsFarming;
        bool canHerd = CapabilityTable.Has(this, e, CapabilityTable.Livestock);
        byte[] wild = canHerd ? Grid.EnsureWildLivestock() : null;
        // 第一遍：Σ 采集/牧场/农田潜在 + 浆果潜在（分配只需总量，逐格 n 按潜在比例）
        float sumPc = 0f, sumPh = 0f, sumPf = 0f, sumBerry = 0f;
        for (int k = 0; k < terr.Count; k++)
        {
            int c = terr[k];
            if (R[c] <= 0f) continue;
            float w = ProductionWeight(dists[k]);
            float cult = Cultivation != null ? Cultivation[c] : 0f;
            float frac = PreyFrac((BiomeType)Grid.Biome[c]);
            float pc = R[c] * A * w * ((1f - PreyHabitatLoss * cult) * frac + (1f - cult) * (1f - frac));
            if (pc > 0f) { sumPc += pc; sumBerry += R[c] * A * w * (1f - cult) * (1f - frac); }
            if (canHerd && wild[c] != 0) sumPh += R[c] * HerdMult * A * w * (1f - cult);   // 草场被农田直接替代
            if (isFarm && cult > 0f)
            {
                float rAgri = R[c] * IrrigFactor(c) * AlluvFactor(Grid.SoilLevel[c]);
                if (rAgri > 0f)
                {
                    float best = 0f;
                    foreach (var s in TechTable.SeedKeys)
                    {
                        if (!e.TechKeys.Contains(s)) continue;
                        var def = TechTable.Get(s);
                        best = Mathf.Max(best, def.AgriBase * Phi(c, def.SeedIndex));
                    }
                    if (best > 0f) sumPf += best * rAgri * A * cult * w;
                }
            }
        }
        float sumCollect = sumPc + sumPh;   // 采集档总量（采集+牧场，LF 均 0.1）
        float total = sumCollect + sumPf;
        if (total <= 0f) return 0f;
        float N = e.P;
        // 等边际 μ（LF 两档分段闭式）
        // 激活条件：建筑激活 ⟺ μ ≤ 1/LF_类型（采集/牧场 10 / 农田 5）；段 A 解 μA 若 ≥ 5 → 农田不激活
        float sqrtMu;
        float sqrtMuA = Mathf.Sqrt(LaborFrac) * sumCollect / (N + LaborFrac * sumCollect);   // 段 A：仅采集档
        if (sqrtMuA >= Mathf.Sqrt(1f / LaborFracFarm))   // √μA ≥ √5（μ ≥ 5）→ 农田边际 5 不激活
            sqrtMu = sqrtMuA;
        else
            sqrtMu = (Mathf.Sqrt(LaborFrac) * sumCollect + Mathf.Sqrt(LaborFracFarm) * sumPf)
                   / (N + LaborFrac * sumCollect + LaborFracFarm * sumPf);               // 段 B：混合（√μB ≤ √5 自动保证）
        // 第二遍：逐格分配 n_i = √LF·P_i/√μ − LF·P_i，产出 F_i = P_i·n_i/(D_i+n_i)
        float fHunt = 0f, fHerd = 0f, fFarm = 0f;
        for (int k = 0; k < terr.Count; k++)
        {
            int c = terr[k];
            if (R[c] <= 0f) continue;
            float w = ProductionWeight(dists[k]);
            float cult = Cultivation != null ? Cultivation[c] : 0f;
            float frac = PreyFrac((BiomeType)Grid.Biome[c]);
            float pc = R[c] * A * w * ((1f - PreyHabitatLoss * cult) * frac + (1f - cult) * (1f - frac));
            if (pc > 0f)
            {
                float n = Mathf.Max(0f, Mathf.Sqrt(LaborFrac) * pc / sqrtMu - LaborFrac * pc);   // 未激活建筑 = 0 工人
                fHunt += pc * n / (LaborFrac * pc + n);
            }
            if (canHerd && wild[c] != 0)
            {
                float ph = R[c] * HerdMult * A * w * (1f - cult);   // 草场被农田直接替代
                if (ph > 0f)   // ⚠️ 2026-08-17：开垦 1 的格 ph=0 → 0×0/0 = NaN 污染 FLast（T03 分叉根因）
                {
                    float n = Mathf.Max(0f, Mathf.Sqrt(LaborFrac) * ph / sqrtMu - LaborFrac * ph);
                    fHerd += ph * n / (LaborFrac * ph + n);
                }
            }
            if (isFarm && cult > 0f)
            {
                float rAgri = R[c] * IrrigFactor(c) * AlluvFactor(Grid.SoilLevel[c]);
                if (rAgri > 0f)
                {
                    float best = 0f;
                    foreach (var s in TechTable.SeedKeys)
                    {
                        if (!e.TechKeys.Contains(s)) continue;
                        var def = TechTable.Get(s);
                        best = Mathf.Max(best, def.AgriBase * Phi(c, def.SeedIndex));
                    }
                    if (best > 0f)
                    {
                        float pf = best * rAgri * A * cult * w;
                        // ⚠️ 2026-08-17：w=0 边界格（影响圈边缘 d=R）→ pf=0 → 0×0/0 = NaN 污染 FLast（T03 分叉根因；采集 pc>0、牧场 ph>0 已有检查，农田漏了）
                        if (pf > 0f)
                        {
                            float n = Mathf.Max(0f, Mathf.Sqrt(LaborFracFarm) * pf / sqrtMu - LaborFracFarm * pf);
                            fFarm += pf * n / (LaborFracFarm * pf + n);
                        }
                    }
                }
            }
        }
        e.FBerryLast = sumPc > 0f ? fHunt * (sumBerry / sumPc) : 0f;   // 浆果实际（仅采集部分，牧场无浆果）
        e.FHerdLast = fHerd;
        e.FFarmLast = fFarm;
        return fHunt;   // ⚠️ 2026-08-17 修双计：返回**采集分量**（fHerd/fFarm 已入实体缓存；
                        //   旧版返回总产出 → HarvestModel 的 FLast = FHuntLast+FFarmLast+FHerdLast 双计农业/畜牧）
    }

    /// <summary>BFS 半径 maxDepth（格步数），确定性：格遍历顺序 = 邻接表顺序。visit(cell, depth)。</summary>
    /// landOnly：只走 R>0 陆地可居格（影响圈/领地不进海洋——2026-08-10 修复：此前领地含 R=0 格致分裂驻海洋）。</summary>
    internal void BfsRadius(int start, int maxDepth, Action<int, int> visit, bool landOnly = false)
    {
        BfsStampValue++;
        int sv = BfsStampValue;
        _bfsQ ??= new Queue<int>();
        _bfsDQ ??= new Queue<int>();
        _bfsQ.Clear(); _bfsDQ.Clear();
        BfsStamp[start] = sv;
        _bfsQ.Enqueue(start); _bfsDQ.Enqueue(0);
        visit(start, 0);
        while (_bfsQ.Count > 0)
        {
            int c = _bfsQ.Dequeue();
            int d = _bfsDQ.Dequeue();
            if (d >= maxDepth) continue;
            foreach (int nb in Grid.Neighbors[c])
                if (BfsStamp[nb] != sv && (!landOnly || R[nb] > 0f))
                {
                    BfsStamp[nb] = sv;
                    _bfsQ.Enqueue(nb); _bfsDQ.Enqueue(d + 1);
                    visit(nb, d + 1);
                }
        }
    }

    /// <summary>本 tick 归属是否可迁移：饿（F<D）且领地内无可扩（领地格数已达影响圈内无主格上限）——简化：F<P 且连续饿。</summary>
    public bool IsStarving(Tribe e) => e.FLast < e.P * 0.999f;
}
