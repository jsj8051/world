using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 动力学补强（设计-04 批次 2）测试：板片账户 + 洋脊推力 + 造山负载阻力。
/// 断言的测量项：
///   · 俯冲入账：平流 Pass C 俯冲裁决把 mafic 质量 × 流向记入 SlabInflow（来料板名下）；
///   · 账户拉力：无板缘驱动的板，仅凭板片账户也获得速度（回地幔 ≠ 拉力消失——03 §3.6 缺口补上）；
///   · 账户衰减与封顶：记忆 My 内逐 步衰减、按板内地壳总量封顶；
///   · 阻力方向性：顶死世界的板速低于账户清零的对照（阻力 = μ × 幻影力真实做负功）。
/// 纪律（同 H3PlateStaticTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService；浮点断言带容差。
/// </summary>
public class H3DynamicForceTests
{
    private const int Res = 1;
    private const float OldAgeMy = 300f;    // > 250：密度 3300 > 地幔 → 负浮力驱动
    private const float YoungAgeMy = 0f;    // 密度 2890 < 地幔 → 浮力 0

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;
    static readonly MaterialDensity Material = new();

    static void FillOldOceanic(H3PlateFields fields, int cell)
    {
        fields.ClearCell(cell);
        fields.MaficVolcanic[cell] = Material.MaficVolcanicMin * 7100f;
        fields.Age[cell] = OldAgeMy;
    }

    [Test]
    public void Advection_Subduction_RecordsSlabInflowForLosingPlate()
    {
        int i = 0;
        while (Ball.CellNeighbors[i].Length == 0) i++;
        int j = Ball.CellNeighbors[i][0];

        var source = new H3PlateFields(Ball.CellIds.Length);
        FillOldOceanic(source, i);
        source.PlateId[i] = 0;                              // 来料板 0（3300，更密）
        source.ClearCell(j);
        source.MaficVolcanic[j] = Material.MaficVolcanicMin * 7100f;
        source.Age[j] = YoungAgeMy;                         // 目标年轻（2890，更轻）→ 俯冲
        source.PlateId[j] = 1;

        var motion = new H3PlateMotion(Ball.CellIds.Length);
        Vector3 toJ = Ball.CellCenters[j] - Ball.CellCenters[i];
        motion.Velocity[i] = toJ * 0.01f;
        motion.PlateFired = new bool[8];
        for (int p = 0; p < 8; p++) motion.PlateFired[p] = true;

        var target = new H3PlateFields(Ball.CellIds.Length);
        var advection = new H3PlateAdvection(Ball.CellIds.Length);
        advection.Step(Ball, source, motion, target, Material, stepIndex: 0);

        Assert.IsTrue(advection.SlabInflow.ContainsKey(0), "俯冲入账应记在来料板（板 0）名下");
        var (mass, dir) = advection.SlabInflow[0];
        Assert.Greater(mass, 0.0, "入账质量 = 俯冲格 mafic 面密度");
        Assert.AreEqual(source.MaficVolcanic[i] + source.MaficPlutonic[i], mass, 1e-3, "入账质量与回地幔记账同源");
        Assert.Greater(dir.Dot(toJ.Normalized()), 0.99f, "入账方向 = 来料格→目标格切向");
    }

    [Test]
    public void SolvePlateSpeeds_SlabAccount_AloneDrivesPlate()
    {
        // 年轻洋壳单板（浮力恒 0、无板缘）——唯一驱动 = 手工注入的板片账户。
        // 旧模型此处速度恒 0（回地幔即失拉力）；批次 2 后应被账户驱动（03 §3.6 缺口的正向断言）。
        var fields = new H3PlateFields(Ball.CellIds.Length);
        for (int c = 0; c < fields.Count; c++)
        {
            fields.PlateId[c] = 0;
            fields.ClearCell(c);
            fields.MaficVolcanic[c] = Material.MaficVolcanicMin * 7100f;
            fields.Age[c] = YoungAgeMy;
        }

        var motionNoAccount = new H3PlateMotion(Ball.CellIds.Length);
        motionNoAccount.Step(Ball, fields, Material, 9.8f, 4f);
        Assert.AreEqual(0f, motionNoAccount.PlateOmega[0].Length(), 1e-9f, "无账户无板缘 → 无驱动（对照）");

        var motion = new H3PlateMotion(Ball.CellIds.Length);
        Vector3 dir = new Vector3(0, 1, 0);
        // 100 个俯冲格当量的账户（单格当量只解出 0.02 cm/yr——低于拟合门槛 MinFit 0.1 cm/yr，
        // 物理上"一次俯冲拉不动整板"正是账户要积累的原因；100 格当量 ≈ 2.4 cm/yr，在封顶内）。
        motion.AddSlab(0, Material.MaficVolcanicMin * 7100f * 100f, dir);
        motion.Step(Ball, fields, Material, 9.8f, 4f);

        Assert.Greater(motion.PlateSlabPullN[0], 0f, "账户应产生板片拉力");
        Assert.Greater(motion.PlateSlabPullN[0], motion.PlateRidgePushN[0],
            "年轻洋板洋脊推力 ∝ sqrt(0/80) ≈ 0，板片应主导");
        Assert.Greater(motion.PlateOmega[0].Length(), 1f / H3PlateMotion.EarthRadiusKm,
            "仅凭板片账户应解出超拟合门槛的速度（≥ 0.1 cm/yr）");
        Assert.Less(motion.SlabMass[0], Material.MaficVolcanicMin * 7100f * 100f,
            "一步衰减后账户应已缩小（记忆 33 My，步长 4 My）");
    }

    [Test]
    public void AddSlab_CappedByPlateCrustMass()
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        for (int c = 0; c < fields.Count; c++)
        {
            fields.PlateId[c] = 0;
            fields.ClearCell(c);
            fields.MaficVolcanic[c] = Material.MaficVolcanicMin * 7100f;
            fields.Age[c] = YoungAgeMy;
        }
        var motion = new H3PlateMotion(Ball.CellIds.Length);
        motion.Step(Ball, fields, Material, 9.8f, 4f);      // 先跑一步：容量与板内地壳量就绪

        double crustMass = 0;
        for (int c = 0; c < fields.Count; c++) crustMass += fields.TotalMass(c);
        double cap = H3PlateMotion.SlabCapFractionOfPlateCrust * crustMass;

        motion.AddSlab(0, cap * 100.0, new Vector3(0, 1, 0));   // 远超上限的入账
        Assert.AreEqual(cap, motion.SlabMass[0], cap * 1e-3, "账户应被封顶在 板内地壳总量 × SlabCapFraction");
    }

    [Test]
    public void SolvePlateSpeeds_OrogenResistance_MakesJamWorldSlowerThanAccountOnly()
    {
        // 顶死世界（两板等密老洋壳，v1.8 的幻影剔除已生效）：阻力项 R = μ×幻影力 应让它比
        // "幻影力为零但其余同款"的对照更慢——顶死段开始真实做负功，而不是只被剔除。
        var centers = Ball.CellCenters;
        var fieldsJam = new H3PlateFields(Ball.CellIds.Length);
        var fieldsFree = new H3PlateFields(Ball.CellIds.Length);
        for (int c = 0; c < fieldsJam.Count; c++)
        {
            bool north = centers[c].Z >= 0f;
            foreach (var f in new[] { fieldsJam, fieldsFree })
            {
                f.PlateId[c] = north ? 0 : 1;
                f.ClearCell(c);
                f.MaficVolcanic[c] = Material.MaficVolcanicMin * 7100f;
                f.Age[c] = OldAgeMy;
            }
        }
        // fieldsJam：等密 → 板缘全顶死（幻影剔除 + 阻力）。fieldsFree：南板换年轻（浮力 0）→
        // 北板板缘不顶死、有净板缘驱动——两世界的北板账户/洋龄/面积同款。
        for (int c = 0; c < fieldsFree.Count; c++)
            if (fieldsFree.PlateId[c] == 1) fieldsFree.Age[c] = YoungAgeMy;

        var motionJam = new H3PlateMotion(Ball.CellIds.Length);
        var motionFree = new H3PlateMotion(Ball.CellIds.Length);
        motionJam.Step(Ball, fieldsJam, Material, 9.8f, 4f);
        motionFree.Step(Ball, fieldsFree, Material, 9.8f, 4f);

        Assert.Greater(motionJam.PlateOrogenResistN[0], 0f, "顶死世界北板应有造山阻力（R = μ×幻影力）");
        Assert.AreEqual(0f, motionFree.PlateOrogenResistN[0], 1e-6f, "无顶死 → 无阻力");
        Assert.Less(motionJam.PlateOmega[0].Length(), motionFree.PlateOmega[0].Length(),
            "顶死 + 阻力的板速必须低于净板缘驱动的对照");
    }
}
