using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Mechanics.State;
using World.Utils;

namespace World.Tests;

/// <summary>
/// 国家制度机制测试（2026-08-25 通用国家机制——EU4 式制度层持久状态）。
/// 覆盖：建档（都城/君主/国库/稳定度/合法性初始值）、君主死亡继位（国家不灭）、
/// 国库收支（高合法盈余/低合法赤字压稳定）、崩盘（稳定 ≤ −2 → 都城陷落 → 自愈删档）、
/// 存档往返（CIVI 顺序流 STAT 哨兵块）。
/// </summary>
[TestFixture]
public class StateMechanismTests
{
    /// <summary>构造三条件满足的国家场景（复用 CivSimMechanicTests 的国家涌现构造：
    /// ① 都城治理中心 ✓ ③ 贡赋池 2.0 ≥ 150×0.01 ✓ ④ 存续 30−0 ≥ 20 ✓）。</summary>
    private static (CivSimContext ctx, Polity chief, Polity member, Habitation capital) MakeStateCtx()
    {
        var chief = new Polity
        {
            Id = 0, Cell = 0, P = 100, IsChief = true, ChiefdomId = 0,
            Contributed = 1.5f, PlaceId = 10, TerritoryId = 0, Prestige = 5f,
        };
        var member = new Polity
        {
            Id = 1, Cell = 1, P = 50, Contributed = 0.5f,
            PlaceId = -1, TerritoryId = 1, Prestige = 1f,
        };
        var capital = new Habitation { Id = 10, Cell = 0, BornTick = 0, HasAdmin = true, DwellFrom = 0, OccupantId = 0 };
        var cells = new List<int>[8];
        for (int i = 0; i < 8; i++) cells[i] = new List<int>();
        cells[0] = new List<int> { 0, 1 };
        var ctx = new CivSimContext
        {
            Polities = new List<Polity> { chief, member },
            Habitations = new List<Habitation> { capital },
            ChiefdomCells = cells,
            Tick = 30,
            Wars = new List<War>(),
        };
        return (ctx, chief, member, capital);
    }

    // ══════════════════════════════════════════════════════════════════
    // 1. 建档
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Create_NewState_RegisteredWithMonarchCapital()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        StateAssign.Rebuild(ctx);
        Assert.AreEqual(0, chief.StateId, "三条件满足 → 国家涌现（前置于建档）");

        StateMechanism.Run(ctx);

        Assert.AreEqual(1, ctx.States.Count, "涌现国家 → 自动建档");
        var st = ctx.States[0];
        Assert.AreEqual(0, st.Id, "档案 Id = 酋长 Polity Id");
        Assert.AreEqual(10, st.CapitalHabId, "都城 = 制度载体（酋长占据的城市聚落）");
        Assert.AreEqual(0, st.MonarchId, "君主 = 成员 Prestige 最高者（chief 5 > member 1——P7 虚拟头衔）");
        Assert.AreEqual(0f, st.Treasury, 1e-5f, "首 tick 收支平衡（合法 50 → 税=费=2.25）");
        Assert.AreEqual(0f, st.Stability, 1e-5f, "新国稳定度 0（无危机无战争）");
        Assert.AreEqual(CivSimContext.StateLegitimacyBase, st.Legitimacy, 1e-5f, "建档合法性基准 50");
        Assert.AreEqual(30, st.BornTick, "建国 tick = 建档 tick");
    }

    [Test]
    public void NonState_NoArchive_AndLostState_ArchiveRemoved()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        StateAssign.Rebuild(ctx);
        StateMechanism.Run(ctx);
        Assert.AreEqual(1, ctx.States.Count);

        // 打破三条件（贡赋池清零）→ 国家消失 → 档案自愈删除
        chief.Contributed = 0f;
        member.Contributed = 0f;
        StateAssign.Rebuild(ctx);
        Assert.AreEqual(-1, chief.StateId, "贡赋断流 → 国家不再涌现");
        StateMechanism.Run(ctx);
        Assert.AreEqual(0, ctx.States.Count, "亡国档案自愈删除");
    }

    // ══════════════════════════════════════════════════════════════════
    // 2. 君主更替（P7/P9：君主死 → Prestige 最高继位——国家不灭，制度化推举）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void MonarchDeath_NewMonarchSucceeds_StateSurvives()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        member.Prestige = 10f;   // 让成员当君主（成员 Prestige 最高）
        StateAssign.Rebuild(ctx);
        StateMechanism.Run(ctx);
        var st = ctx.States[0];
        Assert.AreEqual(1, st.MonarchId, "建档君主 = Prestige 最高成员（member 10 > chief 5）");

        member.Dead = true;      // 君主驾崩
        StateMechanism.Run(ctx);

        Assert.AreEqual(1, ctx.States.Count, "君主死 → 国家不灭（官僚制推举，P9）");
        Assert.AreEqual(0, st.MonarchId, "新君主 = 次高 Prestige 成员（chief 继位）");
        // −1.1 = −1（更替惩罚）+ −0.1（连锁危机：新君合法 30 → 税 1.3 < 费 1.5 → 赤字 −0.2 → 财政危机压稳定）
        //   这正是 EU4 式连锁：继位混乱 → 低合法 → 征税难 → 财政压力（机制正确行为）
        Assert.AreEqual(-1.1f, st.Stability, 1e-5f, "更替稳定度惩罚 −1 + 赤字危机 −0.1（继位连锁）");
        // 30.4 = 30（新君初立）+ 0.4（同 tick 向基准回归 (50−30)×0.02——治绩修复当 tick 生效）
        Assert.AreEqual(30.4f, st.Legitimacy, 1e-5f, "新君初立合法性 30 + 回归 0.4（EU4 继承合法性）");
    }

    // ══════════════════════════════════════════════════════════════════
    // 3. 国库收支（税收 − 维持费；合法折损）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Treasury_HighLegitimacy_AccumulatesSurplus()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        StateAssign.Rebuild(ctx);
        StateMechanism.Run(ctx);
        var st = ctx.States[0];
        st.Legitimacy = 100f;   // 全合法 → 税率折损系数 1.0（税 3.0 > 费 2.25）

        StateMechanism.Run(ctx);

        Assert.AreEqual(0.75f, st.Treasury, 1e-5f, "盈余 = 150×0.02×1.0 − 150×0.015 = +0.75");
        Assert.AreEqual(0f, st.Stability, 1e-5f, "国库盈余 + 无危机 → 稳定度不降");
    }

    [Test]
    public void Treasury_Deficit_NegativeTreasuryPressuresStability()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        StateAssign.Rebuild(ctx);
        StateMechanism.Run(ctx);
        var st = ctx.States[0];
        st.Legitimacy = 0f;   // 零合法 → 税率折损 0.5（税 1.5 < 费 2.25 → 赤字）

        StateMechanism.Run(ctx);

        Assert.AreEqual(-0.75f, st.Treasury, 1e-5f, "赤字 = 150×0.02×0.5 − 150×0.015 = −0.75");
        Assert.AreEqual(-CivSimContext.StateStabilityCrisisDrop, st.Stability, 1e-5f,
            "国库赤字 = 财政危机 → 稳定度每 tick −0.1（EU4：财政拖垮稳定）");
    }

    // ══════════════════════════════════════════════════════════════════
    // 4. 崩盘：稳定度 ≤ −2 → 都城陷落（制度载体毁）→ 三条件断 → 国家自然消亡
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Collapse_StabilityBelowThreshold_CapitalFalls_ThenStateDies()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        StateAssign.Rebuild(ctx);
        StateMechanism.Run(ctx);
        var st = ctx.States[0];
        st.Stability = -2.1f;   // 处于崩盘线以下（−2.1 recover 后 −2.07 仍触发）

        StateMechanism.Run(ctx);

        Assert.AreEqual(-1, capital.OccupantId, "都城陷落（被弃）——制度载体毁（EU4 低稳定首都沦陷）");
        Assert.AreEqual(30, capital.RuinFrom, "都城废弃起点 = 崩盘 tick");
        Assert.AreEqual(1, ctx.States.Count, "档案尚未删（下 tick StateAssign 判三条件断）");

        // 下 tick：都城失守 → 三条件断 → 国家不再涌现 → 档案自愈删除
        StateAssign.Rebuild(ctx);
        Assert.AreEqual(-1, chief.StateId, "都城陷落 → 都城条件失败 → 国家消失（StateAssign）");
        StateMechanism.Run(ctx);
        Assert.AreEqual(0, ctx.States.Count, "亡国档案自愈删除");
    }

    // ══════════════════════════════════════════════════════════════════
    // 5. 存档往返：CIVI 顺序流 STAT 哨兵块（.mpa v8 / .cmp 共用路径）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Archive_Roundtrip_StateEntityRestored()
    {
        var civ = ArchiveChunkTests.MakeMinCivPublic();
        civ.Context.States.Add(new StateEntity
        {
            Id = 3,
            CapitalHabId = 8,
            MonarchId = 3,
            Treasury = 12.5f,
            Stability = -1.25f,
            Legitimacy = 40f,
            BornTick = 77,
        });

        var ms = new MemoryStream();
        var w = new ChunkWriter(ms, "MPA1", 9);   // 与 MapArchive.Version=9 对齐（v9：CIVI 功能定性）
        w.BeginSegment("CIVI", 1);
        CivMapArchive.WriteCivilization(w, civ);
        w.EndSegment();
        w.Finish();
        ms.Position = 0;

        var r = new ChunkReader(ms);
        Assert.IsTrue(r.SeekSegment("CIVI"));
        var back = CivMapArchive.ReadCivilization(r, ArchiveChunkTests.MakeMinGridPublic(), out bool corrupted);
        Assert.IsFalse(corrupted, "CIVI 段解码不应报损坏");
        Assert.IsNotNull(back);

        Assert.AreEqual(1, back.Context.States.Count, "STAT 哨兵块往返恢复");
        var st = back.Context.States[0];
        Assert.AreEqual(3, st.Id);
        Assert.AreEqual(8, st.CapitalHabId);
        Assert.AreEqual(3, st.MonarchId);
        Assert.AreEqual(12.5f, st.Treasury, 1e-4f);
        Assert.AreEqual(-1.25f, st.Stability, 1e-4f);
        Assert.AreEqual(40f, st.Legitimacy, 1e-4f);
        Assert.AreEqual(77, st.BornTick);
    }
}