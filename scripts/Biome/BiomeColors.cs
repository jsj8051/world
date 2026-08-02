using Godot;

namespace World.Biome;

/// <summary>
/// 生物群系 / 气候图层色板（策略地图色块风，纯函数）。
/// </summary>
public static class BiomeColors
{
    public static Color BiomeToColor(BiomeType b) => b switch
    {
        BiomeType.DeepOcean => new Color(0.02f, 0.10f, 0.25f),
        BiomeType.Ocean => new Color(0.06f, 0.35f, 0.60f),
        BiomeType.IceCap => new Color(0.92f, 0.96f, 1.00f),
        BiomeType.Tundra => new Color(0.55f, 0.62f, 0.55f),
        BiomeType.Taiga => new Color(0.14f, 0.34f, 0.17f),
        BiomeType.ColdDesert => new Color(0.60f, 0.56f, 0.50f),
        BiomeType.TemperateForest => new Color(0.24f, 0.52f, 0.18f),
        BiomeType.TemperateGrassland => new Color(0.62f, 0.68f, 0.28f),
        BiomeType.Desert => new Color(0.80f, 0.72f, 0.45f),
        BiomeType.Savanna => new Color(0.72f, 0.62f, 0.22f),
        BiomeType.TropicalForest => new Color(0.08f, 0.40f, 0.14f),
        BiomeType.TropicalDryForest => new Color(0.42f, 0.52f, 0.18f),
        BiomeType.Alpine => new Color(0.58f, 0.55f, 0.52f),
        _ => Colors.Magenta,
    };

    /// <summary>温度色板（分段，非线性）：
    /// ⚠️ 2026-08-02 改为分段色带——线性归一化(-85..45)会把常见温度区间压缩（用户指出
    ///   "常见温度和极端温度差距很大，直接归一化不能查看敏感温度区间"）。
    ///   分段分配：极寒 -85~-30 占 25%，冰点区 -30~0 占 20%，0~15 占 20%，
    ///   宜居带 15~30 占 20%，高温 30~45 占 15% → 常见区(-30..+30)拿到 60% 色带分辨率。
    /// 断点/位置/颜色三段平行数组，插值用 FieldOps.Lerp 同款逻辑。</summary>
    private static readonly float[] TempBreaks =
        { -85f, -30f, 0f, 15f, 30f, 45f };
    private static readonly Color[] TempColors =
    {
        new(0.08f, 0.12f, 0.45f),   // -85 极深蓝（极寒）
        new(0.10f, 0.28f, 0.62f),   // -30 深蓝
        new(0.22f, 0.52f, 0.72f),   // 0  蓝青（冰点附近细粒度）
        new(0.38f, 0.72f, 0.42f),   // 15 绿
        new(0.92f, 0.78f, 0.28f),   // 30 黄（宜居带）
        new(0.88f, 0.30f, 0.15f),   // 45 红（高温）
    };

    public static Color TemperatureToColor(float t)
    {
        // 二分找段，段内线性插值（断点按位置映射，段内颜色线性）
        int seg = -1;
        for (int i = 0; i < TempBreaks.Length - 1; i++)
        {
            if (t >= TempBreaks[i] && t <= TempBreaks[i + 1]) { seg = i; break; }
        }
        if (seg < 0)
            return t < TempBreaks[0] ? TempColors[0] : TempColors[^1];
        float f = (t - TempBreaks[seg]) / (TempBreaks[seg + 1] - TempBreaks[seg]);
        return TempColors[seg].Lerp(TempColors[seg + 1], f);
    }

    /// <summary>降水色板：0mm 黄 → 2000mm+ 深蓝。</summary>
    public static Color PrecipitationToColor(float p)
    {
        float x = Mathf.Clamp(p / 2000f, 0f, 1f);
        return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    }
}
