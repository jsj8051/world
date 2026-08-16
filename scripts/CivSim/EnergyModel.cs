// Responsibility: Energy accounting (Order 10) - extracted from CivModels.cs verbatim (pure refactor).
using System;
using System.Collections.Generic;
using Godot;
using World.Biome;
using World.LogicGrid;

namespace World.CivSim;


// ══════════════════════════════════════════════════════════════════
// ② 能量核算（Order 10）：e = Y/P；s = e − 1。
//    e_猎(P)=Y_猎/(P+h) 仅用于生产方式选择（ModeModel）；此处用实际产量（§二 注）。
// ══════════════════════════════════════════════════════════════════
public sealed class EnergyModel : CivModelBase
{
    public override string Name => "能量核算";
    public override int Order => 10;

    public override void Execute(CivSimContext ctx)
    {
        // 刷新格人口（本 tick 起始快照：增长/压力共用）
        Array.Clear(ctx.CellPop, 0, ctx.CellPop.Length);
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead) continue;
            ctx.CellPop[e.Cell] += e.P;
        }
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead) continue;
            float f = e.FLast;   // 当 tick 实际产出（RefreshCellState 已算，含劳动因子/冷下限）
            e.EPerCap = f / Mathf.Max(0.001f, e.P);
            e.Surplus = e.EPerCap - 1f;
        }
    }
}
