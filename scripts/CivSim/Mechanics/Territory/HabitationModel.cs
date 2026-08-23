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
// ①j 聚集地（Order 48，2026-08-19 阶段3 聚落设计；docs/阶段3设计-聚落实体.md；
//   ⚠️ 2026-08-23 功能定性重设计：Dwell×P 等级演化已删（用户拍板 D3）→ 职能条件系统）：
//    物理场所实体——农业部落（settle）的驻扎点固化；场所比人长寿。
//    形成：IsFarming 部落无聚落 → 所在格废墟接管/新建；
//    存续：部落迁徙/灭绝 → 聚落 OccupantId=-1（废墟——实体保留）；
//    条件：职能条件收集（用户拍板："有了什么条件才能做什么；集镇/城市也是一个条件"）——
//      HasAdmin（酋邦中心=城市）/HasRitual（宗教圣地=集镇级）/HasMarket（商路节点=集镇级，TradeModel 写）；
//    收益：存储容量 ×(1+0.5×TownTier)（AccumulateStorage）、增长 ×(1+0.25×TownTier)（GrowthModel）。
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
                // 接管废墟：占据恢复（场所比人长寿）；粮仓清空（新占据者从零开始）——2026-08-23 功能定性：无 Level，条件由新占据者重新涌现
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
                    DwellFrom = ctx.Tick,
                    OccupantId = e.Id,
                };
                ctx.Habitations.Add(s);
                e.PlaceId = s.Id;
            }
        }
        // ⚠️ ③ 职能条件收集（2026-08-23 功能定性重设计：旧 Dwell×P 升阶移除——用户拍板 D3）。
        //    村庄/集镇/城市 = 职能条件（用户拍板："有了什么条件才能做什么；集镇/城市也是一个条件"）：
        //    HasAdmin（治理）→ 城市；HasMarket（市场）/HasRitual（仪式）→ 集镇；无 → 村庄。
        //    条件 = 每 tick 派生缓存（确定性无 Rng；**入档**——Growth 等 Order 前机制滞后读需恢复，防分叉）。
        //    HasMarket 由 TradeModel（Order 55）扫描统计写入（晚于此机制）。
        for (int i = 0; i < ctx.Habitations.Count; i++)
        {
            var s = ctx.Habitations[i];
            if (s.OccupantId < 0) continue;
            var occ = FindPolity(ctx, s.OccupantId);
            if (occ == null || occ.Dead) continue;
            // 治理条件：酋邦中心（至尊酋长聚落——D2 拍板"历史上存在什么就是什么"：
            //   乌尔前期/杰里科酋长中心即城市雏形；国家都城也是酋邦中心——同一聚落）
            s.HasAdmin = occ.IsChief && occ.ChiefdomId == occ.Id;
            // 仪式条件：宗教多教汇聚（主导派别份额 < 阈值——圣地涌现；ShareField.DomFrac01 主导份额）
            s.HasRitual = ShareField.DomFrac01(occ.ReligionShare) < CivSimContext.RitualDominantFrac;
        }
    }

    private static Polity FindPolity(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (ctx.Polities[i].Id == id && !ctx.Polities[i].Dead) return ctx.Polities[i];
        return null;
    }
}
