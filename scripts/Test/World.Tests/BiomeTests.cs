using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using NUnit.Framework;
using World.Domain;
using World.HexPlanet;

namespace World.Tests;

/// <summary>
/// Biome 模块补充测试（L0 纯逻辑；与 BiomeClimateTests 互补，不重复其覆盖面）。
/// 聚焦四类增量：
///   1. BiomeClassifier 阈值的【恰好等于边界】——按源码 &lt;/&gt;= 严格语义断言（哪些值落在哪边是契约）；
///   2. BiomeType 枚举穷举（byte 基类型 / 0-31 / 无重复）与 BiomeColors 边界连续性；
///   3. MonsoonSystem：Compute 依赖 ClimateGenerator（构造即建 FastNoiseLite = 引擎类，
///      测试进程必崩 0xC0000005）→ 不测；其内部唯一纯静态辅助 TraceUpstream（private）
///      用反射驱动（只做 Vector3 点积/比较，纯托管）。
///
/// 纪律：只用 [Test]/[TestCase(字面量)]；不写文件；不触碰 GD.*/LogService/FastNoiseLite。
/// </summary>
public class BiomeTests
{
    public void BiomeType_UnderlyingByte_AllValuesDistinctInRange()
    {
        // byte 直接写存档（0-31 全部有效）——基类型必须 byte
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(BiomeType)));

        var seen = new HashSet<byte>();
        var values = Enum.GetValues<BiomeType>();
        foreach (var b in values)
        {
            byte v = (byte)b;
            Assert.LessOrEqual(v, 31, $"值 {v} 超出 0-31 存档范围");
            Assert.True(seen.Add(v), $"枚举值 {v} 重复（序列化歧义）");
        }
        // 定义成员数为 24（0-3、12-13、14-31）；无未定义化石值混入
        Assert.AreEqual(24, values.Length);
    }

    // ═══════════════════════════════════════════════════════════════
    // BiomeColors —— 色板扩展边界（Magenta 兜底 / 断点连续性 / 单调性）
    // ═══════════════════════════════════════════════════════════════


    public void BiomeToColor_UndefinedValue_FallsBackToMagenta()
    {
        // default 兜底色：任何未定义值（含化石 4-11 与越界值）→ Magenta（存档契约：不得再产生）
        Assert.AreEqual(Colors.Magenta, BiomeColors.BiomeToColor((BiomeType)4));
        Assert.AreEqual(Colors.Magenta, BiomeColors.BiomeToColor((BiomeType)11));
        Assert.AreEqual(Colors.Magenta, BiomeColors.BiomeToColor((BiomeType)99));
    }


    public void BiomeToColor_AllDefinedBiomes_HaveDistinctColors()
    {
        // 每个已知枚举都有专色（!= Magenta），且两两不共享同一色
        var keys = new HashSet<string>();
        foreach (var b in Enum.GetValues<BiomeType>())
        {
            Color c = BiomeColors.BiomeToColor(b);
            Assert.AreNotEqual(Colors.Magenta, c, $"枚举 {b} 不应落入默认 Magenta 兜底色");
            string key = $"{c.R:F4},{c.G:F4},{c.B:F4}";
            Assert.True(keys.Add(key), $"颜色与其它生物群系重复：{b} → {key}");
        }
        Assert.AreEqual(24, keys.Count);
    }


    public void TemperatureToColor_Breakpoints_ExactContinuity()
    {
        // 每个断点恰好落在线性段的 f=1 端点（= 下一段首色）→ 跨断点颜色连续
        AssertColor(BiomeColors.TemperatureToColor(-85f), 0.08f, 0.12f, 0.45f); // 最低断点 = 第一色
        AssertColor(BiomeColors.TemperatureToColor(-30f), 0.10f, 0.28f, 0.62f);
        AssertColor(BiomeColors.TemperatureToColor(0f), 0.22f, 0.52f, 0.72f);
        AssertColor(BiomeColors.TemperatureToColor(15f), 0.38f, 0.72f, 0.42f);
        AssertColor(BiomeColors.TemperatureToColor(30f), 0.92f, 0.78f, 0.28f);
        AssertColor(BiomeColors.TemperatureToColor(45f), 0.88f, 0.30f, 0.15f); // 最高断点 = 最后一色
        // 跨断点连续性：±小量两侧几乎同色（每通道差 &lt; 1e-3）
        foreach (float br in new[] { -30f, 0f, 15f, 30f })
        {
            Color a = BiomeColors.TemperatureToColor(br - 0.01f);
            Color b = BiomeColors.TemperatureToColor(br + 0.01f);
            Assert.AreEqual(a.R, b.R, 1e-3f, $"R 通道在断点 {br} 不连续");
            Assert.AreEqual(a.G, b.G, 1e-3f, $"G 通道在断点 {br} 不连续");
            Assert.AreEqual(a.B, b.B, 1e-3f, $"B 通道在断点 {br} 不连续");
        }
    }


    public void TemperatureToColor_MidSegment_LinearInterpolation()
    {
        // 0~15 段中点 f=0.5：c2=(0.22,0.52,0.72) 与 c3=(0.38,0.72,0.42) 线性混合
        AssertColor(BiomeColors.TemperatureToColor(7.5f), 0.30f, 0.62f, 0.57f);
    }


    public void PrecipitationToColor_EndpointsAndClamp()
    {
        // 端点精确色；越界按 [0,2000] 夹取
        AssertColor(BiomeColors.PrecipitationToColor(0f), 0.90f, 0.80f, 0.40f);
        AssertColor(BiomeColors.PrecipitationToColor(2000f), 0.10f, 0.30f, 0.70f);
        AssertColor(BiomeColors.PrecipitationToColor(-500f), 0.90f, 0.80f, 0.40f);  // 负值夹到 0
        AssertColor(BiomeColors.PrecipitationToColor(99999f), 0.10f, 0.30f, 0.70f); // 超大值夹到 2000
    }


    public void PrecipitationToColor_MonotonicSeries()
    {
        // 黄(干) → 蓝(湿)：R、G 通道单调不增，B 通道单调不减（含夹取区）
        float[] ps = { 0f, 500f, 1000f, 1500f, 2000f, 3000f };
        Color prev = Colors.Transparent;
        for (int i = 0; i < ps.Length; i++)
        {
            Color c = BiomeColors.PrecipitationToColor(ps[i]);
            if (i > 0)
            {
                Assert.True(c.R <= prev.R + 1e-6f, $"R 应在 {ps[i]}mm 不升（{prev.R}→{c.R}）");
                Assert.True(c.G <= prev.G + 1e-6f, $"G 应在 {ps[i]}mm 不升");
                Assert.True(c.B >= prev.B - 1e-6f, $"B 应在 {ps[i]}mm 不降");
            }
            prev = c;
        }
    }

    // ═══════════════════════════════════════════════════════════════

    private static void AssertColor(Color c, float r, float g, float b, float tol = 5e-3f)
    {
        Assert.AreEqual(r, c.R, tol, "R 通道");
        Assert.AreEqual(g, c.G, tol, "G 通道");
        Assert.AreEqual(b, c.B, tol, "B 通道");
    }

}