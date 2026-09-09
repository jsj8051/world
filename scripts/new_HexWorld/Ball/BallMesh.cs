using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.NewHexWorld;
using World.Utils;
using World.Utils.H3;

namespace World.NewHexWorld;

public class BallMesh
{

    public Vector3[] DisplayVerts => _displayVerts;    // 渲染用：显示网格顶点
    public int[] DisplayIndices => _displayIndices;    // 渲染用：三角索引
    public Vector2[] DisplayUv => _displayUv;          // 渲染用：UV = 格纹素中心（全域材质的区域查找地址）
    public Vector2[] DisplayUv2 => _displayUv2;        // 渲染用：UV2 = (x 边距权重, y 格边边界旗)
    public Color[] DisplayCornerFlags => _cornerFlags; // 渲染用：R = 角 k 旗、G = 角 k+1 旗（描边拐角帽）
    Vector3[] _displayVerts;    // 显示网格：每格 [格心 + 每边 2 顶点] 块（边顶点按边复制，不跨边共享）
    Vector2[] _displayUv;       // 逐顶点 UV：格数据纹理纹素中心（块内同值 → 插值恒定）
    Vector2[] _displayUv2;      // 逐顶点 UV2：x = 边距权重（格心 1/边顶点 0），y = 该顶点所属格边的边界旗
    Color[] _cornerFlags;       // 逐顶点角旗（格心 0）：该顶点所属角点是否贴异板边 → 片元拐角帽
    int[] _displayIndices;      // 格心扇形三角索引
    int[] _tileVertOffset;      // 每格块起点

    public BallMesh()
    {

    }


    // ── 逐格数据纹理布局（全域材质的区域查找地址；本类填 UV 与 BallView 烘纹理共用同一布局）──

    // 纹理宽 = ceil(√N)（近方形；高 = ceil(N/宽)，尾行留空纹素，UV 恒指向有效格）。
    public static int DataTexWidth(int cellCount) => (int)Math.Ceiling(Math.Sqrt(cellCount));

    // 格下标 → 该格纹素中心 UV（纹素中心 + nearest 采样 → 浮点抖动也恒命中本格）。
    public static Vector2 CellDataUv(int cellIndex, int width, int height) =>
        new(((cellIndex % width) + 0.5f) / width, ((cellIndex / width) + 0.5f) / height);

    // 格块布局 = [格心, 边0a, 边0b, 边1a, 边1b, ...]（六边格 1+2×6=13 / 五边格 11；格心 = 角点均值
    // 投影回球面）。每条格边一对专属顶点（不跨边共享）+ 三角 (格心, 角k, 角k+1)——描边带需要
    // 【每条边】独立的插值参数（UV2）：旧布局角点被相邻两边共用，"角点是否贴边界"一个标量无法
    // 区分角点属于哪条边 → 五边贴异板的格其同板边两端角点都被记账 0 → 整条误描（2026-09-09
    // 用户报告；短须同源）。边顶点复制后 UV2.y 按边赋旗，非边界边恒 0 绝不描边。
    // UV2 = (x: 格心 1 → 边顶点 0，等值线平行格边 = 边距权重；y: 本边边界旗，格心 0)。
    // UV 全块写同一纹素中心（2026-09-09 材质覆盖方案）：片元对 region_data 的区域查找地址。
    public void BuildTileMeshData(Ball ball, float radius)
    {
        int n = ball.CellIds.Length;
        _displayVerts = new Vector3[13 * n - 24];   // 六边格 13（格心+6边×2）×(n−12) + 五边格 11×12
        _displayIndices = new int[18 * n - 36];     // 每格 m 个三角：6×3×(n−12) + 5×3×12
        _displayUv = new Vector2[_displayVerts.Length];
        _displayUv2 = new Vector2[_displayVerts.Length];
        _cornerFlags = new Color[_displayVerts.Length];
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
            Vector3 center = Vector3.Zero;
            for (int k = 0; k < m; k++)
                center += ball.VertexPositions[ball.VertexIndexOf(vids[k])];

            int v0 = v;                                        // 块首格心（全部 m 个三角共用）
            _displayVerts[v] = center.Normalized() * radius;
            _displayUv[v] = uv;
            _displayUv2[v] = new Vector2(1f, 0f);              // 格心：边距 1（永不暗化）、旗 0
            _cornerFlags[v] = new Color(0f, 0f, 0f, 1f);       // 格心：无角旗（帽只在角点处）
            v++;
            for (int k = 0; k < m; k++)
            {
                Vector3 a = ball.VertexPositions[ball.VertexIndexOf(vids[k])];
                Vector3 b = ball.VertexPositions[ball.VertexIndexOf(vids[(k + 1) % m])];
                _displayVerts[v] = a;
                _displayVerts[v + 1] = b;
                _displayUv[v] = uv;
                _displayUv[v + 1] = uv;
                _displayUv2[v] = new Vector2(0f, 0f);          // 边顶点：边距 0；旗由 BuildTileEdgeFlags 填
                _displayUv2[v + 1] = new Vector2(0f, 0f);
                _displayIndices[t++] = v0;                     // 三角 (格心, 角k, 角k+1)，环绕取模闭合
                _displayIndices[t++] = v;
                _displayIndices[t++] = v + 1;
                v += 2;
            }
        }
    }

    // 逐格【每条边】边界旗落 UV2.y + 【每角】角旗落 COLOR.r/g（描边渲染输入，建一次常驻）：
    // - 边旗：格边 ∈ 边界格边集 → 该边两个顶点旗 1（片元沿边压暗成轮廓带，两侧格各暗半带骑缝对称）；
    //   否则 0（该边绝不整条描边——即使两端角点都贴邻边界边，2026-09-09 误描修复）。
    // - 角旗：角点贴任一异板边（其两条邻边任一 ∈ 集）→ 1。片元在 λ_corner > 1-带宽 处补"拐角帽"，
    //   边界路径折弯/分岔处两侧条带靠帽衔接成连续折线（修纯边旗方案拐角外侧的楔形缺口）；帽只在
    //   角点带宽半径内生效、不沿非边界边蔓延（五边贴异板格第 6 边中段仍干净，误描不复发）。
    //   ⚠️ 通道纪律：a 端（角 k）入 COLOR.r、b 端（角 k+1）入 COLOR.g，且各写【该角自己】的旗——
    //   上一实验把角 k 的旗写进 b 端 G 通道 → 帽整体错位一个角（游离杂点根因，2026-09-09 修）。
    // boundaryEdges = VM 的 H3Plate.ExtractBoundaryCellEdges 派生集（(格 id, 顶点对规范化序 va<vb)）。
    public void BuildTileEdgeFlags(Ball ball, IReadOnlySet<(ulong cell, ulong va, ulong vb)> boundaryEdges)
    {
        for (int i = 0; i < ball.CellIds.Length; i++)
        {
            ulong cell = ball.CellIds[i];
            ulong[] vids = H3.CellToVertexes(cell);
            int m = vids.Length;
            int off = _tileVertOffset[i];

            var edgeFlags = new bool[m];
            for (int k = 0; k < m; k++)
            {
                ulong v1 = vids[k], v2 = vids[(k + 1) % m];
                (ulong va, ulong vb) = v1 < v2 ? (v1, v2) : (v2, v1);   // 集合键 = 规范化序（与提取口同规）
                edgeFlags[k] = boundaryEdges.Contains((cell, va, vb));
            }

            for (int k = 0; k < m; k++)
            {
                int a = off + 1 + 2 * k, b = a + 1;                      // 边 k 的两端顶点副本
                _displayUv2[a] = new Vector2(0f, edgeFlags[k] ? 1f : 0f);
                _displayUv2[b] = new Vector2(0f, edgeFlags[k] ? 1f : 0f);
                // 角 j 的旗 = 其两条邻边（j-1、j）任一为异板边；a=角 k 入 R、b=角 k+1 入 G，各归各通道
                bool cornerFlagK = edgeFlags[(k - 1 + m) % m] || edgeFlags[k];
                bool cornerFlagK1 = edgeFlags[k] || edgeFlags[(k + 1) % m];
                _cornerFlags[a] = new Color(cornerFlagK ? 1f : 0f, 0f, 0f, 1f);
                _cornerFlags[b] = new Color(0f, cornerFlagK1 ? 1f : 0f, 0f, 1f);
            }
        }
    }

}
