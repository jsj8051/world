using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.HexPlanet;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// Tectonics 模块 L0 测试（纯托管，可在 dotnet test / 本地执行器下运行，无需 Godot 引擎）。
///
/// 覆盖：
///   FieldOps（标量/向量场、形态学、插值、统计）、MatrixOps（3×3 列主序矩阵）、
///   Crust（8 物质池、厚度/密度/浮力/均衡位移、ModelErosion/Weathering/Lithification/Metamorphosis）、
///   Plate（映射初始化、重采样、Move 零速度/小步长）、SphereGrid（n=2/3 小网格）、
///   Tectonophysics（纯函数子集）、TectonicsSimulation 模块（ctor / MergePlatesToMaster /
///   ApplySurfaceProcesses / SyncWorldToPlates / ComputeDisplacement / ApplyFlexure /
///   SolveSeaLevel / SolveSeaLevelByVolume / UpdateRifting / UpdateSubducted / ApplyAccretion）。
///
/// ⚠️ 不可测试路径（引擎依赖，无引擎进程会 0xC0000005 崩溃，全部避开）：
///   TectonicsSimulation.Run / RunWithProgress / GenerateInitialCrust / ResetPlates /
///   InitContinentalRiftMask / ComputeSubductionZones / InitializeOceanVolume /
///   SplitIntoPlates / TrySplitSupercontinent（成功分支含日志）/ MergeTwoPlates（私有，含日志）/
///   Tectonophysics.GuessPlateMap / SphereGrid.PrintDiagnostics —— 内部无条件调用
///   LogService.Log（GD.Print）或 new FastNoiseLite()。
///   InitializeOceanVolume 仅为日志 + 赋值公开字段 TotalOceanDepth → 测试直接设字段。
///   ApplySurfaceProcesses/UpdateRifting/UpdateSubducted 读写 Mineral*（ctor 后为 null）→
///   测试先初始化这些公开数组。
///
/// 纪律：只用 [Test]/[TestCase(字面量)]，无 SetUp/Theory/TestCaseSource，无 Pass/Ignore/Warn；
/// 确定性输入、小网格（n=2→42 顶点 / n=3→92）、不写文件、浮点断言带容差。
/// </summary>
public class TectonicsTests
{
    /// <summary>百万年（秒）——模拟内部年龄单位（Units.MEGAYEAR）。</summary>
    private const float My = Units.MEGAYEAR;

    // ═════════════════════════════════════════════════════════════════
    // 工具
    // ═════════════════════════════════════════════════════════════════

    /// <summary>n 细分网格（SphereGrid 构造；Subdivide 重构后为纯几何函数，无引擎安全）。</summary>
    private static SphereGrid Grid(int n) => new SphereGrid(n);

    /// <summary>模拟实例 + 矿藏数组初始化（ApplySurfaceProcesses/UpdateRifting/UpdateSubducted 需要）。</summary>
    private static TectonicsSimulation NewSim(int n)
    {
        var sim = new TectonicsSimulation(n);
        int count = sim.GlobalGrid.VertexCount;
        sim.MineralHydro = new float[count];
        sim.MineralSed = new float[count];
        sim.MineralMeta = new float[count];
        return sim;
    }

    /// <summary>空 crust + mask（给定格集合）的板块；映射初始恒等（各格一一对应）。</summary>
    private static Plate BuildPlate(int id, SphereGrid grid, params int[] cells)
    {
        var crust = new Crust(grid);
        var mask = new byte[grid.VertexCount];
        foreach (var c in cells) mask[c] = 1;
        return new Plate(id, grid, crust, mask);
    }

    /// <summary>全部守恒池（5 种 felsic 类）全球总量。</summary>
    private static double SumConserved5(Crust c)
    {
        double s = 0;
        var pools = c.ConservedPools();
        for (int p = 0; p < pools.Length; p++)
            for (int i = 0; i < pools[p].Length; i++) s += pools[p][i];
        return s;
    }

    private static bool AllFinite(Crust c)
    {
        foreach (var pool in c.AllPools())
            foreach (var v in pool)
                if (float.IsNaN(v) || float.IsInfinity(v)) return false;
        return true;
    }

    private static bool AllFinite(float[] a)
    {
        foreach (var v in a)
            if (float.IsNaN(v) || float.IsInfinity(v)) return false;
        return true;
    }

    private static void AssertFinite(float[] a)
    {
        foreach (var v in a)
            Assert.IsFalse(float.IsNaN(v) || float.IsInfinity(v), "数组含 NaN/Inf");
    }

    private static void AssertVecClose(Vector3 expected, Vector3 actual, float tol)
    {
        Assert.AreEqual(expected.X, actual.X, tol, "X");
        Assert.AreEqual(expected.Y, actual.Y, tol, "Y");
        Assert.AreEqual(expected.Z, actual.Z, tol, "Z");
    }

    private static void AssertArrayClose(float[] a, float[] b, float tol)
    {
        Assert.AreEqual(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            Assert.AreEqual(a[i], b[i], tol, $"index {i}");
    }

    private static void AssertArrayExact(float[] a, float[] b)
    {
        Assert.AreEqual(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                Assert.Fail($"index {i}: {a[i]} != {b[i]}");
    }

    private static float[] Transpose(float[] m) => new[]
        { m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8] };

    /// <summary>M×Mᵀ 与单位阵的最大偏差（列主序）。</summary>
    private static float MaxOrthoError(float[] m)
    {
        var p = MatrixOps.MultMatrix(m, Transpose(m));
        var ident = MatrixOps.Identity();
        float err = 0;
        for (int i = 0; i < 9; i++) err = Math.Max(err, Math.Abs(p[i] - ident[i]));
        return err;
    }

    // ═════════════════════════════════════════════════════════════════
    // 1. FieldOps —— 标量场基础运算
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void FieldOps_AddScalar_AddsConstant()
    {
        var r = new float[3];
        FieldOps.AddScalar(new[] { 1f, 2f, 3f }, 0.5f, r);
        AssertArrayClose(new[] { 1.5f, 2.5f, 3.5f }, r, 1e-6f);
    }

    [Test]
    public void FieldOps_SubScalar_SubtractsConstant()
    {
        var r = new float[3];
        FieldOps.SubScalar(new[] { 1f, 2f, 3f }, 0.5f, r);
        AssertArrayClose(new[] { 0.5f, 1.5f, 2.5f }, r, 1e-6f);
    }

    [Test]
    public void FieldOps_MultScalar_MultipliesConstant()
    {
        var r = new float[3];
        FieldOps.MultScalar(new[] { 1f, 2f, 3f }, 2f, r);
        AssertArrayClose(new[] { 2f, 4f, 6f }, r, 1e-6f);
    }

    [Test]
    public void FieldOps_AddField_Elementwise()
    {
        var r = new float[3];
        FieldOps.AddField(new[] { 1f, 2f, 3f }, new[] { 10f, 20f, 30f }, r);
        AssertArrayClose(new[] { 11f, 22f, 33f }, r, 1e-6f);
    }

    [Test]
    public void FieldOps_MultField_Elementwise()
    {
        var r = new float[3];
        FieldOps.MultField(new[] { 1f, 2f, 3f }, new[] { 2f, 3f, 4f }, r);
        AssertArrayClose(new[] { 2f, 6f, 12f }, r, 1e-6f);
    }

    [Test]
    public void FieldOps_MaxScalar_ClampsLowValues()
    {
        var r = new float[3];
        FieldOps.MaxScalar(new[] { -5f, 0f, 3f }, 1f, r);
        AssertArrayClose(new[] { 1f, 1f, 3f }, r, 1e-6f);
    }

    [Test]
    public void FieldOps_MinScalar_ClampsHighValues()
    {
        var r = new float[3];
        FieldOps.MinScalar(new[] { -5f, 0f, 3f }, 0f, r);
        AssertArrayClose(new[] { -5f, 0f, 0f }, r, 1e-6f);
    }

    [Test]
    public void FieldOps_Clamp_RestrictsRange()
    {
        var r = new float[3];
        FieldOps.Clamp(new[] { -10f, 0.5f, 10f }, -1f, 1f, r);
        AssertArrayClose(new[] { -1f, 0.5f, 1f }, r, 1e-6f);
    }

    [Test]
    public void FieldOps_GtScalar_StrictThreshold()
    {
        var r = FieldOps.GtScalar(new[] { -1f, 0f, 1f }, 0f);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 1 }, r);
    }

    [Test]
    public void FieldOps_EqScalar_FloatTolerance()
    {
        // 源码：|f - s| < 1e-6 → 1，否则 0
        var r = FieldOps.EqScalar(new[] { 1f, 1f + 5e-7f, 1f + 1e-5f }, 1f);
        CollectionAssert.AreEqual(new byte[] { 1, 1, 0 }, r);
    }

    [Test]
    public void FieldOps_EqScalar_ByteExact()
    {
        CollectionAssert.AreEqual(new byte[] { 1, 0, 1 },
            FieldOps.EqScalar(new byte[] { 3, 4, 3 }, (byte)3));
    }

    [Test]
    public void FieldOps_NeScalar_ByteExact()
    {
        CollectionAssert.AreEqual(new byte[] { 0, 1, 0 },
            FieldOps.NeScalar(new byte[] { 3, 4, 3 }, (byte)3));
    }

    // ═════════════════════════════════════════════════════════════════
    // 2. FieldOps —— 形态学（小网格 n=2）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void FieldOps_Erode_AllOnes_StaysAllOnes()
    {
        var g = Grid(2);
        var mask = Enumerable.Repeat((byte)1, g.VertexCount).ToArray();
        var eroded = FieldOps.Erode(g, mask, 2);
        CollectionAssert.AreEqual(mask, eroded);
    }

    [Test]
    public void FieldOps_Erode_IsolatedCell_Removed()
    {
        var g = Grid(2);
        var mask = new byte[g.VertexCount];
        mask[0] = 1;   // 无任何邻居在 mask 内 → 腐蚀 1 层即消失
        var eroded = FieldOps.Erode(g, mask, 1);
        Assert.AreEqual(0, eroded.Sum(b => b));
    }

    [Test]
    public void FieldOps_Erode_MonotoneNestedUnderMask()
    {
        var g = Grid(2);
        var mask = new byte[g.VertexCount];
        for (int i = 0; i < g.VertexCount; i++) if (i % 3 == 0) mask[i] = 1;
        var e1 = FieldOps.Erode(g, mask, 1);
        var e2 = FieldOps.Erode(g, mask, 2);
        for (int i = 0; i < g.VertexCount; i++)
        {
            Assert.LessOrEqual(e1[i], mask[i], $"Erode(k=1) 不得增加格 {i}");
            Assert.LessOrEqual(e2[i], e1[i], $"Erode(k=2) ⊆ Erode(k=1) 于格 {i}");
        }
    }

    [Test]
    public void FieldOps_Dilate_SingleCell_ExpandsToItsNeighbors()
    {
        var g = Grid(2);
        var mask = new byte[g.VertexCount];
        mask[0] = 1;
        var dilated = FieldOps.Dilate(g, mask, 1);
        int deg = g.Neighbors[0].Length;
        Assert.AreEqual(1 + deg, dilated.Sum(b => b), "膨胀 = 自身 + 全部邻居");
        Assert.AreEqual(1, dilated[0]);
        foreach (int nb in g.Neighbors[0]) Assert.AreEqual(1, dilated[nb], $"邻居 {nb}");
    }

    [Test]
    public void FieldOps_Dilate_AllZeros_StaysZero()
    {
        var g = Grid(2);
        var mask = new byte[g.VertexCount];
        var dilated = FieldOps.Dilate(g, mask, 3);
        Assert.AreEqual(0, dilated.Sum(b => b));
    }

    [Test]
    public void FieldOps_Margin_SingleCell_GivesNeighborsOnly()
    {
        var g = Grid(2);
        var mask = new byte[g.VertexCount];
        mask[0] = 1;
        var margin = FieldOps.Margin(g, mask, 1);
        int deg = g.Neighbors[0].Length;
        Assert.AreEqual(deg, margin.Sum(b => b), "margin = 边界外一层（不含自身）");
        Assert.AreEqual(0, margin[0], "原 mask 格不进 margin");
        for (int i = 0; i < g.VertexCount; i++)
            if (margin[i] == 1)
                Assert.IsTrue(Array.IndexOf(g.Neighbors[0], i) >= 0, $"margin 格 {i} 必须是邻居");
    }

    [Test]
    public void FieldOps_Margin_AllOnes_Empty()
    {
        var g = Grid(2);
        var mask = Enumerable.Repeat((byte)1, g.VertexCount).ToArray();
        Assert.AreEqual(0, FieldOps.Margin(g, mask, 2).Sum(b => b), "全域 mask 无外部边界层");
    }

    // ═════════════════════════════════════════════════════════════════
    // 3. FieldOps —— 插值 / 向量场 / 扩散 / 统计
    // ═════════════════════════════════════════════════════════════════

    [TestCase(0f, 100f)]
    [TestCase(5f, 150f)]
    [TestCase(10f, 200f)]
    [TestCase(20f, 300f)]
    [TestCase(-3f, 100f)]   // 低于 breaks[0] → values[0]
    [TestCase(99f, 300f)]   // 高于 breaks[^1] → values[^1]
    public void FieldOps_Lerp_PiecewiseLinear(float x, float expected)
    {
        float[] breaks = { 0f, 10f, 20f };
        float[] values = { 100f, 200f, 300f };
        Assert.AreEqual(expected, FieldOps.Lerp(breaks, values, x), 1e-4f);
    }

    [TestCase(2f, 4f, 2f, 0f)]
    [TestCase(2f, 4f, 3f, 0.5f)]
    [TestCase(2f, 4f, 4f, 1f)]
    [TestCase(2f, 4f, 1f, 0f)]
    [TestCase(2f, 4f, 9f, 1f)]
    public void FieldOps_Linearstep_SegmentAndClamp(float a, float b, float x, float expected)
    {
        Assert.AreEqual(expected, FieldOps.Linearstep(a, b, x), 1e-6f);
    }

    [Test]
    public void FieldOps_CrossField_KnownAndParallel()
    {
        var a = new[] { new Vector3(1, 0, 0), new Vector3(1, 0, 0) };
        var b = new[] { new Vector3(0, 1, 0), new Vector3(2, 0, 0) };
        var r = new Vector3[2];
        FieldOps.CrossField(a, b, r);
        AssertVecClose(new Vector3(0, 0, 1), r[0], 1e-6f);
        AssertVecClose(Vector3.Zero, r[1], 1e-6f);   // 平行 → 0
    }

    [Test]
    public void FieldOps_CrossVectorField_Known()
    {
        // v×r：v=(0,0,1), r=(1,0,0) → (0,1,0)
        var v = new[] { new Vector3(0, 0, 1) };
        var r = new[] { new Vector3(1, 0, 0) };
        var outArr = new Vector3[1];
        FieldOps.CrossVectorField(v, r, outArr);
        AssertVecClose(new Vector3(0, 1, 0), outArr[0], 1e-6f);
    }

    [Test]
    public void FieldOps_Normalize_UnitLength()
    {
        var v = new[] { new Vector3(3, 4, 0) };
        var r = new Vector3[1];
        FieldOps.Normalize(v, r);
        AssertVecClose(new Vector3(0.6f, 0.8f, 0f), r[0], 1e-5f);
        Assert.AreEqual(1f, r[0].Length(), 1e-5f);
    }

    [Test]
    public void FieldOps_DotField_Elementwise()
    {
        var a = new[] { new Vector3(1, 0, 0) };
        var b = new[] { new Vector3(2, 3, 4) };
        var r = new float[1];
        FieldOps.DotField(a, b, r);
        Assert.AreEqual(2f, r[0], 1e-6f);
    }

    [Test]
    public void FieldOps_Gradient_ConstantField_Zero()
    {
        var g = Grid(2);
        var f = Enumerable.Repeat(5f, g.VertexCount).ToArray();
        var grad = new Vector3[g.VertexCount];
        FieldOps.Gradient(g, f, grad);
        for (int i = 0; i < g.VertexCount; i++)
            AssertVecClose(Vector3.Zero, grad[i], 1e-6f);
    }

    [Test]
    public void FieldOps_Gradient_YField_NonZeroSomewhere()
    {
        // 高度场 = Y 坐标：球面上必存在非零梯度格
        var g = Grid(2);
        var f = new float[g.VertexCount];
        for (int i = 0; i < g.VertexCount; i++) f[i] = g.Vertices[i].Y;
        var grad = new Vector3[g.VertexCount];
        FieldOps.Gradient(g, f, grad);
        Assert.IsTrue(grad.Any(v => v.Length() > 1e-4f), "Y 场梯度不应全零");
    }

    [Test]
    public void FieldOps_Diffuse_ConstantField_Unchanged()
    {
        var g = Grid(2);
        var f = Enumerable.Repeat(7f, g.VertexCount).ToArray();
        var r = FieldOps.Diffuse(g, f, 0.5f, 4);
        AssertArrayClose(f, r, 1e-6f);
    }

    [Test]
    public void FieldOps_Diffuse_SingleSpike_ShrinksAndSpreads()
    {
        var g = Grid(2);
        var f = new float[g.VertexCount];
        f[0] = 1f;
        var r = FieldOps.Diffuse(g, f, 0.5f, 1);
        // new[0] = 1 + 0.5×(邻居均值0 - 1) = 0.5；邻居获得正值
        Assert.AreEqual(0.5f, r[0], 1e-6f);
        foreach (int nb in g.Neighbors[0])
            Assert.Greater(r[nb], 0f, $"邻居 {nb} 应获得扩散量");
        // 峰值减弱（高度被抹平）
        Assert.Less(r[0], f[0]);
    }

    [Test]
    public void FieldOps_Stats_MinMaxAverageSum()
    {
        var f = new[] { 1f, 2f, 3f, 4f, -5f };
        Assert.AreEqual(-5f, FieldOps.Min(f), 1e-6f);
        Assert.AreEqual(4f, FieldOps.Max(f), 1e-6f);
        Assert.AreEqual(1f, FieldOps.Average(f), 1e-6f);
        Assert.AreEqual(5f, FieldOps.Sum(f), 1e-4f);
    }

    [Test]
    public void FieldOps_WeightedAverage_WeightedMeanAndZeroWeights()
    {
        var positions = new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0) };
        var weights = new[] { 1f, 3f };
        AssertVecClose(new Vector3(0.25f, 0.75f, 0f), FieldOps.WeightedAverage(positions, weights), 1e-6f);
        AssertVecClose(Vector3.Zero, FieldOps.WeightedAverage(positions, new[] { 0f, 0f }), 1e-6f);
    }

    // ═════════════════════════════════════════════════════════════════
    // 4. MatrixOps（3×3 列主序浮点矩阵）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void MatrixOps_Identity_Structure()
    {
        CollectionAssert.AreEqual(new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, MatrixOps.Identity());
    }

    [Test]
    public void MatrixOps_FromRotationVector_Zero_IsIdentity()
    {
        AssertArrayExact(MatrixOps.Identity(), MatrixOps.FromRotationVector(Vector3.Zero));
    }

    [Test]
    public void MatrixOps_FromRotationVector_Orthogonal()
    {
        var m = MatrixOps.FromRotationVector(new Vector3(0.3f, -0.2f, 0.5f));
        Assert.Less(MaxOrthoError(m), 1e-4f, "旋转矩阵必须正交 M·Mᵀ≈I");
    }

    [Test]
    public void MatrixOps_FromRotationVector_ZRotation_LiesInXYPlaneAndPreservesLength()
    {
        var m = MatrixOps.FromRotationVector(new Vector3(0, 0, (float)Math.PI / 2f));
        var v = MatrixOps.MultVector(m, new Vector3(1, 0, 0));
        Assert.Less(Math.Abs(v.X), 1e-4f, "应离开 x 轴");
        Assert.AreEqual(1f, v.Length(), 1e-4f, "旋转保持长度");
        Assert.AreEqual(0f, v.Z, 1e-6f, "绕 z 旋转留在 XY 平面");
    }

    [Test]
    public void MatrixOps_MultMatrix_IdentityIsNeutral()
    {
        var w = new Vector3(0.2f, 0.4f, -0.1f);
        var rot = MatrixOps.FromRotationVector(w);
        AssertArrayClose(rot, MatrixOps.MultMatrix(MatrixOps.Identity(), rot), 1e-6f);
        AssertArrayClose(rot, MatrixOps.MultMatrix(rot, MatrixOps.Identity()), 1e-6f);
    }

    [Test]
    public void MatrixOps_MultVector_ColumnMajorOrder()
    {
        // 列主序 {1,2,3, 4,5,6, 7,8,9}：列 0 = (1,2,3) → M×(1,0,0) = (1,2,3)
        float[] m = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        AssertVecClose(new Vector3(1, 2, 3), MatrixOps.MultVector(m, new Vector3(1, 0, 0)), 1e-6f);
    }

    [Test]
    public void MatrixOps_MultVector_Linear()
    {
        var m = MatrixOps.FromRotationVector(new Vector3(0.1f, 0.2f, 0.3f));
        var u = new Vector3(1, 2, 3);
        var v = new Vector3(-2, 0.5f, 4);
        AssertVecClose(
            MatrixOps.MultVector(m, u) + MatrixOps.MultVector(m, v),
            MatrixOps.MultVector(m, u + v), 1e-4f);
        AssertVecClose(
            MatrixOps.MultVector(m, u) * 2f,
            MatrixOps.MultVector(m, u * 2f), 1e-4f);
    }

    [Test]
    public void MatrixOps_Invert_RoundTrip()
    {
        // 非对称可逆矩阵（剪切）：列主序
        float[] a = { 1, 0, 0, 0.5f, 1, 0, 0, 0, 1 };
        var inv = MatrixOps.Invert(a);
        var prod = MatrixOps.MultMatrix(inv, a);
        float err = 0;
        var ident = MatrixOps.Identity();
        for (int i = 0; i < 9; i++) err = Math.Max(err, Math.Abs(prod[i] - ident[i]));
        Assert.Less(err, 1e-5f, "A⁻¹×A≈I");
    }

    [Test]
    public void MatrixOps_Invert_OrthogonalMatrix_IsTranspose()
    {
        var m = MatrixOps.FromRotationVector(new Vector3(0.5f, -0.1f, 0.2f));
        AssertArrayClose(Transpose(m), MatrixOps.Invert(m), 1e-4f);
    }

    // ═════════════════════════════════════════════════════════════════
    // 5. Crust（8 物质池模型）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Crust_Ctor_AllPoolsZeroAndSized()
    {
        var g = Grid(2);
        var c = new Crust(g);
        foreach (var pool in c.AllPools())
        {
            Assert.AreEqual(g.VertexCount, pool.Length);
            Assert.AreEqual(0f, pool.Sum(v => Math.Abs(v)), 1e-12f, "新 crust 全零");
        }
    }

    [Test]
    public void Crust_PoolAccessors_OrderAndReference()
    {
        var c = new Crust(Grid(2));
        var all = c.AllPools();
        Assert.AreEqual(8, all.Length);
        Assert.AreSame(c.Sediment, all[0]);
        Assert.AreSame(c.Sedimentary, all[1]);
        Assert.AreSame(c.Metamorphic, all[2]);
        Assert.AreSame(c.FelsicPlutonic, all[3]);
        Assert.AreSame(c.FelsicVolcanic, all[4]);
        Assert.AreSame(c.MaficVolcanic, all[5]);
        Assert.AreSame(c.MaficPlutonic, all[6]);
        Assert.AreSame(c.Age, all[7]);

        var mass = c.MassPools();
        Assert.AreEqual(7, mass.Length, "质量场不含 age");
        Assert.AreSame(c.MaficPlutonic, mass[6]);

        var conserved = c.ConservedPools();
        Assert.AreEqual(5, conserved.Length, "守恒组 = 5 种 felsic 类");
        Assert.AreSame(c.Sediment, conserved[0]);
        Assert.AreSame(c.FelsicVolcanic, conserved[4]);
    }

    [Test]
    public void Crust_Reset_ClearsAll()
    {
        var c = new Crust(Grid(2));
        foreach (var pool in c.AllPools())
            for (int i = 0; i < pool.Length; i++) pool[i] = 123f;
        c.Reset();
        foreach (var pool in c.AllPools())
            Assert.AreEqual(0f, pool.Sum(v => Math.Abs(v)), 1e-12f);
    }

    [Test]
    public void Crust_GetConservedMass_SumOfFivePools()
    {
        var g = Grid(2);
        var c = new Crust(g);
        c.Sediment[0] = 1f; c.Sedimentary[0] = 2f; c.Metamorphic[0] = 3f;
        c.FelsicPlutonic[0] = 4f; c.FelsicVolcanic[0] = 5f;
        c.MaficVolcanic[0] = 100f;   // 非守恒 → 不计入
        c.Age[0] = 999f;             // 非质量 → 不计入
        var mass = c.GetConservedMass();
        Assert.AreEqual(15f, mass[0], 1e-5f);
        Assert.AreEqual(0f, mass[1], 1e-5f);
    }

    [Test]
    public void Crust_GetTotalMass_SevenPools_ReuseEqualsAlloc()
    {
        var g = Grid(2);
        var c = new Crust(g);
        for (int i = 0; i < g.VertexCount; i++)
        {
            c.Sediment[i] = i + 1; c.Sedimentary[i] = i + 2; c.Metamorphic[i] = i + 3;
            c.FelsicPlutonic[i] = i + 4; c.FelsicVolcanic[i] = i + 5;
            c.MaficVolcanic[i] = i + 6; c.MaficPlutonic[i] = i + 7;
            c.Age[i] = 1000f;   // 不计质量
        }
        var alloc = c.GetTotalMass();
        var into = new float[g.VertexCount];
        Assert.AreSame(into, c.GetTotalMass(into));
        AssertArrayClose(alloc, into, 1e-4f);
        // 7 池之和 = 7i + 28
        Assert.AreEqual(7 * 3 + 28, alloc[3], 1e-4f);
    }

    [Test]
    public void Crust_GetThickness_MatchManualFormula()
    {
        var g = Grid(2);
        var c = new Crust(g);
        var m = new MaterialDensity();
        // age=0 → mafic 密度取最小值 2890
        c.MaficVolcanic[0] = 2890f * 10f;    // 10 m
        c.Sediment[0] = 1500f * 3f;          // 3 m
        c.FelsicPlutonic[0] = 2600f * 5f;    // 5 m
        var t = c.GetThickness(m);
        Assert.AreEqual(18f, t[0], 1e-4f);
        Assert.AreEqual(0f, t[1], 1e-6f);
        // 复用版与分配版一致
        var into = new float[g.VertexCount];
        Assert.AreSame(into, c.GetThickness(m, into));
        AssertArrayClose(t, into, 1e-6f);
    }

    [Test]
    public void Crust_GetThickness_MaficDensityAgesWithAge()
    {
        var g = Grid(2);
        var c = new Crust(g);
        var m = new MaterialDensity();
        c.MaficVolcanic[0] = 3300f * 10f;   // 老洋壳密度的质量
        c.Age[0] = 300f * My;              // ≥250My → frac=1 → 密度 3300
        Assert.AreEqual(10f, c.GetThickness(m)[0], 1e-3f, "老洋壳按 max 密度折算");
        c.Age[0] = 0f;
        float young = c.GetThickness(m)[0];
        Assert.Greater(young, 10f, "年轻洋壳按 min 密度折算 → 厚度更大");
    }

    [Test]
    public void Crust_GetDensity_MassOverThickness_WithDefault()
    {
        float[] mass = { 260f, 0f };
        float[] thickness = { 20f, 0f };
        var d = new Crust(Grid(2)).GetDensity(mass, thickness, 2890f);
        Assert.AreEqual(13f, d[0], 1e-5f);
        Assert.AreEqual(2890f, d[1], 1e-5f, "零厚度格用默认密度");
    }

    [Test]
    public void Crust_GetBuoyancy_NonPositive_Formula()
    {
        var m = new MaterialDensity();   // Mantle = 3075
        float[] density = { 1000f, 3075f, 4000f };
        var b = new Crust(Grid(2)).GetBuoyancy(density, m, 9.8f);
        Assert.AreEqual(0f, b[0], 1e-2f, "低于地幔 → 截断为 0");
        Assert.AreEqual(0f, b[1], 1e-2f, "等于地幔 → 0");
        Assert.AreEqual(-(4000f - 3075f) * 9.8f, b[2], 1e-2f, "高于地幔 → 负浮力");
        Assert.LessOrEqual(b[2], 0f);
    }

    [Test]
    public void Crust_GetIsostaticDisplacement_Formula()
    {
        var m = new MaterialDensity();
        float[] thickness = { 100f, 0f };
        float[] density = { 2000f, 0f };
        var d = new Crust(Grid(2)).GetIsostaticDisplacement(thickness, density, m);
        // disp = t - t×ρ/ρ_mantle = 100 - 100×2000/3075 ≈ 34.95935
        Assert.AreEqual(100f - 100f * 2000f / 3075f, d[0], 1e-3f);
        Assert.AreEqual(0f, d[1], 1e-6f);
        Assert.AreEqual(2, d.Length);
    }

    [Test]
    public void Crust_AddDelta_AccumulatesAllPools()
    {
        var g = Grid(2);
        var c = new Crust(g);
        var delta = new Crust(g);
        c.FelsicPlutonic[0] = 100f;
        c.Age[3] = 50f;
        delta.FelsicPlutonic[0] = 25f;
        delta.Age[3] = 10f;
        delta.Sediment[0] = 7f;
        Crust.AddDelta(c, delta);
        Assert.AreEqual(125f, c.FelsicPlutonic[0], 1e-5f);
        Assert.AreEqual(60f, c.Age[3], 1e-5f);
        Assert.AreEqual(7f, c.Sediment[0], 1e-5f);
        Assert.AreEqual(0f, c.FelsicPlutonic[1], 1e-5f, "未触碰格不变");
    }

    // ── Crust 地表过程（M3，纯静态）──

    [Test]
    public void Crust_ModelErosion_EqualHeights_ZeroDelta()
    {
        var g = Grid(2);
        var top = new Crust(g);
        top.FelsicPlutonic[0] = 100000f;
        var delta = new Crust(g);
        var h = Enumerable.Repeat(100f, g.VertexCount).ToArray();
        Crust.ModelErosion(g, h, 3f * My, new MaterialDensity(), top, delta);
        foreach (var pool in delta.AllPools())
            Assert.AreEqual(0f, pool.Sum(v => Math.Abs(v)), 1e-9f, "等高 → 无搬运");
    }

    [Test]
    public void Crust_ModelErosion_ConservesPerPoolMass()
    {
        var g = Grid(2);
        var top = new Crust(g);
        top.FelsicPlutonic[0] = 100000f;   // 孤峰格
        var delta = new Crust(g);
        var h = new float[g.VertexCount];
        h[0] = 1f;                          // 峰比邻居高 1m
        Crust.ModelErosion(g, h, 1f * My, new MaterialDensity(), top, delta);

        // 5 个守恒池各自全球和近似守恒（搬运只是换格，不消失）
        double sum = 0;
        foreach (var pool in delta.ConservedPools()) sum += pool.Sum(v => (double)v);
        Assert.AreEqual(0.0, sum, 0.05, "搬运总量守恒（Δ=0）");
        Assert.Less(delta.FelsicPlutonic[0], 0f, "发源格应流失");
        foreach (int nb in g.Neighbors[0])
            Assert.Greater(delta.FelsicPlutonic[nb], 0f, $"邻居 {nb} 应接收沉积");
        Assert.IsTrue(AllFinite(delta));
    }

    [Test]
    public void Crust_ModelWeathering_ConvertsRockToSedimentPerCell()
    {
        var g = Grid(2);
        var top = new Crust(g);
        top.Sediment[0] = 100f;            // 暴露度高
        top.Sedimentary[0] = 100000f;      // 可风化的岩石
        top.FelsicPlutonic[0] = 20000f;
        var delta = new Crust(g);
        var h = new float[g.VertexCount];
        h[0] = 100f;                        // 高低差 → avgDiff>0
        Crust.ModelWeathering(g, h, 1f * My, new MaterialDensity(), top, delta);

        float sed = delta.Sediment[0];
        float rock = delta.Sedimentary[0] + delta.Metamorphic[0]
                   + delta.FelsicPlutonic[0] + delta.FelsicVolcanic[0];
        if (sed > 0f)
        {
            // 每格守恒：风化把岩石转沉积物（同格内守恒组和为零）
            Assert.AreEqual(sed, -rock, 0.5f, "风化产物=岩石消耗（同格守恒）");
            Assert.Greater(delta.Sediment[0], 0f);
            Assert.LessOrEqual(delta.Sedimentary[0], 0f);
            Assert.LessOrEqual(delta.FelsicPlutonic[0], 0f);
        }
        // 无岩石格不风化
        var delta2 = new Crust(g);
        Crust.ModelWeathering(g, h, 1f * My, new MaterialDensity(), new Crust(g), delta2);
        Assert.AreEqual(0f, delta2.Sediment[0], 1e-6f, "无岩石 → 无风化");
        Assert.IsTrue(AllFinite(delta));
    }

    [Test]
    public void Crust_ModelLithification_SedimentToSedimentary_Threshold()
    {
        var g = Grid(2);
        var m = new MaterialDensity();
        var top = new Crust(g);
        top.Sediment[0] = 300000f;   // 300000×9.8 = 2.94e6 Pa > 2.2e6 → 部分成岩
        var delta = new Crust(g);
        var h = new float[g.VertexCount];
        Crust.ModelLithification(h, 1f * My, m, top, delta);

        // 每格：sediment 减少 == sedimentary 增加（同格守恒）
        Assert.Greater(delta.Sedimentary[0], 0f);
        Assert.AreEqual(delta.Sediment[0], -delta.Sedimentary[0], 0.5f);
        // 幅度 = (overpressure - 2.2e6)/g = (2.94e6 - 2.2e6)/9.8 ≈ 75510
        Assert.AreEqual((300000f * 9.8f - 2.2e6f) / 9.8f, delta.Sedimentary[0], 1f);

        // 阈值下：不动作
        top.Sediment[1] = 200000f;   // 1.96e6 < 2.2e6
        var d2 = new Crust(g);
        Crust.ModelLithification(h, 1f * My, m, top, d2);
        Assert.AreEqual(0f, d2.Sediment[1], 1e-3f);
        Assert.AreEqual(0f, d2.Sedimentary[1], 1e-3f);
    }

    [Test]
    public void Crust_ModelMetamorphosis_SedimentaryToMetamorphic_Threshold()
    {
        var g = Grid(2);
        var m = new MaterialDensity();
        var top = new Crust(g);
        top.Sedimentary[0] = 3.5e7f;   // 3.5e7×9.8 = 3.43e8 > 3e8 → 部分变质
        var delta = new Crust(g);
        var h = new float[g.VertexCount];
        Crust.ModelMetamorphosis(h, 1f * My, m, top, delta);

        Assert.Greater(delta.Metamorphic[0], 0f);
        Assert.AreEqual(delta.Metamorphic[0], -delta.Sedimentary[0], 1f);
        // 幅度 = (3.43e8 - 3e8)/9.8 ≈ 4.3878e6
        Assert.AreEqual((3.5e7f * 9.8f - 300e6f) / 9.8f, delta.Metamorphic[0], 10f);

        // 阈值下不动作
        top.Sedimentary[1] = 3.0e7f;   // 2.94e8 < 3e8
        var d2 = new Crust(g);
        Crust.ModelMetamorphosis(h, 1f * My, m, top, d2);
        Assert.AreEqual(0f, d2.Sedimentary[1], 1e-3f);
        Assert.AreEqual(0f, d2.Metamorphic[1], 1e-3f);
    }

    // ═════════════════════════════════════════════════════════════════
    // 6. Plate
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Plate_Ctor_MappingsIdentityAndTileCount()
    {
        var g = Grid(2);
        var mask = new byte[g.VertexCount];
        mask[0] = 1; mask[1] = 1; mask[5] = 1;
        var p = new Plate(7, g, new Crust(g), mask);
        Assert.AreEqual(7, p.Id);
        Assert.AreEqual(3, p.TileCount);
        for (int i = 0; i < g.VertexCount; i++)
        {
            Assert.AreEqual(i, p.LocalIdsOfGlobalCells[i], $"LocalIds[{i}]");
            Assert.AreEqual(i, p.GlobalIdsOfLocalCells[i], $"GlobalIds[{i}]");
        }
        // mask 是克隆：改源数组不影响板块
        mask[0] = 0;
        Assert.AreEqual(1, p.Mask[0]);
        // 初始矩阵为单位阵
        AssertArrayExact(MatrixOps.Identity(), p.LocalToGlobal);
        AssertArrayExact(MatrixOps.Identity(), p.GlobalToLocal);
    }

    [Test]
    public void Plate_ResampleCrustToGlobal_IdentityMapping_CopiesAllPools()
    {
        var g = Grid(2);
        var crust = new Crust(g);
        for (int i = 0; i < g.VertexCount; i++)
        {
            crust.Sediment[i] = i + 0.5f;
            crust.Sedimentary[i] = i * 2f;
            crust.Metamorphic[i] = -i;
            crust.FelsicPlutonic[i] = i * 10f;
            crust.FelsicVolcanic[i] = i * 100f;
            crust.MaficVolcanic[i] = i * 1000f;
            crust.MaficPlutonic[i] = i * 10000f;
            crust.Age[i] = i * My;
        }
        var p = new Plate(0, g, crust, Enumerable.Repeat((byte)1, g.VertexCount).ToArray());
        var target = new Crust(g);
        p.ResampleCrustToGlobal(target);
        var a = crust.AllPools();
        var b = target.AllPools();
        for (int pool = 0; pool < 8; pool++)
            AssertArrayExact(a[pool], b[pool]);
    }

    [Test]
    public void Plate_Move_ZeroVelocity_KeepsIdentityAndMappings()
    {
        var g = Grid(2);
        var mask = new byte[g.VertexCount];
        for (int i = 0; i < 21; i++) mask[i] = 1;
        var p = new Plate(0, g, new Crust(g), mask);   // 全零 crust → 浮力 0 → 无运动
        p.Move(1f, g, new MaterialDensity(), 9.8f);
        AssertArrayClose(MatrixOps.Identity(), p.LocalToGlobal, 1e-6f);
        AssertArrayClose(MatrixOps.Identity(), p.GlobalToLocal, 1e-6f);
        for (int i = 0; i < g.VertexCount; i++)
        {
            Assert.AreEqual(i, p.LocalIdsOfGlobalCells[i], $"LocalIds[{i}]");
            Assert.AreEqual(i, p.GlobalIdsOfLocalCells[i], $"GlobalIds[{i}]");
        }
    }

    [Test]
    public void Plate_Move_SmallStep_FiniteAndMappingsValid()
    {
        var g = Grid(2);
        var crust = new Crust(g);
        var mask = new byte[g.VertexCount];
        for (int i = 0; i < 21; i++)
        {
            mask[i] = 1;
            crust.MaficVolcanic[i] = 3300f * 100f;   // 密度 3300 > 地幔 3075 → 负浮力
            crust.Age[i] = 300f * My;                 // 老洋壳密度到上限
        }
        var p = new Plate(0, g, crust, mask);
        p.Move(1f, g, new MaterialDensity(), 9.8f, true);

        Assert.IsTrue(AllFinite(p.LocalToGlobal), "旋转矩阵有限");
        Assert.IsTrue(AllFinite(p.GlobalToLocal), "逆矩阵有限");
        Assert.Less(MaxOrthoError(p.LocalToGlobal), 1e-4f, "旋转矩阵保持正交");
        AssertArrayClose(MatrixOps.Invert(p.LocalToGlobal), p.GlobalToLocal, 1e-4f);
        for (int i = 0; i < g.VertexCount; i++)
        {
            Assert.That(p.LocalIdsOfGlobalCells[i], Is.InRange(0, g.VertexCount - 1),
                $"LocalIds[{i}] 越界");
            Assert.That(p.GlobalIdsOfLocalCells[i], Is.InRange(0, g.VertexCount - 1),
                $"GlobalIds[{i}] 越界");
        }
        for (int i = 0; i < p.BuoyancyVec.Length; i++)
        {
            var bv = p.BuoyancyVec[i];
            Assert.IsFalse(float.IsNaN(bv.X) || float.IsInfinity(bv.X) ||
                           float.IsNaN(bv.Y) || float.IsInfinity(bv.Y) ||
                           float.IsNaN(bv.Z) || float.IsInfinity(bv.Z),
                $"浮力场 {i} 有限");
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // 7. SphereGrid
    // ═════════════════════════════════════════════════════════════════

    [TestCase(2, 42)]
    [TestCase(3, 92)]
    public void SphereGrid_VertexCount_MatchesIcosahedronFormula(int n, int expected)
    {
        var g = new SphereGrid(n);
        Assert.AreEqual(expected, g.VertexCount);
        Assert.AreEqual(Icosahedron.VertexCountFor(n), g.VertexCount);
        Assert.AreEqual(20 * n * n, g.Faces.Count, "面数 = 20n²");
    }

    [Test]
    public void SphereGrid_Vertices_UnitLength()
    {
        var g = Grid(2);
        for (int i = 0; i < g.VertexCount; i++)
            Assert.AreEqual(1f, g.Vertices[i].Length(), 1e-4f, $"顶点 {i}");
    }

    [Test]
    public void SphereGrid_Neighbors_SymmetricNoSelfLoop()
    {
        var g = Grid(2);
        for (int i = 0; i < g.VertexCount; i++)
        {
            Assert.GreaterOrEqual(g.Neighbors[i].Length, 3, $"顶点 {i} 度数");
            foreach (int nb in g.Neighbors[i])
            {
                Assert.AreNotEqual(i, nb, $"顶点 {i} 自环");
                Assert.IsTrue(Array.IndexOf(g.Neighbors[nb], i) >= 0,
                    $"邻接不对称：{i}→{nb} 但 {nb}↛{i}");
            }
        }
    }

    [Test]
    public void SphereGrid_NearestId_OwnVertex_ReturnsSelf()
    {
        var g = Grid(2);
        for (int i = 0; i < g.VertexCount; i++)
            Assert.AreEqual(i, g.NearestId(g.Vertices[i]), $"顶点 {i} 的最近邻应是自身");
    }

    [Test]
    public void SphereGrid_NearestId_ArbitraryPoint_ValidId()
    {
        var g = Grid(2);
        var p = new Vector3(0.7f, 0.7f, 0.1f).Normalized();
        Assert.That(g.NearestId(p), Is.InRange(0, g.VertexCount - 1));
    }

    [Test]
    public void SphereGrid_NearestIds_MatchesPerElement()
    {
        var g = Grid(2);
        var pos = new Vector3[g.VertexCount];
        for (int i = 0; i < g.VertexCount; i++)
            pos[i] = g.Vertices[(i * 7) % g.VertexCount];
        var result = new int[g.VertexCount];
        g.NearestIds(pos, result);
        for (int i = 0; i < g.VertexCount; i++)
            Assert.AreEqual(g.NearestId(pos[i]), result[i]);
    }

    [Test]
    public void SphereGrid_NearestIdSeeded_CorrectSeed_ReturnsSelf()
    {
        var g = Grid(2);
        var scratch = new int[256];
        for (int i = 0; i < g.VertexCount; i++)
            Assert.AreEqual(i, g.NearestIdSeeded(g.Vertices[i], i, scratch),
                $"正确种子应直接返回自身 {i}");
    }

    [Test]
    public void SphereGrid_NearestIdSeeded_WrongSeed_StillValidOrFallback()
    {
        // n=2 网格仅 42 顶点 < MaxCandidates(64) → 永远不会 -1，返回合法 id
        var g = Grid(2);
        var scratch = new int[256];
        for (int i = 0; i < g.VertexCount; i++)
        {
            int seed = (i + 7) % g.VertexCount;
            int r = g.NearestIdSeeded(g.Vertices[i], seed, scratch);
            Assert.That(r, Is.InRange(0, g.VertexCount - 1), $"查询 {i} 种子 {seed}");
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // 8. Tectonophysics（纯函数子集；GuessPlateMap 含日志 → 跳过）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Tectonophysics_LateralSpeedPerForce_PositiveAndInverseViscosity()
    {
        float k1 = Tectonophysics.LateralSpeedPerForce(1.57e20f);
        float k2 = Tectonophysics.LateralSpeedPerForce(2f * 1.57e20f);
        Assert.Greater(k1, 0f);
        Assert.AreEqual(0.5f, k2 / k1, 1e-6f, "k ∝ 1/μ（粘度翻倍速度减半）");
    }

    [Test]
    public void Tectonophysics_GetPlateCenterOfMass_UniformMask_IsMeanPosition()
    {
        var g = Grid(2);
        var mask = new byte[g.VertexCount];
        mask[0] = 1; mask[1] = 1; mask[2] = 1;
        var mass = Enumerable.Repeat(1f, g.VertexCount).ToArray();
        var com = Tectonophysics.GetPlateCenterOfMass(g, mass, mask);
        var expect = (g.Vertices[0] + g.Vertices[1] + g.Vertices[2]) / 3f;
        AssertVecClose(expect, com, 1e-6f);
        // 零总质量 → Zero
        AssertVecClose(Vector3.Zero, Tectonophysics.GetPlateCenterOfMass(g, new float[g.VertexCount], mask), 1e-6f);
    }

    [Test]
    public void Tectonophysics_GetBoundaryNormal_InteriorZeroBoundaryUnit()
    {
        var g = Grid(2);
        var mask = new byte[g.VertexCount];
        for (int i = 0; i < 21; i++) mask[i] = 1;
        var normal = new Vector3[g.VertexCount];
        Tectonophysics.GetBoundaryNormal(g, mask, normal);
        // 源码语义：任何>0 的梯度都归一化到单位长；板内部（mask 且全邻居在 mask 内）梯度≈0。
        // 非零法线必须单位化；板内部应为 0。
        bool anyBoundary = false;
        for (int i = 0; i < g.VertexCount; i++)
        {
            bool interior = mask[i] == 1 && g.Neighbors[i].All(nb => mask[nb] == 1);
            float len = normal[i].Length();
            if (interior)
                Assert.AreEqual(0f, len, 1e-5f, $"板内部格 {i} 法线应为 0");
            else if (mask[i] == 1)
                anyBoundary = true;
            if (len > 1e-9f)
                Assert.AreEqual(1f, len, 1e-5f, $"非零法线 {i} 应归一化");
        }
        Assert.IsTrue(anyBoundary, "半格 mask 必须存在边界");
    }

    [Test]
    public void Tectonophysics_GuessPlateVelocity_ScalesLinearlyWithBuoyancy()
    {
        var g = Grid(2);
        var bn = new Vector3[g.VertexCount];
        for (int i = 0; i < g.VertexCount; i++) bn[i] = new Vector3(1, 0, 0);
        float k = Tectonophysics.LateralSpeedPerForce(1.57e20f);

        var b1 = new float[g.VertexCount];
        var b2 = new float[g.VertexCount];
        for (int i = 0; i < g.VertexCount; i++) { b1[i] = -1000f; b2[i] = -2000f; }
        var v1 = new Vector3[g.VertexCount];
        var v2 = new Vector3[g.VertexCount];
        Tectonophysics.GuessPlateVelocity(g, bn, b1, 1.57e20f, v1);
        Tectonophysics.GuessPlateVelocity(g, bn, b2, 1.57e20f, v2);

        for (int i = 0; i < g.VertexCount; i++)
        {
            // v = bN × (buoyancy × k)：方向沿 bN，大小随浮力线性
            AssertVecClose(bn[i] * (b1[i] * k), v1[i], Math.Abs(b1[i] * k) * 1e-4f + 1e-6f);
            AssertVecClose(v1[i] * 2f, v2[i], Math.Abs(v1[i].X) * 1e-4f + 1e-6f);
        }
    }

    [Test]
    public void Tectonophysics_GetPlateRotationMatrix3x3_NoPulledCells_Identity()
    {
        var g = Grid(2);
        var vel = new Vector3[g.VertexCount];   // 全零 → is_pulled 阈值以下
        var rot = Tectonophysics.GetPlateRotationMatrix3x3(g, vel, Vector3.Zero, 1f);
        AssertArrayExact(MatrixOps.Identity(), rot);
    }

    [Test]
    public void Tectonophysics_GetPlateRotationMatrix3x3_Pulled_Orthogonal()
    {
        var g = Grid(2);
        var vel = new Vector3[g.VertexCount];
        vel[0] = new Vector3(0.01f, 0, 0);
        var rot = Tectonophysics.GetPlateRotationMatrix3x3(g, vel, g.Vertices[0], 1f);
        Assert.IsTrue(AllFinite(rot));
        Assert.Less(MaxOrthoError(rot), 1e-4f, "旋转矩阵必须正交");
    }

    [Test]
    public void Tectonophysics_CrossToAngularVelocity_Formula()
    {
        var v = new[] { new Vector3(0, 0, 1) };
        var pos = new[] { new Vector3(1, 0, 0) };
        var r = new Vector3[1];
        Tectonophysics.CrossToAngularVelocity(v, pos, r);
        AssertVecClose(new Vector3(0, 1, 0), r[0], 1e-6f);
    }

    // ═════════════════════════════════════════════════════════════════
    // 9. TectonicsSimulation —— 模块测试
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Sim_Ctor_InitializesFields()
    {
        var sim = new TectonicsSimulation(2);
        int n = sim.GlobalGrid.VertexCount;
        Assert.AreEqual(2, sim.GridN);
        Assert.AreEqual(42, n);
        Assert.IsNotNull(sim.WorldCrust);
        Assert.IsNotNull(sim.Accretion);
        foreach (var pool in sim.WorldCrust.AllPools()) Assert.AreEqual(n, pool.Length);
        Assert.AreEqual(n, sim.Displacement.Length);
        Assert.AreEqual(n, sim.Elevation.Length);
        Assert.AreEqual(n, sim.TopPlateMap.Length);
        Assert.AreEqual(n, sim.PlateCount.Length);
        Assert.AreEqual(0, sim.Plates.Count);
        Assert.AreEqual(0f, sim.SeaLevel, 1e-6f);
    }

    /// <summary>
    /// 手工双板（分区全覆盖，留下 2 格未覆盖）：
    ///   板 A（id 0）：格 0..split-1，FelsicPlutonic=260000（100m）、MaficVolcanic=144500（50m）、Age=10My
    ///   板 B（id 1）：格 split..nv-3，Sedimentary=520000（200m）、Age=5My
    ///   未覆盖：最后 2 格。
    /// </summary>
    private static TectonicsSimulation PartitionSim(out Plate a, out Plate b)
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int nv = g.VertexCount;
        int split = nv / 2;

        a = BuildPlate(0, g, Enumerable.Range(0, split).ToArray());
        for (int i = 0; i < nv; i++)
        {
            if (i < split)
            {
                a.Crust.FelsicPlutonic[i] = 2600f * 100f;
                a.Crust.MaficVolcanic[i] = 2890f * 50f;
                a.Crust.Age[i] = 10f * My;
            }
        }
        b = BuildPlate(1, g, Enumerable.Range(split, nv - 2 - split).ToArray());
        for (int i = 0; i < nv; i++)
        {
            if (i >= split && i < nv - 2)
            {
                b.Crust.Sedimentary[i] = 2600f * 200f;
                b.Crust.Age[i] = 5f * My;
            }
        }
        sim.Plates.Add(a);
        sim.Plates.Add(b);
        return sim;
    }

    [Test]
    public void Merge_Partition_TopMapPlateCountAndConserved()
    {
        var sim = PartitionSim(out var a, out var b);
        int nv = sim.GlobalGrid.VertexCount;
        int split = nv / 2;
        sim.MergePlatesToMaster();

        for (int i = 0; i < nv; i++)
        {
            if (i < split)
            {
                Assert.AreEqual(1, sim.PlateCount[i], $"A 格 {i}");
                Assert.AreEqual(a.Id, (int)sim.TopPlateMap[i], $"A 顶层 {i}");
                Assert.AreEqual(2600f * 100f, sim.WorldCrust.FelsicPlutonic[i], 1e-3f, $"A felsic {i}");
                Assert.AreEqual(2890f * 50f, sim.WorldCrust.MaficVolcanic[i], 1e-3f, $"A mafic {i}");
                Assert.AreEqual(10f * My, sim.WorldCrust.Age[i], 10f, $"A age {i}");
                Assert.AreEqual(0f, sim.WorldCrust.Sedimentary[i], 1e-3f);
            }
            else if (i < nv - 2)
            {
                Assert.AreEqual(1, sim.PlateCount[i], $"B 格 {i}");
                Assert.AreEqual(b.Id, (int)sim.TopPlateMap[i], $"B 顶层 {i}");
                Assert.AreEqual(2600f * 200f, sim.WorldCrust.Sedimentary[i], 1e-3f, $"B sedi {i}");
                Assert.AreEqual(0f, sim.WorldCrust.FelsicPlutonic[i], 1e-3f);
                Assert.AreEqual(0f, sim.WorldCrust.MaficVolcanic[i], 1e-3f);
                Assert.AreEqual(5f * My, sim.WorldCrust.Age[i], 10f, $"B age {i}");
            }
            else
            {
                Assert.AreEqual(0, sim.PlateCount[i], $"未覆盖格 {i} PlateCount=0");
                Assert.AreEqual(-1, (int)sim.TopPlateMap[i], $"未覆盖格 {i} Top=-1");
                Assert.AreEqual(0f, sim.WorldCrust.FelsicPlutonic[i], 1e-3f, $"未覆盖格 {i} 无物质");
            }
        }
        Assert.IsTrue(AllFinite(sim.WorldCrust), "合并结果有限");
    }

    [Test]
    public void Merge_Overlap_TopByDensityAndConservedSum()
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int nv = g.VertexCount;
        // 两板都覆盖全部格：板 0 洋壳（密度 2890），板 1 沉积岩（密度 2600）→ 板 1 顶层
        var p0 = BuildPlate(0, g, Enumerable.Range(0, nv).ToArray());
        var p1 = BuildPlate(1, g, Enumerable.Range(0, nv).ToArray());
        for (int i = 0; i < nv; i++)
        {
            p0.Crust.MaficVolcanic[i] = 2890f * 100f;
            p0.Crust.Age[i] = 0f;
            p1.Crust.Sedimentary[i] = 2600f * 200f;
            p1.Crust.Age[i] = 100f * My;
        }
        sim.Plates.Add(p0);
        sim.Plates.Add(p1);
        sim.MergePlatesToMaster();

        for (int i = 0; i < nv; i++)
        {
            Assert.AreEqual(2, sim.PlateCount[i], $"格 {i}");
            Assert.AreEqual(1, (int)sim.TopPlateMap[i], $"顶层应为低密度板 1：格 {i}");
            // 守恒组叠加 = 各板守恒之和（板 0 守恒为 0，板 1 为 520000）
            Assert.AreEqual(2600f * 200f, sim.WorldCrust.Sedimentary[i], 1e-3f, $"守恒叠加 {i}");
            Assert.AreEqual(0f, sim.WorldCrust.FelsicPlutonic[i], 1e-3f);
            // mafic/age 由顶层板决定（板 1 无 mafic、age=100My）
            Assert.AreEqual(0f, sim.WorldCrust.MaficVolcanic[i], 1e-3f, $"顶层板 mafic {i}");
            Assert.AreEqual(100f * My, sim.WorldCrust.Age[i], 10f, $"顶层板 age {i}");
        }
        Assert.IsTrue(AllFinite(sim.WorldCrust));
    }

    [Test]
    public void Merge_SameInputTwice_Deterministic()
    {
        var simA = NewSim(2);
        var simB = NewSim(2);
        var g = simA.GlobalGrid;
        int nv = g.VertexCount;
        foreach (var sim in new[] { simA, simB })
        {
            var p0 = BuildPlate(0, g, Enumerable.Range(0, nv / 2).ToArray());
            var p1 = BuildPlate(1, g, Enumerable.Range(nv / 2, nv - nv / 2).ToArray());
            for (int i = 0; i < nv; i++)
            {
                if (i < nv / 2) p0.Crust.FelsicPlutonic[i] = 2600f * 75f;
                else p1.Crust.Sediment[i] = 1500f * 90f;
            }
            sim.Plates.Add(p0);
            sim.Plates.Add(p1);
            sim.MergePlatesToMaster();
        }
        AssertArrayExact(simA.TopPlateMap, simB.TopPlateMap);
        CollectionAssert.AreEqual(simA.PlateCount, simB.PlateCount);
        var pa = simA.WorldCrust.AllPools();
        var pb = simB.WorldCrust.AllPools();
        for (int p = 0; p < 8; p++) AssertArrayExact(pa[p], pb[p]);
    }

    [Test]
    public void Merge_ThicknessCappedAt70km()
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int nv = g.VertexCount;
        var plate = BuildPlate(0, g, Enumerable.Range(0, nv).ToArray());
        for (int i = 0; i < nv; i++) plate.Crust.FelsicPlutonic[i] = 2600f * 100000f;   // 100km 地壳
        sim.Plates.Add(plate);
        sim.MergePlatesToMaster();

        var thickness = sim.WorldCrust.GetThickness(sim.Material);
        for (int i = 0; i < nv; i++)
            Assert.AreEqual(70000f, thickness[i], 0.1f, $"格 {i} 厚度上限 70km");
    }

    [Test]
    public void SurfaceProcesses_ConservesConservedMass_NoNaN()
    {
        var sim = PartitionSim(out _, out _);
        sim.MergePlatesToMaster();
        sim.ComputeDisplacement();
        double before = SumConserved5(sim.WorldCrust);

        var delta = sim.ApplySurfaceProcesses(1f);

        double after = SumConserved5(sim.WorldCrust);
        Assert.AreEqual(before, after, 5.0, "地表过程后 5 守恒池全球总量不变（侵蚀/风化/成岩/变质均为池间转换）");
        Assert.IsNotNull(delta);
        Assert.IsTrue(AllFinite(delta), "delta 无 NaN/Inf");
        Assert.IsTrue(AllFinite(sim.WorldCrust), "WorldCrust 无 NaN/Inf");
        Assert.IsTrue(AllFinite(sim.Displacement), "Displacement 无 NaN/Inf");
        Assert.IsTrue(AllFinite(sim.MineralSed) && AllFinite(sim.MineralMeta), "矿化事件累积有限");
    }

    [Test]
    public void SyncWorldToPlates_AppliesDeltaToTopPlatesOnly()
    {
        var sim = PartitionSim(out var a, out var b);
        sim.MergePlatesToMaster();
        int nv = sim.GlobalGrid.VertexCount;

        var delta = new Crust(sim.GlobalGrid);
        delta.Sediment[0] = 111f;            // A 区
        delta.FelsicPlutonic[1] = 222f;      // A 区
        delta.Sediment[nv - 3] = 333f;       // B 区（B mask 覆盖 split..nv-3）

        var aBefore = a.Crust.AllPools().Select(p => (float[])p.Clone()).ToArray();
        var bBefore = b.Crust.AllPools().Select(p => (float[])p.Clone()).ToArray();

        sim.SyncWorldToPlates(delta);

        // 格 0、1 顶层 = A（映射恒等 li==i）
        Assert.AreEqual(aBefore[0][0] + 111f, a.Crust.Sediment[0], 1e-5f);
        Assert.AreEqual(aBefore[3][1] + 222f, a.Crust.FelsicPlutonic[1], 1e-5f);
        Assert.AreEqual(aBefore[0][1], a.Crust.Sediment[1], 1e-5f, "A 未触格不变");
        // 格 nv-3 顶层 = B
        Assert.AreEqual(bBefore[0][nv - 3] + 333f, b.Crust.Sediment[nv - 3], 1e-5f);
        // 其他 B 格不变
        Assert.AreEqual(bBefore[0][0], b.Crust.Sediment[0], 1e-5f);
    }

    [Test]
    public void Isostasy_ComputeDisplacement_FiniteAndLandFraction()
    {
        var sim = PartitionSim(out _, out _);
        sim.MergePlatesToMaster();
        var disp = sim.ComputeDisplacement();
        Assert.AreSame(disp, sim.Displacement, "返回就是字段本身");
        Assert.IsTrue(AllFinite(disp));
        Assert.That(sim.LandFraction(), Is.InRange(0f, 1f));
    }

    [Test]
    public void Isostasy_ApplyFlexure_UniformPositiveLoadScaled()
    {
        var sim = NewSim(2);
        int nv = sim.GlobalGrid.VertexCount;
        // 均匀正负载：挠曲平滑不动（均值=自身），唯一效果是 flexK 削减
        sim.Displacement = Enumerable.Repeat(5000f, nv).ToArray();
        sim.ApplyFlexure();
        for (int i = 0; i < nv; i++)
            Assert.AreEqual(5000f * (1f - 0.35f), sim.Displacement[i], 1e-3f, $"格 {i}");
        // 零位移 → 无变化
        sim.Displacement = new float[nv];
        sim.ApplyFlexure();
        Assert.AreEqual(0f, sim.Displacement.Sum(v => Math.Abs(v)), 1e-6f);
    }

    [Test]
    public void Isostasy_SolveSeaLevel_ConvergesToDistributionEdge()
    {
        var sim = NewSim(2);
        // {-2000,-1000,0,1000,2000}；oceanFraction 0.4 → targetLand 0.6
        // 陆地占比在 (-1000, 0] 恰为 0.6 → 二分收敛于 -1000
        sim.Displacement = new[] { -2000f, -1000f, 0f, 1000f, 2000f };
        float sea = sim.SolveSeaLevel(0.4f);
        Assert.AreEqual(-1000f, sea, 0.05f);
        Assert.AreEqual(sea, sim.SeaLevel, 1e-6f, "写入 SeaLevel 字段");
    }

    [Test]
    public void Isostasy_SolveSeaLevelByVolume_NoOceanVolume_ShortCircuits()
    {
        var sim = NewSim(2);
        sim.Displacement = new float[sim.GlobalGrid.VertexCount];
        // TotalOceanDepth=0（默认，等价于 InitializeOceanVolume 前的状态）→ 直接返回当前 SeaLevel
        Assert.AreEqual(0f, sim.SolveSeaLevelByVolume(), 1e-6f);
    }

    [Test]
    public void Isostasy_SolveSeaLevelByVolume_FlatWorld_ConvergesToOceanDepth()
    {
        var sim = NewSim(2);
        int nv = sim.GlobalGrid.VertexCount;
        sim.Displacement = new float[nv];          // 全平
        sim.TotalOceanDepth = 2000f;               // 公开字段（等价 InitializeOceanVolume(2000)）
        float sea = sim.SolveSeaLevelByVolume();
        Assert.AreEqual(2000f, sea, 1e-3f, "全平世界需海平面=平均深度才能装下 2000m 水");
        Assert.IsTrue(AllFinite(sim.Displacement));
    }

    [Test]
    public void Isostasy_LandFractionAboveSea_UsesSeaLevel()
    {
        var sim = NewSim(2);
        sim.Displacement = new[] { -100f, -50f, 10f, 20f, 30f };
        sim.SeaLevel = 0f;
        Assert.AreEqual(3f / 5f, sim.LandFractionAboveSea(), 1e-6f);
        sim.SeaLevel = -60f;
        Assert.AreEqual(4f / 5f, sim.LandFractionAboveSea(), 1e-6f);
    }

    [Test]
    public void Rifting_NoGap_NoOp()
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int nv = g.VertexCount;
        var a = BuildPlate(0, g, Enumerable.Range(0, nv / 2).ToArray());
        var b = BuildPlate(1, g, Enumerable.Range(nv / 2, nv - nv / 2).ToArray());
        sim.Plates.Add(a);
        sim.Plates.Add(b);
        sim.MergePlatesToMaster();

        var maskA = (byte[])a.Mask.Clone();
        var maskB = (byte[])b.Mask.Clone();
        var crustA = a.Crust.AllPools().Select(p => (float[])p.Clone()).ToArray();

        sim.UpdateRifting();

        CollectionAssert.AreEqual(maskA, a.Mask, "无空洞 → mask 不扩展");
        CollectionAssert.AreEqual(maskB, b.Mask);
        for (int pool = 0; pool < 8; pool++)
            AssertArrayExact(crustA[pool], a.Crust.AllPools()[pool]);
        Assert.AreEqual(0f, sim.MineralHydro.Sum(v => Math.Abs(v)), 1e-9f, "无裂谷 → 无热液矿化");
    }

    [Test]
    public void Subduction_Overlap_DeepBurialMetamorphism_NoRemoval()
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int nv = g.VertexCount;
        // 两板全覆盖：板 0 密度 ≈2880（< 地幔，不会被消减移除），板 1 密度 2600 顶层
        var p0 = BuildPlate(0, g, Enumerable.Range(0, nv).ToArray());
        var p1 = BuildPlate(1, g, Enumerable.Range(0, nv).ToArray());
        for (int i = 0; i < nv; i++)
        {
            p0.Crust.MaficVolcanic[i] = 2890f * 100f;
            p0.Crust.Sediment[i] = 1000f;
            p0.Crust.Sedimentary[i] = 2000f;
            p0.Crust.FelsicPlutonic[i] = 3000f;
            p0.Crust.FelsicVolcanic[i] = 4000f;
            p1.Crust.Sedimentary[i] = 2600f * 200f;
        }
        sim.Plates.Add(p0);
        sim.Plates.Add(p1);
        sim.MergePlatesToMaster();
        Assert.AreEqual(1, (int)sim.TopPlateMap[0], "板 1（密度更低）应为顶层");

        sim.UpdateSubducted();

        for (int i = 0; i < nv; i++)
        {
            // 被压板（板 0）深埋变质：4 种 felsic 全转 metamorphic
            Assert.AreEqual(10000f, p0.Crust.Metamorphic[i], 1e-3f, $"格 {i} 变质总量");
            Assert.AreEqual(0f, p0.Crust.Sediment[i], 1e-6f, $"格 {i} sediment 清零");
            Assert.AreEqual(0f, p0.Crust.Sedimentary[i], 1e-6f);
            Assert.AreEqual(0f, p0.Crust.FelsicPlutonic[i], 1e-6f);
            Assert.AreEqual(0f, p0.Crust.FelsicVolcanic[i], 1e-6f);
            Assert.AreEqual(2890f * 100f, p0.Crust.MaficVolcanic[i], 1e-3f, "mafic 不受变质影响");
            // 变质矿化事件累积
            Assert.AreEqual(1f, sim.MineralMeta[i], 1e-6f);
            Assert.AreEqual(0.5f, sim.MineralHydro[i], 1e-6f);
        }
        // 密度 < 地幔 → 无消减移除 → 无增生楔
        foreach (var pool in sim.Accretion.AllPools())
            Assert.AreEqual(0f, pool.Sum(v => Math.Abs(v)), 1e-9f, "Accretion 应为空");
        // 顶层板 crust 未被触碰
        Assert.AreEqual(2600f * 200f, p1.Crust.Sedimentary[0], 1e-3f);
    }

    [Test]
    public void Accretion_AppliesToTopPlate_ThenResets()
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int nv = g.VertexCount;
        var p0 = BuildPlate(0, g, Enumerable.Range(0, nv).ToArray());
        var p1 = BuildPlate(1, g, Enumerable.Range(0, nv).ToArray());
        for (int i = 0; i < nv; i++) p1.Crust.Sedimentary[i] = 2600f * 200f;
        sim.Plates.Add(p0);
        sim.Plates.Add(p1);
        sim.MergePlatesToMaster();   // 顶层 = 板 1（密度 2600 < 板 0 空 crust 默认 2890）

        // 手工写入增生楔（变质岩，俯冲上盘造山带）
        for (int i = 0; i < 5; i++) sim.Accretion.Metamorphic[i] = 5000f;
        sim.ApplyAccretion();

        for (int i = 0; i < nv; i++)
        {
            float expected = i < 5 ? 5000f : 0f;
            Assert.AreEqual(expected, p1.Crust.Metamorphic[i], 1e-3f, $"板 1 变质增生 {i}");
            Assert.AreEqual(0f, p0.Crust.Metamorphic[i], 1e-3f, "非顶层板不接收增生");
            Assert.AreEqual(i < 5 ? 1f : 0f, sim.MineralHydro[i], 1e-6f, $"增生矿化 {i}");
        }
        foreach (var pool in sim.Accretion.AllPools())
            Assert.AreEqual(0f, pool.Sum(v => Math.Abs(v)), 1e-9f, "Accretion 已清空");
    }

    [Test]
    public void TryMergeCollidingPlates_TwoPlates_NoOp()
    {
        // Plates.Count ≤ 5 → 直接返回（不进入含日志的缝合路径）
        var sim = PartitionSim(out _, out _);
        sim.MergePlatesToMaster();
        int before = sim.Plates.Count;
        sim.TryMergeCollidingPlates();
        Assert.AreEqual(before, sim.Plates.Count);
    }
}