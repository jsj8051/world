using System;
using System.Collections.Generic;
using Godot;
using World.LogicGrid;

namespace World.CivSim;

/// <summary>文明演化纪元（时间轴标签；推进完全逐格/逐部落异步，纪元仅用于存档标签与统计）。</summary>
public enum EpochKind
{
    StoneAge = 0,     // 旧石器：狩猎采集、部落扩散
    Neolithic,        // 新石器：农业革命、定居（部落异步进入）
    BronzeAge,        // 青铜
    IronAge,          // 铁器
    Classical,        // 古典
    Medieval,         // 中世纪（终点）
}

/// <summary>纪元定义（标签/时长）。</summary>
public sealed class EpochDefinition
{
    public EpochKind Kind;
    public string Name;
    public int MaxTicks;
    public int TickYears;

    public EpochDefinition(EpochKind kind, string name, int maxTicks, int tickYears)
    {
        Kind = kind; Name = name; MaxTicks = maxTicks; TickYears = tickYears;
    }
}

/// <summary>宗教阶段（史实演进链：泛灵论→萨满图腾→祖先崇拜→多神教→一神教）。</summary>
public enum ReligionType
{
    Animism = 0,        // 万物有灵（旧石器早期：尼安德特墓葬、泛灵论）
    ShamanTotem = 1,    // 萨满/图腾（旧石器晚期：洞穴壁画、维纳斯雕像）
    AncestorWorship = 2,// 祖先崇拜（新石器：哥贝克力石阵、聚落祖先祭祀）
    Polytheism = 3,     // 多神教（青铜：神庙、神系）
    Monotheism = 4,     // 一神教（铁器/古典：一神信仰、圣典）
}

/// <summary>
/// 部落 = 格内社会单元（动态）。一格可容纳多个部落；部落不跨格。
/// 会分裂（segmentary lineage：人口超阈值同格裂变）、被吞并、和平合并、迁徙（迁往相邻格）。
/// 技术 = 部落属性（位掩码）；文化 = 分层（文化群[语言-文化大群，慢] + 文化标签[快]）；宗教 = 5 阶段演进。
/// </summary>
public class Tribe
{
    public int Id;
    public int Cell;           // 所在格（游动中心；部落不占多格）
    public float Population;
    public byte Culture;       // 文化标签（接触同化快：语言/习俗）
    public byte CultureGroup;  // 文化群（语言-文化大群；起源决定，同化慢：千年尺度）
    public byte Religion;      // ReligionType 0-4（演进 + 接触传播）
    public ulong TechFlags;    // 技术位掩码（TechTable）
    public int OriginCell;     // 起源格（历史记录）
    public int BornTick;       // 成立 tick
    public bool Dead;          // 死亡标记（被吞并/合并；Tribes 定期 compact，避免 O(n) Remove）
}

/// <summary>文明演化上下文（一次演化的全部状态；机制在此读写）。</summary>
public sealed class CivSimContext
{
    public GameGrid Grid;             // 自然层（只读输入）
    public List<Tribe>[] CellTribes;  // 每格部落列表（格=地理容器，一格多部落）
    public List<Tribe> Tribes;        // 全部存活部落
    public int Tick;
    public EpochDefinition Epoch;
    public int Seed;
    public int OriginCount = 3;
    public Random Rng;                // 确定性随机（同 seed 可复现）

    public float[] BaseK;             // 每格基础承载（环境 only）
    public float[] CellK;             // 每格当前承载（环境 × 格内部落技术并集乘数）
    public float[] CellPop;           // 每格总人口（Σ 部落，每 tick 缓存）

    // ── 演化统计（诊断/输出）──
    public int Fissions;              // 分裂次数
    public int Absorptions;           // 吞并次数
    public int Merges;                // 和平合并次数
    public int Migrations;            // 迁徙次数
    public long TradeContacts;        // 贸易接触次数
    public int CultureGroupCount;     // 文化群 id 计数（隔离分化分配新 id）
    public int[] BfsStamp;            // BFS 时间戳（探路目标搜索复用，避免每部落分配 visited）
    public int BfsStampValue;

    // ── 参数标定（人类学依据，见 docs/文明演化v1.md）──
    public const float GrowthRatePerYear = 0.005f;   // 自然增长率 r（前工业 ~0.05%/年，游戏压缩 10×）
    public const float SplitPop = 400f;              // 部落分裂阈值（游戏部落=数百人社会单元，超阈值 segmentary lineage 裂变）
    public const int MaxTribesPerCell = 8;           // 格内部落上限（超限不分裂，迁徙优先——性能 + 社会密度双重约束）
    public const float SplitShare = 0.45f;           // 分裂时新部落带走比例
    public const float MigrateThreshold = 0.75f;     // 格饱和触发迁徙（更早迁徙，避免人口锁死在富饶区）
    public const float MigrateShare = 0.5f;          // 饱和迁徙时迁出部落分出比例
    public const float ScoutChance = 0.12f;          // 探路迁徙概率/tick（持续扩散——农业扩张 ~1km/年 需强扩散）
    public const float ScoutMinPop = 100f;           // 探路最小部落人口
    public const float ScoutShare = 0.4f;            // 探路迁出比例
    public const float AbsorbRatio = 3f;             // 冲突吞并人口比（>3:1）
    public const float MergeRatioMax = 2f;           // 和平合并人口比上限（0.5~2 视为对等）
    public const float MergeChance = 0.005f;         // 和平合并概率/tick/接触
    public const float AssimilateChance = 0.3f;      // 文化同化概率/tick（格内，文化标签快）
    public const float ReligionSpreadChance = 0.02f;    // 宗教传播概率/tick/接触（只向更高阶段）
    public const float CultureDriftChance = 0.002f;     // 文化群隔离分化概率/tick（语言分化：印欧→拉丁/日耳曼…）
    public const float TradeSpreadBonus = 2f;        // 贸易对技术传播的加速倍率
    public const int TechInventRolls = 4;            // 每部落每 tick 发明尝试的技术数（随机轮转，性能上限）

    public float TickFactor => GrowthRatePerYear * Epoch.TickYears;

    /// <summary>承载密度（人/km²）按 biome 标定（Binford 狩猎采集密度研究量级）。</summary>
    public static float CarrierDensityPerKm2(byte biome)
    {
        switch ((Biome.BiomeType)biome)
        {
            case Biome.BiomeType.DeepOcean:
            case Biome.BiomeType.Ocean:
            case Biome.BiomeType.TropicalOcean:
            case Biome.BiomeType.FrigidOcean:
                return 0f;
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
                return 0.05f;
            case Biome.BiomeType.TropicalForest:
            case Biome.BiomeType.TropicalRainforest:
            case Biome.BiomeType.TropicalMonsoon:
                return 0.15f;                                // 雨林
            case Biome.BiomeType.TropicalDryForest:
                return 0.20f;
            case Biome.BiomeType.TemperateForest:
            case Biome.BiomeType.HumidSubtropical:
            case Biome.BiomeType.Oceanic:
            case Biome.BiomeType.MonsoonSubtropical:
            case Biome.BiomeType.ContinentalHot:
            case Biome.BiomeType.ContinentalWarm:
            case Biome.BiomeType.ContinentalDry:
                return 0.30f;                                // 温带/亚热带/大陆性
            case Biome.BiomeType.MediterraneanHot:
            case Biome.BiomeType.MediterraneanCool:
                return 0.30f;                                // 地中海
            case Biome.BiomeType.TemperateGrassland:
            case Biome.BiomeType.Savanna:
            case Biome.BiomeType.TropicalSavanna:
            case Biome.BiomeType.HotSteppe:
            case Biome.BiomeType.ColdSteppe:
            case Biome.BiomeType.Riparian:
                return 0.45f;                                // 草原/稀树草原/河岸带
            default:
                return 0.20f;
        }
    }

    /// <summary>部落所在格环境是否满足技术环境要求（any=不限）。</summary>
    public bool EnvMatches(int cell, string[] env)
    {
        if (env == null || env.Length == 0) return true;
        var biome = (Biome.BiomeType)Grid.Biome[cell];
        foreach (var e in env)
        {
            switch (e)
            {
                case "river":   if (biome == Biome.BiomeType.Riparian) return true; break;
                case "coast":   if (Grid.IsCoast(cell)) return true; break;
                case "grass":   if (biome is Biome.BiomeType.TemperateGrassland or Biome.BiomeType.Savanna
                        or Biome.BiomeType.TropicalSavanna or Biome.BiomeType.HotSteppe
                        or Biome.BiomeType.ColdSteppe) return true; break;
                case "plain":   if (biome is Biome.BiomeType.TemperateGrassland or Biome.BiomeType.TemperateForest
                        or Biome.BiomeType.ContinentalHot or Biome.BiomeType.ContinentalWarm
                        or Biome.BiomeType.ContinentalDry) return true; break;
                case "mediterranean": if (biome is Biome.BiomeType.MediterraneanHot or Biome.BiomeType.MediterraneanCool) return true; break;
                case "copper":  if ((Grid.MineralLevel[cell] & 0x0F) == 2) return true; break;   // 矿种 2=铜
                case "iron":    if ((Grid.MineralLevel[cell] & 0x0F) == 1) return true; break;   // 矿种 1=铁
            }
        }
        return false;
    }

    /// <summary>全球总人口（Σ 存活部落）。</summary>
    public float TotalPopulation()
    {
        float s = 0f;
        for (int i = 0; i < Tribes.Count; i++)
            if (!Tribes[i].Dead) s += Tribes[i].Population;
        return s;
    }
}

/// <summary>
/// 文明演化引擎。输入 GameGrid（自然层只读），输出演化结果（部落表 + 每格状态），
/// 不修改自然层。确定性：同 seed 同网格 → 同结果。
///
/// 模型：部落=格内社会单元（一格多部落）；分裂/迁徙/接触（传播/贸易/吞并/和平合并）；
/// 技术=部落属性，发明（人口+环境+随机）/传播（接触），效果=承载乘数/能力解锁。
/// 时代推进完全异步——部落按各自节奏发展（农业发明少数起源中心 + 接触传播）。
/// </summary>
public static class CivEngine
{
    /// <summary>石器时代纪元定义（300 tick × 100 年 = 3 万年；饱和+停滞可提前终止）。</summary>
    public static readonly EpochDefinition StoneAgeEpoch = new(EpochKind.StoneAge, "石器时代", 300, 100);

    /// <summary>运行一次完整演化（v2 部落模型：动态分裂 + 部落级技术）。
    /// onProgress：后台线程调用（0..1，tick 级），调用方须保证线程安全（如写 volatile 字段）。</summary>
    public static CivSimResult Run(GameGrid grid, int seed, int originCount = 3, Action<float> onProgress = null)
    {
        TechTable.Load();
        int n = grid.N;
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellTribes = new List<Tribe>[n],
            Tribes = new List<Tribe>(),
            Seed = seed,
            OriginCount = originCount,
            Rng = new Random(seed),
            Epoch = StoneAgeEpoch,
            BaseK = new float[n],
            CellK = new float[n],
            CellPop = new float[n],
            BfsStamp = new int[n],
            BfsStampValue = 1,
        };
        for (int i = 0; i < n; i++)
            ctx.CellTribes[i] = new List<Tribe>();

        // ── 0. 基础承载场（环境 × 胞面积；技术乘数后续叠乘）──
        float cellArea = grid.CellAreaKm2;
        for (int i = 0; i < n; i++)
        {
            float dens = grid.IsLandCell(i) ? CivSimContext.CarrierDensityPerKm2(grid.Biome[i]) : 0f;
            if (dens > 0f && (grid.RiverLevel[i] > 0 || grid.LakeLevel[i] > 0))
                dens *= 1.5f;   // 河流/湖泊水源加成
            ctx.BaseK[i] = dens * cellArea;
            ctx.CellK[i] = ctx.BaseK[i];
        }

        // ── 1. 起源播种（registry 的 OriginModel 在 tick 0 执行）+ tick 循环 ──
        var registry = CivModelRegistry.StoneAge();

        // ── 2. tick 循环 ──
        int stagnant = 0;
        float prevPop = 0f;
        int prevTribes = -1;
        for (ctx.Tick = 0; ctx.Tick < ctx.Epoch.MaxTicks; ctx.Tick++)
        {
            registry.ExecuteAll(ctx);
            RefreshCellState(ctx);

            // 定期清理死亡部落（吞并/合并标记；避免 List 无限膨胀）
            if ((ctx.Tick & 15) == 15)
                ctx.Tribes.RemoveAll(t => t.Dead);

            onProgress?.Invoke((ctx.Tick + 1f) / ctx.Epoch.MaxTicks);   // tick 级进度（后台线程，调用方负责线程安全）

            // 终止：全球人口连续 20 tick 增长 <1% 且部落数不再增长（环境容量 + 社会结构饱和）
            float pop = ctx.TotalPopulation();
            if (pop <= prevPop * 1.01f && ctx.Tribes.Count <= prevTribes) stagnant++;
            else stagnant = 0;
            prevPop = pop;
            prevTribes = ctx.Tribes.Count;
            if (stagnant >= 20 && ctx.Tick >= 40)
                break;
        }

        // ── 3. 终态统计（先清理死亡部落——写档/输出只含存活）──
        ctx.Tribes.RemoveAll(t => t.Dead);
        RefreshCellState(ctx);
        return new CivSimResult { Context = ctx, FinalTick = ctx.Tick + 1 };
    }

    /// <summary>重算每格总人口与当前承载（K = BaseK × 格内部落技术并集乘数；极寒解锁处理）。</summary>
    public static void RefreshCellState(CivSimContext ctx)
    {
        int n = ctx.Grid.N;
        Array.Clear(ctx.CellPop, 0, n);
        var techUnion = new ulong[n];
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var t = ctx.Tribes[i];
            if (t.Dead) continue;
            ctx.CellPop[t.Cell] += t.Population;
            techUnion[t.Cell] |= t.TechFlags;
        }
        for (int i = 0; i < n; i++)
        {
            if (ctx.CellPop[i] <= 0f) { ctx.CellK[i] = ctx.BaseK[i]; continue; }
            float k = ctx.BaseK[i] * TechTable.CarryFactor(techUnion[i]);
            // 火（T01 位 = id 1）解锁极寒：苔原/冰原 K 下限 ×3
            if (TechTable.Has(techUnion[i], 1) && ctx.BaseK[i] <= 0.05f * ctx.Grid.CellAreaKm2)
                k = Mathf.Max(k, 0.05f * ctx.Grid.CellAreaKm2 * 3f);
            ctx.CellK[i] = k;
        }
    }
}

/// <summary>演化结果（输出载体）。</summary>
public sealed class CivSimResult
{
    public CivSimContext Context;
    public int FinalTick;
}
