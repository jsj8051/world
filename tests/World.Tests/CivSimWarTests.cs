using System;
using System.Collections.Generic;
using Godot;
using NUnit.Framework;
using World.CivSim;
using World.HexPlanet;
using World.LogicGrid;

using World.CivSim.Entities;
using World.CivSim.Mechanics.Military;
namespace World.Tests;

/// <summary>
/// 国家战争机制测试（阶段5 军事征服，docs/阶段5设计-军事征服.md）——"构造国家+战争状态 → 逐机制验证"。
/// 覆盖：会战（军力=ΣP×MilitMult，极端军力比 → 胜负不依赖 Rng，确定性）→ 败方损耗 →
///   停战（WarMaxTicks 超时移除）→ 吞并（碾压：胜场≥WarAnnexWins 且军力比≥WarPowerRatio；
///   成员 ConqueredBy 强制效忠、首领流放豁免、战利品入池、WarsAnnexed）→
///   朝贡（险胜：胜场≥WarTributeWins 且军力占优；TributeTo 模式 + 边境割地 CedeCells）→
///   朝贡转移（每 tick 贡赋、TributesLeft 归零移除）→ 宣战负向门（冷却）。
/// 约束同项目铁律：只 [Test]、无 SetUp/TestCaseSource/Pass；小网格（单位向量！）；无引擎调用。
/// 跳过（注释说明）：BattleChanceOf/CanDeclare 为 internal（无 InternalsVisibleTo）→ 经 Execute
///   行为间接验证；宣战**正门**概率门控 WarDeclareChance=0.002 → 不可确定性构造（需种子搜索），
///   只测负向门（冷却/池足等可构造项）。若需直接测 internal 纯函数可加 InternalsVisibleTo。
/// </summary>
public class CivSimWarTests
{
    private const int WarInterval = 5;

    /// <summary>星形 4 格网格：0 居中，邻 1/2/3（割地场景：败国格全部邻战胜国格）。Verts 单位化。</summary>
    private static GameGrid StarGrid()
    {
        Icosahedron.Subdivide(2, 6371f, out var verts, out _);
        int n = 4;
        var g = new GameGrid
        {
            N = n, GridN = 2, RadiusKm = 6371f,
            Verts = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(-1, 0, 0) },
            Elev = new float[n], Temp = new float[n], Precip = new float[n], Biome = new byte[n],
        };
        g.OverrideNeighbors(new[] { new[] { 1, 2, 3 }, new[] { 0 }, new[] { 0 }, new[] { 0 } });
        return g;
    }

    /// <summary>构造战争上下文：A 国（酋长 id0 + 成员 id1）、B 国（酋长 id2 + 成员 id3）。</summary>
    private static CivSimContext WarCtx(GameGrid grid)
    {
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellBands = new Band[grid.N],
            Bands = new List<Band>(),
            Tick = 0,
            Rng = new DeterministicRandom(7),
            NextBandId = 4,
            Settlements = new List<Settlement>(),
            Wars = new List<War>(),
            CellOwner = new int[grid.N],
            CellBestOwner = new int[grid.N],
            CellBestInf = new float[grid.N],
            CellOwnerInf = new float[grid.N],
            LockedUntil = new int[grid.N],
            BfsStamp = new int[grid.N],
            BfsStampValue = 1,
            R = new float[grid.N],
            CellF = new float[grid.N],
            CellPop = new float[grid.N],
            CellFarmPop = new float[grid.N],
            Cultivation = new float[grid.N],
        };
        ctx.ChiefdomCells = new List<int>[4];
        for (int i = 0; i < 4; i++) ctx.ChiefdomCells[i] = new List<int>();
        return ctx;
    }

    private static Band AddBand(CivSimContext ctx, int id, int cell, float p, float contributed, bool chief = false)
    {
        var e = new Band
        {
            Id = id, Cell = cell, P = p, Contributed = contributed,
            ChiefdomId = id, StateId = chief ? id : -1, StateSize = chief ? 2 : 1,
            IsChief = chief, LastWarTick = -1,
        };
        ctx.Bands.Add(e);
        ctx.CellBands[cell] = e;
        ctx.ChiefdomCells[id].Add(id);   // 酋长自身在其邦成员表
        if (chief)
            ctx.ChiefdomCells[id].Add(id == 0 ? 1 : 3);   // A: 成员1；B: 成员3
        return e;
    }

    private static War AddWar(CivSimContext ctx, int a, int b, int tick, int winsA = 0, int winsB = 0, int lastBattle = 0)
    {
        var w = new War { StateIdA = a, StateIdB = b, Defender = b, StartTick = tick, WinsA = winsA, WinsB = winsB, LastBattleTick = lastBattle };
        ctx.Wars.Add(w);
        return w;
    }

    // ═════════════════════════════════════════════════════════════════
    // 1. 会战：军力 = Σ P×MilitMult；极端军力比 → 胜负与 Rng 无关（确定性）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Battle_OverwhelmingWinner_AlwaysWins()
    {
        // fA = 150（100+50），fB = 0 → BattleChanceOf=1 → NextDouble()<1 恒真 → A 必胜（任意种子）
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        var aChief = AddBand(ctx, 0, 0, 100f, 0f, chief: true);
        AddBand(ctx, 1, 1, 50f, 0f);
        var bChief = AddBand(ctx, 2, 2, 0f, 0f, chief: true);
        AddBand(ctx, 3, 3, 0f, 0f);
        var w = AddWar(ctx, 0, 2, tick: 0, lastBattle: 0);
        ctx.Tick = WarInterval;   // 会战节奏到点

        new WarModel().Execute(ctx);

        Assert.AreEqual(1, w.WinsA, "碾压方必赢会战");
        Assert.AreEqual(0, w.WinsB);
        Assert.AreEqual(ctx.Tick, w.LastBattleTick, "会战时间戳更新");
        Assert.AreEqual(1, ctx.Wars.Count, "胜负未决 → 战争继续");
        // 败方损耗：P=0 保持 0；Contributed 0 保持 0（不产生负值）
        Assert.AreEqual(0f, bChief.P, 1e-6f);
        Assert.AreEqual(0f, bChief.Contributed, 1e-6f);
    }

    [Test]
    public void Battle_PowerlessSide_AlwaysLoses()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        AddBand(ctx, 0, 0, 0f, 0f, chief: true);
        AddBand(ctx, 1, 1, 0f, 0f);
        AddBand(ctx, 2, 2, 100f, 0f, chief: true);
        AddBand(ctx, 3, 3, 50f, 0f);
        var w = AddWar(ctx, 0, 2, tick: 0, lastBattle: 0);
        ctx.Tick = WarInterval;

        new WarModel().Execute(ctx);

        Assert.AreEqual(1, w.WinsB, "军力为 0 方必败（BattleChanceOf=0）");
        Assert.AreEqual(0, w.WinsA);
    }

    // ═════════════════════════════════════════════════════════════════
    // 2. 停战：WarMaxTicks 超时 → 战争移除
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void War_Timeout_RemovedFromList()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        AddBand(ctx, 0, 0, 100f, 0f, chief: true);
        AddBand(ctx, 1, 1, 50f, 0f);
        AddBand(ctx, 2, 2, 100f, 0f, chief: true);
        AddBand(ctx, 3, 3, 50f, 0f);
        AddWar(ctx, 0, 2, tick: 0, lastBattle: 0);
        ctx.Tick = CivSimContext.WarMaxTicks;   // 恰好 60 tick → 超时停战（先于会战判定）

        new WarModel().Execute(ctx);

        Assert.AreEqual(0, ctx.Wars.Count, "超时战争必须移除（停战）");
        Assert.False(WarModel.IsAtWar(ctx, 0, 2), "停战后不再是交战状态");
    }

    // ═════════════════════════════════════════════════════════════════
    // 3. 吞并（碾压）：胜场≥WarAnnexWins 且军力比≥WarPowerRatio →
    //    成员 ConqueredBy 效忠、首领流放豁免、战利品入池、原战争移除
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Annex_OverwhelmingWins_ConquersMembersAndPlunders()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        var aChief = AddBand(ctx, 0, 0, 100f, 0f, chief: true);
        AddBand(ctx, 1, 1, 50f, 0f);
        var bChief = AddBand(ctx, 2, 2, 0f, 10f, chief: true);   // 败国池 10
        var bMember = AddBand(ctx, 3, 3, 0f, 5f);                // 成员池 5 → 总池 15
        // 预置碾压胜场（3 场）+ 本 tick 不触发会战（LastBattle=Tick）→ 直接进结算
        AddWar(ctx, 0, 2, tick: 0, winsA: CivSimContext.WarAnnexWins, lastBattle: 0);
        ctx.Tick = 0;
        ctx.Wars[0].LastBattleTick = 0;   // 已是最新 → 无新会战

        new WarModel().Execute(ctx);

        // 吞并效忠：败国成员（非首领）ConqueredBy = 战胜国酋长 Id
        Assert.AreEqual(0, bMember.ConqueredBy, "败国成员强制效忠征服者");
        Assert.AreEqual(-1, bChief.ConqueredBy, "败国首领不效忠（流放/消散）");
        // 战利品：败国池 15 × WarPlunderRate 0.5 = 7.5 入战胜国池
        Assert.AreEqual(7.5f, aChief.Contributed, 1e-4f, "战利品入池（Tilly 战争养战争）");
        Assert.AreEqual(1, ctx.WarsAnnexed);
        Assert.AreEqual(0, ctx.Wars.Count, "吞并后战争终结移除");
        // 首领流放：仍存活且格合法（PickMigrateTarget 可空转——只断言不崩溃/格有效）
        Assert.False(bChief.Dead);
        Assert.That(bChief.Cell, Is.InRange(0, grid.N - 1));
    }

    // ═════════════════════════════════════════════════════════════════
    // 4. 朝贡（险胜）：胜场≥WarTributeWins 且军力占优 → TributeTo 模式 + 边境割地（战争保留）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Tribute_CedeBorderCells_KeepsWar()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        AddBand(ctx, 0, 0, 100f, 0f, chief: true);
        AddBand(ctx, 1, 1, 50f, 0f);
        AddBand(ctx, 2, 2, 1f, 0f, chief: true);    // 军力 1 < 150 → 险胜
        AddBand(ctx, 3, 3, 0f, 0f);
        // 领地：0 属 A；1/2/3 属 B（星形：1/2/3 都邻 0）
        ctx.CellOwner[0] = 0; ctx.CellOwner[1] = 2; ctx.CellOwner[2] = 2; ctx.CellOwner[3] = 2;
        AddWar(ctx, 0, 2, tick: 0, winsA: CivSimContext.WarTributeWins, lastBattle: 0);
        ctx.Tick = 0;   // 无新会战（LastBattle=Tick）→ 直接结算

        new WarModel().Execute(ctx);

        var w = ctx.Wars[0];
        Assert.AreEqual(0, w.TributeTo, "A 为朝贡受方");
        Assert.AreEqual(2, w.TributeFrom, "B 为朝贡出方");
        Assert.AreEqual(CivSimContext.WarTributeTicks, w.TributesLeft);
        Assert.AreEqual(1, ctx.Wars.Count, "朝贡期战争保留（外交状态延续）");
        // 割地：败国边境格（邻战胜国）全部易主（星形 3 格 ≤ WarCedeCells=3）
        Assert.AreEqual(0, ctx.CellOwner[1], "败国边境格 1 割让");
        Assert.AreEqual(0, ctx.CellOwner[2], "败国边境格 2 割让");
        Assert.AreEqual(0, ctx.CellOwner[3], "败国边境格 3 割让");
        Assert.AreEqual(0, ctx.CellOwner[0], "胜国格不动");
        Assert.True(WarModel.IsAtWar(ctx, 0, 2), "朝贡期仍算敌对（断交/冲突×2 依据）");
    }

    // ═════════════════════════════════════════════════════════════════
    // 5. 朝贡转移：每 tick 战败国总人口×WarTributeRate 入胜国池；TributesLeft 归零移除
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Tribute_TransfersPerTick_ThenRemoved()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        var aChief = AddBand(ctx, 0, 0, 100f, 0f, chief: true);
        AddBand(ctx, 1, 1, 50f, 0f);
        AddBand(ctx, 2, 2, 100f, 200f, chief: true);   // 败国人口 200，池 200
        AddBand(ctx, 3, 3, 100f, 100f, chief: false);
        var w = AddWar(ctx, 0, 2, tick: 0);
        w.TributeTo = 0; w.TributeFrom = 2; w.TributesLeft = 2;   // 朝贡期，剩 2 tick
        ctx.Tick = 10;

        new WarModel().Execute(ctx);
        // 每 tick 转移 amount = 200×0.005 = 1.0；按人口均摊：每人 Contributed −0.005×P
        Assert.AreEqual(199.5f, ctx.Bands[2].Contributed, 1e-3f, "败国酋长贡赋扣减");
        Assert.AreEqual(99.5f, ctx.Bands[3].Contributed, 1e-3f, "败国成员贡赋扣减");
        Assert.AreEqual(1.0f, aChief.Contributed, 1e-3f, "胜国池收到贡赋");
        Assert.AreEqual(1, w.TributesLeft, "朝贡倒数 1");
        Assert.AreEqual(1, ctx.Wars.Count);

        ctx.Tick = 11;
        new WarModel().Execute(ctx);
        Assert.AreEqual(0, ctx.Wars.Count, "朝贡归零 → 战争终结移除");
        Assert.AreEqual(2.0f, aChief.Contributed, 1e-3f);
    }

    // ═════════════════════════════════════════════════════════════════
    // 6. 宣战负向门：冷却期内不能宣战（WarCooldownTicks）
    //    ⚠️ 宣战正门概率门控（WarDeclareChance=0.002）不可确定性构造——只测可确定的负向门
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Declare_CooldownBlocksNewWar()
    {
        var grid = StarGrid();
        var ctx = WarCtx(grid);
        var a = AddBand(ctx, 0, 0, 100f, 50f, chief: true);   // 池足（50 ≥ 200×0.02）
        AddBand(ctx, 1, 1, 100f, 0f);
        var b = AddBand(ctx, 2, 2, 100f, 50f, chief: true);
        AddBand(ctx, 3, 3, 100f, 0f);
        a.LastWarTick = 0; b.LastWarTick = 0;   // 冷却期内（30 tick）
        ctx.Tick = 0;

        new WarModel().Execute(ctx);

        Assert.AreEqual(0, ctx.Wars.Count, "冷却期内不得宣战");
        Assert.AreEqual(0, ctx.WarsDeclared);
    }
}