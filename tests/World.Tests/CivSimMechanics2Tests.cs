using Godot;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using World.Biome;
using World.CivSim;
using World.HexPlanet;
using World.LogicGrid;

using World.CivSim.Entities;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Territory;
using World.CivSim.Mechanics.Politics;
using World.CivSim.Mechanics.Culture;
using World.CivSim.Mechanics.Military;
namespace World.Tests;

/// <summary>
/// CivSim 机制层Ⅱ测试（L0，无引擎）。本文件是 CivSimMechanicTests.cs 的姊妹补充，聚焦：
///   ① CivEngine 纯静态函数（RefreshCellState 三部曲 / RecomputeProduction / DeriveLeadership / SettleDerived）
///   ② 单模型隔离：Harvest / Influence / Absorption / Mode / Invention / Spread / Prestige /
///     Culture / Religion / Conflict / War
///   ③ 模块确定性：CivEngine.Continue 续跑契约（与"从头跑 N+k tick"逐项一致 = T04）。
/// 约束遵循项目铁律：
///   · CivSimContext 是普通类（public 字段直赋），按被测模型 Execute 实际读取的字段补齐。
///   · 构造网格顶点必须归一化为单位向量（GameGrid.DistKm 用 dot 当 cos——Subdivide 返回半径向量）。
///   · 本地执行器只支持 [Test]/[TestCase(字面量)]；不用 Assert.Pass/Ignore/Warn。
///   · 绝调 TechTable.Load()/CivEngine.Run()（FileAccess/GD.Print/PerfLog → 进程级崩溃）。
/// 跳过项与原因（探针确定）：
///   · ModeModel 的"e_农≥e_猎 转向农业"与 Invention/Spread 的正向传播：TechTable 在测试进程恒空
///     （Load 崩溃），FFarmPotentialTerritory/SpreadTech 对缺表的种子 key 会 def==null → NRE，因此
///     只测种子门控退化 + 依赖链纯函数（SyncAgriculture/HeldSeeds/Knowledge）。
///   · Conflict/War 的触发/会战深路径：internal 辅助（ConflictChanceOf/ResolveConflict/CanDeclare/
///     BattleChanceOf/DeclareWars）无 InternalsVisibleTo 且概率门控（0.01/0.002）使确定性触发不可构造
///     ——只测纯 static IsAtWar、朝贡结算、超时停战、非触发安全退化。
/// </summary>
public class CivSimMechanics2Tests
{
    // ══════════════════════════════════════════════════════════════════
    // 语境构造助手（自带一套，不引用既有测试文件的私有方法）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>42 顶点小网格（全陆地/温湿/草原）。Verts 单位化（DistKm/BucketOf 假定单位向量）。</summary>
    private static GameGrid MakeMiniGrid()
    {
        Icosahedron.Subdivide(2, 6371f, out var verts, out var indices);
        int n = verts.Count;
        var unit = new Vector3[n];
        for (int i = 0; i < n; i++) unit[i] = verts[i].Normalized();
        return new GameGrid
        {
            N = n,
            GridN = 2,
            RadiusKm = 6371f,
            Seed = 7,
            Verts = unit,
            Elev = Enumerable.Repeat(1f, n).ToArray(),
            Temp = Enumerable.Repeat(25f, n).ToArray(),
            Precip = Enumerable.Repeat(1500f, n).ToArray(),
            Biome = Enumerable.Repeat((byte)BiomeType.HotSteppe, n).ToArray(),
            SoilLevel = Enumerable.Repeat((byte)3, n).ToArray(),
            LakeLevel = new byte[n],
        };
    }

    /// <summary>4 格直线图 0-1-2-3（OverrideNeighbors 测试钩子）。</summary>
    private static GameGrid PathGrid()
    {
        var g = new GameGrid
        {
            N = 4,
            GridN = 2,
            RadiusKm = 6371f,
            Seed = 7,
            Verts = new[]
            {
                new Vector3(1, 0, 0), new Vector3(0, 1, 0),
                new Vector3(0, 0, 1), new Vector3(-1, 0, 0),
            },
            Elev = Enumerable.Repeat(1f, 4).ToArray(),
            Temp = Enumerable.Repeat(25f, 4).ToArray(),
            Precip = Enumerable.Repeat(1500f, 4).ToArray(),
            Biome = Enumerable.Repeat((byte)BiomeType.HotSteppe, 4).ToArray(),
        };
        g.OverrideNeighbors(new[] { new[] { 1 }, new[] { 0, 2 }, new[] { 1, 3 }, new[] { 2 } });
        return g;
    }

    private static int[] Repeat(int v, int n)
    {
        var a = new int[n];
        Array.Fill(a, v);
        return a;
    }

    private static int GrainIdx => CommodityTable.Index(CommodityTable.Grain);
    private static int BerryIdx => CommodityTable.Index(CommodityTable.Berry);
    private static int LeatherIdx => CommodityTable.Index(CommodityTable.Leather);

    /// <summary>演化全量初始语境（复刻 CivEngine.Run 的字段布局；R 取 8.2e-6 让原生态部落能存续）。</summary>
    private static CivSimContext InitFullCtx(GameGrid grid, int seed)
    {
        int n = grid.N;
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellBands = new Band[n],
            Bands = new List<Band>(),
            Seed = seed,
            OriginCount = 3,
            Rng = new DeterministicRandom(seed),
            R = Enumerable.Repeat(8.2e-6f, n).ToArray(),
            RMax = 8.2e-6f,
            CellF = new float[n],
            CellPop = new float[n],
            CellFarmPop = new float[n],
            Cultivation = new float[n],
            CellOwner = Repeat(-1, n),
            CellBestOwner = Repeat(-1, n),
            CellBestInf = new float[n],
            CellOwnerInf = new float[n],
            LockedUntil = new int[n],
            BfsStamp = new int[n],
            BfsStampValue = 1,
            // ⚠️ 不调 grid.EnsureWildCrops()/Suitability()——它们读 MonthTemp/MonthPrecip，测试网格未设置
            //    （会 NRE）。用全 0 WildCrops + null Suit：狩猎/种子路径不触发（grinding 永不可得），
            //    与既有 CivSimMechanicTests 同款规避（引擎安全 + 确定性）。
            WildCrops = new byte[n],
            Suit = null,
            Tick = 0,
        };
        return ctx;
    }

    /// <summary>手工等价 tick 循环（复刻 CivEngine.Run 主循环；不调 TechTable.Load/PerfLog/GD.Print）。
    /// 与 CivEngine.Continue 逐 tick 语义一致（RefreshCellState + StoneAge 全模型 + 末态 SettleDerived）。</summary>
    private static CivSimContext RunManual(GameGrid grid, int seed, int ticks)
    {
        var ctx = InitFullCtx(grid, seed);
        var registry = CivModelRegistry.StoneAge();
        for (int t = 0; t < ticks; t++)
        {
            ctx.Tick = t;
            CivEngine.RefreshCellState(ctx);
            registry.ExecuteAll(ctx);
        }
        ctx.Bands.RemoveAll(e => e.Dead);
        CivEngine.SettleDerived(ctx);
        ctx.Tick = ticks;
        return ctx;
    }

    /// <summary>代表性末态签名（确定性比较用；浮点固定 6 位防区域性差异）。</summary>
    private static string Signature(CivSimContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append("Tick=").Append(ctx.Tick);
        sb.Append(";Pop=").Append(ctx.TotalPopulation().ToString("0.000000", CultureInfo.InvariantCulture));
        sb.Append(";Bands=").Append(ctx.Bands.Count);
        sb.Append(";Fis=").Append(ctx.Fissions).Append(";Mig=").Append(ctx.Migrations);
        sb.Append(";Conf=").Append(ctx.Conflicts);
        sb.Append(";TradeEv=").Append(ctx.TradeEvents);
        sb.Append(";TradeVol=").Append(ctx.TradeVolume.ToString("0.000000", CultureInfo.InvariantCulture));
        sb.Append(";NextT=").Append(ctx.NextBandId).Append(";NextS=").Append(ctx.NextSettlementId);
        sb.Append(";Settles=").Append(ctx.Settlements.Count).Append(";Wars=").Append(ctx.Wars.Count);
        var ts = ctx.Bands.OrderBy(t => t.Id).ToArray();
        foreach (var t in ts)
        {
            sb.Append("|").Append(t.Id).Append(":").Append(t.Cell)
              .Append(":P").Append(t.P.ToString("0.000000", CultureInfo.InvariantCulture))
              .Append(":F").Append(t.FLast.ToString("0.000000", CultureInfo.InvariantCulture))
              .Append(":FH").Append(t.FHuntLast.ToString("0.000000", CultureInfo.InvariantCulture))
              .Append(":Fm").Append(t.FFarmLast.ToString("0.000000", CultureInfo.InvariantCulture))
              .Append(":Fh").Append(t.FHerdLast.ToString("0.000000", CultureInfo.InvariantCulture))
              .Append(":Pr").Append(t.Prestige.ToString("0.000000", CultureInfo.InvariantCulture))
              .Append(":Co").Append(t.Contributed.ToString("0.000000", CultureInfo.InvariantCulture))
              .Append(":Ter").Append(t.TerritoryId).Append("/").Append(t.TerritorySize)
              .Append(":Ch").Append(t.ChiefdomId).Append("/").Append(t.ChiefdomSize)
              .Append(":St").Append(t.StateId).Append("/").Append(t.StateSize)
              .Append(":Dead").Append(t.Dead ? 1 : 0).Append(":Farm").Append(t.IsFarming ? 1 : 0);
        }
        for (int c = 0; c < ctx.Grid.N; c++)
        {
            sb.Append("[o").Append(ctx.CellOwner[c])
              .Append(":f").Append(ctx.CellF[c].ToString("0.000000", CultureInfo.InvariantCulture))
              .Append(":p").Append(ctx.CellPop[c].ToString("0.000000", CultureInfo.InvariantCulture))
              .Append(":c").Append(ctx.Cultivation[c].ToString("0.000000", CultureInfo.InvariantCulture)).Append(']');
        }
        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════════
    // 1. CivEngine 纯静态函数
    // ══════════════════════════════════════════════════════════════════

    /// <summary>保证：RefreshCellStateCore 把每格变成"一格一实体"单引用、聚合 CellPop/CellFarmPop，
    /// 并刷新 CarryMult/CapMask（灌入 Cells 缓存供后续模型 O(1) 查询）。</summary>
    [Test]
    public void RefreshCore_OneBandPerCell_AggregatesAndCaches()
    {
        var grid = MakeMiniGrid();
        int n = grid.N;
        var hunter = new Band { Id = 0, Cell = 0, P = 10, IsFarming = false };
        hunter.TechKeys.Add(TechTable.StoneCore);
        var farmer = new Band { Id = 1, Cell = 1, P = 20, IsFarming = true };
        farmer.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 1);
        ctx.Bands = new List<Band> { farmer, hunter };
        ctx.CellBands[0] = hunter; ctx.CellBands[1] = farmer;

        CivEngine.RefreshCellStateCore(ctx);

        Assert.AreSame(hunter, ctx.CellBands[0], "一格一实体：hunter 占格 0");
        Assert.AreSame(farmer, ctx.CellBands[1], "farmer 占格 1");
        Assert.IsNull(ctx.CellBands[2], "其余格为空");
        Assert.AreEqual(10f, ctx.CellPop[0], 1e-6f);
        Assert.AreEqual(20f, ctx.CellPop[1], 1e-6f);
        Assert.AreEqual(0f, ctx.CellFarmPop[0], 1e-6f, "非农部落不计入 CellFarmPop");
        Assert.AreEqual(20f, ctx.CellFarmPop[1], 1e-6f);
        Assert.AreEqual(1f, hunter.CarryMult, 1e-6f, "空科技表 HuntingCarry 退化 = 1");
        Assert.True(CapabilityTable.Has(ctx, hunter, CapabilityTable.Settle) == false, "游群无定居");
        Assert.True(CapabilityTable.Has(ctx, farmer, CapabilityTable.Settle), "转农部落 = Settle 能力（缓存）");
    }

    /// <summary>保证：AccumulateStorage 对随身池按"基础年率折算 tick"衰变（存储科技保护只作用于粮仓）。
    /// 耐储契约（CommodityTable 表值即契约）：谷物(0.08/年) 远耐于 浆果(0.5/年)——
    /// 新石器革命因果链的存储端。皮革(0.03/年)作为 Material 比谷物更耐储。</summary>
    [Test]
    public void AccumulateStorage_CarryPool_DecaysByBaseYearRate()
    {
        var e = new Band { Id = 0, Cell = 0, P = 100 };
        e.Stocks[GrainIdx] = CivSimContext.CarryFoodCap * 100f;      // 6（等于随身容量上限，避免 clamp 干扰）
        e.Stocks[BerryIdx] = CivSimContext.CarryFoodCap * 100f;      // 6（浆果）
        e.Stocks[LeatherIdx] = CivSimContext.CarryMatCap * 100f;     // 2
        var ctx = InitFullCtx(MakeMiniGrid(), 1);
        ctx.Bands = new List<Band> { e };
        ctx.Settlements = new List<Settlement>();   // 无聚落 = 游群（随身即全部）

        CivEngine.AccumulateStorage(ctx);

        // carryDecay = 1 − (1 − BaseDecay)^TickYears(100)；衰变后低于容量上限 → 不被 clamp 抬升
        float grainDecay = 1f - Mathf.Pow(1f - 0.08f, (float)CivSimContext.TickYears);
        float berryDecay = 1f - Mathf.Pow(1f - 0.5f, (float)CivSimContext.TickYears);
        float leatherDecay = 1f - Mathf.Pow(1f - 0.03f, (float)CivSimContext.TickYears);
        Assert.AreEqual(6f * (1f - grainDecay), e.Stocks[GrainIdx], 1e-4f, "谷物随身衰变（年率 0.08）");
        Assert.AreEqual(2f * (1f - leatherDecay), e.Stocks[LeatherIdx], 1e-4f, "皮革随身衰变（年率 0.03）");
        Assert.Greater(1f - grainDecay, 1f - berryDecay, "谷物耐储：存留比例高于浆果（耐储因果链）");
        Assert.Less(CommodityTable.All[LeatherIdx].BaseDecay, CommodityTable.All[GrainIdx].BaseDecay,
            "皮革（材料）年衰变率应低于谷物（表值即契约）");
    }

    /// <summary>保证：Material 副产流入**粮仓优先**（正式存储归聚落），随身只收溢余。</summary>
    [Test]
    public void AccumulateStorage_MaterialInflow_GranaryFirstThenCarry()
    {
        var e = new Band { Id = 0, Cell = 0, P = 100, IsFarming = true, PlaceId = 0 };
        e.FHuntLast = 10f;          // 狩猎产出 → 皮革副产 = 10×0.10 = 1.0
        var s = new Settlement { Id = 0, Cell = 0, Level = 0, OccupantId = e.Id, DwellFrom = 0 };
        var ctx = InitFullCtx(MakeMiniGrid(), 1);
        ctx.Bands = new List<Band> { e };
        ctx.Settlements = new List<Settlement> { s };

        CivEngine.AccumulateStorage(ctx);

        // 粮仓皮革容量 = 0.2×(1+0.5×0)×100 = 20 > 1.0 → 全部入仓
        Assert.AreEqual(1.0f, s.Stocks[LeatherIdx], 1e-4f, "皮革流入粮仓（仓容量足够）");
        Assert.AreEqual(0f, e.Stocks[LeatherIdx], 1e-4f, "随身不收（无溢余）");
    }

    /// <summary>保证：随身/粮仓容量统一 clamp——贸易/流入可能超限，下 tick 归位到上限。
    /// ⚠️ 用耐储材料（秸秆 BaseDecay=0.01 → 100t 后仍保留 36.6%）让"超限→归位"可观测：
    ///   谷物/浆果随身池经 100 年复利衰变（×0.0002）后远低于上限，clamp 语义无法由它们观测。
    /// （Food → CarryFoodCap×P / SettleFoodCap×P×levelMult；Material → CarryMatCap/SettleMatCap）。</summary>
    [Test]
    public void AccumulateStorage_CapacityClamps_OvershootToCap()
    {
        var e = new Band { Id = 0, Cell = 0, P = 100 };
        e.Stocks[CommodityTable.Index(CommodityTable.Straw)] = 5f * CivSimContext.CarryMatCap * 100f;   // 超上限 5×（秸秆随身）
        var ctx = InitFullCtx(MakeMiniGrid(), 1);
        ctx.Bands = new List<Band> { e };
        ctx.Settlements = new List<Settlement>();   // 游群

        CivEngine.AccumulateStorage(ctx);

        // 5×上限=10 → 衰变 0.99^100≈0.366 → 3.66 > 上限 2 → clamp 到 2
        Assert.AreEqual(CivSimContext.CarryMatCap * 100f, e.Stocks[CommodityTable.Index(CommodityTable.Straw)], 1e-4f,
            "超限随身材料（秸秆）归位到上限 0.02×P");
    }

    /// <summary>保证：RecomputeProduction 按领地分配产出，FLast = FHunt+FFarm+FHerd 分量分解；
    /// 重复调用给出确定性相同结果（读档续跑无分叉的基础——派生态从持久字段一律重算）。</summary>
    [Test]
    public void RecomputeProduction_HunterTerritory_DecomposesDeterministically()
    {
        var grid = MakeMiniGrid();
        int n = grid.N;
        var e = new Band { Id = 0, Cell = 0, P = 100 };
        e.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 1);
        ctx.Bands = new List<Band> { e };
        ctx.CellBands[0] = e;
        ctx.R = new float[n];
        ctx.R[0] = 1e-5f;                       // 单格领地高生产力
        ctx.RMax = 1e-5f;
        ctx.Cultivation = new float[n];          // 全 0（未开垦，纯采集）
        ctx.TerritoryCells = new List<int>[e.Id + 1];
        ctx.TerritoryDists = new List<byte>[e.Id + 1];
        ctx.TerritoryCells[0] = new List<int> { 0 };
        ctx.TerritoryDists[0] = new List<byte> { 0 };

        CivEngine.RecomputeProduction(ctx);
        var fh1 = e.FHuntLast; var fl1 = e.FLast; var fb1 = e.FBerryLast;
        Assert.Greater(fh1, 0f, "领地采集产出为正");
        Assert.AreEqual(0f, e.FFarmLast, 1e-6f, "非农部落无农业分量");
        Assert.AreEqual(0f, e.FHerdLast, 1e-6f, "无畜牧能力无畜牧分量");
        Assert.AreEqual(fl1, fh1 + e.FFarmLast + e.FHerdLast, 1e-4f, "FLast = Σ分量（领地采集）");
        Assert.Greater(fb1, 0f);
        Assert.Less(fb1, fh1, "浆果是采集一部分，非全部");

        CivEngine.RecomputeProduction(ctx);     // 幂等：二次同样结果
        Assert.AreEqual(fh1, e.FHuntLast, 1e-6f, "重复重建 FHunt 相同");
        Assert.AreEqual(fl1, e.FLast, 1e-6f, "重复重建 FLast 相同");
        Assert.AreEqual(fb1, e.FBerryLast, 1e-6f);
    }

    /// <summary>保证：DeriveLeadership 从持久字段确定性派生首领标记——IsBigMan = Prestige≥阈值(1.0)；
    /// IsChief = BigMan 且祖先宗教份额(谱系合法性)>0。</summary>
    [Test]
    public void DeriveLeadership_BigManAndChief_FromPrestigeAndReligion()
    {
        var zero = new Band { Prestige = 0.5f };
        zero.ReligionShare = ShareField.NewReligion(ReligionStage.Animism);
        CivEngine.DeriveLeadership(zero);
        Assert.False(zero.IsBigMan, "声望 0.5 < 1.0 非 BigMan");
        Assert.False(zero.IsChief);

        var big = new Band { Prestige = 1.0f };
        big.ReligionShare = ShareField.NewReligion(ReligionStage.Animism);
        CivEngine.DeriveLeadership(big);
        Assert.True(big.IsBigMan, "声望达标 = BigMan");
        Assert.False(big.IsChief, "BigMan 无祖先宗教（泛灵）≠ 酋长");

        var chief = new Band { Prestige = 1.0f };
        chief.ReligionShare = ShareField.NewReligion(ReligionStage.Ancestor);
        CivEngine.DeriveLeadership(chief);
        Assert.True(chief.IsBigMan);
        Assert.True(chief.IsChief, "BigMan + 祖先崇拜（谱系） = 酋长（Polynesia divine kingship）");
    }

    /// <summary>保证：SettleDerived 是幂等派生重建（读档/Run 结尾/Continue 三入口同函数，
    /// 同一输入调用两次逐字段一致）——杜绝"重算路径各写一套"的 T04 类分叉。</summary>
    [Test]
    public void SettleDerived_Idempotent_SecondCallIdentical()
    {
        var grid = MakeMiniGrid();
        int n = grid.N;
        var a = new Band { Id = 0, Cell = 0, P = 40, FLast = 30 };
        a.TechKeys.Add(TechTable.StoneCore);
        a.CultureGroupShare = ShareField.NewCulture("g");
        a.Prestige = 2.0f; a.ReligionShare = ShareField.NewReligion(ReligionStage.Ancestor);   // 酋长候选
        var b = new Band { Id = 1, Cell = 1, P = 30, FLast = 25 };
        b.TechKeys.Add(TechTable.StoneCore);
        b.CultureGroupShare = ShareField.NewCulture("g");
        var ctx = InitFullCtx(grid, 2);
        ctx.Bands = new List<Band> { a, b };
        ctx.CellBands[0] = a; ctx.CellBands[1] = b;

        CivEngine.SettleDerived(ctx);
        string s1 = Signature(ctx);
        CivEngine.SettleDerived(ctx);
        string s2 = Signature(ctx);

        Assert.AreEqual(s1, s2, "SettleDerived 连调两次必须逐字段一致（幂等纯派生）");
    }

    // ══════════════════════════════════════════════════════════════════
    // 2. 单模型隔离
    // ══════════════════════════════════════════════════════════════════

    /// <summary>保证：HarvestModel 把领地分配产出写入 FHuntLast 并分解 FLast=Σ分量（FBerry 为浆果子集）。</summary>
    [Test]
    public void Harvest_CollectIterator_FLastIsSumOfComponents()
    {
        var grid = MakeMiniGrid();
        int n = grid.N;
        var e = new Band { Id = 0, Cell = 0, P = 100 };
        e.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 1);
        ctx.Bands = new List<Band> { e };
        ctx.CellBands[0] = e;
        ctx.R = new float[n];
        ctx.R[0] = 1e-5f; ctx.RMax = 1e-5f;
        ctx.Cultivation = new float[n];
        ctx.TerritoryCells = new List<int>[1];
        ctx.TerritoryDists = new List<byte>[1];
        ctx.TerritoryCells[0] = new List<int> { 0, 1 };   // 领地含两格（含一格 0 生产力，验证跳过）
        ctx.TerritoryDists[0] = new List<byte> { 0, 1 };

        new HarvestModel().Execute(ctx);

        Assert.Greater(e.FHuntLast, 0f);
        Assert.AreEqual(0f, e.FFarmLast, 1e-6f);
        Assert.AreEqual(0f, e.FHerdLast, 1e-6f);
        Assert.AreEqual(e.FLast, e.FHuntLast + e.FFarmLast + e.FHerdLast, 1e-4f, "FLast = Σ分量");
        Assert.Less(e.FBerryLast, e.FHuntLast + 1e-6f, "浆果 ≤ 采集总量");
    }

    /// <summary>保证：ColdFloor 冷区下限——火解锁 0.05·area·3、皮毛再 ×3（技术解锁空间层的冰雪生态位）。</summary>
    [Test]
    public void ColdFloor_RaisesSurvivalFloor_ByFireAndClothing()
    {
        var grid = MakeMiniGrid();
        var fire = new Band { Id = 0, Cell = 0, P = 10 };
        fire.TechKeys.Add(TechTable.Fire);
        var both = new Band { Id = 1, Cell = 0, P = 10 };
        both.TechKeys.Add(TechTable.Fire);
        both.TechKeys.Add(TechTable.Clothing);
        var plain = new Band { Id = 2, Cell = 0, P = 10 };
        var ctx = InitFullCtx(grid, 1);
        // 把格 0 换成寒冷区（Tundra）以便触发下限路径
        var g2 = MakeMiniGrid();
        g2.Biome[0] = (byte)BiomeType.Tundra;
        ctx.Grid = g2;

        float area = g2.CellAreaKm2;
        Assert.AreEqual(0f, ctx.ColdFloor(plain), 1e-4f, "无火寒冷区无下限");
        Assert.AreEqual(0.05f * area * 3f, ctx.ColdFloor(fire), 1e-4f * area, "火解锁基础下限");
        Assert.AreEqual(0.05f * area * 9f, ctx.ColdFloor(both), 1e-4f * area, "皮毛再 ×3（0.05·area·3·3）");
    }

    /// <summary>保证：InfluenceModel 影响力场归属 = argmax(P×M×w(d))，粘性 1.15；
    /// 强族能覆盖弱族**驻扎格**（弱 band 家被吞——Absorption 前置条件）；远格权重按紧支撑核衰减。</summary>
    [Test]
    public void Influence_StrongOverlaysWeakHome_StickyField()
    {
        var grid = PathGrid();
        var a = new Band { Id = 0, Cell = 0, P = 1000 };   // 强
        a.TechKeys.Add(TechTable.StoneCore);
        var b = new Band { Id = 1, Cell = 1, P = 10 };     // 弱
        b.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 3);
        ctx.Bands = new List<Band> { a, b };
        ctx.CellBands[0] = a; ctx.CellBands[1] = b;
        ctx.R = new float[4] { 1f, 1f, 1f, 1f };         // 全陆地可居

        new InfluenceModel().Execute(ctx);

        // A 强度 1000、B 强度 10：A 的紧支撑核（w:1/.544/.192/0）覆盖整条链上近前 3 格；
        // B 驻扎格(1)被 A 覆盖（A 544 >> B 10），只有 B 能凭"远格权重衰减到 0"守住格 3。
        Assert.AreEqual(0, ctx.CellOwner[0], "A 家格归 A");
        Assert.AreEqual(0, ctx.CellOwner[1], "强 A 覆盖弱 B 驻扎格（家 w=1 也守不住）");
        Assert.AreEqual(0, ctx.CellOwner[2], "A 权重 0.192×1000=192 仍压过 B 的 0.544×10 —— 紧支撑核让强族扩张");
        Assert.AreEqual(1, ctx.CellOwner[3], "A 权重在格3 衰减到 0 → 弱 B 保住远格（弱族靠距离保住边角）");
        Assert.True(ctx.TerritoryOf(b).Contains(3), "B 领地 = 其实际归属格集合（仅保住的远格）");
        Assert.False(ctx.TerritoryOf(b).Contains(2), "被覆盖格不入 B 领地");
    }

    /// <summary>保证：AbsorptionModel 散兵穿越被覆盖→无主格可逃则迁走（保留身份流亡）；否则并入
    /// （P×0.5 转移，战斗损耗+同化）。条件= 跨势力 + 覆盖者更强。</summary>
    [Test]
    public void Absorption_WithExileCell_MigratesAway()
    {
        var grid = PathGrid();
        var a = new Band { Id = 0, Cell = 0, P = 100 };
        a.TechKeys.Add(TechTable.StoneCore);
        var b = new Band { Id = 1, Cell = 1, P = 20, ChiefdomId = -1, TerritoryId = -1 };
        b.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 3);
        ctx.Bands = new List<Band> { a, b };
        ctx.CellBands[0] = a; ctx.CellBands[1] = b;
        ctx.R = new float[4] { 1f, 1f, 2f, 1f };                 // 格 2 最富饶且无主
        ctx.CellOwner = new int[4] { 0, 0, -1, -1 };             // B 驻扎格(1)被 A 覆盖
        ctx.TerritoryCells = new List<int>[2];
        ctx.TerritoryDists = new List<byte>[2];
        ctx.TerritoryCells[0] = new List<int> { 0 };
        ctx.TerritoryDists[0] = new List<byte> { 0 };
        ctx.TerritoryCells[1] = new List<int> { 1, 2 };          // B 领地含无主格 2
        ctx.TerritoryDists[1] = new List<byte> { 0, 1 };
        ctx.Tick = 10; ctx.AbsorptionLastEval = 0;

        new AbsorptionModel().Execute(ctx);

        Assert.False(b.Dead, "有可逃格 → 不吞并");
        Assert.AreEqual(2, b.Cell, "迁到领地内最高富饶无主格");
        Assert.AreEqual(10, b.LastMigrateTick);
        Assert.AreEqual(100f, a.P, 1e-6f, "强族未被削弱（未到并入分支）");
    }

    /// <summary>保证：AbsorptionModel 无无主格可逃 → 并入：overlord.P += e.P×0.5，弱方消亡。</summary>
    [Test]
    public void Absorption_NoExile_MergesHalfPopulation()
    {
        var grid = PathGrid();
        var a = new Band { Id = 0, Cell = 0, P = 100 };
        a.TechKeys.Add(TechTable.StoneCore);
        var b = new Band { Id = 1, Cell = 1, P = 20, ChiefdomId = -1, TerritoryId = -1 };
        b.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 3);
        ctx.Bands = new List<Band> { a, b };
        ctx.CellBands[0] = a; ctx.CellBands[1] = b;
        ctx.R = new float[4] { 1f, 1f, 1f, 1f };
        ctx.CellOwner = new int[4] { 0, 0, -1, -1 };             // B 驻扎格被覆盖
        ctx.TerritoryCells = new List<int>[2];
        ctx.TerritoryDists = new List<byte>[2];
        ctx.TerritoryCells[0] = new List<int> { 0 };
        ctx.TerritoryDists[0] = new List<byte> { 0 };
        ctx.TerritoryCells[1] = new List<int> { 1 };              // 领地无无主格
        ctx.TerritoryDists[1] = new List<byte> { 0 };
        ctx.Tick = 10; ctx.AbsorptionLastEval = 0;

        new AbsorptionModel().Execute(ctx);

        Assert.True(b.Dead, "无主格 → 并入");
        Assert.AreEqual(0f, b.P, 1e-6f);
        Assert.AreEqual(110f, a.P, 1e-6f, "强族吞并 +20×0.5=110（战斗损耗+同化）");
    }

    /// <summary>保证：ModeModel 种子门控——部落无种子（转农前提缺失）→ 强制生产方式=狩猎
    /// （种子压力触发发明是转农唯一入口；无种子不可能农业）。</summary>
    [Test]
    public void Mode_NoSeed_ForcesNonFarming()
    {
        var grid = MakeMiniGrid();
        var e = new Band { Id = 0, Cell = 0, P = 10, IsFarming = true };   // 预置错误地"已转农"
        e.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 5);
        ctx.Bands = new List<Band> { e };
        ctx.CellBands[0] = e;

        new ModeModel().Execute(ctx);

        Assert.False(e.IsFarming, "无种子（无 Seed 能力）→ 生产方式被强制重置为狩猎");
        Assert.AreEqual(-1, ctx.FirstFarmTick, "无转农 → FirstFarmTick 不锚定（终止条件不触发）");
    }

    /// <summary>保证：科技依赖链纯函数——持任一种子 key → agriculture 母科技自动置位；
    /// Knowledge = 已获科技数（Kremer 累积项）、HeldSeeds 枚举实存种子。</summary>
    [Test]
    public void Invention_SeedDependencyChain_PureHelpers()
    {
        var keys = new HashSet<string> { TechTable.SeedWheat };
        Assert.AreEqual(1, TechTable.HeldSeeds(keys).Count, "已持小麦种子");
        Assert.AreEqual(1, TechTable.Knowledge(keys));
        TechTable.SyncAgriculture(keys);
        Assert.True(keys.Contains(TechTable.Agriculture), "持种子 → 母科技 agriculture 自动置位（依赖链顶端）");

        var empty = new HashSet<string> { TechTable.StoneCore };
        TechTable.SyncAgriculture(empty);
        Assert.False(empty.Contains(TechTable.Agriculture), "无种子不派生 agriculture");
    }

    /// <summary>保证：InventionModel 在空科技表下安全退化（无通用发明、无种子发明冲突），不崩溃、不改状态。</summary>
    [Test]
    public void Invention_EmptyTable_SafeDegradation()
    {
        var grid = MakeMiniGrid();
        var e = new Band { Id = 0, Cell = 0, P = 100 };
        e.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 5);
        ctx.Bands = new List<Band> { e };
        ctx.CellBands[0] = e;
        // 通用发明遍历 TechTable.All（空）→ 无任何新科技；种子路径因无 grinding → 不触发
        new InventionModel().Execute(ctx);
        Assert.AreEqual(1, e.TechKeys.Count, "空科技表无新增发明");
        Assert.False(e.Dead);
    }

    /// <summary>保证：SpreadModel 在空科技表下安全退化（传播源 key 查表为空 → 无可传技术，不崩溃）。</summary>
    [Test]
    public void Spread_EmptyTable_SafeDegradation()
    {
        var grid = PathGrid();
        var a = new Band { Id = 0, Cell = 0, P = 10 };
        a.TechKeys.Add(TechTable.StoneCore);
        var b = new Band { Id = 1, Cell = 1, P = 20 };
        b.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 5);
        ctx.Bands = new List<Band> { a, b };
        ctx.CellBands[0] = a; ctx.CellBands[1] = b;

        new SpreadModel().Execute(ctx);

        Assert.AreEqual(1, b.TechKeys.Count, "空表无可传播技术，B 未获新科技");
        Assert.AreEqual(1, a.TechKeys.Count);
    }

    /// <summary>保证：PrestigeModel 绝对盈余 → 声望（surplus×Rate）；BigMan 阈值随领袖派生即时生效。</summary>
    [Test]
    public void Prestige_SurplusAccrues_BigManAtThreshold()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 20 };   // 盈余 10
        e.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(MakeMiniGrid(), 5);
        ctx.Bands = new List<Band> { e };

        new PrestigeModel().Execute(ctx);

        Assert.AreEqual(10f * CivSimContext.PrestigeGainRate, e.Prestige, 1e-5f, "声望 = 绝对盈余×0.02 = 0.2");
        Assert.False(e.IsBigMan, "0.2 < 1.0 阈值，尚未 BigMan");
    }

    /// <summary>保证：PrestigeModel 无盈余 → 声望可逆衰减（Big Man 个人化，Sahlins）；不回升。</summary>
    [Test]
    public void Prestige_NoSurplus_DecaysTowardZero()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 5, Prestige = 2.0f };   // 缺口 5
        e.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(MakeMiniGrid(), 5);
        ctx.Bands = new List<Band> { e };

        new PrestigeModel().Execute(ctx);

        Assert.AreEqual(2.0f - CivSimContext.PrestigeDecay, e.Prestige, 1e-5f, "无盈余衰变 0.001/tick");
    }

    /// <summary>保证：PrestigeModel 酋长精英供养——贡赋池足则按成员贡献比例扣减（实物税），
    /// 池不足则酋长自身 P 被精英饿死式削减。此处验证足池正常供养路径。</summary>
    [Test]
    public void Prestige_ChiefSustained_ByTributePool()
    {
        var chief = new Band { Id = 0, Cell = 0, P = 10, FLast = 10, ChiefdomId = 0 };
        chief.TechKeys.Add(TechTable.StoneCore);
        chief.Prestige = 2.0f;
        chief.ReligionShare = ShareField.NewReligion(ReligionStage.Ancestor);   // IsChief 派生成立
        var member = new Band { Id = 1, Cell = 1, P = 5, FLast = 5, ChiefdomId = 0, Contributed = 2.0f };
        member.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(MakeMiniGrid(), 5);
        ctx.Bands = new List<Band> { member, chief };

        new PrestigeModel().Execute(ctx);

        // 只有 chief 走精英供养：elite = 10×0.1 = 1.0 ≤ pool=2.0 → 从成员贡献扣 1.0
        Assert.AreEqual(1.0f, member.Contributed, 1e-5f, "贡赋池向酋长转移 1.0（实物税）");
        Assert.AreEqual(10f, chief.P, 1e-5f, "池足 → 酋长人口不削减");
        Assert.AreEqual(0f, chief.Contributed, 1e-5f);   // 酋长自身无盈余贡献（FLast=P）
    }

    /// <summary>保证：CultureModel 相邻同语言群、异文化部落：弱方（P 小）文化向强方主导文化转移
    /// （CultureSpreadRate×BorderCost）——文化横向传播的 Axelrod 语义（格级混合）。</summary>
    [Test]
    public void Culture_AdjacentSameGroup_WeakLearnsStrong()
    {
        var grid = PathGrid();
        var weak = new Band { Id = 0, Cell = 0, P = 10 };
        weak.CultureGroupShare = ShareField.NewCulture("g");
        weak.CultureShare = ShareField.NewCulture("a");
        var strong = new Band { Id = 1, Cell = 1, P = 20 };
        strong.CultureGroupShare = ShareField.NewCulture("g");
        strong.CultureShare = ShareField.NewCulture("b");
        var ctx = InitFullCtx(grid, 5);
        ctx.Bands = new List<Band> { weak, strong };
        ctx.CellBands[0] = weak; ctx.CellBands[1] = strong;
        // 同温湿草原 → BorderCost = TerrainCost(1)×ClimateSim(1) = 1
        int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.CultureSpreadRate * 1f);   // round(12.75)=13

        new CultureModel().Execute(ctx);

        Assert.AreEqual("g", ShareField.DomKey(weak.CultureGroupShare), "语言群边界允许跨界传播");
        Assert.AreEqual((byte)(255 - amt), ShareField.DomFrac(weak.CultureShare), "弱方主导文化让出 amt 份");
        Assert.AreEqual("b", ShareField.SecKey(weak.CultureShare), "强方文化进入弱方次席");
        Assert.AreEqual((byte)amt, ShareField.SecFrac(weak.CultureShare));
        Assert.AreEqual("b", ShareField.DomKey(strong.CultureShare), "强方主导不变");
    }

    /// <summary>保证：ReligionModel 升级链——泛灵→萨满（盈余+细石器）、萨满→祖先（定居=农业派生）
    /// 每 tick 份额转移 ReligionUpgradeRate（0.05），比例守恒。</summary>
    [Test]
    public void Religion_Upgrade_AnimismToShamanThenAncestor()
    {
        var grid = PathGrid();
        var shamanCan = new Band { Id = 0, Cell = 0, P = 10, Surplus = 1f };
        shamanCan.TechKeys.Add(TechTable.Microlith);                    // 细石器 → 萨满
        shamanCan.ReligionShare = ShareField.NewReligion(ReligionStage.Animism);
        var ancestorCan = new Band { Id = 1, Cell = 3, P = 10, Surplus = 1f, IsFarming = true };
        ancestorCan.TechKeys.Add(TechTable.Microlith);                   // 定居=农业派生 → 祖先
        ancestorCan.ReligionShare = ShareField.NewReligion(ReligionStage.Shaman);
        var ctx = InitFullCtx(grid, 5);
        ctx.Bands = new List<Band> { shamanCan, ancestorCan };
        ctx.CellBands[0] = shamanCan; ctx.CellBands[3] = ancestorCan;   // 分置非相邻格(0/3)，隔离"传播"只测"升级"
        int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionUpgradeRate);   // 13

        new ReligionModel().Execute(ctx);

        Assert.AreEqual(255 - amt, ShareField.RelFrac(shamanCan.ReligionShare, ReligionStage.Animism), "泛灵让出 amt");
        Assert.AreEqual(amt, ShareField.RelFrac(shamanCan.ReligionShare, ReligionStage.Shaman), "泛灵→萨满");
        Assert.AreEqual(255 - amt, ShareField.RelFrac(ancestorCan.ReligionShare, ReligionStage.Shaman), "萨满让出 amt");
        Assert.AreEqual(amt, ShareField.RelFrac(ancestorCan.ReligionShare, ReligionStage.Ancestor), "萨满→祖先（定居）");
    }

    /// <summary>保证：ConflictModel 在无粘性僵局/单方独占时安全不触发（不改变归属、不计数、不崩溃）——
    /// 无挑战者则场保持和平归属。触发/锁定深路径需 internal 辅助，此处测守卫退化。</summary>
    [Test]
    public void Conflict_NoStalemate_SafeNoop()
    {
        var grid = PathGrid();
        var a = new Band { Id = 0, Cell = 0, P = 1000 };
        a.TechKeys.Add(TechTable.StoneCore);
        var b = new Band { Id = 1, Cell = 3, P = 1000 };   // 远隔，互不争格
        b.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(grid, 5);
        ctx.Bands = new List<Band> { a, b };
        ctx.CellBands[0] = a; ctx.CellBands[3] = b;
        ctx.R = new float[4] { 1f, 1f, 1f, 1f };
        new InfluenceModel().Execute(ctx);   // 建立归属场（无僵持：每格 argmax 唯一且稳定）
        int[] before = (int[])ctx.CellOwner.Clone();
        int conflicts = ctx.Conflicts;

        new ConflictModel().Execute(ctx);

        Assert.AreEqual(conflicts, ctx.Conflicts, "无粘性僵持 → 冲突计数不变");
        CollectionAssert.AreEqual(before, ctx.CellOwner, "归属场未被武力改动");
    }

    /// <summary>保证：WarModel 朝贡期结算——每 tick 战败方总人口×WarTributeRate 转移入战胜方贡赋池，
    /// TributesLeft 递减；朝贡随战争存在，归零即停战。</summary>
    [Test]
    public void War_TributeTransfer_MovesPoolAndCountsDown()
    {
        var winner = new Band { Id = 1, Cell = 0, P = 100, Contributed = 0f, StateId = -1 };
        winner.TechKeys.Add(TechTable.StoneCore);
        var loser = new Band { Id = 2, Cell = 3, P = 100, Contributed = 50f, StateId = -1 };
        loser.TechKeys.Add(TechTable.StoneCore);
        var ctx = InitFullCtx(PathGrid(), 5);
        ctx.Bands = new List<Band> { winner, loser };
        ctx.CellBands[0] = winner; ctx.CellBands[3] = loser;
        ctx.ChiefdomCells = new List<int>[64];
        for (int i = 0; i < 64; i++) ctx.ChiefdomCells[i] = new List<int>();
        ctx.ChiefdomCells[1] = new List<int> { 1 };
        ctx.ChiefdomCells[2] = new List<int> { 2 };
        ctx.Wars = new List<War>
        {
            new War { StateIdA = 1, StateIdB = 2, Defender = 2, StartTick = 0, LastBattleTick = 0,
                      TributeTo = 1, TributeFrom = 2, TributesLeft = 5 },
        };
        ctx.Tick = 10; ctx.WarsDeclared = 0;

        new WarModel().Execute(ctx);

        float amount = 100f * CivSimContext.WarTributeRate;   // 100×0.005 = 0.5
        Assert.AreEqual(0.5f, winner.Contributed, 1e-5f, "朝贡流入战胜方池");
        Assert.AreEqual(50f - amount, loser.Contributed, 1e-4f, "战败方分担贡赋");
        Assert.AreEqual(4, ctx.Wars[0].TributesLeft, "TributesLeft 递减");
        Assert.AreEqual(1, ctx.Wars.Count, "朝贡期战争保留（未移除）");
        Assert.AreEqual(0, ctx.WarsDeclared, "成员非国家状态 → 本 tick 无新宣战");
    }

    /// <summary>保证：WarModel 超时停战——非朝贡战争持续 ≥ WarMaxTicks(60) 即从 Wars 移除（6000 年防死锁）。</summary>
    [Test]
    public void War_Timeout_CeasesAfterMaxTicks()
    {
        var ctx = InitFullCtx(PathGrid(), 5);
        ctx.Wars = new List<War>
        {
            new War { StateIdA = 1, StateIdB = 2, Defender = 2, StartTick = 0, LastBattleTick = 0 },
        };
        ctx.Tick = CivSimContext.WarMaxTicks;   // 60

        new WarModel().Execute(ctx);

        Assert.AreEqual(0, ctx.Wars.Count, "达到 WarMaxTicks → 停战移除（无赔偿）");
    }

    // ══════════════════════════════════════════════════════════════════
    // 3. 模块确定性 —— CivEngine.Continue 续跑契约（T04）
    //    CivEngine.Continue 只调 RefreshCellState/registry/SettleDerived（源码确认无引擎调用，
    //    无 TechTable.Load/PerfLog/GD.Print）→ 可直接测读档续跑无分叉。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>保证：Continue 相同输入跑两次 → 末态逐项一致（确定性续跑）。</summary>
    [Test]
    public void Continue_SameStateTwice_IdenticalFinalState()
    {
        var grid = MakeMiniGrid();
        var a = RunManual(grid, 11, 25);
        var b = RunManual(grid, 11, 25);

        CivEngine.Continue(a, 5, null);
        CivEngine.Continue(b, 5, null);

        Assert.AreEqual(Signature(a), Signature(b), "同状态两次 Continue(k) 必须逐项一致");
    }

    /// <summary>保证：T04 核心契约——"从头跑 N+k tick" 与 "跑 N tick 后 Continue(k)" 末态逐项一致
    /// （读档续跑无分叉；状态经 Save/Settle → Continue 无缝衔接）。</summary>
    [Test]
    public void Continue_ResumeEqualsFreshRun_NoDivergence()
    {
        var grid = MakeMiniGrid();
        int n = 25, k = 5;
        var fresh = RunManual(grid, 11, n + k);          // 从头直接跑到 n+k
        var resume = RunManual(grid, 11, n);             // 先跑到 n（含 SettleDerived 边界重建）

        CivEngine.Continue(resume, k, null);             // 读档续跑 k tick

        Assert.AreEqual(fresh.Tick, resume.Tick, "终止 tick 一致");
        Assert.AreEqual(Signature(fresh), Signature(resume), "读档续跑与从头全跑必须逐项一致（T04 无分叉）");
    }
}
