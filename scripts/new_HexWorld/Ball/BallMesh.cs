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
    public Vector2[] DisplayCellLocal => _cellLocal;   // 渲染用：UV2 = 格心切面坐标（片元位置）
    public Color[] DisplayCellAttrs => _cellAttrs;     // 渲染用：R=旗标 G/B=边法向 A=内切半径（归一）
    Vector3[] _displayVerts;    // 显示网格：每格 m 个三角 × 3 顶点（逐三角复制，属性插值恒精确）
    Vector2[] _displayUv;       // 逐顶点 UV：格数据纹理纹素中心（块内同值 → 插值恒定）
    Vector2[] _cellLocal;       // 逐顶点 UV2：格心切面坐标（格心 (0,0)、角点为其切面坐标）
    Color[] _cellAttrs;         // 逐顶点 COLOR：R=边旗、G/B=边法向 xy、A=内切半径/ρ尺度（逐三角常量）
    int[] _displayIndices;      // 三角索引
    int[] _tileVertOffset;      // 每格首个三角顶点起点（每格 3m 个顶点）

    public float RhoScale { get; private set; } = 0.02f;          // COLOR.a 的内切半径归一化尺度（rad）
    public Vector2 CellMetricsHex { get; private set; } = Vector2.Zero;   // 六边格（内切半径, 半边长）
    public Vector2 CellMetricsPent { get; private set; } = Vector2.Zero;  // 五边格

    public BallMesh()
    {

    }


    // ── 逐格数据纹理布局（全域材质的区域查找地址；本类填 UV 与 BallView 烘纹理共用同一布局）──

    // 纹理宽 = ceil(√N)（近方形；高 = ceil(N/宽)，尾行留空纹素，UV 恒指向有效格）。
    public static int DataTexWidth(int cellCount) => (int)Math.Ceiling(Math.Sqrt(cellCount));

    // 格下标 → 该格纹素中心 UV（纹素中心 + nearest 采样 → 浮点抖动也恒命中本格）。
    public static Vector2 CellDataUv(int cellIndex, int width, int height) =>
        new(((cellIndex % width) + 0.5f) / width, ((cellIndex / width) + 0.5f) / height);

    // 切线框架（CPU 与 sphere_region_material 同式）：tu = up×ĉ 归一、tv = ĉ×tu——
    // 两式框架一致，片元侧的边法向/切面坐标才与 CPU 写入的属性对齐。
    static (Vector3 Tu, Vector3 Tv) TangentFrame(Vector3 cDir)
    {
        Vector3 up = Math.Abs(cDir.Y) < 0.98f ? Vector3.Up : Vector3.Right;
        Vector3 tu = up.Cross(cDir).Normalized();
        return (tu, cDir.Cross(tu));
    }

    // 采样格（首个六边格 + 首个五边格）实测几何度量：内切半径 ρ 与半边长（切线平面单位）。
    // 供片元解析边距：COLOR.a 归一化尺度、段端圆角的半长阈值。逐格畸变由每三角真实法向/ρ 承担，
    // 此处均值只作为 COLOR.a 归一化尺度与五边格半长近似。
    public void ComputeCellMetrics(Ball ball)
    {
        var hex = Vector2.Zero;
        var pent = Vector2.Zero;
        int hexN = 0, pentN = 0;
        for (int i = 0; i < ball.CellIds.Length && (hexN == 0 || pentN == 0); i++)
        {
            ulong[] vids = H3.CellToVertexes(ball.CellIds[i]);
            int m = vids.Length;
            if ((m == 6 && hexN > 0) || (m == 5 && pentN > 0)) continue;
            Vector3 cDir = ball.CellCenters[i].Normalized();
            var (tu, tv) = TangentFrame(cDir);
            float rho = 0f, halfL = 0f;
            for (int k = 0; k < m; k++)
            {
                Vector2 tK = CornerTangent(ball, vids[k], tu, tv);
                Vector2 tK1 = CornerTangent(ball, vids[(k + 1) % m], tu, tv);
                Vector2 mid = (tK + tK1) * 0.5f;
                rho += mid.Length();
                halfL += (tK1 - tK).Length() * 0.5f;
            }
            if (m == 6) { hex = new Vector2(rho / m, halfL / m); hexN = 1; }
            else { pent = new Vector2(rho / m, halfL / m); pentN = 1; }
        }
        RhoScale = Math.Max(hex.X, 1e-4f) * 4f;   // COLOR.a 8bit：ρ/尺度 → 步长 ≈ ρ/64（带宽的 ~4%）
        CellMetricsHex = hex;
        CellMetricsPent = pent;
    }

    static Vector2 CornerTangent(Ball ball, ulong vid, Vector3 tu, Vector3 tv)
    {
        Vector3 d = ball.VertexPositions[ball.VertexIndexOf(vid)].Normalized();
        return new Vector2(d.Dot(tu), d.Dot(tv));
    }

    // 格块布局 = 每格 m 个三角 × 3 顶点 [center_k, corner_k, corner_k+1]（逐三角复制）。
    // 描边带需要片元处"到本格旗标边线段的精确距离"——每条边的法向/旗标必须作为该三角的常量
    // 属性下发，因此三角的三个顶点独占（格心/角点不再跨三角共用，2026-09-09 v3 解析边距方案）。
    // 逐顶点属性：
    //   UV   = 格纹素中心（region_data 查找）；
    //   UV2  = 格心切面坐标（格心 (0,0)、角点为其切面投影）——插值 = 片元自身切面坐标；
    //   COLOR= (旗标 f, 法向 n.x, 法向 n.y, ρ/RhoScale)——逐三角常量，插值恒精确。
    public void BuildTileMeshData(Ball ball, float radius)
    {
        int n = ball.CellIds.Length;
        _displayVerts = new Vector3[18 * n - 36];   // 六边格 18（6 三角×3）×(n−12) + 五边格 15×12
        _displayUv = new Vector2[_displayVerts.Length];
        _cellLocal = new Vector2[_displayVerts.Length];
        _cellAttrs = new Color[_displayVerts.Length];
        _displayIndices = new int[18 * n - 36];
        _tileVertOffset = new int[n];
        int texW = DataTexWidth(n), texH = (n + texW - 1) / texW;
        int v = 0, t = 0;
        for (int i = 0; i < n; i++)
        {
            ulong cell = ball.CellIds[i];
            _tileVertOffset[i] = v;
            Vector2 uv = CellDataUv(i, texW, texH);
            ulong[] vids = H3.CellToVertexes(cell);
            int m = vids.Length;
            Vector3 cDir = ball.CellCenters[i].Normalized();
            var (tu, tv) = TangentFrame(cDir);
            Vector3 center = Vector3.Zero;
            for (int k = 0; k < m; k++)
                center += ball.VertexPositions[ball.VertexIndexOf(vids[k])];
            center = center.Normalized() * radius;

            for (int k = 0; k < m; k++)
            {
                Vector2 tK = CornerTangent(ball, vids[k], tu, tv);
                Vector2 tK1 = CornerTangent(ball, vids[(k + 1) % m], tu, tv);
                Vector2 mid = (tK + tK1) * 0.5f;
                Vector2 nrm = mid.Normalized();                     // 边 k 外法向（切面）；勿名 n——与外层格数 n 冲突
                float rho = mid.Length();                           // 内切半径（切面弦单位）
                Color attr = new Color(0f, nrm.X * 0.5f + 0.5f, nrm.Y * 0.5f + 0.5f, rho / RhoScale);

                _displayVerts[v] = center;                          // 三角三顶点：格心 + 两角（逐三角复制）
                _displayVerts[v + 1] = ball.VertexPositions[ball.VertexIndexOf(vids[k])];
                _displayVerts[v + 2] = ball.VertexPositions[ball.VertexIndexOf(vids[(k + 1) % m])];
                _displayUv[v] = uv;
                _displayUv[v + 1] = uv;
                _displayUv[v + 2] = uv;
                _cellLocal[v] = Vector2.Zero;                       // 格心 = 切面原点
                _cellLocal[v + 1] = tK;
                _cellLocal[v + 2] = tK1;
                _cellAttrs[v] = attr;                               // 旗标由 BuildCellAttributeFlags 填
                _cellAttrs[v + 1] = attr;
                _cellAttrs[v + 2] = attr;
                _displayIndices[t++] = v;                           // 三角 (格心, 角k, 角k+1)
                _displayIndices[t++] = v + 1;
                _displayIndices[t++] = v + 2;
                v += 3;
            }
        }
    }

    // 逐格逐【边】的异板边旗落 COLOR.r（描边渲染输入，建一次常驻）：边 ∈ 边界格边集 → 该边所属
    // 三角的三个顶点旗 1，片元对旗标边求到线段的精确距离。同板边绝不描边（2026-09-09 误描修复），
    // 拐角由段端距离自动圆角衔接（2026-09-09 缺口/深斑修复——不再需要角点帽）。
    // boundaryEdges = VM 的 H3Plate.ExtractBoundaryCellEdges 派生集（(格 id, 顶点对规范化序 va<vb)）。
    public void BuildCellAttributeFlags(Ball ball, IReadOnlySet<(ulong cell, ulong va, ulong vb)> boundaryEdges)
    {
        for (int i = 0; i < ball.CellIds.Length; i++)
        {
            ulong cell = ball.CellIds[i];
            ulong[] vids = H3.CellToVertexes(cell);
            int m = vids.Length;
            int off = _tileVertOffset[i];

            for (int k = 0; k < m; k++)
            {
                ulong v1 = vids[k], v2 = vids[(k + 1) % m];
                (ulong va, ulong vb) = v1 < v2 ? (v1, v2) : (v2, v1);   // 集合键 = 规范化序（与提取口同规）
                float f = boundaryEdges.Contains((cell, va, vb)) ? 1f : 0f;
                int tri = off + 3 * k;
                Color attr = _cellAttrs[tri];
                attr.R8 = (byte)(f * 255f);
                _cellAttrs[tri] = attr;
                _cellAttrs[tri + 1] = attr;
                _cellAttrs[tri + 2] = attr;
            }
        }
    }

}
