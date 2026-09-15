using System;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;                  // MaterialDensity / Units（与生产同一密度表与时间单位）

namespace World.Tests;

/// <summary>
/// 地表过程（H3SurfaceProcesses，v1.13 自旧项目 M3 逐行移植）单元测试。
/// 断言的测量项：侵蚀沿边下坡搬运且均摊到各下坡邻、搬运量 = h差×降水×秒×系数×ρ 的解析值、
/// 守恒组总量严格不变（搬运/转化全在组内）、风化受沉积盖屏蔽（≥1 m 不风化）、
/// 成岩/变质的压力阈值换算、流水线整体质量账本守恒（演化 + 侵蚀同开）。
/// 纪律（同 H3PlateStaticTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3SurfaceProcessTests
{
    private const int Res = 1;          // 842 格，构造便宜
    private const int Plates = 4;
    private const int Seed = 42;
    private const float StepMy = 4f;

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;

    static readonly Lazy<MaterialDensity> SharedMaterial = new(() => new MaterialDensity());
    static MaterialDensity Material => SharedMaterial.Value;

    // 与实现同式：每米高差沿一条下坡边一个时间步的搬运量（kg/m²）——侵蚀的解析锚点
    static float EdgeTransferPerMeter(float stepMy)
        => H3SurfaceProcesses.PrecipMS * (stepMy * Units.MEGAYEAR)
           * H3SurfaceProcesses.ErosiveFactor * Material.FelsicPlutonic;

    static double GroupTotal(H3PlateFields f)
    {
        double sum = 0;
        foreach (var pool in f.ConservedPools())
            for (int i = 0; i < pool.Length; i++) sum += pool[i];
        return sum;
    }

    // ═══════════════════════════════════════════════════════════════
    // 侵蚀：下坡搬运 / 均摊 / 组内守恒
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Erosion_MovesDownhill_EvenlyToLowerNeighbors_GroupTotalInvariant()
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        int high = 0;
        var lowers = Ball.CellNeighbors[high];
        fields.FelsicPlutonic[high] = 1e8f;                // 存量 >> 出站总量 → 份额 clamp 到满（全给出站）
        var height = new float[Ball.CellIds.Length];
        height[high] = 3000f;                              // 唯一高格：所有边都是它向下

        // 全图铺 2000 kg/m² 沉积盖（≈1.3 m）：基岩暴露度处处为 0 → 风化关闭，本测试侵蚀独跑。
        //（否则高峰格同时被风化剥蚀，解析锚点被污染。）沉积池在侵蚀份额里占比 ~5.7e-5，可忽略。
        for (int i = 0; i < fields.Count; i++) fields.Sediment[i] = 2000f;

        double before = GroupTotal(fields);
        new H3SurfaceProcesses(Ball).Apply(fields, height, Material, StepMy, 1f);
        double after = GroupTotal(fields);

        Assert.That(after, Is.EqualTo(before).Within(before * 1e-5),
            "侵蚀只在守恒组内搬运：组总量必须严格不变");

        // 解析锚点：每条下坡边满额搬运 = h差 × 每米搬运率，均摊到全部下坡邻
        float perEdge = 3000f * EdgeTransferPerMeter(StepMy);
        Assert.That(fields.FelsicPlutonic[high],
            Is.EqualTo(1e8f - perEdge).Within(perEdge * 1e-3),
            "高格失量 = 出站总量（份额 clamp 到满）");
        foreach (int nb in lowers)
            Assert.That(fields.FelsicPlutonic[nb],
                Is.EqualTo(perEdge / lowers.Length).Within(perEdge * 1e-3),
                $"下坡邻格 {nb} 应均摊到每边满额搬运量的 1/邻数");
    }

    [Test]
    public void Erosion_FlatOrUphill_EdgesMoveNothing()
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        int a = 0;
        fields.FelsicPlutonic[a] = 2.6e7f;
        var height = new float[Ball.CellIds.Length];       // 全图等高 → 无下坡边
        height[a] = 0f;

        var surface = new H3SurfaceProcesses(Ball);
        surface.Apply(fields, height, Material, StepMy, 1f);

        Assert.AreEqual(2.6e7f, fields.FelsicPlutonic[a], 1f, "无下坡边 = 零侵蚀");
        Assert.AreEqual(0.0, surface.ErosionMovedMassLastStep, "无下坡边 → 判读口为 0");
    }

    // ═══════════════════════════════════════════════════════════════
    // 风化：出露基岩 → sediment；厚沉积盖屏蔽
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Weathering_ExposedBedrockWeathers_ThickSedimentCoverBlocks()
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        int exposed = 0, covered = Ball.CellIds.Length / 2;   // covered 远离高峰：不吃侵蚀输入
        fields.FelsicPlutonic[exposed] = 1e7f;             // 出露基岩
        fields.Sediment[covered] = 2000f;                  // > 1 m × ρ_sed(1500) = 盖住
        fields.FelsicPlutonic[covered] = 1e7f;
        var height = new float[Ball.CellIds.Length];
        height[exposed] = 1000f;                           // 制造非零平均高差（风化的粗糙度输入）

        var surface = new H3SurfaceProcesses(Ball);
        surface.Apply(fields, height, Material, StepMy, 1f);

        Assert.Greater(fields.Sediment[exposed], 0f, "出露基岩应风化出沉积物");
        Assert.AreEqual(surface.WeatheredMassLastStep, (double)fields.Sediment[exposed], 1f,
            "本场景唯一风化源 = exposed 格，判读口应等于其新增沉积物");
        Assert.AreEqual(1e7f, fields.FelsicPlutonic[covered], 1f,
            "沉积盖 ≥ 1 m 的格：基岩暴露度 = 0 → 不得风化");
    }

    // ═══════════════════════════════════════════════════════════════
    // 成岩 / 变质：压力阈值换算
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Lithification_ConvertsExcessOverThreshold()
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        int over = 0, under = 1;
        fields.Sediment[over] = 3e5f;                      // 柱压 2.94 MPa > 2.2 MPa
        fields.Sediment[under] = 1e5f;                     // 柱压 0.98 MPa < 阈值
        var height = new float[Ball.CellIds.Length];       // 全平 → 无侵蚀/风化干扰

        new H3SurfaceProcesses(Ball).Apply(fields, height, Material, StepMy, 1f);

        float expected = 3e5f - H3SurfaceProcesses.LithificationPressurePa / 9.8f;
        Assert.AreEqual(expected, fields.Sedimentary[over], expected * 1e-4f, "超压部分应压实成沉积岩");
        Assert.AreEqual(3e5f - expected, fields.Sediment[over], expected * 1e-4f, "沉积物等量减少");
        Assert.AreEqual(0f, fields.Sedimentary[under], 1f, "未超压格不得成岩");
    }

    [Test]
    public void Metamorphosis_ConvertsExcessOverThreshold_AndClampsToAvailable()
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        int over = 0;
        fields.Sedimentary[over] = 3.2e7f;                 // 柱压 3.14 MPa·1e2 = 313.6 MPa > 300 MPa
        var height = new float[Ball.CellIds.Length];

        new H3SurfaceProcesses(Ball).Apply(fields, height, Material, StepMy, 1f);

        float expected = 3.2e7f - H3SurfaceProcesses.MetamorphismPressurePa / 9.8f;
        Assert.AreEqual(expected, fields.Metamorphic[over], expected * 1e-4f, "超压的沉积岩应变质");
        Assert.AreEqual(3.2e7f - expected, fields.Sedimentary[over], expected * 1e-4f, "沉积岩等量减少");

        // 变质上限 = 现有 sedimentary（clamp）：巨厚沉积盖顶过柱压阈值，但可变质体远小于需求时
        // 不得变出超过存量的量。⚠️ 成岩在本步同样活动（巨厚 sediment 大量转 sedimentary），
        // net sedimentary 会涨，clamp 只能从 metamorphic（变质唯一出口）侧断言。
        var fields2 = new H3PlateFields(Ball.CellIds.Length);
        fields2.Sediment[over] = 5e7f;                     // 柱压顶过 300 MPa 阈值
        fields2.Sedimentary[over] = 1e6f;                  // 但可变质体远小于需求量
        new H3SurfaceProcesses(Ball).Apply(fields2, height, Material, StepMy, 1f);
        Assert.That(fields2.Metamorphic[over], Is.EqualTo(1e6f).Within(1e6f * 1e-4f),
            "变质被 clamp 到现有 sedimentary（需求 ~2e7 >> 存量 1e6）");
        Assert.GreaterOrEqual(fields2.Sedimentary[over], 0f, "sedimentary 不得变负");
    }

    // ═══════════════════════════════════════════════════════════════
    // 流水线集成（演化 + 侵蚀同开）：质量账本 + 无 NaN
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Pipeline_WithErosion_MassLedgerHolds_NoNaN()
    {
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball) { EnableErosion = true };
        sim.Initialize(plateOfCell, Seed);

        for (int s = 0; s < 3; s++) sim.Step();

        foreach (var pool in sim.Fields.AllPools())
            foreach (var v in pool)
                Assert.IsFalse(float.IsNaN(v), "地表过程后不得出现 NaN");

        double expected = sim.InitialCrustMass + sim.CrustCreatedTotal - sim.CrustDestroyedTotal;
        Assert.That(sim.TotalCrustMass(),
            Is.EqualTo(expected).Within(Math.Abs(expected) * 1e-6 + 1.0),
            "质量账本：总量 ≈ 初始 + 创建 − 消减（侵蚀零净额，不得破坏对账）");
        Assert.Greater(sim.Surface.ErosionMovedMassLastStep + sim.Surface.WeatheredMassLastStep, 0.0,
            "有陆有起伏的世界应存在侵蚀/风化活动（判读口恒 0 = 地表过程没接进流水线）");
    }
}
