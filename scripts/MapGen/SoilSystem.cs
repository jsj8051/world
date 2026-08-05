using Godot;
using World.Biome;

namespace World.MapGen;

/// <summary>
/// 土壤肥力系统（2026-08-03）：每格农业产出系数 1-5 级（存档 1 字节；0=海洋）。
///
/// 肥力 = biome 基础(1-5)
///      + 冲积加成（Riparian 河岸带——尼罗河模式，最肥沃）
///      + 火山加成（MaficVolcanic 火山岩风化——印尼/日本）
///      − 坡度惩罚（陡坡水土流失/薄土）
///      − 气候惩罚（极干 &lt;150mm / 极寒 &lt;−10°C / 极湿淋溶 &gt;3000mm）
///      → clamp 1..5
///
/// 用途：粮食产出修正、城市富饶区选址、粮食贸易（人文层）。
/// 纯计算（无 Godot 对象），生成时后台线程安全。
/// </summary>
public static class SoilSystem
{
    /// <summary>biome 基础肥力（1-5；海洋/不可耕 = 0）。</summary>
    public static int BiomeBase(byte biome)
    {
        return biome switch
        {
            (byte)BiomeType.Riparian => 5,             // 河岸冲积带（最肥沃）
            (byte)BiomeType.TemperateGrassland => 4,   // 黑土草原（乌克兰/大平原）
            (byte)BiomeType.TemperateForest => 4,      // 温带森林腐殖质
            (byte)BiomeType.Taiga => 2,                // 针叶林酸性土
            (byte)BiomeType.Savanna => 3,              // 稀树草原
            (byte)BiomeType.TropicalForest => 3,       // 雨林淋溶中偏贫
            (byte)BiomeType.TropicalDryForest => 3,    // 季雨林
            (byte)BiomeType.Tundra => 1,               // 冻土苔原
            (byte)BiomeType.ColdDesert => 1,           // 寒漠
            (byte)BiomeType.Desert => 1,               // 荒漠
            (byte)BiomeType.IceCap => 1,               // 冰原（不可耕）
            (byte)BiomeType.Alpine => 1,               // 高山薄土
            // ── 柯本细类（2026-08-16）──
            (byte)BiomeType.TropicalRainforest => 3,   // 雨林淋溶中偏贫
            (byte)BiomeType.TropicalMonsoon => 3,      // 季雨林
            (byte)BiomeType.TropicalSavanna => 3,      // 稀树草原
            (byte)BiomeType.HotDesert => 1,            // 荒漠
            (byte)BiomeType.ColdDesertKoppen => 1,     // 寒漠
            (byte)BiomeType.HotSteppe => 3,            // 半干旱草原
            (byte)BiomeType.ColdSteppe => 3,           // 冷草原（黑土草原）
            (byte)BiomeType.HumidSubtropical => 4,     // 亚热带森林腐殖质
            (byte)BiomeType.Oceanic => 4,              // 海洋性温带森林
            (byte)BiomeType.MonsoonSubtropical => 4,   // 冬干亚热带（季风冲积）
            (byte)BiomeType.MediterraneanHot => 3,     // 地中海硬叶灌丛土
            (byte)BiomeType.MediterraneanCool => 3,
            (byte)BiomeType.ContinentalHot => 4,       // 大陆黑钙土
            (byte)BiomeType.ContinentalWarm => 3,
            (byte)BiomeType.Subarctic => 2,            // 针叶林酸性土（同 Taiga）
            (byte)BiomeType.ContinentalDry => 2,       // 冬干大陆
            _ => 0,                                    // 海洋
        };
    }

    /// <summary>土壤肥力标注：每格 1 字节（0=海洋，1-5=肥力级）。
    /// ⚠️ 2026-08-03：坡度/气候惩罚用【分位数相对阈值】（该星球分布 P15/P80/P95/P10）——
    ///   固定阈值（150mm/-10°C/0.03 坡度）绑定特定星球，湿润/干旱/高坡星球失效（同矿藏教训）。</summary>
    public static void ComputeSoil(
        float[] elevNorm, byte[] biome, float[] precip, float[] temp,
        float[] maficVolcanic, int[] flow, out byte[] soil)
    {
        int n = elevNorm.Length;
        soil = new byte[n];

        // ── 分位预计算（该星球相对分布）──
        float slopeP80 = 0f, slopeP95 = float.MaxValue, precipP15 = float.MinValue, precipP30 = float.MinValue,
              precipP95 = float.MaxValue, tempP10 = float.MinValue;
        var slopeArr = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (flow != null && flow[i] >= 0 && flow[i] < n)
                slopeArr[i] = Mathf.Max(0f, elevNorm[i] - elevNorm[flow[i]]);
        }
        slopeP80 = Percentile(slopeArr, 0.80f);
        slopeP95 = Percentile(slopeArr, 0.95f);
        if (precip != null) { precipP15 = Percentile(precip, 0.15f); precipP30 = Percentile(precip, 0.30f); precipP95 = Percentile(precip, 0.95f); }
        if (temp != null) tempP10 = Percentile(temp, 0.10f);

        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) continue;            // 海洋
            int f = BiomeBase(biome[i]);
            if (f <= 0) continue;

            // 火山加成：镁铁质火山岩（风化 → 肥沃火山土）
            if (maficVolcanic != null && maficVolcanic[i] > 0.1f)
                f++;

            // 坡度惩罚（相对：>P80 陡坡 −1；>P95 极陡 −2——水土流失/薄土）
            if (slopeArr[i] > slopeP95) f -= 2;
            else if (slopeArr[i] > slopeP80) f -= 1;

            // 气候惩罚（相对分位）：极干 <P15 −2 / 干 <P30 −1 / 极寒 <P10 −2 / 极湿淋溶 >P95 −1
            if (precip != null && precip[i] < precipP15) f -= 2;
            else if (precip != null && precip[i] < precipP30) f -= 1;
            if (temp != null && temp[i] < tempP10) f -= 2;
            if (precip != null && precip[i] > precipP95) f -= 1;

            soil[i] = (byte)Mathf.Clamp(f, 1, 5);
        }
    }

    /// <summary>数组第 p 分位数（排序；p∈0..1）。</summary>
    private static float Percentile(float[] arr, float p)
    {
        var copy = (float[])arr.Clone();
        System.Array.Sort(copy);
        int idx = Mathf.Clamp((int)(copy.Length * p), 0, copy.Length - 1);
        return copy[idx];
    }
}
