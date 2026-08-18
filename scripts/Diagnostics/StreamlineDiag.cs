using Godot;
using World.MapGen;
using World.Services;

namespace World.Diagnostics;

/// <summary>流线诊断：用存档洋流场追踪流线，画到等距柱状图验证形状。</summary>
public partial class StreamlineDiag : DiagSceneBase
{
    public override void _Ready()
    {
        // 存档直读（-- --arch=...）：指定地图；默认 user://maps/map1.mpa（开发规范 §4 约定）
        string arch = ArchiveDiag.ResolveArchPath();
        string path = arch ?? "user://maps/map1.mpa";
        if (!MapArchive.Read(path, out var map) || map.CurrentDirs == null)
        {
            LogService.LogErr("StreamlineDiag", $"存档无洋流段: {path}");
            GetTree().Quit();
            return;
        }

        const int W = 1024, H = 512;
        var img = Image.CreateEmpty(W, H, false, Image.Format.Rgba8);
        img.Fill(new Color(0.08f, 0.10f, 0.14f));   // 深底

        // 画流线：从种子沿 CurrentDirs 追踪
        int lineCount = 0, ptCount = 0;
        for (float lat = -82.5f; lat <= 82.5f; lat += 7.5f)
        {
            float la = Mathf.DegToRad(lat);
            float cosLa = Mathf.Cos(la);
            int lonCount = Mathf.Max(10, Mathf.RoundToInt(48 * cosLa));
            for (int j = 0; j < lonCount; j++)
            {
                float lo = Mathf.Tau * j / lonCount;
                var seed = new Vector3(cosLa * Mathf.Cos(lo), Mathf.Sin(la), cosLa * Mathf.Sin(lo));
                // 种子必须在海洋
                if (map.SampleSpherical(seed, map.Elev) >= 0f) continue;

                var line = new System.Collections.Generic.List<Vector3> { seed };
                Vector3 pos = seed;
                bool valid = true;
                for (int s = 0; s < 120; s++)
                {
                    int id = map.NearestVertex(pos);
                    if (map.SampleSpherical(pos, map.Elev) >= 0f) { valid = false; break; }
                    Vector3 dir = map.CurrentDirs[id];
                    if (dir.LengthSquared() < 1e-9f) { valid = false; break; }
                    Vector3 next = (pos + dir * 0.03f).Normalized();
                    if (line.Count > 12 && (next - seed).Length() < 0.08f) break;
                    line.Add(next);
                    pos = next;
                }
                if (!valid || line.Count < 15) continue;
                lineCount++;

                // 画流线（白）
                foreach (var p in line)
                {
                    float pl = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f)));
                    float po = Mathf.RadToDeg(Mathf.Atan2(p.Z, p.X));
                    int x = (int)((po + 180f) / 360f * W);
                    int y = (int)((90f - pl) / 180f * H);
                    x = Mathf.Clamp(x, 0, W - 1); y = Mathf.Clamp(y, 0, H - 1);
                    img.SetPixel(x, y, new Color(0.9f, 0.9f, 0.95f));
                    ptCount++;
                }
            }
        }

        // 画陆地轮廓（暗绿）帮助定位
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
                if (map.SampleSpherical(p, map.Elev) >= 0f)
                    img.SetPixel(x, y, new Color(0.15f, 0.22f, 0.12f));
            }
        }

        img.SavePng("user://maps/streamline_diag.png");
        LogService.Log("StreamlineDiag", $"流线 {lineCount} 条 / {ptCount} 点 → user://maps/streamline_diag.png");
        GetTree().Quit();
    }
}
