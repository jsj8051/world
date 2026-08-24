using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Godot;
using NUnit.Framework;
using World.Biome;
using World.CivSim;
using World.CivSim.Events;
using World.CivSim.Mechanics.Culture;
using World.CivSim.Mechanics.Military;
using World.CivSim.Mechanics.State;
using World.HexPlanet;
using World.LogicGrid;

using World.CivSim.Entities;
namespace World.Tests;

/// <summary>
/// 文明记录事件系统测试（2026-08-24 ⑪，docs/设计-观测面板与文明记录.md）。
/// 覆盖：
///   T88 领口事件——国家涌现/崩溃（StateModel diff）、朝贡归零停战（WarPeace）、发明（lambda 必然）；
///   T88b 事件合法性——全量演化事件：类型索引合法、Tick 单调；
///   T90 确定性红线——同 seed 演化两次：末态签名 + 事件序列逐条一致（事件旁路不破坏确定性）。
/// 约束同项目铁律：只 [Test]、无 SetUp；小网格；无引擎调用。
/// </summary>
public class CivEventTests
{
    // ── 网格与语境构造（CivSimMechanics2Tests 同式）──

    private static GameGrid MakeMiniGrid()
    {
        Icosahedron.Subdivide(2, 6371f, out var verts, out _);
        int n = verts.Count;
        var unit = new Vector3[n];
        for (int i = 0; i < n; i++) unit[i] = verts[i].Normalized();
        return new GameGrid
        {
            N = n, GridN = 2, RadiusKm = 6371f, Seed = 7,
            Verts = unit,
            Elev = Enumerable.Repeat(1f, n).ToArray(),
            Temp = Enumerable.Repeat(25f, n).ToArray(),
            Precip = Enumerable.Repeat(1500f, n).ToArray(),
            Biome = Enumerable.Repeat((byte)BiomeType.HotSteppe, n).ToArray(),
            SoilLevel = Enumerable.Repeat((byte)3, n).ToArray(),
            LakeLevel = new byte[n],
        };
    }

    /// <summary>演化全量初始语境（复刻 CivEngine.Run 布局；R 微利让原生态部落可存续；无种子/无转农）。</summary>
    private static CivSimContext InitFullCtx(GameGrid grid, int seed)
    {
        int n = grid.N;
        return new CivSimContext
        {
            Grid = grid,
            CellPolities = new Polity[n],
            Polities = new List<Polity>(),
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
            WildCrops = new byte[n],
            Suit = null,
            Tick = 0,
        };
    }

    /// <summary>手工等价 tick 循环（复刻 CivEngine.Run 主循环；CivSimMechanics2Tests 同式）。</summary>
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
        ctx.Polities.RemoveAll(e => e.Dead);
        CivEngine.SettleDerived(ctx);
        ctx.Tick = ticks;
        return ctx;
    }

    private static int[] Repeat(int v, int n)
    {
        var a = new int[n];
        Array.Fill(a, v);
        return a;
    }

    /// <summary>事件序列签名（确定性比较；tick:type:subj:targ:val 固定格式逐条）。</summary>
    private static string EventSignature(CivSimContext ctx)
    {
        var sb = new StringBuilder();
        if (ctx.Events != null)
            foreach (var e in ctx.Events)
                sb.Append(e.Tick).Append(':').Append(e.TypeIndex).Append(':').Append(e.SubjectId)
                  .Append(':').Append(e.TargetId).Append(':').Append(e.Value.ToString("0.000", CultureInfo.InvariantCulture)).Append(';');
        return sb.ToString();
    }

    /// <summary>末态签名（总人口 6 位 + 关键统计——确定性回归用）。</summary>
    private static string StateSignature(CivSimContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append("Pop=").Append(ctx.TotalPopulation().ToString("0.000000", CultureInfo.InvariantCulture));
        sb.Append(";Pol=").Append(ctx.Polities.Count);
        sb.Append(";Fis=").Append(ctx.Fissions);
        sb.Append(";Mig=").Append(ctx.Migrations);
        sb.Append(";Conf=").Append(ctx.Conflicts);
        return sb.ToString();
    }

    /// <summary>4 格星形网格 + 全字段语境（国家构造用——抄 CivSimWarTests）。</summary>
    private static CivSimContext NationCtx(GameGrid grid)
    {
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellPolities = new Polity[grid.N],
            Polities = new List<Polity>(),
            Tick = 30,
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

    // ═══════════════════════════════════════════════════════════════
    // T88 领口事件
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void StateEmerge_And_StateGone_Events_Emitted()
    {
        var grid = StarGrid();
        var ctx = NationCtx(grid);
        // 都城聚落（治理中心 IsCity + 存续 30 tick ≥ 20）
        ctx.Habitations.Add(new Habitation { Id = 7, Cell = 0, BornTick = 0, OccupantId = 2, HasAdmin = true });
        // 酋长 id2（至尊，P=40 贡赋池 50）+ 成员 id3
        ctx.ChiefdomCells[2].Add(2);
        ctx.ChiefdomCells[2].Add(3);
        var chief = new Polity { Id = 2, Cell = 0, P = 40f, IsChief = true, ChiefdomId = 2, ChiefdomSize = 2, Contributed = 50f, PlaceId = 7 };
        ctx.Polities.Add(chief);
        ctx.CellPolities[0] = chief;
        var member = new Polity { Id = 3, Cell = 1, P = 30f, ChiefdomId = 2, ChiefdomSize = 2 };
        ctx.Polities.Add(member);
        ctx.CellPolities[1] = member;

        // 涌现：执行 StateModel → StateEmerge
        new StateModel().Execute(ctx);
        var emerge = ctx.Events.FirstOrDefault(e => e.TypeIndex == EventTypes.StateEmerge);
        Assert.That(emerge.SubjectId, Is.EqualTo(2), "国家涌现事件主体 = 至尊酋长");

        // 崩溃：都城废弃（统治者迁走/聚落被毁）→ 条件不再满足 → StateGone
        var cap = ctx.Habitations[0];
        cap.OccupantId = -1;
        new StateModel().Execute(ctx);
        var gone = ctx.Events.LastOrDefault(e => e.TypeIndex == EventTypes.StateGone);
        Assert.That(gone.SubjectId, Is.EqualTo(2), "国家崩溃事件主体 = 原至尊酋长");
    }

    [Test]
    public void WarPeace_Event_WhenTributeEnds()
    {
        var grid = StarGrid();
        var ctx = NationCtx(grid);
        // 朝贡战争：TributeTo=0 收贡、TributeFrom=2 供贡、剩余 1 tick → 归零 → 停战事件
        ctx.ChiefdomCells[0].Add(0);
        ctx.ChiefdomCells[2].Add(2);
        ctx.Polities.Add(new Polity { Id = 0, Cell = 0, P = 100f, IsChief = true, ChiefdomId = 0, ChiefdomSize = 2, StateId = 0, StateSize = 2 });
        ctx.CellPolities[0] = ctx.Polities[0];
        ctx.Polities.Add(new Polity { Id = 2, Cell = 2, P = 50f, IsChief = true, ChiefdomId = 2, ChiefdomSize = 2, StateId = 2, StateSize = 2 });
        ctx.CellPolities[2] = ctx.Polities[1];
        ctx.Wars.Add(new War { StateIdA = 0, StateIdB = 2, Defender = 2, StartTick = 5, LastBattleTick = 5, TributeTo = 0, TributeFrom = 2, TributesLeft = 1 });

        new WarModel().Execute(ctx);

        var peace = ctx.Events.FirstOrDefault(e => e.TypeIndex == EventTypes.WarPeace);
        Assert.That(peace.SubjectId, Is.EqualTo(0), "停战事件主体 = 战胜方（收贡国）");
        Assert.That(peace.TargetId, Is.EqualTo(2), "停战事件客体 = 战败方（供贡国）");
        Assert.That(ctx.Wars.Count, Is.Zero, "朝贡归零 → 战争移除");
    }

    [Test]
    public void Invention_Event_Emitted_WhenLambdaForced()
    {
        // 完整演化语境（SoilLevel/WildCrops/流动性数组齐全——InventionModel 内部调 RefreshCellState）
        var grid = MakeMiniGrid();
        var ctx = InitFullCtx(grid, 7);
        // 迷你科技表（测试注入；fire 索引 0；字段补全防 EnvFactor null 解引用）——
        // 人口 1e6 → λ = 0.02×(1e6/30)×1 ≈ 666 > 1 → 必然发明。
        // ⚠️ finally 恢复空表：LoadForTest 是全局静态，不能污染其他用例（EmptyTable 安全降级用例依赖空表）
        TechTable.LoadForTest(new[]
        {
            new TechDef { Key = "fire", Name = "火", InvRate = 0.02f, PRef = 30f, InvEnv = Array.Empty<string>(), Requires = Array.Empty<string>() },
        });
        try
        {
            var e = new Polity { Id = 0, Cell = 0, P = 1_000_000f };
            ctx.Polities.Add(e);
            ctx.CellPolities[0] = e;

            new InventionModel().Execute(ctx);

            var inv = ctx.Events.FirstOrDefault(ev => ev.TypeIndex == EventTypes.Invention);
            Assert.That(inv.SubjectId, Is.EqualTo(0));
            Assert.That((int)inv.Value, Is.EqualTo(0), "fire 在注入表的索引 = 0（Value 编码科技索引）");
            Assert.That(e.TechKeys.Contains(TechTable.Fire), Is.True);
        }
        finally
        {
            TechTable.LoadForTest(null);   // 恢复空表（测试进程共享静态——防泄漏）
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // T88b 事件合法性（全量演化）：类型索引合法 + Tick 单调
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void IntegratedEvents_AllValid_MonotonicTicks()
    {
        var ctx = RunManual(MakeMiniGrid(), 7, 40);
        int lastTick = -1;
        foreach (var e in ctx.Events)
        {
            Assert.That(EventTypes.NameOf(e.TypeIndex), Is.Not.EqualTo("未知"), "类型索引必须在注册表内");
            Assert.That(e.Tick, Is.GreaterThanOrEqualTo(lastTick), "事件 tick 单调不减（旁路 append 序）");
            lastTick = e.Tick;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // T90 确定性红线：同 seed 演化两次 → 末态签名 + 事件序列逐条一致
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SameSeed_Twice_StateAndEvents_Identical()
    {
        var grid = MakeMiniGrid();
        var ctx1 = RunManual(grid, 7, 40);
        var ctx2 = RunManual(grid, 7, 40);

        Assert.That(EventSignature(ctx1), Is.EqualTo(EventSignature(ctx2)),
            "事件序列必须逐条可复现（事件旁路不读 Rng、不改遍历序）");
        Assert.That(StateSignature(ctx1), Is.EqualTo(StateSignature(ctx2)),
            "末态签名必须一致（事件系统不改变演化结果一比特）");

        // 顺带证明事件真实发生（40 tick 演化必然有分裂/灭绝/发明——旁路在记录）
        Assert.That(ctx1.Events.Count, Is.GreaterThan(0), "演化应产生事件（否则旁路无效）");
    }

    // ═══════════════════════════════════════════════════════════════
    // T89 EVNT 段格式契约（层内测试——CivMapArchive 走 LogService（GD.Print）测试进程不可用，
    //   接线由窗口真实写/读档验证；本测守住"5 字段紧凑布局 + 缺段语义"不漂移）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void EvntSegment_Roundtrip_SameLayoutAsArchive()
    {
        // 与 CivMapArchive.Write("EVNT") 完全同款布局：Count + [Tick u32, TypeIndex u16, SubjectId u32, TargetId u32, Value f32]
        var ms = new System.IO.MemoryStream();
        var w = new World.Utils.ChunkWriter(ms, "CMP1", 17);
        w.BeginSegment("EVNT", 1);
        var events = new List<CivEventRecord>
        {
            new(10, EventTypes.WarDeclared, 2, 5, 0f),
            new(33, EventTypes.Invention, 7, -1, 1f),
            new(120, EventTypes.StateEmerge, 2, -1, 0f),
        };
        w.Store32((uint)events.Count);
        foreach (var ev in events)
        {
            w.Store32((uint)ev.Tick);
            w.Store16((ushort)ev.TypeIndex);
            w.Store32((uint)ev.SubjectId);
            w.Store32((uint)ev.TargetId);
            w.StoreFloat(ev.Value);
        }
        w.EndSegment();
        w.Finish();

        ms.Position = 0;
        var r = new World.Utils.ChunkReader(ms);
        Assert.That(r.SeekSegment("EVNT"), Is.True);
        int count = (int)r.Get32();
        Assert.That(count, Is.EqualTo(events.Count));
        for (int i = 0; i < count; i++)
        {
            Assert.That((int)r.Get32(), Is.EqualTo(events[i].Tick));
            Assert.That(r.Get16(), Is.EqualTo(events[i].TypeIndex));
            Assert.That((int)r.Get32(), Is.EqualTo(events[i].SubjectId));
            Assert.That((int)r.Get32(), Is.EqualTo(events[i].TargetId));
            Assert.That(r.GetFloat(), Is.EqualTo(events[i].Value).Within(1e-6f));
        }
    }

    [Test]
    public void EvntSegment_Missing_SeekFails_EmptyHistory()
    {
        // 旧档无 EVNT 段：SeekSegment 返回 false（读端 → 空历史，不阻断）——缺段惯例
        var ms = new System.IO.MemoryStream();
        var w = new World.Utils.ChunkWriter(ms, "CMP1", 17);
        w.BeginSegment("HEAD", 1);
        w.Store32(7);
        w.EndSegment();
        w.Finish();

        ms.Position = 0;
        var r = new World.Utils.ChunkReader(ms);
        Assert.That(r.SeekSegment("EVNT"), Is.False, "缺段 = Seek 失败（读端按空事件处理）");
    }
}