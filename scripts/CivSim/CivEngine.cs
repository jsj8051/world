using System;
using System.Collections.Generic;
using Godot;
using World.LogicGrid;

namespace World.CivSim;

/// <summary>文明演化纪元（时间轴）。v1 只实现 StoneAge，后续纪元机制逐代注册。</summary>
public enum EpochKind
{
    StoneAge = 0,     // 旧石器：狩猎采集、部落扩散（v1）
    Neolithic,        // 新石器：农业革命、聚落定居（v2）
    BronzeAge,        // 青铜：城市、文字、国家雏形（v3）
    IronAge,          // 铁器：军事扩张、边界战争（v4）
    Classical,        // 古典：帝国整合、文化繁荣（v5）
    Medieval,         // 中世纪：封建、贸易（终点）
}

/// <summary>纪元定义（名称/机制注册）。</summary>
public sealed class EpochDefinition
{
    public EpochKind Kind;
    public string Name;
    public int MaxTicks;          // 该纪元 tick 上限（演化时长）
    public int TickYears;         // 每 tick 对应年数

    public EpochDefinition(EpochKind kind, string name, int maxTicks, int tickYears)
    {
        Kind = kind; Name = name; MaxTicks = maxTicks; TickYears = tickYears;
    }
}

/// <summary>每格文明状态（连续场；v1 石器时代无聚落/国家）。</summary>
public struct CellCiv
{
    public float Population;   // 人口（狩猎采集者）
    public byte Culture;       // 文化标签 id（0=无）
    public byte Tech;          // 技术层级 0=石核 1=手斧 2=细石器 3=弓箭
    public int TribeId;        // 所属部落谱系 id（-1=无人）
}

/// <summary>部落实体（谱系跟踪：起源格/文化/技术/当前主格/总人口；v1 用于输出实体表）。</summary>
public class Tribe
{
    public int Id;
    public int OriginCell;     // 起源格
    public byte Culture;       // 当前文化
    public byte Tech;          // 当前技术层级
    public int MainCell;       // 当前人口最多格
    public float Population;   // 总人口（演化末统计）
}

/// <summary>文明演化上下文（一次演化的全部状态；机制在此读写）。</summary>
public sealed class CivSimContext
{
    public GameGrid Grid;             // 自然层（只读输入）
    public CellCiv[] Cells;           // 每格文明状态
    public List<Tribe> Tribes = new();// 部落谱系表
    public int Tick;                  // 当前 tick
    public EpochDefinition Epoch;     // 当前纪元
    public int Seed;                  // 演化种子（确定性）
    public int OriginCount = 3;       // 起源摇篮数（1-3）
    public Random Rng;                // 确定性随机（同 seed 可复现）
    public int LandCells;             // 陆地格数（预统计）
    public float[] CellK;             // 每格当前承载人口（环境×技术，tick 内更新）
    public float[] BaseK;             // 每格基础承载人口（环境 only，技术 0）
    public float TotalPopulation;     // 总人口（每 tick 统计）

    // ── 参数标定（狩猎采集人类学依据，见 docs/文明演化v1.md）──
    public const float GrowthRatePerYear = 0.005f;   // 自然增长率 r（前工业 ~0.05%/年，游戏压缩 10×）
    public const float MigrateThreshold = 0.75f;     // 人口 ≥75% K 触发迁徙（饱和前开始泄压）
    public const float MigrateShare = 0.25f;         // 主候选格溢出比例（最优宜居格）
    public const float MigrateShareSecondary = 0.08f;// 其余候选格播种比例（多路扩散，前沿铺开）
    public const float OvercrowdLimit = 1.3f;        // 超载上限（>1.3K 资源枯竭惩罚）
    public const float AssimilateRatio = 3f;         // 相邻人口比 >3:1 才同化文化
    public const float AssimilateChance = 0.5f;      // 同化概率/tick
    public const int TechUnlockPop = 100_000;        // 细石器全局人口门槛（旧石器晚期 ~几十万）
    public const int TechUnlockTick = 60;            // 且至少演化 60 tick（6000 年）
    public const float TechKFactor = 1.4f;           // 每级技术承载提升 ×1.4
    public const float FireKFactor = 1.0f;           // 技术≥1（火）后极寒区可居（K 下限提升）

    public float TickFactor => GrowthRatePerYear * Epoch.TickYears;  // 每 tick logistic 系数 r×Δt

    /// <summary>承载密度（人/km²）按 biome 标定（Binford 狩猎采集密度研究量级）。</summary>
    public static float CarrierDensityPerKm2(byte biome)
    {
        switch ((Biome.BiomeType)biome)
        {
            case Biome.BiomeType.DeepOcean:
            case Biome.BiomeType.Ocean:
            case Biome.BiomeType.TropicalOcean:
            case Biome.BiomeType.FrigidOcean:
                return 0f;                                   // 海洋
            case Biome.BiomeType.IceCap:
            case Biome.BiomeType.Tundra:
                return 0.02f;                                // 极地冻原（北极 ~0.02）
            case Biome.BiomeType.Taiga:
            case Biome.BiomeType.Subarctic:
                return 0.10f;                                // 寒带针叶林
            case Biome.BiomeType.HotDesert:
            case Biome.BiomeType.ColdDesert:
            case Biome.BiomeType.ColdDesertKoppen:
            case Biome.BiomeType.Desert:
                return 0.05f;                                // 沙漠（卡拉哈里 ~0.05）
            case Biome.BiomeType.Alpine:
                return 0.05f;                                // 高山
            case Biome.BiomeType.TropicalForest:
            case Biome.BiomeType.TropicalRainforest:
            case Biome.BiomeType.TropicalMonsoon:
                return 0.15f;                                // 雨林（亚马逊/刚果 ~0.15-0.2）
            case Biome.BiomeType.TropicalDryForest:
                return 0.20f;                                // 干热疏林
            case Biome.BiomeType.TemperateForest:
            case Biome.BiomeType.HumidSubtropical:
            case Biome.BiomeType.Oceanic:
            case Biome.BiomeType.MonsoonSubtropical:
            case Biome.BiomeType.ContinentalHot:
            case Biome.BiomeType.ContinentalWarm:
            case Biome.BiomeType.ContinentalDry:
                return 0.30f;                                // 温带林/亚热带/大陆性
            case Biome.BiomeType.MediterraneanHot:
            case Biome.BiomeType.MediterraneanCool:
                return 0.30f;                                // 地中海
            case Biome.BiomeType.TemperateGrassland:
            case Biome.BiomeType.Savanna:
            case Biome.BiomeType.TropicalSavanna:
            case Biome.BiomeType.HotSteppe:
            case Biome.BiomeType.ColdSteppe:
            case Biome.BiomeType.Riparian:
                return 0.45f;                                // 草原/稀树草原/河岸带（狩猎采集高密区）
            default:
                return 0.20f;                                // 其余
        }
    }
}

/// <summary>
/// 文明演化引擎（v1 石器时代）。输入 GameGrid（自然层只读），输出演化结果（文明状态 +
/// 部落表），不修改自然层。确定性：同 seed 同网格 → 同结果。
///
/// 流程：起源播种（1-3 摇篮）→ tick 循环（增长→技术→迁徙→文化→竞争）→
/// 终止（固定上限或全球人口饱和）→ 纪元终态。
/// </summary>
public static class CivEngine
{
    /// <summary>石器时代纪元定义（300 tick × 100 年 = 3 万年；智人出非洲到全球 ~5 万年的游戏压缩；
    /// 饱和+覆盖停滞可提前终止）。</summary>
    public static readonly EpochDefinition StoneAgeEpoch = new(EpochKind.StoneAge, "石器时代", 300, 100);

    /// <summary>运行一次完整石器时代演化。</summary>
    /// <param name="grid">逻辑网格（自然层，只读）。</param>
    /// <param name="seed">演化种子（确定性复现）。</param>
    /// <param name="originCount">起源摇篮数（1-3）。</param>
    public static CivSimResult Run(GameGrid grid, int seed, int originCount = 3)
    {
        int n = grid.N;
        var ctx = new CivSimContext
        {
            Grid = grid,
            Cells = new CellCiv[n],
            Seed = seed,
            OriginCount = originCount,
            Rng = new Random(seed),
            Epoch = StoneAgeEpoch,
            CellK = new float[n],
            BaseK = new float[n],
        };
        for (int i = 0; i < n; i++)
            ctx.Cells[i].TribeId = -1;

        // ── 0. 承载人口场（环境×胞面积；技术 0）──
        float cellArea = grid.CellAreaKm2;
        for (int i = 0; i < n; i++)
        {
            float dens = ctx.Grid.IsLandCell(i) ? CivSimContext.CarrierDensityPerKm2(ctx.Grid.Biome[i]) : 0f;
            // 河流/湖泊水源加成 ×1.5（定居水源充足 → 食物集中）
            if (dens > 0f && (ctx.Grid.RiverLevel[i] > 0 || ctx.Grid.LakeLevel[i] > 0))
                dens *= 1.5f;
            ctx.BaseK[i] = dens * cellArea;
            ctx.CellK[i] = ctx.BaseK[i];
        }

        // ── 1. 起源播种 + tick 循环（机制注册表，每 tick 按 Order 执行）──
        var registry = CivModelRegistry.StoneAge();
        int stagnant = 0;
        float prevPop = ctx.TotalPopulation;
        int prevOcc = -1;
        for (ctx.Tick = 0; ctx.Tick < ctx.Epoch.MaxTicks; ctx.Tick++)
        {
            registry.ExecuteAll(ctx);

            // 饱和终止：全球人口连续 20 tick 增长 <1% **且 覆盖不再扩张**（旧石器已达环境上限；
            // ⚠️ 只查人口会误杀探路扩散——总人口饱和时前沿仍在开拓新格，须覆盖也停滞才终止）
            float pop = ctx.TotalPopulation;
            int occ = 0;
            for (int i = 0; i < n; i++) if (ctx.Cells[i].TribeId >= 0) occ++;
            if (pop <= prevPop * 1.01f && occ <= prevOcc) stagnant++;
            else stagnant = 0;
            prevPop = pop;
            prevOcc = occ;
            if (stagnant >= 20 && ctx.Tick >= 40)
                break;
        }

        // ── 3. 终态统计（部落总人口/主格/文化/技术同步）──
        foreach (var t in ctx.Tribes) { t.Population = 0; t.MainCell = -1; }
        for (int i = 0; i < n; i++)
        {
            var c = ctx.Cells[i];
            if (c.TribeId >= 0 && ctx.Tribes[c.TribeId] != null)
            {
                var t = ctx.Tribes[c.TribeId];
                t.Population += c.Population;
                if (t.MainCell < 0 || c.Population > ctx.Cells[t.MainCell].Population)
                {
                    t.MainCell = i;
                    t.Culture = c.Culture;   // 主格文化/技术同步（部落表反映实际状态）
                    t.Tech = c.Tech;
                }
            }
        }

        return new CivSimResult { Context = ctx, FinalTick = ctx.Tick + 1 };
    }
}

/// <summary>演化结果（输出载体）。</summary>
public sealed class CivSimResult
{
    public CivSimContext Context;
    public int FinalTick;   // 实际演化 tick 数
}
