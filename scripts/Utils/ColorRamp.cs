using Godot;
using System;

namespace World.Utils;

/// <summary>连续色带通用工具（2026-08-31 迁入 World.Utils——通用算法之家；业务色带定义
/// 内聚各归属处：ElevationLayer.ElevationStops / BiomeColors.TempStops / PrecipitationLayer.PrecipStops，
/// 本类只含采样与图例算法，不含业务色）。</summary>
public static class ColorRamp
{
    /// <summary>连续色带停点（坐标=物理域：海拔米 / 温度°C / 降水归一化 0..1）。
    /// 改色带 = 增删/改这里：同位置双停点 = 硬台阶（无渐变），异位置 = 渐变。</summary>
    public readonly record struct ColorStop(float Pos, Color C);

    /// <summary>连续色带采样（线性）：v 在停点间线性插值；越界 clamp 到两端色；【同位置双停点 = 硬台阶】
    /// （海拔 -200m 深海→浅海、0m 浅海→沙 的台阶由两对同位置停点实现，无渐变）。
    /// ⚠️ 停点必须按 Pos 升序且 ≥2 个；段内插值逐点线性。</summary>
    public static Color RampSample(ColorStop[] stops, float v)
    {
        if (v < stops[0].Pos) return stops[0].C;
        for (int i = 0; i < stops.Length - 1; i++)
        {
            var a = stops[i];
            var b = stops[i + 1];
            if (v < b.Pos)   // 半开区间 [a.Pos, b.Pos)：端点归属右侧段
            {
                float span = b.Pos - a.Pos;
                if (span <= 1e-6f) return b.C;   // 同位置双停点 → 硬台阶（取右侧色）
                return a.C.Lerp(b.C, (v - a.Pos) / span);
            }
        }
        return stops[^1].C;   // ≥ 末停点 → 末色
    }

    /// <summary>连续色带采样（三次平滑——Catmull-Rom，即三次贝塞尔形式；2026-08-31 用户指定
    /// 海拔色带用三次贝塞尔过渡）：段内用相邻 4 停点做三次插值（段端颜色严格经过停点色，
    /// 段间切线连续），RGB 分量各自插值并 clamp [0,1]；越界/同位置台阶行为与 RampSample 一致。
    /// ⚠️ 相同输入下与 RampSample 的逐点值不同（平滑 ≠ 线性）——选哪种按色带语义定。</summary>
    public static Color RampSampleSmooth(ColorStop[] stops, float v)
    {
        if (v < stops[0].Pos) return stops[0].C;
        for (int i = 0; i < stops.Length - 1; i++)
        {
            var a = stops[i];
            var b = stops[i + 1];
            if (v < b.Pos)   // 半开区间 [a.Pos, b.Pos)：端点归属右侧段
            {
                float span = b.Pos - a.Pos;
                if (span <= 1e-6f) return b.C;   // 同位置双停点 → 硬台阶（取右侧色）
                float t = (v - a.Pos) / span;
                var p0 = stops[Math.Max(i - 1, 0)].C;                     // 前邻（越界折首）
                var p1 = a.C;                                              // 段首（t=0 恒等）
                var p2 = b.C;                                              // 段末（t→1 恒等）
                var p3 = stops[Math.Min(i + 2, stops.Length - 1)].C;       // 后邻（越界折尾）
                return CatmullRom(p0, p1, p2, p3, t);
            }
        }
        return stops[^1].C;   // ≥ 末停点 → 末色
    }

    /// <summary>Catmull-Rom 三次插值（RGBA 逐分量；clamp [0,1] 防控制点外推越界——首尾段
    /// 会因 p0/p3 折边产生轻微外推，clamp 保证合法颜色）。</summary>
    private static Color CatmullRom(Color p0, Color p1, Color p2, Color p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        float w0 = -0.5f * t3 + t2 - 0.5f * t;
        float w1 = 1.5f * t3 - 2.5f * t2 + 1f;
        float w2 = -1.5f * t3 + 2f * t2 + 0.5f * t;
        float w3 = 0.5f * t3 - 0.5f * t2;
        return new Color(
            Mathf.Clamp(p0.R * w0 + p1.R * w1 + p2.R * w2 + p3.R * w3, 0f, 1f),
            Mathf.Clamp(p0.G * w0 + p1.G * w1 + p2.G * w2 + p3.G * w3, 0f, 1f),
            Mathf.Clamp(p0.B * w0 + p1.B * w1 + p2.B * w2 + p3.B * w3, 0f, 1f),
            Mathf.Clamp(p0.A * w0 + p1.A * w1 + p2.A * w2 + p3.A * w3, 0f, 1f));
    }

    /// <summary>图例色序列：每段首色（连续重复折叠）+ 末停点色——与画面同一份色带；
    /// 带温度叠加维的色带由调用方自行处理末尾。</summary>
    public static Color[] RampLegendColors(ColorStop[] stops)
    {
        var list = new System.Collections.Generic.List<Color>(stops.Length + 1);
        for (int i = 0; i < stops.Length - 1; i++)
        {
            var c = stops[i].C;
            if (list.Count == 0 || list[^1] != c) list.Add(c);
        }
        list.Add(stops[^1].C);
        return list.ToArray();
    }
}
