using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Utils.H3;

namespace World.Tests;

/// <summary>
/// new_HexWorld 静态地壳生成（H3Plate，设计-01 v1.1）正确性测试。
/// 断言的测量项（文档 §6 中可自动化的部分）：确定性、铺满无空洞、每板连通、
/// 海陆=板块属性两级料场（含海拔初值）、边界链段集与异板共享边集合逐段对账（无遗漏无重复）、
/// 链不跨三板块交汇点（断链语义）、多种子海陆占比围绕 LandFrac。
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService。Ball 需 H3 原生库（测试工程已引用）。
/// </summary>
public class H3PlateStaticTests
{
    private const int Res = 2;          // res2：N=5882 格——测试网格（构造 ~百 ms 级，类内共享）
    private const int Plates = 15;      // 文档默认 P（10–20 区间）
    private const int Seed = 42;        // 文档默认 seed（场景默认同值）

    // 类级共享网格（构造昂贵：全格 CellToVertexes/GridDisk；只建一次跨用例复用）
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;

    static H3Plate Generate(int seed, int plates = Plates, float landFrac = 1f / 3f, Ball ball = null)
    {
        var plate = new H3Plate(ball ?? Ball);
        plate.CreatePlates(plates, seed, landFrac);
        return plate;
    }

    // ═══════════════════════════════════════════════════════════════
    // 确定性（测量项：同 seed 两次生成逐位一致）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void CreatePlates_SameSeedTwice_AllFieldsBitwiseIdentical()
    {
        H3Plate a = Generate(Seed), b = Generate(Seed);
        CollectionAssert.AreEqual(a.Crust.PlateId, b.Crust.PlateId);
        CollectionAssert.AreEqual(a.Crust.FelsicThick, b.Crust.FelsicThick);
        CollectionAssert.AreEqual(a.Crust.MaficThick, b.Crust.MaficThick);
        CollectionAssert.AreEqual(a.Crust.Age, b.Crust.Age);
        CollectionAssert.AreEqual(a.Crust.Elevation, b.Crust.Elevation);
    }

    [Test]
    public void CreatePlates_DifferentSeeds_LayoutsDiffer()
    {
        // 弱断言：不同 seed 的抽取/生长/陆性序列不同 → 至少板表陆性组合或归属不完全一致
        H3Plate a = Generate(Seed), b = Generate(Seed + 1);
        bool samePlateId = a.Crust.PlateId.SequenceEqual(b.Crust.PlateId);
        bool sameLand = a.Plates.Select(p => p.IsLand).SequenceEqual(b.Plates.Select(p => p.IsLand));
        Assert.IsFalse(samePlateId && sameLand, "不同 seed 应产生不同板块格局");
    }

    // ═══════════════════════════════════════════════════════════════
    // 铺满 / 连通 / 两级料场（测量项：场完整、海陆=板属性）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void CreatePlates_CoversAllCells_NoHolesNoEmptyPlates()
    {
        var plate = Generate(Seed);
        int n = plate.Crust.PlateId.Length;
        var counts = new int[Plates];
        foreach (int p in plate.Crust.PlateId)
        {
            Assert.That(p, Is.InRange(0, Plates - 1), "归属板号越界 = 空洞/脏数据");
            counts[p]++;
        }
        Assert.AreEqual(n, counts.Sum(), "归属计数总和须等于格数");
        for (int p = 0; p < Plates; p++)
            Assert.Greater(counts[p], 0, $"板 {p} 无格（种子胞必占 ≥1 胞）");
    }

    [Test]
    public void CreatePlates_EachPlate_ConnectedOnCellNeighborGraph()
    {
        var plate = Generate(Seed);
        var ball = Ball;
        int n = plate.Crust.PlateId.Length;
        var counts = new int[Plates];
        foreach (int p in plate.Crust.PlateId) counts[p]++;

        // 逐板 BFS（经格邻居表，只走同板格）→ 到达数须等于该板格数
        for (int p = 0; p < Plates; p++)
        {
            int start = Array.FindIndex(plate.Crust.PlateId, x => x == p);
            var seen = new bool[n];
            var queue = new Queue<int>();
            queue.Enqueue(start);
            seen[start] = true;
            int reached = 0;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                reached++;
                foreach (int j in ball.CellNeighbors[i])
                {
                    if (!seen[j] && plate.Crust.PlateId[j] == p)
                    {
                        seen[j] = true;
                        queue.Enqueue(j);
                    }
                }
            }
            Assert.AreEqual(counts[p], reached, $"板 {p} 不连通（BFS 到达 {reached}/{counts[p]}）");
        }
    }

    [Test]
    public void CreatePlates_TwoTierCrustFields_MatchDesignConstants()
    {
        var plate = Generate(Seed);
        var crust = plate.Crust;
        int landCells = 0, oceanCells = 0;
        for (int i = 0; i < crust.PlateId.Length; i++)
        {
            int p = crust.PlateId[i];
            if (plate.Plates[p].IsLand)
            {
                landCells++;
                Assert.AreEqual(H3Plate.LandFelsicThicknessM, crust.FelsicThick[i], $"陆格 {i} felsic");
                Assert.AreEqual(0f, crust.MaficThick[i], $"陆格 {i} mafic 应为 0");
                Assert.AreEqual(H3Plate.ContinentalAgeMy, crust.Age[i], $"陆格 {i} age");
                Assert.AreEqual(H3Plate.LandElevationM, crust.Elevation[i], $"陆格 {i} elevation");
            }
            else
            {
                oceanCells++;
                Assert.AreEqual(0f, crust.FelsicThick[i], $"洋格 {i} felsic 应为 0");
                Assert.AreEqual(H3Plate.OceanMaficThicknessM, crust.MaficThick[i], $"洋格 {i} mafic");
                Assert.AreEqual(H3Plate.OceanicAgeMy, crust.Age[i], $"洋格 {i} age");
                Assert.AreEqual(-H3Plate.OceanDepthM, crust.Elevation[i], $"洋格 {i} elevation");
            }
            Assert.AreEqual(0f, crust.SedimentThick[i], "沉积物本阶段全 0");
        }
        Assert.Greater(landCells, 0, "应有陆格（LandFrac≈1/3、P=15 时几乎必然）");
        Assert.Greater(oceanCells, 0, "应有洋格");
        Assert.AreEqual(landCells + oceanCells, crust.PlateId.Length, "陆洋格数合计 = 总格数（海陆=板属性全覆盖）");
    }

    [Test]
    public void CreatePlates_CrustIsLand_MatchesPlateAttribute()
    {
        // 统一判陆口径不变量：Crust.IsLand（长英质厚>0 派生）必须与板属性 IsLand 逐格一致
        var plate = Generate(Seed);
        var crust = plate.Crust;
        for (int i = 0; i < crust.PlateId.Length; i++)
            Assert.AreEqual(plate.Plates[crust.PlateId[i]].IsLand, crust.IsLand(i),
                $"格 {i} 的 Crust.IsLand 与板属性 IsLand 不一致（判陆口径被破坏）");
    }

    [Test]
    public void CreatePlates_InvalidPlateCount_ThrowsLeavesStateClean()
    {
        // 参数校验前置：非法参数当场抛，且不留半成品状态（同实例换合法参数可直接重试）
        var plate = new H3Plate(Ball);
        Assert.Throws<ArgumentOutOfRangeException>(() => plate.CreatePlates(1, Seed), "板块数至少 2");
        Assert.Throws<ArgumentOutOfRangeException>(() => plate.CreatePlates(int.MaxValue, Seed), "超过格数须抛");
        Assert.IsNull(plate.Crust, "非法参数抛出后不得留下半成品地壳场");

        plate.CreatePlates(Plates, Seed);   // 抛出后对象未被污染，合法参数可直接重跑
        Assert.IsNotNull(plate.Crust);
        Assert.AreEqual(Plates, plate.NumPlates);
    }

    [Test]
    public void CreatePlates_ManySeeds_LandShareAroundLandFrac()
    {
        // 40 个 seed 平均陆占比应贴近 1/3（二项+板大小涨落），区间宽松防误伤
        const int seeds = 40;
        long landTotal = 0, cellTotal = 0;
        for (int s = 1; s <= seeds; s++)
        {
            var plate = Generate(s);
            int n = plate.Crust.PlateId.Length;
            cellTotal += n;
            for (int i = 0; i < n; i++)
                if (plate.Plates[plate.Crust.PlateId[i]].IsLand) landTotal++;
        }
        double avg = (double)landTotal / cellTotal;
        Assert.That(avg, Is.InRange(0.18, 0.50), $"40 seed 平均陆占比 {avg:F3} 偏离 LandFrac=1/3 过多");
    }

    // ═══════════════════════════════════════════════════════════════
    // 边界链提取（01 §4：对账 = 无遗漏无重复；链不跨三板块交汇 = 断链语义）
    // ═══════════════════════════════════════════════════════════════

    // 独立期望：直接枚举异板格邻居对求共享边（顶点 id 规范化序）——与实现同语义但独立书写，用于对账
    static (ulong, ulong) SharedEdgeIds(ulong cellI, ulong cellJ)
    {
        ulong[] vi = H3.CellToVertexes(cellI);
        ulong[] vj = H3.CellToVertexes(cellJ);
        ulong a = 0, b = 0;
        int count = 0;
        foreach (ulong v in vj)
        {
            foreach (ulong u in vi)
            {
                if (v != u) continue;
                if (count == 0) a = v; else b = v;
                count++;
                break;
            }
        }
        Assert.AreEqual(2, count, "H3 相邻格应恰共享一条整边");
        return a < b ? (a, b) : (b, a);
    }

    [Test]
    public void ExtractBoundaryChains_Segments_EqualSharedEdgeSet()
    {
        var plate = Generate(Seed);
        var ball = Ball;
        var crust = plate.Crust;
        int n = crust.PlateId.Length;

        // 期望：异板格对共享边（id 规范化序）
        var expect = new HashSet<(ulong, ulong)>();
        var expectDegree = new Dictionary<ulong, int>();
        for (int i = 0; i < n; i++)
        {
            int pi = crust.PlateId[i];
            foreach (int j in ball.CellNeighbors[i])
            {
                if (j <= i || crust.PlateId[j] == pi) continue;
                var edge = SharedEdgeIds(ball.CellIds[i], ball.CellIds[j]);
                expect.Add(edge);
                expectDegree.TryGetValue(edge.Item1, out int d1);
                expectDegree.TryGetValue(edge.Item2, out int d2);
                expectDegree[edge.Item1] = d1 + 1;
                expectDegree[edge.Item2] = d2 + 1;
            }
        }
        Assert.Greater(expect.Count, 0, "P≥2 时全球必有板边界");

        // 实际：链内相邻点对（位置 → 顶点 id 反查，按 id 规范化）
        var chains = H3Plate.ExtractBoundaryChains(ball, crust.PlateId);
        Assert.Greater(chains.Count, 0, "应至少一条边界链");
        var posToId = new Dictionary<Vector3, ulong>();
        for (int v = 0; v < ball.VertexIds.Length; v++) posToId[ball.VertexPositions[v]] = ball.VertexIds[v];

        var actual = new HashSet<(ulong, ulong)>();
        foreach (var chain in chains)
        {
            Assert.GreaterOrEqual(chain.Length, 2, "链至少两点（一折线段）");
            for (int k = 0; k < chain.Length - 1; k++)
            {
                ulong x = posToId[chain[k]];
                ulong y = posToId[chain[k + 1]];
                actual.Add(x < y ? (x, y) : (y, x));
            }
        }

        // 对账：段集合逐段相等（无遗漏无重复）；链内内部顶点在原边界图中度必须 = 2（未跨三板块交汇）
        CollectionAssert.AreEquivalent(expect, actual, "链段集合与异板共享边集合不一致：有漏段或重复/伪段");
        foreach (var chain in chains)
        {
            bool closed = posToId[chain[0]] == posToId[chain[^1]];
            int lo = closed ? 0 : 1;                 // 闭环：除尾部重复起点外全为内部点；开放链：去掉首尾端点
            int hi = closed ? chain.Length - 1 : chain.Length - 1;
            for (int k = lo; k < hi; k++)
            {
                ulong id = posToId[chain[k]];
                int degree = expectDegree.TryGetValue(id, out int d) ? d : 0;
                Assert.AreEqual(2, degree, $"链内部顶点 {H3.H3ToString(id)} 度 {degree} ≠ 2：链跨过了三板块交汇点（断链语义被破坏）");
            }
            // 开放链端点 = 三板块交汇（度≥3）处断链；端点度为 2 说明链被错误截断在普通边界点上
            if (!closed)
            {
                ulong endA = posToId[chain[0]], endB = posToId[chain[^1]];
                Assert.AreNotEqual(2, expectDegree[endA], $"开放链端点 {H3.H3ToString(endA)} 度为 2：链截断位置错误");
                Assert.AreNotEqual(2, expectDegree[endB], $"开放链端点 {H3.H3ToString(endB)} 度为 2：链截断位置错误");
            }
        }
    }

    [Test]
    public void ExtractBoundaryCellEdges_ExactlyDifferentPlateEdges_BothSides()
    {
        var plate = Generate(Seed);
        var ball = Ball;
        var crust = plate.Crust;
        int n = crust.PlateId.Length;

        // 独立期望：每条异板共享边，边界两侧【各格】名下都应记账（描边骑缝语义）
        var expect = new HashSet<(ulong cell, ulong va, ulong vb)>();
        for (int i = 0; i < n; i++)
        {
            int pi = crust.PlateId[i];
            foreach (int j in ball.CellNeighbors[i])
            {
                if (j <= i || crust.PlateId[j] == pi) continue;
                var edge = SharedEdgeIds(ball.CellIds[i], ball.CellIds[j]);
                expect.Add((ball.CellIds[i], edge.Item1, edge.Item2));
                expect.Add((ball.CellIds[j], edge.Item1, edge.Item2));
            }
        }
        Assert.Greater(expect.Count, 0, "P≥2 时全球必有板边界");

        var actual = H3Plate.ExtractBoundaryCellEdges(ball, crust.PlateId);
        CollectionAssert.AreEquivalent(expect, actual, "描边格边集与异板共享边两侧记账不一致：有漏记/多记");

        // 记账的顶点必须真属该格（旗标构建按 (格, 边顶点对) 查集，错记会描到不相干格边）
        foreach (var (cell, va, vb) in actual)
        {
            ulong[] vids = H3.CellToVertexes(cell);
            Assert.That(vids, Has.Member(va), $"格 {H3.H3ToString(cell)} 被记了不属于它的顶点 {H3.H3ToString(va)}");
            Assert.That(vids, Has.Member(vb), $"格 {H3.H3ToString(cell)} 被记了不属于它的顶点 {H3.H3ToString(vb)}");
        }

        // 回归（2026-09-09 用户报告：五边贴异板的格其同板边整条误描）：逐格逐边核对——
        // 边 ∈ 集 ⟺ 跨该边的邻居格异板。边粒度记账的硬保证：同板边绝不入集（角点怎么贴边界都不行）
        for (int i = 0; i < n; i++)
        {
            ulong cellI = ball.CellIds[i];
            ulong[] vids = H3.CellToVertexes(cellI);
            int m = vids.Length;
            for (int k = 0; k < m; k++)
            {
                ulong va = vids[k], vb = vids[(k + 1) % m];
                (ulong, ulong) pair = va < vb ? (va, vb) : (vb, va);
                bool inSet = actual.Contains((cellI, pair.Item1, pair.Item2));
                bool crossesDifferentPlate = false;
                foreach (int j in ball.CellNeighbors[i])
                {
                    var edge = SharedEdgeIds(cellI, ball.CellIds[j]);
                    if ((edge.Item1, edge.Item2) != pair) continue;
                    crossesDifferentPlate = crust.PlateId[j] != crust.PlateId[i];
                    break;
                }
                Assert.AreEqual(crossesDifferentPlate, inSet,
                    $"格 {H3.H3ToString(cellI)} 边 {H3.H3ToString(va)}-{H3.H3ToString(vb)} 记账状态与跨边邻居异板与否不符（同板边被误记/异板边漏记）");
            }
        }
    }
}
