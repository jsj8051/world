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
///   · JamsInto 判据：**需有长英质（陆壳）参与**（v1.17；洋-洋一律俯冲，03 §3.5）+ 不比目标密才顶死；
///     foreign 前提由调用方保证；
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

    /// <summary>年轻洋壳柱：纯 mafic、Age = 0 → 柱密度 = 2890（浮力恰 0，不驱动）。</summary>
    static void FillYoungOceanic(H3PlateFields fields, int cell)
    {
        fields.ClearCell(cell);
        fields.MaficVolcanic[cell] = Material.MaficVolcanicMin * 7100f;
        fields.Age[cell] = YoungAgeMy;
    }

    /// <summary>陆壳柱：纯长英质 35 km → 柱密度 = 2600（浮，不驱动）；`IsLand` = true（物质口径）。</summary>
    static void FillContinental(H3PlateFields fields, int cell)
    {
        fields.ClearCell(cell);
        fields.FelsicPlutonic[cell] = Material.FelsicPlutonic * 35000f;
        fields.Age[cell] = 0f;
    }

    /// <summary>把 <paramref name="plateA"/>/<paramref name="plateB"/> 的**接触带两侧**改写成陆壳
    /// （v1.17：顶死/造山/缝合只在"有陆壳参与"的接触上成立——03 §3.5 只有陆-陆是"两侧都停 + 增厚"，
    /// 洋-洋一律俯冲。旧 fixture 的"等密老洋壳双板 → 全边界顶死"前提在 v1.17 已作废）。
    /// 只动接触带一格：板其余部分保持致密老洋壳 ⇒ 驱动力/洋龄/面积口径与旧 fixture 一致。</summary>
    static void MakeContinentalContact(H3PlateFields fields, int plateA, int plateB)
    {
        int n = fields.Count;
        var mark = new bool[n];
        for (int c = 0; c < n; c++)
        {
            if (fields.PlateId[c] != plateA) continue;
            foreach (int nb in Ball.CellNeighbors[c])
                if (fields.PlateId[nb] == plateB) { mark[c] = true; break; }
        }
        for (int c = 0; c < n; c++)
        {
            if (fields.PlateId[c] != plateB) continue;
            foreach (int nb in Ball.CellNeighbors[c])
                if (fields.PlateId[nb] == plateA) { mark[c] = true; break; }
        }
        for (int c = 0; c < n; c++)
            if (mark[c]) FillContinental(fields, c);
    }

    /// <summary>边界带改造方式（v1.17 对照实验用；见 <see cref="HalfWorld"/>）。</summary>
    enum BandMode
    {
        /// <summary>不动（致密老洋壳 = 驱动）。</summary>
        OldOcean,
        /// <summary>改陆壳（可顶死；但陆壳无拉力 ⇒ 这些格退出驱动集）。</summary>
        Continental,
        /// <summary>改 age=0 年轻洋壳（**同样退出驱动集**，但不顶死）——顶死实验的对照必须用这一档：
        /// 两个世界的驱动集逐格一致，唯一差异才是"锁定"，否则拟合出的 ω 差的是驱动图案而不是力。</summary>
        YoungOcean,
    }

    /// <summary>全格铺统一成分的半区世界：Z≥0 为板 0，Z&lt;0 为板 1（连续边界带，无无主格）。
    /// v1.17：顶死（造山）只在"有陆壳参与"的接触上成立（03 §3.5），故"顶死世界 vs 俯冲世界"的对照
    /// 必须靠<see cref="BandMode"/> 且只在**板 0 边界带上 X&gt;0 那半**做文章——板 0 在 X&lt;0 那半
    /// 保持致密老洋壳 ⇒ 驱动力非零（两世界同款），对照才可比。</summary>
    static H3PlateFields HalfWorld(float agePlate0, float agePlate1, BandMode band = BandMode.OldOcean)
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
        if (band != BandMode.OldOcean)
        {
            for (int c = 0; c < fields.Count; c++)
            {
                if (fields.PlateId[c] != 0 || centers[c].X <= 0f) continue;
                bool touchesPlate1 = false;
                foreach (int nb in Ball.CellNeighbors[c])
                    if (fields.PlateId[nb] == 1) { touchesPlate1 = true; break; }
                if (!touchesPlate1) continue;
                fields.ClearCell(c);                                  // 板号不变（只换物质）
                if (band == BandMode.Continental)
                {
                    fields.FelsicPlutonic[c] = Material.FelsicPlutonic * 35000f;
                    fields.Age[c] = 1000f;
                }
                else
                {
                    fields.MaficVolcanic[c] = Material.MaficVolcanicMin * 7100f;
                    fields.Age[c] = YoungAgeMy;
                }
                fields.PlateId[c] = 0;
            }
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
    public void JamsInto_RequiresContinentalContact_AndNotDenserThanTarget()
    {
        // ⚠️ v1.17 口径（03 §3.5 规则表 + 02 §5.2："洋洋/洋陆俯冲 = 下盘传送带消减"）：
        //   顶死（= 造山）**只在有长英质（陆壳）参与的接触上**成立。旧判据只有密度比较
        //   ⇒ 年轻洋壳撞老洋壳/同龄洋壳也顶死堆厚，在海底堆出 +3~4 km 假山系（永久定格，不进侵蚀）。
        var oceans = new H3PlateFields(2);
        FillOldOceanic(oceans, 0);                       // 老洋壳 3300（> 地幔 → 负浮力驱动）
        FillYoungOceanic(oceans, 1);                     // 年轻洋壳 2890
        Assert.IsFalse(H3PlateContact.JamsInto(oceans, Material, 0, 1), "老洋壳撞年轻洋壳 = 更密 ⇒ 俯冲");
        Assert.IsFalse(H3PlateContact.JamsInto(oceans, Material, 1, 0), "年轻洋壳撞老洋壳 = 亦然俯冲（洋-洋不造山）");
        FillOldOceanic(oceans, 1);
        Assert.IsFalse(H3PlateContact.JamsInto(oceans, Material, 0, 1), "老洋壳撞老洋壳 = 等密仍俯冲（v1.17 关掉洋内造山）");

        var pair = new H3PlateFields(2);                 // 0 = 老洋壳，1 = 陆壳
        FillOldOceanic(pair, 0);
        FillContinental(pair, 1);
        Assert.IsFalse(H3PlateContact.JamsInto(pair, Material, 0, 1), "洋撞陆 = 目标更轻但来料更密 ⇒ 俯冲（下盘传送带）");
        Assert.IsTrue(H3PlateContact.JamsInto(pair, Material, 1, 0), "陆撞洋 = 较浮的陆侧 = 上盘 = 顶死停驻（03 §3.5）");

        var continents = new H3PlateFields(2);
        FillContinental(continents, 0);
        FillContinental(continents, 1);
        Assert.IsTrue(H3PlateContact.JamsInto(continents, Material, 0, 1), "陆-陆等密相撞 = 顶死（两侧都停 + 增厚）");
    }

    // ═══════════════════════════════════════════════════════════════
    // 运动学：顶死接触零功修正（剔除幻影驱动力 → 板速下降）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SolvePlateSpeeds_JamContactsExcludedFromNetForce_SlowsPlate()
    {
        // 世界 A（顶死）：板 0 边界带 X>0 那半是陆壳、板 1 是致密老洋壳 → 该段接触满足
        //   "有陆壳参与 + 来料不比目标密" ⇒ 锁定（顶死）。世界 B（对照，俯冲）：同一批格改
        //   **age=0 年轻洋壳** ⇒ 同样退出驱动集（age>0 才驱动）但不顶死 ⇒ 两世界驱动集**逐格一致**，
        //   唯一差异是"锁定"。（⚠️ 不能用"全老洋壳"当对照：那样板 0 整个环都在驱动，驱动图案变了，
        //   刚体拟合出的 ω 差的是图案不是力——v1.17 实测过这个坑。）
        var fieldsJam = HalfWorld(OldAgeMy, OldAgeMy, BandMode.Continental);
        var fieldsSub = HalfWorld(OldAgeMy, OldAgeMy, BandMode.YoungOcean);
        var motionJam = new H3PlateMotion(Ball.CellIds.Length);
        var motionSub = new H3PlateMotion(Ball.CellIds.Length);

        motionJam.Step(Ball, fieldsJam, Material, 9.8f, 4f);
        motionSub.Step(Ball, fieldsSub, Material, 9.8f, 4f);

        // 顶死世界：剔除真实发生（存在陆-洋接触带），幻影力占毛力一定比例；板 0 的幻影力非零
        Assert.Greater(motionJam.JamContactCellCount, 0, "陆壳接触带应有顶死接触格");
        Assert.Greater(motionJam.PhantomForceFraction, 0f, "顶死格的拉力应计入幻影占比");
        Assert.Greater(motionJam.PlateJamContacts[0], 0, "板 0 陆壳边界带应存在顶死格");
        Assert.Greater(motionJam.PlatePhantomForceN[0], 0f);
        Assert.LessOrEqual(motionJam.PlatePhantomForceN[0], motionJam.PlateGrossForceN[0] + 1e-6f,
            "幻影力不得超过毛力");

        // 对照世界：全洋-洋汇聚（一律俯冲）→ 无顶死、无幻影力
        Assert.AreEqual(0, motionSub.JamContactCellCount, "洋-洋俯冲对照世界不应有顶死接触");
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
        // v1.17：顶死（造山）走**陆-陆**接触（03 §3.5）——旧 fixture 用"等密老洋壳异板"，
        // 那条通道已按设计关闭（洋-洋一律俯冲），故本测试改用两块陆壳。
        var (i, j) = AdjacentPair();
        var source = new H3PlateFields(Ball.CellIds.Length);
        FillContinental(source, i);
        source.PlateId[i] = 0;
        FillContinental(source, j);                      // 等密（皆 2600）异板 → 顶死
        source.PlateId[j] = 1;

        var toJ = Ball.CellCenters[j] - Ball.CellCenters[i];
        var motion = ManualMotion(Ball.CellIds.Length, (i, toJ));
        var target = new H3PlateFields(Ball.CellIds.Length);
        var advection = new H3PlateAdvection(Ball.CellIds.Length);
        advection.Step(Ball, source, motion, target, Material, stepIndex: 0);

        Assert.AreEqual(1, advection.JamCellCount, "陆-陆等密异板相撞计 1 次顶死");
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
