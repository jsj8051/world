using Godot;
using System;
using World.Biome;
using World.MapGen;
using World.Services;
using World.Tectonics;

namespace World.Diagnostics;

/// <summary>
/// 自转速度验证（headless）：同 seed 下 0.2×/1×/5× 的降水空间分布差异。
/// 期望：慢速 → 科里奥利弱 → 风带模糊 → 东西岸降水差小；
///       快速 → 西风/信风强偏转 → 东西岸降水差大。
/// </summary>
public partial class WindTest : DiagSceneBase
{
    public override void _Ready()
    {
        string arch = ArchiveDiag.ResolveArchPath();
        if (arch != null)
        {
            if (!ArchiveDiag.TryLoad(arch, out var ctx))
            {
                LogService.LogErr("WindTest", "存档直读失败");
                GetTree().Quit(1);
                return;
            }
            RunFromArchive(ctx);
        }
        else
        {
            // 原流程（无存档）：每 seed 重跑板块模拟（n=16 600My）
            RunSeed(42);
            RunSeed(7);
        }
        LogService.Log("WindTest", "完成");
        GetTree().Quit();
    }

    /// <summary>存档直读：用存档海拔重算降水，扫多速度——改 ClimateGenerator 后秒级验证雨影。</summary>
    private void RunFromArchive(DiagContext ctx)
    {
        const float radius = MapArchive.DefaultRadiusKm;   // 坐标标度（纬度驱动，R 任意）
        int vn = ctx.VertexCount;
        var verts = ctx.Verts;
        var eNorm = ctx.ElevNorm;

        System.Func<Vector3, float> elevOf = p => eNorm[ctx.Grid.NearestId(p)];

        foreach (var sp in new[] { 0.2f, 1f, 5f })
        {
            WindField.Prograde = ctx.Map.ProgradeRotation;
            WindField.RotationSpeed = sp;
            var climate = new ClimateGenerator(ctx.Map.Seed);
            var precip = new float[vn];
            for (int i = 0; i < vn; i++)
            {
                Vector3 p = verts[i] * radius;
                float e1 = eNorm[i];
                precip[i] = climate.ComputePrecipitation(p, e1, elevOf);
            }

            int coast = 0, inland = 0;
            float coastP = 0, inlandP = 0;
            for (int i = 0; i < vn; i++)
            {
                if (eNorm[i] <= 0f) continue;
                var w = WindField.WindAt(verts[i]);
                Vector3 up = (verts[i] - w * 0.12f).Normalized();
                if (eNorm[ctx.Grid.NearestId(up)] < 0f) { coast++; coastP += precip[i]; }
                else { inland++; inlandP += precip[i]; }
            }
            string c = coast > 0 ? $"{coastP / coast:F0}mm" : "N/A";
            string inc = inland > 0 ? $"{inlandP / inland:F0}mm" : "N/A";
            LogService.Log("WindTest", $"直读 seed={ctx.Map.Seed} 速度{sp}×: 迎风海岸({coast}点)={c} 内陆({inland}点)={inc} 海陆差={(coast > 0 && inland > 0 ? coastP / coast - inlandP / inland : 0):F0}mm");
        }
    }

    private void RunSeed(int seed)
    {
        const int n = 16;
        const float radius = MapArchive.DefaultRadiusKm;   // 坐标标度（纬度驱动，R 任意）
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

        foreach (var sp in new[] { 0.2f, 1f, 5f })
        {
            WindField.Prograde = true;
            WindField.RotationSpeed = sp;
            var climate = new ClimateGenerator(seed);
            var precip = new float[vn];
            for (int i = 0; i < vn; i++)
            {
                Vector3 p = verts[i] * radius;
                float e1 = elevOf(p);
                precip[i] = climate.ComputePrecipitation(p, e1, elevOf);
            }

            // 海岸点（上风向是海）vs 内陆
            int coast = 0, inland = 0;
            float coastP = 0, inlandP = 0;
            for (int i = 0; i < vn; i++)
            {
                if (elevOf(verts[i]) <= 0f) continue;
                var w = WindField.WindAt(verts[i]);
                Vector3 up = (verts[i] - w * 0.12f).Normalized();
                if (elevOf(up) < 0f) { coast++; coastP += precip[i]; }
                else { inland++; inlandP += precip[i]; }
            }
            string c = coast > 0 ? $"{coastP / coast:F0}mm" : "N/A";
            string inc = inland > 0 ? $"{inlandP / inland:F0}mm" : "N/A";
            LogService.Log("WindTest", $"seed={seed} 速度{sp}×: 迎风海岸({coast}点)={c} 内陆({inland}点)={inc} 海陆差={(coast > 0 && inland > 0 ? coastP / coast - inlandP / inland : 0):F0}mm");
        }
    }
}
