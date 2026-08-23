// 职责：Population growth (Order 20)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Mechanics.Society;
namespace World.CivSim.Mechanics.Society;


// ══════════════════════════════════════════════════════════════════
// ③ 人口增长（Order 20）：P_i ×= exp(r_eff·(1 − D_i/F_i))，r_eff=0.5/tick ★
//    D_i = P_i×c（c=1）；F_i = 部落当 tick 实际产出（两层模型 2026-08-17：按部落，不共享格因子）。
//    F_i < D_i → 负增长 = 饿死人（用户拍板 2026-08-06）；P<1 灭绝。
// ══════════════════════════════════════════════════════════════════
public sealed class GrowthModel : CivModelBase
{
    public override string Name => "人口增长";
    public override int Order => 20;

    protected override void Apply(CivSimContext ctx)
    {
        float r = ctx.TickFactor;   // 0.5/tick
        for (int i = 0; i < ctx.Bands.Count; i++)
        {
            var e = ctx.Bands[i];
            if (e.Dead) continue;
            float f = e.FLast;   // 当 tick 实际产出（RefreshCellState 已算，农业含劳动因子；寒冷区含下限）
            // ⚠️ 2026-08-18 阶段3 存储机制：有效粮食 = 当年产出 + Food 存储缓冲（AccumulateStorage 已做衰变/容量）。
            //   缺口（FLast<P）：从 Food 存储扣，**优先吃易腐（高衰变：浆果/肉），耐储者（谷物）留底**——
            //   这是"特定食物耐储"的机制意义（谷物是饥荒最后防线，新石器革命核心）。
            //   盈余（FLast>P）：按容量入仓（随身 0.06×P → 粮仓 0.5×P×等级倍率）。
            //   饥荒 = 连续歉年吃空存粮 → 缺口扩大 → 饿死（非硬标志）。
            //   ⚠️ 2026-08-19 聚落双池：缺口**先吃随身、再吃粮仓**（粮仓=耐储最后防线——人先耗行囊）；
            //   盈余**随身先满、粮仓后收**（正式存储归聚落，用户拍板）。
            var st = ctx.SettlementOf(e);   // 粮仓（定居部落；null=游群——随身即全部）
            if (e.Stocks != null && e.Stocks.Length == CommodityTable.Count)
            {
                if (f < e.P)
                {
                    float deficit = e.P - f;
                    var foodIdx = FoodIdxByDecayDesc();
                    foreach (int s in foodIdx)
                    {
                        if (deficit <= 0f) break;
                        float take = Mathf.Min(deficit, e.Stocks[s]);
                        e.Stocks[s] -= take;
                        deficit -= take;
                    }
                    if (st != null)
                    {
                        foreach (int s in foodIdx)
                        {
                            if (deficit <= 0f) break;
                            float take = Mathf.Min(deficit, st.Stocks[s]);
                            st.Stocks[s] -= take;
                            deficit -= take;
                        }
                    }
                    f += e.P - f - deficit;   // 存储补足缺口（不足则 f 仍 < P）
                }
                else if (f > e.P)
                {
                    // 盈余入仓：随身谷物（cap CarryFoodCap）→ 粮仓谷物（cap SettleFoodCap×等级倍率）
                    int gi = CommodityTable.Index(CommodityTable.Grain);
                    float surplus = f - e.P;
                    float carryRoom = Mathf.Max(0f, CivSimContext.CarryFoodCap * e.P - e.Stocks[gi]);
                    float toCarry = Mathf.Min(surplus, carryRoom);
                    e.Stocks[gi] += toCarry;
                    surplus -= toCarry;
                    if (st != null && surplus > 0f)
                    {
                        float granCap = CivSimContext.SettleFoodCap * (1f + CivSimContext.SettlementStoragePerLevel * st.Level) * e.P;
                        st.Stocks[gi] += Mathf.Min(surplus, Mathf.Max(0f, granCap - st.Stocks[gi]));
                    }
                }
            }
            if (f <= 0f) continue;
            // ⚠️ 2026-08-17 定居生育跃迁（史实：定居 → 生育间隔缩短/婴儿存活率↑，人口密度 10-50× 游群）
            float rEff = r;
            if (CapabilityTable.Has(ctx, e, CapabilityTable.Settle)) rEff *= CivSimContext.SettleGrowthMult;   // 1.5
            // ⚠️ 2026-08-19 聚落城市化集聚：占据高等级聚落 → 增长加成（城镇 ×1.25、城市 ×1.5——集聚收益）
            if (st != null && st.Level > 0)
                rEff *= 1f + CivSimContext.SettlementGrowthPerLevel * st.Level;
            float factor = Mathf.Exp(rEff * (1f - e.P / f));
            // 酋邦再分配互惠（2026-08-17：Halstead-O'Shea 1989 坏年景开仓——贡献过才受赈）：
            //   成员 band 曾交贡赋（Contributed>0）→ 灾年缺口 ×0.5（酋长开仓）；未贡献不受赈
            if (factor < 1f && e.ChiefdomId >= 0 && e.Contributed > 0f)
                factor = 1f + (factor - 1f) * CivSimContext.TributeRelief;
            e.P *= factor;
            if (e.P < 1f) { e.P = 0f; e.Dead = true; }   // 饿死灭绝
        }
    }

    /// <summary>Food 类商品索引，按衰变率**降序**（易腐先吃：浆果/肉 → 谷物留底）。
    /// 静态缓存（目录固定）；确定性（同目录同序）。</summary>
    private static int[] _foodIdxByDecay;
    private static int[] FoodIdxByDecayDesc()
    {
        if (_foodIdxByDecay != null) return _foodIdxByDecay;
        var list = new List<int>();
        for (int s = 0; s < CommodityTable.Count; s++)
            if (CommodityTable.All[s].Kind == CommodityKind.Food) list.Add(s);
        list.Sort((a, b) => CommodityTable.All[b].BaseDecay.CompareTo(CommodityTable.All[a].BaseDecay));
        _foodIdxByDecay = list.ToArray();
        return _foodIdxByDecay;
    }
}
