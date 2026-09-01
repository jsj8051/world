using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Godot;
using World.HexPlanet;
using World.MapView;

namespace World.Tests;

/// <summary>点击拾取纯函数测试（2026-09-01 收敛 PickTileAt）：NearestTile 球面最近中心判定——
/// 对 RadiusKm 球面的命中点与 tile.Center 同尺度比较，最近邻 = Voronoi 归属（正确格）。</summary>
public class MapViewerPickTests
{
    private static List<HexTile> Tiles(params Vector3[] centers)
        => centers.Select(c => new HexTile { Center = c }).ToList();

    [Test]
    public void NearestTile_Empty_ReturnsMinusOne()
    {
        Assert.AreEqual(-1, MapViewer.NearestTile(Vector3.Zero, new List<HexTile>()));
    }

    [Test]
    public void NearestTile_SelectsClosestCenter()
    {
        // 球面三点：命中点靠近 t1
        var tiles = Tiles(
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(0f, 0f, 1f));
        var hit = new Vector3(0.05f, 0.95f, 0.30f);   // ∈ t1 的 Voronoi 域
        Assert.AreEqual(1, MapViewer.NearestTile(hit, tiles));
    }

    [Test]
    public void NearestTile_RadiusScale_IsInvariant()
    {
        // 命中点与中心同在 RadiusKm 球面时（tile.Center = Normalized()*radius，射线交点在 r=RadiusKm）：
        // 无论半径多大，最近判定一致（距离平方同尺度缩放，序不变）
        float r = 6371f;
        var tiles = Tiles(
            new Vector3(1f, 0f, 0f).Normalized() * r,
            new Vector3(0f, 1f, 0f).Normalized() * r);
        var hit = new Vector3(0.1f, 0.9f, 0.2f).Normalized() * r;
        Assert.AreEqual(1, MapViewer.NearestTile(hit, tiles));
    }

    [Test]
    public void NearestTile_Tie_KeepsFirst()
    {
        // 等距（球面 90° 对称点）→ bestD 严格小于 → 保持先扫描者（确定性）
        var tiles = Tiles(
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f));
        var hit = new Vector3(1f, 1f, 0f).Normalized();
        Assert.AreEqual(0, MapViewer.NearestTile(hit, tiles));
    }

    [Test]
    public void NearestTile_ExactCenter_HitsOwn()
    {
        var tiles = Tiles(
            new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(0f, 0f, 1f));
        Assert.AreEqual(2, MapViewer.NearestTile(new Vector3(0f, 0f, 1f), tiles));
    }
}
