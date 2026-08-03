using Godot;
using World.Biome;
using World.Tectonics;

namespace World.Diagnostics;

/// <summary>洋流验证：顺转/逆转的洋流冷暖分布 + 导出冷暖图。</summary>
public partial class CurrentDiag : Node
{
    public override void _Ready()
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

        // 导出冷暖图（lat-lon 网格采样，最近顶点冷暖）
        void Export(string name, bool prograde)
        {
            WindField.Prograde = prograde;
            WindField.RotationSpeed = 1f;
            OceanCurrent.Compute(verts, sim.GlobalGrid.Neighbors, eNorm, out _, out var warmth, out _);

            // 统计：暖流/寒流海洋顶点数
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
                    int id = sim.GlobalGrid.NearestId(p);
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

        Export("pro", true);
        Export("retro", false);
        GetTree().Quit();
    }
}
