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
    public Vector2[] DisplayCellDirXy => _cellDirXy;   // 渲染用：UV2 = 格心单位方向 x/y（z 片元重构）
    public Color[] DisplayCellAttrs => _cellAttrs;     // 渲染用：R=边0法向方位角/2π，G=旗标字节，B/A=1
    Vector3[] _displayVerts;    // 显示网格：每格 [格心, 角0..角m-1] 块（格心扇形）
    Vector2[] _displayUv;       // 逐顶点 UV：格数据纹理纹素中心（块内同值 → 插值恒定）
    Vector2[] _cellDirXy;       // 逐顶点 UV2：格心单位方向 x/y（块内同值 → 插值 = 常量精确）
    Color[] _cellAttrs;         // 逐顶点格属性（块内同值）：R=边0法向方位角/2π、G=旗标字节、B/A=1
    int[] _displayIndices;      // 格心扇形三角索引
    int[] _tileVertOffset;      // 每格块起点

    const float TwoPi = 6.2831855f;

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
    // 两式框架一致，片元侧的边方位角才与 CPU 写入的 θ 对齐。
    public static (Vector3 Tu, Vector3 Tv) TangentFrame(Vector3 cDir)
    {
        Vector3 up = Math.Abs(cDir.Y) < 0.98f ? Vector3.Up : Vector3.Right;
        Vector3 tu = up.Cross(cDir).Normalized();
        return (tu, cDir.Cross(tu));
    }

    // 各格平均内切半径/半边长（切线平面弦长单位，rad 级）：[0]=六边格、[1]=五边格。
    // 供片元解析式边距用（正六/五边形模型；H3 畸变带来的 ±数% 逐格偏差可接受）。
    public static Vector2[] ComputeCellMetrics(Ball ball)
    {
        var hex = Vector2.Zero;
        var pent = Vector2.Zero;
        int hexN = 0, pentN = 0;
        for (int i = 0; i < ball.CellIds.Length && (hexN == 0 || pentN == 0); i++)
        {
            ulong[] vids = H3.CellToVertexes(ball.CellIds[i]);
            int m = vids.Length;
            if ((m == 6 && hexN > 0) || (m == 5 && pentN > 0)) continue;
            Vector3 c = ball.CellCenters[i].Normalized();
            var (tu, tv) = TangentFrame(c);
            float rho = 0f, halfL = 0f;
            for (int k = 0; k < m; k++)
            {
                Vector2 tK = CornerTangent(ball, vids[k], tu, tv);
                Vector2 tK1 = CornerTangent(ball, vids[(k + 1) % m], tu, tv);
                Vector2 mid = (tK + tK1) * 0.5f;                 // 边中点 = 内切圆触点方向
                rho += mid.Length();
                halfL += (tK1 - tK).Length() * 0.5f;
            }
            if (m == 6) { hex = new Vector2(rho / m, halfL / m); hexN = 1; }
            else { pent = new Vector2(rho / m, halfL / m); pentN = 1; }
        }
        return new[] { hex, pent };
    }

    static Vector2 CornerTangent(Ball ball, ulong vid, Vector3 tu, Vector3 tv)
    {
        Vector3 d = ball.VertexPositions[ball.VertexIndexOf(vid)].Normalized();
        return new Vector2(d.Dot(tu), d.Dot(tv));
    }

    // 格块布局 = [格心, 角0..角m-1]（六边格 m=6 / 五边格 m=5），格心 = 角点均值投影回球面。
    // 扇形三角 (格心, 角k, 角k+1) 环绕取模闭合。（2026-09-09 v3 起**边顶点不再复制**：描边带改
    // 片元解析六边距离——每格常量属性（格心方向/边方位角/旗标）块内插值恒精确，无需每边独立
    // 插值参数，几何大幅简化。）
    // UV 全块写同一纹素中心；UV2 = 格心方向 x/y（块内常量）；COLOR = (θ/2π, 旗标字节/255 待填, 0, 1)。
    public void BuildTileMeshData(Ball ball, float radius)
    {
        int n = ball.CellIds.Length;
        _displayVerts = new Vector3[7 * n - 12];    // 六边格 7（格心+6角）×(n−12) + 五边格 6×12
        _displayIndices = new int[18 * n - 36];     // 每格 m 个三角：6×3×(n−12) + 5×3×12
        _displayUv = new Vector2[_displayVerts.Length];
        _cellDirXy = new Vector2[_displayVerts.Length];
        _cellAttrs = new Color[_displayVerts.Length];
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
            Vector2 t0 = CornerTangent(ball, vids[0], tu, tv);
            Vector2 t1 = CornerTangent(ball, vids[1], tu, tv);
            Vector2 mid01 = (t0 + t1) * 0.5f;                       // 边 0（角0-角1）法向方位
            float theta = Mathf.Atan2(mid01.Y, mid01.X);
            if (theta < 0) theta += TwoPi;

            int v0 = v;                                             // 块首格心（全部 m 个三角共用）
            Vector3 center = Vector3.Zero;
            for (int k = 0; k < m; k++)
                center += ball.VertexPositions[ball.VertexIndexOf(vids[k])];
            _displayVerts[v] = center.Normalized() * radius;
            _displayUv[v] = uv;
            _cellDirXy[v] = new Vector2(cDir.X, cDir.Y);
            _cellAttrs[v] = new Color(theta / TwoPi, 0f, 0f, 1f);   // G 旗标字节由 BuildCellAttributeFlags 填
            v++;
            for (int k = 0; k < m; k++)
            {
                int ck = v0 + 1 + k;                                // 角 k
                _displayVerts[ck] = ball.VertexPositions[ball.VertexIndexOf(vids[k])];
                _displayUv[ck] = uv;
                _cellDirXy[ck] = new Vector2(cDir.X, cDir.Y);
                _cellAttrs[ck] = new Color(theta / TwoPi, 0f, 0f, 1f);
                int ck1 = v0 + 1 + ((k + 1) % m);                   // 环绕取模：末边绕回角 0
                _displayIndices[t++] = v0;                          // 三角 (格心, 角k, 角k+1)
                _displayIndices[t++] = ck;
                _displayIndices[t++] = ck1;
            }
            v = v0 + 1 + m;                                         // 推进到下一格块首
        }
    }

    // 逐格旗标字节落 COLOR.g（描边渲染输入，建一次常驻；块内全顶点同值 → 插值精确）：
    // bit0..5 = 边 k（角 k→角 k+1）是否异板边（片元只对旗标边算距离），bit6 = 格心方向 z 为负，
    // bit7 = 五边格。boundaryEdges = VM 的 H3Plate.ExtractBoundaryCellEdges 派生集
    // （(格 id, 顶点对规范化序 va<vb)，与片元解析的距离计算同源）。
    public void BuildCellAttributeFlags(Ball ball, IReadOnlySet<(ulong cell, ulong va, ulong vb)> boundaryEdges)
    {
        for (int i = 0; i < ball.CellIds.Length; i++)
        {
            ulong cell = ball.CellIds[i];
            ulong[] vids = H3.CellToVertexes(cell);
            int m = vids.Length;
            int off = _tileVertOffset[i];

            int flags = m == 5 ? 128 : 0;                           // bit7：五边格
            if (ball.CellCenters[i].Z < 0) flags |= 64;             // bit6：格心方向 z 为负
            for (int k = 0; k < m; k++)
            {
                ulong v1 = vids[k], v2 = vids[(k + 1) % m];
                (ulong va, ulong vb) = v1 < v2 ? (v1, v2) : (v2, v1);   // 集合键 = 规范化序（与提取口同规）
                if (boundaryEdges.Contains((cell, va, vb))) flags |= 1 << k;
            }

            float g = flags / 255f;
            int count = m + 1;
            for (int j = 0; j < count; j++)
            {
                Color c = _cellAttrs[off + j];
                _cellAttrs[off + j] = new Color(c.R, g, 0f, 1f);
            }
        }
    }

}
