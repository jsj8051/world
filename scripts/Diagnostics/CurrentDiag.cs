using Godot;
using World.Biome;
using World.MapGen;
using World.Tectonics;

namespace World.Diagnostics;

/// <summary>洋流验证：顺转/逆转的洋流冷暖分布 + 导出冷暖图。
/// 存档直读（-- --arch=...）：跳板块模拟，用存档海拔重算洋流（秒级）。</summary>
public partial class CurrentDiag : Node
{
    public override void _Ready()
    {
        string arch = ArchiveDiag.ResolveArchPath();
        if (arch != null)
        {
            if (!ArchiveDiag.TryLoad(arch, out var ctx))
            {
                GD.PrintErr("[CurrentDiag] 存档直读失败");
                GetTree().Quit(1);
                return;
            }
            RunFromArchive(ctx);
            return;
        }
        RunSim();
    }

    /// <summary>原流程：n=16 板块模拟 600My（慢，~30s）→ 洋流两种自转方向对比。</summary>
    private void RunSim()
    {
        const int n = 16;
        var sim = new TectonicsSimulation(n);
        sim.GenerateInitialCrust(42);
        sim.SplitIntoPlates(8, 42);
        sim.Run(600f, 2f);
        var verts = sim.GlobalGrid.Vertices;
        var disp = sim.Displacement;
        float sea = sim.SeaLevel;
        int vn = verts.Length;

        var eNorm = new float[vn];
        float span = 0f;
        for (int i = 0; i < vn; i++) span = Mathf.Max(span, Mathf.Abs(disp[i] - sea));
        for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? (disp[i] - sea) / span : 0f;

        Export(sim.GlobalGrid, eNorm, "pro", true);
        Export(sim.GlobalGrid, eNorm, "retro", false);
        GetTree().Quit();
    }

    /// <summary>存档直读：用存档海拔（归一化）重算洋流，两种自转方向对比——改 OceanCurrent 后秒级验证。</summary>
    private void RunFromArchive(DiagContext ctx)
    {
        Export(ctx.Grid, ctx.ElevNorm, "pro", true);
        Export(ctx.Grid, ctx.ElevNorm, "retro", false);
        GetTree().Quit();
    }

    /// <summary>统计暖/寒流海洋顶点 + 导出 1024×512 冷暖图（暖=红 寒=蓝 白=无）。</summary>
    private void Export(SphereGrid grid, float[] eNorm, string name, bool prograde)
    {
        WindField.Prograde = prograde;
        WindField.RotationSpeed = 1f;
        OceanCurrent.Compute(grid.Vertices, grid.Neighbors, eNorm, out _, out var warmth, out _);

        int vn = eNorm.Length;
        int warm = 0, cold = 0;
        for (int i = 0; i < vn; i++)
        {
            if (warmth[i] > 0.3f) warm++;
            else if (warmth[i] < -0.3f) cold++;
        }
        GD.Print($"[CurrentDiag] {(prograde ? "顺转" : "逆转")}: 暖流海洋顶点={warm} 寒流海洋顶点={cold}");

        const int w = 1024, h = 512;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            float lat = 90f - 180f * y / (h - 1);
            float la = Mathf.DegToRad(lat);
            float sinLa = Mathf.Sin(la), cosLa = Mathf.Cos(la);
            for (int x = 0; x < w; x++)
            {
                float lon = -180f + 360f * x / (w - 1);
                float lo = Mathf.DegToRad(lon);
                var p = new Vector3(cosLa * Mathf.Cos(lo), sinLa, cosLa * Mathf.Sin(lo));
                int id = grid.NearestId(p);
                float v = eNorm[id] >= 0f ? 0f : warmth[id];   // 内陆不显示
                // 暖流=红、寒流=蓝、无=白
                Color c;
                if (v > 0.05f) c = new Color(1f, 0.3f, 0.2f).Lerp(new Color(1f, 0.9f, 0.5f), 1f - v);
                else if (v < -0.05f) c = new Color(0.3f, 0.6f, 1f).Lerp(new Color(0.6f, 0.9f, 1f), 1f + v);
                else c = new Color(0.95f, 0.95f, 0.95f);
                img.SetPixel(x, y, c);
            }
        }
        img.SavePng($"user://maps/current_{name}.png");
        GD.Print($"[CurrentDiag] saved current_{name}.png");
    }
}
