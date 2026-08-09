using Godot;
using System;
using World.LogicGrid;

namespace World.MapGen;

/// <summary>
/// 野生作物层 WildCrops（自然层，与矿藏/土壤同层；与人文无关）。
/// 每格 byte bitmask：5 位 = 5 种子科技（0=小麦 1=粟 2=水稻 3=玉米 4=土豆）。
///
/// 生成（BIOCLIM/MaxEnt 式生态位，Phillips et al. 2006）：
///   1. 适宜度 = 加权高斯生态位 exp(−Σ w_j·((x_j − x_ideal)/σ_j)²)
///      x_j = 年均温 / 年降水 / 降水季节性(MonthPrecip 派生) / 海拔（作物特殊项）
///      FAO 农业气候学生态位参数（docs/石器时代设计.md §9）
///   2. 分布区 = 适宜度 > 全球陆地 P70 分位数（相对该星球分布，非绝对阈值）
///   3. 群聚洒落：分布区内随机种子点 + BFS 邻域聚簇（斑块形态，非均匀铺开）
///   4. 不做保底：星球无匹配气候 → 该野生种天然灭绝（位全 0，诊断警告）
///
/// 确定性：纯 f(seed, 气候场)，同 seed 同网格同结果。
/// 不存档：.mpa/.gmp/.cmp 均不含 WildCrops 段——读档后现场重推导（同 seed 同结果，
/// 设计定稿：存档布局零改动，确定性重建）。
/// </summary>
public static class WildCropsSystem
{
    public const int SeedCount = 5;
    public const int Wheat = 0, Millet = 1, Rice = 2, Corn = 3, Potato = 4;

    /// <summary>每格每种子适宜度 φ ∈ [0,1]（海洋 0）。CivSim 产量/传播直接消费。
    /// 月温/月降水数组每格只算一次（5 种子复用，性能：n=64 全格 ~5 万次高斯）。</summary>
    public static float[,] Suitability(GameGrid g)
    {
        int n = g.N;
        var suit = new float[n, SeedCount];
        var mt = new float[12];
        var mp = new float[12];
        for (int i = 0; i < n; i++)
        {
            if (!g.IsLandCell(i)) continue;
            for (int m = 0; m < 12; m++)
            {
                mt[m] = g.MonthTemp[m][i] / 255f * 120f - 60f;
                mp[m] = g.MonthPrecip[m][i] / 255f;
            }
            suit[i, Wheat]  = WheatSuit(g, i, mt, mp);
            suit[i, Millet] = MilletSuit(g, i, mt, mp);
            suit[i, Rice]   = RiceSuit(g, i, mt, mp);
            suit[i, Corn]   = CornSuit(g, i, mt, mp);
            suit[i, Potato] = PotatoSuit(g, i, mt, mp);
        }
        return suit;
    }

    /// <summary>野生作物位（bitmask 5 位）。suit 可传入缓存，null 则现算。</summary>
    public static byte[] Compute(GameGrid g, int seed, float[,] suit = null)
    {
        int n = g.N;
        suit ??= Suitability(g);
        var bits = new byte[n];
        for (int s = 0; s < SeedCount; s++)
        {
            // ── 分布区：适宜度 > 陆地 P70（相对该星球分布）──
            var landSuit = new System.Collections.Generic.List<float>(n);
            for (int i = 0; i < n; i++)
                if (g.IsLandCell(i) && suit[i, s] > 1e-4f)
                    landSuit.Add(suit[i, s]);
            if (landSuit.Count == 0) continue;   // 天然灭绝（诊断层警告）
            landSuit.Sort();
            float p70 = landSuit[Mathf.Clamp((int)(landSuit.Count * 0.70f), 0, landSuit.Count - 1)];
            var zone = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++)
                if (g.IsLandCell(i) && suit[i, s] >= p70)
                    zone.Add(i);
            if (zone.Count == 0) continue;

            // ── 群聚洒落：随机种子点 + BFS 邻域聚簇（斑块形态）──
            var rng = new Random(seed * 97 + s * 131 + 17);
            int seedPoints = Mathf.Max(1, zone.Count / 12);
            int targetPer = Mathf.Max(3, zone.Count / seedPoints * 65 / 100);
            var marked = new bool[n];
            var visitStamp = new int[n];
            int stamp = 1;
            for (int p = 0; p < seedPoints; p++)
            {
                int start = zone[rng.Next(zone.Count)];
                if (marked[start]) continue;
                marked[start] = true;
                bits[start] |= (byte)(1 << s);
                var queue = new System.Collections.Generic.Queue<int>();
                queue.Enqueue(start);
                int size = 1;
                var order = new int[g.Neighbors[start].Length];
                while (queue.Count > 0 && size < targetPer)
                {
                    int cur = queue.Dequeue();
                    var nbs = g.Neighbors[cur];
                    int cnt = nbs.Length;
                    if (order.Length != cnt) order = new int[cnt];
                    for (int k = 0; k < cnt; k++) order[k] = k;
                    for (int a = cnt - 1; a > 0; a--)   // Fisher–Yates（确定性 rng）
                    {
                        int b = rng.Next(a + 1);
                        (order[a], order[b]) = (order[b], order[a]);
                    }
                    for (int k = 0; k < cnt && size < targetPer; k++)
                    {
                        int nb = nbs[order[k]];
                        if (nb < 0 || nb >= n || !g.IsLandCell(nb)) continue;
                        if (visitStamp[nb] == stamp) continue;
                        visitStamp[nb] = stamp;
                        if (suit[nb, s] < p70) continue;        // 斑块只长在分布区内
                        if (marked[nb]) continue;
                        if (rng.NextDouble() >= 0.5) continue;  // 聚簇疏松度（~50% 填充）
                        marked[nb] = true;
                        bits[nb] |= (byte)(1 << s);
                        queue.Enqueue(nb);
                        size++;
                    }
                }
            }
        }
        return bits;
    }

    /// <summary>查询适宜度（格/种子）；suit 缓存矩阵可传入。</summary>
    public static float Phi(GameGrid g, int cell, int seedIdx, float[,] suit = null)
    {
        if (!g.IsLandCell(cell)) return 0f;
        if (suit != null) return Mathf.Clamp(suit[cell, seedIdx], 0f, 1f);
        var mt = new float[12];
        var mp = new float[12];
        for (int m = 0; m < 12; m++)
        {
            mt[m] = g.MonthTemp[m][cell] / 255f * 120f - 60f;
            mp[m] = g.MonthPrecip[m][cell] / 255f;
        }
        return seedIdx switch
        {
            Wheat  => WheatSuit(g, cell, mt, mp),
            Millet => MilletSuit(g, cell, mt, mp),
            Rice   => RiceSuit(g, cell, mt, mp),
            Corn   => CornSuit(g, cell, mt, mp),
            _      => PotatoSuit(g, cell, mt, mp),
        };
    }

    // ── 气候派生量（每格，一次性）──

    /// <summary>最冷 6 个月（按月温排序）降水比例和 ∈[0,1]（冬半年降水占比）。</summary>
    private static float WinterShare(GameGrid g, int i, float[] monthT, float[] monthP)
    {
        var order = new int[12];
        for (int m = 0; m < 12; m++) order[m] = m;
        for (int a = 0; a < 12; a++)          // 选择排序（12 元素，确定性）
            for (int b = a + 1; b < 12; b++)
                if (monthT[order[b]] < monthT[order[a]]) (order[a], order[b]) = (order[b], order[a]);
        float s = 0f;
        for (int k = 0; k < 6; k++) s += monthP[order[k]];
        return Mathf.Clamp(s, 0f, 1f);
    }

    /// <summary>最热月均温 °C。</summary>
    private static float MaxMonthTemp(GameGrid g, int i, float[] monthT)
    {
        float m = float.MinValue;
        for (int k = 0; k < 12; k++) m = Mathf.Max(m, monthT[k]);
        return m;
    }

    /// <summary>最干月降水 mm（月比例最小值 × 年降水）。</summary>
    private static float MinMonthPrecip(GameGrid g, int i, float[] monthP, float yearPrecip)
    {
        float m = float.MaxValue;
        for (int k = 0; k < 12; k++) m = Mathf.Min(m, monthP[k]);
        return m * yearPrecip;
    }

    /// <summary>雨季分明度：最湿月比例 / 最干月比例（>2 雨季分明）。</summary>
    private static float WetDryRatio(GameGrid g, int i, float[] monthP)
    {
        float max = 0f, min = float.MaxValue;
        for (int k = 0; k < 12; k++) { max = Mathf.Max(max, monthP[k]); min = Mathf.Min(min, monthP[k]); }
        return max / Mathf.Max(min, 0.02f);
    }

    private static float Gauss(float x, float ideal, float sigma) =>
        Mathf.Exp(-(x - ideal) * (x - ideal) / (2f * sigma * sigma));

    // ── 各作物生态位（FAO 农业气候学参数，docs §9）──

    /// <summary>小麦：15°C(σ6)、400-700mm、冬雨(冬半年>0.55)、钙质土偏好(忽略：无土壤类型字段)。</summary>
    private static float WheatSuit(GameGrid g, int i, float[] mt, float[] mp)
    {
        float t = g.Temp[i], p = g.Precip[i];
        float winter = WinterShare(g, i, mt, mp);
        float s = 0.45f * Gauss(t, 15f, 6f) + 0.40f * Gauss(p, 550f, 180f) + 0.15f * Gauss(winter, 0.62f, 0.15f);
        return Mathf.Clamp(s, 0f, 1f);
    }

    /// <summary>粟/高粱：10°C(σ7)、300-600mm、夏雨、耐贫瘠(Soil 权重低→不进适宜度，产量 f(Soil) 处理)。</summary>
    private static float MilletSuit(GameGrid g, int i, float[] mt, float[] mp)
    {
        float t = g.Temp[i], p = g.Precip[i];
        float summer = 1f - WinterShare(g, i, mt, mp);   // 夏半年降水占比
        float s = 0.45f * Gauss(t, 10f, 7f) + 0.40f * Gauss(p, 450f, 150f) + 0.15f * Gauss(summer, 0.62f, 0.15f);
        return Mathf.Clamp(s, 0f, 1f);
    }

    /// <summary>水稻：25°C(σ4)、>1000mm、最干月>50mm(湿润)、河湖加成。</summary>
    private static float RiceSuit(GameGrid g, int i, float[] mt, float[] mp)
    {
        float t = g.Temp[i], p = g.Precip[i];
        float dry = MinMonthPrecip(g, i, mp, p);
        float s = 0.45f * Gauss(t, 25f, 4f) + 0.40f * Gauss(p, 1700f, 550f) + 0.15f * Gauss(dry, 120f, 70f);
        bool wet = g.Biome[i] == (byte)Biome.BiomeType.Riparian || g.LakeLevel[i] > 0;
        if (wet) s *= 1.3f;
        return Mathf.Clamp(s, 0f, 1f);
    }

    /// <summary>玉米/豆类：22°C(σ5)、800-1500mm、雨季分明。</summary>
    private static float CornSuit(GameGrid g, int i, float[] mt, float[] mp)
    {
        float t = g.Temp[i], p = g.Precip[i];
        float wd = WetDryRatio(g, i, mp);
        float s = 0.45f * Gauss(t, 22f, 5f) + 0.40f * Gauss(p, 1150f, 250f) + 0.15f * Gauss(wd, 5f, 2f);
        return Mathf.Clamp(s, 0f, 1f);
    }

    /// <summary>土豆/块茎：10°C(σ5)、600-1000mm、冷凉(最热月<20°C)、海拔加成。</summary>
    private static float PotatoSuit(GameGrid g, int i, float[] mt, float[] mp)
    {
        float t = g.Temp[i], p = g.Precip[i];
        float hot = MaxMonthTemp(g, i, mt);
        float s = 0.45f * Gauss(t, 10f, 5f) + 0.40f * Gauss(p, 800f, 200f) + 0.15f * Gauss(hot, 16f, 5f);
        s *= 1f + 0.3f * Mathf.Clamp(g.Elev[i] / 2500f, 0f, 1f);   // 高原加成
        return Mathf.Clamp(s, 0f, 1f);
    }

    /// <summary>野生畜牧位（bitmask 1 位；2026-08-09）：草原类 biome（HotSteppe/ColdSteppe/TropicalSavanna/
    /// MediterraneanHot/MediterraneanCool）+ 年降水 300-1200mm → 可驯。同 WildCrops 同构：确定性重建不入档。</summary>
    public static byte[] ComputeLivestock(GameGrid g, int seed)
    {
        int n = g.N;
        var bits = new byte[n];
        for (int i = 0; i < n; i++)
        {
            if (!g.IsLandCell(i)) continue;
            var b = (Biome.BiomeType)g.Biome[i];
            bool grass = b is Biome.BiomeType.HotSteppe or Biome.BiomeType.ColdSteppe
                       or Biome.BiomeType.TropicalSavanna or Biome.BiomeType.MediterraneanHot
                       or Biome.BiomeType.MediterraneanCool;
            if (!grass) continue;
            if (g.Precip[i] >= 300f && g.Precip[i] <= 1200f) bits[i] = 1;   // 年降水 mm
        }
        return bits;
    }
}
