using Godot;
using System.Collections.Generic;
using World.MapGen;

namespace World.MapView;

/// <summary>
/// 显示格 ↔ 逻辑格索引映射（2026-08-19，P2-③ 索引收敛）。
///
/// 历史教训（63km 错位案 2026-08-18）：
///   显示格（Goldberg 面）编号 ≠ 逻辑格（Icosahedron 顶点）编号——两套排序不同，
///   用面编号查顶点数组 → 显示位置与逻辑位置错位 63km（实测）。MapViewer 曾散落
///   _tileVerts[id] 手写映射 + 顶点→面反查表，本类统一收敛：
///     · FaceToVertex(f)    显示面 → 逻辑顶点（读逻辑数据必经）
///     · FacesOf(v)         逻辑顶点 → 显示面列表（写显示数据必经，如驻扎格势力）
///     · PopToFace(f)       人口显示辅助（人口按顶点写，显示面读其顶点）
///
/// 不变量：逻辑数据按顶点空间读写，显示数据按面空间读写，禁止裸下标互串。
/// </summary>
public sealed class TileIndex
{
    private readonly int[] _faceToVertex;   // 显示面 i → 逻辑顶点 id
    private readonly List<int>[] _vertexToFaces;   // 逻辑顶点 v → 显示面列表（惰性反查）

    public int Count => _faceToVertex.Length;

    /// <summary>构建：每显示面取最近模拟顶点（crisp flat per-tile，用户偏好）。</summary>
    public TileIndex(MapData map, Vector3[] centers)
    {
        int n = centers.Length;
        _faceToVertex = new int[n];
        for (int i = 0; i < n; i++)
            _faceToVertex[i] = map.NearestVertex(centers[i]);
        _vertexToFaces = new List<int>[n];
    }

    /// <summary>显示面 → 逻辑顶点 id（读逻辑数据唯一入口）。</summary>
    public int FaceToVertex(int face) => _faceToVertex[face];

    /// <summary>逻辑顶点 → 显示面列表（写显示数据唯一入口；惰性建反查）。</summary>
    public List<int> FacesOf(int vertex)
    {
        var list = _vertexToFaces[vertex];
        if (list == null)
        {
            list = new List<int>(2);
            for (int f = 0; f < _faceToVertex.Length; f++)
                if (_faceToVertex[f] == vertex) list.Add(f);
            _vertexToFaces[vertex] = list;
        }
        return list;
    }
}
