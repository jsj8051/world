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
///  - 球面细分几何量采用 km 单位约定(源码 Icosahedron.VertexKey 按 1km 量化),
///    统一 radius=6371f: 足够大的半径保证不同顶点量化后不误合并, 顶点数正确。
///
/// ★2026-10-06 D-A（HexPlanet Legacy Closure）：本文件原含 14 个测试，
///   其中 7 个覆盖已清退的 `SubdividedMesh` / `GoldbergBuilder` / `HexTile`
///   闭包（含 `ModuleTest_SubdividedSphere_AllDegrees5Or6`），随能力一并删除。
///   保留的 7 个测试全部针对 `Icosahedron` —— 它是**全项目唯一公式源**(10n²+2)
///   且被 9+ 个测试文件当"合成小网格"夹具，故必须保留其覆盖。
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
    // 工具
    // ─────────────────────────────────────────────────────────────────────────────

    private static void AssertScalarNear(float actual, float expected, float tolerance, string message)
    {
        Assert.AreEqual(expected, actual, tolerance, message);
    }
}
