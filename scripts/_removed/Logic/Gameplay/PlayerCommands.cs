using System;
using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Mechanics.Military;

namespace World.Gameplay;

/// <summary>
/// 玩家命令引擎（第二阶段——EU4 式游玩，2026-08-25 用户拍板：命令队列/注入/存档，纯逻辑 UI 后补）。
/// 命令流：UI（后补）→ Enqueue → CivEngine.Continue 每 tick 开头 ApplyPending（drain）→ 影响本 tick 机制。
/// 确定性：命令按队列序固定应用；命令序列入档（.cmp PLAY 段）→ 读档续玩不丢命令。
/// 玩家模式不破坏自动演化的确定性：ctx.Player == null（纯自动）时 ApplyPending 为 no-op。
/// </summary>
public static class PlayerCommands
{
    // ── 提稳定命令参数（硬编码——用户拍板快速推进）──
    public const float BoostStabilityCost = 20f;     // 花国库量（≈万人国 10 tick 盈余——EU4 花钱买稳定）
    public const float BoostStabilityGain = 0.5f;    // 每次 +0.5 稳定（EU4 +1/次；温和——玩家可连续投入）
    public const float MaxPlayerTaxRate = 0.1f;      // 税率上限 10%（早期国家低税率史实；下限 0）


    /// <summary>玩家给本国外交宣战（即时调用——EU4 玩家手动宣战，不排队）。
    /// 等价 PlayerCommands.Enqueue(DeclareWar) 后立即 drain；门槛同自动宣战（CanDeclare）。</summary>
    public static void Enqueue(PlayerSession player, PlayerCommandKind kind, int targetA, int targetB, float value)
    {
        ArgumentNullException.ThrowIfNull(player);   // 防 null 会话 NRE（P2——复查建议）
        player.Queue.Add(new PlayerCommand
        {
            Kind = kind,
            TargetA = targetA,
            TargetB = targetB,
            Value = value,
            IssuedTick = 0,   // 调用方在注入时补 tick；队列语义即时
        });
    }

    /// <summary>每 tick 开头注入（CivEngine.Continue 循环内调用）：按队列序应用全部命令并清空。
    /// 无玩家（ctx.Player null）或空队列 → no-op（纯自动演化行为不变）。
    /// ⚠️ 玩家只对自己的国家下令（TargetA == 玩家国——P1 复查：引擎是唯一闸门，防误操纵他国）。</summary>
    public static void ApplyPending(CivSimContext ctx)
    {
        var p = ctx.Player;
        if (p == null || p.Queue.Count == 0) return;
        for (int i = 0; i < p.Queue.Count; i++)
        {
            var cmd = p.Queue[i];
            if (cmd.TargetA != p.StateId) continue;   // 己国校验（对别国的命令作废——玩家不是世界神）
            cmd.IssuedTick = ctx.Tick;   // 补下达 tick（存档/诊断）
            switch (cmd.Kind)
            {
                case PlayerCommandKind.SetTaxRate:
                    p.TaxRateOverride = Math.Clamp(cmd.Value, 0f, MaxPlayerTaxRate);
                    break;
                case PlayerCommandKind.BoostStability:
                    BoostStability(ctx, cmd.TargetA);
                    break;
                case PlayerCommandKind.DeclareWar:
                    DeclareWar(ctx, cmd.TargetA, cmd.TargetB);
                    break;
            }
        }
        p.Queue.Clear();   // 全部应用（失败的作废——命令是一次性意志）
    }

    /// <summary>国库出资提稳定（EU4：花行政点/钱 +稳定；国库不足 → 失败作废）。</summary>
    public static bool BoostStability(CivSimContext ctx, int stateId)
    {
        var st = StateById(ctx, stateId);
        if (st == null || st.Treasury < BoostStabilityCost || st.Stability >= 3f) return false;
        st.Treasury -= BoostStabilityCost;
        st.Stability = Math.Min(3f, st.Stability + BoostStabilityGain);
        return true;
    }

    /// <summary>玩家宣战（EU4：玩家意志直接执行，但仍须满足条件——冷却/未交战/领地接触/池足）。</summary>
    public static bool DeclareWar(CivSimContext ctx, int fromStateId, int toStateId)
    {
        if (fromStateId < 0 || toStateId < 0) return false;
        return WarModel.PlayerDeclareWar(ctx, LeaderOf(ctx, fromStateId), LeaderOf(ctx, toStateId));
    }

    private static StateEntity StateById(CivSimContext ctx, int stateId)
    {
        if (ctx.States == null) return null;
        for (int i = 0; i < ctx.States.Count; i++)
            if (ctx.States[i].Id == stateId) return ctx.States[i];
        return null;
    }

    /// <summary>国家首领实体（StateId == 自身 Id 的酋长——与 StateModel.StateSet 同判据）。</summary>
    private static Polity LeaderOf(CivSimContext ctx, int stateId)
    {
        if (ctx.Polities == null) return null;
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead || e.Id != stateId) continue;
            if (e.IsChief && e.StateId == e.Id && e.StateSize >= 2) return e;
        }
        return null;
    }
}