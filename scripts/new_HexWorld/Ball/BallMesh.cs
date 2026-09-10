using System;
using System.Collections.Generic;
using Godot;
using World.NewHexWorld;
using World.Utils.H3;

namespace World.NewHexWorld;

public class BallMesh
{

    public Vector3[] DisplayVerts => _displayVerts;    // 渲染用：显示网格顶点
    public int[] DisplayIndices => _displayIndices;    // 渲染用：三角索引
    public Vector2[] DisplayUv => _displayUv;          // 渲染用：UV = 格纹素中心（region_data 查找）
    public Vector3[] LineVerts => _lineVerts;          // 渲染用：边界线带顶点（线带 + 端帽圆盘）
    public int[] LineIndices => _lineIndices;          // 渲染用：边界线带三角索引
    Vector3[] _displayVerts;    // 渲染网格：每格 1 格心 + m 角点（格内顶点共享）
    Vector2[] _displayUv;       // 逐顶点 UV：格数据纹理纹素中心（块内同值 → 插值恒定）
    int[] _displayIndices;      // 三角索引
    Vector3[] _lineVerts;       // 边界线带顶点（世界系，BallView 独立 MeshInstance 提交）
    int[] _lineIndices;         // 边界线带三角索引

    public float MeanInradius { get; private set; } = 0.005f;   // 六边格平均内切半径 ρ（rad，切面弦长）
    public float LineWidthFrac { get; set; } = 0.18f;           // 边界线半宽 = × ρ（旧片元描边同宽默认）


    public BallMesh()
    {

    }


    // ── 逐格数据纹理布局（全域材质的区域查找地址；本类填 UV 与 BallView 烘纹理共用同一布局）──

    // 纹理宽 = ceil(√N)（近方形；高 = ceil(N/宽)，尾行留空纹素，UV 恒指向有效格）。
    public static int DataTexWidth(int cellCount) => (int)Math.Ceiling(Math.Sqrt(cellCount));

    // 格下标 → 该格纹素中心 UV（纹素中心 + nearest 采样 → 浮点抖动也恒命中本格）。
    public static Vector2 CellDataUv(int cellIndex, int width, int height) =>
        new(((cellIndex % width) + 0.5f) / width, ((cellIndex / width) + 0.5f) / height);

    // 切线框架（CPU 侧度量用）：tu = up×ĉ 归一、tv = ĉ×tu。
    static (Vector3 Tu, Vector3 Tv) TangentFrame(Vector3 cDir)
    {
        Vector3 up = Math.Abs(cDir.Y) < 0.98f ? Vector3.Up : Vector3.Right;
        Vector3 tu = up.Cross(cDir).Normalized();
        return (tu, cDir.Cross(tu));
    }

    // 采样首个六边格实测平均内切半径 ρ（rad）：边界线半宽的换算尺度（LineWidthFrac × ρ）。
    public void ComputeCellMetrics(Ball ball)
    {
        for (int i = 0; i < ball.CellIds.Length; i++)
        {
            ulong[] vids = H3.CellToVertexes(ball.CellIds[i]);
            if (vids.Length != 6) continue;
            var (tu, tv) = TangentFrame(ball.CellCenters[i].Normalized());
            float rho = 0f;
            for (int k = 0; k < 6; k++)
                rho += ((CornerTangent(ball, vids[k], tu, tv)
                       + CornerTangent(ball, vids[(k + 1) % 6], tu, tv)) * 0.5f).Length();
            MeanInradius = Math.Max(rho / 6f, 1e-5f);
            return;
        }
    }

    static Vector2 CornerTangent(Ball ball, ulong vid, Vector3 tu, Vector3 tv)
    {
        Vector3 d = ball.VertexPositions[ball.VertexIndexOf(vid)].Normalized();
        return new Vector2(d.Dot(tu), d.Dot(tv));
    }

    // 格块布局 = 每格 [格心, 角0..角m-1] + m 个扇形三角（格心, 角k, 角k+1），格内顶点共享——
    // UV 是格常量，共享无损。2026-09-10 描边改几何线带（BuildBoundaryLineMesh，独立网格渲染）后，
    // 格面不再携带逐边旗标/切面坐标/线段端点等描边属性（v4 的 UV2/COLOR/CUSTOM0 全删）。
    public void BuildTileMeshData(Ball ball, float radius)
    {
        int n = ball.CellIds.Length;
        _displayVerts = new Vector3[7 * n - 12];    // 六边格 7 顶点 ×(n−12) + 五边格 6×12
        _displayUv = new Vector2[_displayVerts.Length];
        _displayIndices = new int[18 * n - 36];     // 索引数 = 三角数×3：六边格 18 ×(n−12) + 五边格 15×12
        int texW = DataTexWidth(n), texH = (n + texW - 1) / texW;
        int v = 0, t = 0;
        for (int i = 0; i < n; i++)
        {
            ulong cell = ball.CellIds[i];
            Vector2 uv = CellDataUv(i, texW, texH);
            ulong[] vids = H3.CellToVertexes(cell);
            int m = vids.Length;
            Vector3 center = Vector3.Zero;
            for (int k = 0; k < m; k++)
                center += ball.VertexPositions[ball.VertexIndexOf(vids[k])];
            center = center.Normalized() * radius;

            _displayVerts[v] = center;              // 格心 = 切面原点外的球面点
            _displayUv[v] = uv;
            for (int k = 0; k < m; k++)
            {
                _displayVerts[v + 1 + k] = ball.VertexPositions[ball.VertexIndexOf(vids[k])];
                _displayUv[v + 1 + k] = uv;
                _displayIndices[t++] = v;
                _displayIndices[t++] = v + 1 + k;
                _displayIndices[t++] = v + 1 + (k + 1) % m;
            }
            v += m + 1;
        }
    }

    // ── 板块边界线几何（2026-09-10 点串描边方案，替 v4 片元解析边距）──

    // 边界线带构建：输入 = 异板共享边顶点对全集（每边恰一次，VM.BoundaryVertexEdges 派生口）。
    // 做法（Red Blob Games "Hex Region Borders" 共享边法 + 圆角接头）：
    //   · 线带——每条边沿两端角点的球面弧采点，左右各偏移半宽 α = LineWidthFrac·ρ，连成三角带；
    //   · 端帽——每条边两端角点补半径 α 的扇形圆盘：相邻边在共享角点处的开角楔形由帽盘填满
    //     （圆角衔接、无缺口），且帽盘 ⊇ 线带端截面 → 接缝恒闭合；
	//   · 防深斑——线带/帽盘在角点处必然重叠,BallView 以 blend_mix + depth_draw_always 单遍
	//     渲染:首片元入深度、重叠片元被深度测试丢弃 → 每像素只混一次色,半透明线色也不叠加变深;
	//     线带抬升 lift 悬于格面上方防 z-fighting（格面顶点同在半径 R 球面）。
    public void BuildBoundaryLineMesh(Ball ball, IReadOnlyList<(ulong va, ulong vb)> edges, float radius)
    {
        float alpha = LineWidthFrac * MeanInradius;    // 线半宽（球面角，rad）
        // 悬浮高度 = 线半宽的 10%：只须压过线/格面两套三角的弦高差与深度量化（∝ρ²，小两个数量级）
        // 即防 z-fighting；原 0.002·R 在 res3 下 ≈ 55% 线半宽，特写可见"浮起"，2026-09-10 收紧。
        float lift = alpha * radius * 0.1f;
        float rTop = radius + lift;
        float sinA = MathF.Sin(alpha), cosA = MathF.Cos(alpha);
        const int capSegs = 12;                        // 端帽圆盘扇段数

        var verts = new List<Vector3>(edges.Count * 40);
        var idx = new List<int>(edges.Count * 60);
        var capped = new HashSet<int>();               // 每个边界角点只补一次帽（顶点下标）

        foreach (var (va, vb) in edges)
        {
            Vector3 a = ball.VertexPositions[ball.VertexIndexOf(va)].Normalized();
            Vector3 b = ball.VertexPositions[ball.VertexIndexOf(vb)].Normalized();
            Vector3 chord = (b - a).Normalized();      // 弦方向（格边弧极短，作全程切向足够）

            float theta = a.AngleTo(b);
            int subs = Math.Clamp((int)MathF.Ceiling(theta / (0.4f * MeanInradius)), 1, 8);
            int ring0 = verts.Count;
            for (int s = 0; s <= subs; s++)
            {
                Vector3 d = a.Slerp(b, (float)s / subs).Normalized();   // 弧采点（球面方向）
                // 侧偏方向 ⊥ 弧点、⊥切向：d 与 chord 同在 {a,b} 张成的平面 → 叉指恒同侧不扭转
                Vector3 n = d.Cross(chord).Normalized();
                verts.Add((cosA * d - sinA * n) * rTop);                // 左环点
                verts.Add((cosA * d + sinA * n) * rTop);                // 右环点
            }
            for (int s = 0; s < subs; s++)
            {
                int l0 = ring0 + 2 * s, r0 = l0 + 1, l1 = l0 + 2, r1 = l0 + 3;
                idx.Add(l0); idx.Add(l1); idx.Add(r0);
                idx.Add(r0); idx.Add(l1); idx.Add(r1);
            }

            Cap(ball.VertexIndexOf(va));
            Cap(ball.VertexIndexOf(vb));
        }
        _lineVerts = verts.ToArray();
        _lineIndices = idx.ToArray();
        return;

        // 端帽：角点处角半径 α 的扇形圆盘（三点扇心 + 首尾重复接缝环）。
        void Cap(int vi)
        {
            if (!capped.Add(vi)) return;
            Vector3 p = ball.VertexPositions[vi].Normalized();
            Vector3 up = Math.Abs(p.Y) < 0.98f ? Vector3.Up : Vector3.Right;
            Vector3 e1 = up.Cross(p).Normalized();
            Vector3 e2 = p.Cross(e1);
            int c = verts.Count;
            verts.Add(p * rTop);
            for (int k = 0; k <= capSegs; k++)
            {
                float phi = k * MathF.Tau / capSegs;
                Vector3 t = MathF.Cos(phi) * e1 + MathF.Sin(phi) * e2;
                verts.Add((cosA * p + sinA * t) * rTop);
            }
            for (int k = 0; k < capSegs; k++)
            {
                idx.Add(c); idx.Add(c + 1 + k); idx.Add(c + 2 + k);
            }
        }
    }

}
