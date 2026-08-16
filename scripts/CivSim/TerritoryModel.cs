// Responsibility: Territory cohesion (Order 45) - extracted from CivModels.cs verbatim (pure refactor).
using System;
using System.Collections.Generic;
using Godot;
using World.Biome;
using World.LogicGrid;

namespace World.CivSim;


// ══════════════════════════════════════════════════════════════════
// ⑤ 领地凝聚（Order 45）：band 凝聚体 = 连通分量（每 TerritoryRebuildEvery tick 重算）。
//    凝聚边 = 同格 band 对 或 邻格格代表对 + CultureGroupShare 主导 key 相同 + 双方存活。
//    分量标号 = 分量最小实体 Id（确定性：读档重建 → 续跑无分叉）。纯派生，不入档。
//    距离衰减 = 接触衰减：远格接触少 → 漂变分群 → 边断（零新常量，全部涌现）。
// ══════════════════════════════════════════════════════════════════
public sealed class TerritoryModel : CivModelBase
{
    public override string Name => "领地凝聚";
    public override int Order => 45;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.Tick - ctx.TerritoryLastRebuild < CivSimContext.TerritoryRebuildEvery) return;
        ctx.TerritoryLastRebuild = ctx.Tick;
        Rebuild(ctx);
    }

    /// <summary>重建全部实体领地（读档入口也调用——派生状态从存档确定性重算）。</summary>
    public static void Rebuild(CivSimContext ctx)
    {
        var parent = new Dictionary<int, int>();   // 实体 Id → 并查集父
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        foreach (var e in ctx.Tribes)
            if (!e.Dead) parent[e.Id] = e.Id;
        // 邻格凝聚边（一格一实体：无同格对）：相邻占据格的部落，同语言群 → 凝聚
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var ea = ctx.CellTribes[i];
            if (ea == null || ea.Dead) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var eb = ctx.CellTribes[nb];
                if (eb == null || eb.Dead) continue;
                if (ShareField.DomKey(ea.CultureGroupShare) == ShareField.DomKey(eb.CultureGroupShare))
                    Union(ea.Id, eb.Id);
            }
        }
        // 填分量：标号 = 分量最小实体 Id（确定性）；size = 分量实体数
        var sizes = new Dictionary<int, int>();
        var mins = new Dictionary<int, int>();
        foreach (var e in ctx.Tribes)
        {
            if (e.Dead) continue;
            int root = Find(e.Id);
            sizes[root] = sizes.TryGetValue(root, out var v) ? v + 1 : 1;
            if (!mins.TryGetValue(root, out var m) || e.Id < m) mins[root] = e.Id;
        }
        foreach (var e in ctx.Tribes)
        {
            if (e.Dead) continue;
            int root = Find(e.Id);
            e.TerritoryId = mins[root];
            e.TerritorySize = sizes[root];
        }
    }

    private static Tribe MaxPop(List<Tribe> list)
    {
        Tribe best = null;
        for (int k = 0; k < list.Count; k++)
            if (!list[k].Dead && (best == null || list[k].P > best.P)) best = list[k];
        return best;
    }
}
