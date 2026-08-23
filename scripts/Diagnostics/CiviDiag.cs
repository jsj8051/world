using Godot;
using World.CivSim;
using World.LogicGrid;
using World.MapGen;
using World.Services;

namespace World.Diagnostics;

/// <summary>v7 单存档化链路验证（2026-08-23）：读自然 .mpa → Grid → CivEngine.Run → 写回带 CIVI →
/// 再读 → 断言 Civilization 非 null 且部落数一致。headless 跑：--quit-after N res://scenes/diag/CiviDiag.tscn -- --map=user://maps/xxx.mpa</summary>
public partial class CiviDiag : Node
{
    public override void _Ready()
    {
        var args = DiagSceneBase.ParseUserArgs();
        string path = args.TryGetValue("map", out var m) ? m : "user://maps/regress_v9_n64.mpa";
        string outPath = args.TryGetValue("out", out var o) ? o : "user://maps/civi_out.mpa";

        TechTable.Load();
        if (!MapArchive.Read(path, out var map))
        {
            GD.PrintErr($"[CiviDiag] 读自然档失败: {path}");
            GetTree().Quit(1);
            return;
        }
        GD.Print($"[CiviDiag] 读自然档 OK: n={map.Verts.Length} seed={map.Seed}");

        var grid = GameGrid.FromMapData(map);
        var result = CivEngine.Run(grid, 42, 3, p => { });
        GD.Print($"[CiviDiag] 演化完成: tribes={result.Context.Tribes.Count} tick={result.FinalTick}");

        bool wrote = MapArchive.WriteSpherical(outPath, map, result);
        GD.Print($"[CiviDiag] 写 v7 带 CIVI: {wrote}");
        if (!wrote)
        {
            GetTree().Quit(1);
            return;
        }

        if (!MapArchive.Read(outPath, out var round))
        {
            GD.PrintErr("[CiviDiag] 读回失败");
            GetTree().Quit(1);
            return;
        }
        bool hasCiv = round.Civilization != null;
        GD.Print($"[CiviDiag] 读回含文明: {hasCiv}");
        if (!hasCiv)
        {
            GD.PrintErr("[CiviDiag] ✗ 缺 CIVI 段（读回 Civilization=null）");
            GetTree().Quit(1);
            return;
        }
        GD.Print($"[CiviDiag] 读回文明: tribes={round.Civilization.Context.Tribes.Count} tick={round.Civilization.FinalTick}");
        if (round.Civilization.Context.Tribes.Count != result.Context.Tribes.Count)
        {
            GD.PrintErr($"[CiviDiag] ✗ 部落数不一致 {round.Civilization.Context.Tribes.Count} vs {result.Context.Tribes.Count}");
            GetTree().Quit(1);
            return;
        }
        GD.Print("[CiviDiag] ✓ 全链路一致");
        GetTree().Quit(0);
    }
}