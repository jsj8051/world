// 职责：Absorption (Order 47)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Mechanics.Politics;
using World.CivSim.Events;
namespace World.CivSim.Mechanics.Politics;


// ══════════════════════════════════════════════════════════════════
// ①i 吞并（Order 47，2026-08-17 用户拍板）：驻扎格被外部势力覆盖（CellOwner≠自己）→ 吞并。
//   消灭"无家 band"中间态（弱 band 的家被强邻影响力覆盖→要么并入要么迁走——
//   不存在"势力色块无人口点"）。条件：非同格共住（共享村合法）+ 覆盖者更强。
//   处置：迁走优先（领地内无主格可逃→保留身份流亡）；无可逃→并入（P×0.5 转移，
//   战斗损耗+同化——被征服部落，其余人口流失）。同频评估（10 tick，守卫不入档）。
// ══════════════════════════════════════════════════════════════════
public sealed class AbsorptionModel : CivModelBase
{
    public override string Name => "吞并";
    public override int Order => 47;

    protected override bool CanApply(CivSimContext ctx) =>
        ctx.Tick - ctx.AbsorptionLastEval >= 10;   // 频率守卫（10 tick 同频，不入档）

    protected override void Apply(CivSimContext ctx)
    {
        ctx.AbsorptionLastEval = ctx.Tick;   // 频率时间戳（副作用）
        var snapshot = ctx.Polities.ToArray();
        foreach (var e in snapshot)
        {
            if (e.Dead || e.Cell < 0 || e.Cell >= ctx.Grid.N) continue;
            int overlordId = ctx.CellOwner != null ? ctx.CellOwner[e.Cell] : -1;
            if (overlordId == e.Id || overlordId < 0) continue;   // 家在自己手里
            var overlord = FindById(ctx, overlordId);
            if (overlord == null || overlord.Dead) continue;
            // ⚠️ 2026-08-17 v4 修正：恢复同领地/同酋邦豁免（v3 过度——领地内吞并导致部落
            //   聚合崩溃，T22 领地 30→2）。联盟内显示同色（PowerIdOf 同 TerritoryId/ChiefdomId
            //   → 同势力色）→ 弱成员驻扎格被覆盖也显示部落色（色块有强成员驻扎格=有人口）——
            //   不产生无人口势力。散兵（跨势力被覆盖，含同格共住）→ 吞并（用户拍板）。
            if (e.ChiefdomId >= 0 && e.ChiefdomId == overlord.ChiefdomId) continue;
            if (e.TerritorySize >= 2 && e.TerritoryId == overlord.TerritoryId) continue;
            // 覆盖者必须更强（w 陡化后覆盖已需 2.1×——防御性再确认）
            if (overlord.P < e.P) continue;
            // 处置：迁走优先（领地内无主格可逃——流亡保留身份）
            int exile = FindExileCell(ctx, e);
            if (exile >= 0)
            {
                e.Cell = exile;
                e.LastMigrateTick = ctx.Tick;
            }
            else
            {
                // 并入：P×0.5 转移（战斗损耗+同化），自身消亡
                overlord.P += e.P * 0.5f;
                e.P = 0f;
                e.Dead = true;
                ctx.Events.Add(new CivEventRecord(ctx.Tick, EventTypes.PolityDied, e.Id, overlord.Id));
            }
        }
    }

    private static Polity FindById(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (ctx.Polities[i].Id == id) return ctx.Polities[i];
        return null;
    }

    /// <summary>迁走目标：领地格内无主格（CellOwner=-1 且 R>0）最高富饶者——留在自己影响圈内。</summary>
    private static int FindExileCell(CivSimContext ctx, Polity e)
    {
        var terr = e.Id < (ctx.TerritoryCells?.Length ?? 0) ? ctx.TerritoryOf(e) : null;
        if (terr == null) return -1;
        int best = -1;
        float bestR = 0f;
        foreach (var c in terr)
        {
            if (ctx.CellOwner[c] >= 0) continue;
            if (ctx.R == null || ctx.R[c] <= 0f) continue;
            if (ctx.R[c] > bestR) { bestR = ctx.R[c]; best = c; }
        }
        return best;
    }
}
