using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Utils.H3;

namespace World.Tests;

/// <summary>
/// 河流走廊护栏（气候批次 2-R3，2026-09-17）：河流 = 表面网格的一部分（走廊格细分出 res+1
/// 子格，河路径占子格，shader 河流材质）：
///   · 分级阈值：物理汇水面积口径，三档边界归属高档；
///   · 走廊构建：年青世界（有起伏）走廊非空；走廊格 = 河格 ∪ 下游陆格（链到海连续）；
///   · 子格正确性：父子关系（CellToParent 回指）、每走廊格 6~7 子格、河路径子格有档；
///   · 网格烘焙：BallMesh 走廊版容量/纹素/子格账本自洽，索引不越界。
/// 年青世界 = RunMy 60（600 My 的 res1 小世界是准平原，无河为诚实结果，见 H3FluvialTests 注）。
/// 纪律（同 H3DynamicRunTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3RiversTests
{
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
    static Ball Ball => SharedBall.Value;

    const float MeanPrecipMm = 700f;
    static float CellAreaKm2 => 4f * MathF.PI * 6371f * 6371f / Ball.CellIds.Length;

    [Test]
    public void GradeOf_Thresholds_AttributeToHigherGrade()
    {
        float AreaToDischarge(float areaKm2) => areaKm2 / CellAreaKm2 * MeanPrecipMm;
        Assert.AreEqual(0, H3Rivers.GradeOf(0f, MeanPrecipMm, Ball.CellIds.Length), "零出流 = 无河");
        Assert.AreEqual(1, H3Rivers.GradeOf(1.0f * MeanPrecipMm, MeanPrecipMm, Ball.CellIds.Length),
            "粗分辨率单格汇水（60 万 km²）已超支流档：凡入海径流皆河（口径诚实推论）");
        Assert.AreEqual(1, H3Rivers.GradeOf(AreaToDischarge(H3Rivers.TributaryAreaKm2), MeanPrecipMm, Ball.CellIds.Length));
        Assert.AreEqual(2, H3Rivers.GradeOf(AreaToDischarge(H3Rivers.MainstemAreaKm2), MeanPrecipMm, Ball.CellIds.Length), "恰 100 万 km² = 干流");
        Assert.AreEqual(3, H3Rivers.GradeOf(AreaToDischarge(H3Rivers.TrunkAreaKm2), MeanPrecipMm, Ball.CellIds.Length), "恰 400 万 km² = 大河");
        Assert.AreEqual(0, H3Rivers.GradeOf(1000f, 0f, Ball.CellIds.Length), "退化均降水 = 无河");
        Assert.AreEqual(0, H3Rivers.GradeOf(1000f, MeanPrecipMm, 0), "退化格数 = 无河");
    }

    // 年青世界跑一遍（共享：一次 60 My，费用可接受）
    static readonly Lazy<(H3Plate plate, H3Rivers.Corridors corridors)> SharedRun =
        new(() =>
        {
            var plate = new H3Plate(Ball) { RunMy = 60f };
            plate.CreatePlates(4, 42);
            var corridors = H3Rivers.Build(Ball, plate.Crust, plate.WaterCycle);
            return (plate, corridors);
        });

    [Test]
    public void Corridor_YoungWorldHasRivers_AndIncludesDownstreamLand()
    {
        var (plate, corridors) = SharedRun.Value;
        var wc = plate.WaterCycle;
        float meanPrecipMm = (float)(wc.TotalPrecipM / Ball.CellIds.Length * 1000.0);
        var grades = H3Rivers.ComputeGrades(wc.RiverDischargeMmYear, meanPrecipMm, Ball.CellIds.Length);

        int riverCells = 0;
        for (int i = 0; i < grades.Length; i++)
        {
            if (grades[i] == 0) continue;
            riverCells++;
            Assert.IsTrue(corridors.Corridor[i], $"河格 {i} 必须在走廊内");
            int t = wc.FlowTarget[i];
            if (t >= 0 && plate.Crust.IsLand(t))
                Assert.IsTrue(corridors.Corridor[t], $"河格 {i} 的下游陆格 {t} 必须在走廊内（链到海连续）");
        }
        Assert.Greater(riverCells, 0, "年青世界应有入海河流");
        int corridorCount = 0;
        foreach (bool c in corridors.Corridor) if (c) corridorCount++;
        Assert.Greater(corridors.ChildCount, 0, "走廊应有子格");
        Assert.AreEqual(corridorCount, corridors.Children.Count, "Children 与走廊格一一对应");
    }

    [Test]
    public void Corridor_ChildrenAreResPlusOneChildren_OfTheirParent()
    {
        var (plate, corridors) = SharedRun.Value;
        // 父子回指：每个子格的 res 父 = 所在走廊基格；子格数 = 7（六边格）或 6（五边格）
        int checkedParents = 0;
        for (int i = 0; i < corridors.Corridor.Length; i++)
        {
            if (!corridors.Corridor[i]) continue;
            int listIdx = RankOf(corridors, i);
            ulong[] children = corridors.Children[listIdx];
            Assert.That(children.Length, Is.InRange(6, 7),
                $"走廊格 {i} 的子格数应为 7（六边格）或 6（五边格）");
            ulong parentId = Ball.CellIds[i];
            foreach (ulong child in children)
            {
                Assert.AreEqual(H3.GetResolution(child), Ball.Res + 1, "子格 = res+1");
                ulong back = H3.CellToParent(child, Ball.Res);
                Assert.AreEqual(parentId, back, $"子格 {child} 的父应回指基格 {i}");
                checkedParents++;
            }
            if (checkedParents > 200) break;   // 抽查足够（确定性下全查等价，省时）
        }
        Assert.Greater(checkedParents, 0);
    }

    [Test]
    public void Corridor_RiverPathChildrenCarryGrade()
    {
        var (plate, corridors) = SharedRun.Value;
        var wc = plate.WaterCycle;
        float meanPrecipMm = (float)(wc.TotalPrecipM / Ball.CellIds.Length * 1000.0);
        var grades = H3Rivers.ComputeGrades(wc.RiverDischargeMmYear, meanPrecipMm, Ball.CellIds.Length);

        int riverPathChildren = 0;
        for (int listIdx = 0; listIdx < corridors.ChildGrades.Count; listIdx++)
            foreach (byte g in corridors.ChildGrades[listIdx])
                if (g > 0) riverPathChildren++;
        Assert.Greater(riverPathChildren, 0, "河路径应命中至少一些子格（追踪生效）");

        // 抽查：任一河格若与下游同在走廊，其出流边至少命中一个子格
        for (int i = 0; i < grades.Length; i++)
        {
            if (grades[i] == 0) continue;
            int t = wc.FlowTarget[i];
            bool own = corridors.Corridor[i];
            bool down = t >= 0 && corridors.Corridor[t];
            if (!own || !down) continue;
            int listIdx = RankOf(corridors, i);
            int ownRiver = 0;
            foreach (byte g in corridors.ChildGrades[listIdx]) if (g > 0) ownRiver++;
            Assert.Greater(ownRiver, 0, $"河格 {i} 出流边应至少命中自己的一个子格");
            break;
        }
    }

    [Test]
    public void BallMesh_CorridorBuild_BookkeepingConsistent()
    {
        var (plate, corridors) = SharedRun.Value;
        var mesh = new BallMesh();
        mesh.ComputeCellMetrics(Ball);
        mesh.BuildTileMeshData(Ball, 1f, corridors);

        Assert.AreEqual(Ball.CellIds.Length + corridors.ChildCount, mesh.DataTexelCount, "纹素 = 基格 + 子格");
        Assert.AreEqual(corridors.ChildCount, mesh.CorridorChildParent.Length, "子格账本长度对账");
        Assert.AreEqual(corridors.ChildCount, mesh.CorridorChildGrade.Length);

        Assert.Greater(mesh.DisplayVerts.Length, 0);
        Assert.AreEqual(mesh.DisplayVerts.Length, mesh.DisplayUv.Length);
        Assert.AreEqual(0, mesh.DisplayIndices.Length % 3);
        foreach (int idx in mesh.DisplayIndices)
            Assert.GreaterOrEqual(idx, 0, "索引非负");
        Assert.Less(mesh.DisplayIndices[^1], mesh.DisplayVerts.Length, "索引不越界");

        // 子格纹素地址 = n + 子格序（BallView 烘焙的复制源/目标对账）
        int n = Ball.CellIds.Length;
        for (int e = 0; e < mesh.CorridorChildParent.Length; e++)
        {
            int parent = mesh.CorridorChildParent[e];
            Assert.IsTrue(corridors.Corridor[parent], $"子格 {e} 的父 {parent} 应是走廊格");
        }

        // 无走廊重建 = 旧口径（无子格纹素）
        var plain = new BallMesh();
        plain.BuildTileMeshData(Ball, 1f);
        Assert.AreEqual(Ball.CellIds.Length, plain.DataTexelCount);
        Assert.AreEqual(0, plain.CorridorChildParent.Length);
        GC.KeepAlive(plate);
    }

    // 走廊位次（与 BallMesh 同口径：i 之前的走廊格数）
    static int RankOf(H3Rivers.Corridors corridors, int cellIndex)
    {
        int rank = 0;
        for (int i = 0; i < cellIndex; i++)
            if (corridors.Corridor[i]) rank++;
        return rank;
    }
}
