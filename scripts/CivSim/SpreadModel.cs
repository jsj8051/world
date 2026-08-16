// Responsibility: Tech spread (Order 50) - extracted from CivModels.cs verbatim (pure refactor).
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

namespace World.CivSim;


// ══════════════════════════════════════════════════════════════════
// ⑥ 科技传播（Order 50）：同格实体对 + 邻格边界（代表实体对）。
//    p = SpreadBase × 种子修正（clamp(φ, 0.3, 1.0)）；依赖缺失不传；Rogers S 自然涌现。
// ══════════════════════════════════════════════════════════════════
public sealed class SpreadModel : CivModelBase
{
    public override string Name => "科技传播";
    public override int Order => 50;

    public override void Execute(CivSimContext ctx)
    {
        // ── 一格一实体：传播只在"相邻有部落的格"之间（占据格彼此球面相邻 → 领地接触）。
        //   不跨空格传播（邻近不行），无同格对（一格一实体）。──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellTribes[i];
            if (a == null || a.Dead) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var b = ctx.CellTribes[nb];
                if (b == null || b.Dead) continue;
                // 闭塞区域：跨格传播 ×= BorderCost（地形障碍 × 气候相似度；A→B 用 A 的科技判定障碍突破）
                float cost = ctx.BorderCost(i, nb, a.TechKeys);
                if (cost <= 0f) continue;
                SpreadTech(ctx, a, b, cost);
                SpreadTech(ctx, b, a, cost);
            }
        }
    }

    private static Tribe MaxPop(List<Tribe> list)
    {
        var best = list[0];
        for (int k = 1; k < list.Count; k++)
            if (!list[k].Dead && list[k].P > best.P) best = list[k];
        return best;
    }

    /// <summary>领地传播乘数：同领地 ×1.5（整合加成）；至少一方是正式领地（≥2 band）→ ×0.5（跨边界软冲突）；散兵部落间 ×1（BorderCost 已有）。</summary>
    internal static float TerritoryMult(Tribe a, Tribe b)
    {
        if (a.TerritoryId >= 0 && a.TerritoryId == b.TerritoryId) return CivSimContext.TerritorySpreadMult;
        if (a.TerritorySize >= 2 || b.TerritorySize >= 2) return CivSimContext.CrossBorderSpreadMult;
        return 1f;
    }

    /// <summary>技术传播 from → to（to 缺 from 的技术且依赖满足 → 按概率获得）。
    /// ⚠️ 2026-08-10 确定性修复：HashSet 遍历顺序依赖构建历史（读档重建 Add 顺序 ≠ 演化布局）→
    ///    同 Rng 数对应不同 key → 读档续跑分叉。改为**排序遍历**（与布局无关，ctx 缓冲无分配）。</summary>
    private void SpreadTech(CivSimContext ctx, Tribe from, Tribe to, float border = 1f)
    {
        float terr = TerritoryMult(from, to);   // 领地乘数（同领地×1.5 / 跨领地×0.5 / 散兵×1）
        int nKeys = from.TechKeys.Count;
        if (nKeys == 0) return;
        var keys = ctx.KeyBuf;
        if (keys == null || keys.Length < nKeys) ctx.KeyBuf = keys = new string[Math.Max(16, nKeys * 2)];
        from.TechKeys.CopyTo(keys, 0);
        Array.Sort(keys, 0, nKeys, StringComparer.Ordinal);   // 确定性顺序（HashSet 布局无关）
        for (int ki = 0; ki < nKeys; ki++)
        {
            var key = keys[ki];
            if (to.TechKeys.Contains(key)) continue;
            var t = TechTable.Get(key);
            if (t == null || t.IsAgricultureConcept) continue;
            if (!HasAll(to.TechKeys, t.Requires)) continue;   // 依赖硬门槛
            float p = t.SpreadBase * border * terr;
            if (t.IsSeed)
                p *= Mathf.Clamp(ctx.Phi(to.Cell, t.SeedIndex), 0.3f, 1f);   // 种子传播修正
            if (ctx.Rng.NextDouble() < Mathf.Min(0.5f, p))
            {
                to.TechKeys.Add(t.Key);
                TechTable.SyncAgriculture(to.TechKeys);
            }
        }
    }

    private static bool HasAll(HashSet<string> keys, string[] req)
    {
        foreach (var r in req) if (!keys.Contains(r)) return false;
        return true;
    }
}
