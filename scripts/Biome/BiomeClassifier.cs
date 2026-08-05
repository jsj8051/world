using System;

namespace World.Biome;

/// <summary>
/// 生物群系分类器（柯本气候分类 Köppen–Geiger，纯函数，线程安全）。
/// 输入：归一化海拔、年均温、年降水 + 【真实月数据】（MonsoonSystem 产出：最热月/最冷月均温、
/// 最干月降水 + 最干月月份）。2026-08-16 起不再内部推导月尺度——季风环流诊断场直接给真实值。
///
/// 判据标准（Kottek et al. 2006, Köppen–Geiger）：
///   · B 带：P_thr = 2×(T+14)（夏雨型/冬干）/ 2×T（冬雨型/夏干）/ 2×(T+7)（均匀型，无干季）；
///     BW = P &lt; 5×P_thr，BS = 5×P_thr ≤ P &lt; 10×P_thr。
///   · A 带（最冷月≥18°C）：Af = 最干月≥60mm；Am = 最干月&lt;60 且 P ≥ 100cm − 最干月/25；Aw = 其余。
///   · E 带（最热月&lt;10°C）：ET = 最热月≥0°C，EF = 最热月&lt;0°C。
///   · D 带（最冷月≤−3°C）：f = 最干月≥30mm，w = 最干月&lt;30mm；a = 最热月≥22，b = 10~22。
///   · C 带（最冷月>−3°C）：f = 最干月≥30mm，w = 冬干，s = 夏干（地中海）。
/// </summary>
public static class BiomeClassifier
{
    /// <summary>海拔归一化值 &lt; 此值 → 深海。</summary>
    public const float DeepOceanLevel = -0.1f;
    /// <summary>海拔归一化值 &lt; 此值 → 海洋。</summary>
    public const float OceanLevel = 0.02f;
    /// <summary>高于此归一化海拔（-1..1）→ 山地（Alpine 或冰雪）。0.5 ≈ 5km。</summary>
    public const float AlpineLevel = 0.5f;

    public static BiomeType Classify(float elevNorm, float tempC, float precipMm,
        float tHot, float tCold, float dryMonth, int dryMonthIndex = 3, float latDeg = 0f)
    {
        // ── 海洋（温度分带）──
        if (elevNorm < DeepOceanLevel)
            return BiomeType.DeepOcean;
        if (elevNorm < OceanLevel)
        {
            if (tempC < -2f) return BiomeType.FrigidOcean;   // 海冰带（海水冰点 -1.8°C）
            if (tempC >= 18f) return BiomeType.TropicalOcean;
            return BiomeType.Ocean;                          // 温带海洋
        }

        // ── 高海拔山地（垂直带覆盖一切）──
        if (elevNorm > AlpineLevel)
            return tempC < -8f ? BiomeType.IceCap : BiomeType.Alpine;

        // 干季季节：最干月在冬季 = 夏雨型（降水集中夏季）；在夏季 = 冬雨型（地中海式）
        bool dryInWinter;
        if (latDeg >= 0f) dryInWinter = dryMonthIndex >= 11 || dryMonthIndex <= 1;  // 北半球冬季
        else dryInWinter = dryMonthIndex >= 5 && dryMonthIndex <= 7;                // 南半球冬季

        // ── B 带（干旱，覆盖一切；阈值随季节型修正，Kottek 2006）──
        float pThr;
        if (dryMonth >= 30f) pThr = 2f * (tempC + 7f);        // 无明显干季 → 均匀型
        else pThr = dryInWinter ? 2f * (tempC + 14f)          // 夏雨型（冬干）
                                : 2f * tempC;                 // 冬雨型（夏干，地中海）
        if (precipMm < 5f * pThr)         // BW：沙漠
            return tempC >= 18f ? BiomeType.HotDesert : BiomeType.ColdDesertKoppen;
        if (precipMm < 10f * pThr)        // BS：半干旱草原
            return tempC >= 18f ? BiomeType.HotSteppe : BiomeType.ColdSteppe;

        if (tCold >= 18f)                 // A 带：热带（真实最冷月 ≥18°C）
        {
            if (dryMonth >= 60f) return BiomeType.TropicalRainforest;   // Af
            // Am/Aw 分界：P ≥ 100cm − 最干月/25（100 是 cm → 1000mm；最干月 cm/25 = mm/2.5）
            if (precipMm >= 1000f - dryMonth / 2.5f) return BiomeType.TropicalMonsoon;  // Am
            return BiomeType.TropicalSavanna;                            // Aw
        }

        if (tHot < 10f)                   // E 带：极地（真实最热月 <10°C）
            return tHot >= 0f ? BiomeType.Tundra : BiomeType.IceCap;    // ET / EF

        if (tCold <= -3f)                 // D 带：大陆性（f 判据 30mm，非 A 带 60mm）
        {
            if (dryMonth < 30f) return BiomeType.ContinentalDry;         // Dwa（冬干）
            if (tHot >= 22f) return BiomeType.ContinentalHot;            // Dfa
            if (tCold <= -15f) return BiomeType.Subarctic;               // Dfc（更冷 → 针叶林）
            return BiomeType.ContinentalWarm;                            // Dfb
        }
        // C 带：温带（f 判据 30mm）
        if (dryMonth < 30f)               // 冬干 w / 夏干 s
            return dryInWinter
                ? (tHot >= 22f ? BiomeType.MonsoonSubtropical : BiomeType.Oceanic)       // Cwa / Cwb→Oceanic
                : (tHot >= 22f ? BiomeType.MediterraneanHot : BiomeType.MediterraneanCool); // Csa / Csb
        return tHot >= 22f ? BiomeType.HumidSubtropical : BiomeType.Oceanic;  // Cfa / Cfb
    }
}
