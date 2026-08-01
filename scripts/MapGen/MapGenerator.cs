using Godot;
using System;
using System.Threading.Tasks;
using World.Biome;
using World.Surface;

namespace World.MapGen;

/// <summary>
/// 地图生成器（生成阶段，离线）：海拔场 + 气候场（温度/降水）→ 生物群系 →
/// 等距柱状投影纹理 → 地图存档（v2）。
///
/// 生成与游玩解耦：生成可花费数十分钟，产出存档；游玩只读存档。
/// 场 = 无损中间表示；逻辑网格（n 值）是以后的"从场导出"步骤，与第一版无关。
/// </summary>
public partial class MapGenerator : Node
{
    [Export] public int Seed = 42;
    [Export] public int Width = 2048;   // 等距柱状纹理宽（像素）
    [Export] public int Height = 1024;  // 高（像素）
    [Export] public float RadiusKm = 6330f;
    [Export] public string OutputPath = "user://maps/map1.mpa";
    [Export] public bool AutoQuit = false; // true=生成后退出；false=切到查看场景

    public override void _Ready()
    {
        Generate();
        if (AutoQuit)
            GetTree().Quit();
        else
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/MapViewer.tscn");
    }

    public void Generate()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var surface = new SurfaceGenerator(Seed);
        var climate = new ClimateGenerator(Seed);
        var elev = new float[Width * Height];
        var temp = new float[Width * Height];
        var precip = new float[Width * Height];
        var biome = new byte[Width * Height];

        // ── 第一遍：海拔（Parallel：FastNoiseLite 只读调用，线程安全）──
        Parallel.For(0, Height, y =>
        {
            float lat = 90f - 180f * y / (Height - 1);          // y=0 北纬 90
            float la = Mathf.DegToRad(lat);
            float sinLa = Mathf.Sin(la), cosLa = Mathf.Cos(la);
            for (int x = 0; x < Width; x++)
            {
                float lon = -180f + 360f * x / (Width - 1);
                float lo = Mathf.DegToRad(lon);
                Vector3 p = new Vector3(
                    cosLa * Mathf.Cos(lo),
                    sinLa,
                    cosLa * Mathf.Sin(lo)) * RadiusKm;
                elev[y * Width + x] = surface.ComputeElevation(p);
            }
            if (y % (Height / 10) == 0)
                GD.Print($"[MapGenerator] row {y}/{Height} (elev) ...");
        });

        float minE = float.MaxValue, maxE = float.MinValue;
        foreach (var e in elev)
        {
            if (e < minE) minE = e;
            if (e > maxE) maxE = e;
        }
        float eRange = maxE - minE;

        // ── 第二遍：气候 + 生物群系 ──
        Parallel.For(0, Height, y =>
        {
            float lat = 90f - 180f * y / (Height - 1);
            float la = Mathf.DegToRad(lat);
            float sinLa = Mathf.Sin(la), cosLa = Mathf.Cos(la);
            for (int x = 0; x < Width; x++)
            {
                float lon = -180f + 360f * x / (Width - 1);
                float lo = Mathf.DegToRad(lon);
                Vector3 p = new Vector3(
                    cosLa * Mathf.Cos(lo),
                    sinLa,
                    cosLa * Mathf.Sin(lo)) * RadiusKm;
                int idx = y * Width + x;
                float eNorm = eRange > 1e-6f ? (elev[idx] - minE) / eRange : 0.5f; // 0..1
                float e1 = eNorm * 2f - 1f; // -1..1，0=海平面（气候与分类的约定）
                float t = climate.ComputeTemperature(p, e1);
                float pp = climate.ComputePrecipitation(p, e1);
                temp[idx] = t;
                precip[idx] = pp;
                biome[idx] = (byte)BiomeClassifier.Classify(e1, t, pp);
            }
            if (y % (Height / 10) == 0)
                GD.Print($"[MapGenerator] row {y}/{Height} (climate) ...");
        });

        // ── 统计：场范围 + biome 分布 ──
        float minT = float.MaxValue, maxT = float.MinValue;
        float minP = float.MaxValue, maxP = float.MinValue;
        var dist = new int[13];
        foreach (var t in temp) { if (t < minT) minT = t; if (t > maxT) maxT = t; }
        foreach (var p in precip) { if (p < minP) minP = p; if (p > maxP) maxP = p; }
        foreach (var b in biome) dist[b]++;
        sw.Stop();

        GD.Print($"[MapGenerator] seed={Seed} {Width}x{Height} elev[{minE:F4},{maxE:F4}] " +
                 $"temp[{minT:F1},{maxT:F1}]°C precip[{minP:F0},{maxP:F0}]mm took {sw.ElapsedMilliseconds}ms");
        long total = (long)Width * Height;
        var sb = new System.Text.StringBuilder("[MapGenerator] biome dist: ");
        for (int i = 0; i < dist.Length; i++)
        {
            var name = ((BiomeType)i).ToString();
            sb.Append($"{name}={dist[i]}({dist[i] * 100.0 / total:F1}%) ");
        }
        GD.Print(sb.ToString());

        MapArchive.Write(OutputPath, Seed, Width, Height, minE, maxE, elev,
            temp, precip, biome, minT, maxT, minP, maxP);
    }
}
