using NUnit.Framework;
using World.MapView.Layers;

namespace World.Tests;

/// <summary>海拔格信息分带测试（2026-09-01 格信息面板）：ElevationZoneName 与 ElevationStops
/// 断点同源（海 0/-50/-200/-2000/-6000；陆 0/500/2000/5000），边界归属右侧段（半开区间一致）。</summary>
public class ElevationLayerTests
{
    [Test]
    public void ZoneName_Ocean_DeepToShallow()
    {
        Assert.AreEqual("海沟", ElevationLayer.ElevationZoneName(-8000f));
        Assert.AreEqual("海沟", ElevationLayer.ElevationZoneName(-6000.1f));
        Assert.AreEqual("深海平原", ElevationLayer.ElevationZoneName(-6000f));   // 边界归属右段
        Assert.AreEqual("深海平原", ElevationLayer.ElevationZoneName(-2000.1f));
        Assert.AreEqual("大陆坡", ElevationLayer.ElevationZoneName(-2000f));
        Assert.AreEqual("大陆坡", ElevationLayer.ElevationZoneName(-200.1f));
        Assert.AreEqual("大陆架", ElevationLayer.ElevationZoneName(-200f));
        Assert.AreEqual("大陆架", ElevationLayer.ElevationZoneName(-50.1f));
        Assert.AreEqual("潮间带", ElevationLayer.ElevationZoneName(-50f));
        Assert.AreEqual("潮间带", ElevationLayer.ElevationZoneName(-0.01f));
    }

    [Test]
    public void ZoneName_Land_LowToExtreme()
    {
        Assert.AreEqual("低海拔", ElevationLayer.ElevationZoneName(0f));
        Assert.AreEqual("低海拔", ElevationLayer.ElevationZoneName(499.9f));
        Assert.AreEqual("中海拔", ElevationLayer.ElevationZoneName(500f));
        Assert.AreEqual("中海拔", ElevationLayer.ElevationZoneName(1999.9f));
        Assert.AreEqual("高海拔", ElevationLayer.ElevationZoneName(2000f));
        Assert.AreEqual("高海拔", ElevationLayer.ElevationZoneName(4999.9f));
        Assert.AreEqual("极高山区", ElevationLayer.ElevationZoneName(5000f));
        Assert.AreEqual("极高山区", ElevationLayer.ElevationZoneName(9000f));
    }
}
