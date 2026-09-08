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
    Vector3[] _displayVerts;    // 显示网格：每格 [格心+角点] 块
    int[] _displayIndices;      // 格心扇形三角索引
    int[] _tileVertOffset;      // 每格块起点

    public BallMesh()
    {

    }


    // 格块布局 = [格心, 角0..角m-1]（六边格 m=6 / 五边格 m=5），格心 = 角点均值投影回球面。
    // 扇形三角 (格心, 角k, 角k+1) 环绕取模闭合——每格恰 m 个三角，全部落在自己块内。
    // （2026-09-08 描边改版随带修复：旧"首角扇形 + k+1 不取模"每格多出一个越界到邻块的环绕三角。）
    // 块首留格心顶点还服务描边渲染：格心权重恒 1、角点带边界权重（BuildTileOutlineWeights），
    // 边界带宽度 = 权重插值 × 格心到边距离，对所有边均匀。
    public void BuildTileMeshData(Ball ball, float radius)
    {
        int n = ball.CellIds.Length;
        _displayVerts = new Vector3[7 * n - 12];    // 六边格 7（格心+6角）×(n−12) + 五边格 6×12
        _displayIndices = new int[18 * n - 36];     // 每格 m 个三角：6×3×(n−12) + 5×3×12
        _tileVertOffset = new int[n];
        int v = 0, t = 0;
        for (int i = 0; i < n; i++)
        {
            ulong cell = ball.CellIds[i];
            _tileVertOffset[i] = v;
            ulong[] vids = H3.CellToVertexes(cell);
            int m = vids.Length;
            Vector3 center = Vector3.Zero;
            for (int k = 0; k < m; k++)
            {
                Vector3 p = ball.VertexPositions[ball.VertexIndexOf(vids[k])];
                _displayVerts[v + 1 + k] = p;
                center += p;
            }
            _displayVerts[v] = center.Normalized() * radius;
            v += m + 1;
            int v0 = _tileVertOffset[i];
            for (int k = 0; k < m; k++)
            {
                _displayIndices[t++] = v0;
                _displayIndices[t++] = v0 + 1 + k;
                _displayIndices[t++] = v0 + 1 + (k + 1) % m;
            }
        }

    }

    // 按每格颜色展开成顶点色（下标与 Ball.CellIds 对齐；消费口 = 球视图 VM 的逐格投影缓存）。
    // 每格一整块同色（块内顶点共享格色）；cornerWeights 非空时其逐顶点值并入 alpha
    // （描边渲染输入：1 = 内部 / 0 = 贴边界角点；null = 全 1，不描边）。
    public Color[] BuildTileColors(Ball ball, Color[] perCellColors, float[] cornerWeights = null)
    {
        var colors = new Color[_displayVerts.Length];
        for (int i = 0; i < perCellColors.Length; i++)
        {
            Color c = perCellColors[i];
            int off = _tileVertOffset[i];
            int blockLen = (i + 1 < perCellColors.Length ? _tileVertOffset[i + 1] : _displayVerts.Length) - off;
            for (int k = 0; k < blockLen; k++)
                colors[off + k] = new Color(c.R, c.G, c.B, cornerWeights == null ? 1f : cornerWeights[off + k]);
        }
        return colors;
    }

    // 逐显示顶点的边界权重（描边渲染输入，建一次常驻）：格心恒 1；角点为异板共享边端点则 0
    // （该格此角贴板块边界 → 片元侧压暗成轮廓带），否则 1。boundaryVerts = VM 的
    // H3Plate.ExtractBoundaryVerts 派生集（(格 id, 顶点 id) 对；每格角点副本独立判）。
    public float[] BuildTileOutlineWeights(Ball ball, IReadOnlySet<(ulong cell, ulong vid)> boundaryVerts)
    {
        var weights = new float[_displayVerts.Length];
        for (int i = 0; i < ball.CellIds.Length; i++)
        {
            int off = _tileVertOffset[i];
            weights[off] = 1f;                          // 格心：永不暗化
            ulong cell = ball.CellIds[i];
            ulong[] vids = H3.CellToVertexes(cell);
            for (int k = 0; k < vids.Length; k++)
                weights[off + 1 + k] = boundaryVerts.Contains((cell, vids[k])) ? 0f : 1f;
        }
        return weights;
    }

}
