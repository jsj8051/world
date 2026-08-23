// 职责：Culture interaction (Order 60)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
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
// ⑦ 文化互动（Order 60）：格级聚合-演化-分摊（不分部落，用户拍板）+ 相邻格 Axelrod。
//    同化：主导 x' = x + 0.3(1−x)；文化群：Abrams-Strogatz 竞争（慢）。
// ══════════════════════════════════════════════════════════════════
public sealed class CultureModel : CivModelBase
{
    public override string Name => "文化互动";
    public override int Order => 60;

    protected override void Apply(CivSimContext ctx)
    {
        // ── 相邻格：Axelrod 相似度互动（一格一实体：无同格聚合，只做邻格互动）──
        // ⚠️ 2026-08-19 修复（死代码）：旧版 sim = 同文化0.5+同群0.5，门槛 sim<=0.5 continue + rate=sim−0.5——
        //   唯一有传播意义的组合（同语言群、异文化）恰好 sim=0.5 被门槛挡死且 rate=0 → 文化永不混合
        //   （实测 60 tick 零传播）→ 单一 lineage 靠分裂无限扩张 → 地图大片单色。
        //   新语义（Axelrod）：**同语言群、异文化**的相邻部落互动（语言群=沟通能力——同群能交流才传文化，
        //   异群保持边界分界）；弱方（P 小）主导文化向强方主导文化转移（速率 CultureSpreadRate×BorderCost）。
        //   同文化对跳过（无转移语义——旧版同 key 自转移还瞬态污染份额场：次席重复 key）。
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellBands[i];
            if (a == null || a.Dead) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var b = ctx.CellBands[nb];
                if (b == null || b.Dead) continue;
                string domA = ShareField.DomKey(a.CultureShare);
                string domB = ShareField.DomKey(b.CultureShare);
                if (domA == null || domB == null || domA == domB) continue;   // 无文化/已同 → 无转移
                string grpA = ShareField.DomKey(a.CultureGroupShare);
                string grpB = ShareField.DomKey(b.CultureGroupShare);
                if (grpA == null || grpB == null || grpA != grpB) continue;   // 异语言群不传（边界文化分界）
                float cost = ctx.BorderCost(i, nb, a.TechKeys);
                if (cost <= 0f) continue;   // 闭塞区域：跨格文化转移 ×= BorderCost（障碍区交流弱 → 边界差异保持）
                var strong = a.P >= b.P ? a : b;
                var weak = strong == a ? b : a;
                string strongDom = ShareField.DomKey(strong.CultureShare);
                string weakDom = ShareField.DomKey(weak.CultureShare);
                int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.CultureSpreadRate * cost);
                if (amt <= 0) continue;
                ShareField.Shift(weak.CultureShare, weakDom, strongDom, amt);   // 弱方文化向强方文化转移
            }
        }
    }

    /// <summary>Abrams-Strogatz 份额竞争一步（dx/dt = (1−x)s·x^a − x(1−s)(1−x)^a，a=1.31）。</summary>
    private static void StepAbramsStrogatz(ShareEntry[] g)
    {
        const float a = 1.31f;
        float x = ShareField.DomFrac01(g);          // 主导群份额
        float s = x;                                // 地位 = 人口占比 = 份额
        if (x <= 0f || x >= 1f) return;
        float dx = (1f - x) * s * Mathf.Pow(x, a) - x * (1f - s) * Mathf.Pow(1f - x, a);
        int d = (int)MathF.Round(dx * 255f);
        if (d == 0) return;
        int sec = g[1].Frac;
        if (d > 0)
        {
            int take = Mathf.Min(d, sec);
            g[0].Frac = (byte)Mathf.Min(255, g[0].Frac + take);
            g[1].Frac = (byte)Mathf.Max(0, g[1].Frac - take);
        }
        else
        {
            int take = Mathf.Min(-d, g[0].Frac);
            g[0].Frac = (byte)Mathf.Max(0, g[0].Frac - take);
            g[1].Frac = (byte)Mathf.Min(255, g[1].Frac + take);
            if (g[0].Frac == 0) (g[0], g[1]) = (g[1], g[0]);   // 主导被反超 → 交换
        }
        if (g[0].Frac == 255) g[1] = new ShareEntry();   // 全占 → 清第二位
    }

    private static Band MaxPop(List<Band> list)
    {
        var best = list[0];
        for (int k = 1; k < list.Count; k++)
            if (!list[k].Dead && list[k].P > best.P) best = list[k];
        return best;
    }
}
