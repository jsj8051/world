using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 构造地形带（设计-04 批次 3）测试：连续收敛率驱动的海沟/火山弧。
/// 断言的测量项：
///   · 汇聚边洋侧（下盘）出海沟：位移被下挖到基准 −2.5 km 以深（轴率 = C/10km/My 饱和）；
///   · 上盘内陆偏移出弧：新增火山质量（含长英质）⇒ IsLand 转 true（大陆生长通道）；
///   · 离散边界无沟无弧（对照组）；
///   · 确定性：同构世界两次施加逐位一致。
/// 纪律（同 H3PlateStaticTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService；浮点断言带容差。
/// </summary>
public class H3BoundaryReliefTests
{
    private const int Res = 1;

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;
    static readonly MaterialDensity Material = new();

    // X≥0 板 0（东），X<0 板 1（西）；全洋壳；东慢（上盘）西快（下盘俯冲）。
    static H3PlateFields OceanWorld() => OceanWorld(speedScaleEast: 1f, speedScaleWest: 2f);

    static H3PlateFields OceanWorld(float speedScaleEast, float speedScaleWest)
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        for (int i = 0; i < fields.Count; i++)
        {
            fields.PlateId[i] = Ball.CellCenters[i].X >= 0f ? 0 : 1;
            fields.ClearCell(i);
            fields.MaficVolcanic[i] = Material.MaficVolcanicMin * 7100f;
            fields.Age[i] = 0f;
        }
        _speedScaleEast = speedScaleEast;
        _speedScaleWest = speedScaleWest;
        return fields;
    }

    static float _speedScaleEast = 1f, _speedScaleWest = 2f;

    // 相向速度场：东板朝 −X、西板朝 +X（切向投影）；幅值 = 5 km/My × 各自 scale（scale 大 = 快 = 下盘）。
    static Vector3[] ConvergentVelocities()
    {
        int n = Ball.CellIds.Length;
        var velocity = new Vector3[n];
        const float speedRadPerMy = 5f / H3PlateBoundary.EarthRadiusKm;
        for (int i = 0; i < n; i++)
        {
            Vector3 center = Ball.CellCenters[i];
            Vector3 radial = center.Normalized();
            float scale = center.X >= 0f ? _speedScaleEast : _speedScaleWest;
            Vector3 toward = center.X >= 0f ? new Vector3(-scale, 0, 0) : new Vector3(scale, 0, 0);
            Vector3 tangential = toward - radial * toward.Dot(radial);
            velocity[i] = tangential.Normalized() * speedRadPerMy * scale;
        }
        return velocity;
    }

    // 离散速度场（对照）：东西各退 → 无沟无弧。
    static Vector3[] DivergentVelocities()
    {
        var velocity = ConvergentVelocities();
        for (int i = 0; i < velocity.Length; i++) velocity[i] = -velocity[i];
        return velocity;
    }

    [Test]
    public void Apply_ConvergentOcean_TrenchOnLowerPlateAndArcOnUpper()
    {
        var fields = OceanWorld();
        var velocity = ConvergentVelocities();
        var isostasy = new H3Isostasy(Ball.CellIds.Length) { TotalOceanDepth = 2000.0 };
        var relief = new H3BoundaryRelief();
        relief.Apply(Ball, fields, velocity, isostasy, Material);

        Assert.Greater(relief.TrenchAxisCells, 0, "汇聚边洋侧应有海沟轴格");
        float deepest = float.MaxValue;
        foreach (float d in isostasy.Displacement) if (d < deepest) deepest = d;
        // 洋底基准 ≈ −2500 m（age=0）；轴率饱和（C=15 km/My ≥ 参考值 10）→ 轴深 −2500−2500 = −5000
        Assert.LessOrEqual(deepest, -4800f,
            $"最深位移 {deepest:F0} m 应达海沟量级（基准 −2500 − 轴深 2500；03 §5 改进 8 的闭环断言）");

        Assert.Greater(relief.ArcCells, 0, "上盘内陆应有弧格");
        Assert.Greater(relief.ArcAddedMass, 0.0);
        bool arcLand = false;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields.FelsicVolcanic[i] > 0f && fields.IsLand(i)) { arcLand = true; break; }
        }
        Assert.IsTrue(arcLand, "弧物质含长英质 ⇒ 该格转陆（大陆生长通道，testproject T6）");

        // 质量口径：弧新增 = mafic + felsic 逐格增量之和
        double massSum = 0;
        for (int i = 0; i < fields.Count; i++)
            massSum += fields.MaficVolcanic[i] + fields.FelsicVolcanic[i] - Material.MaficVolcanicMin * 7100f;
        Assert.AreEqual(relief.ArcAddedMass, massSum, Math.Max(1.0, relief.ArcAddedMass * 1e-6),
            "弧新增质量记账应与场内增量一致");
    }

    [Test]
    public void Apply_DivergentBoundary_NoTrenchNoArc()
    {
        var fields = OceanWorld();
        var velocity = DivergentVelocities();
        var isostasy = new H3Isostasy(Ball.CellIds.Length) { TotalOceanDepth = 2000.0 };
        var relief = new H3BoundaryRelief();
        relief.Apply(Ball, fields, velocity, isostasy, Material);

        Assert.AreEqual(0, relief.TrenchAxisCells, "离散边不应有海沟");
        Assert.AreEqual(0, relief.ArcCells, "离散边不应有弧");
        Assert.AreEqual(0.0, relief.ArcAddedMass, 1e-6);
    }

    [Test]
    public void Apply_SameWorldTwice_BitwiseIdentical()
    {
        var fieldsA = OceanWorld();
        var fieldsB = OceanWorld();
        var velocity = ConvergentVelocities();
        var isostasyA = new H3Isostasy(Ball.CellIds.Length) { TotalOceanDepth = 2000.0 };
        var isostasyB = new H3Isostasy(Ball.CellIds.Length) { TotalOceanDepth = 2000.0 };
        var reliefA = new H3BoundaryRelief();
        var reliefB = new H3BoundaryRelief();
        reliefA.Apply(Ball, fieldsA, velocity, isostasyA, Material);
        reliefB.Apply(Ball, fieldsB, velocity, isostasyB, Material);

        CollectionAssert.AreEqual(fieldsA.MaficVolcanic, fieldsB.MaficVolcanic);
        CollectionAssert.AreEqual(fieldsA.FelsicVolcanic, fieldsB.FelsicVolcanic);
        CollectionAssert.AreEqual(isostasyA.Displacement, isostasyB.Displacement);
        Assert.AreEqual(reliefA.ArcAddedMass, reliefB.ArcAddedMass);
        Assert.AreEqual(reliefA.TrenchAxisCells, reliefB.TrenchAxisCells);
    }
}
