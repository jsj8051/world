using System;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 顶死接触零功修正（03 v1.8）测试：运动学把"本步必被顶死"的致密板缘格剔除出净驱动力 F
/// （堵住不动的接触不做功），判据与平流 Pass C 共用 H3PlateContact。
/// 断言的测量项：
///   · FlowNeighbor 落点选择：切向投影最大者、纯径向邻居跳过、平局保先到者（确定性）；
///   · JamsInto 判据：不比目标密才顶死（更密 = 俯冲）；foreign 前提由调用方保证；
///   · 运动学：顶死接触世界 vs 俯冲对照世界——顶死世界有幻影驱动力（毛 F > 净 F）且板速更低；
///   · 平流：真接触顶死计数（JamCellCount）只认 foreign——相位停驻不计；
///   · 全链路 smoke：判读口接线（JammedCells/JamContactCells/PhantomForceFraction）。
/// 纪律（同 H3PlateStaticTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService；浮点断言带容差。
/// </summary>
public class H3PlateJamTests
{
    // res1：N=842 格，构造便宜（对比 H3PlateStaticTests 的 res3 共享网格）；一对近邻足够定向
    private const int Res = 1;
    private const float OldAgeMy = 300f;    // > MaficAgeSaturationMy(250)：密度 = MaficVolcanicMax(3300) > 地幔(3075)
    private const float YoungAgeMy = 0f;    // 密度 = MaficVolcanicMin(2890) < 地幔 → 浮力恰 0，不驱动

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;

    static readonly MaterialDensity Material = new();

    // ── 工具 ──

    /// <summary>一对相邻格（前者在前）：全球任取首格 i，取其首邻居 j（H3 邻接对称）。</summary>
    static (int i, int j) AdjacentPair()
    {
        int i = 0;
        while (Ball.CellNeighbors[i].Length == 0) i++;   // 恒不触发（H3 网格无孤立格）
        return (i, Ball.CellNeighbors[i][0]);
    }

    /// <summary>老洋壳柱：纯 mafic、Age 超饱和 → 柱密度 = 3300（> 地幔 → 负浮力 → 驱动）。</summary>
    static void FillOldOceanic(H3PlateFields fields, int cell)
    {
        fields.ClearCell(cell);
        fields.MaficVolcanic[cell] = Material.MaficVolcanicMin * 7100f;   // 质量面密度 = ρ₀×7.1km 柄
        fields.Age[cell] = OldAgeMy;
    }

    /// <summary>全格铺统一成分的半区世界：Z≥0 为板 0，Z<0 为板 1（连续边界带，无无主格）。</summary>
    static H3PlateFields HalfWorld(float agePlate0, float agePlate1)
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        var centers = Ball.CellCenters;
        for (int c = 0; c < fields.Count; c++)
        {
            bool north = centers[c].Z >= 0f;
            fields.PlateId[c] = north ? 0 : 1;
            fields.ClearCell(c);
            fields.MaficVolcanic[c] = Material.MaficVolcanicMin * 7100f;
            fields.Age[c] = north ? agePlate0 : agePlate1;
        }
        return fields;
    }

    /// <summary>手工运动学状态：两板都起跳、指定格朝指定方向走（供平流直接消费）。</summary>
    static H3PlateMotion ManualMotion(int cellCount, params (int cell, Vector3 dir)[] flows)
    {
		var motion = new H3PlateMotion(cellCount)
		{
			PlateFired = Enumerable.Repeat(true, 8).ToArray(),   // 起跳相位钉在"本步都走"
		};
        foreach (var (cell, dir) in flows) motion.Velocity[cell] = dir * 0.01f;   // 方向即意图，量级随意
        return motion;
    }

    // ═══════════════════════════════════════════════════════════════
    // H3PlateContact.FlowNeighbor —— 落点选择
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void FlowNeighbor_PicksMaxTangentialProjection()
    {
        // 合成 3 格：中心格 + 东西两个邻居（中心在单位球 +X 上，切向 = YZ 平面）
        var centers = new[] { new Vector3(1, 0, 0), new Vector3(1, 0.1f, 0), new Vector3(1, 0, 0.1f) };
        var neighbors = new[] { new[] { 1, 2 }, new[] { 0 }, new[] { 0 } };
        Assert.AreEqual(1, H3PlateContact.FlowNeighbor(centers, neighbors, 0, new Vector3(0, 1, 0)), "朝 +Y 流 → 选东邻");
        Assert.AreEqual(2, H3PlateContact.FlowNeighbor(centers, neighbors, 0, new Vector3(0, 0, 1)), "朝 +Z 流 → 选北邻");
    }

    [Test]
    public void FlowNeighbor_SkipsRadialNeighbor_KeepsFirstOnTie()
    {
        // 邻居 1 纯径向（切向长度 ≈ 0，不可达）；邻居 2/3 与流向等夹角 → 平局保先到者（序固定）
        var centers = new[] { new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(1, 0.1f, 0.1f), new Vector3(1, 0.1f, -0.1f) };
        var neighbors = new[] { new[] { 1, 2, 3 }, new[] { 0 }, new[] { 0 }, new[] { 0 } };
        Assert.AreEqual(2, H3PlateContact.FlowNeighbor(centers, neighbors, 0, new Vector3(0, 1, 1)),
            "纯径向邻居跳过；2/3 等距平局取先到者");
    }

    // ═══════════════════════════════════════════════════════════════
    // H3PlateContact.JamsInto —— 俯冲/顶死判据
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void JamsInto_OnlyWhenNotDenserThanTarget()
    {
        var fields = new H3PlateFields(2);
        FillOldOceanic(fields, 0);                       // 来料：3300（老洋壳）
        FillOldOceanic(fields, 1);                       // 等密 → 顶死
        Assert.IsTrue(H3PlateContact.JamsInto(fields, Material, 0, 1), "等密相撞 = 顶死（严格更密才俯冲）");

        fields.ClearCell(1);                             // 目标换轻陆壳（2600）→ 俯冲
        fields.FelsicPlutonic[1] = Material.FelsicPlutonic * 35000f;
        Assert.IsFalse(H3PlateContact.JamsInto(fields, Material, 0, 1), "来料更密 = 俯冲，不顶死");

        fields.ClearCell(0);                             // 来料换轻陆壳（2600），目标老洋壳（3300）→ 顶死
        fields.FelsicPlutonic[0] = Material.FelsicPlutonic * 35000f;
        FillOldOceanic(fields, 1);
        Assert.IsTrue(H3PlateContact.JamsInto(fields, Material, 0, 1), "轻料撞重料 = 顶死停驻");
    }

    // ═══════════════════════════════════════════════════════════════
    // 运动学：顶死接触零功修正（剔除幻影驱动力 → 板速下降）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SolvePlateSpeeds_JamContactsExcludedFromNetForce_SlowsPlate()
    {
        // 世界 A（顶死）：两板同为老洋壳 → 边界接触互为等密 → 全部顶死剔除。
        // 世界 B（对照，俯冲）：板 0 老洋壳不动、板 1 换年轻洋壳（2890 < 来料）→ 板 0 全部俯冲、
        // 无剔除。两世界几何与板 0 完全相同，唯一差异 = 接触目标密度 → 差值即剔除效果。
        var fieldsJam = HalfWorld(OldAgeMy, OldAgeMy);
        var fieldsSub = HalfWorld(OldAgeMy, YoungAgeMy);
        var motionJam = new H3PlateMotion(Ball.CellIds.Length);
        var motionSub = new H3PlateMotion(Ball.CellIds.Length);

        motionJam.Step(Ball, fieldsJam, Material, 9.8f, 4f);
        motionSub.Step(Ball, fieldsSub, Material, 9.8f, 4f);

        // 顶死世界：剔除真实发生（存在接触带），幻影力占毛力一定比例；板 0 的幻影力非零
        Assert.Greater(motionJam.JamContactCellCount, 0, "等密双板世界应有顶死接触格");
        Assert.Greater(motionJam.PhantomForceFraction, 0f, "顶死格的拉力应计入幻影占比");
        Assert.Greater(motionJam.PlateJamContacts[0], 0, "板 0 边界带应存在顶死格");
        Assert.Greater(motionJam.PlatePhantomForceN[0], 0f);
        Assert.LessOrEqual(motionJam.PlatePhantomForceN[0], motionJam.PlateGrossForceN[0] + 1e-6f,
            "幻影力不得超过毛力");

        // 对照世界：目标全比来料轻 → 无顶死、无幻影力
        Assert.AreEqual(0, motionSub.JamContactCellCount, "俯冲对照世界不应有顶死接触");
        Assert.AreEqual(0f, motionSub.PhantomForceFraction, 1e-6f);
        Assert.AreEqual(0f, motionSub.PlatePhantomForceN[0], 1e-6f);

        // 净效果：同一块板，顶死世界解出的速度必须低于俯冲对照（剔除 ⇒ 净 F 变小 ⇒ v 变小）
        float speedJam = motionJam.PlateOmega[0].Length();
        float speedSub = motionSub.PlateOmega[0].Length();
        Assert.Greater(speedSub, 0f, "俯冲对照世界板 0 应保持驱动力（速度非零）");
        Assert.Less(speedJam, speedSub, "顶死剔除后板速必须下降");
    }

    [Test]
    public void SolvePlateSpeeds_PhaseStallContact_StillDrives()
    {
        // 相位停驻不是碰撞：全球一块板 → 无 foreign 接触，无论密度如何都不得剔除、不得计顶死；
        // 单板无板缘 → 边界法线全零 → 无驱动格 → 速度为零。
        var fields = HalfWorld(OldAgeMy, OldAgeMy);
        for (int c = 0; c < fields.Count; c++) fields.PlateId[c] = 0;
        var motion = new H3PlateMotion(Ball.CellIds.Length);
        motion.Step(Ball, fields, Material, 9.8f, 4f);

        Assert.AreEqual(0, motion.JamContactCellCount, "单板世界无 foreign 接触，不得计顶死");
        Assert.AreEqual(0f, motion.PhantomForceFraction, 1e-6f);
        Assert.AreEqual(0f, motion.PlateOmega[0].Length(), 1e-9f, "无板缘无驱动");
    }

    // ═══════════════════════════════════════════════════════════════
    // 平流：真接触顶死计数（foreign 才计）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Advection_ForeignJam_CountsAndStaysPut()
    {
        var (i, j) = AdjacentPair();
        var source = new H3PlateFields(Ball.CellIds.Length);
        FillOldOceanic(source, i);
        source.PlateId[i] = 0;
        FillOldOceanic(source, j);                       // 等密异板 → 顶死
        source.PlateId[j] = 1;

        var toJ = Ball.CellCenters[j] - Ball.CellCenters[i];
        var motion = ManualMotion(Ball.CellIds.Length, (i, toJ));
        var target = new H3PlateFields(Ball.CellIds.Length);
        var advection = new H3PlateAdvection(Ball.CellIds.Length);
        advection.Step(Ball, source, motion, target, Material, stepIndex: 0);

        Assert.AreEqual(1, advection.JamCellCount, "等密异板相撞计 1 次顶死");
        Assert.AreEqual(1, advection.JamCellsPerPlate[0], "顶死格按来料板记账");
        Assert.AreEqual(0, advection.HoleCount, "顶死驻留不产生空洞");
        Assert.AreEqual(0, advection.RecycledToMantleMass, 1e-6, "顶死不回地幔");
        Assert.AreEqual(0, target.PlateId[i], "顶死格原地驻留，归属不变");
        Assert.AreEqual(source.TotalMass(i), target.TotalMass(i), 1e-3, "顶死格物质原地保留");
        Assert.AreEqual(1, target.PlateId[j], "目标格归属不受顶死影响");
    }

    [Test]
    public void Advection_DenserHead_Subducts_NoJamCounted()
    {
        var (i, j) = AdjacentPair();
        var source = new H3PlateFields(Ball.CellIds.Length);
        FillOldOceanic(source, i);                       // 来料 3300
        source.PlateId[i] = 0;
        source.ClearCell(j);
        source.MaficVolcanic[j] = Material.MaficVolcanicMin * 7100f;   // 目标年轻 2890 → 更轻
        source.Age[j] = YoungAgeMy;
        source.PlateId[j] = 1;

        var toJ = Ball.CellCenters[j] - Ball.CellCenters[i];
        var motion = ManualMotion(Ball.CellIds.Length, (i, toJ));
        var target = new H3PlateFields(Ball.CellIds.Length);
        var advection = new H3PlateAdvection(Ball.CellIds.Length);
        advection.Step(Ball, source, motion, target, Material, stepIndex: 0);

        Assert.AreEqual(0, advection.JamCellCount, "更密链头俯冲，不顶死");
        Assert.AreEqual(1, advection.HoleCount, "俯冲链头腾空成空洞（待裂谷）");
        Assert.Greater(advection.RecycledToMantleMass, 0.0, "俯冲 mafic 记回地幔");
        Assert.AreEqual(1, target.PlateId[j], "埋入后顶层 = 目标板（不翻色）");
        Assert.AreEqual(1, advection.MixedCellCount, "埋入格 = 异板混合格");
    }

    [Test]
    public void Advection_PhaseStallIntoSamePlate_DoesNotCountJam()
    {
        var (i, j) = AdjacentPair();
        var source = new H3PlateFields(Ball.CellIds.Length);
        FillOldOceanic(source, i);
        source.PlateId[i] = 0;
        FillOldOceanic(source, j);
        source.PlateId[j] = 0;                           // 同板：j 未起跳 = 相位停驻，不是碰撞

        var toJ = Ball.CellCenters[j] - Ball.CellCenters[i];
        var motion = ManualMotion(Ball.CellIds.Length, (i, toJ));   // 只给 i 速度：j 零速驻留
        var target = new H3PlateFields(Ball.CellIds.Length);
        var advection = new H3PlateAdvection(Ball.CellIds.Length);
        advection.Step(Ball, source, motion, target, Material, stepIndex: 0);

        Assert.AreEqual(0, advection.JamCellCount, "链头撞同板驻留格是相位停驻，不得计顶死");
        Assert.AreEqual(0, advection.HoleCount, "驻留不产生空洞");
        Assert.AreEqual(0, target.PlateId[i], "驻留格归属不变");
        Assert.AreEqual(0, target.PlateId[j]);
    }

    // ═══════════════════════════════════════════════════════════════
    // 全链路 smoke：判读口接线 + 两步演化不炸（真初始分板）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void DynamicTectonics_Steps_SurfaceJamTelemetry()
    {
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(2, seed: 42);
        var sim = new H3DynamicTectonics(Ball) { RunMy = 8f, StepMy = 4f };
        sim.Initialize(plateOfCell, seed: 42);
        for (int s = 0; s < 2; s++) sim.Step();

        Assert.GreaterOrEqual(sim.JammedCellsLastStep, 0, "平流实测顶死口已接线");
        Assert.GreaterOrEqual(sim.JamContactCellsLastStep, 0, "运动学预测顶死口已接线");
        Assert.That(sim.PhantomForceFractionLastStep, Is.InRange(0f, 1f), "幻影占比 ∈ [0,1]");
        Assert.AreEqual(sim.Fields.Count, Ball.CellIds.Length);
    }
}
