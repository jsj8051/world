using Godot;
using System;
using World.CivSim;
using World.LogicGrid;
using World.MapGen;
using World.Services;

namespace World.Diagnostics;

/// <summary>重新演化 .mpa → 新 .cmp（2026-08-19 综合验证轮：全部修复后重新演化，另存不覆盖旧档）。
/// 用：godot --headless --path . res://scenes/diag/EvolveCmp.tscn
///     -- --map=user://maps/xxx.mpa [--out=user://maps/xxx_v2.cmp] [--seed=N] [--origins=N]
/// 日志重定向 logs\diag\civsim\。</summary>
public partial class EvolveCmp : DiagSceneBase
{
    public override void _Ready()
    {
        string mapPath = "user://maps/map_seed42_n128.mpa";
        string outPath = null;
        int seed = 42, origins = 3;
        var args = ParseUserArgs();
        if (args.TryGetValue("map", out var mapArg)) mapPath = mapArg;
        if (args.TryGetValue("out", out var outArg)) outPath = outArg;
        if (args.TryGetValue("seed", out var seedArg) && int.TryParse(seedArg, out int s)) seed = s;
        if (args.TryGetValue("origins", out var oArg) && int.TryParse(oArg, out int o)) origins = Math.Clamp(o, 1, 6);
        outPath ??= mapPath.GetBaseName() + "_v2.cmp";
        if (!MapArchive.Read(mapPath, out var map))
        {
            LogService.LogErr("EvolveCmp", $"读取失败 {mapPath}");
            GetTree().Quit(1);
            return;
        }
        var grid = GameGrid.FromMapData(map);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogService.Log("EvolveCmp", $"演化 {mapPath} n={grid.N} seed={seed} origins={origins}（全部修复：文化传播/派别传播/酋邦庇护）...");
        int lastPct = -1;
        var result = CivEngine.Run(grid, seed, origins, p =>
        {
            int pct = (int)(p * 100f);
            if (pct >= lastPct + 10) { lastPct = pct; LogService.Log("EvolveCmp", $"进度 {pct}%"); }
        });
        sw.Stop();
        var ctx = result.Context;
        LogService.Log("EvolveCmp", $"演化完成 {result.FinalTick} tick（{result.FinalTick * CivSimContext.TickYears} 年）| 实体 {ctx.Bands.Count} | 人口 {ctx.TotalPopulation():F0} | 首转农 tick {ctx.FirstFarmTick} | 耗时 {sw.ElapsedMilliseconds}ms" +
                 $" | 贸易 {ctx.TradeEvents} 次/{ctx.TradeVolume:F0} 量 | 冲突 {ctx.Conflicts} | 分裂 {ctx.Fissions}");
        bool wrote = CivMapArchive.Write(outPath, grid, result);
        LogService.Log("EvolveCmp", $"写档 {outPath} = {wrote}");
        GetTree().Quit(wrote ? 0 : 1);
    }
}
