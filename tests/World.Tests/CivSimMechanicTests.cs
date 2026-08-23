using Godot;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using World.Biome;
using World.CivSim;
using World.HexPlanet;
using World.LogicGrid;

using World.CivSim.Entities;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Territory;
using World.CivSim.Mechanics.Politics;
using World.CivSim.Mechanics.State;
using World.CivSim.Mechanics.Culture;
using World.CivSim.Mechanics.Military;
namespace World.Tests;

/// <summary>
/// CivSim 机制层测试（L0，无引擎）：单模型隔离执行 + 模块确定性对比。
/// 构造约定（与 scripts/CivSim 源码逐一对照）：
///   · CivSimContext 是普通类（public 字段直赋），按被测模型 Execute 实际读取的字段补齐。
///   · 小网格 = Icosahedron.Subdivide(2, 6371f) → 42 顶点；Verts 归一化为单位向量
///     （GameGrid.DistKm 用 Verts.Dot 当 cos，必须单位化——Subdivide 返回的是半径缩放向量）。
///   · 自定义图用 GameGrid.OverrideNeighbors（源码注明"仅测试用"钩子）。
///   · 本地执行器只支持 [Test]/[TestCase(字面量)]：无 SetUp/OneTime/TestCaseSource/Theory；
///     只用普通断言（无 Assert.Pass/Ignore/Warn）。
/// 不可测路径（探针确定，勿在此触发）：
///   · TechTable.Load() 用 FileAccess/LogService → 进程级崩溃 0xC0000005；表恒为空
///     （All=0、Get→null）→ 凡依赖 TechTable.Get 的科技路径安全退化，常量/纯方法不受影响。
///   · CivEngine.Run() 调 TechTable.Load + PerfLog.Append(写历史) + GD.Print → 不可测；
///     模块确定性改为"手工等价 tick 循环 + StoneAge 注册表全模型"，两次执行状态逐项对比。
///   · internal 静态（BattleChanceOf/CanDeclare/ConflictChanceOf/ColonizeScore）无
///     InternalsVisibleTo，不可直接断言 → 经公开 Execute 行为间接覆盖（分裂/迁徙/贸易），
///     战争/冲突深路径跳过（上下文极难构造且内部辅助不可达）。
/// </summary>
public class CivSimMechanicTests
{
    // ══════════════════════════════════════════════════════════════════
    // 上下文构造助手
    // ══════════════════════════════════════════════════════════════════

    /// <summary>42 顶点小网格（全陆地/温湿/草原，便于各类模型跑通）。Verts 单位化。</summary>
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
            Verts = unit,
            Elev = Enumerable.Repeat(1f, n).ToArray(),
            Temp = Enumerable.Repeat(25f, n).ToArray(),
            Precip = Enumerable.Repeat(1500f, n).ToArray(),
            Biome = Enumerable.Repeat((byte)BiomeType.HotSteppe, n).ToArray(),
            SoilLevel = Enumerable.Repeat((byte)3, n).ToArray(),
            LakeLevel = new byte[n],
        };
    }

    /// <summary>4 格直线图 0-1-2-3（OverrideNeighbors 测试钩子；单位向量垂直放置——DistKm 可算）。</summary>
    private static GameGrid PathGrid()
    {
        var g = new GameGrid
        {
            N = 4,
            GridN = 2,
            RadiusKm = 6371f,
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
    private static int MeatIdx => CommodityTable.Index(CommodityTable.Meat);

    // ══════════════════════════════════════════════════════════════════
    // 1. 注册表
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Registry_StoneAge_SortedModelsAscendingUniqueOrders()
    {
        var models = CivModelRegistry.StoneAge().SortedModels();
        Assert.GreaterOrEqual(models.Count, 20, "石器时代注册表至少应有 20+ 模型");
        for (int i = 0; i < models.Count; i++)
        {
            Assert.False(string.IsNullOrEmpty(models[i].Name), $"模型 {i} Name 为空");
            if (i > 0)
                Assert.Greater(models[i].Order, models[i - 1].Order,
                    $"Order 必须严格升序且唯一：{models[i - 1].Order} → {models[i].Order}");
        }
        // 基线对照：首个模型是起源（Order 0），末个是分裂迁移（Order 80）——注册表全文尾部注释的契约。
        Assert.AreEqual(0, models[0].Order);
        Assert.AreEqual(80, models[models.Count - 1].Order);
    }

    // ══════════════════════════════════════════════════════════════════
    // 2. 商品目录 / 能力表
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void CommodityTable_CountAndIndex_Consistent()
    {
        Assert.AreEqual(CommodityTable.All.Length, CommodityTable.Count);
        // 目录序即索引（static ctor 建 _index；注册表数组序 = 契约）
        Assert.AreEqual(0, CommodityTable.Index(CommodityTable.Grain));
        Assert.AreEqual(1, CommodityTable.Index(CommodityTable.Berry));
        Assert.AreEqual(2, CommodityTable.Index(CommodityTable.Meat));
        Assert.AreEqual(3, CommodityTable.Index(CommodityTable.Leather));
        Assert.AreEqual(4, CommodityTable.Index(CommodityTable.Wool));
        Assert.AreEqual(5, CommodityTable.Index(CommodityTable.Straw));
        for (int i = 0; i < CommodityTable.Count; i++)
            Assert.AreEqual(CommodityTable.All[i].Id, CommodityTable.All[CommodityTable.Index(CommodityTable.All[i].Id)].Id);
    }

    [Test]
    public void CommodityTable_FoodPerishabilityOrder_MatchesGrowthContract()
    {
        // GrowthModel.FoodIdxByDecayDesc：食物按 BaseDecay 降序（易腐先吃，谷物留底）。
        // 契约：浆果/肉先吃、谷物最后——"谷物 = 饥荒最后防线"是该机制的语义核心。
        var foods = new List<(string id, float decay)>();
        for (int s = 0; s < CommodityTable.Count; s++)
            if (CommodityTable.All[s].Kind == CommodityKind.Food)
                foods.Add((CommodityTable.All[s].Id, CommodityTable.All[s].BaseDecay));
        Assert.AreEqual(3, foods.Count, "食物类应有 3 种：谷物/浆果/肉");
        foods.Sort((a, b) => b.decay.CompareTo(a.decay));   // 与 GrowthModel 同排序
        Assert.AreEqual(CommodityTable.Berry, foods[0].id, "易腐排序：浆果第一");
        Assert.AreEqual(CommodityTable.Meat, foods[1].id, "易腐排序：肉第二");
        Assert.AreEqual(CommodityTable.Grain, foods[2].id, "耐储者（谷物）最后 = 留底");
        Assert.Less(CommodityTable.All[GrainIdx].BaseDecay, CommodityTable.All[MeatIdx].BaseDecay);
        Assert.Less(CommodityTable.All[MeatIdx].BaseDecay, CommodityTable.All[BerryIdx].BaseDecay);
    }

    [Test]
    public void CommodityTable_NewStocks_ZeroFilledFullLength()
    {
        var s = CommodityTable.NewStocks();
        Assert.AreEqual(CommodityTable.Count, s.Length);
        foreach (var v in s) Assert.AreEqual(0f, v);
    }

    [Test]
    public void CapabilityTable_AllIds_ContainsCanonicalIds()
    {
        var ids = CapabilityTable.AllIds();
        Assert.AreEqual(10, ids.Count, "能力上限 32 位；当前内置 10 个");
        foreach (var id in new[] { CapabilityTable.Canoe, CapabilityTable.Microlith, CapabilityTable.Grinding,
                                   CapabilityTable.Fire, CapabilityTable.Clothing, CapabilityTable.Seed,
                                   CapabilityTable.Storage, CapabilityTable.Livestock, CapabilityTable.Pottery,
                                   CapabilityTable.Settle })
            Assert.True(ids.Contains(id), $"能力 {id} 缺失");
    }

    [Test]
    public void CapabilityTable_Settle_RequiresFarming()
    {
        var ctx = new CivSimContext { Grid = MakeMiniGrid() };
        var hunter = new Band { P = 10 };
        var farmer = new Band { P = 10, IsFarming = true };
        Assert.False(CapabilityTable.Has(ctx, hunter, CapabilityTable.Settle), "旧石器（未转农）无定居能力");
        Assert.AreEqual(0u, hunter.CapMask, "无科技部落能力位图应为 0");
        Assert.True(CapabilityTable.Has(ctx, farmer, CapabilityTable.Settle), "转农即定居（源码：Settle = IsFarming，无发明事件）");
        Assert.False(CapabilityTable.Has(ctx, farmer, CapabilityTable.Seed), "无种子科技无播种能力");
    }

    // ══════════════════════════════════════════════════════════════════
    // 3. 人口增长（单模型隔离，重点）
    // ══════════════════════════════════════════════════════════════════

    private static CivSimContext GrowthCtx(Band e)
    {
        var grid = MakeMiniGrid();
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellBands = new Band[grid.N],
            Bands = new List<Band> { e },
            Tick = 0,
            Rng = new DeterministicRandom(1),
            R = Enumerable.Repeat(1f, grid.N).ToArray(),
            RMax = 1f,
        };
        ctx.CellBands[e.Cell] = e;
        return ctx;
    }

    [Test]
    public void Growth_FBelowP_NoStocks_NegativeGrowth()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 5 };
        var ctx = GrowthCtx(e);
        new GrowthModel().Execute(ctx);
        // P_i ×= exp(r·(1 − D/F))，r=0.5，D=F... D=P=10，F=5 → exp(0.5·(1−2)) = e^−0.5
        float expected = 10f * MathF.Exp(0.5f * (1f - 10f / 5f));
        Assert.AreEqual(expected, e.P, 1e-4f, "缺口（F<P）负增长");
        Assert.False(e.Dead);
    }

    [Test]
    public void Growth_PBelowOne_Extinction()
    {
        var e = new Band { Id = 0, Cell = 0, P = 3, FLast = 0.5f };
        var ctx = GrowthCtx(e);
        new GrowthModel().Execute(ctx);
        // exp(0.5·(1−6)) = e^−2.5 ≈ 0.0821 → P≈0.246 < 1 → 灭绝
        Assert.AreEqual(0f, e.P);
        Assert.True(e.Dead, "P<1 饿死灭绝（源码：e.P=0; e.Dead=true）");
    }

    [Test]
    public void Growth_Deficit_EatsPerishableBeforeStaple()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 9 };   // 缺口 1
        e.Stocks[BerryIdx] = 0.5f;
        e.Stocks[MeatIdx] = 0.3f;
        e.Stocks[GrainIdx] = 0.7f;
        var ctx = GrowthCtx(e);
        new GrowthModel().Execute(ctx);
        // 缺口 1：浆果 0.5 → 肉 0.3 → 谷物 0.2（易腐先吃）
        Assert.AreEqual(0f, e.Stocks[BerryIdx], 1e-4f);
        Assert.AreEqual(0f, e.Stocks[MeatIdx], 1e-4f);
        Assert.AreEqual(0.5f, e.Stocks[GrainIdx], 1e-4f, "谷物留底（只吃 0.2）");
        Assert.AreEqual(10f, e.P, 1e-4f, "存粮补足缺口后 f=P → 因子 1，人口不变");
        Assert.False(e.Dead);
    }

    [Test]
    public void Growth_Deficit_EatsCarryBeforeGranary()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 8, IsFarming = true };   // 缺口 2，定居
        e.Stocks[BerryIdx] = 0.4f;
        e.Stocks[MeatIdx] = 0.4f;
        var s = new Settlement { Id = 0, Cell = 0, Level = 0, OccupantId = e.Id, DwellFrom = 0 };
        s.Stocks[BerryIdx] = 1.5f;
        s.Stocks[GrainIdx] = 3.0f;
        e.PlaceId = s.Id;
        var ctx = GrowthCtx(e);
        ctx.Settlements.Add(s);
        new GrowthModel().Execute(ctx);
        // 缺口 2：随身全部（易腐先吃：浆果 0.4 + 肉 0.4 = 0.8）→ 粮仓浆果补 1.2（易腐）→
        // 粮仓谷物不动（耐储留底）。实现契约：先吃随身整池、再吃粮仓整池，池内易腐优先。
        Assert.AreEqual(0f, e.Stocks[BerryIdx], 1e-4f);
        Assert.AreEqual(0f, e.Stocks[MeatIdx], 1e-4f, "随身整池先被耗尽（缺口 2 > 随身食物 0.8）");
        Assert.AreEqual(0.3f, s.Stocks[BerryIdx], 1e-4f, "粮仓浆果补 1.2（1.5−1.2）");
        Assert.AreEqual(3.0f, s.Stocks[GrainIdx], 1e-4f, "粮仓谷物未动——最后防线");
        Assert.AreEqual(10f, e.P, 1e-4f);
    }

    [Test]
    public void Growth_Surplus_FillsCarryToCap_NoGranary()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 20 };   // 盈余 10（游群，无聚落）
        float p0 = e.P;
        var ctx = GrowthCtx(e);
        new GrowthModel().Execute(ctx);
        float cap = CivSimContext.CarryFoodCap * p0;   // 0.06×P = 0.6（容量按当 tick 起始人口算）
        Assert.AreEqual(cap, e.Stocks[GrainIdx], 1e-4f, "随身谷物入仓封顶 CarryFoodCap×P");
    }

    [Test]
    public void Growth_Surplus_SettledStoresToGranary()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 25, IsFarming = true };
        var s = new Settlement { Id = 0, Cell = 0, Level = 0, OccupantId = e.Id, DwellFrom = 0 };
        e.PlaceId = s.Id;
        var ctx = GrowthCtx(e);
        ctx.Settlements.Add(s);
        new GrowthModel().Execute(ctx);
        // 盈余 15：随身先满 0.6，粮仓收至 SettleFoodCap×(1+0.5×Level)×P = 0.5×10 = 5
        Assert.AreEqual(CivSimContext.CarryFoodCap * 10f, e.Stocks[GrainIdx], 1e-4f);
        Assert.AreEqual(CivSimContext.SettleFoodCap * (1f + CivSimContext.SettlementStoragePerLevel * 0) * 10f,
            s.Stocks[GrainIdx], 1e-4f);
    }

    [Test]
    public void Growth_Surplus_GranaryCapScalesWithLevel()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 30, IsFarming = true };
        var s = new Settlement { Id = 0, Cell = 0, Level = 2, OccupantId = e.Id, DwellFrom = 0 };
        e.PlaceId = s.Id;
        var ctx = GrowthCtx(e);
        ctx.Settlements.Add(s);
        new GrowthModel().Execute(ctx);
        // 城镇（Level 2）：0.5×(1+0.5×2)×10 = 10
        float cap = CivSimContext.SettleFoodCap * (1f + CivSimContext.SettlementStoragePerLevel * 2) * 10f;
        Assert.AreEqual(cap, s.Stocks[GrainIdx], 1e-4f);
        Assert.AreEqual(CivSimContext.CarryFoodCap * 10f, e.Stocks[GrainIdx], 1e-4f);
    }

    [Test]
    public void Growth_SettleCapability_MultipliesGrowth()
    {
        var nomad = new Band { Id = 0, Cell = 0, P = 10, FLast = 20 };
        new GrowthModel().Execute(GrowthCtx(nomad));
        var farmer = new Band { Id = 0, Cell = 0, P = 10, FLast = 20, IsFarming = true };
        new GrowthModel().Execute(GrowthCtx(farmer));
        float nomadExpected = 10f * MathF.Exp(0.5f * (1f - 10f / 20f));         // r=0.5
        float farmerExpected = 10f * MathF.Exp(0.75f * (1f - 10f / 20f));       // r×1.5
        Assert.AreEqual(nomadExpected, nomad.P, 1e-4f);
        Assert.AreEqual(farmerExpected, farmer.P, 1e-4f, "定居（IsFarming→Settle 能力）增长 ×1.5");
        Assert.Greater(farmer.P, nomad.P);
    }

    [Test]
    public void Growth_ChiefdomTributeRelief_HalvesLoss()
    {
        var relieved = new Band { Id = 0, Cell = 0, P = 10, FLast = 9, ChiefdomId = 5, Contributed = 1f };
        new GrowthModel().Execute(GrowthCtx(relieved));
        var plain = new Band { Id = 0, Cell = 0, P = 10, FLast = 9 };
        new GrowthModel().Execute(GrowthCtx(plain));
        // 灾年（factor<1）且 Contributed>0（酋邦成员曾交贡赋）→ 缺口 ×0.5 缓冲
        float expectedRelieved = 10f * (1f + (MathF.Exp(0.5f * (1f - 10f / 9f)) - 1f) * 0.5f);
        Assert.AreEqual(expectedRelieved, relieved.P, 1e-4f, "酋邦开仓：损失减半");
        Assert.Greater(relieved.P, plain.P);
    }

    // ══════════════════════════════════════════════════════════════════
    // 4. 起源播种
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Origin_SeedsSingleBand_WithStoneAgeCulture()
    {
        var grid = MakeMiniGrid();
        int n = grid.N;
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellBands = new Band[n],
            Bands = new List<Band>(),
            Seed = 42,
            OriginCount = 3,
            Rng = new DeterministicRandom(42),
            R = Enumerable.Repeat(1f, n).ToArray(),
            RMax = 1f,
            Tick = 0,
        };
        new OriginModel().Execute(ctx);
        // n=2 网格 42 格：最小格距阈值 = 12×√AreaKm² ≈ 41815 km > 星球最大球面距 πR ≈ 20015 km
        // → 第二个起源永不能满足格距约束 → 恰 1 个起源（与 Rng 无关，确定性）。
        Assert.AreEqual(1, ctx.Bands.Count, "42 格小网格上格距约束使起源数恒为 1");
        var e = ctx.Bands[0];
        Assert.AreEqual(CivSimContext.OriginPop, e.P);
        Assert.AreEqual(0, e.Id);
        Assert.True(e.TechKeys.Contains(TechTable.StoneCore), "起源自带 stone_core");
        Assert.AreEqual("cult_0", ShareField.DomKey(e.CultureShare), "首个文化 key = cult_0（NextCultureKey 计数）");
        Assert.AreEqual(255, (int)ShareField.DomFrac(e.CultureShare), "文化份额全占（255 归一）");
        Assert.AreEqual(ReligionStage.Animism, ShareField.DomReligion(e.ReligionShare), "起源宗教 = 泛灵");
        Assert.AreEqual("relig_0", ShareField.DomKey(e.ReligionCultShare));
        Assert.AreSame(e, ctx.CellBands[e.Cell], "一格一实体：起源占据空格");
        Assert.AreEqual(1, ctx.NextBandId);
    }

    [Test]
    public void Origin_DoesNotRun_AfterTickZero()
    {
        var grid = MakeMiniGrid();
        int n = grid.N;
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellBands = new Band[n],
            Bands = new List<Band>(),
            Rng = new DeterministicRandom(1),
            R = Enumerable.Repeat(1f, n).ToArray(),
            Tick = 5,
        };
        new OriginModel().Execute(ctx);
        Assert.AreEqual(0, ctx.Bands.Count, "起源只在 Tick==0 播种");
    }

    // ══════════════════════════════════════════════════════════════════
    // 5. 分裂 / 迁徙（行为级——目标寻路为 internal 不可直接断言）
    // ══════════════════════════════════════════════════════════════════

    private static CivSimContext SplitCtx(Band e, int rngSeed = 7)
    {
        var grid = MakeMiniGrid();
        int n = grid.N;
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellBands = new Band[n],
            Bands = new List<Band> { e },
            Tick = 0,
            NextBandId = 1,   // e.Id=0 已手动分配 → 计数器必须 >0（新实体 Id = NextBandId++）
            Rng = new DeterministicRandom(rngSeed),
            R = Enumerable.Repeat(2e-6f, n).ToArray(),
            RMax = 2e-6f,
            BfsStamp = new int[n],
            BfsStampValue = 1,
        };
        ctx.CellBands[e.Cell] = e;
        return ctx;
    }

    [Test]
    public void Split_FissionCarvesNewBand_AtShare()
    {
        // P=30 → 张力 (30−25)/8=0.625，pEff=30×(1+0.667+0.625)≈68.8 > SplitPop(25) → 分裂
        // R=2e-6 → 目标格承载 = R×Area×carry ≈ 24.3 > 30×0.45=13.5 → 新实体带 13.5（SplitShare=0.45）
        var e = new Band { Id = 0, Cell = 0, P = 30, FLast = 10, LastSplitTick = -1, LastMigrateTick = 0 };
        e.TechKeys.Add(TechTable.StoneCore);
        var ctx = SplitCtx(e);
        new SplitMigrateModel().Execute(ctx);
        Assert.AreEqual(1, ctx.Fissions);
        Assert.AreEqual(2, ctx.Bands.Count);
        var nt = ctx.Bands[1];
        Assert.AreEqual(1, nt.Id, "新实体 Id = NextBandId 分配（读档安全计数）");
        Assert.AreEqual(30f - CivSimContext.SplitShare * 30f, e.P, 1e-4f, "母体扣减 45%");
        Assert.AreEqual(CivSimContext.SplitShare * 30f, nt.P, 1e-4f, "新实体带走 SplitShare 比例");
        Assert.AreNotEqual(e.Cell, nt.Cell, "殖民到新格");
        Assert.AreSame(nt, ctx.CellBands[nt.Cell], "一格一实体：殖民占空格");
        Assert.True(nt.TechKeys.SetEquals(e.TechKeys), "分裂瞬间技术相同（此后各自发明）");
        Assert.AreEqual(0, nt.BornTick);
        Assert.AreEqual(0, nt.LastSplitTick);
        Assert.AreEqual(0, e.LastSplitTick, "母体记录分裂 tick（冷却用）");
        Assert.AreEqual(0, ctx.Migrations, "本 tick 母体迁移冷却（LastMigrateTick=0）→ 只分裂不迁移");
    }

    [Test]
    public void Split_DoesNotTrigger_BelowThreshold()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 10 };
        var ctx = SplitCtx(e);
        new SplitMigrateModel().Execute(ctx);
        Assert.AreEqual(1, ctx.Bands.Count, "P=10 pEff=10 ≤ SplitPop 25 → 不分裂");
        Assert.AreEqual(0, ctx.Fissions);
    }

    [Test]
    public void Migrate_StarvingBand_MovesToNewCell()
    {
        var e = new Band { Id = 0, Cell = 0, P = 10, FLast = 3, LastMigrateTick = -1, LastSplitTick = 0 };
        var ctx = SplitCtx(e);
        new SplitMigrateModel().Execute(ctx);
        Assert.AreEqual(1, ctx.Migrations);
        Assert.AreEqual(0, ctx.Fissions, "pEff=17 ≤ 25 不分裂");
        Assert.AreNotEqual(0, e.Cell, "饿（F<P）→ 迁徙搬家");
        Assert.AreSame(e, ctx.CellBands[e.Cell]);
        Assert.IsNull(ctx.CellBands[0], "旧格清除（一格一实体）");
        Assert.AreEqual(0, e.LastMigrateTick, "迁移冷却记录");
    }

    // ══════════════════════════════════════════════════════════════════
    // 6. 聚落实体
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Settlement_FarmingBand_CreatesLevel0()
    {
        var e = new Band { Id = 0, Cell = 0, P = 300, IsFarming = true, PlaceId = -1, SettledSince = -1 };
        var ctx = GrowthCtx(e);
        ctx.Tick = 10;
        new SettlementModel().Execute(ctx);
        Assert.AreEqual(1, ctx.Settlements.Count);
        var s = ctx.Settlements[0];
        Assert.AreEqual(0, s.Id, "NextSettlementId 从 0 分配");
        Assert.AreEqual(e.Cell, s.Cell);
        Assert.AreEqual(0, s.Level, "新村 Level 0");
        Assert.AreEqual(e.Id, s.OccupantId);
        Assert.False(s.IsRuin);
        Assert.AreEqual(e.PlaceId, s.Id, "部落占据聚落");
        Assert.AreEqual(10, e.SettledSince, "定居起点 = 转农当 tick");
        Assert.AreEqual(10, s.DwellFrom);
        Assert.AreEqual(10, s.LastLevelUpTick);
        Assert.AreEqual(10, s.BornTick);
    }

    [Test]
    public void Settlement_LevelUp_ByDwellAndPop_WithCooldown()
    {
        var e = new Band { Id = 0, Cell = 0, P = 300, IsFarming = true, PlaceId = 0 };
        var s = new Settlement { Id = 0, Cell = 0, Level = 0, DwellFrom = 0, LastLevelUpTick = 0, BornTick = 0, OccupantId = e.Id };
        var ctx = GrowthCtx(e);
        ctx.Settlements.Add(s);
        ctx.Tick = 20;
        new SettlementModel().Execute(ctx);
        // dwell=20 ≥ SettlementLevelTicks1(3) 且 P=300 ≥ SettlementPop1(200) → 村庄；< 800 → 不到城镇
        Assert.AreEqual(1, s.Level, "dwell×P 阈值驱动等级");
        Assert.AreEqual(20, s.LastLevelUpTick);
        // 升级冷却 SettlementLevelCooldown=2：紧接下一 tick 不再跳级
        ctx.Tick = 21;
        new SettlementModel().Execute(ctx);
        Assert.AreEqual(1, s.Level, "冷却内不跳级");
    }

    [Test]
    public void Settlement_Capital_HalvesLevelThresholds()
    {
        var capitalChief = new Band { Id = 0, Cell = 0, P = 400, IsFarming = true, IsChief = true, ChiefdomId = 0, PlaceId = 0 };
        var cap = new Settlement { Id = 0, Cell = 0, Level = 0, DwellFrom = 10, LastLevelUpTick = 0, BornTick = 0, OccupantId = 0 };
        var ctx = GrowthCtx(capitalChief);
        ctx.Settlements.Add(cap);
        ctx.Tick = 20;
        new SettlementModel().Execute(ctx);
        // 都城（至尊酋长聚落）阈值减半：L2 需 800/2=400 → P=400 达标 → 城镇
        Assert.AreEqual(2, cap.Level, "都城（ChiefdomId==Id 的酋长聚落）阈值减半");

        var plain = new Band { Id = 1, Cell = 1, P = 400, IsFarming = true, PlaceId = 1 };
        var ps = new Settlement { Id = 1, Cell = 1, Level = 0, DwellFrom = 10, LastLevelUpTick = 0, BornTick = 0, OccupantId = 1 };
        var ctx2 = GrowthCtx(plain);
        ctx2.Tick = 20;
        ctx2.Settlements.Add(ps);
        new SettlementModel().Execute(ctx2);
        Assert.AreEqual(1, ps.Level, "非都城 P=400 只到村庄（<800）");
    }

    [Test]
    public void Settlement_Abandoned_OnOccupantDeath()
    {
        var e = new Band { Id = 0, Cell = 0, P = 300, IsFarming = true, Dead = true, PlaceId = 0 };
        var s = new Settlement { Id = 0, Cell = 0, Level = 1, OccupantId = 0, DwellFrom = 0, LastLevelUpTick = 0, BornTick = 0 };
        var ctx = GrowthCtx(e);
        ctx.Settlements.Add(s);
        ctx.Tick = 5;
        new SettlementModel().Execute(ctx);
        Assert.True(s.IsRuin, "部落灭绝 → 聚落成废墟（场所比人长寿）");
        Assert.AreEqual(-1, s.OccupantId);
        Assert.AreEqual(5, s.RuinFrom);
        Assert.AreEqual(e.PlaceId, s.Id, "死亡实体聚落关联不动（无迁走清理语义）");
    }

    // ══════════════════════════════════════════════════════════════════
    // 7. 能量核算 / 农田开垦 / 领地凝聚
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Energy_ComputesPerCapAndSurplus()
    {
        var a = new Band { Id = 0, Cell = 1, P = 10, FLast = 20 };
        var b = new Band { Id = 1, Cell = 1, P = 5, FLast = 10 };
        var dead = new Band { Id = 2, Cell = 2, P = 100, FLast = 100, Dead = true };
        var ctx = new CivSimContext
        {
            CellPop = new float[4],
            Bands = new List<Band> { a, b, dead },
            Grid = PathGrid(),
        };
        new EnergyModel().Execute(ctx);
        Assert.AreEqual(15f, ctx.CellPop[1]);
        Assert.AreEqual(0f, ctx.CellPop[2], "死亡实体不计入");
        Assert.AreEqual(2f, a.EPerCap, 1e-4f);
        Assert.AreEqual(1f, a.Surplus, 1e-4f);
        Assert.AreEqual(2f, b.EPerCap, 1e-4f);
        Assert.AreEqual(1f, b.Surplus, 1e-4f);
    }

    [Test]
    public void Cultivate_RaisesTerritoryCultivation_FarmingOnly()
    {
        var farmer = new Band { Id = 0, Cell = 0, P = 20, IsFarming = true };
        var hunter = new Band { Id = 1, Cell = 2, P = 20, IsFarming = false };
        var ctx = new CivSimContext
        {
            Cultivation = new float[4],
            Bands = new List<Band> { farmer, hunter },
            Grid = PathGrid(),
            TerritoryCells = new List<int>[4],
            TerritoryDists = new List<byte>[4],
        };
        for (int i = 0; i < 4; i++) { ctx.TerritoryCells[i] = new List<int>(); ctx.TerritoryDists[i] = new List<byte>(); }
        ctx.TerritoryCells[0] = new List<int> { 0, 1 };   // 农田：领地格 {0,1}
        ctx.TerritoryCells[1] = new List<int> { 2, 3 };   // 狩猎部落领地格
        new CultivateModel().Execute(ctx);
        Assert.AreEqual(CivSimContext.CultivateRate, ctx.Cultivation[0], 1e-4f);
        Assert.AreEqual(CivSimContext.CultivateRate, ctx.Cultivation[1], 1e-4f);
        Assert.AreEqual(0f, ctx.Cultivation[2], "非农业部落不开垦");
        Assert.AreEqual(0f, ctx.Cultivation[3]);
    }

    [Test]
    public void Territory_UnionFinds_ByCultureGroup()
    {
        var a = new Band { Id = 1, Cell = 0, P = 10 };
        a.CultureGroupShare = ShareField.NewCulture("g");
        var b = new Band { Id = 5, Cell = 1, P = 10 };
        b.CultureGroupShare = ShareField.NewCulture("g");
        var c = new Band { Id = 2, Cell = 3, P = 10 };
        c.CultureGroupShare = ShareField.NewCulture("h");
        var grid = PathGrid();
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellBands = new Band[4] { a, b, null, c },
            Bands = new List<Band> { a, b, c },
        };
        TerritoryModel.Rebuild(ctx);
        Assert.AreEqual(1, a.TerritoryId, "分量标号 = 分量最小实体 Id");
        Assert.AreEqual(1, b.TerritoryId);
        Assert.AreEqual(2, a.TerritorySize, "邻格同语言群凝聚");
        Assert.AreEqual(2, b.TerritorySize);
        Assert.AreEqual(2, c.TerritoryId, "异语言群（无邻格同群）独立");
        Assert.AreEqual(1, c.TerritorySize);
    }

    // ══════════════════════════════════════════════════════════════════
    // 8. 酋邦 / 国家（纯派生重建，公开静态）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Chiefdom_Patronage_AssignsChief_PrestigeHighestWins()
    {
        var c1 = new Band { Id = 0, Cell = 0, P = 20, Prestige = 2.0f, IsChief = true, TerritoryId = 0 };
        var n1 = new Band { Id = 1, Cell = 1, P = 10, Prestige = 0f, TerritoryId = 1 };
        var c2 = new Band { Id = 2, Cell = 2, P = 20, Prestige = 1.5f, IsChief = true, TerritoryId = 2 };
        var ctx = new CivSimContext
        {
            Grid = PathGrid(),
            CellBands = new Band[4] { c1, n1, c2, null },
            Bands = new List<Band> { c1, n1, c2 },
            R = Enumerable.Repeat(1f, 4).ToArray(),
            BfsStamp = new int[4],
            Tick = 0,
        };
        ChiefdomModel.Rebuild(ctx);
        // 庇护半径 ChiefReach=12 覆盖全图：band 1 同时见两位酋长 → 取 Prestige 更高者（c1）
        Assert.AreEqual(0, n1.ChiefdomId, "非酋长 band 挂靠 Prestige 最高酋长");
        Assert.AreEqual(2, n1.ChiefdomSize);
        Assert.AreEqual(0, c1.ChiefdomId, "酋长 = 自己酋邦中心");
        Assert.AreEqual(2, c1.ChiefdomSize);
        // c2 只有自己一名成员 → 少于 ChiefdomMinBands(2) → 解散
        Assert.AreEqual(-1, c2.ChiefdomId, "单人酋邦不成邦（<2 解散）");
        Assert.AreEqual(1, c2.ChiefdomSize);
        Assert.True(ctx.ChiefdomCells[0].Contains(0) && ctx.ChiefdomCells[0].Contains(1), "成员表按酋邦 id 填充");
    }

    [Test]
    public void State_Emergence_RequiresCapital_Hierarchy_Pool_Dwell()
    {
        var chief = new Band { Id = 0, Cell = 0, P = 100, IsChief = true, ChiefdomId = 0, Contributed = 1.5f, PlaceId = 10, TerritoryId = 0 };
        var member = new Band { Id = 1, Cell = 1, P = 50, Contributed = 0.5f, PlaceId = 11, TerritoryId = 1 };
        var capital = new Settlement { Id = 10, Cell = 0, BornTick = 0, Level = 2, LastLevelUpTick = 0, DwellFrom = 0, OccupantId = 0 };
        var sub = new Settlement { Id = 11, Cell = 1, BornTick = 5, Level = 1, LastLevelUpTick = 5, DwellFrom = 5, OccupantId = 1 };
        var cells = new List<int>[8];
        foreach (var i in Enumerable.Range(0, 8)) cells[i] = new List<int>();
        cells[0] = new List<int> { 0, 1 };
        var ctx = new CivSimContext
        {
            Bands = new List<Band> { chief, member },
            Settlements = new List<Settlement> { capital, sub },
            ChiefdomCells = cells,
            Tick = 30,
        };
        StateAssign.Rebuild(ctx);
        // ①都城 Level2(≥2) ✓ ②成员聚落 2 + 次级中心 Level1 ✓ ③池 2.0 ≥ 150×0.01 ✓ ④30−0≥20 ✓
        Assert.AreEqual(0, chief.StateId, "酋邦制度化 → 国家");
        Assert.AreEqual(2, chief.StateSize);
        Assert.AreEqual(0, member.StateId);
        Assert.AreEqual(2, member.StateSize);
    }

    [Test]
    public void State_NotEmerging_WithoutSubCenter()
    {
        var chief = new Band { Id = 0, Cell = 0, P = 100, IsChief = true, ChiefdomId = 0, Contributed = 1.5f, PlaceId = 10, TerritoryId = 0 };
        var member = new Band { Id = 1, Cell = 1, P = 50, Contributed = 0.5f, PlaceId = -1, TerritoryId = 1 };
        var capital = new Settlement { Id = 10, Cell = 0, BornTick = 0, Level = 2, LastLevelUpTick = 0, DwellFrom = 0, OccupantId = 0 };
        var cells = new List<int>[8];
        foreach (var i in Enumerable.Range(0, 8)) cells[i] = new List<int>();
        cells[0] = new List<int> { 0, 1 };
        var ctx = new CivSimContext
        {
            Bands = new List<Band> { chief, member },
            Settlements = new List<Settlement> { capital },
            ChiefdomCells = cells,
            Tick = 30,
        };
        StateAssign.Rebuild(ctx);
        Assert.AreEqual(-1, chief.StateId, "决策层级（成员聚落 ≥2 + 次级中心）缺失 → 非国家");
        Assert.AreEqual(-1, member.StateId);
    }

    // ══════════════════════════════════════════════════════════════════
    // 9. 战争状态（外交状态对象 + 公开静态查询；会战/宣战 internal 不可达）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void WarState_InvolesAndIsTribute()
    {
        var w = new War { StateIdA = 1, StateIdB = 2 };
        Assert.True(w.Involves(1));
        Assert.True(w.Involves(2));
        Assert.False(w.Involves(3));
        Assert.False(w.IsTribute, "交战中无朝贡标记");
        w.TributeTo = 1;
        w.TributeFrom = 2;
        Assert.True(w.IsTribute, "朝贡期 = 战争已决出但关系延续");
    }

    [Test]
    public void WarModel_IsAtWar_DetectsActiveWar()
    {
        var ctx = new CivSimContext { Wars = new List<War> { new War { StateIdA = 1, StateIdB = 2 } } };
        Assert.True(WarModel.IsAtWar(ctx, 1, 2));
        Assert.False(WarModel.IsAtWar(ctx, 1, 3), "非交战对");
        Assert.False(WarModel.IsAtWar(ctx, 1, 1), "同国不算战争");
        Assert.False(WarModel.IsAtWar(ctx, -1, 2), "Id<0 无战争");
    }

    // ══════════════════════════════════════════════════════════════════
    // 10. 物物交换（接触即互通；无 Rng、固定对序 → 直接断言转移量）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Trade_TransfersSurplus_ToDeficitNeighbor()
    {
        var a = new Band { Id = 0, Cell = 0, P = 10 };
        var b = new Band { Id = 1, Cell = 1, P = 10 };
        a.Stocks[GrainIdx] = 1.0f;
        var grid = PathGrid();
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellBands = new Band[4] { a, b, null, null },
            Bands = new List<Band> { a, b },
            TerritoryCells = new List<int>[2],
            TerritoryDists = new List<byte>[2],
        };
        ctx.TerritoryCells[0] = new List<int> { 0 };
        ctx.TerritoryDists[0] = new List<byte> { 0 };
        ctx.TerritoryCells[1] = new List<int> { 1 };
        ctx.TerritoryDists[1] = new List<byte> { 0 };
        new TradeModel().Execute(ctx);
        // 人均差 1/10−0=0.1 ≥ TradeMinGap；量 = 0.1×TradeRate(0.1)×min(P)=10 ×mult(1/(1+0.5·1))
        float amount = 0.1f * CivSimContext.TradeRate * 10f * (1f / (1f + CivSimContext.TradeDistanceRate * 1f));
        Assert.AreEqual(amount, b.Stocks[GrainIdx], 1e-4f, "B 缺粮方收到 A 的盈余谷物");
        Assert.AreEqual(1.0f - amount, a.Stocks[GrainIdx], 1e-4f, "出方：粮仓先出→随身后出（此处随身）");
        Assert.AreEqual(1, ctx.TradeEvents, "每商品转移记 1 次");
        Assert.AreEqual(amount, ctx.TradeVolume, 1e-4f);
    }

    // ══════════════════════════════════════════════════════════════════
    // 11. 上下文纯辅助
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Context_SettlementOf_ResolvesByPlaceId()
    {
        var s = new Settlement { Id = 5, Cell = 0, OccupantId = 7 };
        var ctx = new CivSimContext { Settlements = new List<Settlement> { s } };
        var t = new Band { Id = 7, PlaceId = 5 };
        Assert.AreSame(s, ctx.SettlementOf(t));
        t.PlaceId = -1;
        Assert.IsNull(ctx.SettlementOf(t));
        t.PlaceId = 99;
        Assert.IsNull(ctx.SettlementOf(t), "未知 PlaceId → null");
    }

    [Test]
    public void Context_NextCultureKey_SequentialCounters()
    {
        var ctx = new CivSimContext();
        Assert.AreEqual("cult_0", ctx.NextCultureKey());
        Assert.AreEqual("cult_1", ctx.NextCultureKey());
        Assert.AreEqual("cultg_0", ctx.NextCultureGroupKey());
        Assert.AreEqual("relig_0", ctx.NextReligionKey());
    }

    [Test]
    public void Context_TotalPopulation_CountsLiveOnly()
    {
        var a = new Band { P = 10 };
        var dead = new Band { P = 100, Dead = true };
        var b = new Band { P = 5 };
        var ctx = new CivSimContext { Bands = new List<Band> { a, dead, b } };
        Assert.AreEqual(15f, ctx.TotalPopulation());
    }

    [Test]
    public void Context_IsStarving_WhenFBelowP()
    {
        var ctx = new CivSimContext();
        var fed = new Band { P = 10, FLast = 10 };
        var starving = new Band { P = 10, FLast = 5 };
        Assert.False(ctx.IsStarving(fed), "F ≥ P×0.999 → 不饿");
        Assert.True(ctx.IsStarving(starving), "F < P → 饿（迁徙/冲突压力判据）");
    }

    // ══════════════════════════════════════════════════════════════════
    // 12. 模块确定性（CivEngine.Run 有 TechTable.Load/PerfLog/GD.Print 不可测——降级方案：
    //     手工复刻 Run 的 tick 语义（RefreshCellState + StoneAge 全模型）跑两次，逐项对比末态）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>等价 tick 循环（复刻 CivEngine.Run 的主循环；不调 TechTable.Load/PerfLog/GD.Print）。
    /// R 取小值让领地总产出 ≈ 人口量级：动态有分裂/迁徙/影响力更替，但人口有界不爆。</summary>
    private static CivSimContext RunMiniEvolution(GameGrid grid, int seed, int ticks = 40)
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
            // R = 8.2e-6 → R×胞面积(≈12.14e6 km²) ≈ 100 人/格 ≈ 起源人口量级——部落能存续繁衍、
            // 分裂/迁徙发生；R 太小（1.2e-8）→ 每格承载 0.15 人 → 起源即饿死灭绝（退化空演化）
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
            WildCrops = new byte[n],
            Tick = 0,
        };
        var registry = CivModelRegistry.StoneAge();
        // ⚠️ 循环条件不能含 Bands.Count>0：起源在循环内 tick 0 由 OriginModel 播种（复刻
        //   CivEngine.Run 主循环——其循环无空检查）；旧条件使循环一次都不执行 → 空演化。
        for (int t = 0; t < ticks; t++)
        {
            ctx.Tick = t;
            CivEngine.RefreshCellState(ctx);
            foreach (var m in registry.SortedModels())
                m.Execute(ctx);
        }
        ctx.Bands.RemoveAll(e => e.Dead);
        CivEngine.SettleDerived(ctx);   // 与 Run 结尾同式边界态重建
        return ctx;
    }

    [Test]
    public void MiniEvolution_SameSeedSameGrid_TwoRunsIdentical()
    {
        var grid = MakeMiniGrid();
        var a = RunMiniEvolution(grid, 42);
        var b = RunMiniEvolution(grid, 42);
        AssertSameFinalState(a, b);
    }

    [Test]
    public void MiniEvolution_DifferentSeeds_Diverge()
    {
        // 起源格 = Rng.Next(富饶池) 随机：不同 seed 应产生不同演化结果。
        // ⚠️ 均匀网格上聚合量（人口/分裂数）可能镜像重合——起源格是种子最直接的观测
        // （OriginModel.Execute: pick = minCands[Rng.Next(...)]）；聚合量差异作为补充信号。
        var grid = MakeMiniGrid();
        var baseline = RunMiniEvolution(grid, 42);
        bool diverged = false;
        for (int s = 43; s <= 60 && !diverged; s++)
        {
            var other = RunMiniEvolution(grid, s);
            diverged = baseline.TotalPopulation() != other.TotalPopulation()
                || baseline.Bands.Count != other.Bands.Count
                || baseline.Fissions != other.Fissions
                || baseline.Migrations != other.Migrations
                || (baseline.Bands.Count > 0 && other.Bands.Count > 0
                    && baseline.Bands[0].Cell != other.Bands[0].Cell);
        }
        Assert.True(diverged, "不同 seed 演化路径应分叉（起源格/随机序列由种子驱动）");
    }

    /// <summary>逐项对比两次演化的代表性末态字段（同进程内浮点序列完全一致；容差作保险）。</summary>
    private static void AssertSameFinalState(CivSimContext a, CivSimContext b)
    {
        Assert.AreEqual(a.Tick, b.Tick, "终止 tick 一致");
        Assert.AreEqual(a.Bands.Count, b.Bands.Count, "实体数一致");
        Assert.AreEqual(a.TotalPopulation(), b.TotalPopulation(), 0.001f);
        Assert.AreEqual(a.Fissions, b.Fissions);
        Assert.AreEqual(a.Migrations, b.Migrations);
        Assert.AreEqual(a.Conflicts, b.Conflicts);
        Assert.AreEqual(a.TradeVolume, b.TradeVolume, 0.001f);
        Assert.AreEqual(a.TradeEvents, b.TradeEvents);
        Assert.AreEqual(a.NextBandId, b.NextBandId);
        Assert.AreEqual(a.NextSettlementId, b.NextSettlementId);
        Assert.AreEqual(a.CultureKeyCount, b.CultureKeyCount);
        Assert.AreEqual(a.ReligionKeyCount, b.ReligionKeyCount);
        Assert.AreEqual(a.Settlements.Count, b.Settlements.Count);
        Assert.AreEqual(a.Wars.Count, b.Wars.Count);

        var ta = a.Bands.OrderBy(t => t.Id).ToArray();
        var tb = b.Bands.OrderBy(t => t.Id).ToArray();
        for (int i = 0; i < ta.Length; i++)
        {
            Assert.AreEqual(ta[i].Id, tb[i].Id, "按 Id 对应");
            Assert.AreEqual(ta[i].P, tb[i].P, 0.001f);
            Assert.AreEqual(ta[i].Cell, tb[i].Cell);
            Assert.AreEqual(ta[i].Dead, tb[i].Dead);
            Assert.AreEqual(ta[i].IsFarming, tb[i].IsFarming);
            Assert.AreEqual(ta[i].LastSplitTick, tb[i].LastSplitTick);
            Assert.AreEqual(ta[i].LastMigrateTick, tb[i].LastMigrateTick);
            Assert.AreEqual(ta[i].Prestige, tb[i].Prestige, 0.001f);
            Assert.AreEqual(ta[i].Contributed, tb[i].Contributed, 0.001f);
            Assert.AreEqual(ta[i].FLast, tb[i].FLast, 0.001f);
            Assert.That(ta[i].TechKeys.SetEquals(tb[i].TechKeys), "科技 key 集合一致（顺序无关）");
            Assert.AreEqual(ShareField.DomKey(ta[i].CultureShare), ShareField.DomKey(tb[i].CultureShare));
            Assert.AreEqual(ShareField.DomFrac(ta[i].CultureShare), ShareField.DomFrac(tb[i].CultureShare));
            Assert.AreEqual(ShareField.DomKey(ta[i].CultureGroupShare), ShareField.DomKey(tb[i].CultureGroupShare));
            Assert.AreEqual(ShareField.DomKey(ta[i].ReligionCultShare), ShareField.DomKey(tb[i].ReligionCultShare));
            Assert.AreEqual(ShareField.DomReligion(ta[i].ReligionShare), ShareField.DomReligion(tb[i].ReligionShare));
            for (int s = 0; s < CommodityTable.Count; s++)
                Assert.AreEqual(ta[i].Stocks[s], tb[i].Stocks[s], 0.001f);
        }
        for (int c = 0; c < a.Grid.N; c++)
        {
            Assert.AreEqual(a.CellOwner[c], b.CellOwner[c], "每格归属一致");
            Assert.AreEqual(a.Cultivation[c], b.Cultivation[c], 0.001f);
        }
    }
}