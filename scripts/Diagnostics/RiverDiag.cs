using Godot;
using System.Collections.Generic;
using World.Biome;
using World.MapGen;
using World.Tectonics;

namespace World.Diagnostics;

/// <summary>河流诊断：河流统计 + 等距柱状图（河线+陆地+海洋）。
/// 存档直读（-- --arch=...）：用存档海拔/降水/温度重跑 RiverSystem（秒级），
/// 并与其存档河流数据做自洽性对比——改 RiverSystem 后免 n=64 板块模拟（~3min）。</summary>
public partial class RiverDiag : DiagSceneBase
{
    public override void _Ready()
    {
        string arch = ArchiveDiag.ResolveArchPath();
        if (arch != null)
        {
            if (!ArchiveDiag.TryLoad(arch, out var ctx))
            {
                GD.PrintErr("[RiverDiag] 存档直读失败");
                GetTree().Quit(1);
                return;
            }
            RunFromArchive(ctx);
            return;
        }
        RunSim();
    }

    /// <summary>原流程：n=64 板块模拟 600My（~3min）+ 河流迭代。</summary>
    private void RunSim()
    {
        const int n = 64;
        var sim = new TectonicsSimulation(n);
        sim.GenerateInitialCrust(42);
        sim.SplitIntoPlates(8, 42);
        sim.Run(600f, 4f);   // stepMy 4：300→150 步，快 2 倍（2026-08-03 验证质量一致）
        var verts = sim.GlobalGrid.Vertices;
        var neighbors = sim.GlobalGrid.Neighbors;
        var disp = sim.Displacement;
        float sea = sim.SeaLevel;
        int vn = verts.Length;

        var eNorm = new float[vn];
        float span = 0f;
        for (int i = 0; i < vn; i++) span = Mathf.Max(span, Mathf.Abs(disp[i] - sea));
        for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? (disp[i] - sea) / span : 0f;

        // 气候（降水 + 温度 → 水量版河流）
        World.Biome.WindField.Prograde = true;
        World.Biome.WindField.RotationSpeed = 1f;
        var climate = new World.Biome.ClimateGenerator(42, 23.4f, 1.0f);
        var precip = new float[vn];
        var temp = new float[vn];
        for (int i = 0; i < vn; i++)
        {
            temp[i] = climate.ComputeTemperature(verts[i], eNorm[i]);
            precip[i] = climate.ComputePrecipitation(verts[i], eNorm[i], p => eNorm[sim.GlobalGrid.NearestId(p)]);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var eNormWork = (float[])eNorm.Clone();
        var elevWork = (float[])disp.Clone();
        RiverSystem.ComputeIterative(verts, neighbors, eNormWork, elevWork,
            precip, temp,
            waterThreshold: RiverSystem.DefaultWaterThreshold, lakeThreshold: RiverSystem.DefaultLakeThreshold,
            seaLevelM: 0f, elevSpan: span, rounds: RiverSystem.DefaultRounds,   // 与生产管线同参（诊断复现）
            out var flow, out var area, out var riverLevel, out var lakeLevel, out var paths);

        // 侵蚀/沉积统计（对比原海拔）
        {
            float maxCut = 0f, maxDep = 0f; int cut = 0, dep = 0;
            for (int i = 0; i < vn; i++)
            {
                float d = elevWork[i] - disp[i];
                if (d < -0.5f) { cut++; maxCut = Mathf.Min(maxCut, d); }
                if (d > 0.5f) { dep++; maxDep = Mathf.Max(maxDep, d); }
            }
            GD.Print($"[RiverDiag] 侵蚀沉积(v2输沙): 下切格 {cut} 最大切深 {maxCut:F0}m | 堆积格 {dep} 最大堆积 {maxDep:F0}m");
        }

        sw.Stop();
        PrintStats(n, vn, eNorm, area, flow, riverLevel, lakeLevel, paths, sw.ElapsedMilliseconds);
        DrawPng(sim.GlobalGrid, verts, eNorm, paths, lakeLevel, flow);
        GetTree().Quit();
    }

    /// <summary>存档直读：存档海拔/降水/温度 → 重跑 RiverSystem（秒级）+ 与存档河流自洽性对比。</summary>
    private void RunFromArchive(DiagContext ctx)
    {
        int vn = ctx.VertexCount;
        var verts = ctx.Verts;
        var neighbors = ctx.Neighbors;
        var eNorm = ctx.ElevNorm;
        var elevM = ctx.ElevM;
        var precip = ctx.Precip;
        var temp = ctx.Temp;
        float span = ctx.ElevSpan;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var eNormWork = (float[])eNorm.Clone();
        var elevWork = (float[])elevM.Clone();
        RiverSystem.ComputeIterative(verts, neighbors, eNormWork, elevWork,
            precip, temp,
            waterThreshold: RiverSystem.DefaultWaterThreshold, lakeThreshold: RiverSystem.DefaultLakeThreshold,
            seaLevelM: 0f, elevSpan: span, rounds: RiverSystem.DefaultRounds,   // 与生产管线同参（诊断复现）
            out var flow, out var area, out var riverLevel, out var lakeLevel, out var paths);
        sw.Stop();

        // 与存档河流数据自洽性对比（同输入重算 vs 存档结果；存档 n 与直读 n 同为 10n²+2 网格，
        // 顶点索引一致——直接逐项对比；若顺序不同则最近顶点映射兜底）
        int sameLevel = 0, sameFlow = 0, totalLand = 0;
        for (int i = 0; i < vn; i++)
        {
            if (eNorm[i] >= 0f)
            {
                totalLand++;
                int ai = ctx.Map.NearestVertex(verts[i]);
                if (ctx.Map.RiverLevel != null && ai >= 0 && ai < ctx.Map.RiverLevel.Length && riverLevel[i] == ctx.Map.RiverLevel[ai]) sameLevel++;
                if (ctx.Map.RiverFlow != null && ai >= 0 && ai < ctx.Map.RiverFlow.Length && flow[i] == ctx.Map.RiverFlow[ai]) sameFlow++;
            }
        }
        GD.Print($"[RiverDiag] 直读自洽性(陆地 {totalLand} 顶点): 级别一致 {sameLevel} ({100f * sameLevel / totalLand:F1}%) | 流向一致 {sameFlow} ({100f * sameFlow / totalLand:F1}%)");
        PrintStats(ctx.VertexCount, vn, eNorm, area, flow, riverLevel, lakeLevel, paths, sw.ElapsedMilliseconds);
        DrawPng(ctx.Grid, verts, eNorm, paths, lakeLevel, flow);
        GetTree().Quit();
    }

    /// <summary>统计输出。</summary>
    private void PrintStats(int n, int vn, float[] eNorm, float[] area, int[] flow, byte[] riverLevel, byte[] lakeLevel,
        List<int[]> paths, long ms)
    {
        int[] levelCount = new int[4];
        for (int i = 0; i < vn; i++) levelCount[riverLevel[i]]++;
        // 水量分位数（陆地顶点，辅助标定 waterThreshold）
        var landWater = new List<float>();
        for (int i = 0; i < vn; i++)
            if (eNorm[i] >= 0f && area[i] > 0f) landWater.Add(area[i]);
        if (landWater.Count > 0)
        {
            landWater.Sort();
            float q50 = landWater[landWater.Count / 2];
            float q90 = landWater[(int)(landWater.Count * 0.9)];
            float q99 = landWater[(int)(landWater.Count * 0.99)];
            GD.Print($"[RiverDiag] 水量分位数(陆地): 50%={q50:F0} 90%={q90:F0} 99%={q99:F0}mm");
        }
        int maxLen = 0;
        for (int p = 0; p < paths.Count; p++)
            if (paths[p].Length > maxLen) maxLen = paths[p].Length;
        int lakeCount = 0;
        for (int i = 0; i < lakeLevel.Length; i++) if (lakeLevel[i] > 0) lakeCount++;
        // 盆地候选 = 陆地 flow==自身 的格
        var lakeIds = new List<int>();
        for (int i = 0; i < vn; i++)
            if (eNorm[i] >= 0f && flow[i] == i) lakeIds.Add(i);
        var basinWaters = new List<float>();
        foreach (var b in lakeIds) basinWaters.Add(area[b]);
        basinWaters.Sort();
        if (basinWaters.Count > 0)
        {
            float med = basinWaters[basinWaters.Count / 2];
            float p90 = basinWaters[(int)(basinWaters.Count * 0.9f)];
            GD.Print($"[RiverDiag] 盆地水量: 中位 {med:F0} p90 {p90:F0} 最大 {basinWaters[^1]:F0}mm");
        }
        GD.Print($"[RiverDiag] n={n} 河流格: 1级={levelCount[1]} 2级={levelCount[2]} 3级={levelCount[3]} | 路径 {paths.Count} 条 | 最长 {maxLen} 格 | 盆地候选 {lakeIds.Count} 湖 {lakeCount} | 耗时 {ms}ms");
    }

    /// <summary>等距柱状图（1024×512）：深蓝海/暗绿陆/蓝河/红主河/亮蓝湖。</summary>
    private void DrawPng(SphereGrid grid, Vector3[] verts, float[] eNorm, List<int[]> paths, byte[] lakeLevel, int[] flow)
    {
        const int W = 1024, H = 512;
        var img = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
        img.Fill(new Color(0.06f, 0.10f, 0.18f));   // 深蓝海底
        for (int y = 0; y < H; y++)
        {
            float lat = 90f - 180f * y / (H - 1);
            float la = Mathf.DegToRad(lat);
            float sinLa = Mathf.Sin(la), cosLa = Mathf.Cos(la);
            for (int x = 0; x < W; x++)
            {
                float lon = -180f + 360f * x / (W - 1);
                float lo = Mathf.DegToRad(lon);
                var p = new Vector3(cosLa * Mathf.Cos(lo), sinLa, cosLa * Mathf.Sin(lo));
                int id = grid.NearestId(p);
                if (eNorm[id] >= 0f)
                    img.SetPixel(x, y, new Color(0.18f, 0.26f, 0.15f));   // 陆地
            }
        }
        // 湖泊（亮蓝）
        var paintedLake = new HashSet<int>();
        for (int i = 0; i < lakeLevel.Length; i++)
        {
            if (lakeLevel[i] > 0 && !paintedLake.Contains(i))
            {
                paintedLake.Add(i);
                float lat = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(verts[i].Y, -1f, 1f)));
                float lon = Mathf.RadToDeg(Mathf.Atan2(verts[i].Z, verts[i].X));
                int x = (int)((lon + 180f) / 360f * W), y = (int)((90f - lat) / 180f * H);
                if (x >= 0 && x < W && y >= 0 && y < H) img.SetPixel(x, y, new Color(0.3f, 0.7f, 0.9f));
            }
        }
        // 河流（每条主河道独立颜色：HSL 色相黄金角循环；支流只画独有上游段，遇汇合点截断）
        var painted = new HashSet<int>();
        var pathList = new List<int[]>(paths);
        pathList.Sort((a, b) => b.Length.CompareTo(a.Length));   // 主河优先（最长先画）
        for (int p = 0; p < pathList.Count; p++)
        {
            float hue = (p * 0.6180339887f) % 1f;   // 黄金角：相邻路径色相差最大
            var c = HslToRgb(hue, 0.85f, 0.6f);
            foreach (var v in pathList[p])
            {
                if (painted.Contains(v)) break;   // 遇汇合点（已画过）→ 支流段结束
                painted.Add(v);
                float lat = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(verts[v].Y, -1f, 1f)));
                float lon = Mathf.RadToDeg(Mathf.Atan2(verts[v].Z, verts[v].X));
                int x = (int)((lon + 180f) / 360f * W), y = (int)((90f - lat) / 180f * H);
                if (x >= 0 && x < W && y >= 0 && y < H) img.SetPixel(x, y, c);
            }
        }
        img.SavePng("user://maps/river_diag.png");
        GD.Print("[RiverDiag] saved user://maps/river_diag.png");
    }

    static Color HslToRgb(float h, float s, float l)
    {
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float H2R(float t)
        {
            if (t < 0f) t += 1f;
            if (t > 1f) t -= 1f;
            if (t < 1f / 6f) return p + (q - p) * 6f * t;
            if (t < 1f / 2f) return q;
            if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
            return p;
        }
        return new Color(H2R(h + 1f / 3f), H2R(h), H2R(h - 1f / 3f));
    }
}
