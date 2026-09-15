using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;                  // MaterialDensity（Airy 系数的密度表）

namespace World.Tests;

/// <summary>
/// 动态板块初始化测试：初始陆洋 + 初始起伏。
/// v1.12 起初始陆洋默认逐格混合（大陆斑块 + 海岸细节 × 板属性，`LandOceanNoiseBlend`）；
/// blend = 0 严格退回 v1.9 板级纯相（整板陆/整板洋）。断言的测量项：
///   · blend=0 成分纯度：陆板全格 felsic>0 且 mafic=0，洋板全格 felsic=0 且 mafic>0（无过渡带）；
///   · blend>0 混生：海岸线与板块解耦（必有混生板）且海洋占比仍被 OceanFraction 钉住；
///   · 确定性：同种子两次初始化逐位一致。
/// 纪律（同 H3PlateStaticTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3DynamicInitTests
{
    private const int Res = 1;          // 842 格，构造便宜
    private const int Plates = 4;
    private const int Seed = 42;

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;

    [Test]
    public void Initialize_PlateLevelLandOcean_PureCompositionPerPlate()
    {
        // v1.9 板级纯相 = v1.12 的 blend=0 特例口径，显式关掉混合再断言纯度
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball) { LandOceanNoiseBlend = 0f };
        sim.Initialize(plateOfCell, Seed);
        var fields = sim.Fields;

        // 逐板盘点：陆板（全格 felsic>0 且 mafic=0）/ 洋板（全格 felsic=0 且 mafic>0），不允许混
        int landPlates = 0, oceanPlates = 0;
        var seen = new HashSet<int>();
        foreach (int p in plateOfCell)
        {
            if (p < 0 || !seen.Add(p)) continue;
            bool allLand = true, allOcean = true;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields.PlateId[i] != p) continue;
                bool hasFelsic = fields.FelsicTotalMass(i) > 0f;
                bool hasMafic = fields.MaficTotalMass(i) > 0f;
                if (!hasFelsic || hasMafic) allLand = false;    // 陆板格：纯长英质
                if (hasFelsic || !hasMafic) allOcean = false;   // 洋板格：纯镁铁质
            }
            Assert.IsTrue(allLand || allOcean, $"板 {p} 陆洋混生（v1.9 初始口径应为板级纯相）");
            if (allLand) landPlates++; else oceanPlates++;
        }

        Assert.AreEqual(2, landPlates, "P=4、OceanFraction=0.6 → 陆板数 = round(4×0.4) = 2");
        Assert.AreEqual(2, oceanPlates, "两类板块都应出现");
    }

    [Test]
    public void Initialize_NoiseBlend_CoastlineDecoupled_OceanFractionPinned()
    {
        // v1.12 逐格混合（blend=1：陆洋与板块完全解耦）：① 必有混生板——海岸线不再贴板块边界，
        // 这是"陆壳洋壳分得太死"的回归判据；② 海洋占比仍被 OceanFraction 的分位数阈值逐格钉住。
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball) { LandOceanNoiseBlend = 1f };
        sim.Initialize(plateOfCell, Seed);
        var fields = sim.Fields;

        var hasLand = new HashSet<int>();
        var hasOcean = new HashSet<int>();
        int land = 0, total = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields.PlateId[i] < 0) continue;
            total++;
            if (fields.IsLand(i)) { land++; hasLand.Add(fields.PlateId[i]); }
            else hasOcean.Add(fields.PlateId[i]);
        }
        int mixed = 0;
        foreach (int p in hasLand)
            if (hasOcean.Contains(p)) mixed++;
        Assert.Greater(mixed, 0, "blend=1 时应有板陆洋混生——海岸线与板块解耦的回归判据");

        double fraction = land / (double)total;
        Assert.That(fraction, Is.InRange(0.39, 0.41),
            $"陆占比 {fraction:P1} 应被 OceanFraction 分位数阈值钉在 1−0.6 = 40% 附近");
    }

    [Test]
    public void Initialize_DefaultBlend_AlreadyMixesPlates()
    {
        // 默认档（blend = 0.7）就要"板块给大势、噪声撕海岸线"：至少一块板陆洋混生，
        // 且不退化成整板纯相（那是 blend=0 的口径）。默认值改动若让世界回到死板格局，这里先红。
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball);
        sim.Initialize(plateOfCell, Seed);
        var fields = sim.Fields;

        var hasLand = new HashSet<int>();
        var hasOcean = new HashSet<int>();
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields.PlateId[i] < 0) continue;
            if (fields.IsLand(i)) hasLand.Add(fields.PlateId[i]);
            else hasOcean.Add(fields.PlateId[i]);
        }
        int mixed = 0;
        foreach (int p in hasLand)
            if (hasOcean.Contains(p)) mixed++;
        Assert.Greater(mixed, 0, $"默认 blend 应产生混生板（陆板 {hasLand.Count} / 洋板 {hasOcean.Count} 全互斥 = 退化回板级纯相）");
    }

    [Test]
    public void Initialize_SameSeedTwice_BitwiseIdentical()
    {
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var a = new H3DynamicTectonics(Ball);
        var b = new H3DynamicTectonics(Ball);
        a.Initialize(plateOfCell, Seed);
        b.Initialize(plateOfCell, Seed);

        var poolsA = a.Fields.AllPools();
        var poolsB = b.Fields.AllPools();
        for (int k = 0; k < poolsA.Length; k++)
            CollectionAssert.AreEqual(poolsA[k], poolsB[k], $"八场第 {k} 场逐位不一致");
        CollectionAssert.AreEqual(a.Fields.PlateId, b.Fields.PlateId);
    }

    [Test]
    public void Initialize_LandCells_WithinPlateGranularity()
    {
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball);
        sim.Initialize(plateOfCell, Seed);

        int land = 0, total = 0;
        for (int i = 0; i < sim.Fields.Count; i++)
        {
            if (sim.Fields.PlateId[i] < 0) continue;
            total++;
            if (sim.Fields.IsLand(i)) land++;
        }
        Assert.Greater(land, 0, "应有陆板格");
        Assert.Less(land, total, "应有洋板格");
        // 板粒度近似：陆占比应落在 (0,1) 内且与 0.4 同量级（不苛求精确——板大小不均）
        double fraction = land / (double)total;
        Assert.That(fraction, Is.InRange(0.1, 0.9), $"陆占比 {fraction:P0} 偏离 OceanFraction 补集过远");
    }

    [Test]
    public void Initialize_LandElevation_AnchoredToBaselineAboveSea()
    {
        // 陆格露出**锚在自由板高度**（`H3Isostasy.LandBaselineM` = 800 m）± fBm 起伏（01 §3.3 目标 ~+800 m）。
        // ⚠️ 这条断言在 v1.10 前是 [200,3000] 的宽区间（"千 m 量级"），于是**放过了**一个真实缺陷：
        //    d 同时被当"海拔"与"老 Init 山地模板曲线的输入"，曲线在 d=797 处已编码 36.7 km 陆壳
        //    （比均衡参考厚 28.3 km 厚 8.4 km）→ 经 Airy 多抬 1.3 km，陆均值实测 **+2316 m**（偏目标 2.9 倍）。
        //    现陆壳走 Airy 反解，基准即海拔；本断言收紧到目标邻域 + 陆格恒在海平面之上（海平面求解的硬前提）。
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball);
        sim.Initialize(plateOfCell, Seed);

        var d = sim.Displacement;
        float sea = sim.SeaLevel;
        double sum = 0;
        int land = 0;
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < d.Length; i++)
        {
            if (!sim.Fields.IsLand(i)) continue;
            land++;
            float elevation = d[i] - sea;
            sum += elevation;
            if (elevation < min) min = elevation;
            if (elevation > max) max = elevation;
        }
        double mean = sum / land;
        float amplitude = sim.LandReliefAmplitudeM;
        Assert.That(mean, Is.InRange(600.0, 1000.0),
            $"陆格露出均值 {mean:F0} m 应锚在自由板高度 800 m（±{amplitude:F0} m 起伏）；海平面 {sea:F0} m");
        Assert.GreaterOrEqual(min, 0f,
            $"最低陆格 {min:F0} m —— 陆格不得落到海平面以下（幅度 {amplitude:F0} m 相对 800 m 自由板过大）");
        Assert.Greater(max, 800f + amplitude * 0.3f, $"最高陆格 {max:F0} m 过低 = 陆起伏没铺开");
        Assert.LessOrEqual(max, 800f + amplitude * 1.45f, $"最高陆格 {max:F0} m 超过 fBm 硬上界");
        Assert.Greater(sea, -5000f, "海平面应落在洋底基准决定的负值区间");
    }

    [Test]
    public void Initialize_CrustThickness_CenteredOnIsostasyReference()
    {
        // 陆壳厚度 = 参考厚（28.3 km）+ 起伏/kAiry ⇒ 均值必须回到参考厚（旧模板给的是 38.1 km，
        // 那 9.8 km 的"白送厚度"正是陆均值 +2.3 km 的根因）。洋壳同理锚在 7.1 km。
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball);
        sim.Initialize(plateOfCell, Seed);

        var fields = sim.Fields;
        var material = new MaterialDensity();
        double landSum = 0, oceanSum = 0;
        int land = 0, ocean = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields.IsLand(i)) { land++; landSum += fields.FelsicTotalMass(i) / material.FelsicPlutonic; }
            else { ocean++; oceanSum += fields.MaficTotalMass(i) / material.MaficVolcanicMin; }
        }
        double meanLand = landSum / land, meanOcean = oceanSum / ocean;
        Assert.That(meanLand, Is.InRange(34000.0, 36200.0),
            $"陆壳平均厚 {meanLand / 1000:F1} km 应锚在参考厚 {H3Isostasy.LandReferenceThicknessM / 1000:F1} km");
        Assert.That(meanOcean, Is.InRange(6900.0, 7300.0),
            $"洋壳平均厚 {meanOcean / 1000:F1} km 应锚在参考厚 {H3Isostasy.OceanReferenceThicknessM / 1000:F1} km");
    }

    // ═══════════════════════════════════════════════════════════════
    // v1.10：初始起伏 = 3D 噪声 + fBm（不再是逐格白噪声）
    // ═══════════════════════════════════════════════════════════════

    // 起伏"粗糙度"判据（**相对量，不设绝对阈值**，遵守开发规范 §5）：
    //   分子 = 相邻同板同相格（陆-陆 / 洋-洋）的壳厚差均值；分母 = 远距格对（半圈取对）的壳厚差均值。
    //   fBm 场空间相关 ⇒ 相邻差远小于远距差（比值 ≪ 1）；逐格白噪声 ⇒ 两者同量级（比值 ≈ 1）。
    // 波长远大于格距 → 相关（分形）；波长压到格距以下 → 退化为白噪声（对照臂）。
    double ReliefRoughnessRatio(float baseWavelengthKm, bool land)
    {
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball) { ReliefBaseWavelengthKm = baseWavelengthKm };
        sim.Initialize(plateOfCell, Seed);

        var fields = sim.Fields;
        var neighbors = Ball.CellNeighbors;
        float Thickness(int cell) => land ? fields.FelsicTotalMass(cell) : fields.MaficTotalMass(cell);

        var cells = new List<int>();
        double adjacentSum = 0;
        int adjacentCount = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields.IsLand(i) != land) continue;
            cells.Add(i);
            foreach (int nb in neighbors[i])
            {
                if (nb <= i || fields.IsLand(nb) != land) continue;
                if (fields.PlateId[nb] != fields.PlateId[i]) continue;   // 只比同板同相（跨板是壳类型跳变）
                adjacentSum += Math.Abs(Thickness(nb) - Thickness(i));
                adjacentCount++;
            }
        }
        Assert.Greater(adjacentCount, 20, "样本太少，判据不成立");

        double farSum = 0;
        int farCount = 0;
        int half = cells.Count / 2;
        for (int k = 0; k < cells.Count; k++)
        {
            farSum += Math.Abs(Thickness(cells[(k + half) % cells.Count]) - Thickness(cells[k]));
            farCount++;
        }
        return (adjacentSum / adjacentCount) / (farSum / farCount);
    }

    [Test]
    public void Initialize_InitialRelief_SpatiallyCorrelated_InBothRealms()
    {
        // 同一段代码、只改 fBm 基波长：6000 km（大陆级）应强相关；60 km（远小于 res1 格距 ~780 km）
        // 彻底去相关 ⇒ 白噪声对照臂。两臂同构，故判据是**比值差**而不是绝对阈值。
        double oceanFractal = ReliefRoughnessRatio(6000f, land: false);
        double oceanWhite = ReliefRoughnessRatio(60f, land: false);
        double landFractal = ReliefRoughnessRatio(6000f, land: true);
        double landWhite = ReliefRoughnessRatio(60f, land: true);

        Assert.Less(oceanFractal, 0.65, $"洋底起伏应空间连续（相邻/远距 = {oceanFractal:F2}）");
        Assert.Less(landFractal, 0.65, $"陆地起伏应空间连续（相邻/远距 = {landFractal:F2}）");
        Assert.Greater(oceanWhite, 0.8, $"对照臂应退化为白噪声（{oceanWhite:F2}）");
        Assert.Greater(landWhite, 0.8, $"对照臂应退化为白噪声（{landWhite:F2}）");
        Assert.Greater(oceanWhite - oceanFractal, 0.2, "分形臂与白噪声臂应显著分开（洋）");
        Assert.Greater(landWhite - landFractal, 0.2, "分形臂与白噪声臂应显著分开（陆）");
    }

    [Test]
    public void Initialize_OceanRelief_MapsThroughAiry_InKnownThicknessBand()
    {
        // 洋壳厚度 = 洋参考厚 + 起伏 / kAiry（Airy 反解）⇒ 厚度必须落在 [参考 ∓ 幅度/kAiry] 内，
        // 且实际铺开的跨度可观（不是常量场）。这条同时守住"洋底起伏是写厚度得来的"这个机制口径。
        const float amplitude = 400f;
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball) { OceanReliefAmplitudeM = amplitude };
        sim.Initialize(plateOfCell, Seed);

        float airy = H3Isostasy.AiryFactor(new MaterialDensity());
        float reference = H3Isostasy.OceanReferenceThicknessM;
        float span = amplitude / airy;
        var fields = sim.Fields;
        float min = float.MaxValue, max = float.MinValue;
        int ocean = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields.IsLand(i)) continue;
            ocean++;
            float thickness = fields.MaficTotalMass(i) / new MaterialDensity().MaficVolcanicMin;
            if (thickness < min) min = thickness;
            if (thickness > max) max = thickness;
        }
        Assert.Greater(ocean, 20, "样本太少");
        Assert.GreaterOrEqual(min, reference - span - 1f, $"洋壳厚度低于下界 {reference - span:F0} m");
        Assert.LessOrEqual(max, reference + span + 1f, $"洋壳厚度高于上界 {reference + span:F0} m");
        Assert.Greater(max - min, span * 0.5f, $"洋底起伏没铺开（厚度跨度 {max - min:F0} m）");
    }

    [Test]
    public void Initialize_ZeroRelief_LeavesFlatPlateBaseline_NoWhiteNoise()
    {
        // 幅度归零 ⇒ 初始地壳应是"板级纯相 + 匀质常数"（陆板壳厚处处相同、洋板壳厚 = 参考厚）。
        // 这是"逐格白噪声已彻底退役"的回归判据：旧 ±250 m 口径下本断言必然失败。
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball) { LandReliefAmplitudeM = 0f, OceanReliefAmplitudeM = 0f };
        sim.Initialize(plateOfCell, Seed);

        var fields = sim.Fields;
        var material = new MaterialDensity();
        var landThickness = new Dictionary<int, float>();
        int landCells = 0, oceanCells = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            int plate = fields.PlateId[i];
            if (fields.IsLand(i))
            {
                landCells++;
                float thickness = fields.FelsicTotalMass(i) / material.FelsicPlutonic;
                if (!landThickness.TryGetValue(plate, out float reference)) landThickness[plate] = thickness;
                else Assert.AreEqual(reference, thickness, 1e-3f, $"无起伏时陆板 {plate} 的壳厚应处处相同");
            }
            else
            {
                oceanCells++;
                Assert.AreEqual(H3Isostasy.OceanReferenceThicknessM,
                    fields.MaficTotalMass(i) / material.MaficVolcanicMin, 1e-3f, "无起伏时洋壳厚 = 参考厚");
            }
        }
        Assert.Greater(landCells, 0, "应有陆格");
        Assert.Greater(oceanCells, 0, "应有洋格");
    }
}
