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
        BiomeType.Alpine => new Color(0.58f, 0.55f, 0.52f),
        BiomeType.Riparian => new Color(0.20f, 0.72f, 0.38f),   // 河岸带：翠绿（沙漠绿洲线）
        // ── 柯本细类（2026-08-16）──
        BiomeType.TropicalRainforest => new Color(0.05f, 0.45f, 0.12f),  // Af 深绿雨林
        BiomeType.TropicalMonsoon => new Color(0.12f, 0.52f, 0.18f),     // Am 绿
        BiomeType.TropicalSavanna => new Color(0.68f, 0.60f, 0.20f),     // Aw 黄绿
        BiomeType.HotDesert => new Color(0.86f, 0.75f, 0.45f),           // BWh 沙黄
        BiomeType.ColdDesertKoppen => new Color(0.66f, 0.61f, 0.48f),    // BWk 灰黄
        BiomeType.HotSteppe => new Color(0.74f, 0.66f, 0.30f),           // BSh 橙黄（稀树灌丛）
        BiomeType.ColdSteppe => new Color(0.55f, 0.63f, 0.40f),          // BSk 灰绿草原
        BiomeType.HumidSubtropical => new Color(0.30f, 0.60f, 0.22f),    // Cfa 亮绿
        BiomeType.Oceanic => new Color(0.18f, 0.48f, 0.20f),             // Cfb 深绿
        BiomeType.MonsoonSubtropical => new Color(0.50f, 0.60f, 0.22f),  // Cwa 绿黄
        BiomeType.MediterraneanHot => new Color(0.58f, 0.55f, 0.25f),    // Csa 橄榄
        BiomeType.MediterraneanCool => new Color(0.66f, 0.63f, 0.35f),   // Csb 浅橄榄
        BiomeType.ContinentalHot => new Color(0.28f, 0.55f, 0.25f),      // Dfa 绿
        BiomeType.ContinentalWarm => new Color(0.22f, 0.50f, 0.30f),     // Dfb 绿蓝
        BiomeType.Subarctic => new Color(0.15f, 0.40f, 0.28f),           // Dfc 蓝绿针叶
        BiomeType.ContinentalDry => new Color(0.55f, 0.58f, 0.32f),      // Dwa 黄绿灰
        BiomeType.FrigidOcean => new Color(0.55f, 0.72f, 0.88f),         // 极地海冰淡蓝
        BiomeType.TropicalOcean => new Color(0.05f, 0.55f, 0.65f),       // 热带海洋青蓝
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
