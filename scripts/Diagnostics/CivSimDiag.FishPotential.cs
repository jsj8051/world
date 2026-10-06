// Slice: CivSimDiag.FishPotential.cs - T91 ① FishPotential 独立验收（2026-10-06 用户拍板：Fish 与 Riparian 分开做）。
// 纯只读：不新增状态、不动等边际 water-filling、不碰存档格式、不消耗 Rng。
// 验收口径（用户给定）：海岸 FishPotential>0 / 湖泊 FishPotential>0 / 纯内陆不凭空产鱼；
//   且 water-filling 不变、FishFrac=0 时精确退化（退化解原则）。
using Godot;
using System;
using System.Collections.Generic;
using World.Domain;
using World.CivSim;
using World.LogicGrid;
using World.Services;

using World.CivSim.Entities;
namespace World.Diagnostics;

public partial class CivSimDiag
{
    /// <summary>
    /// T91 ① FishPotential 验收（2026-10-06）。
    /// ① 格级语义 + 退化解（纯静态世界事实，不依赖演化）：
    ///    内陆 fish==0；沿海/湖泊 fish>0；三分和 ≡ 1；
    ///    **代数恒等式** pc_新 − pc_旧 ≡ fish·开垦（与领地权重 w 无关）⇒ 开垦=0 时逐格精确退化。
    /// ② 端到端：真实演化末态，水产确实进入实体产出且不与猎物双计。
    /// ⚠️ 与 T90 同款纪律：`WantExplicit` gate，**不进全量默认**，`--only=T91` 触发。
    /// </summary>
    private void T91_FishPotential(int seed, int origins)
    {
        int n = _grid.N;
        float A = _grid.CellAreaKm2;
        LogService.Log("T91", $"── ① FishPotential 验收 ── n={n} seed={seed} 起源={origins} 格面积={A:F2}km²");

        // ── ① 格级语义 + 退化解（世界事实层，纯静态）──
        int live = 0, coastCells = 0, lakeCells = 0, lakeNbCells = 0, ripCells = 0, inlandCells = 0, fishCells = 0;
        double maxSumErr = 0, maxIdentErr = 0;
        bool inlandHasFish = false, coastHasFish = true, lakeHasFish = true;
        var ks = new[] { 0f, 0.25f, 1f };
        for (int c = 0; c < n; c++)
        {
            if (!_grid.IsLandCell(c)) continue;
            live++;
            bool isCoast = _grid.IsCoast(c);
            bool hasLake = _grid.LakeLevel[c] > 0;
            bool hasLakeNb = false;
            foreach (int nb in _grid.Neighbors[c])
                if (_grid.LakeLevel[nb] > 0) { hasLakeNb = true; break; }
            bool lakeAccess = hasLake || hasLakeNb;
            bool rip = _grid.Biome[c] == (byte)BiomeType.Riparian;

            if (rip) ripCells++;
            if (hasLake) lakeCells++;
            else if (hasLakeNb) lakeNbCells++;
            if (isCoast && !lakeAccess && !rip) coastCells++;
            if (!isCoast && !lakeAccess && !rip) inlandCells++;

            var b = (BiomeType)_grid.Biome[c];
            float fishFrac = CivSimContext.FishFrac(b, isCoast, rip, lakeAccess);
            CivSimContext.ForageShares(b, isCoast, rip, lakeAccess, out float prey, out float berry, out float fish);
            float pf = CivSimContext.PreyFrac(b);

            maxSumErr = Math.Max(maxSumErr, Math.Abs(prey + berry + fish - 1f));
            // 恒等式（与 w 无关，故取 w=1）：pc_新 − pc_旧 = fish·开垦
            for (int q = 0; q < ks.Length; q++)
            {
                float k = ks[q];
                float newPc = (1f - 0.5f * k) * prey + (1f - k) * berry + fish;
                float oldPc = (1f - 0.5f * k) * pf + (1f - k) * (1f - pf);
                maxIdentErr = Math.Max(maxIdentErr, Math.Abs((newPc - oldPc) - fish * k));
            }

            if (fishFrac > 0f) fishCells++;
            if (!isCoast && !lakeAccess && !rip && fishFrac != 0f) inlandHasFish = true;
            if ((isCoast || lakeAccess) && fishFrac <= 0f)
            { if (isCoast) coastHasFish = false; if (lakeAccess) lakeHasFish = false; }
        }

        LogService.Log("T91", $"[格级水条件] 陆地格={live} 沿海={coastCells} 湖水格={lakeCells}+邻湖格={lakeNbCells} " +
                             $"河岸={ripCells} 内陆={inlandCells} 有水产格={fishCells}");
        LogService.Log("T91", "[口径] Riparian 格 = 0 属**已知独立缺口**（WorldGen 不生成 Riparian）——② 独立变更修复；" +
                             "FishFrac 的 Riparian 分支为预留，当前恒不命中。");
        Check("T91a 水产语义：纯内陆=0 / 沿海>0 / 湖泊>0（不凭空产鱼）",
            !inlandHasFish && coastHasFish && lakeHasFish,
            $"内陆产鱼={inlandHasFish} 沿海有鱼={coastHasFish} 湖泊有鱼={lakeHasFish}");
        Check("T91b 三分和≡1 且 pc新−pc旧 ≡ 水产×开垦（开垦=0 精确退化解）",
            maxSumErr < 1e-5 && maxIdentErr < 1e-5,
            $"max|三分和−1|={maxSumErr:E2} max|恒等式残差|={maxIdentErr:E2}（后者应为 0）");

        // ── ② 端到端：真实演化末态 ──
        var run = CivEngine.Run(_grid, seed, origins);
        var ctx = run.Context;
        int bands = 0, fishBands = 0, fishTerrCells = 0;
        double sumFLast = 0, sumFish = 0, maxSplitOver = 0, maxCompErr = 0;
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            bands++;
            sumFLast += e.FLast; sumFish += e.FFishLast;
            if (e.FFishLast > 0f) fishBands++;
            maxSplitOver = Math.Max(maxSplitOver, e.FBerryLast + e.FFishLast - e.FHuntLast);
            maxCompErr = Math.Max(maxCompErr, Math.Abs(e.FLast - (e.FHuntLast + e.FFarmLast + e.FHerdLast)));
            var terr = ctx.TerritoryOf(e);
            if (terr != null)
                foreach (int c in terr)
                    if (HasFishAt(ctx, c)) fishTerrCells++;
        }
        LogService.Log("T91", $"[端到端] 末态 tick={run.FinalTick} 实体={bands} 产鱼实体={fishBands} " +
                             $"有水产领地格={fishTerrCells} ΣFFishLast={sumFish:F0} ΣFLast={sumFLast:F0}（占 {R(sumFish, sumFLast):P2}）");

        // ── ③ ① 的**可归因量级**（与轨迹分岔解耦）：直接食物注入 = Σ_领地格 R·A·w·水产·开垦 ──
        //   ⚠️ 口径：轨迹级差异（实体数/人口/种子持有）是多变量混沌分岔，**不能**解读为"鱼导致人口变化"。
        //     唯一可归因的是"新公式相对旧公式在该格多出的食物"——旧式无水产项，故净增 ≡ R·A·w·fish·cult。
        double sumPot = 0, sumInject = 0;
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            var terr = ctx.TerritoryOf(e);
            var dists = ctx.TerritoryDistsOf(e);
            if (terr == null) continue;
            for (int k = 0; k < terr.Count; k++)
            {
                int c = terr[k];
                if (ctx.R[c] <= 0f) continue;
                float w = CivSimContext.ProductionWeight(dists[k]);
                if (w <= 0f) continue;
                float cult = ctx.Cultivation != null ? ctx.Cultivation[c] : 0f;
                ctx.ForageSharesAt(c, out _, out _, out float fish);
                float sp = ctx.R[c] * A * w;
                sumPot += sp;
                sumInject += sp * fish * cult;
            }
        }
        LogService.Log("T91", $"[可归因量级] 直接食物注入 Σ R·A·w·水产·开垦 = {sumInject:F1}" +
                             $"（占归属格总潜在 ΣR·A·w={sumPot:F0} 的 {R(sumInject, sumPot):P2}）" +
                             " ——与轨迹分岔解耦，是 ① 的净效应。");

        Check("T91c 水产进入实体产出（≥1 实体 FFishLast>0）", fishBands > 0,
            $"产鱼实体={fishBands}/{bands} ΣFFishLast={sumFish:F1}");
        Check("T91d 三分拆分不越界（浆果+水产 ≤ 采集总量）", maxSplitOver < 1e-2,
            $"max(FBerry+FFish−FHunt)={maxSplitOver:E2}");
        Check("T91e FLast=Σ分量 不变（水产含在采集分量内，不新增总和项 ⇒ 能量模型不动）", maxCompErr < 1e-2,
            $"max|FLast−(FHunt+FFarm+FHerd)|={maxCompErr:E2}");
        LogService.Log("T91", "[water-filling] 未改动（同 LF 两档、同凹化闭式）——结构由 T30/T38/T39 守；" +
                             "本工具只证明采集档潜在的**分量分解**变了（三分），总量在开垦=0 时逐格不变。");
    }

    /// <summary>格是否具备水产（R>0 且 FishFrac>0）——端到端统计用。</summary>
    private bool HasFishAt(CivSimContext ctx, int c)
    {
        if (ctx.R[c] <= 0f) return false;
        var b = (BiomeType)_grid.Biome[c];
        return CivSimContext.FishFrac(b, _grid.IsCoast(c), b == BiomeType.Riparian, ctx.LakeFishAccess(c)) > 0f;
    }
}
