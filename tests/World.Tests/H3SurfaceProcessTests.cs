using System;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;                  // MaterialDensity / Units（与生产同一密度表与时间单位）

namespace World.Tests;

/// <summary>
/// 地表过程（H3SurfaceProcesses，v1.13 自旧项目 M3 逐行移植；v1.23 三介质口径）单元测试。
/// 断言的测量项：侵蚀沿边下坡搬运且均摊到各下坡邻、参考态 = 重力通道解析锚点（v1.23 水退役）、
/// 守恒组总量严格不变（搬运/转化全在组内）、风化受沉积盖屏蔽（≥1 m 不风化）、
/// 成岩/变质的压力阈值换算、重力门槛松散/固结分池、流水线整体质量账本守恒（演化 + 侵蚀同开）。
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

    /// <summary>本模型的**重力介质**当量落差（m）：超过门槛（本测试的柱都是基岩主导 ⇒ 门槛
    /// ≈ `GravityReliefSolidM`）的部分 × 系数。v1.20：重力不需要介质 ⇒ 裸调 `Apply`（参考态）也生效。</summary>
    static float GravityEquivalentRelief(float dropM)
        => H3SurfaceProcesses.GravityErodibility
           * MathF.Max(0f, dropM - H3SurfaceProcesses.GravityReliefSolidM);

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

        // 解析锚点（v1.23 三介质）：参考态（无介质场）下风/冰川率 = 0 ⇒ 侵蚀只剩**重力通道**——
        // 3000 m 落差 > 门槛（1500）⇒ 出站当量 = 3×(3000−1500) = 4500 m（坡面水搬运已在河道里）。
        float reliefEquivalent = GravityEquivalentRelief(3000f);
        float perEdge = reliefEquivalent * EdgeTransferPerMeter(StepMy);
        Assert.That(fields.FelsicPlutonic[high],
            Is.EqualTo(1e8f - perEdge).Within(perEdge * 1e-3),
            $"高格失量 = 出站总量（份额 clamp 到满）：当量落差 {reliefEquivalent:F0} m");
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

        // 变质上限 = 现有**剩余** sedimentary（clamp）：巨厚沉积盖顶过柱压阈值，但可变质体远小于需求时
        // 不得变出超过存量的量。⚠️ 成岩在本步同样活动（巨厚 sediment 大量转 sedimentary）⇒ 变质的**上界
        // 必须看见成岩刚加进来的那批**（v1.20 修：旧实现读池原值 ⇒ 上界被算成 1e6 而不是"1e6 + 本步成岩量"，
        // 白丢一步的变质）。故期望值按两步串联解析算：先成岩、再变质。
        var fields2 = new H3PlateFields(Ball.CellIds.Length);
        fields2.Sediment[over] = 5e7f;                     // 柱压顶过 300 MPa 阈值
        fields2.Sedimentary[over] = 1e6f;                  // 但可变质体小于需求（需求 ~2e7）
        new H3SurfaceProcesses(Ball).Apply(fields2, height, Material, StepMy, 1f);

        float lithified = 5e7f - H3SurfaceProcesses.LithificationPressurePa / 9.8f;   // 成岩量（clamp 到 sediment）
        float sedimentaryNow = 1e6f + lithified;                                   // 成岩刚加进来的
        float demand = (5e7f + 1e6f) * 9.8f - H3SurfaceProcesses.MetamorphismPressurePa;
        Assert.That(fields2.Metamorphic[over],
            Is.EqualTo(demand / 9.8f).Within(demand / 9.8f * 1e-4f),
            $"变质被 clamp 到**本步剩余** sedimentary（{sedimentaryNow:E2}）：需求 {demand / 9.8f:E2}");
        Assert.GreaterOrEqual(fields2.Sedimentary[over], 0f, "sedimentary 不得变负");
        Assert.GreaterOrEqual(fields2.Sediment[over], 0f, "sediment 不得变负");
    }

    // ═══════════════════════════════════════════════════════════════
    // 四介质门（v1.20）：水 / 风 / 重力 / 冰川——侵蚀必须"有介质"
    // ═══════════════════════════════════════════════════════════════

    /// <summary>单峰地形（one 高格 + 其余 0）：所有边都从高格向下 ⇒ 侵蚀只发生在它身上。
    /// 全图铺 2000 kg/m² 沉积盖 ⇒ 风化关闭（本组测试只看侵蚀）。</summary>
    static (H3PlateFields fields, float[] height) SinglePeak(float peakM)
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        for (int i = 0; i < fields.Count; i++) fields.Sediment[i] = 2000f;
        fields.FelsicPlutonic[0] = 1e8f;
        var height = new float[Ball.CellIds.Length];
        height[0] = peakM;
        return (fields, height);
    }

    static float[] Uniform(float value)
    {
        var a = new float[Ball.CellIds.Length];
        Array.Fill(a, value);
        return a;
    }

    [Test]
    public void Medium_WaterRetiredFromErosion_WindAndGravityRemain()
    {
        // **v1.23 拍板："水蚀只有河流"**——坡面扩散没有水介质：无论旱涝，侵蚀介质只有 风/重力/冰川。
        // 干世界（P = 0）里干燥度 = 1 ⇒ 风介质满档；水率（只进**风化门**）严格为 0。
        var (fields, height) = SinglePeak(3000f);
        var surface = new H3SurfaceProcesses(Ball);
        surface.Apply(fields, height, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(0f), Uniform(20f), Uniform(12f)));

        Assert.AreEqual(0f, surface.WaterRatePerCell[0], 1e-6f, "零降水 → 水率（风化门）为 0");
        Assert.Greater(surface.WindRatePerCell[0], 0f, "干旱度 1 + 12 m/s ⇒ 风介质接管");
        Assert.AreEqual(0f, surface.GlacierRatePerCell[0], 1e-6f, "无水 ⇒ 无雪 ⇒ 无冰蚀");
        Assert.Greater(surface.WindMovedMassLastStep, 0.0, "坡面风介质应搬运");
        Assert.Greater(surface.GravityMovedMassLastStep, 0.0, "3000 m 落差 > 门槛 ⇒ 重力也参与（不需要介质）");
        Assert.AreEqual(surface.ErosionMovedMassLastStep,
            surface.WindMovedMassLastStep + surface.GravityMovedMassLastStep, 1.0,
            "侵蚀介质之和 = 总搬运量（三介质记账口径，水不在其中）");
    }

    [Test]
    public void Medium_WindSpeedFieldDirectRead_StrongerWindErodesMore()
    {
        // v1.22 拍板项兑现：风介质直读逐格风速 |u|（÷ `H3Wind.ReferenceWindSpeedMS` 归一）——
        // 同一降水/温度、只有风速不同 ⇒ 风介质率随风速走；Ferrel 海平面档（12 m/s）必须精确
        // 回到旧参考权重 1.0（与 v1.20 环流带档位同锚，平滑接管）。
        // 干世界口径（P = 0 ⇒ 干燥度 1、裸露度 1）隔离干燥度门，只看风速。
        var height = Uniform(0f);
        height[0] = 3000f;

        var fields = new H3PlateFields(Ball.CellIds.Length);
        for (int i = 0; i < fields.Count; i++) fields.Sediment[i] = 2000f;
        fields.FelsicPlutonic[0] = 1e8f;
        var surface = new H3SurfaceProcesses(Ball);
        surface.Apply(fields, height, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(0f), Uniform(20f), Uniform(12f)));

        float expectedFerrel = H3Wind.ReferenceWindSpeedMS / H3Wind.ReferenceWindSpeedMS
            * H3SurfaceProcesses.WindErodibility;   // 风强 1.0 × 干燥度 1 × 裸露 1 × 0.5
        Assert.AreEqual(expectedFerrel, surface.WindRatePerCell[0], 1e-3f,
            "12 m/s（Ferrel 海平面档）应精确回到参考风强 1.0 的权重（旧档位同锚）");

        var slowFields = new H3PlateFields(Ball.CellIds.Length);
        for (int i = 0; i < slowFields.Count; i++) slowFields.Sediment[i] = 2000f;
        slowFields.FelsicPlutonic[0] = 1e8f;
        var slow = new H3SurfaceProcesses(Ball);
        slow.Apply(slowFields, height, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(0f), Uniform(20f), Uniform(3f)));

        Assert.Greater(surface.WindRatePerCell[0], slow.WindRatePerCell[0],
            "12 m/s 的风蚀必须强于 3 m/s（风介质直读 |u|）");
        Assert.Greater(slow.WindRatePerCell[0], 0f, "3 m/s 弱风也应有非零风介质率（线性于 |u|）");
    }

    [Test]
    public void Medium_AridCellHasZeroRunoff_WetCellRunsOff_WindFillsTheGap()
    {
        // **同一趟里的干湿对照**（v1.21 产流口径）：干格（P = 0）产流**严格 0** ⇒ 水介质 0（不是"很小"），
        // 风介质顶上来；湿格（P = 1800、T = 20°C）产流 ≈ 0.39P ⇒ 水介质大、而风被"干燥度 × 裸露度"压住。
        var fields = new H3PlateFields(Ball.CellIds.Length);
        for (int i = 0; i < fields.Count; i++) fields.Sediment[i] = 2000f;
        int dry = 0, wet = 1;
        fields.FelsicPlutonic[dry] = 1e8f;
        fields.FelsicPlutonic[wet] = 1e8f;
        var height = new float[Ball.CellIds.Length];
        height[dry] = 3000f;
        height[wet] = 1000f;
        var precip = new float[Ball.CellIds.Length];
        precip[dry] = 0f;
        precip[wet] = 1800f;
        var temp = Uniform(20f);

        var surface = new H3SurfaceProcesses(Ball);
        surface.Apply(fields, height, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(precip, temp));

        // 干格：产流 0 ⇒ 水介质**严格 0**；干燥度 1 ⇒ 风介质满
        Assert.AreEqual(0f, H3Climate.RunoffMmPerYear(0f, 20f), 1e-6f,
            "P = 0 ⇒ 产流严格 0（\"没水就没水蚀\"是物理结论）");
        Assert.AreEqual(0f, surface.WaterRatePerCell[dry], 1e-6f, "干格水介质必须为 0");
        Assert.AreEqual(1f - H3Climate.AridityIndex(0f, 20f), 1f, 1e-6f, "P = 0 ⇒ 干燥度 1");
        Assert.Greater(surface.WindRatePerCell[dry], 0f, "干格：干燥度 1 ⇒ 风蚀接管");

        // 湿格：水率 = 产流/参考（解析锚点；v1.23 起只进**风化门**），风被压住
        float expectedWet = Math.Clamp(H3Climate.RunoffMmPerYear(1800f, 20f)
            / H3SurfaceProcesses.RunoffRefMmPerYear, 0f, H3SurfaceProcesses.WaterWeightCap);
        Assert.That(surface.WaterRatePerCell[wet], Is.EqualTo(expectedWet).Within(1e-4f),
            "湿格水率 = 产流 / 参考产流（467.5 mm/yr；风化门口径）");
        Assert.Greater(surface.WaterRatePerCell[wet], surface.WaterRatePerCell[dry] * 10f,
            "湿格水率应远大于干格（干格严格 0）");
        Assert.Less(surface.WindRatePerCell[wet], surface.WindRatePerCell[dry],
            "湿格的风介质必须低于干格（干燥度与裸露度都低）");
        Assert.Greater(surface.WindMovedMassLastStep, 0.0, "干格应有风搬运（风蚀在这里是主力）");
        Assert.AreEqual(surface.ErosionMovedMassLastStep,
            surface.WindMovedMassLastStep
            + surface.GravityMovedMassLastStep + surface.GlacierMovedMassLastStep, 1.0,
            "侵蚀介质之和 = 总搬运量（水不在侵蚀侧——v1.23 水蚀只有河流）");
    }

    [Test]
    public void Medium_PetControlsWaterGate_SameRain_ColderMeansLessAridityButAlsoLessRunoff()
    {
        // PET 是水率的**唯一标定旋钮**（`H3Climate.PetRefMmPerYear`）：同样的雨、不同的温度 ⇒
        // 产流不同（`R = P(1 − 1/√(1+(P/PET)²))`）⇒ 风化门的水率不同；干燥度 = `1 − AI` 也随温度走。
        // （v1.23：水率不再进坡面侵蚀——水蚀只有河流；这里验证的是风化门与干燥度联动。）
        var (coldFields, coldHeight) = SinglePeak(3000f);
        var cold = new H3SurfaceProcesses(Ball);
        cold.Apply(coldFields, coldHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(600f), Uniform(-10f)));   // PET(−10) = 175

        var (warmFields, warmHeight) = SinglePeak(3000f);
        var warm = new H3SurfaceProcesses(Ball);
        warm.Apply(warmFields, warmHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(600f), Uniform(25f)));    // PET(25) = 1979

        float runoffCold = H3Climate.RunoffMmPerYear(600f, -10f);   // P ≫ PET ⇒ 产流 ≈ P
        float runoffWarm = H3Climate.RunoffMmPerYear(600f, 25f);    // P < PET ⇒ 产流小
        Assert.Greater(runoffCold, runoffWarm * 3f,
            $"同雨量下冷区产流应远大于热区（冷 {runoffCold:F0} vs 热 {runoffWarm:F0} mm/yr）");
        Assert.Greater(cold.WaterRatePerCell[0], warm.WaterRatePerCell[0],
            "冷区水率高于热区（PET 低 ⇒ 蒸散少 ⇒ 产流多；风化门口径）");
        Assert.Greater(warm.WindRatePerCell[0], cold.WindRatePerCell[0],
            "热区更干 ⇒ 风蚀更强（与风蚀互补）");
    }

    [Test]
    public void Medium_RockErodibility_SoftBasinErodesFasterThanHardShield()
    {
        // 岩性可蚀性 K（v1.21）= 柱内软/硬物质厚度占比：软（沉积物/沉积岩）⇒ K > 1（盆地易被冲开），
        // 硬（变质岩）⇒ K < 1（盾地留住高地）。K 只乘水/风（力学搬运与岩性一阶无关）。
        var (softFields, softHeight) = SinglePeak(3000f);
        for (int i = 0; i < softFields.Count; i++)
        {
            softFields.Sediment[i] = 0f;
            softFields.Sedimentary[i] = 2600f * 20000f;          // 20 km 沉积岩（软）
        }
        var soft = new H3SurfaceProcesses(Ball);
        soft.BindFieldsForK(softFields);
        soft.Apply(softFields, softHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(900f), Uniform(20f)));

        var (hardFields, hardHeight) = SinglePeak(3000f);
        for (int i = 0; i < hardFields.Count; i++)
        {
            hardFields.Sediment[i] = 0f;
            hardFields.Metamorphic[i] = 2800f * 20000f;          // 20 km 变质岩（硬）
        }
        var hard = new H3SurfaceProcesses(Ball);
        hard.BindFieldsForK(hardFields);
        hard.Apply(hardFields, hardHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(900f), Uniform(20f)));

        Assert.Greater(soft.RockErodibilityPerCell[0], 1f, "软柱（沉积岩）⇒ K > 1");
        Assert.Less(hard.RockErodibilityPerCell[0], 1f, "硬柱（变质岩）⇒ K < 1");
        Assert.Greater(soft.WindMovedMassLastStep, hard.WindMovedMassLastStep,
            "K 乘风介质 ⇒ 软盆地被坡面风搬运的量应大于硬盾地（侵蚀侧介质只剩风/重力/冰川）");
    }

    [Test]
    public void Medium_ColdWorld_GlacierErosionTurnsOn_AndIsStrongerThanWater()
    {
        // 冰川门 = **低温 + 有降水（雪）**两个条件：−10°C 且有降水 ⇒ 冰蚀开（系数 2× 同条件水蚀）；
        // 同一场温度改 +10°C ⇒ 冰蚀严格为 0（门是温度）。
        var (coldFields, coldHeight) = SinglePeak(3000f);
        var cold = new H3SurfaceProcesses(Ball);
        cold.Apply(coldFields, coldHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(900f), Uniform(-10f)));

        Assert.That(cold.GlacierRatePerCell[0],
            Is.EqualTo(2f * Math.Clamp(900f / H3SurfaceProcesses.PrecipRefMmYear, 0f, 1f)).Within(1e-4f),
            "−10°C + 降水 900 ⇒ 冰介质率 = 冰川系数 × 积雪权重（min(P/1050, 1)）");
        Assert.Greater(cold.GlacierMovedMassLastStep, 0.0, "冷世界应有冰蚀搬运");
        Assert.Greater(cold.GlacierMovedMassLastStep, cold.WindMovedMassLastStep,
            "−10°C 湿冷世界：冰蚀系数 2× 且风被干燥度压死 ⇒ 冰搬运量应远大于风搬运量");

        var (warmFields, warmHeight) = SinglePeak(3000f);
        var warm = new H3SurfaceProcesses(Ball);
        warm.Apply(warmFields, warmHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(900f), Uniform(10f)));
        Assert.AreEqual(0f, warm.GlacierRatePerCell[0], 1e-6f, "+10°C ⇒ 无冰川（门是年均温 < 0）");
        Assert.AreEqual(0.0, warm.GlacierMovedMassLastStep, 1.0, "+10°C 世界不得有冰蚀搬运");
    }

    [Test]
    public void Medium_SteepEdge_GravityActs_RegardlessOfClimate()
    {
        // 重力介质 = 不需要介质的通道：低于门槛的落差完全没有它，超过门槛后按系数放大；
        // 且**无水世界**里它照样工作（与"水蚀需要水"形成对照）。
        var (gentle, gentleH) = SinglePeak(H3SurfaceProcesses.GravityReliefSolidM);   // 恰好 = 固结档门槛
        gentle.Sediment[0] = 0f;                           // 清空峰格沉积铺盖 ⇒ 纯基岩柱（门槛恰 1500，不被插值拉动）
        var a = new H3SurfaceProcesses(Ball);
        a.Apply(gentle, gentleH, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(0f), Uniform(20f)));
        Assert.AreEqual(0.0, a.GravityMovedMassLastStep, 1.0, "落差 = 固结档门槛 ⇒ 重力不触发（基岩柱）");

        var (steep, steepH) = SinglePeak(H3SurfaceProcesses.GravityReliefSolidM + 1000f);
        steep.Sediment[0] = 0f;                            // 同上：峰格纯基岩，门槛不被松散占比拉动
        var b = new H3SurfaceProcesses(Ball);
        b.Apply(steep, steepH, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(0f), Uniform(20f)));
        Assert.Greater(b.GravityMovedMassLastStep, 0.0, "落差超门槛 ⇒ 重力搬运（无水世界也有）");
        // 超出门槛的部分按 GravityErodibility 放大：1000 × 3 = 3000 m 当量 ≈ 整段门槛的落差
        Assert.That(b.GravityMovedMassLastStep / b.ErosionMovedMassLastStep, Is.GreaterThan(0.4),
            "3000 m 当量重力 / (风 0.4×2000 + 重力 3000) ⇒ 应占大头");
    }

    [Test]
    public void Medium_AbsoluteWaterAmountEntersWaterGate_NoNormalizationFork()
    {
        // **"世界有多湿"必须真的进水率**（v1.21 定稿口径；v1.23 起水率只进**风化门**——坡面水蚀
        // 已归河道，绝对水量对侵蚀的作用在 H3FluvialTransport 的绝对流量 Q 里）。
        // 反面教材 = v1.20 的归一档：权重 = 降水/本行星域均值 ⇒ 只有地球 1/3 雨量的世界照样按
        // 地球强度风化（绝对水量被抹掉——用户 2026-09-18 点名的"无中生有"）。
        var (wetFields, wetHeight) = SinglePeak(3000f);
        var wet = new H3SurfaceProcesses(Ball);
        wet.Apply(wetFields, wetHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(1200f), Uniform(20f)));

        var (dryFields, dryHeight) = SinglePeak(3000f);
        var dry = new H3SurfaceProcesses(Ball);
        dry.Apply(dryFields, dryHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(400f), Uniform(20f)));

        Assert.Greater(wet.WaterRatePerCell[0], dry.WaterRatePerCell[0] * 2f,
            "降水 1200 vs 400（同温）⇒ 水率应差 2 倍以上（绝对水量进强度）");
        Assert.That(dry.PrecipDomainMeanMmYearLastStep, Is.EqualTo(400f).Within(1f),
            "判读口给出实测域均值降水（不再参与任何归一化）");
        Assert.Greater(dry.RunoffDomainMeanMmYearLastStep, 0f, "半干旱世界仍有产流（但明显少于湿润世界）");
        Assert.Less(dry.RunoffDomainMeanMmYearLastStep, dry.PrecipDomainMeanMmYearLastStep,
            "产流必须小于降水（蒸散扣掉了）");
    }

    [Test]
    public void Medium_GravityThresholdLooseColumnSlidesAtLowerRelief()
    {
        // v1.23 重力分池：门槛按柱内松散质量占比在 300–1500 m 间插值——纯松散柱 800 m 落差就滑，
        // 同落差基岩柱纹丝不动（真实滑坡几乎都发生在松散堆积体上）。
        var looseFields = new H3PlateFields(Ball.CellIds.Length);
        looseFields.Sediment[0] = 1e6f;                    // 纯松散柱（looseFrac = 1 ⇒ 门槛 300 m）
        var looseHeight = new float[Ball.CellIds.Length];
        looseHeight[0] = 800f;
        var loose = new H3SurfaceProcesses(Ball);
        loose.Apply(looseFields, looseHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(0f), Uniform(20f)));
        Assert.Greater(loose.GravityMovedMassLastStep, 0.0, "纯松散柱：800 m > 松散档 300 m ⇒ 应滑");

        var solidFields = new H3PlateFields(Ball.CellIds.Length);
        solidFields.FelsicPlutonic[0] = 1e6f;              // 纯基岩柱（门槛 1500 m）
        var solidHeight = new float[Ball.CellIds.Length];
        solidHeight[0] = 800f;
        var solid = new H3SurfaceProcesses(Ball);
        solid.Apply(solidFields, solidHeight, Material, StepMy, 1f,
            new H3SurfaceProcesses.ErosionMedium(Uniform(0f), Uniform(20f)));
        Assert.AreEqual(0.0, solid.GravityMovedMassLastStep, 1.0, "基岩柱：800 m < 固结档 1500 m ⇒ 不动");
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
