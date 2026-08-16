using Godot;
using World.Biome;
using World.Services;

namespace World.Diagnostics;

/// <summary>自转速度诊断：不同速度下 20°N 风向 + 迎风海岸/内陆降水。</summary>
public partial class SpeedDiag : DiagSceneBase
{
    public override void _Ready()
    {
        foreach (var sp in new[] { 0.2f, 1f, 5f })
        {
            WindField.Prograde = true;
            WindField.RotationSpeed = sp;
            float la = Mathf.DegToRad(20f);
            var p = new Vector3(Mathf.Cos(la), Mathf.Sin(la), 0f);
            var w = WindField.WindAt(p);
            LogService.Log("SpeedDiag", $"速度{sp}×: 20°N 风向=({w.X:F3},{w.Y:F3},{w.Z:F3}) 纬向偏转={w.Z:F3}");
        }
        GetTree().Quit();
    }
}
