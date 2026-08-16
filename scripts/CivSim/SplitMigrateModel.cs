// Responsibility: Split/migrate (Order 80) - extracted from CivModels.cs verbatim (pure refactor).
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

namespace World.CivSim;


// ══════════════════════════════════════════════════════════════════
// ⑨ 分裂/迁徙（Order 80）：segmentary lineage + 饱和/探路迁徙。
//    分裂：P>400 → 45% 带走，份额等比例继承，TechKeys 完整，0.5% 新文化群；格上限 8。
//    迁徙：饱和 0.75K（50%）；探路 2%/tick（30%，1-3 跳最高 K 无人格）；跨海需 canoe；
//          目标全失败 → 留在原地（格人口终填满 → 压力必达，用户拍板）。
// ══════════════════════════════════════════════════════════════════
public sealed class SplitMigrateModel : CivModelBase
{
    public override string Name => "分裂迁移";
    public override int Order => 80;

    public override void Execute(CivSimContext ctx)
    {
        ctx.EnsureTerritory();
        // ── 分裂（2026-08-10 殖民式：快照遍历，新实体下 tick 再判，防同 tick 连锁）──
        //    母 band 人口超载（裂变压力）→ 45% 分群**殖民**影响圈外 1-3 跳最高富饶无主地；
        //    母领地完全不动（承载不变 → P 减半 → 盈余再长 → 周期分裂）；扩散=殖民推进。
        //    无目标（无主地耗尽）→ 不分裂（饱和态：P 继续涨 → 竞争/饿死路径）。
        var snapshot = ctx.Tribes.ToArray();
        foreach (var t in snapshot)
        {
            if (t.Dead) continue;
            if (t.LastSplitTick >= 0 && ctx.Tick - t.LastSplitTick < CivSimContext.SplitCooldown) continue;
            // 裂变压力（2026-08-09 用户拍板：资源压力+内部张力涌现，替代纯 P>SplitPop）：
            float tension = Mathf.Clamp((t.P - CivSimContext.FissionTensionStart) / CivSimContext.FissionTensionSpan, 0f, 1f);
            float pEff = t.P * (1f + Mathf.Max(0f, 1f - t.FLast / t.P) + tension);
            if (pEff <= CivSimContext.SplitPop) continue;
            int target = PickMigrateTarget(ctx, t);   // 殖民目标：影响圈外 1-3 跳最高 R 无主陆地格
            if (target < 0) continue;                 // 无主地耗尽 → 不分裂
            float newPop = t.P * CivSimContext.SplitShare;
            t.P -= newPop;
            t.LastSplitTick = ctx.Tick;
            var nt = new Tribe
            {
                Id = ctx.NextTribeId++,   // 独立计数器（2026-08-10）
                Cell = target,
                P = newPop,
                IsFarming = t.IsFarming,
                TechKeys = new HashSet<string>(t.TechKeys),     // 分裂瞬间技术相同，此后各自发明/学习
                CultureShare = ShareField.CloneShare(t.CultureShare),          // 份额等比例继承
                CultureGroupShare = ShareField.CloneShare(t.CultureGroupShare),
                ReligionShare = (ShareEntry[])t.ReligionShare.Clone(),
                ReligionCultShare = ShareField.CloneShare(t.ReligionCultShare),   // 派别随人口走
                OriginCell = t.Cell,
                BornTick = ctx.Tick,
                LastMigrateTick = ctx.Tick,
                LastSplitTick = ctx.Tick,
            };
            // 文化标签分化：5% 新 key（习俗漂变——方言分化伴习俗变，独立于文化群判定）
            if (ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance)
                nt.CultureShare[0] = new ShareEntry { Key = ctx.NextCultureKey(), Frac = nt.CultureShare[0].Frac };
            // 文化群分化：5% 新 key（方言→语言群漂变），独立计数
            if (ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance)
                nt.CultureGroupShare[0] = new ShareEntry { Key = ctx.NextCultureGroupKey(), Frac = nt.CultureGroupShare[0].Frac };
            // 宗教派别分化：2% 新 key（图腾漂变）
            if (ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance)
                nt.ReligionCultShare[0] = new ShareEntry { Key = ctx.NextReligionKey(), Frac = nt.ReligionCultShare[0].Frac };
            ctx.Tribes.Add(nt);
            ctx.CellTribes[target] = nt;   // 一格一实体：分裂殖民到空格
            ctx.Fissions++;
        }

        // ── 饥饿迁移（2026-08-10 影响力场模型）：饿（F<D）→ 驻扎点搬家到 1-3 跳内最高富饶度无主格。
        //    落脚必须无主（CellOwner==-1——有主格禁入，冲突未实现）；旧领地格下 tick 场重算自动废弃。
        //    冷却 MigrateCooldown tick 防抖动（连续饿会再次触发——游走 band 觅食迁徙）。
        var snap2 = ctx.Tribes.ToArray();
        foreach (var t in snap2)
        {
            if (t.Dead) continue;
            if (t.LastMigrateTick >= 0 && ctx.Tick - t.LastMigrateTick < CivSimContext.MigrateCooldown) continue;
            if (!ctx.IsStarving(t)) continue;
            int target = PickMigrateTarget(ctx, t);
            if (target < 0) continue;   // 无处可去（全被占）→ 饿死路径（GrowthModel）
            if (ctx.CellTribes[t.Cell] == t) ctx.CellTribes[t.Cell] = null;
            t.Cell = target;
            t.LastMigrateTick = ctx.Tick;
            ctx.CellTribes[target] = t;
            ctx.Migrations++;
        }
    }

    /// <summary>迁徙/殖民目标：起始格 BFS 至多 ColonizeRadius 跳；可穿过任意可穿越陆地（BorderCost>0）
    /// 寻找**未定居**目标（CellTribes==null，即格上无实体；CellOwner 影响力场会广泛圈地，不做定居判定）。
    /// 按 (R × 路径 BorderCost 衰减) 择优。确定性：时间戳标记 + 固定遍历顺序。
    /// 阶段2：原 1-3 跳手写展开致"想分裂却无目标" → 改 BFS 到 6 跳。</summary>
    internal static int PickMigrateTarget(CivSimContext ctx, Tribe mover)
    {
        var grid = ctx.Grid;
        var keys = mover.TechKeys;
        int best = -1; float bestScore = -1f;
        int maxLayer = CivSimContext.ColonizeRadius;
        // 复用 BfsStamp 作"本 tick 已访问"标记（stamp 值递增，区分 tick）
        int stamp = ++ctx.BfsStampValue;
        var q = new Queue<(int cell, int layer, float cost)>();
        foreach (int nb in grid.Neighbors[mover.Cell])
        {
            if (!grid.IsLandCell(nb) || ctx.R[nb] <= 0f) continue;
            float c1 = ctx.BorderCost(mover.Cell, nb, keys);
            if (c1 <= 0f) continue;
            ctx.BfsStamp[nb] = stamp;
            q.Enqueue((nb, 1, c1));
        }
        while (q.Count > 0)
        {
            var (c, layer, ccost) = q.Dequeue();
            // 目标 = 可穿越且未定居（CellTribes==null）；CellOwner 影响力圈地不算定居
            if (ctx.CellTribes[c] == null)
            {
                float s = ctx.R[c] * ccost;
                if (s > bestScore) { bestScore = s; best = c; }
            }
            if (layer >= maxLayer) continue;   // 达最大跳数不再扩展
            foreach (int nb in grid.Neighbors[c])
            {
                if (!grid.IsLandCell(nb) || ctx.R[nb] <= 0f) continue;
                if (ctx.BfsStamp[nb] == stamp) continue;
                float c2 = ctx.BorderCost(c, nb, keys);
                if (c2 <= 0f) continue;
                ctx.BfsStamp[nb] = stamp;
                q.Enqueue((nb, layer + 1, ccost * c2));
            }
        }
        return best;
    }
}
