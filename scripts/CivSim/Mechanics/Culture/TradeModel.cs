// 职责：Barter (Order 55)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Mechanics.Culture;
using World.CivSim.Mechanics.Military;
namespace World.CivSim.Mechanics.Culture;


// ══════════════════════════════════════════════════════════════════
// ⑳ 物物交换（Order 55，2026-08-18 阶段3 贸易期；docs/阶段3设计-贸易机制.md）：
//    Material 商品的**出口**——互通有无，为专业化/文明整合铺路。
//    触发：领地边界接触（TerritoryTouches 共享判定，同酋邦凝聚——用户拍板"接触即互通"）。
//    商品流（比较优势）：逐商品比**人均库存**（Stocks[i]/P——相对丰缺）——
//      你多我少才换（无货币 → 无单向贸易，双重巧合需求）；交换量 = TradeRate×人均差×min(P)×距离折减。
//    距离折减：边界格距 d → ×(1/(1+0.5d))（接触对 d=1 → ×0.667；黑曜石随距衰减史实）。
//    食物保底：Food 出口后出口方人均 ≥ TradeFoodFloor×P（5 年存粮——饥荒最后防线）。
//    确定性：无 Rng、固定对序（部落表序 i<j）、顺序应用、纯 Stocks 转移（v12 已入档）——
//      读档续跑无分叉（T04 保证）；SettleDerived 不碰（副作用同 AccumulateStorage 层语义）。
// ══════════════════════════════════════════════════════════════════
public sealed class TradeModel : CivModelBase
{
    public override string Name => "物物交换";
    public override int Order => 55;

    protected override void Apply(CivSimContext ctx)
    {
        // 市场条件统计（2026-08-23 功能定性：扫描时累计每实体的贸易伙伴数——商路节点 = 本 tick
        // 实际成交 ≥ MarketTradePartners 个伙伴；末尾统一写占据聚落 HasMarket，确定性无 Rng）。
        int maxId = ctx.NextPolityId;
        foreach (var pp in ctx.Polities) if (pp.Id > maxId) maxId = pp.Id;   // 测试构造可能 Id ≥ NextPolityId
        int[] tradePartners = new int[maxId + 1];
        // 空间预过滤：领地 = 驻扎点影响圈 R 内格——两领地可能接触仅当驻扎点距 ≤ 2R+1 格
        // （确定性：纯几何；把 O(对²×领地格) 降为 O(对²) 距离检查——全量演化性能防线）
        float reachKm = (2 * CivSimContext.InfluenceRadius + 1) * Mathf.Sqrt(ctx.Grid.CellAreaKm2);
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var a = ctx.Polities[i];
            if (a.Dead || a.P <= 0f) continue;
            EnsureStocks(a);
            for (int j = i + 1; j < ctx.Polities.Count; j++)
            {
                var b = ctx.Polities[j];
                if (b.Dead || b.P <= 0f) continue;
                EnsureStocks(b);
                if (ctx.Grid.DistKm(a.Cell, b.Cell) > reachKm) continue;   // 远隔两地无接触可能
                if (!CivSimContext.TerritoryTouches(ctx, a, b)) continue;   // 领地边界接触（同酋邦判定）
                // ⚠️ 2026-08-19 阶段5：交战国断交（WarTradeBlock——战争=外交状态，不只打仗还有经济封锁；
                //   朝贡期同样敌对；停战（War 移除）后恢复通商）
                if (a.StateId >= 0 && b.StateId >= 0 && WarModel.IsAtWar(ctx, a.StateId, b.StateId)) continue;
                int d = CivSimContext.BoundaryDist(ctx, a, b);
                float mult = 1f / (1f + CivSimContext.TradeDistanceRate * d);   // 运输成本（接触对 d=1 → ×0.667）
                if (mult <= 0f) continue;
                if (Exchange(ctx, a, b, mult) > 0f)
                {
                    tradePartners[a.Id]++;
                    tradePartners[b.Id]++;
                }
            }
        }
        // 市场条件写回（占据者的聚落——商路节点涌现；仅活跃占据）
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead || e.PlaceId < 0) continue;
            var st = ctx.HabitationOf(e);
            if (st != null && st.OccupantId == e.Id)
                st.HasMarket = tradePartners[e.Id] >= CivSimContext.MarketTradePartners;
        }
    }

    /// <summary>部落商品池 = 随身 + 占据聚落粮仓（2026-08-19 双池：正式存储归聚落——贸易互通含其仓）。</summary>
    private static float PoolOf(CivSimContext ctx, Polity e, int s)
    {
        float v = e.Stocks != null && s < e.Stocks.Length ? e.Stocks[s] : 0f;
        var st = ctx.HabitationOf(e);
        if (st != null && st.Stocks != null && s < st.Stocks.Length) v += st.Stocks[s];
        return v;
    }

    /// <summary>逐商品等量交换（固定商品序 = 目录序，确定性；跨商品天然成对——A 出 X、B 出 Y 即双重巧合）。
    /// 返回本对成交总量（&gt;0 = 贸易伙伴成立——商路节点统计）。</summary>
    private static float Exchange(CivSimContext ctx, Polity a, Polity b, float mult)
    {
        float total = 0f;
        for (int s = 0; s < CommodityTable.Count; s++)
        {
            float gap = PoolOf(ctx, a, s) / a.P - PoolOf(ctx, b, s) / b.P;   // A 人均 − B 人均（正 = A 盈余）
            if (Mathf.Abs(gap) < CivSimContext.TradeMinGap) continue;   // 需求匹配不足（无货币 → 无单向贸易）
            float amount = Mathf.Abs(gap) * CivSimContext.TradeRate * Mathf.Min(a.P, b.P) * mult;
            if (amount <= 0f) continue;
            if (gap > 0f) total += Transfer(ctx, a, b, s, amount);
            else total += Transfer(ctx, b, a, s, amount);
        }
        return total;
    }

    /// <summary>单商品转移 from → to（等量守恒；食物出口保底——出口后总池人均不低于 TradeFoodFloor×P；
    /// 出方：粮仓先出（卖存粮）→ 随身后出；入方：粮仓先收（定居）→ 随身（游群）。
    /// 演化级统计：TradeEvents/TradeVolume 累计——2026-08-19 贸易量级观测）。返回成交额（moved）。</summary>
    private static float Transfer(CivSimContext ctx, Polity from, Polity to, int s, float amount)
    {
        var def = CommodityTable.All[s];
        if (def.Kind == CommodityKind.Food)
            amount = Mathf.Min(amount, Mathf.Max(0f, PoolOf(ctx, from, s) - CivSimContext.TradeFoodFloor * from.P));   // 保底（5 年存粮）
        amount = Mathf.Min(amount, PoolOf(ctx, from, s));
        if (amount <= 0f) return 0f;
        // 出方：粮仓先出 → 随身后出
        float moved = 0f;
        var fs = ctx.HabitationOf(from);
        if (fs != null && fs.Stocks != null && s < fs.Stocks.Length && fs.Stocks[s] > 0f)
        {
            float take = Mathf.Min(amount, fs.Stocks[s]);
            fs.Stocks[s] -= take;
            moved += take;
        }
        if (amount - moved > 0f)
        {
            float take = Mathf.Min(amount - moved, from.Stocks[s]);
            from.Stocks[s] -= take;
            moved += take;
        }
        if (moved <= 0f) return 0f;
        // 入方：粮仓先收（定居）→ 随身（游群；不查上限——AccumulateStorage 下 tick clamp）
        var ts = ctx.HabitationOf(to);
        if (ts != null && ts.Stocks != null && s < ts.Stocks.Length)
            ts.Stocks[s] += moved;
        else
            to.Stocks[s] += moved;
        ctx.TradeVolume += moved;
        ctx.TradeEvents++;
        return moved;
    }

    private static void EnsureStocks(Polity e)
    {
        if (e.Stocks == null || e.Stocks.Length != CommodityTable.Count) e.Stocks = CommodityTable.NewStocks();
    }
}
