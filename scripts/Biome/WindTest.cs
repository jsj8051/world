using Godot;
using System;
using World.Tectonics;

namespace World.Biome;

/// <summary>
/// 盛行风验证 v2（headless）：
/// 1. 打印典型纬度（20°N 信风 / 50°N 西风）的风向向量，顺转 vs 逆转应镜像
/// 2. 大陆海岸点（迎风/背风）的降水差异：顺转东岸湿润、逆转西岸湿润
/// </summary>
public partial class WindTest : Node
{
    public override void _Ready()
    {
        // 多种子验证：顺转 vs 逆转的海陆降水差异（内陆/海岸比）
        foreach (var seed in new[] { 42, 7, 123, 2024 })
        {
            RunSeed(seed);
        }
        GD.Print("[WindTest] 多种子验证完成");
        GetTree().Quit();
    }

    private void RunSeed(int seed)
    {
        const int n = 16;
        const float radius = 6330f;
        var sim = new TectonicsSimulation(n);
        sim.GenerateInitialCrust(seed);
        sim.SplitIntoPlates(8, seed);
        sim.Run(600f, 2f);
        var verts = sim.GlobalGrid.Vertices;
        var disp = sim.Displacement;
        float sea = sim.SeaLevel;
        int vn = verts.Length;

        System.Func<Vector3, float> elevOf = p =>
        {
            Vector3 dir = p.Normalized();
            int id = sim.GlobalGrid.NearestId(dir);
            float span = Mathf.Max(-(disp[id] - sea), disp[id] - sea);
            return span > 1e-6f ? (disp[id] - sea) / span : 0f;
        };

        // ── 1. 风向向量验证（球面切向）──
        Vector3 At(float latDeg, float lonDeg)
        {
            float la = Mathf.DegToRad(latDeg), lo = Mathf.DegToRad(lonDeg);
            return new Vector3(Mathf.Cos(la) * Mathf.Cos(lo), Mathf.Sin(la), Mathf.Cos(la) * Mathf.Sin(lo));
        }
        var p0 = At(20f, 0f);
        WindField.Prograde = true;
        var wPro0 = WindField.WindAt(p0);
        WindField.Prograde = false;
        var wRetro0 = WindField.WindAt(p0);
        GD.Print($"[WindTest] seed={seed} 20°N 风向: 顺转=({wPro0.X:F2},{wPro0.Y:F2},{wPro0.Z:F2}) 逆转=({wRetro0.X:F2},{wRetro0.Y:F2},{wRetro0.Z:F2})");

        // ── 2. 海岸降水对比 ──
        float[] RunCase(bool prograde)
        {
            WindField.Prograde = prograde;
            var climate = new ClimateGenerator(seed);
            var precip = new float[vn];
            for (int i = 0; i < vn; i++)
            {
                Vector3 p = verts[i] * radius;
                float e1 = elevOf(p);
                precip[i] = climate.ComputePrecipitation(p, e1, elevOf);
            }
            return precip;
        }
        var pro = RunCase(true);
        var retro = RunCase(false);

        // ── 3. 降水直方图（biome 分类阈值带）──
        // 温带阈值：<350 Desert, <700 Grassland, ≥700 Forest
        void Hist(float[] arr, string name)
        {
            int lt250 = 0, lt350 = 0, lt700 = 0, ge700 = 0;
            foreach (var p in arr)
            {
                if (p < 250f) lt250++;          // 真沙漠（柯本）
                else if (p < 350f) lt350++;     // 半干旱
                else if (p < 700f) lt700++;     // 半湿润/草原
                else ge700++;                   // 湿润/森林
            }
            GD.Print($"[WindTest] seed={seed} {name}: <250mm(沙漠)={lt250} 250-350(半干旱)={lt350} 350-700(半湿润)={lt700} >700mm(湿润)={ge700}");
        }
        Hist(pro, "顺转降水");
        Hist(retro, "逆转降水");

        // 全局平均差异
        float avgPro = 0, avgRetro = 0;
        foreach (var p in pro) avgPro += p;
        foreach (var r in retro) avgRetro += r;
        GD.Print($"[WindTest] seed={seed} 全球平均降水: 顺转={avgPro / vn:F0}mm 逆转={avgRetro / vn:F0}mm");

        // ── 2. 海岸降水对比 ──
        int coast = 0;
        float proCoast = 0, retroCoast = 0;
        int inland = 0;
        float proInland = 0, retroInland = 0;
        for (int i = 0; i < vn; i++)
        {
            if (elevOf(verts[i]) <= 0f) continue;
            bool isCoast = false;
            WindField.Prograde = true;
            var w = WindField.WindAt(verts[i]);
            Vector3 up = (verts[i] - w * 0.12f).Normalized();   // 上风向 ~6.9°（跨胞）
            if (elevOf(up) < 0f) isCoast = true;               // 风从海上来 → 迎风海岸
            if (isCoast) { coast++; proCoast += pro[i]; retroCoast += retro[i]; }
            else { inland++; proInland += pro[i]; retroInland += retro[i]; }
        }
        if (coast > 0)
            GD.Print($"[WindTest] seed={seed} 迎风海岸 {coast} 点: 顺转={proCoast / coast:F0}mm 逆转={retroCoast / coast:F0}mm (差={(retroCoast - proCoast) / (proCoast / coast) / coast * 100:F1}%)");
        if (inland > 0)
            GD.Print($"[WindTest] seed={seed} 内陆 {inland} 点: 顺转={proInland / inland:F0}mm 逆转={retroInland / inland:F0}mm");
        GD.Print($"[WindTest] seed={seed} 完成");
    }
}
