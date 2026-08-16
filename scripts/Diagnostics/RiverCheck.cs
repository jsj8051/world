using Godot;
using World.MapGen;
using World.Services;

namespace World.Diagnostics;

/// <summary>河流存档数据检查：打印 RiverFlow/RiverLevel 合法性。</summary>
public partial class RiverCheck : DiagSceneBase
{
    public override void _Ready()
    {
        if (!MapArchive.Read("user://maps/map1.mpa", out var map)) { GetTree().Quit(); return; }
        LogService.Log("RiverCheck", $"n={map.Verts.Length} rivers={(map.RiverLevel != null ? "yes" : "no")} strength={(map.CurrentStrength != null ? "yes" : "no")} vol={(map.RiverVolume != null ? "yes" : "no")} lake={(map.LakeLevel != null ? "yes" : "no")} dirs={(map.CurrentDirs != null ? "yes" : "no")}");
        if (map.RiverLevel == null) { GetTree().Quit(); return; }
        int flowMin = int.MaxValue, flowMax = -1, bad = 0;
        for (int i = 0; i < map.RiverFlow.Length; i++)
        {
            int f = map.RiverFlow[i];
            if (f < flowMin) flowMin = f;
            if (f > flowMax) flowMax = f;
            if (f < 0 || f >= map.Verts.Length) bad++;
        }
        int lvl1 = 0, lvl2 = 0, lvl3 = 0;
        for (int i = 0; i < map.RiverLevel.Length; i++)
        {
            if (map.RiverLevel[i] == 1) lvl1++;
            else if (map.RiverLevel[i] == 2) lvl2++;
            else if (map.RiverLevel[i] == 3) lvl3++;
        }
        LogService.Log("RiverCheck", $"flow[{flowMin},{flowMax}] 越界={bad} | level: 1级={lvl1} 2级={lvl2} 3级={lvl3}");
        GetTree().Quit();
    }
}
