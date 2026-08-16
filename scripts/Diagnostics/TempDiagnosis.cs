using Godot;
using World.Biome;
using World.MapGen;

namespace World.Diagnostics;

/// <summary>温度图诊断：导出等距柱状温度图 + 打印极寒色值。</summary>
public partial class TempDiagnosis : DiagSceneBase
{
    public override void _Ready()
    {
        if (!MapArchive.Read("user://maps/map1.mpa", out var map) || !map.IsSpherical)
        {
            GD.PrintErr("[TempDiagnosis] 无法读取");
            GetTree().Quit();
            return;
        }
        // 打印色带在极寒/低温段的颜色
        foreach (var t in new[] { -82.4f, -60f, -40f, -30f, -20f, 0f, 26f })
        {
            var c = BiomeColors.TemperatureToColor(t);
            GD.Print($"[TempDiagnosis] {t}°C -> RGB({c.R:F2},{c.G:F2},{c.B:F2})  x={(t + 30f) / 65f:F2}");
        }

        // 导出等距柱状温度图
        const int w = 1024, h = 512;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            float lat = 90f - 180f * y / (h - 1);
            float la = Mathf.DegToRad(lat);
            float sinLa = Mathf.Sin(la), cosLa = Mathf.Cos(la);
            for (int x = 0; x < w; x++)
            {
                float lon = -180f + 360f * x / (w - 1);
                float lo = Mathf.DegToRad(lon);
                var p = new Vector3(cosLa * Mathf.Cos(lo), sinLa, cosLa * Mathf.Sin(lo));
                img.SetPixel(x, y, BiomeColors.TemperatureToColor(map.SampleTemperature(p)));
            }
        }
        img.SavePng("user://maps/temp_diag.png");
        GD.Print("[TempDiagnosis] saved user://maps/temp_diag.png");
        GetTree().Quit();
    }
}
