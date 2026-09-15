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
/// 链不跨三板块交汇点（断链语义）、板表与板缘分类自洽。
/// ⚠️ 03 起改走**动态路线**：陆性不再是板属性（一块板可同时有陆有洋），海陆占比由初始海洋格占比
/// + 600 My 演化共同决定 —— 故「两级料场模板」「陆性 = 板属性」那几条断言已随 02 作废删除。
///（03 v1.9 动态**初始化**又改回板级陆洋属性——那是动态模拟的初始口径，与本静态路线无关，见 03。）
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService。Ball 需 H3 原生库（测试工程已引用）。
/// </summary>
public class H3PlateStaticTests
{
    // res3：N=41162 格。**不用 res2**（用户 2026-09-10 拍板）：带宽按板块尺度缩放后，
    // 弧带全宽 420 km 而 res2 格宽 294 km → 带形落在格与格之间、弧地形在网格上生不出来。
    private const int Res = 3;
    private const int Plates = 15;      // 文档默认 P（10–20 区间）
    private const int Seed = 42;        // 文档默认 seed（场景默认同值）

    // 类级共享网格（构造昂贵：全格 CellToVertexes/GridDisk；只建一次跨用例复用）
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;

    // ⚠️ 口径修正（04 批次 0）：旧名 landFrac = 1/3 直接喂给 oceanFraction——**同名参数语义相反**，
    // 测试世界实际海占 1/3（与场景默认 0.6 相反）。改回与 CreatePlates 同名同默认，消除反义陷阱。
    static H3Plate Generate(int seed, int plates = Plates, float oceanFraction = 0.6f, Ball ball = null)
    {
        var plate = new H3Plate(ball ?? Ball);
        plate.CreatePlates(plates, seed, oceanFraction);
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
        // 04 批次 4：缝合/裂解/重启会改板数与板号（裂解新板号可 ≥ 初始板数）——口径放宽为
        // "逐格归属非负、出现的每个板号都非空、板数 ∈ [2, 2×初始]"（restart 重置回初始数）。
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        foreach (int p in plate.Crust.PlateId)
        {
            Assert.GreaterOrEqual(p, 0, "归属板号非负 = 空洞/脏数据的回归判据");
            counts[p] = counts.GetValueOrDefault(p) + 1;
        }
        Assert.AreEqual(n, counts.Values.Sum(), "归属计数总和须等于格数");
        Assert.That(counts.Count, Is.InRange(2, Plates * 2),
            $"终态板数 {counts.Count}（缝合可减、裂解可增，上限守卫 = 2×初始）");
        foreach (var kv in counts)
            Assert.Greater(kv.Value, 0, $"板 {kv.Key} 无格（空板不应出现在计数里）");
    }

    [Test]
    public void CreatePlates_EachPlate_MainBodyConnectedOnCellNeighborGraph()
    {
        var plate = Generate(Seed);
        var ball = Ball;
        int n = plate.Crust.PlateId.Length;
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        foreach (int p in plate.Crust.PlateId)
        {
            Assert.GreaterOrEqual(p, 0, "有格无主（PlateId = -1：平流空洞未填）——裂谷填充步骤的回归判据");
            counts[p] = counts.GetValueOrDefault(p) + 1;
        }

        // 逐板找**最大连通分量**（经格邻居表，只走同板格）：从每个未访问的同板格起 BFS，
        // 取最大者。⚠️ 不能只从"首个找到的格"BFS —— 那个格可能恰好落在一个小碎片里。
        foreach (int p in counts.Keys)
        {
            var seen = new bool[n];
            var queue = new System.Collections.Generic.Queue<int>();
            int largest = 0;
            for (int cell = 0; cell < n; cell++)
            {
                if (plate.Crust.PlateId[cell] != p || seen[cell]) continue;
                queue.Enqueue(cell);
                seen[cell] = true;
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
                if (reached > largest) largest = reached;
            }
            // ⚠️ 03 动态路线下**板块可以被平流撕碎**——平流是"逐格按本板旋转增量搬运"，
            // 板缘的格会与板主体错开，一块板的格不再保证在邻居图上整体连通。故只断言"主体连通"
            // （最大连通分量 ≥ 半数格）；碎片化程度交给诊断场景判读（这是动态路线的已知性质，
            // 不是缺陷：真实板块本来也会被走滑断层切成碎块）。
            Assert.GreaterOrEqual(largest, counts[p] / 2, $"板 {p} 主体不连通（最大连通分量 {largest}/{counts[p]}）");
        }
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

    // ═══════════════════════════════════════════════════════════════
    // 加权随机生长分板（2026-09-15 拍板，替换撒点-合并 Voronoi）特性
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SplitIntoPlates_Growth_EachPlateExactlyOneComponent()
    {
        // 生长逐格**贴邻认领** ⇒ 每板天然单连通（强于旧路线"主体连通"的硬回归判据：
        // Voronoi 的孤岛/嵌入簇路径在这里直接红）
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var ball = Ball;
        int n = plateOfCell.Length;
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        foreach (int p in plateOfCell)
        {
            Assert.GreaterOrEqual(p, 0, "生长铺满全球，不应有无主格");
            counts[p] = counts.GetValueOrDefault(p) + 1;
        }
        Assert.AreEqual(Plates, counts.Count, "生长式分板应恰好 P 块（每板至少占种子格）");

        foreach (int p in counts.Keys)
        {
            var seen = new bool[n];
            var queue = new System.Collections.Generic.Queue<int>();
            int reached = 0;
            for (int cell = 0; cell < n && reached == 0; cell++)
            {
                if (plateOfCell[cell] != p) continue;
                queue.Enqueue(cell);
                seen[cell] = true;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    reached++;
                    foreach (int j in ball.CellNeighbors[i])
                        if (!seen[j] && plateOfCell[j] == p) { seen[j] = true; queue.Enqueue(j); }
                }
            }
            Assert.AreEqual(counts[p], reached, $"板 {p} 应单连通（最大分量 {reached}/{counts[p]}）");
        }
    }

    [Test]
    public void SplitIntoPlates_Growth_SizeVarianceEmerges()
    {
        // 默认生长率指数（rate = u^1.5，前沿加权 = 富者愈富）应长出"巨板 + 小板"的悬殊格局，
        // 而不是 Voronoi 式的匀称拼贴——这是换生长路线的动机回归判据
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        foreach (int p in plateOfCell) counts[p] = counts.GetValueOrDefault(p) + 1;
        var sizes = counts.Values.OrderBy(v => v).ToList();
        double median = sizes[sizes.Count / 2];
        double ratio = sizes[^1] / median;
        TestContext.Out.WriteLine($"[生长分板] 板大小 {sizes[0]}..{sizes[^1]}（中位 {median:F0}），max/median = {ratio:F2}");
        Assert.Greater(ratio, 1.8, $"最大板/中位板 = {ratio:F2}，大小悬殊格局未出现（生长率/前沿加权可能被改坏）");
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

    // ═══════════════════════════════════════════════════════════════
    // 编辑器旋钮取值域（入口 §4 分辨率表：三档闭集）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void HexResLevel_ClosedSet_MatchesEntryDocResTable()
    {
        // BallManager 的 [Export] 旋钮是**闭集枚举**（inspector 下拉，2026-09-10 用户拍板），
        // 不是裸 int：res ≥ 6（14.1M 格）在编辑器里根本不可表示。本断言把取值域钉到入口 §4
        // 分辨率表——任一侧加档/删档都会打到它，逼"改代码必同步文档"。
        // 只查 H3 常数公式（O(1)），不构造网格：res5 建一次 Ball 要几十秒，不适合进单测。
        CollectionAssert.AreEquivalent(new[] { 3, 4, 5 },
            Enum.GetValues<HexResLevel>().Cast<int>().ToArray(), "旋钮档位与入口 §4 分辨率表不符");
        Assert.AreEqual(41162L, H3.GetNumCells(3), "入口 §4：res3 格数");
        Assert.AreEqual(288122L, H3.GetNumCells(4), "入口 §4：res4 格数");
        Assert.AreEqual(2016842L, H3.GetNumCells(5), "入口 §4：res5 格数");
    }
}
