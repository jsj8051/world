using NUnit.Framework;
using Godot;
using World.Biome;
using World.MapView;
using World.MapView.Layers;
using World.Utils;

namespace World.Tests;

/// <summary>连续色带统一工具测试（2026-08-31 重构；海拔色带 ISO 9241-307 改版 + 海洋冷色分带）：
/// 温度/降水色带走线性 RampSample（与旧内嵌逻辑逐点等价）；海拔色带走三次平滑
/// RampSampleSmooth（Catmull-Rom：停点位置恒等停点色、段间切线连续、同位置台阶硬切）。
/// 色带归属：ElevationLayer.ElevationStops / BiomeColors.TempStops / PrecipitationLayer.PrecipStops。</summary>
public class ColorRampTests
{
    // ── RampSample（线性）基础行为 ────────────────────────────────────

    [Test]
    public void RampSample_ClampsBelowFirstStop_ToFirstColor()
    {
        var c = ColorRamp.RampSample(BiomeColors.TempStops, -200f);
        Assert.IsTrue(c.IsEqualApprox(new Color(0.08f, 0.12f, 0.45f)));
    }

    [Test]
    public void RampSample_ClampsAboveLastStop_ToLastColor()
    {
        var c = ColorRamp.RampSample(BiomeColors.TempStops, 100f);
        Assert.IsTrue(c.IsEqualApprox(new Color(0.88f, 0.30f, 0.15f)));
    }

    [Test]
    public void RampSample_InterpolatesMidSegment()
    {
        // -85→-30 段中点 = 两端 Lerp 0.5
        var c = ColorRamp.RampSample(BiomeColors.TempStops, -57.5f);
        var expect = new Color(0.08f, 0.12f, 0.45f).Lerp(new Color(0.10f, 0.28f, 0.62f), 0.5f);
        Assert.IsTrue(c.IsEqualApprox(expect));
    }

    [Test]
    public void RampSample_SegmentBoundary_BelongsToRightSide()
    {
        // t=-30 恰为段边界 → -30 停点色（与旧二分双闭区间结果一致）
        var c = ColorRamp.RampSample(BiomeColors.TempStops, -30f);
        Assert.IsTrue(c.IsEqualApprox(new Color(0.10f, 0.28f, 0.62f)));
    }

    // ── 海拔色带（ISO 9241-307 海陆分带）结构 ──────────────────────────

    [Test]
    public void ElevationStops_NewSchemeStructure()
    {
        var s = ElevationLayer.ElevationStops;
        Assert.AreEqual(11, s.Length);
        // 海洋冷色系（越深越暗）
        Assert.IsTrue(s[0].C.IsEqualApprox(new Color(0.043f, 0.055f, 0.133f)));  // 海沟 墨紫黑
        Assert.IsTrue(s[1].C.IsEqualApprox(new Color(0.090f, 0.169f, 0.310f)));  // 深海平原底 靛蓝
        Assert.IsTrue(s[2].C.IsEqualApprox(new Color(0.184f, 0.357f, 0.541f)));  // 大陆坡底 深蓝
        Assert.IsTrue(s[3].C.IsEqualApprox(new Color(0.482f, 0.647f, 0.769f)));  // 大陆架 灰蓝
        Assert.IsTrue(s[4].C.IsEqualApprox(new Color(0.776f, 0.863f, 0.922f)));  // 潮间带 浅灰蓝
        Assert.IsTrue(s[5].C.IsEqualApprox(new Color(0.906f, 0.941f, 0.965f)));  // 海面 青白
        // 0m 海陆台阶 + 陆地暖色系（越高越亮）
        Assert.IsTrue(s[6].C.IsEqualApprox(new Color(0.45f, 0.75f, 0.90f)));     // 陆地天蓝
        Assert.IsTrue(s[7].C.IsEqualApprox(new Color(0.58f, 0.78f, 0.32f)));     // 浅绿 500m
        Assert.IsTrue(s[8].C.IsEqualApprox(new Color(0.93f, 0.78f, 0.25f)));     // 金黄 2000m
        Assert.IsTrue(s[9].C.IsEqualApprox(new Color(0.55f, 0.36f, 0.20f)));     // 赭石 5000m
        Assert.IsTrue(s[10].C.IsEqualApprox(new Color(0.98f, 0.99f, 1.00f)));    // 白 6000m
    }

    [Test]
    public void ElevationStops_EveryStopPosition_HitsStopColor()
    {
        // 停点位置采样恒等该停点色（Catmull-Rom 端点不偏移；0m 双停点取右侧=天蓝）
        var s = ElevationLayer.ElevationStops;
        for (int i = 0; i < s.Length; i++)
        {
            var c = ColorRamp.RampSampleSmooth(s, s[i].Pos);
            var expect = i == 5 ? s[6].C : s[i].C;   // 0m 双停点：同位置台阶归属右侧（陆地色）
            Assert.IsTrue(c.IsEqualApprox(expect),
                $"停点 {i} (Pos={s[i].Pos}) 采样 {c} ≠ 期望 {expect}");
        }
    }

    [Test]
    public void ElevationStops_Above6000_IsPureWhite()
    {
        var c = ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, 10000f);
        Assert.IsTrue(c.IsEqualApprox(new Color(0.98f, 0.99f, 1.00f)));
    }

    [Test]
    public void ElevationStops_SeaLandStep_AtZero()
    {
        // 0m 硬台阶：海侧青白 → 陆侧天蓝（色相错开原则）
        var sea = ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, -0.5f);
        var land = ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, 0f);
        Assert.Greater(sea.R, 0.85f, "海侧应接近青白（R 高）");
        Assert.Greater(sea.B, 0.90f, "海侧应接近青白（B 高）");
        Assert.IsTrue(land.IsEqualApprox(new Color(0.45f, 0.75f, 0.90f)));
        Assert.Less(land.R, sea.R, "陆地天蓝 R 应显著低于海洋青白（色相错开）");
    }

    [Test]
    public void ElevationStops_Ocean_DarkensWithDepth()
    {
        // 明度随深度单调下降：-100 > -1000 > -4000 > -7000（越深越暗）
        static float L(Color c) => c.R + c.G + c.B;
        var s100 = L(ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, -100f));
        var s1000 = L(ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, -1000f));
        var s4000 = L(ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, -4000f));
        var s7000 = L(ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, -7000f));
        Assert.Greater(s100, s1000, "浅海应亮于大陆坡");
        Assert.Greater(s1000, s4000, "大陆坡应亮于深海平原");
        Assert.Greater(s4000, s7000, "深海平原应亮于海沟");
    }

    [Test]
    public void ElevationStops_DeepTrench_Below8000_IsDarkest()
    {
        var c = ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, -12000f);
        Assert.IsTrue(c.IsEqualApprox(new Color(0.043f, 0.055f, 0.133f)));
        Assert.Less(c.R, 0.05f, "墨紫黑应极暗");
    }

    // ── RampSampleSmooth（三次贝塞尔/Catmull-Rom）行为 ────────────────

    [Test]
    public void RampSampleSmooth_SegmentMidpoint_IsBetweenEnds()
    {
        // 中海拔段中点（1250m）：应处于浅绿与金黄之间（黄通道高于绿段起点、绿通道尚存）
        var c = ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, 1250f);
        Assert.Greater(c.R, 0.55f, "向金黄过渡 R 应上升");
        Assert.Greater(c.G, 0.40f, "过渡中段 G 高于浅绿起点下限");
        Assert.Less(c.G, 0.85f, "过渡中段 G 低于金黄终点上限");
        Assert.Less(c.B, 0.50f, "B 应已衰减（远离天蓝）");
    }

    [Test]
    public void RampSampleSmooth_ClampsBelowFirst_ToFirstColor()
    {
        var c = ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, -99999f);
        Assert.IsTrue(c.IsEqualApprox(new Color(0.043f, 0.055f, 0.133f)));
    }

    [Test]
    public void RampSampleSmooth_AnyValue_IsValidColor()
    {
        // 全域扫描：Catmull-Rom 外推须被 clamp，颜色分量恒在 [0,1]
        for (float m = -12000f; m <= 9000f; m += 17f)
        {
            var c = ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, m);
            Assert.GreaterOrEqual(c.R, 0f, $"m={m} R 低于 0: {c.R}");
            Assert.LessOrEqual(c.R, 1f, $"m={m} R 越界 {c.R}");
            Assert.GreaterOrEqual(c.G, 0f, $"m={m} G 低于 0: {c.G}");
            Assert.LessOrEqual(c.G, 1f, $"m={m} G 越界 {c.G}");
            Assert.GreaterOrEqual(c.B, 0f, $"m={m} B 低于 0: {c.B}");
            Assert.LessOrEqual(c.B, 1f, $"m={m} B 越界 {c.B}");
        }
    }

    [Test]
    public void RampSampleSmooth_StepStop_StillHardCut()
    {
        // 平滑模式不破坏 0m 海陆台阶：海侧青白 → 陆侧天蓝 硬切
        var sea = ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, -0.01f);
        var land = ColorRamp.RampSampleSmooth(ElevationLayer.ElevationStops, 0f);
        Assert.Greater(sea.B, 0.90f);
        Assert.IsTrue(land.IsEqualApprox(new Color(0.45f, 0.75f, 0.90f)));
    }

    // ── 温度/降水线性色带与旧实现逐点等价（未随海拔改版）────────────────

    [Test]
    public void RampSample_Temperature_MatchesLegacyLogic()
    {
        // 旧 BiomeColors.TemperatureToColor 二分分段逻辑（2026-08-31 重构前快照）
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
            var got = ColorRamp.RampSample(BiomeColors.TempStops, t);
            Assert.IsTrue(got.IsEqualApprox(Legacy(t)), $"温度 {t}°C 不等价: got={got} legacy={Legacy(t)}");
        }
    }

    [Test]
    public void RampSample_Precipitation_MatchesLegacyLogic()
    {
        // 旧 PrecipitationLayer：x 归一化后两色 Lerp
        static Color Legacy(float x)
            => new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), Mathf.Clamp(x, 0f, 1f));

        for (float x = -0.5f; x <= 1.5f; x += 0.01f)
        {
            var got = ColorRamp.RampSample(PrecipitationLayer.PrecipStops, x);
            Assert.IsTrue(got.IsEqualApprox(Legacy(x)), $"归一化 {x} 不等价: got={got} legacy={Legacy(x)}");
        }
    }

    // ── RampLegendColors（图例与画面同源）──────────────────────────────

    [Test]
    public void RampLegendColors_Elevation_Produces11Colors()
    {
        var colors = ColorRamp.RampLegendColors(ElevationLayer.ElevationStops);
        Assert.AreEqual(11, colors.Length);   // 墨紫黑/靛蓝/深蓝/灰蓝/浅灰蓝/青白/天蓝/浅绿/金黄/赭石/白
        Assert.IsTrue(colors[0].IsEqualApprox(new Color(0.043f, 0.055f, 0.133f)));
        Assert.IsTrue(colors[1].IsEqualApprox(new Color(0.090f, 0.169f, 0.310f)));
        Assert.IsTrue(colors[2].IsEqualApprox(new Color(0.184f, 0.357f, 0.541f)));
        Assert.IsTrue(colors[3].IsEqualApprox(new Color(0.482f, 0.647f, 0.769f)));
        Assert.IsTrue(colors[4].IsEqualApprox(new Color(0.776f, 0.863f, 0.922f)));
        Assert.IsTrue(colors[5].IsEqualApprox(new Color(0.906f, 0.941f, 0.965f)));
        Assert.IsTrue(colors[6].IsEqualApprox(new Color(0.45f, 0.75f, 0.90f)));
        Assert.IsTrue(colors[7].IsEqualApprox(new Color(0.58f, 0.78f, 0.32f)));
        Assert.IsTrue(colors[8].IsEqualApprox(new Color(0.93f, 0.78f, 0.25f)));
        Assert.IsTrue(colors[9].IsEqualApprox(new Color(0.55f, 0.36f, 0.20f)));
        Assert.IsTrue(colors[10].IsEqualApprox(new Color(0.98f, 0.99f, 1.00f)));
    }

    [Test]
    public void RampLegendColors_Precipitation_TwoColors()
    {
        var colors = ColorRamp.RampLegendColors(PrecipitationLayer.PrecipStops);
        Assert.AreEqual(2, colors.Length);
    }

    // ── 色带数据完整性 ────────────────────────────────────────────────

    [Test]
    public void RampStops_AreAscendingAndAtLeastTwo()
    {
        AssertStopsValid(ElevationLayer.ElevationStops);
        AssertStopsValid(BiomeColors.TempStops);
        AssertStopsValid(PrecipitationLayer.PrecipStops);
    }

    private static void AssertStopsValid(ColorRamp.ColorStop[] stops)
    {
        Assert.GreaterOrEqual(stops.Length, 2);
        for (int i = 1; i < stops.Length; i++)
            Assert.GreaterOrEqual(stops[i].Pos, stops[i - 1].Pos, $"停点 {i} 未升序");
    }
}
