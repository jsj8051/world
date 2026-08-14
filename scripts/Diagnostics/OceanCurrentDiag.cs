using Godot;
using World.Biome;
using World.MapGen;

namespace World.Diagnostics;

/// <summary>临时验证：洋流算法分辨率修复（读旧 n=128 档重算洋流，验证迭代/梯度补偿生效）。</summary>
public partial class OceanCurrentDiag : Node
{
    public override void _Ready()
    {
        string arch = ArchiveDiag.ResolveArchPath();
        if (arch == null) { GetTree().Quit(1); return; }
        if (!ArchiveDiag.TryLoad(arch, out var dctx)) { GetTree().Quit(1); return; }
        var map = dctx.Map;
        map.EnsureBuckets();
        var nbs = map.BuildNeighbors();
        var eNorm = new float[map.Verts.Length];
        float range = Mathf.Max(-map.MinElev, map.MaxElev);
        for (int i = 0; i < map.Verts.Length; i++) eNorm[i] = range > 1e-6f ? map.Elev[i] / range : 0f;

        OceanCurrent.Compute(map.Verts, nbs, eNorm, out var dirs, out var warmth, out var strength, out var psi,
            windField: null, oceanTemp: map.Temp);
        int ocean = 0, withDir = 0, warm = 0, cold = 0;
        for (int i = 0; i < map.Verts.Length; i++)
        {
            if (eNorm[i] >= 0f) continue;
            ocean++;
            if (dirs[i].LengthSquared() > 1e-12f)
            {
                withDir++;
                if (warmth[i] > 0.2f) warm++;
                else if (warmth[i] < -0.2f) cold++;
            }
        }
        GD.Print($"[OceanCurrentDiag] {arch} n={map.Verts.Length} 海洋格={ocean} 洋流格={withDir} " +
                 $"({100f * withDir / Mathf.Max(1, ocean):F1}%) 暖流={warm} 寒流={cold}");

        // 强度分布（显示层筛选阈值校准；用重算的 strength——SOR 求解效果验证）
        {
            int s035 = 0, s030 = 0, s025 = 0;
            for (int i = 0; i < map.Verts.Length; i++)
            {
                if (eNorm[i] >= 0f || strength[i] <= 0f) continue;
                float s = strength[i];
                if (s > 0.35f) s035++;
                if (s > 0.30f) s030++;
                if (s > 0.25f) s025++;
            }
            GD.Print($"[OceanCurrentDiag] 强度分布(重算): >0.25={s025} >0.30={s030} >0.35={s035}");
        }

        // 温度/降水/biome 分布检查（biome 异常连带排查）
        float tMin = 1e9f, tMax = -1e9f, pMin = 1e9f, pMax = -1e9f;
        int land = 0;
        var biomeHist = new int[40];
        for (int i = 0; i < map.Verts.Length; i++)
        {
            if (eNorm[i] < 0f) continue;
            land++;
            tMin = Mathf.Min(tMin, map.Temp[i]); tMax = Mathf.Max(tMax, map.Temp[i]);
            pMin = Mathf.Min(pMin, map.Precip[i]); pMax = Mathf.Max(pMax, map.Precip[i]);
            if (map.Biome != null && map.Biome[i] < biomeHist.Length) biomeHist[map.Biome[i]]++;
        }
        var top = new System.Text.StringBuilder();
        for (int b = 0; b < biomeHist.Length; b++)
            if (biomeHist[b] * 100 >= land) top.Append($"{b}:{biomeHist[b]} ");
        GD.Print($"[OceanCurrentDiag] 陆地={land} Temp[{tMin:F1},{tMax:F1}] Precip[{pMin:F0},{pMax:F0}] biome≥1%: {top}");

        // 月降水季节性：中纬度(30-60°)陆地格夏季(6-8月)降水占比——Dwa 冬干误判排查
        if (map.MonthPrecip != null && map.MonthPrecip.Length == 12)
        {
            int midLat = 0; double summer = 0, total = 0;
            for (int i = 0; i < map.Verts.Length; i++)
            {
                if (eNorm[i] < 0f) continue;
                float lat = Mathf.Asin(Mathf.Clamp(map.Verts[i].Y, -1f, 1f)) * 180f / Mathf.Pi;
                if (Mathf.Abs(lat) < 30f || Mathf.Abs(lat) > 60f) continue;
                midLat++;
                for (int m = 0; m < MonsoonSystem.MonthCount; m++)
                {
                    float r = map.MonthPrecip[m][i] / 255f;
                    total += r;
                    if (m >= 5 && m <= 7) summer += r;   // 6-8月(索引5,6,7)
                }
            }
            GD.Print($"[OceanCurrentDiag] 中纬度格={midLat} 夏雨占比={(total > 0 ? summer / total : 0):F3}(正常≈0.4-0.6)");

            // ── biome 重算验证（新判据：DryP 比例→mm 换算 + D 带冬干检查）──
            //   ⚠️ 存档 biome 是旧判据固化（比例 0-1 恒<30 → D 带全 Dwa 误判）
            var newBiome = new int[40];
            for (int i = 0; i < map.Verts.Length; i++)
            {
                if (eNorm[i] < 0f) continue;
                float latDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(map.Verts[i].Y, -1f, 1f)));
                float dryMm = 1e9f, wetMm = -1e9f; int dryIdx = 0;
                float tHot = -1e9f, tCold = 1e9f;
                for (int m = 0; m < MonsoonSystem.MonthCount; m++)
                {
                    float p = map.MonthPrecip[m][i] / 255f * map.Precip[i];   // 比例→mm
                    if (p < dryMm) { dryMm = p; dryIdx = m; }
                    if (p > wetMm) wetMm = p;
                    float tc = map.MonthTemp[m][i] / 255f * 120f - 60f;
                    if (tc > tHot) tHot = tc;
                    if (tc < tCold) tCold = tc;
                }
                var b = World.Biome.BiomeClassifier.Classify(eNorm[i], map.Temp[i], map.Precip[i],
                    tHot, tCold, dryMm, dryIdx, latDeg, wetMm);
                if ((int)b < newBiome.Length) newBiome[(int)b]++;
            }
            var top2 = new System.Text.StringBuilder();
            for (int b = 0; b < newBiome.Length; b++)
                if (newBiome[b] * 100 >= land) top2.Append($"{b}:{newBiome[b]} ");
            GD.Print($"[OceanCurrentDiag] 新判据 biome≥1%: {top2}");
        }
        GetTree().Quit(0);
    }
}
