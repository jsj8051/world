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

    // ── 自然层派生缓存（确定性重建，不存档）──
    public byte[] WildCrops;   // WildCrops 位（grid.EnsureWildCrops 惰性）
    public float[,] Suit;      // 每格每种子适宜度 φ（WildCropsSystem.Suitability 缓存）

    // ── 演化统计（诊断/输出）──
    public int Fissions;
    public int Migrations;
    public int CultureKeyCount;         // 文化标签 key 计数器（分裂分化分配新 key，如 "cult_12"）
    public int CultureGroupKeyCount;    // 文化群 key 计数器（2026-08-07 与文化标签分开——语言大群独立 key 空间，防标签挤占）
    public int ReligionKeyCount;        // 宗教派别 key 计数器（起源/分裂分化分配新 key，如 "relig_3"）
    public int FirstFarmTick = -1;      // 首转农 tick（终止条件锚点）
    public int[] BfsStamp;
    public int BfsStampValue;

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
    public const float SplitPop = 300f;               // 分裂阈值（2026-08-07 400→300：分裂更勤 → 分化更多）
    public const int MaxTribesPerCell = 8;            // 格内实体上限（超限不分裂，迁徙优先）
    public const float SplitShare = 0.45f;            // 分裂新实体带走比例
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
    public const float OriginPop = 100f;
    public const int TerminateAfterAgri = 100;        // 首转农 +100 ticks 结束
    public const int MaxTicksNoAgri = 500;            // 兜底：无农 500 ticks 停止（天然灭绝星球）

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

    /// <summary>实体狩猎产出 F_猎 = R × 胞面积 × 工具乘数链（猎物再生率恒定近似，种群动态二期）。
    /// CarryMult 走实体缓存（RefreshCellState 每 tick 算；测试构造未算时 fallback 实时）。</summary>
    public float FHunt(CivEntity e)
    {
        float m = e.CarryMult > 0f ? e.CarryMult : TechTable.HuntingCarry(e.TechKeys);
        return R[e.Cell] * Grid.CellAreaKm2 * m;
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

    /// <summary>寒冷区 F 下限（§4.5：火 → 0.05·面积×3；皮毛 → 再 ×3——空间层被技术解锁）。</summary>
    public float ColdFloor(int cell, HashSet<string> keys)
    {
        if (!IsColdZone((BiomeType)Grid.Biome[cell])) return 0f;
        if (!keys.Contains(TechTable.Fire)) return 0f;
        float area = Grid.CellAreaKm2;
        float floor = 0.05f * area * 3f;
        if (keys.Contains(TechTable.Clothing)) floor *= 3f;
        return floor;
    }

    /// <summary>实体当 tick 实际产出 F_i = max(生产方式实际产出, 寒冷下限)（增长/核算/格压力用）。</summary>
    public float FOf(CivEntity e) =>
        Mathf.Max(e.IsFarming ? FFarmActual(e) : FHunt(e), ColdFloor(e.Cell, e.TechKeys));

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
}
