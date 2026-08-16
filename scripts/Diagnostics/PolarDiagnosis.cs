using Godot;
using System;
using System.Collections.Generic;
using World.HexPlanet;
using World.MapGen;

namespace World.Diagnostics;

/// <summary>
/// 极区网格拓扑诊断（headless）：
/// 检查 Goldberg hex 网格在极区（lat&gt;80°）的结构——五边形位置、每格邻居数、
/// 纬度环带分布。回答"极地圈状/辐射状是否预期"。
/// </summary>
public partial class PolarDiagnosis : DiagSceneBase
{
    public override void _Ready()
    {
        const int n = 128;   // GridN（和 MapViewer 验证一致）
        // 坐标标度：任意 R（单位向量 × R），温度/降水基于纬度与 R 无关——统一默认地球标度
        const float radius = MapArchive.DefaultRadiusKm;
        Icosahedron.Subdivide(n, radius, out var verts, out var indices);
        var mesh = new SubdividedMesh(verts, indices);
        var builder = new GoldbergBuilder(mesh, radius, null);
        var tiles = builder.Tiles;

        // 统计每格邻居数
        var neighborCounts = new Dictionary<int, int>();
        int pentagons = 0, hexagons = 0;
        var pentagonLats = new List<float>();
        foreach (var t in tiles)
        {
            int nb = t.Corners.Length;   // 角数 = 邻居数（对偶格）
            neighborCounts[nb] = neighborCounts.TryGetValue(nb, out var c) ? c + 1 : 1;
            if (nb == 5) { pentagons++; pentagonLats.Add(GetLatDeg(t.Center)); }
            else if (nb == 6) hexagons++;
        }
        GD.Print($"[Polar] tiles={tiles.Count} 五边形={pentagons} 六边形={hexagons}");
        foreach (var kv in neighborCounts)
            GD.Print($"[Polar] {kv.Key}邻居格: {kv.Value} 个");

        // 五边形纬度分布（是否有靠近极点的）
        if (pentagonLats.Count > 0)
        {
            float minLat = float.MaxValue, maxLat = float.MinValue;
            foreach (var la in pentagonLats) { minLat = Mathf.Min(minLat, la); maxLat = Mathf.Max(maxLat, la); }
            GD.Print($"[Polar] 五边形纬度范围: {minLat:F1}° ~ {maxLat:F1}°");
        }

        // 极区（lat>80°）每格邻居数分布
        int polar5 = 0, polar6 = 0;
        foreach (var t in tiles)
        {
            if (GetLatDeg(t.Center) > 80f || GetLatDeg(t.Center) < -80f)
            {
                if (t.Corners.Length == 5) polar5++;
                else if (t.Corners.Length == 6) polar6++;
            }
        }
        GD.Print($"[Polar] 极区(|lat|&gt;80°): 五边形={polar5} 六边形={polar6}");

        // 极区最近顶点环带：采样点与最近顶点的角距分布
        GD.Print("[Polar] 诊断完成");
        GetTree().Quit();
    }

    private static float GetLatDeg(Vector3 p)
    {
        Vector3 dir = p.Normalized();
        return Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f)));
    }
}
