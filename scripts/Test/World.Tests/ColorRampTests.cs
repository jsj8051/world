using NUnit.Framework;
using Godot;
using World.Utils;

namespace World.Tests;

/// <summary>连续色带统一工具测试（2026-08-31 重构；海拔色带 ISO 9241-307 改版 + 海洋冷色分带）：
/// 温度/降水色带走线性 RampSample（与旧内嵌逻辑逐点等价）；海拔色带走三次平滑
/// RampSampleSmooth（Catmull-Rom：停点位置恒等停点色、段间切线连续、同位置台阶硬切）。
/// 色带归属（2026-10-09 校正）：海拔 `ElevationMode` / 温度 `TemperatureMode` / 降水 `PrecipitationMode`
/// ——**色带归消费它的地图模式**；本类用**测试自有夹具色带**测算法本身
///（旧 `Domain.BiomeColors.TempStops` 已随 `World.Domain` 解散删除）。</summary>
public class ColorRampTests
{
    /// <summary>测试自有示例色带（原 `Domain.BiomeColors.TempStops`；域 −85…+45 °C）。
    /// 本类测的是 `ColorRamp.RampSample` **算法** ⇒ 夹具色带即可，不依赖任何生产色带。</summary>
    static readonly ColorRamp.ColorStop[] TempStops =
    {
        new(-85f, new Color(0.08f, 0.12f, 0.45f)),
        new(-30f, new Color(0.10f, 0.28f, 0.62f)),
        new(0f,   new Color(0.22f, 0.52f, 0.72f)),
        new(15f,  new Color(0.38f, 0.72f, 0.42f)),
        new(30f,  new Color(0.92f, 0.78f, 0.28f)),
        new(45f,  new Color(0.88f, 0.30f, 0.15f)),
    };

    // ── RampSample（线性）基础行为 ────────────────────────────────────

    [Test]
    public void RampSample_ClampsBelowFirstStop_ToFirstColor()
    {
        var c = ColorRamp.RampSample(TempStops, -200f);
        Assert.IsTrue(c.IsEqualApprox(new Color(0.08f, 0.12f, 0.45f)));
    }

    [Test]
    public void RampSample_ClampsAboveLastStop_ToLastColor()
    {
        var c = ColorRamp.RampSample(TempStops, 100f);
        Assert.IsTrue(c.IsEqualApprox(new Color(0.88f, 0.30f, 0.15f)));
    }

    [Test]
    public void RampSample_InterpolatesMidSegment()
    {
        // -85→-30 段中点 = 两端 Lerp 0.5
        var c = ColorRamp.RampSample(TempStops, -57.5f);
        var expect = new Color(0.08f, 0.12f, 0.45f).Lerp(new Color(0.10f, 0.28f, 0.62f), 0.5f);
        Assert.IsTrue(c.IsEqualApprox(expect));
    }

    [Test]
    public void RampSample_SegmentBoundary_BelongsToRightSide()
    {
        // t=-30 恰为段边界 → -30 停点色（与旧二分双闭区间结果一致）
        var c = ColorRamp.RampSample(TempStops, -30f);
        Assert.IsTrue(c.IsEqualApprox(new Color(0.10f, 0.28f, 0.62f)));
    }

    // ── 海拔色带（ISO 9241-307 海陆分带）结构 ──────────────────────────

    // ── RampSampleSmooth（三次贝塞尔/Catmull-Rom）行为 ────────────────

    // ── 温度/降水线性色带与旧实现逐点等价（未随海拔改版）────────────────

    [Test]
    public void RampSample_Temperature_MatchesLegacyLogic()
    {
        // 旧二分分段逻辑（2026-08-31 重构前快照；原属 BiomeColors.TemperatureToColor）
        static Color Legacy(float t)
        {
            float[] breaks = { -85f, -30f, 0f, 15f, 30f, 45f };
            Color[] colors =
            {
                new(0.08f, 0.12f, 0.45f), new(0.10f, 0.28f, 0.62f), new(0.22f, 0.52f, 0.72f),
                new(0.38f, 0.72f, 0.42f), new(0.92f, 0.78f, 0.28f), new(0.88f, 0.30f, 0.15f),
            };
            int seg = -1;
            for (int i = 0; i < breaks.Length - 1; i++)
                if (t >= breaks[i] && t <= breaks[i + 1]) { seg = i; break; }
            if (seg < 0) return t < breaks[0] ? colors[0] : colors[^1];
            float f = (t - breaks[seg]) / (breaks[seg + 1] - breaks[seg]);
            return colors[seg].Lerp(colors[seg + 1], f);
        }

        for (float t = -120f; t <= 80f; t += 0.5f)
        {
            var got = ColorRamp.RampSample(TempStops, t);
            Assert.IsTrue(got.IsEqualApprox(Legacy(t)), $"温度 {t}°C 不等价: got={got} legacy={Legacy(t)}");
        }
    }

    // ── RampLegendColors（图例与画面同源）──────────────────────────────

    // ── 色带数据完整性 ────────────────────────────────────────────────

    private static void AssertStopsValid(ColorRamp.ColorStop[] stops)
    {
        Assert.GreaterOrEqual(stops.Length, 2);
        for (int i = 1; i < stops.Length; i++)
            Assert.GreaterOrEqual(stops[i].Pos, stops[i - 1].Pos, $"停点 {i} 未升序");
    }
}
