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
    int[] _displayIndices;      // 扇形三角索引
    int[] _tileVertOffset;      // 每格块起点

    public BallMesh()
    {

    }


    public void BuildTileMeshData(Ball ball, float radius)
    {
        int n = ball.CellIds.Length;
        _displayVerts = new Vector3[7 * n - 12];
        _displayIndices = new int[15 * n - 36];
        _tileVertOffset = new int[n];
        int v = 0, t = 0;
        for (int i = 0; i < n; i++)
        {
            ulong cell = ball.CellIds[i];
            _tileVertOffset[i] = v;
            ulong[] vids = H3.CellToVertexes(cell);
            for (int k = 0; k < vids.Length; k++)
            {
                _displayVerts[v++] = ball.VertexPositions[ball.VertexIndexOf(vids[k])];
            }
            if (i == 0)
            {
                for (int k = 0; k < vids.Length; k++)
                {
                    GD.Print(ball.VertexPositions[ball.VertexIndexOf(vids[k])]);
                }
            }
            int cornerCount = vids.Length;
            int v0 = _tileVertOffset[i];
            for (int k = 1; k < cornerCount; k++)
            {
                _displayIndices[t++] = v0;
                _displayIndices[t++] = v0 + k;
                _displayIndices[t++] = v0 + k + 1;
            }
        }

    }

    public Color[] BuildTileColors(Ball ball, Func<ulong, Color> colorFn)
    {
        var colors = new Color[_displayVerts.Length];
        ulong[] cells = ball.CellIds;
        for (int i = 0; i < cells.Length; i++)
        {
            Color c = colorFn(cells[i]);
            int off = _tileVertOffset[i];
            int blockLen = (i + 1 < cells.Length ? _tileVertOffset[i + 1] : _displayVerts.Length) - off;
            for (int k = 0; k < blockLen; k++)
                colors[off + k] = c;
        }
        return colors;
    }

}