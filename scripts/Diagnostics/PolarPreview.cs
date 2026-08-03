using Godot;
using System;
using World.MapGen;
using World.Surface;

namespace World.Diagnostics;

/// <summary>
/// 极区放大诊断（headless）：读 v3 存档，导出极区（lat 55°~90°）放大图
/// 等距柱状 1024×512，验证极区色带是否为同心圆（等高线）。
/// </summary>
public partial class PolarPreview : Node
{
    public override void _Ready()
    {
        string path = "user://maps/map1.mpa";
        if (!MapArchive.Read(path, out var map) || !map.IsSpherical)
        {
            GD.PrintErr("[PolarPreview] 无法读取 v3 存档");
            GetTree().Quit();
            return;
        }
        const int w = 1024, h = 512;
        float minLat = 55f;   // 裁剪纬度
        float range = map.MaxElev - map.MinElev;
        float hSea = -map.MinElev / range;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            float lat = minLat + (90f - minLat) * y / (h - 1);   // 55° → 90°
            float la = Mathf.DegToRad(lat);
            float sinLa = Mathf.Sin(la), cosLa = Mathf.Cos(la);
            for (int x = 0; x < w; x++)
            {
                float lon = -180f + 360f * x / (w - 1);
                float lo = Mathf.DegToRad(lon);
                var p = new Vector3(cosLa * Mathf.Cos(lo), sinLa, cosLa * Mathf.Sin(lo));
                float hN = map.NormalizedElev(map.SampleElevation(p));
                float e1 = (hN - hSea) / (hSea > 0.5f ? hSea : 1f - hSea);
                img.SetPixel(x, y, PlanetColors.ElevationToColor(e1));
            }
        }
        img.SavePng("user://maps/polar_preview.png");
        GD.Print("[PolarPreview] saved user://maps/polar_preview.png (lat 55-90)");
        GetTree().Quit();
    }
}
