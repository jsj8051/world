using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 板块生命周期（设计-04 批次 4）测试：缝合（segments 粒度）/ 裂解（继承式三分）/
/// 重启循环（场保留）/ 增生楔落地。
/// 手法：等密老洋壳双板世界 → 板缘负浮力互驱 → 全边界顶死（缝合的物理前置）；
/// 单板世界 → 失速 + 最大板占比 100%（裂解/重启的前置）。
/// 纪律（同 H3PlateStaticTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3DynamicCycleTests
{
    private const int Res = 1;
    private const float OldAgeMy = 300f;    // 密度 3300 > 地幔 → 负浮力驱动

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;
    static readonly MaterialDensity Material = new();

    /// <summary>三板碰撞世界：A = 北帽老洋壳（Y>0.5，~25%），B = 东南老洋壳，C = 西南年轻洋壳。
    /// A-C / B-C = 俯冲边（老→年轻，保持驱动力不锁死）；A-B = 等密碰撞边（缝合的物理前置）。
    /// 裂解阈值拉爆（本文件测缝合/增生，裂解另有专项测试）。</summary>
    static H3DynamicTectonics JamWorld(int sutureStreakSteps, int steps)
    {
        int n = Ball.CellIds.Length;
        var plateOfCell = new int[n];
        for (int i = 0; i < n; i++)
        {
            var c = Ball.CellCenters[i];
            plateOfCell[i] = c.Y > 0.5f ? 0 : (c.X >= 0f ? 1 : 2);
        }
        var sim = new H3DynamicTectonics(Ball)
        {
            RunMy = steps * 4f,
            StepMy = 4f,
            SutureStreakSteps = sutureStreakSteps,
            SupercontinentFraction = 0.9f,       // 压住裂解（专项测试另测）
            SupercontinentHardFraction = 0.99f,
        };
        sim.Initialize(plateOfCell, 42);
        for (int i = 0; i < n; i++)
        {
            bool old = plateOfCell[i] != 2;      // A/B 老洋壳（3300 致密驱动），C 年轻（2890 轻浮）
            sim.Fields.ClearCell(i);
            sim.Fields.MaficVolcanic[i] = Material.MaficVolcanicMin * 7100f;
            sim.Fields.Age[i] = old ? OldAgeMy : 0f;
            sim.Fields.PlateId[i] = plateOfCell[i];
        }
        sim.ResetMassAuditBaseline();               // 手工改场后重定基线（否则审计差是基线失真不是丢失）
        return sim;
    }

    [Test]
    public void Suture_SustainedJam_MergesPlates()
    {
        var sim = JamWorld(sutureStreakSteps: 2, steps: 0);
        int suturedAt = -1;
        for (int s = 0; s < 60; s++)
        {
            sim.Step();
            if (sim.SutureCount > 0 && suturedAt < 0) suturedAt = s + 1;
            if (sim.PlateCount == 2) break;
        }

        Assert.GreaterOrEqual(sim.SutureCount, 1, "A-B 等密碰撞边的累积顶死应触发缝合（segments 粒度合并）");
        Assert.AreEqual(2, sim.PlateCount, "三板世界缝合 A-B 后应剩两块（A-C/B-C 是俯冲边不缝合）");
        // 缝合不改物质：总质量与审计口径仍然成立
        double drift = sim.TotalCrustMass()
            - (sim.InitialCrustMass + sim.CrustCreatedTotal - sim.CrustDestroyedTotal);
        Assert.LessOrEqual(Math.Abs(drift) / sim.InitialCrustMass, 1e-6, "缝合只改归属不动物质");
    }

    [Test]
    public void Accretion_OverflowDepositedOntoOrogenicFront()
    {
        var sim = JamWorld(sutureStreakSteps: 999, steps: 0);   // 缝合资格拉爆（不干扰本测试）
        sim.MaxCrustThicknessM = 8000f;                          // 压低厚度帽：堆叠即溢流
        bool anyOverflow = false, anyDeposited = false;
        for (int s = 0; s < 40; s++)                             // 起跳是稀发事件，扫足窗口
        {
            sim.Step();
            if (sim.AccretionMassLastStep > 0) anyOverflow = true;
            if (sim.AccretionDepositedLastStep > 0) anyDeposited = true;
        }

        Assert.IsTrue(anyOverflow, "低厚度帽下碰撞堆叠应有溢流被缩");
        Assert.IsTrue(anyDeposited, "本板有顶死前缘时，溢流质量应堆回（增生楔落地，felsic 85/15）");
        double felsicTotal = 0;
        for (int i = 0; i < sim.Fields.Count; i++)
            felsicTotal += sim.Fields.FelsicPlutonic[i] + sim.Fields.FelsicVolcanic[i];
        Assert.Greater(felsicTotal, 0.0, "前缘应有长英质增生（大陆生长通道）");
    }

    [Test]
    public void OrogenCeiling_EqualsThicknessCapAiryEquivalent()
    {
        // "山有多高"**就是**厚度帽的 Airy 当量：陆海拔 = LandBaselineM + kAiry×(帽 − 陆参考厚)。
        // 这条把该关系钉住（v1.11 前的漂移正是它没被钉住：参考厚 28300 偏薄 + 帽 70000 ⇒ 实测饱和到 +7.2 km，
        // 而 02 §5.2 用户拍板的设计峰值是 +5.3 km / 峰值壳厚 60 km）。
        // ⚠️ v1.13 起侵蚀回来了（8b）：侵蚀会把质量搬进顶帽格造成**步内瞬时越帽**（下一步帽回收），
        // "每步扫描都 ≤ 帽"不再是精确不变量——故本测试显式关侵蚀，专测帽机制本身。
        const float cap = 45000f;                     // 压低帽：让"饱和"在几十步内出现（判据与帽值无关）
        var sim = JamWorld(sutureStreakSteps: 999, steps: 0);
        sim.MaxCrustThicknessM = cap;
        sim.EnableErosion = false;
        float airy = H3Isostasy.AiryFactor(Material);
        float ceiling = H3Isostasy.LandBaselineM + airy * (cap - H3Isostasy.LandReferenceThicknessM);

        float highest = float.MinValue;
        float thickest = 0f;
        int hottest = -1;
        for (int s = 0; s < 120; s++)
        {
            sim.Step();
            for (int i = 0; i < sim.Fields.Count; i++)
            {
                thickest = MathF.Max(thickest, sim.Fields.Thickness(i, Material));
                if (!sim.Fields.IsLand(i)) continue;
                float elevation = sim.Displacement[i] - sim.SeaLevel;
                if (elevation > highest) { highest = elevation; hottest = i; }
            }
        }
        float hotThickness = sim.Fields.Thickness(hottest, Material);
        float expected = H3Isostasy.LandBaselineM + airy * (hotThickness - H3Isostasy.LandReferenceThicknessM);
        Assert.LessOrEqual(thickest, cap + 1f, $"最厚格 {thickest:F0} m 越过厚度帽 {cap:F0} m（帽本身漏了）");
        Assert.Greater(highest, ceiling - 1200f,
            $"碰撞世界最高陆格 {highest:F0} m 应贴近厚度帽的 Airy 当量 {ceiling:F0} m（造山饱和）");
        Assert.LessOrEqual(highest, ceiling + 1f,
            $"最高陆格 {highest:F0} m 越过厚度帽的 Airy 当量 {ceiling:F0} m = 帽没管住山高"
            + $"［诊断：格 {hottest} 厚 {hotThickness:F0} m 年龄 {sim.Fields.Age[hottest]:F0} My 位移 {sim.Displacement[hottest]:F0} "
            + $"海平面 {sim.SeaLevel:F0} 按式应为 {expected:F0}（airy={airy:F6} ref={H3Isostasy.LandReferenceThicknessM:F0}）］");
    }

    [Test]
    public void Split_SinglePlateWorld_SplitsIntoThree()
    {
        // 三板世界 + 一个 60% 巨板（> 硬顶 35%）：巨板应被继承式三分，小板不动。
        int n = Ball.CellIds.Length;
        var plateOfCell = new int[n];
        for (int i = 0; i < n; i++)
        {
            float x = Ball.CellCenters[i].X;
            if (x > 0.2f) plateOfCell[i] = 0;                                  // 巨板（~60%）
            else plateOfCell[i] = Ball.CellCenters[i].Y >= 0f ? 1 : 2;         // 两个小板
        }
        var sim = new H3DynamicTectonics(Ball) { RunMy = 8f, StepMy = 4f };
        sim.Initialize(plateOfCell, 42);
        int splitFiredAt = -1;
        for (int s = 0; s < 2; s++)
        {
            sim.Step();
            if (sim.SplitPlateLastStep >= 0) splitFiredAt = s + 1;
        }

        Assert.GreaterOrEqual(sim.SplitCount, 1, "60% 巨板 > 硬顶 35% 应触发裂解");
        Assert.AreEqual(5, sim.PlateCount, "继承式三分：3 板 → 5 板（巨板一分为三）");
        Assert.GreaterOrEqual(splitFiredAt, 1, "判读口已接线（SplitPlateLastStep 是本步口，逐步捕获）");
    }

    [Test]
    public void Restart_StalledWorld_RepartitionsFieldsPreserved()
    {
        int n = Ball.CellIds.Length;
        var plateOfCell = new int[n];
        Array.Fill(plateOfCell, 0);
        var sim = new H3DynamicTectonics(Ball)
        {
            RunMy = 40f,
            StepMy = 4f,
            RestartStallSteps = 2,
            // 失速世界 = **全陆**（OceanFraction = 0）：没有可俯冲的洋岩石圈 ⇒ 无板片拉力 ⇒ 板均速 0。
            // ⚠️ 本测试原用"年轻洋壳（age 0）"构造失速——那是**旧口径的阈值伪影**（密度链要 age > 113 My
            // 才超过地幔才产驱动）；05-B 改逐长度口径后驱动 = N(age) ∝ √t，任何 age > 0 都产拉力（物理正确），
            // 且年龄每步 +4 My ⇒ 年轻洋壳世界一步后就动起来了。故改用"全陆"这个**物理上真的没驱动**的世界。
            OceanFraction = 0f,
        };
        sim.Repartition = k =>
        {
            var fresh = new int[n];
            for (int i = 0; i < n; i++) fresh[i] = Ball.CellCenters[i].X >= 0f ? 0 : 1;
            return fresh;
        };
        sim.Initialize(plateOfCell, 42);
        double massBefore = 0;
        for (int i = 0; i < n; i++) massBefore += sim.Fields.TotalMass(i);
        for (int s = 0; s < 10; s++) sim.Step();

        Assert.GreaterOrEqual(sim.RestartCount, 1, "失速持续应触发重启循环");
        Assert.AreEqual(2, sim.PlateCount, "重启 = 按 Repartition 重分板");
        double massAfter = sim.TotalCrustMass();
        Assert.AreEqual(massBefore, massAfter, Math.Max(1.0, massBefore * 1e-5),
            "重启只重分归属，物质场保留（场保留式 restart，platec/ testproject 同构）");
    }

    [Test]
    public void CycleStep_NoRepartition_RestartBypassed()
    {
        // 测试直构的 sim 无 Repartition：失速世界不重启（旁路语义——防误触发）
        int n = Ball.CellIds.Length;
        var plateOfCell = new int[n];
        Array.Fill(plateOfCell, 0);
        var sim = new H3DynamicTectonics(Ball) { RunMy = 80f, StepMy = 4f, RestartStallSteps = 2 };
        sim.Initialize(plateOfCell, 42);
        for (int s = 0; s < 20; s++) sim.Step();
        Assert.AreEqual(0, sim.RestartCount, "无 Repartition 委托时重启应旁路");
    }

    // ═══════════════════════════════════════════════════════════════
    // platec 式周期重启（05 §6.3 对齐 Mindwerks/plate-tectonics）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>全洋三板块世界（OceanFraction = 1 ⇒ 无陆 ⇒ **永无陆-陆碰撞**）+ 老洋壳（有驱动、会动）。
    /// 用于隔离验证 platec 的"久无陆碰撞"与"周期硬顶"两条触发条件。</summary>
    static H3DynamicTectonics AllOceanPlateWorld()
    {
        int n = Ball.CellIds.Length;
        var plateOfCell = new int[n];
        for (int i = 0; i < n; i++)
        {
            var c = Ball.CellCenters[i];
            plateOfCell[i] = c.Y > 0.5f ? 0 : (c.X >= 0f ? 1 : 2);
        }
        var sim = new H3DynamicTectonics(Ball)
        {
            RunMy = 2000f,
            StepMy = 4f,
            OceanFraction = 1f,        // 全洋：没有陆壳 ⇒ 碰撞永不记为"陆-陆碰撞"
            RestartStallSteps = 2,
        };
        sim.Repartition = k =>
        {
            var fresh = new int[n];
            for (int i = 0; i < n; i++) fresh[i] = Ball.CellCenters[i].X >= 0f ? 0 : 1;
            return fresh;
        };
        sim.Initialize(plateOfCell, 42);
        return sim;
    }

    // 只留"能量耗尽"主触发（其余全关）——用户 2026-09-15 定的语义：能量耗得差不多了才重启
    static void OnlyEnergyTrigger(H3DynamicTectonics sim, float ratio = 0.15f)
    {
        sim.RestartEnergyRatio = ratio;
        sim.RestartStallSpeedKmPerMy = 0f;          // 关（速度兜底）
        sim.RestartSupercontinentFraction = 2f;     // 关（超大陆，本模型自有）
        sim.RestartCycleSteps = 1_000_000;          // 关（周期硬顶）
        sim.RestartNoCollisionSteps = 0;            // 关（默认即关，见其注释）
    }

    // 只留 platec 的"久无陆碰撞"旋钮（默认关闭，本测试专门覆盖它）
    static void OnlyNoCollisionTrigger(H3DynamicTectonics sim)
    {
        sim.RestartEnergyRatio = 0f;               // 关（能量）
        sim.RestartStallSpeedKmPerMy = 0f;          // 关（速度）
        sim.RestartSupercontinentFraction = 2f;    // 关（超大陆）
        sim.RestartCycleSteps = 1_000_000;         // 关（周期硬顶）
        sim.RestartNoCollisionSteps = 3;           // 开
    }

    [Test]
    public void Restart_NoContinentalCollision_TriggersAtPlatecKnob()
    {
        var sim = AllOceanPlateWorld();
        OnlyNoCollisionTrigger(sim);
        for (int s = 0; s < 8; s++) sim.Step();

        Assert.GreaterOrEqual(sim.RestartCount, 1, "打开该旋钮后，久无陆-陆碰撞应触发重启（platec 语义）");
        Assert.AreEqual("久无陆碰撞", sim.LastRestartReason, "触发理由应逐字可读（判读口）");
        Assert.AreEqual(0, sim.ContinentalJamCellsLastStep, "全洋世界不应有陆-陆碰撞（前提保证）");
        Assert.LessOrEqual(sim.StepsSinceContinentalCollision, 8, "重启后计数应归零再累计");
    }

    [Test]
    public void Restart_EnergyDepleted_IsTheMainTrigger()
    {
        // 主触发语义（用户拍板）：**能量耗得差不多了才重启**。阈值 0.99 ⇒ 只要能量低于峰值 99%
        // 且连续 RestartStallSteps 步成立就重启（这里把连续要求降到 2 步以便快速验证接线）。
        var sim = AllOceanPlateWorld();
        OnlyEnergyTrigger(sim, ratio: 0.99f);
        sim.RestartStallSteps = 2;
        int steps = 0;
        while (sim.RestartCount == 0 && steps < 30) { sim.Step(); steps++; }

        Assert.GreaterOrEqual(sim.RestartCount, 1, "能量跌破阈值应按 platec 口径触发重启");
        Assert.AreEqual("动能衰减", sim.LastRestartReason);
        Assert.That(sim.MomentumRatioLastStep, Is.LessThan(0.99f + 1e-4f), "触发时能量比应低于阈值");
        Assert.LessOrEqual(sim.CycleStepCount, steps, "重启后周期步数应归零");

        // 对照：platec 原值 0.15 下，同一世界 30 步内不该触发（阈值语义 = 真的"耗尽"才重启）
        var control = AllOceanPlateWorld();
        OnlyEnergyTrigger(control, ratio: 0.15f);
        control.RestartStallSteps = 2;
        for (int s = 0; s < 30; s++) control.Step();
        Assert.AreEqual(0, control.RestartCount, "阈值 0.15 下 30 步内不应触发（能量远未耗尽）");
    }

    [Test]
    public void Restart_MomentumRatio_IsTrackedAndBounded()
    {
        var sim = AllOceanPlateWorld();
        for (int s = 0; s < 6; s++)
        {
            sim.Step();
            Assert.That(sim.MomentumRatioLastStep, Is.InRange(0f, 1f + 1e-4f),
                "能量比 ∈ (0,1]（platec systemKineticEnergy / peak_Ek）");
        }
    }

    [Test]
    public void Restart_CycleStepCap_TriggersPlatecCycleRestart()
    {
        var sim = AllOceanPlateWorld();
        OnlyEnergyTrigger(sim, ratio: 0f);          // 关能量（比值 < 0 不可能）
        sim.RestartStallSteps = 2;
        sim.RestartCycleSteps = 3;                  // 只留周期硬顶（platec RESTART_ITERATIONS）

        for (int s = 0; s < 6; s++) sim.Step();
        Assert.GreaterOrEqual(sim.RestartCount, 1, "周期步数超硬顶应触发重启（platec iter_count > RESTART_ITERATIONS）");
        Assert.AreEqual("周期届满", sim.LastRestartReason);
        Assert.LessOrEqual(sim.CycleStepCount, 6, "重启后周期步数应归零");
    }

    [Test]
    public void Restart_MaxCycles_LimitsRestartCount()
    {
        var sim = AllOceanPlateWorld();
        OnlyEnergyTrigger(sim, ratio: 0f);
        sim.RestartStallSteps = 2;
        sim.RestartCycleSteps = 2;
        sim.MaxRestartCycles = 1;                   // platec num_cycles/max_cycles：只允许重启一次
        for (int s = 0; s < 12; s++) sim.Step();
        Assert.AreEqual(1, sim.RestartCount, "周期数达上限后不得再重启（platec max_cycles 语义）");
    }
}
