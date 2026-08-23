// 职责：Habitation (Order 48)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Mechanics.Territory;
namespace World.CivSim.Mechanics.Territory;


// ══════════════════════════════════════════════════════════════════
// ①j 聚落（Order 48，2026-08-19 阶段3 聚落设计；docs/阶段3设计-聚落实体.md）：
//    物理场所实体——农业部落（settle）的驻扎点固化；场所比人长寿。
//    形成：IsFarming 部落无聚落 → 所在格废墟接管（继承 Level）/新建（Level 0）；
//    存续：部落迁徙/灭绝 → 聚落 OccupantId=-1（废墟——实体保留）；
//    等级：Dwell（定居时长）× P 阈值纯函数（无 Rng，读档续跑无分叉）；都城（至尊酋长聚落）阈值减半；
//    收益：存储容量 ×(1+0.5×Level)（AccumulateStorage）、增长 ×(1+0.25×Level)（GrowthModel）。
// ══════════════════════════════════════════════════════════════════
public sealed class HabitationModel : CivModelBase
{
    public override string Name => "聚落";
    public override int Order => 48;

    protected override void Apply(CivSimContext ctx)
    {
        // ① 占据同步：已死/迁走部落释放聚落（废墟——场所比人长寿）
        for (int i = 0; i < ctx.Habitations.Count; i++)
        {
            var s = ctx.Habitations[i];
            if (s.OccupantId < 0) continue;
            var occ = FindPolity(ctx, s.OccupantId);
            if (occ == null || occ.Dead || occ.Cell != s.Cell || occ.PlaceId != s.Id)
            {
                if (occ != null && !occ.Dead && occ.Cell != s.Cell)
                {
                    // 部落迁走：清其聚落关联（SettledSince 随迁徙重置——新址重新定居）
                    occ.PlaceId = -1;
                    occ.SettledSince = -1;
                }
                s.OccupantId = -1;
                if (s.RuinFrom < 0) s.RuinFrom = ctx.Tick;
            }
        }
        // ② 形成/接管：农业部落无聚落 → 建新村/接管废墟
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead || !e.IsFarming || e.PlaceId >= 0) continue;
            if (e.SettledSince < 0) e.SettledSince = ctx.Tick;   // 定居起点（转农/迁入当 tick）
            Habitation reclaim = null;
            for (int k = 0; k < ctx.Habitations.Count; k++)
                if (ctx.Habitations[k].Cell == e.Cell && ctx.Habitations[k].IsRuin) { reclaim = ctx.Habitations[k]; break; }
            if (reclaim != null)
            {
                // 接管废墟：继承 Level（场所比人长寿）；粮仓清空（新占据者从零开始）
                reclaim.OccupantId = e.Id;
                reclaim.DwellFrom = ctx.Tick;
                reclaim.RuinFrom = -1;
                System.Array.Clear(reclaim.Stocks, 0, reclaim.Stocks.Length);
                e.PlaceId = reclaim.Id;
            }
            else
            {
                var s = new Habitation
                {
                    Id = ctx.NextHabitationId++,
                    Cell = e.Cell,
                    BornTick = ctx.Tick,
                    Level = 0,
                    LastLevelUpTick = ctx.Tick,
                    DwellFrom = ctx.Tick,
                    OccupantId = e.Id,
                };
                ctx.Habitations.Add(s);
                e.PlaceId = s.Id;
            }
        }
        // ③ 等级演化（Dwell×P 阈值 + 冷却；都城 = 至尊酋长聚落，阈值减半）
        for (int i = 0; i < ctx.Habitations.Count; i++)
        {
            var s = ctx.Habitations[i];
            if (s.OccupantId < 0) continue;
            var occ = FindPolity(ctx, s.OccupantId);
            if (occ == null || occ.Dead) continue;
            if (ctx.Tick - s.LastLevelUpTick < CivSimContext.SettlementLevelCooldown) continue;
            int dwell = ctx.Tick - s.DwellFrom;
            bool capital = occ.IsChief && occ.ChiefdomId == occ.Id;   // 至尊酋长（自己酋邦中心）聚落 = 都城
            int target = s.Level;
            if (dwell >= CivSimContext.SettlementLevelTicks1 && occ.P >= (capital ? CivSimContext.SettlementPop1 / 2f : CivSimContext.SettlementPop1)) target = Math.Max(target, 1);
            if (dwell >= CivSimContext.SettlementLevelTicks2 && occ.P >= (capital ? CivSimContext.SettlementPop2 / 2f : CivSimContext.SettlementPop2)) target = Math.Max(target, 2);
            if (dwell >= CivSimContext.SettlementLevelTicks3 && occ.P >= (capital ? CivSimContext.SettlementPop3 / 2f : CivSimContext.SettlementPop3)) target = Math.Max(target, 3);
            if (target > s.Level) { s.Level = target; s.LastLevelUpTick = ctx.Tick; }
        }
    }

    private static Polity FindPolity(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (ctx.Polities[i].Id == id && !ctx.Polities[i].Dead) return ctx.Polities[i];
        return null;
    }
}
