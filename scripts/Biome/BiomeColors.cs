using Godot;
using World.Utils;
using static World.Utils.ColorRamp;

namespace World.Biome;

/// <summary>
/// 生物群系 / 气候图层色板（策略地图色块风，纯函数）。
/// 2026-08-31：温度色带定义内聚本模块（气候概念的家——温度/月温度两图层共用，见 TempStops）。
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

    // ── 温度色带（气候模块定义处；TemperatureLayer 与 MonthTempLayer（月份视图）共用）──

    /// <summary>温度连续色带（位置=°C；2026-08-02 分段色带——常见温度区间拿更高色带分辨率：
    /// 极寒 -85~-30 占 25%，冰点区 -30~0 占 20%，0~15 占 20%，宜居带 15~30 占 20%，高温 30~45 占 15%，
    /// → 常见区(-30..+30)拿到 60% 色带分辨率。【改色带】= 编辑下方点位（异位置=线性渐变）。</summary>
    public static readonly ColorStop[] TempStops =
    {
        new(-85f, new Color(0.08f, 0.12f, 0.45f)),  // 极寒
        new(-30f, new Color(0.10f, 0.28f, 0.62f)),  // 深蓝
        new(0f,   new Color(0.22f, 0.52f, 0.72f)),  // 冰点
        new(15f,  new Color(0.38f, 0.72f, 0.42f)),  // 绿
        new(30f,  new Color(0.92f, 0.78f, 0.28f)),  // 黄（宜居带）
        new(45f,  new Color(0.88f, 0.30f, 0.15f)),  // 红（高温）
    };

    /// <summary>温度色板取色（2026-08-02 起为分段色带；2026-08-31 定义收编 TempStops + ColorRamp 采样）。</summary>
    public static Color TemperatureToColor(float t)
        => RampSample(TempStops, t);

    /// <summary>[已废弃 2026-08-31] 固定 2000mm 降水色板——PrecipitationLayer/MonthPrecipLayer
    /// 已按用户拍板改走陆地 min-max 自适应归一化（PrecipitationLayer.PrecipStops），本方法仅由
    /// 单元测试与遗留参考保留，勿再用于新代码。</summary>
    public static Color PrecipitationToColor(float p)
    {
        float x = Mathf.Clamp(p / 2000f, 0f, 1f);
        return new Color(0.90f, 0.80f, 0.40f).Lerp(new Color(0.10f, 0.30f, 0.70f), x);
    }
}
