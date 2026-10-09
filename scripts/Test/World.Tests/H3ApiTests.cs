using System;
using System.Linq;
using NUnit.Framework;
using World.Utils.H3;

namespace World.Tests;

/// <summary>
/// H3 托管门面（scripts/Utils/H3/H3.cs ↔ 原生 native/h3/h3.dll，Uber h3 v4.5.0）对接正确性测试。
/// 断言来源两类：
///   1. H3 官方文档的示例向量：latLngToCell(37.775938728915946, -122.41795063018799, 9)
///      == 0x8928308280fffff（文档/各语言绑定 quickstart 同一向量）；
///   2. H3 数学结构不变量：格子总数公式 2+120·7^r、五边形恒 12 个、
///      k-ring 计数 1+3k(k+1)、父子互逆、压缩/解压互逆、邻居/距离/路径一致性。
///
/// 纪律：只用 [Test]/[TestCase(字面量)]；不写文件；不触碰 GD.*/LogService；
/// DLL 由 world.csproj 的 None+CopyToOutputDirectory 拷到测试输出目录，H3Native 解析器自动定位。
/// </summary>
public class H3ApiTests
{
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>测试用非五边形 origin：赤道 0° 点，res 5（先断言非五边形再使用）。</summary>
    private static ulong OriginRes5()
    {
        ulong cell = H3.LatLngToCell(new LatLng(0, 0), 5);
        Assert.IsFalse(H3.IsPentagon(cell), "赤道 0° 点不应是五边形");
        return cell;
    }

    // ═══════════════════════════════════════════════════════════════
    // 官方示例向量（跨语言 bindings 共用同一断言，最硬的"接对了"证据）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void LatLngToCell_OfficialExample_旧金山res9()
    {
        // H3 文档经典示例：旧金山某点，分辨率 9
        ulong cell = H3.LatLngToCell(new LatLng(37.775938728915946 * DegToRad, -122.41795063018799 * DegToRad), 9);
        Assert.AreEqual(0x8928308280fffffUL, cell);
    }

    [Test]
    public void H3ToString_OfficialExample_MatchCanonical()
    {
        Assert.AreEqual("8928308280fffff", H3.H3ToString(0x8928308280fffffUL));
        Assert.AreEqual(0x8928308280fffffUL, H3.StringToH3("8928308280fffff"));
    }

    // ═══════════════════════════════════════════════════════════════
    // 基础往返 / 元信息
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void CellToLatLng_LatLngToCell_RoundTrip_中心映射回原格()
    {
        // 中心点 → 格子 → 中心：cellToLatLng(latLngToCell(p)) 必须精确落回同一格（H3 不变量）
        ulong cell = OriginRes5();
        LatLng center = H3.CellToLatLng(cell);
        Assert.AreEqual(cell, H3.LatLngToCell(center, 5));
    }

    [Test]
    public void GetNumCells_公式2加120乘7的r次方_AllRes0到10()
    {
        for (int r = 0; r <= 10; r++)
        {
            long expected = 2 + 120 * (long)Math.Pow(7, r);
            Assert.AreEqual(expected, H3.GetNumCells(r), $"res={r}");
        }
    }

    [Test]
    public void Res0Cells_122格_其中恰好12个五边形()
    {
        ulong[] res0 = H3.GetRes0Cells();
        Assert.AreEqual(122, res0.Length);
        Assert.AreEqual(12, res0.Count(H3.IsPentagon));
        Assert.AreEqual(12, H3.GetPentagons(0).Length);
    }

    [Test]
    public void GetPentagons_各分辨率恒12个()
    {
        foreach (int r in new[] { 0, 1, 2, 5 })
            Assert.AreEqual(12, H3.GetPentagons(r).Length, $"res={r}");
    }

    [Test]
    public void InvalidInput_参数越界抛异常()
    {
        // 分辨率越界（res=20 超出 0–15）：门面先校验，抛 ArgumentOutOfRangeException
        Assert.Throws<ArgumentOutOfRangeException>(() => H3.LatLngToCell(new LatLng(0, 0), 20));
        // 非法字符串：原生层返回错误码，门面转 InvalidOperationException
        Assert.Throws<InvalidOperationException>(() => H3.StringToH3("not-a-h3"));
    }

    // ═══════════════════════════════════════════════════════════════
    // 邻居 / 环 / 距离 / 路径
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void GridDisk_K0_仅自身()
    {
        ulong cell = OriginRes5();
        Assert.AreEqual(new[] { cell }, H3.GridDisk(cell, 0));
    }

    [Test]
    public void GridDisk_K1_7格_含自身_K2_19格()
    {
        ulong cell = OriginRes5();
        ulong[] disk1 = H3.GridDisk(cell, 1);
        ulong[] disk2 = H3.GridDisk(cell, 2);
        Assert.AreEqual(7, disk1.Length);
        Assert.AreEqual(19, disk2.Length);
        Assert.Contains(cell, disk1);
    }

    [Test]
    public void GridRingUnsafe_K1_6邻居_均距离1且相邻()
    {
        ulong cell = OriginRes5();
        ulong[] ring = H3.GridRingUnsafe(cell, 1);
        Assert.AreEqual(6, ring.Length);
        foreach (ulong nb in ring)
        {
            Assert.AreEqual(1, H3.GridDistance(cell, nb));
            Assert.IsTrue(H3.AreNeighborCells(cell, nb));
        }
        // 注意：不断言"对侧不相邻"——该格贴近 icosahedron 面边/五边形时，
        // 平面六边形晶格的几何假设不成立（对侧可能共边），此类断言只在远离
        // 畸变区的格子上成立，不宜作为库的不变量。
    }

    [Test]
    public void GridDistance_自身0_对称()
    {
        ulong cell = OriginRes5();
        ulong other = H3.GridDisk(cell, 2)[^1];
        Assert.AreEqual(0, H3.GridDistance(cell, cell));
        Assert.AreEqual(H3.GridDistance(cell, other), H3.GridDistance(other, cell));
    }

    [Test]
    public void GridPathCells_端点与长度一致()
    {
        ulong cell = OriginRes5();
        ulong far = H3.GridDisk(cell, 3)[^1]; // 距离 3 的格子
        ulong[] path = H3.GridPathCells(cell, far);
        Assert.AreEqual(H3.GridDistance(cell, far) + 1, path.Length); // 路径含两端点
        Assert.AreEqual(cell, path[0]);
        Assert.AreEqual(far, path[^1]);
    }

    // ═══════════════════════════════════════════════════════════════
    // 层级：父子 / 中心子格 / 压缩解压
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParentChild_互逆_子格数按7的幂()
    {
        ulong cell = OriginRes5();
        // 距离 1 级：7 个子格（1 中心 + 6 环绕）；距离 2 级：7² = 49
        ulong[] children1 = H3.CellToChildren(cell, 6);
        Assert.AreEqual(7, children1.Length);
        Assert.AreEqual(49, H3.CellToChildren(cell, 7).Length);
        foreach (ulong child in children1)
            Assert.AreEqual(cell, H3.CellToParent(child, 5));
        // 中心子格的父格也是原格
        Assert.AreEqual(cell, H3.CellToParent(H3.CellToCenterChild(cell, 7), 5));
        // 中心子格必在子格集合内
        Assert.Contains(H3.CellToCenterChild(cell, 6), children1);
    }

    [Test]
    public void CompactUncompact_整组子格_压缩回父格_解压还原()
    {
        // H3 关键性质：某格的完整 7 个子格集可压缩回单个父格；解压回原集合
        ulong cell = OriginRes5();
        ulong[] children = H3.CellToChildren(cell, 6);
        Assert.AreEqual(new[] { cell }, H3.CompactCells(children));
        CollectionAssert.AreEquivalent(children, H3.UncompactCells(new[] { cell }, 6));
    }

    // ═══════════════════════════════════════════════════════════════
    // 边界 / 顶点
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void CellToBoundary_顶点数在畸变范围内_五边形边界可穿越面边()
    {
        // 顶点数断言放宽：cellToBoundary 最坏情况 = 五边形 5 原始顶点 + 5 个
        // icosahedron 面边穿越点 = 10（h3api.h 注释明写 MAX_CELL_BNDRY_VERTS=10 的由来）；
        // 六边形同样可能因面边穿越而超过 6。真正的"几何形状"断言由
        // CellToVertexes_与CellToBoundary_位置一致 承担（顶点 id 恒为 6/5）。
        ulong hex = OriginRes5();
        Assert.That(H3.CellToBoundary(hex).Length, Is.InRange(6, 10));

        ulong pentagon = H3.GetPentagons(5)[0];
        Assert.That(H3.CellToBoundary(pentagon).Length, Is.InRange(5, 10));
        Assert.AreEqual(5, H3.CellToVertexes(pentagon).Length); // 顶点 id 恒为 5
        // 五边形 k=1 盘只有 1+5=6 格（安全版 gridDisk 自动处理五边形）
        Assert.AreEqual(6, H3.GridDisk(pentagon, 1).Length);
    }

    [Test]
    public void CellToVertexes_与CellToBoundary_位置一致()
    {
        ulong cell = OriginRes5();
        var boundary = H3.CellToBoundary(cell);
        var vertexes = H3.CellToVertexes(cell);
        Assert.AreEqual(6, vertexes.Length);

        var vertexPts = vertexes.Select(H3.VertexToLatLng).ToList();
        foreach (LatLng b in boundary)
        {
            bool exists = vertexPts.Any(v => Math.Abs(v.Lat - b.Lat) < 1e-9 && Math.Abs(v.Lng - b.Lng) < 1e-9);
            Assert.IsTrue(exists, $"边界顶点 ({b.Lat}, {b.Lng}) 应在顶点列表中存在");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 面积 / 边长（迁移时选分辨率的依据）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void CellArea_与平均面积_同数量级()
    {
        ulong cell = OriginRes5();
        double exact = H3.CellAreaKm2(cell);
        double avg = H3.GetHexagonAreaAvgKm2(5);
        // 精确面积与同分辨率六边形平均面积应在同一数量级（比值 0.5–2）
        double ratio = exact / avg;
        Assert.IsTrue(ratio is > 0.5 and < 2.0, $"ratio={ratio:F3} exact={exact:F3} avg={avg:F3}");
    }
}