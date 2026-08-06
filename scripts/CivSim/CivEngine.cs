using System;
using System.Collections.Generic;
using Godot;
using World.LogicGrid;
using World.MapGen;

namespace World.CivSim;

/// <summary>
/// 文明演化引擎（v4 纯实体模型）。输入 GameGrid（自然层只读），输出演化结果（实体表 + 每格状态）。
/// 确定性：同 seed 同网格 → 同结果；注册表按 Order 每 tick 执行（docs/石器时代设计.md §二）。
///
/// 终止条件（用户拍板 2026-08-06）：
///   首个实体转农 tick + 100 ticks 结束；兜底 500 ticks 无农 → 停止（天然灭绝星球，诊断警告）。
/// </summary>
public static class CivEngine
{
    /// <summary>运行一次完整演化。onProgress：后台线程调用（0..1，tick 级；调用方保证线程安全）。</summary>
    public static CivSimResult Run(GameGrid grid, int seed, int originCount = 3, Action<float> onProgress = null)
    {
        TechTable.Load();
        int n = grid.N;
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellTribes = new List<CivEntity>[n],
            Entities = new List<CivEntity>(),
            Seed = seed,
            OriginCount = originCount,
            Rng = new DeterministicRandom(seed),   // 可序列化状态：读档续跑无分叉
            BaseK = new float[n],
            CellK = new float[n],
            CellPop = new float[n],
            BfsStamp = new int[n],
            BfsStampValue = 1,
            WildCrops = grid.EnsureWildCrops(),
            Suit = WildCropsSystem.Suitability(grid),
        };
        for (int i = 0; i < n; i++)
            ctx.CellTribes[i] = new List<CivEntity>();
        for (int i = 0; i < n; i++)
        {
            ctx.BaseK[i] = ctx.YHunter0(i);     // 无科技 K=Y（c=1 归一化）
            ctx.CellK[i] = ctx.BaseK[i];
        }

        var registry = CivModelRegistry.StoneAge();
        int maxTicks = CivSimContext.MaxTicksNoAgri + CivSimContext.TerminateAfterAgri;
        for (ctx.Tick = 0; ctx.Tick < maxTicks; ctx.Tick++)
        {
            RefreshCellState(ctx);
            registry.ExecuteAll(ctx);
            onProgress?.Invoke(Mathf.Min((ctx.Tick + 1f) / maxTicks, 1f));

            // 终止：首转农 +100 ticks；无农 500 ticks 兜底（天然灭绝星球）
            if (ctx.FirstFarmTick >= 0 && ctx.Tick - ctx.FirstFarmTick >= CivSimContext.TerminateAfterAgri)
            { ctx.Tick++; break; }
            if (ctx.FirstFarmTick < 0 && ctx.Tick >= CivSimContext.MaxTicksNoAgri - 1)
            { ctx.Tick++; break; }
        }

        ctx.Entities.RemoveAll(e => e.Dead);
        RefreshCellState(ctx);
        return new CivSimResult { Context = ctx, FinalTick = ctx.Tick };
    }

    /// <summary>读档续跑：从已恢复的 ctx 继续 extraTicks（IsFarming 入档 → 无滞回分叉）。</summary>
    public static CivSimResult Continue(CivSimContext ctx, int extraTicks, Action<float> onProgress = null)
    {
        var registry = CivModelRegistry.StoneAge();
        for (int k = 0; k < extraTicks; k++, ctx.Tick++)
        {
            RefreshCellState(ctx);
            registry.ExecuteAll(ctx);
            onProgress?.Invoke((k + 1f) / Mathf.Max(1, extraTicks));
        }
        ctx.Entities.RemoveAll(e => e.Dead);
        RefreshCellState(ctx);
        return new CivSimResult { Context = ctx, FinalTick = ctx.Tick };
    }

    /// <summary>重算每格总人口与当前承载（K = 格内实体最优产量/寒冷下限的 max；无人格 = BaseK）。</summary>
    public static void RefreshCellState(CivSimContext ctx)
    {
        int n = ctx.Grid.N;
        Array.Clear(ctx.CellPop, 0, n);
        Array.Clear(ctx.CellK, 0, n);
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead) continue;
            ctx.CellPop[e.Cell] += e.P;
            float k = ctx.KOf(e);
            if (k > ctx.CellK[e.Cell]) ctx.CellK[e.Cell] = k;
        }
        for (int i = 0; i < n; i++)
            if (ctx.CellK[i] <= 0f)
                ctx.CellK[i] = ctx.BaseK[i];
    }
}

/// <summary>演化结果（输出载体）。</summary>
public sealed class CivSimResult
{
    public CivSimContext Context;
    public int FinalTick;
}
