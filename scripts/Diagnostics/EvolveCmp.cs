using Godot;
using System;
using World.CivSim;
using World.LogicGrid;
using World.MapGen;

namespace World.Diagnostics;

/// <summary>重新演化 .mpa → 新 .cmp（2026-08-19 综合验证轮：全部修复后重新演化，另存不覆盖旧档）。
/// 用：godot --headless --path . res://scenes/diag/EvolveCmp.tscn
///     -- --map=user://maps/xxx.mpa [--out=user://maps/xxx_v2.cmp] [--seed=N] [--origins=N]
/// 日志重定向 logs\diag\civsim\。</summary>
public partial class EvolveCmp : Node
{
    public override void _Ready()
    {
        string mapPath = "user://maps/map_seed42_n128.mpa";
        string outPath = null;
        int seed = 42, origins = 3;
        var ua = OS.GetCmdlineUserArgs();
        for (int i = 0; i < ua.Length; i++)
        {
            string a = ua[i];
            string v = a.StartsWith("--") ? a.Substring(2) : a;
            if (v.StartsWith("map=", StringComparison.OrdinalIgnoreCase)) mapPath = v.Substring(4);
            else if (v.StartsWith("out=", StringComparison.OrdinalIgnoreCase)) outPath = v.Substring(4);
            else if (v.StartsWith("seed=", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.Substring(5), out int s)) seed = s;
            else if (v.StartsWith("origins=", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.Substring(8), out int o)) origins = Math.Clamp(o, 1, 6);
        }
        outPath ??= mapPath.GetBaseName() + "_v2.cmp";
        if (!MapArchive.Read(mapPath, out var map))
        {
            GD.PrintErr($"[EvolveCmp] 读取失败 {mapPath}");
            GetTree().Quit(1);
            return;
        }
        var grid = GameGrid.FromMapData(map);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        GD.Print($"[EvolveCmp] 演化 {mapPath} n={grid.N} seed={seed} origins={origins}（全部修复：文化传播/派别传播/酋邦庇护）...");
        int lastPct = -1;
        var result = CivEngine.Run(grid, seed, origins, p =>
        {
            int pct = (int)(p * 100f);
            if (pct >= lastPct + 10) { lastPct = pct; GD.Print($"[EvolveCmp] 进度 {pct}%"); }
        });
        sw.Stop();
        var ctx = result.Context;
        GD.Print($"[EvolveCmp] 演化完成 {result.FinalTick} tick（{result.FinalTick * CivSimContext.TickYears} 年）| 实体 {ctx.Tribes.Count} | 人口 {ctx.TotalPopulation():F0} | 首转农 tick {ctx.FirstFarmTick} | 耗时 {sw.ElapsedMilliseconds}ms" +
                 $" | 贸易 {ctx.TradeEvents} 次/{ctx.TradeVolume:F0} 量 | 冲突 {ctx.Conflicts} | 分裂 {ctx.Fissions}");
        bool wrote = CivMapArchive.Write(outPath, grid, result);
        GD.Print($"[EvolveCmp] 写档 {outPath} = {wrote}");
        GetTree().Quit(wrote ? 0 : 1);
    }
}
