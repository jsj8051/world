// 职责：Religion evolution (Order 70)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Mechanics.Culture;
namespace World.CivSim.Mechanics.Culture;


// ══════════════════════════════════════════════════════════════════
// ⑧ 宗教演进（Order 70）：份额场升级/传播/同化（不读时代 ★）。
//    泛灵→萨满：盈余 s>0 + 细石器；萨满→祖先：农业+定居（旧石器天然锁死）。
//    无农业 → 宗教停在泛灵/萨满（旧石器晚期洞穴壁画 = 萨满图腾，史实吻合）。
// ══════════════════════════════════════════════════════════════════
public sealed class ReligionModel : CivModelBase
{
    public override string Name => "宗教演进";
    public override int Order => 70;

    protected override void Apply(CivSimContext ctx)
    {
        // ── 升级（实体，份额转移 0.05/tick）──
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            // 泛灵 → 萨满：盈余 s>0 + 细石器
            if (e.Surplus > 0f && CapabilityTable.Has(ctx, e, CapabilityTable.Microlith))
            {
                int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionUpgradeRate);
                ShareField.RelTransfer(e.ReligionShare, ReligionStage.Animism, ReligionStage.Shaman, amt);
            }
            // 萨满 → 祖先：定居（=农业派生能力，2026-08-17 落地"定居+存储"缺口——
            //   谷物农业守田定居 → 祖先崇拜；旧石器无农 → settle 能力天然锁死）
            if (e.Surplus > 0f && CapabilityTable.Has(ctx, e, CapabilityTable.Settle))
            {
                int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionUpgradeRate);
                ShareField.RelTransfer(e.ReligionShare, ReligionStage.Shaman, ReligionStage.Ancestor, amt);
            }
            // 祖先 → 多神 / 多神 → 一神：后续阶段
        }

        // ── 传播（接触，0.02/tick 只向更高阶段；一格一实体：仅相邻占据格之间）──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellPolities[i];
            if (a == null || a.Dead) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var b = ctx.CellPolities[nb];
                if (b == null || b.Dead) continue;
                // 闭塞区域：跨格宗教传播 ×= BorderCost（障碍区传教弱）
                float cost = ctx.BorderCost(i, nb, a.TechKeys);
                if (cost <= 0f) continue;
                SpreadReligion(ctx, a, b, cost);
                SpreadReligion(ctx, b, a, cost);
                // ⚠️ 2026-08-19 修复：宗教图层显示 relig_N **派别**——旧版只传 5 段（泛灵→…），
                //   派别只靠分裂继承 → 不横向混合 → 大片单色（与文化传播同根因）。派别随接触转移
                //   （弱方派别向强方派别转移，速率同 5 段传播 ReligionSpreadRate×BorderCost）。
                SpreadSect(ctx, a, b, cost);
            }
        }

        // ── 一格一实体：格级宗教同化已无意义（单部落/格），删除——宗教仅靠实体级升级 + 邻格传播
    }

    /// <summary>宗教派别（relig_N）横向传播：相邻部落弱方（P 小）派别向强方派别转移。
    /// 无 Rng、固定遍历序 + P 比较 → 确定性（读档续跑无分叉）。</summary>
    private static void SpreadSect(CivSimContext ctx, Polity a, Polity b, float border)
    {
        var strong = a.P >= b.P ? a : b;
        var weak = strong == a ? b : a;
        string strongSect = ShareField.DomKey(strong.ReligionCultShare);
        string weakSect = ShareField.DomKey(weak.ReligionCultShare);
        if (strongSect == null || weakSect == null || strongSect == weakSect) return;   // 无派别/已同 → 无转移
        int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionSpreadRate * border);
        if (amt <= 0) return;
        ShareField.Shift(weak.ReligionCultShare, weakSect, strongSect, amt);
    }

    /// <summary>宗教传播：高阶实体主导宗教份额流向低阶实体（只向更高阶段）。</summary>
    private static void SpreadReligion(CivSimContext ctx, Polity from, Polity to, float border = 1f)
    {
        string domFrom = ShareField.DomReligion(from.ReligionShare);
        string domTo = ShareField.DomReligion(to.ReligionShare);
        int fi = ShareField.ReligionIndex(domFrom);
        int ti = ShareField.ReligionIndex(domTo);
        if (fi <= ti) return;   // 只向更高阶段（不回头污染）
        int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionSpreadRate * border);
        ShareField.RelTransfer(to.ReligionShare, domTo, domFrom, amt);
    }

    private static Polity MaxPop(List<Polity> list)
    {
        var best = list[0];
        for (int k = 1; k < list.Count; k++)
            if (!list[k].Dead && list[k].P > best.P) best = list[k];
        return best;
    }
}
