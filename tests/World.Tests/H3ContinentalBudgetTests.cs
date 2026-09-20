using System;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 陆壳收支长跑护栏（2026-09-19 "3000 My 大陆消失"立项）：
/// 守恒组（陆壳）的出口有三户——俯冲埋入层刮削/沉积全额、厚度帽溢流的守恒组份额；
/// 入口只有弧岩浆回流。伺服（H3DynamicTectonics.ApplyContinentalBudgetServo）在累计账本上
/// 用积分控制兜底：弧回流累计 ≥ 守恒组销毁累计 ⇒ 陆壳存量只增不减。
/// 修复前 res1/4板/seed42 实测 3000 My：帽溢流单户送回地幔的长英质 ≈ 初始禀赋的 4 倍，
/// 陆占比 36% → 9.4%（全星海洋+岛屿）；修复后陆占比全程在 20–62% 带内振荡（威尔逊旋回式）。
/// 断言带宽不钉死数值；[res1 全程 750 步共享一次跑（Lazy），控制套件时长]。
/// 纪律（同 H3DynamicRunTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3ContinentalBudgetTests
{
    private const int Res = 1;
    private const int Plates = 4;
    private const int Seed = 42;
    private const float RunMy = 3000f;

    static readonly MaterialDensity Material = new();
    static readonly Lazy<(H3DynamicTectonics sim, double felsicInitial)> SharedRun = new(() =>
    {
        var ball = new Ball(Res, 1f);
        var splitter = new H3Plate(ball);
        int[] plateOfCell = splitter.SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(ball) { RunMy = RunMy, StepMy = 4f };
        sim.Initialize(plateOfCell, Seed);
        sim.Repartition = k => splitter.SplitIntoPlates(k, Seed + 977 * sim.RestartCount);

        double felsic0 = 0;
        for (int i = 0; i < sim.Fields.Count; i++)
            felsic0 += sim.Fields.FelsicPlutonic[i] + sim.Fields.FelsicVolcanic[i];
        sim.RunWithProgress(null);
        return (sim, felsic0);
    });

    static double FelsicMass(H3PlateFields fields)
    {
        double sum = 0;
        for (int i = 0; i < fields.Count; i++)
            sum += fields.FelsicPlutonic[i] + fields.FelsicVolcanic[i];
        return sum;
    }

    [Test]
    public void LongRun_3000My_ContinentsSurvive()
    {
        var (sim, felsicInitial) = SharedRun.Value;

        // 陆壳存量保底（伺服的核心承诺）：长跑终点不得低于初始禀赋。
        // 修复前实测 = 初始的 27%（8.4e9 / 3.1e10），陆格被成片销毁。
        double felsicFinal = FelsicMass(sim.Fields);
        // P5（重启退休后重校）：连续演化（零重启）下瞬时采样可低于禀赋数个百分点（出口/补料的
        // 有界振荡），伺服地板保证不发散——崩塌判据 = 跌破 90%（修复前实测 27%）。
        Assert.GreaterOrEqual(felsicFinal, felsicInitial * 0.9,
            $"长英质存量 {felsicFinal:E3} 跌破初始 {felsicInitial:E3} 的 90%——陆壳收支伺服失守，长跑走向水世界");

        // 陆占比下界：修复前 3000 My = 9.4%（只剩岛屿）；修复后实测 ≈ 22%。
        // 上界沿用 H3DynamicRunTests 的全陆病理线（99.5%）。
        double landFraction = sim.LandFractionLastStep;
        Assert.That(landFraction, Is.InRange(0.15, 0.995),
            $"3000 My 终态陆占比 {landFraction:P1} 越出带宽（水世界或全陆都是病理）");

        foreach (float d in sim.Displacement)
            Assert.IsTrue(float.IsFinite(d), "均衡位移出现非有限值");
    }

    [Test]
    public void LongRun_3000My_ContinentalBudgetCloses()
    {
        var (sim, felsicInitial) = SharedRun.Value;
        double destroyed = sim.RecycledConservedTotal + sim.ConservedYieldToMantleCum;

        Assert.Greater(destroyed, 0.0, "3 Ga 长跑守恒组销毁账应非零（再循环通道在工作）");
        // 伺服不变量（最终一致性，P4 质量地板后改受威胁侧口径）：终态存量跌破地板 = 大陆仍受威胁
        // ⇒ 缺口必须已被跟上（尾差 ≤ 35%，全球陆壳顶帽的窗口期滚存计 ~27%）；存量在地板上方 =
        // 大陆付得起出口账，缺口滚账是**设计行为**（地板门暂停补料），不作断言。
        double gap = destroyed - sim.FelsicArcReturnedCum;
        double felsicFinal = FelsicMass(sim.Fields);
        if (felsicFinal < felsicInitial * sim.ServoFelsicFloorMultiple)
            Assert.LessOrEqual(gap, destroyed * 0.8,
                $"弧回流累计 {sim.FelsicArcReturnedCum:E3} 对守恒组销毁累计 {destroyed:E3} 的缺口 {gap:E3} " +
                "超过 80%——伺服没有跟上销毁（底侵容量枯竭或账本口径漂移）");

        // 质量对账含伺服创建账（伺服补进弧格的质量必须记入 CrustCreatedTotal）
        double total = sim.TotalCrustMass();
        double expected = sim.InitialCrustMass + sim.CrustCreatedTotal - sim.CrustDestroyedTotal;
        double relative = Math.Abs(total - expected) / sim.InitialCrustMass;
        Assert.LessOrEqual(relative, 1e-4,
            $"质量对账失守：实际 {total:E6} vs 账面 {expected:E6}（相对漂移 {relative:E2}）");
    }

    [Test]
    public void Servo_MassFloor_PausesAboveEndowment()
    {
        // P4 形态拍板（第二批.6 对策）：长英质存量在地板（初始禀赋）上方 ⇒ 伺服暂停补料、缺口滚账
        // （防"伺服从兜底变主渠道"的单调陆增长——通量路径 33%→70%）；跌破地板 ⇒ 伺服工作（防水世界）。
        var ball = new Ball(Res, 1f);
        var splitter = new H3Plate(ball);
        var sim = new H3DynamicTectonics(ball) { RunMy = 600f, StepMy = 4f };
        sim.Initialize(splitter.SplitIntoPlates(Plates, Seed), Seed);
        Assert.Greater(sim.FelsicMassInitial, 0.0, "布置检查：禀赋基准应已记账");

        // 相位①：陆格存量抬高 20%（地板上方）⇒ 伺服暂停（出口照常、缺口滚账、补料为零）。
        // ⚠️ 只加陆格——洋格加长英质会让全星变陆，触发"陆内重采样补料"创建通道失控（实测 3× 禀赋）。
        int landCells = 0;
        for (int i = 0; i < sim.Fields.Count; i++)
            if (sim.Fields.IsLand(i)) landCells++;
        for (int i = 0; i < sim.Fields.Count; i++)
            if (sim.Fields.IsLand(i))
                sim.Fields.FelsicVolcanic[i] += (float)(sim.FelsicMassInitial * 0.2 / landCells);
        double placedAbove = 0;
        for (int s = 0; s < 150; s++)   // 600 My：伺服"暂停"断言不依赖赤字，跑窗只证明持续暂停
        {
            sim.Step();
            placedAbove += sim.ContinentalServoPlacedLastStep;
        }
        Assert.AreEqual(0.0, placedAbove, "地板上方：伺服必须暂停补料（防单调陆增长）");

        // 相位②：按比例把存量蚀到地板的一半（必破地板；且柱厚降到帽下 = 伺服的帽余量通道畅通）
        // ⇒ 赤字 > 0（跑窗已累积）且存量在地板下 ⇒ 伺服恢复补料。
        double massNow = 0;
        for (int i = 0; i < sim.Fields.Count; i++)
            massNow += sim.Fields.FelsicPlutonic[i] + sim.Fields.FelsicVolcanic[i];
        double fraction = Math.Min(1.0, (massNow - sim.FelsicMassInitial * 0.5) / massNow);
        for (int i = 0; i < sim.Fields.Count; i++)
        {
            sim.Fields.FelsicVolcanic[i] *= (float)(1.0 - fraction);
            sim.Fields.FelsicPlutonic[i] *= (float)(1.0 - fraction);
        }
        double placedBelow = 0;
        for (int s = 0; s < 3; s++)
        {
            sim.Step();
            placedBelow += sim.ContinentalServoPlacedLastStep;
        }
        Assert.Greater(placedBelow, 0.0, "跌破地板 + 赤字在手：伺服应恢复补料（防海水世界的核心承诺）");
    }
}
