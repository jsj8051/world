// 职责：Prestige (Order 25)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Policies;
using World.CivSim.Mechanics.Politics;
namespace World.CivSim.Mechanics.Politics;

//   盈余 → 宴席（feasting）→ 声望（Sahlins 1963 Big Man：慷慨积累欠人情网络，个人化、可逆）。
//   BigMan = 声望阈值；酋长 Chief = BigMan + 祖先宗教（Polynesia 谱系合法性——divine kingship，
//   祖先崇拜提供谱系，Kirch 1984）。
//   贡赋流入（Earle 1997 实物税）：酋邦成员盈余 → Contributed 累计（互惠记录——灾年开仓资格）。
//   精英供养（等级结构）：酋长 band 的非生产者比例（EliteFrac）由酋邦贡赋供养——
//   贡赋不足 → 精英饿死（P 降）——等级 = 结构性供养，非盈余自动（回应"盈余>0≠等级"）。
// ══════════════════════════════════════════════════════════════════
public sealed class PrestigeModel : CivModelBase
{
    public override string Name => "声望积累";
    public override int Order => 25;

    protected override void Apply(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            float surplus = e.FLast - e.P;   // 实际盈余（人当量；FLast 由 Harvest/RefreshCellState 已算）
            if (surplus > 0f && e.P > 0f)
                e.Prestige += surplus * CivSimContext.PrestigeGainRate;   // **绝对盈余**×rate（宴席=绝对食物量，Sahlins）
            else
                e.Prestige = Mathf.Max(0f, e.Prestige - CivSimContext.PrestigeDecay);   // 可逆（个人化）
            // ⚠️ 2026-08-18 阶段3：领袖标记走共享派生函数（与 SettleDerived 同式）——无两套实现分叉
            CivEngine.DeriveLeadership(e);
            // 贡赋流入（互惠记录——Earle 实物税）：成员盈余 → 酋邦贡赋累计
            // ⚠️ 2026-08-16 阶段4 税制化：国家成员税率 ×2（StateTributeRate）——税 vs 互惠贡赋。
            //   （滞后 1 tick 读 StateId：SettleDerived 重建值 ≡ 演化末值 → 读档续跑无分叉，T04）
            //   对象差异走策略多态（TributePolicies.Of 查表——无身份 if-else，2026-08-23）
            if (e.ChiefdomId >= 0 && surplus > 0f)
                e.Contributed += surplus * TributePolicies.Of(e).TributeRate;
            // 精英供养（等级结构）：酋长 band 非生产者（祭司/战士/亲信）由酋邦贡赋供养
            // ⚠️ 2026-08-16 阶段4 官僚化：国家酋长精英比例 ×2.5（StateEliteFrac）——官僚体系更庞大
            if (e.IsChief && e.P > 0f)
            {
                float elite = e.P * TributePolicies.Of(e).EliteFrac;
                float pool = TributePool(ctx, e);
                if (pool >= elite)
                    ConsumeTribute(ctx, e, elite);
                else
                    e.P = Mathf.Max(1f, e.P - (elite - pool) * 0.5f);   // 贡赋不足 → 精英饿死
            }
        }
    }

    /// <summary>酋邦贡赋池 = Σ成员 Contributed（按 ChiefdomId；滞后酋邦状态可接受——派生同 Territory 模式）。</summary>
    private static float TributePool(CivSimContext ctx, Polity chief)
    {
        if (chief.ChiefdomId < 0) return 0f;
        float sum = 0f;
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var m = ctx.Polities[i];
            if (!m.Dead && m.ChiefdomId == chief.ChiefdomId) sum += m.Contributed;
        }
        return sum;
    }

    /// <summary>消耗贡赋（按成员贡献比例扣减——实物税从贡献者处收取）。</summary>
    private static void ConsumeTribute(CivSimContext ctx, Polity chief, float amount)
    {
        float remaining = amount;
        for (int i = 0; i < ctx.Polities.Count && remaining > 0f; i++)
        {
            var m = ctx.Polities[i];
            if (m.Dead || m.ChiefdomId != chief.ChiefdomId || m.Contributed <= 0f) continue;
            float take = Mathf.Min(remaining, m.Contributed);
            m.Contributed -= take;
            remaining -= take;
        }
    }
}
