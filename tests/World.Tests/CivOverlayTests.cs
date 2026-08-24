using System;
using System.Collections.Generic;
using Godot;
using NUnit.Framework;
using World.CivSim;
using World.CivSim.Observation;
using World.HexPlanet;
using World.LogicGrid;

using World.CivSim.Entities;
namespace World.Tests;

/// <summary>
/// 观测投影层测试（2026-08-24，docs/设计-观测面板与文明记录.md ①投影层）。
/// CivOverlay.Observe 纯函数——构造 Context → 断言快照字段。
/// 覆盖：空 Context 防御 / 计数 / 概念标签（单一事实源判据）/ 国家卡片（都城·君主·成员·池·战争态）/
///   声望降序排序 / 科技持有者数 / 领地格防御读。
/// 约束同项目铁律：只 [Test]、无 SetUp；小网格；无引擎调用。
/// </summary>
public class CivOverlayTests
{
    /// <summary>星形 4 格网格（CivSimWarTests 同式）：0 居中，邻 1/2/3。</summary>
    private static GameGrid StarGrid()
    {
        Icosahedron.Subdivide(2, 6371f, out var verts, out _);
        var g = new GameGrid
        {
            N = 4, GridN = 2, RadiusKm = 6371f,
            Verts = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(-1, 0, 0) },
            Elev = new float[4], Temp = new float[4], Precip = new float[4], Biome = new byte[4],
        };
        g.OverrideNeighbors(new[] { new[] { 1, 2, 3 }, new[] { 0 }, new[] { 0 }, new[] { 0 } });
        return g;
    }

    private static CivSimContext Ctx(GameGrid grid)
    {
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellPolities = new Polity[grid.N],
            Polities = new List<Polity>(),
            Tick = 233,
            Rng = new DeterministicRandom(7),
            NextPolityId = 10,
            Habitations = new List<Habitation>(),
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

    private static Polity Add(CivSimContext ctx, int id, int cell, float p,
        bool farming = false, bool chief = false, int chiefdomId = -1, int chiefdomSize = 1,
        int stateId = -1, int stateSize = 1, float prestige = 0f, float contributed = 0f)
    {
        var e = new Polity
        {
            Id = id, Cell = cell, P = p, IsFarming = farming,
            IsChief = chief, ChiefdomId = chiefdomId, ChiefdomSize = chiefdomSize,
            StateId = stateId, StateSize = stateSize,
            Prestige = prestige, Contributed = contributed,
        };
        ctx.Polities.Add(e);
        ctx.CellPolities[cell] = e;
        return e;
    }

    // ═══════════════════════════════════════════════════════════════
    // 1. 防御：null / 空 Context
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Observe_NullContext_EmptySnapshot_NoThrow()
    {
        var snap = CivOverlay.Observe(null);
        Assert.That(snap, Is.Not.Null);
        Assert.That(snap.PolityCount, Is.Zero);
        Assert.That(snap.States.Count, Is.Zero);
        Assert.That(snap.TotalPop, Is.Zero);
    }

    [Test]
    public void Observe_EmptyContext_NoThrow()
    {
        var snap = CivOverlay.Observe(Ctx(StarGrid()));
        Assert.That(snap.PolityCount, Is.Zero);
        Assert.That(snap.Tick, Is.EqualTo(233));
    }

    // ═══════════════════════════════════════════════════════════════
    // 2. 计数与概念标签（单一事实源判据）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Observe_Counts_And_ConceptLabels()
    {
        var grid = StarGrid();
        var ctx = Ctx(grid);
        Add(ctx, 0, 0, 10f);                                            // band（无农无邦）
        Add(ctx, 1, 1, 30f, farming: true);                             // tribe（务农）
        Add(ctx, 2, 2, 40f, chief: true, chiefdomId: 2, chiefdomSize: 2,
            stateId: 2, stateSize: 2);                                  // state（至尊酋长）
        Add(ctx, 3, 3, 20f, chiefdomId: 2, chiefdomSize: 2);            // chiefdom 成员

        var snap = CivOverlay.Observe(ctx);
        Assert.That(snap.PolityCount, Is.EqualTo(4));
        Assert.That(snap.TotalPop, Is.EqualTo(100L));
        Assert.That(snap.StateCount, Is.EqualTo(1));
        Assert.That(snap.ChiefdomCount, Is.EqualTo(1));

        var byId = new Dictionary<int, string>();
        foreach (var r in snap.Polities) byId[r.Id] = r.Concept;
        Assert.That(byId[0], Is.EqualTo("band"));
        Assert.That(byId[1], Is.EqualTo("tribe"));
        Assert.That(byId[2], Is.EqualTo("state"));
        Assert.That(byId[3], Is.EqualTo("chiefdom"));
    }

    [Test]
    public void Observe_DeadPolities_Excluded()
    {
        var ctx = Ctx(StarGrid());
        var e = Add(ctx, 0, 0, 10f);
        e.Dead = true;
        Add(ctx, 1, 1, 5f);

        var snap = CivOverlay.Observe(ctx);
        Assert.That(snap.PolityCount, Is.EqualTo(1));
        Assert.That(snap.TotalPop, Is.EqualTo(5L));
    }

    // ═══════════════════════════════════════════════════════════════
    // 3. 国家卡片
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Observe_StateRow_Capital_Monarch_Pool_Members_War()
    {
        var grid = StarGrid();
        var ctx = Ctx(grid);
        var capital = new Habitation { Id = 7, Cell = 0, OccupantId = 2 };
        ctx.Habitations.Add(capital);
        // 国家：至尊酋长 id2（声望 5），成员 id3（声望 9 → 君主）
        Add(ctx, 0, 0, 10f, chiefdomId: 0, chiefdomSize: 1);
        ctx.ChiefdomCells[2].Add(2);
        ctx.ChiefdomCells[2].Add(3);
        Add(ctx, 2, 2, 40f, chief: true, chiefdomId: 2, chiefdomSize: 2,
            stateId: 2, stateSize: 2, prestige: 5f, contributed: 12f);
        Add(ctx, 3, 3, 20f, chiefdomId: 2, chiefdomSize: 2, prestige: 9f);
        ctx.Polities.Find(x => x.Id == 2).PlaceId = 7;   // 至尊酋长占据都城聚落

        // 战争态：国家 2 vs 国家 0（其实 0 不是国家——只用 Involves 判定）
        ctx.Wars.Add(new War { StateIdA = 2, StateIdB = 0, Defender = 0, StartTick = 10, LastBattleTick = 10 });

        var snap = CivOverlay.Observe(ctx);
        Assert.That(snap.States.Count, Is.EqualTo(1));
        var s = snap.States[0];
        Assert.That(s.Id, Is.EqualTo(2));
        Assert.That(s.CapitalPlaceId, Is.EqualTo(7));
        Assert.That(s.MonarchId, Is.EqualTo(3));      // 声望 9 > 5 → 成员继任君主
        Assert.That(s.Pool, Is.EqualTo(12f));
        Assert.That(s.MemberCount, Is.EqualTo(2));
        Assert.That(s.IsAtWar, Is.True);
    }

    [Test]
    public void Observe_StateRow_NotAtWar_WhenNoWar()
    {
        var grid = StarGrid();
        var ctx = Ctx(grid);
        ctx.ChiefdomCells[0].Add(0);
        ctx.ChiefdomCells[0].Add(1);
        Add(ctx, 0, 0, 40f, chief: true, chiefdomId: 0, chiefdomSize: 2, stateId: 0, stateSize: 2);
        Add(ctx, 1, 1, 20f, chiefdomId: 0, chiefdomSize: 2);

        var snap = CivOverlay.Observe(ctx);
        Assert.That(snap.States.Count, Is.EqualTo(1));
        Assert.That(snap.States[0].IsAtWar, Is.False);
    }

    // ═══════════════════════════════════════════════════════════════
    // 4. 排序 / 领地防御 / 科技（TechTable 未加载 → 空卷轴，不抛）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Observe_Polities_SortedByPrestigeDesc()
    {
        var ctx = Ctx(StarGrid());
        Add(ctx, 0, 0, 10f, prestige: 2f);
        Add(ctx, 1, 1, 10f, prestige: 8f);
        Add(ctx, 2, 2, 10f, prestige: 5f);

        var snap = CivOverlay.Observe(ctx);
        Assert.That(snap.Polities[0].Prestige, Is.EqualTo(8f));
        Assert.That(snap.Polities[1].Prestige, Is.EqualTo(5f));
        Assert.That(snap.Polities[2].Prestige, Is.EqualTo(2f));
    }

    [Test]
    public void Observe_TerritoryCells_Missing_Zero_NoThrow()
    {
        var ctx = Ctx(StarGrid());
        ctx.TerritoryCells = null;   // 未建（读档前/构造遗漏）
        Add(ctx, 0, 0, 10f);

        var snap = CivOverlay.Observe(ctx);
        Assert.That(snap.Polities[0].TerritoryCells, Is.Zero);
    }

    [Test]
    public void Observe_TechTable_NotLoaded_EmptyTechs_NoThrow()
    {
        var ctx = Ctx(StarGrid());
        Add(ctx, 0, 0, 10f);
        var snap = CivOverlay.Observe(ctx);
        Assert.That(snap.Techs, Is.Not.Null);   // 测试环境无 res:// → 空卷轴（防御）
    }
}