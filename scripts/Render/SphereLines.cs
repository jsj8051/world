using Godot;
using System;
using System.Collections.Generic;

namespace World.Render;

// 球面画线工具（2026-09-10 自 new_HexWorld 板块边界描边抽出，任意球心/半径可复用）：
// 把球面世界坐标点串铺成三角线带 + 角点端帽圆盘（Red Blob Games "Hex Region Borders"
// 共享边法 + 圆角接头），两遍深度预写渲染——第一遍（boundary_line_depth）以 ALPHA=0
// 隐形光栅化但【写深度】，第二遍（boundary_line_color，NextPass）只画深度命中的最近层
// → 线带/端帽在角点处必然几何重叠，每像素却恰混色一次，半透明线色也不叠加变深。
// 遍顺序由 RenderPriority 钉住（透明队列按深度排序会乱，实测必钉）；抗锯齿走视口 MSAA。
// 方案固有语义（非本类缺陷，多实例时须知晓）：
//   · 实例之间不混色——深度预写按实例计，屏幕重叠时只有更近实例的线可见；
//     需要共存的线（如板块边界 + 标注线都要半透明可见）应塞进同一次 SetLines。
//   · 隐形深度会挡住其后渲染、被线盖住像素上的透明几何（不透明几何先渲染，不受影响）。
public partial class SphereLines : Node3D
{
    ShaderMaterial _colorMat;      // 第二遍上色材质（LineColor 的唯一去处）
    MeshInstance3D _instance;      // 线几何渲染产物（SetLines 整体重交；静态线建一次不动）
    Color _lineColor = Colors.Black;

    // 端帽圆盘扇段数（拐角圆角细腻度；12 段在格尺度下已圆滑）。
    public int CapSegments { get; set; } = 12;

    // 线色（a 任意——深度预写保证每像素只混一次，半透明不会叠加变深；a=1 退化为不透明线）。
    public Color LineColor
    {
        get => _lineColor;
        set
        {
            _lineColor = value;
            _colorMat.SetShaderParameter("line_color", value);
        }
    }

    public SphereLines()
    {
        _colorMat = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/boundary_line_color.gdshader"),
            RenderPriority = 1,   // 必须 > depth 遍（默认 0）：透明队列同深度会乱序，实测必钉
        };
        var lineDepthMat = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/boundary_line_depth.gdshader"),
            NextPass = _colorMat,
        };
        _instance = new MeshInstance3D
        {
            MaterialOverride = lineDepthMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_instance);
    }

    // 画线（整体替换上次几何；清线用 Clear）。strips = 球面世界坐标点串（每串一条折线，
    // 相邻两点成一段，段内球面弧按 maxSegmentArc 步长自适应细分）；每段两端补角半径 =
    // 线半宽的端帽圆盘填楔形缺口（圆角衔接，全实例按坐标去重，共享角点只补一次帽）。
    // halfWidth = 线半宽（世界单位）；surfaceOffset = 线相对球面的悬浮高度（防与宿面
    // z-fighting，默认半宽的 10%——只须压过线/宿面两套三角的弦高差与深度量化）；
    // center = 球心（默认原点）。参数须 radius > 0、maxSegmentArc > 0。
    public void SetLines(IReadOnlyList<IReadOnlyList<Vector3>> strips, float radius,
        float halfWidth, float maxSegmentArc, float? surfaceOffset = null,
        Vector3? center = null)
    {
        Vector3 c = center ?? Vector3.Zero;
        float lift = surfaceOffset ?? halfWidth * 0.1f;
        float rTop = radius + lift;
        float alpha = halfWidth / radius;              // 线半宽（球面角，rad）
        float sinA = MathF.Sin(alpha), cosA = MathF.Cos(alpha);
        float step = MathF.Max(maxSegmentArc, 1e-6f);
        int capSegs = CapSegments;

        var verts = new List<Vector3>();
        var idx = new List<int>();
        var capped = new HashSet<Vector3>();           // 每个角点坐标只补一次帽

        foreach (var strip in strips)
        {
            if (strip == null || strip.Count == 0) continue;
            foreach (Vector3 p in strip)
                Cap(p);
            for (int i = 0; i + 1 < strip.Count; i++)
                Ribbon(strip[i], strip[i + 1]);
        }

        if (verts.Count == 0)
        {
            _instance.Mesh = null;
            return;
        }
        var am = new ArrayMesh();
        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
        am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        _instance.Mesh = am;
        return;

        // 线带：单段球面弧采点，左右各偏半宽连成三角带（端点按单位方向处理，落回 rTop + 球心）。
        void Ribbon(Vector3 p0, Vector3 p1)
        {
            Vector3 a = (p0 - c).Normalized();
            Vector3 b = (p1 - c).Normalized();
            Vector3 chord = (b - a).Normalized();      // 弦方向（段弧短时作全程切向足够）
            float theta = a.AngleTo(b);
            if (theta < 1e-6f) return;                 // 零长段：端帽已覆盖，跳过带体
            int subs = Math.Clamp((int)MathF.Ceiling(theta * radius / step), 1, 64);
            int ring0 = verts.Count;
            for (int s = 0; s <= subs; s++)
            {
                Vector3 d = a.Slerp(b, (float)s / subs).Normalized();   // 弧采点（球面方向）
                // 侧偏方向 ⊥ 弧点、⊥切向：d 与 chord 同在 {a,b} 张成的平面 → 叉指恒同侧不扭转
                Vector3 n = d.Cross(chord).Normalized();
                verts.Add((cosA * d - sinA * n) * rTop + c);            // 左环点
                verts.Add((cosA * d + sinA * n) * rTop + c);            // 右环点
            }
            for (int s = 0; s < subs; s++)
            {
                int l0 = ring0 + 2 * s, r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
                idx.Add(l0); idx.Add(l1); idx.Add(r0);
                idx.Add(r0); idx.Add(l1); idx.Add(r1);
            }
        }

        // 端帽：角点处角半径 α 的扇形圆盘（三点扇心 + 首尾重复接缝环）。
        void Cap(Vector3 p)
        {
            if (!capped.Add(p)) return;
            Vector3 dir = (p - c).Normalized();
            Vector3 up = Math.Abs(dir.Y) < 0.98f ? Vector3.Up : Vector3.Right;
            Vector3 e1 = up.Cross(dir).Normalized();
            Vector3 e2 = dir.Cross(e1);
            int v0 = verts.Count;
            verts.Add(dir * rTop + c);
            for (int k = 0; k <= capSegs; k++)
            {
                float phi = k * MathF.Tau / capSegs;
                Vector3 t = MathF.Cos(phi) * e1 + MathF.Sin(phi) * e2;
                verts.Add((cosA * dir + sinA * t) * rTop + c);
            }
            for (int k = 0; k < capSegs; k++)
            {
                idx.Add(v0); idx.Add(v0 + 1 + k); idx.Add(v0 + 2 + k);
            }
        }
    }

    // 清线（隐藏网格；下次 SetLines 直接重交）。
    public void Clear()
    {
        _instance.Mesh = null;
    }
}
