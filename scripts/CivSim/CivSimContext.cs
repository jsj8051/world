using System;
using System.Collections.Generic;
using Godot;
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
    public List<CivEntity>[] CellTribes;   // 每格实体列表（格=地理容器，一格多实体）
    public List<CivEntity> Entities;       // 全部存活实体
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
    public float[] Stock;            // 格存量（人当量食物；0=海洋/不可居；S₀ = StockYears×R×5）
    public int[] CellOwner;          // 格归属 band id（-1=无主；归属唯一，其他 band 禁入）
    public int[] CellBestOwner;      // 影响力场重算暂存：每格当前最强影响力 band
    public float[] CellBestInf;      // 影响力场重算暂存：最强影响力值
    public float[] CellOwnerInf;     // 现 owner 的影响力（粘性比较基准；本 tick 开头）
    public List<int>[] TerritoryCells;   // 每 band 领地格列表（CellOwner 反查派生，RebuildTerritory 重建）
    public List<byte>[] TerritoryDists;  // 每 band 领地格到驻扎点距离（0-3，w 加权用）
    private Queue<int> _bfsQ;            // BFS 复用队列（GC 优化）
    private Queue<int> _bfsDQ;

    // ── 自然层派生缓存（确定性重建，不存档）──
    public byte[] WildCrops;   // WildCrops 位（grid.EnsureWildCrops 惰性）
    public float[,] Suit;      // 每格每种子适宜度 φ（WildCropsSystem.Suitability 缓存）

    // ── 演化统计（诊断/输出）──
    public int Fissions;
    public int Migrations;
    public int Conflicts;   // 冲突计数（2026-08-10 冲突机制）
    public int CultureKeyCount;         // 文化标签 key 计数器（分裂分化分配新 key，如 "cult_12"）
    public int CultureGroupKeyCount;    // 文化群 key 计数器（2026-08-07 与文化标签分开——语言大群独立 key 空间，防标签挤占）
    public int ReligionKeyCount;        // 宗教派别 key 计数器（起源/分裂分化分配新 key，如 "relig_3"）
    public int FirstFarmTick = -1;      // 首转农 tick（终止条件锚点）
    public int TerritoryLastRebuild = -1;   // 最近凝聚重算 tick（TerritoryModel 频率守卫）
    public int[] BfsStamp;
    public int BfsStampValue;
    public int NextEntityId;   // 实体 Id 分配计数器（2026-08-10：独立于 Entities.Count——存档只存活实体，Count 会分叉）
    public string[] KeyBuf;    // 科技遍历排序缓冲（SpreadTech 复用，无分配；2026-08-10 确定性）
    public int[] LockedUntil;  // 武力夺取格锁定到期 tick（-1=无锁定；2026-08-10 冲突机制——锁定内场不重算）

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
    public const float SplitPop = 50f;               // 分裂阈值（2026-08-10 影响力场模型：band 量级——旧 300 是格内 8 部落口径，新模型 band 平衡人口 ~30-50）
    public const int MaxTribesPerCell = 8;            // 格内实体上限（超限不分裂，迁徙优先）
    public const float SplitShare = 0.45f;            // 分裂新实体带走比例
    public const float FissionTensionStart = 50f;   // 规模张力起算点（= SplitPop；band 量级，2026-08-10）
    public const float FissionTensionSpan = 40f;    // 张力封顶跨度（50+40=90 → 张力 1.0）
    public const int TerritoryRebuildEvery = 10;     // 凝聚重算间隔 tick（Union-Find，~35 万边/次）
    public const float TerritorySpreadMult = 1.5f;   // 同领地传播乘数（领地整合加成）
    public const float CrossBorderSpreadMult = 0.5f; // 跨领地边界传播乘数（软冲突）
    public const float TerritoryDriftDiv = 0.5f;     // 领地内分裂漂变概率减半（凝聚自稳）
    public const float StorageFamineRelief = 0.6f;   // 存储饿死缓冲：缺口衰减系数（Testart 分水岭 2026-08-09）

    // ── 货物系统（2026-08-09：生产方式副产品，累积入档 v7；贸易期接物物交换）──
    public const int GoodsLeather = 0, GoodsWool = 1, GoodsStraw = 2;   // Goods[] 索引
    public const float LeatherRate = 0.10f;   // 狩猎产出 → 皮革（★ 标定）
    public const float WoolRate = 0.15f;      // 畜牧产出 → 羊毛（★ 标定）
    public const float StrawRate = 0.05f;     // 农业产出 → 秸秆（★ 标定）
    public const float HerdMult = 2.0f;       // 畜牧单位土地产出倍率（"少许土地产生食物"；★ 标定）
    public const float MigrateThreshold = 0.75f;      // 饱和迁徙阈值（格 P_格/F_格）
    public const float MigrateShare = 0.5f;           // 饱和迁徙分出比例
    public const float ScoutChance = 0.02f;           // 探路迁徙概率/tick
    public const float ScoutMinPop = 100f;            // 探路最小实体人口
    public const float ScoutShare = 0.3f;             // 探路迁出比例
    // ── 两层模型（2026-08-17 定稿）──
    public const float IrrigMult = 5f;                // 灌溉因子：近水格农业 ×5（河谷尖峰来源；★ 待校准）
    public const float LaborFrac = 0.1f;              // P_劳动 = 0.1×潜在产出（开垦满需人数；★ 待校准）
    public const float TargetMedianDensity = 0.3f;    // k 标定锚：陆地 R 中位数 ≈ 0.3 人/km²（Binford 量级）
    public const float AssimilateRate = 0.03f;        // 格级同化速率（文化/宗教；2026-08-07 0.3→0.1→0.03：同化放缓 → 弱文化/新派别有存活窗口）
    public const float ReligionSpreadRate = 0.02f;    // 宗教传播速率/tick/接触（只向高阶）
    public const float ReligionUpgradeRate = 0.05f;   // 泛灵→萨满升级速率/tick
    public const float CultureDriftChance = 0.05f;    // 分裂时新文化群/派别概率（5%；2026-08-07 0.5%→1%→2%→5%：分化加强 → 区域多样性）
    public const float SeedPressure = 0.7f;           // 种子压力触发阈值（格人口 P_格/K_格）
    public const float SeedInvProb = 0.005f;          // 种子基础发明概率/tick（起源区少数）
    public const float EnvMismatchFactor = 0.3f;      // 发明 env_i：环境不匹配但非硬门槛
    public const int OriginDistMin = 12;              // 起源两两最小球面格距（≈1300 km）
    public const float OriginPop = 50f;   // 起源 band 人口（band 量级；5 km² 格超载靠快速扩张消化，2026-08-10）
    public const int TerminateAfterAgri = 100;        // 首转农 +100 ticks 结束
    public const int MaxTicksNoAgri = 500;            // 兜底：无农 500 ticks 停止（天然灭绝星球）
    // ── 影响力场模型常量（2026-08-10 定稿）──
    public const int InfluenceRadius = 3;        // 影响范围（格步数；物理标定 foraging 单程 15 km / 5 km² 格）
    public const float Stickiness = 1.15f;       // 归属粘性：非 owner 需超现 owner 影响力 ×1.15 才易主
    public const float RegenRate = 0.2f;         // 存量再生率（每 tick，向 K 线性回归；S=0 也能恢复——logistic 死锁已否）
    public const float CapRate = 0.2f;           // 采集上限比例（每 tick 最多采存量 α；2026-08-10 标定：0.05→0.2——平衡产出 F=α·S*·Σw ≈ R×领地面积，band 平衡 ~28 人）
    public const float StockYears = 100f;        // 存量深度（K = StockYears×R×5 人当量；2026-08-10 标定 20→100——平衡人口 F≈R×领地面积，band 平衡 ~50 人）
    // ── 冲突机制（2026-08-10 定稿 §十五）：归属两条途径——和平（场 argmax+粘性）/ 武力（冲突强制易主+实控锁定）──
    public const int ConflictLockTicks = 8;      // 实控锁定：武力夺取格 N tick 内场不重算（胜者持续产粮→人口增长窗口）
    public const float ConflictChance = 0.01f;   // 每僵持格/tick 触发概率（低频——旧石器战争是偶发事件，全演化 0~十几次）
    public const float ConflictLossChallenger = 0.08f;  // 胜者（挑战者）损耗比例（胜者损失小）
    public const float ConflictLossOwner = 0.20f;       // 败者（owner）损耗比例（败者损失大）
    public const float ConflictPlunderRate = 0.3f;      // 掠夺：败者易主格存量转移比例（即时资源收益）
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
            case BiomeType.Alpine:   return 0.2f;    // 山脉难翻越
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
    public float FHunt(CivEntity e)
    {
        float m = e.CarryMult > 0f ? e.CarryMult : TechTable.HuntingCarry(e.TechKeys);
        int nTribes = 0;
        var tl = CellTribes != null ? CellTribes[e.Cell] : null;
        if (tl != null)
            foreach (var o in tl)
                if (!o.Dead) nTribes++;
        float yPot = R[e.Cell] * Grid.CellAreaKm2 / Mathf.Max(1, nTribes) * m;
        if (yPot <= 0f) return 0f;
        float plabor = LaborFrac * yPot;
        return yPot * Mathf.Min(1f, e.P / Mathf.Max(1f, plabor));
    }

    /// <summary>农业潜在产出（劳动因子=1；生产方式选择用——防小部落开垦不足永不转农死锁）。</summary>
    public float FFarmPotential(CivEntity e)
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

    /// <summary>农业实际产出（含劳动因子 Boserup 集约化：P_农_格/P_劳动 爬坡，顶到单产上限）。
    /// farmPop 走格缓存（RefreshCellState 两遍循环算，劳动因子用当 tick 完整值）。</summary>
    public float FFarmActual(CivEntity e)
    {
        float potential = FFarmPotential(e);
        if (potential <= 0f) return 0f;
        float farmPop = CellFarmPop != null ? CellFarmPop[e.Cell] : e.P;
        float plabor = LaborFrac * potential;
        return potential * Mathf.Min(1f, farmPop / Mathf.Max(1f, plabor));
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
    public float ColdFloor(CivEntity e)
    {
        if (!IsColdZone((BiomeType)Grid.Biome[e.Cell])) return 0f;
        if (!CapabilityTable.Has(this, e, "fire")) return 0f;
        float area = Grid.CellAreaKm2;
        float floor = 0.05f * area * 3f;
        if (CapabilityTable.Has(this, e, "clothing")) floor *= 3f;
        return floor;
    }

    /// <summary>格内活部落数（土地份额分母；0 → 1 防除零）。</summary>
    private int NTribes(int cell)
    {
        int n = 0;
        var tl = CellTribes != null ? CellTribes[cell] : null;
        if (tl != null)
            foreach (var o in tl)
                if (!o.Dead) n++;
        return Mathf.Max(1, n);
    }

    /// <summary>生产方式并行产出（2026-08-09 用户拍板：混合经济 + 收益权重土地分配，Vic3/EU5 PM 参考）：
    /// 部落方式集 M = {hunt} ∪ {herd if livestock能力+生态位} ∪ {farm if IsFarming}；
    /// 权重 w_k = 方式潜在全地产出（R_k×A×m_k）；土地份额 s_k = w_k/Σw；
    /// 实际 F_k = w_k×s_k×min(1, P/(LaborFrac×w_k×s_k))（份额劳动爬坡）；
    /// 总产出 = ΣF_k。单方式时退化为原公式（纯猎含劳动 ✓ 兼容）。
    /// 分量缓存 FHuntLast/FHerdLast/FFarmLast（货物分解用）。</summary>
    public float FOf(CivEntity e)
    {
        float m = e.CarryMult > 0f ? e.CarryMult : TechTable.HuntingCarry(e.TechKeys);
        float A = Grid.CellAreaKm2 / NTribes(e.Cell);
        float pHunt = R[e.Cell] * A * m;
        float pHerd = CapabilityTable.Has(this, e, "livestock") ? R[e.Cell] * HerdMult * A * m : 0f;
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
                case "plain": if (b is BiomeType.ContinentalHot or BiomeType.ContinentalWarm or BiomeType.ContinentalDry
                        or BiomeType.HotSteppe or BiomeType.ColdSteppe) return true; break;
                case "mediterranean": if (b is BiomeType.MediterraneanHot or BiomeType.MediterraneanCool) return true; break;
                case "monsoon": if (b is BiomeType.TropicalMonsoon or BiomeType.MonsoonSubtropical) return true; break;
                case "humidsubtrop": if (b is BiomeType.HumidSubtropical or BiomeType.Oceanic or BiomeType.TropicalRainforest) return true; break;
                case "coldtemperate": if (b is BiomeType.ContinentalHot or BiomeType.ContinentalWarm or BiomeType.ContinentalDry
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
        for (int i = 0; i < Entities.Count; i++)
            if (!Entities[i].Dead) s += Entities[i].P;
        return s;
    }

    // ══════════════════════════════════════════════════════════════════
    // 影响力场模型（2026-08-10 定稿，5 km² 口径）
    //   归属 = argmax(P×CarryMult×w(d))，w = 紧支撑平滑核，粘性 1.15；
    //   领地 = 归属格集合；F = Σ 领地格 min(需求份额, Cap×w)；存量耗竭→饿→迁移。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>紧支撑平滑核：w(d) = max(0, (1−d²/R²)²)。d=格步数（BFS 深度），d≥R 严格 0。</summary>
    public static float InfluenceWeight(float d)
    {
        float r = InfluenceRadius;
        float t = 1f - (d * d) / (r * r);
        return t > 0f ? t * t : 0f;
    }

    /// <summary>存量初始化：S₀ = StockYears×R×5（20 年产量；海洋/不可居 0）。Run 前调用一次。</summary>
    public void InitStock()
    {
        int n = Grid.N;
        Stock = new float[n];
        for (int c = 0; c < n; c++)
            Stock[c] = R[c] > 0f ? StockYears * R[c] * 5f : 0f;
    }

    /// <summary>存量再生：S' = S + ρ·(K−S)（向 K 线性回归——S=0 也恢复，无 logistic 死锁；K = StockYears×R×5）。</summary>
    public void RegenStocks()
    {
        int n = Grid.N;
        for (int c = 0; c < n; c++)
        {
            float k = StockYears * R[c] * 5f;
            if (k <= 0f) continue;
            float s = Stock[c];
            Stock[c] = s + RegenRate * (k - s);
        }
    }

    /// <summary>影响力场重算：每格归属 = argmax(P×M×w(d))；粘性：非 owner 需超现 owner×1.15 才易主。
    /// band 驱动（每 band 写半径 R 内格，O(band×28)）；确定性（固定遍历顺序）。</summary>
    public void RebuildInfluence()
    {
        int n = Grid.N;
        Array.Fill(CellBestOwner, -1);
        Array.Clear(CellBestInf, 0, n);
        Array.Clear(CellOwnerInf, 0, n);
        for (int i = 0; i < Entities.Count; i++)
        {
            var e = Entities[i];
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
            if (LockedUntil != null && LockedUntil[c] > Tick) continue;   // 实控锁定格：武力既成事实，场不重算（2026-08-10 冲突机制）
            int best = CellBestOwner[c];
            if (best < 0)
            {
                if (CellOwner[c] >= 0 && CellOwner[c] < Entities.Count && Entities[CellOwner[c]].Dead)
                    CellOwner[c] = -1;   // 现 owner 已死且无新覆盖 → 归无主
                continue;
            }
            int cur = CellOwner[c];
            if (cur == best) continue;
            if (cur < 0 || CellBestInf[c] > CellOwnerInf[c] * Stickiness)
                CellOwner[c] = best;
        }
        RebuildTerritory();
    }

    /// <summary>惰性确保领地索引数组存在（构造场景/读档路径可能未初始化）。</summary>
    public void EnsureTerritory()
    {
        if (TerritoryCells != null) return;
        int cap = Math.Max(4096, Entities.Count + 256);
        TerritoryCells = new List<int>[cap];
        TerritoryDists = new List<byte>[cap];
        for (int i = 0; i < cap; i++)
        {
            TerritoryCells[i] = new List<int>();
            TerritoryDists[i] = new List<byte>();
        }
    }

    /// <summary>领地索引重建：每 band 的领地格 = 归属格 ∩ 其影响圈（BFS 半径 R 内）。距离入 TerritoryDists。</summary>
    public void RebuildTerritory()
    {
        EnsureTerritory();
        for (int i = 0; i < Entities.Count; i++)
        {
            if (Entities[i].Dead) continue;
            if (i >= TerritoryCells.Length) EnsureTerritoryCapacity(i + 256);
            TerritoryCells[i].Clear();
            TerritoryDists[i].Clear();
        }
        for (int i = 0; i < Entities.Count; i++)
        {
            var e = Entities[i];
            if (e.Dead) continue;
            var terr = TerritoryCells[i];
            var dists = TerritoryDists[i];
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

    /// <summary>领地采集：返回**潜在产出**（Σ Cap×w，增长用——F 可 > P 才有盈余增长），
    /// 扣减按需求分摊（每格 min(份额, Cap×w)）。Cap_格 = CapRate×S（剩余多→上限高→耗竭反馈）。
    /// ⚠️ 2026-08-10 修：F 若按需求分摊报告则 F≤P 恒成立 → 增长公式永 ≤1 → 全员饿死；
    ///    F 必须报告潜在（旧模型 F=R×面积 同语义），实际采量另算。</summary>
    public float Harvest(CivEntity e)
    {
        var terr = TerritoryCells[e.Id];
        if (terr == null || terr.Count == 0) return 0f;
        var dists = TerritoryDists[e.Id];
        float sumW = 0f;
        for (int k = 0; k < terr.Count; k++)
            sumW += InfluenceWeight(dists[k]);
        if (sumW <= 0f) return 0f;
        float D = e.P;
        float potCap = 0f;   // 潜在可采（增长基准）
        for (int k = 0; k < terr.Count; k++)
        {
            int c = terr[k];
            float w = InfluenceWeight(dists[k]);
            potCap += CapRate * Stock[c] * w;
        }
        if (potCap <= 0f) return 0f;
        float scale = Mathf.Min(1f, D / potCap);   // 需求不足时按比例少采（S 保住）
        for (int k = 0; k < terr.Count; k++)
        {
            int c = terr[k];
            float w = InfluenceWeight(dists[k]);
            float take = CapRate * Stock[c] * w * scale;
            if (take <= 0f) continue;
            take = Mathf.Min(take, Stock[c]);
            Stock[c] -= take;
        }
        return potCap;
    }

    /// <summary>BFS 半径 maxDepth（格步数），确定性：格遍历顺序 = 邻接表顺序。visit(cell, depth)。
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
    public bool IsStarving(CivEntity e) => e.FLast < e.P * 0.999f;
}
