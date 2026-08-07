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

    // ── 承载/压力场（每 tick 刷新）──
    public float[] BaseK;      // 每格基础承载（环境 only：密度×邻水加成×胞面积）
    public float[] CellK;      // 每格当前承载（格内实体最优生产方式产量/寒冷下限的 max）
    public float[] CellPop;    // 每格总人口

    // ── 自然层派生缓存（确定性重建，不存档）──
    public byte[] WildCrops;   // WildCrops 位（grid.EnsureWildCrops 惰性）
    public float[,] Suit;      // 每格每种子适宜度 φ（WildCropsSystem.Suitability 缓存）

    // ── 演化统计（诊断/输出）──
    public int Fissions;
    public int Migrations;
    public int CultureKeyCount;         // 文化/文化群 key 计数器（分裂分化分配新 key，如 "cult_12"）
    public int ReligionKeyCount;        // 宗教派别 key 计数器（起源/分裂分化分配新 key，如 "relig_3"）
    public int FirstFarmTick = -1;      // 首转农 tick（终止条件锚点）
    public int[] BfsStamp;
    public int BfsStampValue;

    /// <summary>分配新文化 key（确定性递增；与科技 key 同风格——字符串可读）。</summary>
    public string NextCultureKey() => $"cult_{CultureKeyCount++}";

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
    public const float MigrateThreshold = 0.75f;      // 饱和迁徙阈值（格 P/K）
    public const float MigrateShare = 0.5f;           // 饱和迁徙分出比例
    public const float ScoutChance = 0.02f;           // 探路迁徙概率/tick
    public const float ScoutMinPop = 100f;            // 探路最小实体人口
    public const float ScoutShare = 0.3f;             // 探路迁出比例
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

    // ── 承载密度（人/km²，Binford 狩猎采集密度量级 ★ 待校准；逐柯本细类，§4.1）──

    public static float CarrierDensityPerKm2(BiomeType b)
    {
        switch (b)
        {
            case BiomeType.DeepOcean:
            case BiomeType.Ocean:
            case BiomeType.FrigidOcean:
            case BiomeType.TropicalOcean:
                return 0f;
            case BiomeType.IceCap:
                return 0f;                       // 冰盖（火/皮毛后按 §4.5 下限）
            case BiomeType.Tundra:
            case BiomeType.Subarctic:
            case BiomeType.Alpine:
                return 0.05f;
            case BiomeType.HotDesert:
            case BiomeType.ColdDesertKoppen:
                return 0.05f;
            case BiomeType.TropicalRainforest:
                return 0.15f;
            case BiomeType.TropicalMonsoon:
                return 0.20f;
            case BiomeType.TropicalSavanna:
                return 0.45f;
            case BiomeType.HotSteppe:
                return 0.45f;
            case BiomeType.ColdSteppe:
                return 0.35f;
            case BiomeType.HumidSubtropical:
            case BiomeType.Oceanic:
                return 0.30f;
            case BiomeType.MonsoonSubtropical:
                return 0.25f;
            case BiomeType.MediterraneanHot:
                return 0.30f;
            case BiomeType.MediterraneanCool:
                return 0.25f;
            case BiomeType.ContinentalHot:
                return 0.30f;
            case BiomeType.ContinentalWarm:
                return 0.25f;
            case BiomeType.ContinentalDry:
                return 0.20f;
            case BiomeType.Riparian:
                return 0.60f;                    // 沼泽湿地（Binford 湿地最丰）
            default:
                return 0f;                       // 旧值 4-11 化石（新档不产生；读旧档报错）
        }
    }

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

    // ── 产量与能量（§4.2-4.4 定稿公式）──

    /// <summary>格基础狩猎产量 Y_猎0 = 密度 × 邻水加成 × 胞面积（加成只乘一次）。</summary>
    public float YHunter0(int cell)
    {
        var b = (BiomeType)Grid.Biome[cell];
        float dens = CarrierDensityPerKm2(b);
        if (dens <= 0f) return 0f;
        if (b != BiomeType.Riparian && (Grid.LakeLevel[cell] > 0 || IsNearWater(cell)))
            dens *= 1.5f;                        // 邻水加成（Riparian 湿地密度已含）
        return dens * Grid.CellAreaKm2;
    }

    private bool IsNearWater(int cell)
    {
        foreach (int nb in Grid.Neighbors[cell])
            if (Grid.Biome[nb] == (byte)BiomeType.Riparian || Grid.LakeLevel[nb] > 0)
                return true;
        return false;
    }

    /// <summary>实体狩猎产量 Y_猎 = Y_猎0 × 工具乘数链。</summary>
    public float YHunter(CivEntity e) => YHunter0(e.Cell) * TechTable.HuntingCarry(e.TechKeys);

    /// <summary>实体农业产量 Y_农 = max over 持种子(基线 × f(Soil) × φ)。</summary>
    public float YFarm(CivEntity e)
    {
        float y0 = YHunter0(e.Cell);
        if (y0 <= 0f) return 0f;
        float best = 0f;
        foreach (var s in TechTable.SeedKeys)
        {
            if (!e.TechKeys.Contains(s)) continue;
            var def = TechTable.Get(s);
            float f = SoilFactor(Grid.SoilLevel[e.Cell]);
            float phi = Phi(e.Cell, def.SeedIndex);
            best = Mathf.Max(best, def.AgriBase * f * phi * y0);
        }
        return best;
    }

    /// <summary>f(SoilLevel)：Soil1=0.4, 2=0.6, 3=0.8, 4=1.0, 5=1.2（★ 待校准）。</summary>
    public static float SoilFactor(byte soil) => soil switch
    {
        1 => 0.4f, 2 => 0.6f, 3 => 0.8f, 4 => 1.0f, _ => 1.2f,
    };

    /// <summary>格/种子适宜度 φ（缓存矩阵）。</summary>
    public float Phi(int cell, int seedIdx) => Suit != null ? Suit[cell, seedIdx] : 0f;

    /// <summary>狩猎人均收益（选择比较用；含耗竭项，非核算 e）。</summary>
    public static float EHunt(float yHunt, float pop) =>
        yHunt > 0f ? yHunt / (pop + HRel * yHunt) : 0f;

    /// <summary>农业人均收益（w 已含，单扣）。</summary>
    public static float EFarm(float yFarm, float pop) =>
        yFarm > 0f ? yFarm / Mathf.Max(0.001f, pop) - W : 0f;

    /// <summary>寒冷区 K 下限（§4.5：火 → 0.05·面积×3；皮毛 → 再 ×3）。</summary>
    public float ColdFloor(int cell, HashSet<string> keys)
    {
        if (!IsColdZone((BiomeType)Grid.Biome[cell])) return 0f;
        if (!keys.Contains(TechTable.Fire)) return 0f;
        float area = Grid.CellAreaKm2;
        float floor = 0.05f * area * 3f;
        if (keys.Contains(TechTable.Clothing)) floor *= 3f;
        return floor;
    }

    /// <summary>实体当前产量（生产方式决定）。</summary>
    public float Yield(CivEntity e) => e.IsFarming ? YFarm(e) : YHunter(e);

    /// <summary>实体承载 K = max(当前产量, 寒冷下限)。</summary>
    public float KOf(CivEntity e) => Mathf.Max(Yield(e), ColdFloor(e.Cell, e.TechKeys));

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
