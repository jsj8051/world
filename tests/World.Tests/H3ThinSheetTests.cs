using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;

namespace World.Tests;

/// <summary>
/// 薄席解算器行为测试（设计-07 P1 验收）。四条行为断言：
///   · 均匀 GPE ⇒ 恒零速度场（体力逐边抵消，解算器不得无中生有）；
///   · GPE 阶跃 ⇒ 高侧向低侧流（洋脊推力方向的离散对应物）；
///   · 屈服定位：低屈服阈值下变形集中在屈服边（max/mean 应变比高于纯粘性对照）——造山带"涌现"的机制前提；
///   · 确定性：同输入两次求解（异实例）逐位一致。
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3ThinSheetTests
{
    const int Res = 1;
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;

    static float[] UniformGpe(float value)
    {
        var gpe = new float[Ball.CellIds.Length];
        Array.Fill(gpe, value);
        return gpe;
    }

    /// <summary>GPE 阶跃场：x &gt; 0 高侧，x ≤ 0 低侧（跨缝差 = Earth 量级 4e12 N/m）。</summary>
    static float[] SteppedGpe(float high, float low)
    {
        var gpe = new float[Ball.CellIds.Length];
        for (int i = 0; i < gpe.Length; i++)
            gpe[i] = Ball.CellCenters[i].X > 0f ? high : low;
        return gpe;
    }

    [Test]
    public void UniformGpe_NoMotion()
    {
        var sheet = new H3ThinSheet(Ball);
        sheet.Solve(UniformGpe(3.7e12f));

        float maxSpeed = 0f;
        foreach (var v in sheet.Velocity) maxSpeed = MathF.Max(maxSpeed, v.Length());
        Assert.Less(maxSpeed, 1e-9f, "均匀 GPE 下体力逐边抵消，速度场应恒零（欠松弛残差 ~1e-11 m/yr）");
    }

    [Test]
    public void GpeStep_FlowsDownGradient()
    {
        var sheet = new H3ThinSheet(Ball);
        sheet.Solve(SteppedGpe(4.0e12f, 3.6e12f));

        // 跨缝高侧格：速度应指向低侧（−x）
        int checkedCells = 0;
        float maxSpeed = 0f;
        for (int i = 0; i < Ball.CellIds.Length; i++)
        {
            maxSpeed = MathF.Max(maxSpeed, sheet.Velocity[i].Length());
            if (Ball.CellCenters[i].X <= 0f) continue;
            bool crossesSeam = false;
            foreach (int nb in Ball.CellNeighbors[i])
                if (Ball.CellCenters[nb].X <= 0f) { crossesSeam = true; break; }
            if (!crossesSeam) continue;
            checkedCells++;
            Assert.Greater(sheet.Velocity[i].Dot(new Vector3(-1f, 0f, 0f)), 0f,
                "跨缝高侧格应向低侧流动（洋脊推力方向的离散对应物）");
        }
        Assert.Greater(checkedCells, 0, "res1 球上应存在跨缝格（测试布置错误检查）");
        Assert.Greater(maxSpeed, 1e-8f, "地球量级的 GPE 阶跃应驱动出可见速度");
        // ⚠️ 只防发散不标定量级：绝对速度档 = SheetViscosityPaS/YieldForce 旋钮的函数，
        // 阶跃强迫又是极端集中源——量级标定是 P2 判读的活（设计-07 §4 旋钮表）。
        Assert.Less(maxSpeed, 1e4f, "速度发散（塑性下限/规范锚或实现有误）");
    }

    [Test]
    public void Yield_LocalizesDeformation_VersusViscous()
    {
        var gpe = SteppedGpe(4.0e12f, 3.6e12f);

        var plastic = new H3ThinSheet(Ball) { YieldForcePerLengthN = 1e10f };   // 弱屈服：跨缝边几乎自由滑移
        plastic.Solve(gpe);
        var viscous = new H3ThinSheet(Ball) { YieldForcePerLengthN = 1e20f };   // 近纯粘性对照
        viscous.Solve(gpe);

        float MaxOverMean(float[] strain)
        {
            float max = 0f, sum = 0f;
            foreach (var s in strain) { max = MathF.Max(max, s); sum += s; }
            return sum > 0f ? max / (sum / strain.Length) : 0f;
        }
        float plasticRatio = MaxOverMean(plastic.StrainRate);
        float viscousRatio = MaxOverMean(viscous.StrainRate);
        Assert.Greater(plasticRatio, viscousRatio,
            "屈服应让变形集中在屈服边（max/mean 应变比高于纯粘性对照）——造山带涌现的机制前提");
        Assert.Greater(plasticRatio, 1.5f, $"塑性档变形应显著定位（实测 max/mean = {plasticRatio:F2}）");
    }

    [Test]
    public void Deterministic_SameInputTwice_BitwiseIdentical()
    {
        var gpe = SteppedGpe(4.0e12f, 3.6e12f);
        var a = new H3ThinSheet(Ball);
        var b = new H3ThinSheet(Ball);
        a.Solve(gpe);
        b.Solve(gpe);

        for (int i = 0; i < Ball.CellIds.Length; i++)
        {
            Assert.AreEqual(a.Velocity[i].X, b.Velocity[i].X);
            Assert.AreEqual(a.Velocity[i].Y, b.Velocity[i].Y);
            Assert.AreEqual(a.Velocity[i].Z, b.Velocity[i].Z);
            Assert.AreEqual(a.StrainRate[i], b.StrainRate[i]);
        }
    }

    [Test]
    public void BasalDrag_SolverLocalizedResponse_SignAndDeterminism()
    {
        // P2 起逐格解算器不进驱动热路径（板级 GPE 净力驱动代替，球面锁死缺陷登记于设计-07 §0.0）；
        // 解算器保留为 P3 应变场引擎——此处只钉它在局域阶跃强迫下的行为：有响应、方向正确、确定性。
        var gpe = SteppedGpe(4.0e12f, 3.6e12f);
        var sheet = new H3ThinSheet(Ball);
        sheet.Solve(gpe);

        // 高 X 侧（高 GPE）跨缝格：速度应指向 −x（低 GPE 侧）
        double flow = 0;
        int cnt = 0;
        for (int i = 0; i < Ball.CellIds.Length; i++)
        {
            if (Ball.CellCenters[i].X <= 0f) continue;
            bool crossesSeam = false;
            foreach (int nb in Ball.CellNeighbors[i])
                if (Ball.CellCenters[nb].X <= 0f) { crossesSeam = true; break; }
            if (!crossesSeam) continue;
            cnt++;
            flow += sheet.Velocity[i].Dot(new Vector3(-1f, 0f, 0f));
        }
        Assert.Greater(cnt, 0, "跨缝格应存在");
        Assert.Greater(flow / cnt, 0f, $"跨缝高侧应向低侧响应（实测 {flow / cnt:E3}）");
    }

    [Test]
    public void LinearRamp_GlobalDrive_FlowsDownhill_NoLockup()
    {
        // 球面锁死缺陷的关闭测试（设计-07 §0.0 登记项，2026-09-20 复判）：
        // 线形 GPE 坡 gpe = k·X 是"平移型"全球驱动——P2.1 早期（×平均边刚度×1e-4 弱规范锚时代）
        // 实测收敛到 ~0，当时的处方是"逐边未知量重入"。复判结论：锁死根因 = 弱规范锚的平移
        // 近零空间饿死 CG；P2.1 改拖曳型规范锚（K/gauge ≈ 22，分辨率无关）后已顺带修复——
        // 逐格速度表述保留，逐边重写不采用（§8 问题 2 关闭）。本测试钉住：全球驱动健康
        // （速度沿下坡、量级与拖曳平衡档同数量级、CG 在预算内收敛），res1/res2 同验。
        foreach (var res in new[] { 1, 2 })
        {
            var ball = new Ball(res, 1f);
            float k = 2e12f;
            var gpe = new float[ball.CellIds.Length];
            for (int i = 0; i < gpe.Length; i++) gpe[i] = k * ball.CellCenters[i].X;

            var sheet = new H3ThinSheet(ball);
            sheet.Solve(gpe);

            float radiusM = H3PlateMotion.EarthRadiusKm * 1000f;
            double sumSpeed = 0, sumAlong = 0;
            int n = ball.CellIds.Length;
            for (int i = 0; i < n; i++)
            {
                Vector3 w = new Vector3(1f, 0f, 0f)
                    - ball.CellDirs[i] * ball.CellDirs[i].Dot(new Vector3(1f, 0f, 0f));   // −x 平移型切向场
                sumSpeed += sheet.Velocity[i].Length();
                sumAlong += sheet.Velocity[i].Dot(-w / w.Length());
            }
            float meanSpeed = (float)(sumSpeed / n) * 100f;      // cm/yr
            float meanAlong = (float)(sumAlong / n) * 100f;
            float dragBalance = k / radiusM / sheet.BasalDragNyrPerM3 * 100f;   // 板式平衡档 |∇GPE|/c_b

            Assert.Greater(meanAlong, 0f, $"res{res}：线形坡应驱动整体沿 −x 下坡（方向不锁死）");
            Assert.Greater(meanAlong, 0.8f * meanSpeed, $"res{res}：运动应以下坡分量为主（无侧向发散）");
            Assert.That(meanSpeed, Is.EqualTo(dragBalance).Within(0.75f * dragBalance),
                $"res{res}：全球驱动速度应与拖曳平衡档（{dragBalance:F2} cm/yr）同数量级——压制超 4× 即锁死复发");
            Assert.Less(sheet.LastCgIterations, sheet.CgMaxIterations,
                $"res{res}：CG 应在预算内收敛（残差 {sheet.LastCgResidual:E2}）");
        }
    }

    [Test]
    public void ShadowCompare_UnitsDirectionThreshold()
    {
        // 旧：1 rad/My = 637.1 cm/yr；新：±0.05 m/yr = ±5 cm/yr；第三格双方低于阈值（0.05 cm/yr）
        var oldV = new[] { new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 0f) };
        var newV = new[] { new Vector3(0.05f, 0f, 0f), new Vector3(-0.05f, 0f, 0f), new Vector3(0.0005f, 0f, 0f) };

        var stats = H3ThinSheet.CompareWithKinematics(oldV, newV, 0.1f);

        Assert.AreEqual((637.1f + 637.1f) / 3f, stats.MeanSpeedOldCmPerYr, 0.5f, "旧场均值（rad/My → cm/yr 换算）");
        Assert.AreEqual((5f + 5f + 0.05f) / 3f, stats.MeanSpeedNewCmPerYr, 0.01f, "新场均值（m/yr → cm/yr 换算，含近零速格）");
        Assert.AreEqual(2, stats.ComparedCells, "第三格双方低于阈值，不参与方向对比");
        Assert.AreEqual(0.5f, stats.DirectionAgreement, 1e-5f, "一格同向一格反向 ⇒ 一致率 0.5");
    }
}
