using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Mechanics.State;
using World.Gameplay;
using World.Utils;

namespace World.Tests;

/// <summary>
/// 玩家命令引擎测试（2026-08-25 第二阶段——EU4 式游玩：命令队列/注入/存档，纯逻辑）。
/// 覆盖：税率覆盖生效（且只对自己国家）、提稳定成功/失败、队列应用语义（清空/补 tick）、
/// 宣战防御（越界/无首领）、存档往返（CIVI 哨兵 PLAY 块）。
/// </summary>
[TestFixture]
public class PlayerCommandsTests
{
    /// <summary>构造三条件满足的国家场景（复用 StateMechanismTests.MakeStateCtx 同款）。</summary>
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
    // 1. 税率覆盖（EU4：君主定税——玩家税率覆盖国家默认）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void SetTaxRate_OverridesStateMechanism_TreasuryUsesPlayerRate()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        StateAssign.Rebuild(ctx);            // 国家涌现（前置于玩家绑定）
        StateMechanism.Run(ctx);             // 建档（Treasury=0）
        var st = StateGet(ctx, 0);
        st.Legitimacy = 100f;                // 合法系数 1.0（隔离税率变量；⑥合法回归在 ④ 之后不影响本 tick 国库）

        // 玩家绑定 + 调税率 0.05（默认 0.02）
        ctx.Player = new PlayerSession { StateId = 0 };
        PlayerCommands.Enqueue(ctx.Player, PlayerCommandKind.SetTaxRate, targetA: 0, targetB: 0, value: 0.05f);
        PlayerCommands.ApplyPending(ctx);
        StateMechanism.Run(ctx);

        // 税 = 150×0.05×1.0 = 7.5，费 = 150×0.015 = 2.25 → +5.25（默认 0.02 税率只有 +0.75）
        Assert.AreEqual(5.25f, st.Treasury, 1e-5f, "玩家税率覆盖生效：国库按 0.05 收税");
        Assert.AreEqual(0.05f, ctx.Player.TaxRateOverride, 1e-6f, "税率覆盖已写入会话");
        Assert.AreEqual(0, ctx.Player.Queue.Count, "命令应用后队列清空");
    }

    [Test]
    public void SetTaxRate_OnlyAffectsOwnState()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        ctx.Player = new PlayerSession { StateId = 5 };   // 玩家绑定别的国家
        PlayerCommands.Enqueue(ctx.Player, PlayerCommandKind.SetTaxRate, targetA: 0, targetB: 0, value: 0.05f);

        PlayerCommands.ApplyPending(ctx);

        Assert.AreEqual(-1f, ctx.Player.TaxRateOverride, "命令对象非玩家国家 → 不生效（税率保持默认）");
    }

    [Test]
    public void SetTaxRate_ClampedToMax()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        ctx.Player = new PlayerSession { StateId = 0 };
        PlayerCommands.Enqueue(ctx.Player, PlayerCommandKind.SetTaxRate, targetA: 0, targetB: 0, value: 9.9f);

        PlayerCommands.ApplyPending(ctx);

        Assert.AreEqual(PlayerCommands.MaxPlayerTaxRate, ctx.Player.TaxRateOverride, 1e-6f, "税率上限 10%");
    }

    // ══════════════════════════════════════════════════════════════════
    // 2. 提稳定（EU4：花国库 +稳定）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void BoostStability_CostsTreasury_GainsStability()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        StateAssign.Rebuild(ctx);
        StateMechanism.Run(ctx);   // 建档（档案是机制产物——StateAssign 只设 StateId 标记）
        var st = StateGet(ctx, 0);
        st.Treasury = 100f;
        st.Stability = 0f;
        ctx.Player = new PlayerSession { StateId = 0 };
        PlayerCommands.Enqueue(ctx.Player, PlayerCommandKind.BoostStability, targetA: 0, targetB: 0, value: 0f);

        PlayerCommands.ApplyPending(ctx);

        Assert.AreEqual(80f, st.Treasury, 1e-5f, "国库 −20（EU4 花钱买稳定）");
        Assert.AreEqual(0.5f, st.Stability, 1e-5f, "稳定 +0.5");
    }

    [Test]
    public void BoostStability_FailsWhenTreasuryShort()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        StateAssign.Rebuild(ctx);
        StateMechanism.Run(ctx);   // 建档
        var st = StateGet(ctx, 0);
        st.Treasury = 10f;   // < 20 成本
        ctx.Player = new PlayerSession { StateId = 0 };
        PlayerCommands.Enqueue(ctx.Player, PlayerCommandKind.BoostStability, targetA: 0, targetB: 0, value: 0f);

        PlayerCommands.ApplyPending(ctx);

        Assert.AreEqual(10f, st.Treasury, 1e-5f, "国库不足 → 命令作废（EU4：穷国买不起稳定）");
        Assert.AreEqual(0f, st.Stability, 1e-5f);
    }

    // ══════════════════════════════════════════════════════════════════
    // 3. 宣战防御（成功路径依赖领地接触网格——UI 集成交互时验证；这里防越界）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void DeclareWar_Guards_InvalidTargets()
    {
        var (ctx, chief, member, capital) = MakeStateCtx();
        ctx.Player = new PlayerSession { StateId = 0 };

        Assert.IsFalse(PlayerCommands.DeclareWar(ctx, -1, 0), "越界宣战方 → 拒绝");
        Assert.IsFalse(PlayerCommands.DeclareWar(ctx, 0, 99), "目标国无首领（未涌现国家）→ 拒绝");
        Assert.AreEqual(0, ctx.Wars.Count, "被拒命令不产生战争");
    }

    // ══════════════════════════════════════════════════════════════════
    // 4. 存档往返：CIVI 顺序流 PLAY 哨兵块（玩家绑定 + 税率覆盖 + 待处理队列）
    // ══════════════════════════════════════════════════════════════════

    [Test]
    public void Archive_Roundtrip_PlayerSessionRestored()
    {
        var civ = ArchiveChunkTests.MakeMinCivPublic();
        civ.Context.Player = new PlayerSession
        {
            StateId = 3,
            TaxRateOverride = 0.04f,
            Queue =
            {
                new PlayerCommand { Kind = PlayerCommandKind.SetTaxRate, TargetA = 3, Value = 0.04f, IssuedTick = 10 },
                new PlayerCommand { Kind = PlayerCommandKind.BoostStability, TargetA = 3, IssuedTick = 11 },
            },
        };

        var ms = new MemoryStream();
        var w = new ChunkWriter(ms, "MPA1", 9);   // 与 MapArchive.Version=9 对齐
        w.BeginSegment("CIVI", 1);
        CivMapArchive.WriteCivilization(w, civ);
        w.EndSegment();
        w.Finish();
        ms.Position = 0;

        var r = new ChunkReader(ms);
        Assert.IsTrue(r.SeekSegment("CIVI"));
        var back = CivMapArchive.ReadCivilization(r, ArchiveChunkTests.MakeMinGridPublic(), out bool corrupted);
        Assert.IsFalse(corrupted);
        Assert.IsNotNull(back);

        var p = back.Context.Player;
        Assert.IsNotNull(p, "PLAY 哨兵块往返恢复玩家会话");
        Assert.AreEqual(3, p.StateId);
        Assert.AreEqual(0.04f, p.TaxRateOverride, 1e-6f);
        Assert.AreEqual(2, p.Queue.Count, "待处理命令队列还原");
        Assert.AreEqual(PlayerCommandKind.SetTaxRate, p.Queue[0].Kind);
        Assert.AreEqual(0.04f, p.Queue[0].Value, 1e-6f);
        Assert.AreEqual(PlayerCommandKind.BoostStability, p.Queue[1].Kind);
        Assert.AreEqual(10, p.Queue[0].IssuedTick);
    }

    [Test]
    public void Archive_Roundtrip_NoPlayer_NullSession()
    {
        var civ = ArchiveChunkTests.MakeMinCivPublic();   // 无玩家（ctx.Player 默认 null）

        var ms = new MemoryStream();
        var w = new ChunkWriter(ms, "MPA1", 9);
        w.BeginSegment("CIVI", 1);
        CivMapArchive.WriteCivilization(w, civ);
        w.EndSegment();
        w.Finish();
        ms.Position = 0;

        var r = new ChunkReader(ms);
        r.SeekSegment("CIVI");
        var back = CivMapArchive.ReadCivilization(r, ArchiveChunkTests.MakeMinGridPublic(), out bool corrupted);
        Assert.IsFalse(corrupted);
        Assert.IsNull(back.Context.Player, "无玩家存档 → 读回 null（纯自动模式）");
    }

    // ── 工具 ──
    private static StateEntity StateGet(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.States.Count; i++)
            if (ctx.States[i].Id == id) return ctx.States[i];
        return null;
    }
}