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

    /// <summary>温度色板：-30°C 深蓝 → 35°C 红。</summary>
    public static Color TemperatureToColor(float t)
    {
        float x = Mathf.Clamp((t + 30f) / 65f, 0f, 1f);
        if (x < 0.25f) return new Color(0.10f, 0.20f, 0.60f).Lerp(new Color(0.20f, 0.45f, 0.75f), x / 0.25f);
        if (x < 0.50f) return new Color(0.20f, 0.45f, 0.75f).Lerp(new Color(0.35f, 0.70f, 0.40f), (x - 0.25f) / 0.25f);
        if (x < 0.75f) return new Color(0.35f, 0.70f, 0.40f).Lerp(new Color(0.95f, 0.80f, 0.30f), (x - 0.50f) / 0.25f);
        return new Color(0.95f, 0.80f, 0.30f).Lerp(new Color(0.85f, 0.25f, 0.15f), (x - 0.75f) / 0.25f);
    }

    /// <summary>降水色板：0mm 黄 → 2000mm+ 深蓝。</summary>
    public static Color PrecipitationToColor(float p)
    {
        float x = Mathf.Clamp(p / 2000f, 0f, 1f);
        return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    }
}
