using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;

namespace World.Tests;

/// <summary>
/// 板缘分类符号回归（设计-04 批次 0）：03 §11-10 曾把汇聚/离散判反（v_rel 写成 v_j − v_i），
/// 修复后无专项测试钉住。本文件用**手工速度场**（不走模拟）构造半球相向/相离两种世界，
/// 断言分类与带符号收敛率的符号。
/// 约定（02 §2.3 / H3PlateBoundary 头注）：v_rel = v_i − v_j，v_n > 0 ⟺ 相向 ⟺ 汇聚。
/// 纪律（同 H3PlateStaticTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3PlateBoundaryTests
{
    private const int Res = 1;          // 842 格，构造便宜

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;

    // X≥0 为板 0（东半球），X<0 为板 1（西半球）——边界 = 过 YZ 平面的大圆。
    // 速度方向 = sign × (板 0 取 −X̂ / 板 1 取 +X̂)：signEast=+1 & signWest=−1 → 相向；
    // 同取反 → 相离；同号（+1,+1）→ 两板同动（都朝 −X̂）。
    // 速度大小取 5 km/My 对应的角速度（> 惰性阈值 2 km/My），投影到切平面（去径向分量）。
    static H3PlateFields HalfWorldFields()
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        for (int i = 0; i < fields.Count; i++)
            fields.PlateId[i] = Ball.CellCenters[i].X >= 0f ? 0 : 1;
        return fields;
    }

    static Vector3[] Velocities(float signEast, float signWest)
    {
        int n = Ball.CellIds.Length;
        var velocity = new Vector3[n];
        const float speedRadPerMy = 5f / H3PlateBoundary.EarthRadiusKm;   // 5 km/My
        for (int i = 0; i < n; i++)
        {
            Vector3 center = Ball.CellCenters[i];
            Vector3 radial = center.Normalized();
            float sign = center.X >= 0f ? signEast : signWest;
            Vector3 toward = center.X >= 0f ? new Vector3(-sign, 0, 0) : new Vector3(sign, 0, 0);
            Vector3 tangential = toward - radial * toward.Dot(radial);
            velocity[i] = tangential.Normalized() * speedRadPerMy;
        }
        return velocity;
    }

    [Test]
    public void Build_ApproachingPlates_ClassifiedConvergent_PositiveRate()
    {
        var fields = HalfWorldFields();
        var boundary = new H3PlateBoundary(Ball.CellIds.Length);
        boundary.Build(Ball, fields, Velocities(signEast: +1f, signWest: +1f));
        // 板 0（东）朝 −X 走、板 1（西）朝 +X 走 → 在 X≈0 边界上相向。

        Assert.Greater(boundary.BoundaryCellCount, 0, "半球相接必有板缘格");
        for (int i = 0; i < Ball.CellIds.Length; i++)
        {
            if (boundary.RelativeSpeedKmPerMy[i] <= 0f) continue;         // 板内格（无支配边）
            Assert.AreEqual(PlateBoundaryKind.Convergent, boundary.Kind[i],
                $"格 {i} 相向运动应判汇聚（v_rel = v_i − v_j 符号约定的回归）");
            Assert.Greater(boundary.RelativeSpeedKmPerMy[i], H3PlateBoundary.InertSpeedKmPerMy,
                "相向世界板缘相对速率应超惰性阈值");
        }
    }

    [Test]
    public void Build_SeparatingPlates_ClassifiedDivergent()
    {
        var fields = HalfWorldFields();
        var boundary = new H3PlateBoundary(Ball.CellIds.Length);
        boundary.Build(Ball, fields, Velocities(signEast: -1f, signWest: -1f));
        // 板 0（东）朝 +X 走、板 1（西）朝 −X 走 → 在边界上相互远离。

        Assert.Greater(boundary.BoundaryCellCount, 0);
        for (int i = 0; i < Ball.CellIds.Length; i++)
        {
            if (boundary.RelativeSpeedKmPerMy[i] <= 0f) continue;
            Assert.AreEqual(PlateBoundaryKind.Divergent, boundary.Kind[i],
                $"格 {i} 相离运动应判离散");
        }
    }

    [Test]
    public void Build_CoMovingPlates_ClassifiedInert()
    {
        var fields = HalfWorldFields();
        var boundary = new H3PlateBoundary(Ball.CellIds.Length);
        boundary.Build(Ball, fields, Velocities(signEast: +1f, signWest: -1f));
        // 两板同向（都朝 −X 的切向投影）→ 相对速度 ≈ 0 → 惰性。

        for (int i = 0; i < Ball.CellIds.Length; i++)
        {
            if (boundary.RelativeSpeedKmPerMy[i] <= 0f) continue;
            Assert.AreEqual(PlateBoundaryKind.Inert, boundary.Kind[i],
                $"格 {i} 同动边界应判惰性（相对速率 {boundary.RelativeSpeedKmPerMy[i]:F2} km/My）");
        }
    }
}
