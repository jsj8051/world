using Godot;
using System;
using World.Tectonics;

namespace World.MapGen;

/// <summary>
/// 矿藏系统（2026-08-02）：生成期标注，物理推断 + 确定性概率。
///
/// 每格最多 1 种矿（稀疏——"矿产不是到处都是"），富度 3 档（贫/富/巨型）。
/// 字节编码：mineral = (richness &lt;&lt; 4) | type（type 0=无 1-8；richness 1-3）。
///
/// 推断依据（全部来自模拟已有数据）：
///   - 岩性池（Crust.Sedimentary/Metamorphic/FelsicPlutonic/MaficVolcanic/FelsicVolcanic）
///   - 地壳年龄（Crust.Age）——老克拉通富铁
///   - 板块边界（邻居不同板块）——火山弧/俯冲带富金属
///   - 地形（elevNorm）——高山石料、低地煤、盆地盐
///   - 气候（precip）——干旱盆地盐湖
///
/// 确定性：每格 Hash(i, seed) 概率判定——同 seed 同分布（用户生成一致性）。
/// 纯计算（无 Godot 对象），生成时后台线程安全。
/// </summary>
public static class MineralSystem
{
    public const int None = 0;
    public const int Iron = 1;    // 铁：老克拉通/变质/沉积
    public const int Copper = 2;  // 铜：火山弧（板块边界 + 镁铁质火山岩）
    public const int Tin = 3;     // 锡：花岗岩（长英质深成岩）
    public const int Gold = 4;    // 金：俯冲带/石英脉（边界 + 长英质）
    public const int Coal = 5;    // 煤：沉积岩 + 内陆低地
    public const int Salt = 6;    // 盐：干旱盆地/盐湖
    public const int Stone = 7;   // 石料：高山
    public const int Gem = 8;     // 宝石：变质岩 + 极稀

    public static readonly string[] Names = { "无", "铁", "铜", "锡", "金", "煤", "盐", "石料", "宝石" };

    /// <summary>
    /// 矿藏标注 v3（矿藏模拟化 + 分位数，通用）：
    ///   矿化强度在板块模拟中事件驱动累积（MineralHydro 裂谷/俯冲/增生热液、
    ///   MineralSed 沉积增厚、MineralMeta 变质累积——矿在演化中"长出来"）。
    ///   倾向分 = 矿化强度（主项）× 岩性/地形/气候软条件，全部相对该星球分布归一化；
    ///   倾向分 > 固定百分位 → 成矿（任何星球参数/网格 n 下稀有度恒定）。
    ///   小随机波动：× (0.9~1.1)（Hash(seed) 确定性微扰）。
    /// </summary>
    public static void ComputeMinerals(
        Vector3[] verts, int[][] neighbors, int[] flow,
        float[] elevNorm, float[] precip, float[] age,
        float[] mineralHydro, float[] mineralSed, float[] mineralMeta,
        Crust crust, int seed, out byte[] minerals)
    {
        int n = verts.Length;
        minerals = new byte[n];
        float[] sed = crust?.Sedimentary, meta = crust?.Metamorphic,
                felP = crust?.FelsicPlutonic, mafV = crust?.MaficVolcanic,
                felV = crust?.FelsicVolcanic;

        // ── 1. 特征预计算（相对该星球分布归一化 0..1）──
        var slope = new float[n];
        float maxSlope = 0f;
        var elevRel = new float[n];
        float maxElev = 0f;
        var precipRel = new float[n];
        float minP = float.MaxValue, maxP = float.MinValue;
        var ageRel = new float[n];
        float minA = float.MaxValue, maxA = float.MinValue;
        // 矿化强度相对值
        var hydroR = new float[n]; var sedMR = new float[n]; var metaMR = new float[n];
        float maxH = 1f, maxS = 1f, maxM = 1f;
        // 池相对值（每池 / 该池星球最大值）
        var sedR = new float[n]; var metaR = new float[n]; var fpR = new float[n];
        var mvR = new float[n]; var fvR = new float[n];
        float maxSed = 1f, maxMeta = 1f, maxFp = 1f, maxMv = 1f, maxFv = 1f;

        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) continue;            // 海洋：全 0
            if (flow != null && flow[i] >= 0 && flow[i] < n)
                slope[i] = Mathf.Max(0f, elevNorm[i] - elevNorm[flow[i]]);
            if (slope[i] > maxSlope) maxSlope = slope[i];
            if (elevNorm[i] > maxElev) maxElev = elevNorm[i];
            if (precip != null)
            {
                if (precip[i] < minP) minP = precip[i];
                if (precip[i] > maxP) maxP = precip[i];
            }
            if (age != null)
            {
                if (age[i] < minA) minA = age[i];
                if (age[i] > maxA) maxA = age[i];
            }
            if (mineralHydro != null && mineralHydro[i] > maxH) maxH = mineralHydro[i];
            if (mineralSed != null && mineralSed[i] > maxS) maxS = mineralSed[i];
            if (mineralMeta != null && mineralMeta[i] > maxM) maxM = mineralMeta[i];
            if (sed != null && sed[i] > maxSed) maxSed = sed[i];
            if (meta != null && meta[i] > maxMeta) maxMeta = meta[i];
            if (felP != null && felP[i] > maxFp) maxFp = felP[i];
            if (mafV != null && mafV[i] > maxMv) maxMv = mafV[i];
            if (felV != null && felV[i] > maxFv) maxFv = felV[i];
        }
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) continue;
            elevRel[i] = maxElev > 1e-6f ? elevNorm[i] / maxElev : 0f;
            if (precip != null) precipRel[i] = maxP > minP ? (precip[i] - minP) / (maxP - minP) : 0.5f;
            if (age != null) ageRel[i] = maxA > minA ? (age[i] - minA) / (maxA - minA) : 0.5f;
            hydroR[i] = mineralHydro != null ? mineralHydro[i] / maxH : 0f;
            sedMR[i] = mineralSed != null ? mineralSed[i] / maxS : 0f;
            metaMR[i] = mineralMeta != null ? mineralMeta[i] / maxM : 0f;
            sedR[i] = sed != null ? sed[i] / maxSed : 0f;
            metaR[i] = meta != null ? meta[i] / maxMeta : 0f;
            fpR[i] = felP != null ? felP[i] / maxFp : 0f;
            mvR[i] = mafV != null ? mafV[i] / maxMv : 0f;
            fvR[i] = felV != null ? felV[i] / maxFv : 0f;
        }

        // ── 2. 每矿种倾向分（矿化强度主项 × 软条件 × 微扰 0.9~1.1）──
        // 矿种：1铁 2铜 3锡 4金 5煤 6盐 7石料 8宝石
        var scores = new float[9][];
        for (int t = 1; t <= 8; t++) scores[t] = new float[n];
        int land = 0;
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) continue;
            land++;
            float sN = maxSlope > 1e-6f ? slope[i] / maxSlope : 0f;   // 坡度分
            float pert = 0.9f + 0.2f * Hash(i, seed);                  // 微扰（同 seed 确定）
            float pert2 = 0.9f + 0.2f * Hash(i, seed + 7);
            float felsic = Mathf.Max(fpR[i], fvR[i]);                  // 长英质（石英脉/花岗岩）

            // ⚠️ v3 矿藏模拟化：热液/沉积/变质强度为增强项（模拟中事件累积——但裂谷/俯冲
            //   多在海洋（洋中脊/海沟），陆地矿以岩性/构造为主项 + 热液增强（0.5+0.5×hydroR））
            scores[1][i] = (metaMR[i] * 0.7f + hydroR[i] * 0.3f) * Mathf.Pow(ageRel[i], 1.2f) * pert;  // 铁：变质矿化 + 老克拉通
            scores[2][i] = mvR[i] * (0.5f + 0.5f * hydroR[i]) * pert2;   // 铜：陆地火山岩 + 热液增强
            scores[3][i] = fpR[i] * (0.5f + 0.5f * hydroR[i]) * pert;    // 锡：花岗岩 + 热液增强
            scores[4][i] = felsic * sN * (0.5f + 0.5f * hydroR[i]) * pert2;  // 金：造山带 + 热液增强
            scores[5][i] = sedMR[i] * (1f - elevRel[i]) * precipRel[i] * pert;   // 煤：沉积矿化 × 湿润低地
            scores[6][i] = (1f - precipRel[i]) * (1f - elevRel[i]) * pert2;      // 盐：干旱低地
            scores[7][i] = elevRel[i] * pert;                          // 石料：高山
            scores[8][i] = metaMR[i] * pert2;                          // 宝石：变质矿化（0.5% 极稀）
        }

        // ── 3. 百分位阈值 + 标记（每矿种固定占比，通用）──
        // 占比（陆地）：铁5% 石料4% 煤3% 铜3% 盐3% 锡2.5% 金1% 宝石0.5%
        float[] percent = { 0f, 0.05f, 0.03f, 0.025f, 0.01f, 0.03f, 0.03f, 0.04f, 0.005f };
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        var thr = new float[9];
        for (int t = 1; t <= 8; t++)
        {
            System.Array.Sort(order, (a, b) => scores[t][b].CompareTo(scores[t][a]));
            int keep = Mathf.Max(1, (int)(land * percent[t]));
            thr[t] = scores[t][order[keep - 1]];      // 第 keep 大 = 阈值
            if (thr[t] <= 0f) thr[t] = float.MaxValue;  // 无倾向（缺数据）→ 不标记
        }
        // 每格：所有中签矿种取倾向最高者
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) continue;
            int bestType = 0;
            float bestScore = 0f;
            for (int t = 1; t <= 8; t++)
                if (scores[t][i] >= thr[t] && scores[t][i] > bestScore)
                {
                    bestScore = scores[t][i];
                    bestType = t;
                }
            if (bestType == 0) continue;
            float h2 = Hash(i, seed + 1013);
            int rich = h2 < 0.4f ? 1 : h2 < 0.8f ? 2 : 3;
            minerals[i] = (byte)((rich << 4) | bestType);
        }
    }

    public static int TypeOf(byte b) => b & 0x0F;
    public static int RichnessOf(byte b) => (b >> 4) & 0x03;

    /// <summary>确定性哈希（seed 派生，同 seed 同分布）。</summary>
    private static float Hash(int i, int seed)
    {
        uint x = (uint)(i * 2654435761u + (uint)seed * 40503u);
        x ^= x >> 13;
        x *= 0x5bd1e995u;
        x ^= x >> 15;
        return (x & 0xFFFF) / 65535f;
    }
}
