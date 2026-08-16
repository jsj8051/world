using System;
using System.Collections.Generic;
using Godot;
using NUnit.Framework;
using World.HexPlanet;

namespace World.Tests;

/// <summary>
/// HexPlanet 模块纯托管测试(L0)。仅依赖 BCL + Godot 数学类型(Vector3/Mathf 托管实现),
/// 可在 dotnet test / 本地执行器直接运行, 不触碰 Godot 原生调用(LogService/GD.* 等)。
///
/// 纪律约束:
///  - 只用 [Test]/[TestCase(字面量参数)], 不用 [SetUp]/[TearDown]/[TestCaseSource]/[Theory]。
///  - Subdivide 为纯几何函数（2026-08 重构后无日志），可直接调用。
///    在无引擎测试进程=进程级崩溃(探针实测 0xC0000005)。
///  - 球面细分几何量采用 km 单位约定(源码 Icosahedron.VertexKey / SubdividedMesh.VertexKey
///    按 1km 量化), 统一 radius=6371f: 足够大的半径保证不同顶点量化后不误合并, 顶点数正确。
/// </summary>
public class HexPlanetTests
{
    /// <summary>球半径(km), 与源码 1km 量化约定一致, 保证细分顶点去重不误合并。</summary>
    private const float Radius = 6371f;

    // ─────────────────────────────────────────────────────────────────────────────
    // Icosahedron: 顶点/面数公式(全项目唯一公式源)
    // ─────────────────────────────────────────────────────────────────────────────

    [TestCase(1, 12)]
    [TestCase(2, 42)]
    [TestCase(4, 162)]
    [TestCase(16, 2562)]
    public void VertexCountFor_MatchesFormula(int n, int expected)
    {
        Assert.AreEqual(expected, Icosahedron.VertexCountFor(n));
    }

    [TestCase(1, 12)]
    [TestCase(2, 42)]
    [TestCase(4, 162)]
    [TestCase(16, 2562)]
    public void VertexCountForLong_MatchesFormula(int n, int expected)
    {
        Assert.AreEqual((long)expected, Icosahedron.VertexCountForLong(n));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(8)]
    [TestCase(16)]
    public void GridNFromVertexCount_IsInverseOfVertexCountFor(int n)
    {
        // ComputeVertexCount(=10n²+2) 与 GridNFromVertexCount 互逆(往返)
        Assert.AreEqual(n, Icosahedron.GridNFromVertexCount(Icosahedron.VertexCountFor(n)));
    }

    [Test]
    public void VertexCountForLong_HandlesLargeN_NoOverflow()
    {
        // long 安全版: 大 n 不溢出且保持精确公式值
        Assert.AreEqual(10L * 512 * 512 + 2, Icosahedron.VertexCountForLong(512));
        Assert.AreEqual(10L * 100000 * 100000 + 2, Icosahedron.VertexCountForLong(100000));
        // 超过 int 可表示范围仍精确（10n²+2 > int.MaxValue ⇔ n > 14654；int 版会溢出回绕）
        Assert.AreEqual(10L * 46340 * 46340 + 2, Icosahedron.VertexCountForLong(46340));
        // 边界一致: n=14654 是 int 版 10n²+2 仍可精确表示的最大 n, 两版应相等
        Assert.AreEqual((long)Icosahedron.VertexCountFor(14654), Icosahedron.VertexCountForLong(14654));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Icosahedron.Subdivide: 计数 / 球面 / 唯一性 / 索引合法性
    // ─────────────────────────────────────────────────────────────────────────────

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void Subdivide_ProducesExpectedCounts(int n)
    {
        Icosahedron.Subdivide(n, Radius, out var verts, out var indices);

        Assert.AreEqual(Icosahedron.VertexCountFor(n), verts.Count);
        Assert.AreEqual(0, indices.Count % 3, "索引数应为 3 的倍数");
        Assert.AreEqual(20 * n * n, indices.Count / 3);
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void Subdivide_AllVerticesOnSphere(int n)
    {
        Icosahedron.Subdivide(n, Radius, out var verts, out var indices);

        float tol = Radius * 1e-4f;
        foreach (var v in verts)
            AssertScalarNear(v.Length(), Radius, tol, $"顶点 {v} 应落在半径 {Radius} 球面上");
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void Subdivide_UniqueVerticesAndValidIndices(int n)
    {
        Icosahedron.Subdivide(n, Radius, out var verts, out var indices);

        // 顶点唯一性: 去重后应恰为 10n²+2(网格最简顶点数, 无重复顶点)
        Assert.AreEqual(Icosahedron.VertexCountFor(n), verts.Count, "存在重复顶点或去重误合并");

        foreach (var idx in indices)
        {
            Assert.GreaterOrEqual(idx, 0, "三角形索引不得为负");
            Assert.Less(idx, verts.Count, "三角形索引越界");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // SubdividedMesh: 去重 / VertToTris 一致性 / TriNeighbors
    // ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void SubdividedMesh_DeduplicatesAndSharedEdgeIsNeighbor()
    {
        // 两个三角形共享边 AB(引用完全相同的顶点坐标), 各有一重复引用。
        // 去重后: 4 个唯一顶点、2 个 tri、共享边 AB → 两 tri 互为邻居。
        var verts = new List<Vector3>
        {
            new(0, 0, 0),      // A
            new(1000, 0, 0),   // B
            new(0, 1000, 0),   // C
            new(0, 0, 1000),   // D
            new(0, 0, 0),      // A(重复坐标)
            new(1000, 0, 0),   // B(重复坐标)
        };
        var indices = new List<int>
        {
            0, 1, 2, // tri0: A B C
            4, 5, 3, // tri1: A B D
        };

        var mesh = new SubdividedMesh(verts, indices);

        Assert.AreEqual(4, mesh.UniqueVerts.Count, "共享顶点 A/B 应被去重");
        Assert.AreEqual(2, mesh.Tris.Count, "应保留 2 个三角形");

        // VertToTris 一致性: 每个 tri 的 3 个顶点都必须包含该 tri 索引
        for (int t = 0; t < mesh.Tris.Count; t++)
        {
            var tri = mesh.Tris[t];
            Assert.True(mesh.VertToTris[tri.v0].Contains(t), $"顶点 {tri.v0} 应包含 tri {t}");
            Assert.True(mesh.VertToTris[tri.v1].Contains(t), $"顶点 {tri.v1} 应包含 tri {t}");
            Assert.True(mesh.VertToTris[tri.v2].Contains(t), $"顶点 {tri.v2} 应包含 tri {t}");
        }

        // TriNeighbors: 共享边 AB → 互为邻居; 各自恰有 1 个三角形邻居(另两边独享)
        Assert.AreEqual(1, mesh.TriNeighbors[0].Count);
        Assert.AreEqual(1, mesh.TriNeighbors[1].Count);
        Assert.True(mesh.TriNeighbors[0].Contains(1), "tri0 应以 tri1 为邻居");
        Assert.True(mesh.TriNeighbors[1].Contains(0), "tri1 应以 tri0 为邻居");
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void SubdividedMesh_TriNeighbors_SymmetricNoSelf_EveryEdgeShared(int n)
    {
        Icosahedron.Subdivide(n, Radius, out var verts, out var indices);
        var mesh = new SubdividedMesh(verts, indices);

        for (int t = 0; t < mesh.TriNeighbors.Count; t++)
        {
            Assert.GreaterOrEqual(mesh.TriNeighbors[t].Count, 3, "闭合流形每个三角形应有 3 条共享边");

            foreach (var nb in mesh.TriNeighbors[t])
            {
                Assert.AreNotEqual(t, nb, $"三角形 {t} 不得自邻");
                Assert.True(mesh.TriNeighbors[nb].Contains(t), $"邻接应对称: {t} → {nb}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // GoldbergBuilder: 经典 Goldberg 多边形计数
    // ─────────────────────────────────────────────────────────────────────────────

    [TestCase(1, 12, 12, 0)]
    [TestCase(2, 42, 12, 30)]
    [TestCase(4, 162, 12, 150)]
    public void GoldbergBuilder_ClassicCounts(int n, int total, int penta, int hexa)
    {
        Icosahedron.Subdivide(n, Radius, out var verts, out var indices);
        var mesh = new SubdividedMesh(verts, indices);
        var builder = new GoldbergBuilder(mesh, Radius);

        Assert.AreEqual(total, builder.Tiles.Count, "tile 数应等于唯一顶点数");

        int p = 0, h = 0;
        foreach (var tile in builder.Tiles)
        {
            // IsPentagon 语义 = 邻居数 5(读源码确认); 非五边形应恰为 6 邻居
            if (tile.IsPentagon)
            {
                p++;
                Assert.AreEqual(5, tile.Neighbors.Length, $"五边形 {tile.Id} 应有 5 个邻居");
            }
            else
            {
                h++;
                Assert.AreEqual(6, tile.Neighbors.Length, $"六边形 {tile.Id} 应有 6 个邻居");
            }
        }

        Assert.AreEqual(penta, p, "五边形(邻居数 5)数量");
        Assert.AreEqual(hexa, h, "六边形(邻居数 6)数量");
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void GoldbergBuilder_NeighborsSymmetricAndCornersMatch(int n)
    {
        Icosahedron.Subdivide(n, Radius, out var verts, out var indices);
        var mesh = new SubdividedMesh(verts, indices);
        var builder = new GoldbergBuilder(mesh, Radius);

        foreach (var tile in builder.Tiles)
        {
            Assert.AreEqual(tile.Neighbors.Length, tile.Corners.Length,
                $"tile {tile.Id} 角点数应等于邻居数");

            foreach (int nb in tile.Neighbors)
            {
                var other = builder.Tiles[nb];
                Assert.AreEqual(nb, other.Id, "Tiles 应按顶点索引定序(Id == 索引)");
                Assert.GreaterOrEqual(Array.IndexOf(other.Neighbors, tile.Id), 0,
                    $"邻居应互指: tile {tile.Id} <-> tile {nb}");
            }
        }
    }

    [Test]
    public void GoldbergBuilder_ManualIcosahedron_AllPentagons()
    {
        // 手工构造 12 顶点 / 20 三角的 icosahedron(黄金比例坐标)。
        // 顶点顺序与 Icosahedron.BaseFaces 的连通性一致(该面表即标准布局)。
        float phi = (1f + Mathf.Sqrt(5f)) / 2f;
        var verts = new List<Vector3>
        {
            new(0, 1, phi),     // 0
            new(0, 1, -phi),    // 1
            new(0, -1, phi),    // 2
            new(0, -1, -phi),   // 3
            new(phi, 0, 1),     // 4
            new(phi, 0, -1),    // 5
            new(-phi, 0, 1),    // 6
            new(-phi, 0, -1),   // 7
            new(1, phi, 0),     // 8
            new(-1, phi, 0),    // 9
            new(1, -phi, 0),    // 10
            new(-1, -phi, 0),   // 11
        };
        int[] flatFaces =
        {
            0, 2, 4, 0, 2, 5, 0, 4, 8, 0, 5, 10, 0, 8, 10,
            1, 3, 6, 1, 3, 7, 1, 6, 8, 1, 7, 10, 1, 8, 10,
            2, 4, 9, 2, 5, 11, 2, 9, 11, 3, 6, 9, 3, 7, 11,
            3, 9, 11, 4, 6, 8, 4, 6, 9, 5, 7, 10, 5, 7, 11,
        };
        var indices = new List<int>(flatFaces);

        var mesh = new SubdividedMesh(verts, indices);
        Assert.AreEqual(12, mesh.UniqueVerts.Count, "黄金比例 icosahedron 应有 12 个唯一顶点");
        Assert.AreEqual(20, mesh.Tris.Count, "应有 20 个三角形");

        var builder = new GoldbergBuilder(mesh, Radius);
        Assert.AreEqual(12, builder.Tiles.Count, "icosahedron 应有 12 个 tile");

        foreach (var tile in builder.Tiles)
        {
            Assert.True(tile.IsPentagon, $"tile {tile.Id} 应为五边形(5 邻居)");
            Assert.AreEqual(5, tile.Neighbors.Length);
            Assert.AreEqual(5, tile.Corners.Length);
            AssertScalarNear(tile.Center.Length(), Radius, Radius * 1e-4f, $"tile {tile.Id} 中心应落在球面上");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // HexTile 字段一致性 + 模块级测试
    // ─────────────────────────────────────────────────────────────────────────────

    [TestCase(1)]
    [TestCase(4)]
    public void HexTile_FieldsConsistent(int n)
    {
        Icosahedron.Subdivide(n, Radius, out var verts, out var indices);
        var mesh = new SubdividedMesh(verts, indices);
        var builder = new GoldbergBuilder(mesh, Radius);

        Assert.AreEqual(mesh.UniqueVerts.Count, builder.Tiles.Count, "tile 数应等于唯一顶点数");

        for (int i = 0; i < builder.Tiles.Count; i++)
        {
            var tile = builder.Tiles[i];
            Assert.AreEqual(i, tile.Id, "Id 应为顶点索引(无空槽时按序入列)");
            AssertScalarNear(tile.Center.Length(), Radius, Radius * 1e-4f, $"tile {i} 中心长度");
            // Center 方向应等于该顶点单位方向(单位方向 × radius)
            var dir = mesh.UniqueVerts[i].Normalized();
            AssertScalarNear(tile.Center.Normalized().Dot(dir), 1f, 1e-3f, $"tile {i} 中心方向");
        }
    }

    [TestCase(2)]
    public void ModuleTest_SubdividedSphere_AllDegrees5Or6(int n)
    {
        Icosahedron.Subdivide(n, Radius, out var verts, out var indices);
        var mesh = new SubdividedMesh(verts, indices);
        var builder = new GoldbergBuilder(mesh, Radius);

        // 模块级不变量: 细分球上每个 tile 的邻居数只能为 5(五边形)或 6(六边形)
        Assert.AreEqual(builder.Tiles.Count, mesh.UniqueVerts.Count);
        foreach (var tile in builder.Tiles)
        {
            Assert.True(tile.Neighbors.Length == 5 || tile.Neighbors.Length == 6,
                $"tile {tile.Id} 邻接度应为 5 或 6, 实为 {tile.Neighbors.Length}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 工具
    // ─────────────────────────────────────────────────────────────────────────────

    private static void AssertScalarNear(float actual, float expected, float tolerance, string message)
    {
        Assert.AreEqual(expected, actual, tolerance, message);
    }
}
