using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 逐格壳型：按布局选宿主板块放陆壳核，从核在全球格网生长陆壳省，剩余格为洋壳。
/// 生长用低频噪声塑形 + 白噪声粗糙化；陆壳省可以跨板块（板块只决定核的位置）。
/// </summary>
public static class PlateCrustOps
{
    /// <summary>单个宿主板块的陆壳覆盖率上限；越低则板内洋壳边缘越宽、海岸越不贴板块边界。</summary>
    const float HostCoverage = 0.55f;

    public static CrustField Build(Ball ball, int seed, PlateSpec spec, ContinentLayout layout,
        int[] plateOfCell, int plateCount, int[] sizeOfPlate, bool[,] adjacency)
    {
        if (ball == null) throw new ArgumentNullException(nameof(ball));
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
        if (sizeOfPlate == null) throw new ArgumentNullException(nameof(sizeOfPlate));
        if (adjacency == null) throw new ArgumentNullException(nameof(adjacency));
        if (plateCount < 1 || sizeOfPlate.Length != plateCount)
            throw new ArgumentException("板块数与格数表不一致", nameof(sizeOfPlate));
        if (plateOfCell.Length != ball.CellDirs.Length)
            throw new ArgumentException("板块号长度与格数不一致", nameof(plateOfCell));

        int n = plateOfCell.Length;
        int target = Math.Clamp((int)MathF.Round(n * spec.ContinentalRatio), 1, n);
        List<int> hosts = SelectHosts(seed, spec, layout, plateCount, sizeOfPlate, adjacency);

        var type = new CrustType[n];
        Array.Fill(type, CrustType.Oceanic);
        var provinceId = new int[n];
        Array.Fill(provinceId, -1);
        var provinces = new List<CrustProvince>();

        var host = new bool[plateCount];
        int hostArea = 0;
        foreach (int p in hosts) { host[p] = true; hostArea += sizeOfPlate[p]; }

        // 低频噪声塑形（海湾 / 半岛），逐格白噪声只做边缘粗糙度。
        var shape = new SphericalFbmNoise(
            SeedDerivation.Derive(seed, SeedDerivation.Plate_Crust), 2600f, 4);
        var wobble = new float[n];
        Parallel.For(0, n, i => wobble[i] = shape.Sample(ball.CellDirs[i]));

        if (layout == ContinentLayout.Pangea)
        {
            int seedCell = Nucleus(ball, plateOfCell, host, -1, type);
            int pangeaTarget = Math.Min(target, (int)(hostArea * 0.9f));
            int count = Grow(ball, seedCell, pangeaTarget, type, provinceId, 0, seed, wobble);
            if (count > 0)
                provinces.Add(new CrustProvince(0, CrustType.Continental, seedCell, plateOfCell[seedCell], count));
        }
        else
        {
            float coverage = MathF.Min(1f, (float)target / Math.Max(1, hostArea));
            foreach (int p in hosts)
            {
                int want = Math.Min(sizeOfPlate[p], Math.Max(1, (int)MathF.Round(sizeOfPlate[p] * coverage)));
                int seedCell = Nucleus(ball, plateOfCell, host, p, type);
                int count = Grow(ball, seedCell, want, type, provinceId,
                    provinces.Count, seed + p * 7919, wobble);
                if (count > 0)
                    provinces.Add(new CrustProvince(provinces.Count, CrustType.Continental,
                        seedCell, p, count));
            }
        }
        return new CrustField(type, provinceId, provinces.ToArray());
    }

    static List<int> SelectHosts(int seed, PlateSpec spec, ContinentLayout layout, int p,
        int[] sizeOfPlate, bool[,] adjacency)
    {
        var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Plate_Crust));
        int want = Math.Clamp((int)MathF.Round(p * spec.ContinentalRatio), 1, p);
        want = layout switch
        {
            ContinentLayout.ManyContinents => Math.Min(p, want + ContinentLayouts.ManyExtraPlates),
            ContinentLayout.Archipelago => Math.Min(p, want + ContinentLayouts.ArchipelagoExtraPlates),
            _ => want,
        };
        // 目标覆盖率 0.55 ⇒ 宿主块数按面积比例补足（大小板块近似等大时成立）。
        want = Math.Min(p, Math.Max(want,
            (int)MathF.Ceiling(p * spec.ContinentalRatio / HostCoverage)));

        var score = new float[p];
        for (int k = 0; k < p; k++)
            score[k] = sizeOfPlate[k] * (0.5f + (float)rnd.NextDouble());

        var order = new int[p];
        for (int k = 0; k < p; k++) order[k] = k;
        Array.Sort(order, (a, b) => score[a] != score[b] ? score[b].CompareTo(score[a]) : a.CompareTo(b));

        var chosen = new bool[p];
        var picked = new List<int>(want);
        switch (layout)
        {
            case ContinentLayout.Pangea: PickContiguous(order, chosen, picked, want, adjacency); break;
            case ContinentLayout.Archipelago: PickByScore(order, chosen, picked, want); break;
            default: PickSplit(order, chosen, picked, want, adjacency); break;
        }

        return picked;
    }

    /// <summary>区域内离质心最近的格；<paramref name="plate"/> = −1 表示用 <paramref name="host"/> 并集。</summary>
    static int Nucleus(Ball ball, int[] plateOfCell, bool[] host, int plate, CrustType[] type)
    {
        var dirs = ball.CellDirs;
        Vector3 sum = default;
        int count = 0;
        for (int i = 0; i < plateOfCell.Length; i++)
        {
            if (type[i] == CrustType.Continental) continue;
            if (plate >= 0 ? plateOfCell[i] != plate : !host[plateOfCell[i]]) continue;
            sum += dirs[i];
            count++;
        }
        if (count == 0) return -1;

        Vector3 center = sum.Normalized();
        int best = -1;
        float bestDot = -1f;
        for (int i = 0; i < plateOfCell.Length; i++)
        {
            if (type[i] == CrustType.Continental) continue;
            if (plate >= 0 ? plateOfCell[i] != plate : !host[plateOfCell[i]]) continue;
            float dot = dirs[i].Dot(center);
            if (dot > bestDot) { bestDot = dot; best = i; }
        }
        return best;
    }

    static int Grow(Ball ball, int seedCell, int target, CrustType[] type, int[] provinceId,
        int province, int noiseSeed, float[] wobble)
    {
        if (seedCell < 0 || target <= 0) return 0;
        var dist = new double[type.Length];
        Array.Fill(dist, double.PositiveInfinity);
        var heap = new PriorityQueue<int, double>();
        dist[seedCell] = 0;
        heap.Enqueue(seedCell, 0);

        int count = 0;
        while (count < target && heap.TryDequeue(out int cell, out double d))
        {
            if (d > dist[cell]) continue;   // 过期条目
            if (type[cell] == CrustType.Continental) continue;
            type[cell] = CrustType.Continental;
            provinceId[cell] = province;
            count++;

            foreach (int nb in ball.CellNeighbors[cell])
            {
                if (type[nb] == CrustType.Continental) continue;
                // 代价 ∈ [1, 3.8]：低频噪声决定扩张快慢 ⇒ 海岸是平滑等值线，不是折线。
                double cost = 1.0 + 2.5 * (0.5 + 0.5 * wobble[nb]) + 0.3 * Hash01(nb, noiseSeed);
                double nd = d + cost;
                if (nd >= dist[nb]) continue;
                dist[nb] = nd;
                heap.Enqueue(nb, nd);
            }
        }
        return count;
    }

    /// <summary>逐格确定性噪声 [0,1)；只扰动生长前沿，不参与任何物理量。</summary>
    static float Hash01(int cell, int seed)
    {
        uint h = unchecked((uint)cell * 747796405u + (uint)seed * 2891336453u);
        h = unchecked((h ^ (h >> 16)) * 2246822519u);
        h = unchecked((h ^ (h >> 13)) * 3266489917u);
        h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777216f;
    }

    static void PickContiguous(int[] order, bool[] chosen, List<int> picked, int want, bool[,] adjacency)
    {
        while (picked.Count < want)
        {
            int best = -1;
            foreach (int c in order)
            {
                if (chosen[c]) continue;
                if (picked.Count > 0 && !TouchesChosen(c, picked, adjacency)) continue;
                best = c;
                break;
            }
            if (best < 0)
                foreach (int c in order)
                    if (!chosen[c]) { best = c; break; }
            if (best < 0) break;
            chosen[best] = true;
            picked.Add(best);
        }
    }

    static void PickSplit(int[] order, bool[] chosen, List<int> picked, int want, bool[,] adjacency)
    {
        foreach (int c in order)
        {
            if (picked.Count >= want) break;
            if (chosen[c] || TouchesChosen(c, picked, adjacency)) continue;
            chosen[c] = true;
            picked.Add(c);
        }
        foreach (int c in order)
        {
            if (picked.Count >= want) break;
            if (chosen[c]) continue;
            chosen[c] = true;
            picked.Add(c);
        }
    }

    static void PickByScore(int[] order, bool[] chosen, List<int> picked, int want)
    {
        foreach (int c in order)
        {
            if (picked.Count >= want) break;
            if (chosen[c]) continue;
            chosen[c] = true;
            picked.Add(c);
        }
    }

    static bool TouchesChosen(int candidate, List<int> picked, bool[,] adjacency)
    {
        foreach (int q in picked)
            if (adjacency[candidate, q]) return true;
        return false;
    }
}
