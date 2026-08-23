// 职责：Origin seeding (Order 0)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
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
// ① 起源播种（Order 0）：富饶区池内随机 + 格距 ≥12 + 不同大陆优先。
//    每摇篮独立文化/文化群（互不同源）；P=100，自带 stone_core。
// ══════════════════════════════════════════════════════════════════
public sealed class OriginModel : CivModelBase
{
    public override string Name => "起源播种";
    public override int Order => 0;

    protected override void Apply(CivSimContext ctx)
    {
        if (ctx.Tick > 0) return;
        var grid = ctx.Grid;
        int n = grid.N;

        // ── 富饶区：陆地 ∩ R>0，按 R 降序前 30% ──
        var land = new List<int>();
        for (int i = 0; i < n; i++)
            if (grid.IsLandCell(i) && ctx.R[i] > 0f)
                land.Add(i);
        if (land.Count == 0) return;
        land.Sort((a, b) => ctx.R[b].CompareTo(ctx.R[a]));
        int rich = Mathf.Max(8, land.Count * 30 / 100);
        var pool = land.GetRange(0, Mathf.Min(rich, land.Count));

        // ── 大陆连通分量（BFS 陆地；-1=海洋）──
        int[] continent = ComputeContinents(grid, n);

        // ── 贪心选格：优先"已选起源数最少的大陆"，大陆内随机；格距 ≥ OriginDistMin ──
        float minDistKm = CivSimContext.OriginDistMin * Mathf.Sqrt(grid.CellAreaKm2);   // 12 格 × 平均格距
        int count = Mathf.Min(ctx.OriginCount, pool.Count);
        var chosen = new List<int>();
        var contCount = new Dictionary<int, int>();
        for (int k = 0; k < count; k++)
        {
            // 候选 = 池内、空格（一格一实体）、且距已选 ≥ 阈值
            var cands = new List<int>();
            foreach (int c in pool)
            {
                bool occupied = ctx.CellBands != null && ctx.CellBands[c] != null;
                if (occupied) continue;   // 一格一实体：起源只能选空格
                bool ok = true;
                foreach (int p in chosen)
                    if (grid.DistKm(c, p) < minDistKm) { ok = false; break; }
                if (ok) cands.Add(c);
            }
            if (cands.Count == 0) break;
            // 优先未占大陆：取"大陆上已选起源数最少"的候选组（确定性分组，组内随机抽取；
            // ⚠️ 不能用随机 tie-break 排序——比较器必须一致，否则 ArraySortHelper 抛异常）
            int minCount = int.MaxValue;
            foreach (int c in cands)
            {
                int cc = contCount.TryGetValue(continent[c], out var vc) ? vc : 0;
                if (cc < minCount) minCount = cc;
            }
            var minCands = new List<int>();
            foreach (int c in cands)
            {
                int cc = contCount.TryGetValue(continent[c], out var vc) ? vc : 0;
                if (cc == minCount) minCands.Add(c);
            }
            int pick = minCands[ctx.Rng.Next(minCands.Count)];
            chosen.Add(pick);
            contCount[continent[pick]] = contCount.TryGetValue(continent[pick], out var v) ? v + 1 : 1;
        }

        foreach (int pick in chosen)
        {
            string key = ctx.NextCultureKey();   // 每摇篮独立文化/文化群 key（互不同源）
            string relKey = ctx.NextReligionKey();   // 每摇篮独立宗教派别（图腾体系互不同源）
            var e = new Band
            {
                Id = ctx.NextBandId++,   // 独立计数器（2026-08-10：Bands.Count 读档后分叉）
                Cell = pick,
                P = CivSimContext.OriginPop,
                OriginCell = pick,
                BornTick = 0,
                CultureShare = ShareField.NewCulture(key),
                CultureGroupShare = ShareField.NewCulture(key),
                ReligionShare = ShareField.NewReligion(ReligionStage.Animism),
                ReligionCultShare = ShareField.NewCulture(relKey),
            };
            e.TechKeys.Add(TechTable.StoneCore);
            ctx.Bands.Add(e);
            ctx.CellBands[pick] = e;   // 一格一实体：起源占据空格
        }
        ctx.FirstFarmTick = -1;
    }

    /// <summary>陆地连通分量（BFS，确定性：格序遍历）。</summary>
    private static int[] ComputeContinents(GameGrid grid, int n)
    {
        var cont = new int[n];
        for (int i = 0; i < n; i++) cont[i] = -1;
        int id = 0;
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (cont[i] != -1 || !grid.IsLandCell(i)) continue;
            cont[i] = id;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                foreach (int nb in grid.Neighbors[c])
                    if (cont[nb] == -1 && grid.IsLandCell(nb))
                    {
                        cont[nb] = id;
                        queue.Enqueue(nb);
                    }
            }
            id++;
        }
        return cont;
    }
}
