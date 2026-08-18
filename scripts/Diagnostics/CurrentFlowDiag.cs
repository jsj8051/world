using Godot;
using World.MapGen;
using World.MapView;
using World.MapView.Layers;
using World.Services;

namespace World.Diagnostics;

/// <summary>洋流箭头流图验证（2026-08-21 v3）：CurrentFlow 组件（整体流图 + 箭头）构建验证。
/// 存档直读（-- --arch=...）：构建流线/箭头 → 导出等距柱状箭头图 + 交叉计数。用法：
///   godot --headless --path E:/godotGames/world --quit-after 60 res://scenes/diag/CurrentFlowDiag.tscn -- --arch=user://maps/xxx.mpa
/// </summary>
public partial class CurrentFlowDiag : DiagSceneBase
{
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

        var flow = new CurrentFlow();
        AddChild(flow);

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

        flow.Build(map, map.RadiusKm);

        int lines = flow.LineCount;
        int arrows = flow.ArrowCount;
        LogService.Log("CurrentFlowDiag", $"流线 {lines} 条 / 箭头 {arrows}（{path}）");
        if (lines == 0)
        {
            LogService.LogErr("CurrentFlowDiag", "失败：无有效流线（洋流场全弱）");
            GetTree().Quit(1);
            return;
        }

        // 导出等距柱状箭头图（同 StreamlineDiag 投影）——直接看箭头流图形态
        ExportArrowsPNG(map, flow);
        // 交叉计数：流线数学上不相交（平滑场积分曲线定理）；此检查量化渲染层的残留
        LogService.Log("CurrentFlowDiag", $"流线交叉对：{CountCrossings(flow.ExportedLines())}");
    }

    /// <summary>交叉计数：任意两流线的 3D 弦段相交且交点近球面（|X|≈1）= 视觉交叉。
    /// 平滑方向场的积分曲线不相交（定理）；残留交叉 = 折线离散误差或采样残留问题。
    /// 流线过多时跳过（O(L²·pts²) 太慢）。</summary>
    private static int CountCrossings(CurrentFlow.ExportedLine[] lines)
    {
        if (lines.Length > 300) return -1;   // 太多跳过（-1 = 未统计；稠密流图靠定理保证）
        int count = 0;
        for (int li = 0; li < lines.Length; li++)
        {
            var A = lines[li].Pts;
            // 同线自交叉（非相邻段）
            for (int i = 1; i < A.Length - 2; i++)
                for (int j = i + 2; j < A.Length; j++)
                    if (SegmentsCross(A[i - 1], A[i], A[j - 1], A[j])) count++;
            // 异线交叉
            for (int lj = li + 1; lj < lines.Length; lj++)
            {
                var B = lines[lj].Pts;
                for (int i = 1; i < A.Length; i++)
                    for (int j = 1; j < B.Length; j++)
                        if (SegmentsCross(A[i - 1], A[i], B[j - 1], B[j])) count++;
            }
        }
        return count;
    }

    /// <summary>3D 弦段相交判定：最近点参数 t,s ∈ [0,1]，最近点间距 < 线宽（真交叉，
    /// 非擦肩而过），且交点距球心 ≈ 1（表面弧交叉，非球内穿行）。</summary>
    private static bool SegmentsCross(Vector3 a1, Vector3 a2, Vector3 b1, Vector3 b2)
    {
        Vector3 u = a2 - a1, v = b2 - b1;
        Vector3 w = a1 - b1;
        float uu = u.Dot(u), vv = v.Dot(v), uv = u.Dot(v);
        float denom = uu * vv - uv * uv;
        if (Mathf.Abs(denom) < 1e-12f) return false;                 // 平行
        float wu = w.Dot(u), wv = w.Dot(v);
        float t = (uv * wv - vv * wu) / denom;
        float s = (uu * wv + uv * wu) / denom;
        if (t < 0f || t > 1f || s < 0f || s > 1f) return false;
        Vector3 x = a1 + u * t;                                      // 最近点
        // 最近点间距必须 < 线宽（~0.003 rad）——否则是擦肩而过不是交叉
        float gap = (x - (b1 + v * s)).Length();
        if (gap > 0.003f) return false;
        return Mathf.Abs(x.Length() - 1f) < 0.05f;                   // 近表面 = 表面弧交叉
    }

    /// <summary>把 CurrentFlow 的箭头流图画到 1024×512 等距柱状 PNG
    /// （暖=红橙 寒=蓝；箭头 = 三角箭头，同渲染层几何）。</summary>
    private static void ExportArrowsPNG(MapData map, CurrentFlow flow)
    {
        const int w = 1024, h = 512;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        img.Fill(new Color(0.06f, 0.10f, 0.16f));   // 深底（海洋）

        // 陆地轮廓（暗绿）定位
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

        // 箭头：沿流线每 ~8 点画一个三角箭头（方向 = 流线切向；暖流红橙/寒流蓝）
        const int arrowStep = 8;
        foreach (var line in flow.ExportedLines())
        {
            int n = line.Pts.Length;
            for (int i = arrowStep; i < n - arrowStep / 2; i += arrowStep)
            {
                var dir = line.Dirs[i];
                if (dir.LengthSquared() < 1e-9f) continue;
                var col = WarmthColor(line.Warmth[i]);
                PlotArrow(img, line.Pts[i], dir, col);
            }
        }

        img.SavePng("user://maps/current_flow_diag.png");
        LogService.Log("CurrentFlowDiag", "saved user://maps/current_flow_diag.png");
    }

    /// <summary>在等距柱状图上画三角箭头（尖头 + 短杆；约 4px 长）。</summary>
    private static void PlotArrow(Image img, Vector3 pos, Vector3 dir, Color col)
    {
        Vector3 tip = pos + dir * 0.03f;
        Vector3 tail = pos - dir * 0.02f;
        Vector3 side = pos.Cross(dir).Normalized() * 0.012f;
        PlotLine(img, (pos + side).Normalized(), tip.Normalized(), col, col);
        PlotLine(img, (pos - side).Normalized(), tip.Normalized(), col, col);
        PlotLine(img, (pos + side).Normalized(), (pos - side).Normalized(), col, col);
        PlotLine(img, pos, tail, col, col);
    }

    private static void PlotLine(Image img, Vector3 a, Vector3 b, Color ca, Color cb)
    {
        // 在等距柱状图上画线段（线性插值像素）
        int x0 = LonToX(a), y0 = LatToY(a), x1 = LonToX(b), y1 = LatToY(b);
        int steps = Mathf.Max(1, Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)));
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(x0, x1, t)), 0, 1023);
            int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(y0, y1, t)), 0, 511);
            img.SetPixel(x, y, ca.Lerp(cb, t));
        }
    }

    private static int LonToX(Vector3 p)
        => Mathf.Clamp((int)((Mathf.RadToDeg(Mathf.Atan2(p.Z, p.X)) + 180f) / 360f * 1024f), 0, 1023);

    private static int LatToY(Vector3 p)
        => Mathf.Clamp((int)((90f - Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f)))) / 180f * 512f), 0, 511);

    private static Color WarmthColor(float w)
    {
        float t = Mathf.Clamp(w, -1f, 1f);
        return t < 0f
            ? new Color(0.25f, 0.55f, 1f).Lerp(new Color(0.90f, 0.92f, 1f), -t)
            : new Color(0.90f, 0.92f, 1f).Lerp(new Color(1f, 0.45f, 0.15f), t);
    }
}
