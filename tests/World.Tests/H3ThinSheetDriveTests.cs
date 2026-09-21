using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// P2 薄席驱动接线测试（设计-07 §5）：
///   · 覆写通道：ThinSheetDriving 时 motion 直写外部速度场、⑤b 节拍吃外部逐板均速（力平衡跳过）；
///   · 集成：薄席驱动下质量审计闭账、速度场非零、且与旧力平衡驱动的轨迹不同（切换确有行为效果）；
///   · 确定性：同 seed 两跑逐位一致。
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3ThinSheetDriveTests
{
    const int Res = 1;
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;
    static readonly MaterialDensity Material = new();

    [Test]
    public void ThinSheetDriving_SlabTraction_PullsPlateTowardAccount()
    {
        // 手工两板（X±），板 0 质心在 +x 侧：账户方向 = −x̂ ⇒ 有账户的板 0 应被拉向 −x
        int n = Ball.CellIds.Length;
        var plateOfCell = new int[n];
        for (int i = 0; i < n; i++) plateOfCell[i] = Ball.CellCenters[i].X > 0f ? 0 : 1;

        var with = new H3DynamicTectonics(Ball) { EnableThinSheetDriving = true };
        with.Initialize(plateOfCell, 42);
        with.Motion.AddSlab(0, Material.MaficVolcanicMin * 7100f * 50f, new Vector3(-1f, 0f, 0f));
        with.Step();

        var without = new H3DynamicTectonics(Ball) { EnableThinSheetDriving = true };
        without.Initialize(plateOfCell, 42);
        without.Step();

        // 板 0 格均速度的 −x 分量：有账户 > 无账户（差值 = 板片拉力的行为证据）
        double flowWith = 0, flowWithout = 0;
        int cnt = 0;
        for (int i = 0; i < n; i++)
        {
            if (Ball.CellCenters[i].X <= 0f) continue;
            cnt++;
            flowWith += with.Motion.Velocity[i].X;
            flowWithout += without.Motion.Velocity[i].X;
        }
        Assert.Less(flowWith / cnt, flowWithout / cnt,
            "板片账户应把板 0 拉向账户方向（−x̂ ⇒ 速度 X 分量更负）——有账户 vs 无账户对照");
    }

    [Test]
    public void ThinSheetDriving_FlowsTowardLowerGpe_AtSeams()
    {
        var sim = NewDrivenSim(true);
        sim.Step();

        // 重算本步薄席所见的 GPE 场（与 DriveByThinSheet 同式同源）
        var ball = Ball;
        int n = ball.CellIds.Length;
        var elev = new float[n];
        var gpe = new float[n];
        for (int i = 0; i < n; i++) elev[i] = sim.Displacement[i] - sim.SeaLevel;
        H3Gpe.ComputeInto(sim.Fields, elev, Material, gpe);

        // 跨缝高压格：速度应指向低 GPE 侧（B 方案的核心物理：物质沿 GPE 下坡流 = 涌现的洋脊推力）
        var centers = ball.CellCenters;
        var neighbors = ball.CellNeighbors;
        int cnt = 0;
        double flow = 0;
        for (int i = 0; i < n; i++)
        {
            int lowNb = -1;
            float drop = 0f;
            foreach (int nb in neighbors[i])
            {
                float d = gpe[i] - gpe[nb];
                if (d > drop) { drop = d; lowNb = nb; }
            }
            if (lowNb < 0 || drop < 1e10f) continue;           // 只看有明显 GPE 落差的格（缝边）
            Vector3 toward = centers[lowNb] - centers[i];
            toward -= ball.CellDirs[i] * toward.Dot(ball.CellDirs[i]);
            toward = toward.Normalized();
            cnt++;
            flow += sim.Motion.Velocity[i].Dot(toward);
        }
        Assert.Greater(cnt, 0, "应存在跨 GPE 落差格（测试布置检查）");
        Assert.Greater(flow / cnt, 0, $"高压格速度应指向低 GPE 侧（实测均流 {flow / cnt:E3} cm/yr 口径）");
    }

    static H3DynamicTectonics NewDrivenSim(bool thinSheet, bool flux = false)
    {
        var splitter = new H3Plate(Ball);
        int[] plateOfCell = splitter.SplitIntoPlates(6, 42);
        var sim = new H3DynamicTectonics(Ball)
        {
            EnableThinSheetDriving = thinSheet,
            EnableFluxTransport = thinSheet && flux,   // 通量吃薄席驱动场，单独开无意义
        };
        sim.Initialize(plateOfCell, 42);
        return sim;
    }

    [Test]
    public void FluxTransport_MassAuditCloses_AndDrivesMotion()
    {
        var sim = NewDrivenSim(true, flux: true);
        for (int s = 0; s < 6; s++)
        {
            sim.Step();
            double dbg = sim.TotalCrustMass()
                - (sim.InitialCrustMass + sim.CrustCreatedTotal - sim.CrustDestroyedTotal);
            Console.WriteLine($"[FLUXDBG] step={s} audit={dbg:E3} created={sim.CrustCreatedTotal:E3} " +
                $"destroyed={sim.CrustDestroyedTotal:E3} mass={sim.TotalCrustMass():E3}");
        }

        double audit = sim.TotalCrustMass()
            - (sim.InitialCrustMass + sim.CrustCreatedTotal - sim.CrustDestroyedTotal);
        Assert.Less(Math.Abs(audit), sim.InitialCrustMass * 1e-6,
            "质量守恒对账（通量运输：注壳/俯冲两户台账经 CrustCreated/Destroyed 闭账）");

        bool anyMotion = false;
        for (int i = 0; i < sim.Motion.Velocity.Length && !anyMotion; i++)
            anyMotion = sim.Motion.Velocity[i].Length() > 0f;
        Assert.IsTrue(anyMotion, "薄席驱动应产生非零速度场");
    }

    [Test]
    public void SolverDrive_CouplingStaysBounded_OverSteps()
    {
        // P5 R1 耦合有界性测试（架构接线前的正确顺序）：解算器直驱 + 通量平流的耦合回路
        // 在海岸 GPE 悬崖处的正反馈（实测 0.25 → 46 rad/My → NaN）必须被收敛纪律 + 步长
        // 反馈压住：N 步内驱动场保持有界、无 NaN、审计闭账。本测试是 P5 的第一道闸门。
        var sim = NewDrivenSim(true, flux: true);
        H3ThinSheet.TraceSolve = true;
        float maxDrive = 0f;
        for (int s = 0; s < 12; s++)
        {
            sim.Step();
            var drive = sim.DriveVelocityRadPerMy!;
            for (int i = 0; i < drive.Length; i++)
            {
                Assert.IsTrue(float.IsFinite(drive[i].X) && float.IsFinite(drive[i].Y)
                    && float.IsFinite(drive[i].Z), $"step {s} 格 {i} 驱动场出现非有限值（耦合失稳）");
                maxDrive = MathF.Max(maxDrive, drive[i].Length());
            }
        }
        float maxDriveCmPerYr = maxDrive * H3PlateMotion.EarthRadiusKm * 10f;   // rad/My → cm/yr
        Console.WriteLine($"[BOUND] 12 步 maxDrive = {maxDriveCmPerYr:F0} cm/yr（限幅器上限 50 cm/yr）");
        Assert.Less(maxDrive, 0.1f,
            $"耦合 12 步驱动场峰值 {maxDrive:E3} rad/My 越过限幅器（≈ {maxDriveCmPerYr:F0} cm/yr）——耦合正反馈复发");
    }

    [Test]
    public void FluxTransport_Deterministic()
    {
        var a = NewDrivenSim(true, flux: true);
        var b = NewDrivenSim(true, flux: true);
        for (int s = 0; s < 3; s++) { a.Step(); b.Step(); }
        Assert.AreEqual(a.TotalCrustMass(), b.TotalCrustMass(), "同 seed 两跑逐位一致（质量）");
        Assert.AreEqual(a.Motion.Velocity[10].X, b.Motion.Velocity[10].X, "同 seed 两跑逐位一致（速度场）");
    }

    [Test]
    public void ThinSheetDriving_MassAuditCloses_AndDrivesMotion()
    {
        var sim = NewDrivenSim(true);
        for (int s = 0; s < 6; s++)
        {
            sim.Step();
            double dv = 0;
            for (int i = 0; i < sim.Motion.Velocity.Length; i++) dv += sim.Motion.Velocity[i].Length();
            var tbl = sim.Motion.PlateSpeedRadPerMy;
            double spd = 0;
            if (tbl != null) foreach (var w in tbl) spd += w * H3PlateMotion.EarthRadiusKm;
            // GPE 方差 + 海拔方差（塌场检查）
            int n = Ball.CellIds.Length;
            var elev = new float[n];
            var gpe = new float[n];
            for (int i = 0; i < n; i++) elev[i] = sim.Displacement[i] - sim.SeaLevel;
            H3Gpe.ComputeInto(sim.Fields, elev, Material, gpe);
            double gMean = 0, eMean = 0;
            for (int i = 0; i < n; i++) { gMean += gpe[i]; eMean += elev[i]; }
            gMean /= n; eMean /= n;
            double gVar = 0, eVar = 0;
            for (int i = 0; i < n; i++) { gVar += (gpe[i] - gMean) * (gpe[i] - gMean); eVar += (elev[i] - eMean) * (elev[i] - eMean); }
            Console.WriteLine($"[P2DBG] step={s} meanV={dv / n:E3} sumPlateSpeed={spd:E3} minFit={sim.Motion.MinFitSpeedKmPerMy:E3} " +
                $"gpeStd={Math.Sqrt(gVar / n):E3} elevStd={Math.Sqrt(eVar / n):E3} seaLevel={sim.SeaLevel:E3} " +
                $"ownerless={sim.OwnerlessCellsLastStep} plates={sim.PlateCount} motPlates={sim.Motion.PlateIds.Count}");
        }

        double audit = sim.TotalCrustMass()
            - (sim.InitialCrustMass + sim.CrustCreatedTotal - sim.CrustDestroyedTotal);
        Assert.Less(Math.Abs(audit), sim.InitialCrustMass * 1e-6, "质量守恒对账（P2 切驱动下平流仍守恒）");

        bool anyMotion = false;
        for (int i = 0; i < sim.Motion.Velocity.Length && !anyMotion; i++)
            anyMotion = sim.Motion.Velocity[i].Length() > 0f;
        Assert.IsTrue(anyMotion, "薄席驱动应产生非零速度场");
    }

    [Test]
    public void ThinSheetDriving_ChangesTrajectory_VersusKinematics()
    {
        var kinematic = NewDrivenSim(false);
        var thin = NewDrivenSim(true);
        for (int s = 0; s < 3; s++) { kinematic.Step(); thin.Step(); }

        bool differs = false;
        for (int i = 0; i < Ball.CellIds.Length && !differs; i++)
            differs = kinematic.Motion.Velocity[i] != thin.Motion.Velocity[i];
        Assert.IsTrue(differs, "两种驱动方式的轨迹应不同（切换确有行为效果）");
    }

    [Test]
    public void ThinSheetDriving_Deterministic()
    {
        var a = NewDrivenSim(true);
        var b = NewDrivenSim(true);
        for (int s = 0; s < 3; s++) { a.Step(); b.Step(); }
        Assert.AreEqual(a.TotalCrustMass(), b.TotalCrustMass(), "同 seed 两跑逐位一致（质量）");
        Assert.AreEqual(a.Motion.Velocity[10].X, b.Motion.Velocity[10].X, "同 seed 两跑逐位一致（速度场）");
    }
}
