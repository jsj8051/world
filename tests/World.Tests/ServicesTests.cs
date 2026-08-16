using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.HexPlanet;
using World.LogicGrid;
using World.MapGen;
using World.MapView;
using World.Services;
using World.Surface;

namespace World.Tests;

/// <summary>
/// EventBus 跨场景事件总线测试（L0 纯 C# 事件；无引擎依赖）。
/// ⚠️ 静态状态（事件订阅 / pending 路径）跨测试存续：每个测试 try/finally 自清理，
/// 不依赖 SetUp/TearDown（本地执行器不支持）。
/// </summary>
public class EventBusTests
{
    [Test]
    public void GenerationProgress_SubscribePublishUnsubscribe()
    {
        int count = 0;
        float last = float.NaN;
        Action<float> handler = v => { count++; last = v; };
        EventBus.GenerationProgress += handler;
        try
        {
            EventBus.PublishProgress(0.25f);
            Assert.AreEqual(1, count);
            Assert.AreEqual(0.25f, last, 1e-6f);
            EventBus.PublishProgress(0.75f);
            Assert.AreEqual(2, count);
            Assert.AreEqual(0.75f, last, 1e-6f);
        }
        finally
        {
            EventBus.GenerationProgress -= handler;
        }
        EventBus.PublishProgress(1f);
        Assert.AreEqual(2, count, "退订后不应再收到");
    }

    [Test]
    public void MapViewRequested_ConsumeReturnsPendingThenClears()
    {
        EventBus.ConsumeMapViewRequest();          // 清理可能残留的 pending
        string seen = null;
        Action<string> handler = p => seen = p;
        EventBus.MapViewRequested += handler;
        try
        {
            EventBus.RequestMapView("user://a.mpa");
            Assert.AreEqual("user://a.mpa", seen);
            Assert.AreEqual("user://a.mpa", EventBus.ConsumeMapViewRequest());
            Assert.IsNull(EventBus.ConsumeMapViewRequest(), "第二次消费应返回 null");
            // 新请求覆盖旧 pending，仍只可消费一次
            EventBus.RequestMapView("user://b.mpa");
            Assert.AreEqual("user://b.mpa", EventBus.ConsumeMapViewRequest());
            Assert.IsNull(EventBus.ConsumeMapViewRequest());
        }
        finally
        {
            EventBus.MapViewRequested -= handler;
            EventBus.ConsumeMapViewRequest();
        }
    }

    [Test]
    public void GenerationFinished_SubscriberReceivesArgs()
    {
        bool ok = false;
        string path = null;
        Action<bool, string> handler = (o, p) => { ok = o; path = p; };
        EventBus.GenerationFinished += handler;
        try
        {
            EventBus.PublishFinished(true, "user://c.mpa");
            Assert.IsTrue(ok);
            Assert.AreEqual("user://c.mpa", path);
        }
        finally
        {
            EventBus.GenerationFinished -= handler;
        }
    }

    [Test]
    public void Publish_WithoutSubscribers_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => EventBus.PublishProgress(0.5f));
        Assert.DoesNotThrow(() => EventBus.PublishFinished(true, "x"));
        Assert.DoesNotThrow(() => EventBus.RequestMapView("user://tmp.mpa"));
        EventBus.ConsumeMapViewRequest();          // 清掉上面 RequestMapView 写入的 pending
    }
}

/// <summary>
/// PlanetColors 海拔色板测试（L0 纯函数：Godot.Color struct + Mathf，无引擎原生调用）。
/// 断言锚点色 + 五个渐变边界连续（深海→浅海→沙滩→低地→高地→雪顶）。
/// </summary>
public class PlanetColorsTests
{
    private static float L1(Color a, Color b) =>
        Mathf.Abs(a.R - b.R) + Mathf.Abs(a.G - b.G) + Mathf.Abs(a.B - b.B);

    [Test]
    public void ElevationToColor_ExtremeAnchors()
    {
        AssertColor(PlanetColors.ElevationToColor(-1f), 0.01f, 0.05f, 0.18f);   // 深海
        AssertColor(PlanetColors.ElevationToColor(-2f), 0.01f, 0.05f, 0.18f);   // 下限钳制
        AssertColor(PlanetColors.ElevationToColor(1f), 0.95f, 0.97f, 1.00f);    // 雪顶
        AssertColor(PlanetColors.ElevationToColor(2f), 0.95f, 0.97f, 1.00f);    // 上限钳制
        AssertColor(PlanetColors.ElevationToColor(0f), 0.12f, 0.45f, 0.68f);    // 海平面 = 浅海色
    }

    [Test]
    public void ElevationToColor_ContinuousAtRampBoundaries()
    {
        float[] bounds = { -0.05f, 0f, 0.05f, 0.35f, 0.65f };
        const float eps = 1e-4f;
        foreach (float b in bounds)
        {
            var left = PlanetColors.ElevationToColor(b - eps);
            var mid = PlanetColors.ElevationToColor(b);
            var right = PlanetColors.ElevationToColor(b + eps);
            Assert.LessOrEqual(L1(left, mid), 0.01f, $"e={b} 左侧不连续");
            Assert.LessOrEqual(L1(mid, right), 0.01f, $"e={b} 右侧不连续");
        }
    }

    [Test]
    public void ElevationToColor_RgbChannelsInRange_Deterministic()
    {
        for (float e = -1.5f; e <= 1.5f; e += 0.1f)
        {
            var c = PlanetColors.ElevationToColor(e);
            Assert.That(c.R, Is.InRange(0f, 1f));
            Assert.That(c.G, Is.InRange(0f, 1f));
            Assert.That(c.B, Is.InRange(0f, 1f));
            Assert.AreEqual(c, PlanetColors.ElevationToColor(e), "同输入必须同色");
        }
    }

    private static void AssertColor(Color c, float r, float g, float b, float tol = 1e-3f)
    {
        Assert.AreEqual(r, c.R, tol);
        Assert.AreEqual(g, c.G, tol);
        Assert.AreEqual(b, c.B, tol);
    }
}

/// <summary>
/// PowerPalette 最远点采样调色板测试（L0 纯函数）。
/// 契约：确定性（同 id 集同色、与输入顺序无关）；任意两势力颜色可分
/// （源码注释实测 291 势力最小色距 ≥0.1，远超 0.05 肉眼阈值——本测试锁定 ≥0.05 保守界）。
/// </summary>
public class PowerPaletteTests
{
    [Test]
    public void Build_Empty_ReturnsEmpty()
    {
        Assert.AreEqual(0, PowerPalette.Build(new int[0]).Count);
    }

    [Test]
    public void Build_ContainsEveryId()
    {
        var ids = new List<int> { 9, 3, 7, 1, 42 };
        var pal = PowerPalette.Build(ids);
        Assert.AreEqual(ids.Count, pal.Count);
        foreach (int id in ids)
        {
            Assert.True(pal.ContainsKey(id), $"缺少势力 {id}");
            AssertColorValid(pal[id]);
        }
    }

    [Test]
    public void Build_Deterministic_IndependentOfInputOrder()
    {
        var a = new List<int> { 1, 3, 7, 42 };
        var b = new List<int> { 42, 7, 3, 1 };
        var pa = PowerPalette.Build(a);
        var pb = PowerPalette.Build(b);
        foreach (int id in a)
            Assert.AreEqual(pa[id], pb[id], $"势力 {id} 的颜色不应随输入顺序变化");
    }

    [Test]
    public void Build_ColorsWellSeparated()
    {
        foreach (int count in new[] { 5, 30, 100, 291 })
        {
            var ids = Enumerable.Range(10, count).ToList();
            var pal = PowerPalette.Build(ids);
            var colors = new List<Color>(pal.Values);
            Assert.AreEqual(count, colors.Count);
            float minDist = float.MaxValue;
            for (int i = 0; i < colors.Count; i++)
                for (int j = i + 1; j < colors.Count; j++)
                    minDist = Mathf.Min(minDist, PowerPalette.Dist(colors[i], colors[j]));
            Assert.GreaterOrEqual(minDist, 0.05f, $"count={count} 最小色距 {minDist:F4} 跌破肉眼阈值");
        }
    }

    [Test]
    public void Dist_IsL1Manhattan()
    {
        Assert.AreEqual(1f, PowerPalette.Dist(new Color(1f, 0f, 0f), new Color(0f, 0f, 0f)), 1e-6f);
        Assert.AreEqual(0.3f, PowerPalette.Dist(new Color(0f, 0.2f, 0f), new Color(0.1f, 0f, 0f)), 1e-6f);
        Assert.AreEqual(0f, PowerPalette.Dist(new Color(0.1f, 0.2f, 0.3f), new Color(0.1f, 0.2f, 0.3f)), 1e-6f);
        // 海色锚点 (0.10, 0.22, 0.48) 距黑 = 0.10+0.22+0.48 = 0.80
        Assert.AreEqual(0.8f, PowerPalette.Dist(new Color(0.10f, 0.22f, 0.48f), new Color(0f, 0f, 0f)), 1e-5f);
    }

    private static void AssertColorValid(Color c)
    {
        Assert.That(c.R, Is.InRange(0f, 1f));
        Assert.That(c.G, Is.InRange(0f, 1f));
        Assert.That(c.B, Is.InRange(0f, 1f));
        Assert.AreEqual(1f, c.A, 1e-6f);
    }
}

/// <summary>
/// TileIndex 显示面 ↔ 逻辑顶点索引测试（L0：MapData.NearestVertex + 惰性反查，纯托管）。
/// 不变量：FacesOf(v) ≡ {f : FaceToVertex(f) == v}；显示数据按面空间读写，逻辑数据按顶点空间读写。
/// </summary>
public class TileIndexTests
{
    private static MapData BuildMapData(int n)
    {
        Icosahedron.Subdivide(n, GameGrid.DefaultRadiusKm, out var verts, out _);
        // ⚠️ MapData 假定单位球顶点（BucketOf 用 asin(Y) 算纬度；生产存档 verts 即单位方向）
        var m = new MapData { Verts = verts.ConvertAll(v => v.Normalized()).ToArray() };
        m.EnsureBuckets();
        return m;
    }

    private static bool Has(List<int> list, int v)
    {
        foreach (int x in list) if (x == v) return true;
        return false;
    }

    [Test]
    public void FaceToVertex_FacesOf_BijectionInvariant()
    {
        var map = BuildMapData(2);
        var ti = new TileIndex(map, map.Verts);
        Assert.AreEqual(map.Verts.Length, ti.Count);
        for (int f = 0; f < ti.Count; f++)
        {
            int v = ti.FaceToVertex(f);
            Assert.That(v, Is.InRange(0, map.Verts.Length - 1));
            Assert.True(Has(ti.FacesOf(v), f), $"面 {f} 不在其顶点的 FacesOf 中");
        }
        // 独立复核：FacesOf(v) == {f : FaceToVertex(f) == v}
        for (int v = 0; v < map.Verts.Length; v++)
        {
            var expected = new List<int>();
            for (int f = 0; f < ti.Count; f++)
                if (ti.FaceToVertex(f) == v) expected.Add(f);
            CollectionAssert.AreEquivalent(expected, ti.FacesOf(v), $"顶点 {v} 反查不一致");
        }
    }

    [Test]
    public void FaceToVertex_SelfIdentity_WhenCentersAreVerts()
    {
        var map = BuildMapData(2);
        var ti = new TileIndex(map, map.Verts);
        for (int f = 0; f < ti.Count; f++)
            Assert.AreEqual(f, ti.FaceToVertex(f), "中心=顶点自身时最近顶点应为自身");
    }

    [Test]
    public void FacesOf_IsCached()
    {
        var map = BuildMapData(2);
        var ti = new TileIndex(map, map.Verts);
        Assert.AreSame(ti.FacesOf(5), ti.FacesOf(5), "反查表应惰性缓存");
    }

    [Test]
    public void RepeatedCenters_StillSatisfyInvariant()
    {
        var map = BuildMapData(2);
        int n = map.Verts.Length;
        var centers = new Vector3[n];
        for (int i = 0; i < n; i++) centers[i] = map.Verts[(i * 7) % n];   // 大量重复中心
        var ti = new TileIndex(map, centers);
        Assert.AreEqual(n, ti.Count);
        for (int f = 0; f < n; f++)
        {
            int v = ti.FaceToVertex(f);
            Assert.That(v, Is.InRange(0, n - 1));
            Assert.True(Has(ti.FacesOf(v), f));
        }
    }
}