using Godot;
using World.MapGen;
using World.MapView;
using World.MapView.Layers;
using World.Services;

namespace World.Diagnostics;

/// <summary>洋流粒子动画验证（2026-08-21 v4）：CurrentFlow 组件（cambecc 式粒子动画）构建验证。
/// 存档直读（-- --arch=...）：构建 → 跑 ~120 帧动画 → 统计粒子/重生 → 导出等距柱状粒子图。用法：
///   godot --headless --path E:/godotGames/world --quit-after 200 res://scenes/diag/CurrentFlowDiag.tscn -- --arch=user://maps/xxx.mpa
/// </summary>
public partial class CurrentFlowDiag : DiagSceneBase
{
    private CurrentFlow _flow;
    private int _frame;

    public override void _Ready()
    {
        string arch = ArchiveDiag.ResolveArchPath();
        string path = arch ?? "user://maps/map1.mpa";
        if (!MapArchive.Read(path, out var map) || map.CurrentDirs == null)
        {
            LogService.LogErr("CurrentFlowDiag", $"存档无洋流段: {path}");
            GetTree().Quit(1);
            return;
        }

        _flow = new CurrentFlow();
        AddChild(_flow);

        // 存档洋流场统计（诊断用：全零/微量/正常 判定 + 重算触发观察）
        int c12 = 0, c9 = 0, c6 = 0, c1 = 0;
        for (int i = 0; i < map.CurrentDirs.Length; i++)
        {
            float m2 = map.CurrentDirs[i].LengthSquared();
            if (m2 > 1e-12f) c12++;
            if (m2 > 1e-9f) c9++;
            if (m2 > 1e-6f) c6++;
            if (m2 > 0.1f) c1++;
        }
        LogService.Log("CurrentFlowDiag", $"存档 CurrentDirs: |d|²>1e-12={c12} >1e-9={c9} >1e-6={c6} >0.1={c1}");

        _flow.Build(map, map.RadiusKm);
        if (!_flow.IsBuilt)
        {
            // 2026-08-21 用户拍板去掉兜底：场异常 → 不渲染（预期行为，非错误）
            LogService.Log("CurrentFlowDiag", "存档洋流场异常 → 粒子动画未构建（预期：旧档不渲染洋流）");
            GetTree().Quit(0);
            return;
        }
        LogService.Log("CurrentFlowDiag", $"构建完成：粒子 {_flow.ParticleCountConst}（{path}）");
    }

    public override void _Process(double delta)
    {
        if (_flow == null) return;
        _frame++;
        // 跑 ~120 帧让粒子散布开（重生/自平流稳定后导出才有流带形态）
        if (_frame >= 120)
        {
            LogService.Log("CurrentFlowDiag", $"动画 {_frame} 帧后：粒子 {_flow.ParticleCountConst} / 累计重生 {_flow.ResetCount} / " +
                $"重生率 {(float)_flow.ResetCount / _frame:F1} 次/帧");
            ExportParticlesPNG(_flow);
            GetTree().Quit(0);
        }
    }

    /// <summary>把 CurrentFlow 粒子快照画到 1024×512 等距柱状 PNG（暖=红橙 寒=蓝；2×2 像素点）。</summary>
    private static void ExportParticlesPNG(CurrentFlow flow)
    {
        const int w = 1024, h = 512;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        img.Fill(new Color(0.06f, 0.10f, 0.16f));   // 深底（海洋）

        // 陆地轮廓（暗绿）定位——粒子只画在海上的验证
        var map = flow.MapSnapshot;
        if (map != null)
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
                    if (map.SampleSpherical(p, map.Elev) >= 0f)
                        img.SetPixel(x, y, new Color(0.14f, 0.20f, 0.11f));
                }
            }

        // 粒子点（2×2 块，颜色 = 粒子冷暖色）
        foreach (var pt in flow.ExportParticles())
        {
            int px = LonToX(pt.Pos), py = LatToY(pt.Pos);
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int xx = Mathf.Clamp(px + dx, 0, w - 1);
                    int yy = Mathf.Clamp(py + dy, 0, h - 1);
                    img.SetPixel(xx, yy, pt.Col);
                }
        }

        img.SavePng("user://maps/current_flow_diag.png");
        LogService.Log("CurrentFlowDiag", "saved user://maps/current_flow_diag.png");
    }

    private static int LonToX(Vector3 p)
        => Mathf.Clamp((int)((Mathf.RadToDeg(Mathf.Atan2(p.Z, p.X)) + 180f) / 360f * 1024f), 0, 1023);

    private static int LatToY(Vector3 p)
        => Mathf.Clamp((int)((90f - Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f)))) / 180f * 512f), 0, 511);
}
