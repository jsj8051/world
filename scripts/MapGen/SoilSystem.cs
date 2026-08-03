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
            _ => 0,                                    // 海洋
        };
    }

    /// <summary>土壤肥力标注：每格 1 字节（0=海洋，1-5=肥力级）。</summary>
    public static void ComputeSoil(
        float[] elevNorm, byte[] biome, float[] precip, float[] temp,
        float[] maficVolcanic, int[] flow, out byte[] soil)
    {
        int n = elevNorm.Length;
        soil = new byte[n];
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) continue;            // 海洋
            int f = BiomeBase(biome[i]);
            if (f <= 0) continue;

            // 火山加成：镁铁质火山岩（风化 → 肥沃火山土）
            if (maficVolcanic != null && maficVolcanic[i] > 0.1f)
                f++;

            // 坡度惩罚：与下游高差（陡坡水土流失/薄土）
            if (flow != null && flow[i] >= 0 && flow[i] < n)
            {
                float slope = Mathf.Max(0f, elevNorm[i] - elevNorm[flow[i]]);
                if (slope > 0.06f) f -= 2;
                else if (slope > 0.03f) f -= 1;
            }

            // 气候惩罚：极干 / 极寒 / 极湿淋溶
            if (precip != null && precip[i] < 150f) f -= 2;       // 荒漠勉强耕作
            if (temp != null && temp[i] < -10f) f -= 2;           // 冻土短生长期
            else if (precip != null && precip[i] > 3000f) f -= 1; // 雨林养分流失

            soil[i] = (byte)Mathf.Clamp(f, 1, 5);
        }
    }
}
