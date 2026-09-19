using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 均衡口径的回归测试（v1.17 / v1.18 "地图太假"系列修复）：
///   ① **逐物质 Airy**：镁铁质增量用 1 − 2890/3075 = 0.0602，不再错用长英质系数
///      （ρ_c 2700 时 0.122；旧表 2600 时 0.1545。旧口径把洋壳增厚的
///      抬升放大 2.57× ⇒ 海底堆出假山）；
///   ② **单一 datum**（v1.18，06 §2.2 批次 A）：位移 = 柱的**绝对**函数（参考洋柱 age=0 处 ≡ 0，35 km 纯陆柱
///      = 陆洋均衡阶跃 ≈ +3840 m；ρ_c 2600 历史口径 +4980），**不含任何海平面项** ⇒ 旧"陆/洋两 datum"的 ~3.9 km 跨线台阶
///      **结构上不存在**、`RebaseLandToSeaLevel` 作废、水位真的管海陆；
///   ③ **水位维度**：海平面按全球水量解、容器含被淹没的陆壳 ⇒ 薄壳低地可以低于海平面（大陆架/浅海）；
///   ④ **水量守恒（v1.19 全球等效水层口径）**：水量 = `GlobalWaterLayerM`(2700 m，地球实测) × 全星格数
///      摊到洋盆格数，海平面由容器容积二分解出——无任何海拔目标；大陆露出是涌现量
///      （≈ 陆洋均衡阶跃 − 海平面），且被淹没的陆壳存在。
/// 纪律（同其余 H3 测试类）：只用 [Test]；不写文件；不触碰 GD.*/LogService；浮点断言带容差。
/// </summary>
public class H3IsostasyContinuityTests
{
    private const int Res = 1;
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;
    static readonly MaterialDensity Material = new();

    static H3PlateFields PureOcean(float maficThicknessM, float ageMy)
    {
        var fields = new H3PlateFields(1);
        fields.PlateId[0] = 0;
        fields.MaficVolcanic[0] = Material.MaficVolcanicMin * maficThicknessM;
        fields.Age[0] = ageMy;
        return fields;
    }

    /// <summary>陆柱探针：Age = 0（v1.18 语义：`Age` 是**岩石圈热年龄**，大陆处于热稳态 ⇒ 无冷却沉降；
    /// 初值 0 与 `BuildInitialCrust` 的陆格写入口径一致）。</summary>
    static H3PlateFields Continental(float felsicThicknessM)
    {
        var fields = new H3PlateFields(1);
        fields.PlateId[0] = 0;
        fields.FelsicPlutonic[0] = Material.FelsicPlutonic * felsicThicknessM;
        fields.Age[0] = 0f;
        return fields;
    }

    static float Displacement(H3PlateFields fields) => H3Isostasy.ComputeCellDisplacement(fields, 0, Material);

    // ═══════════════════════════════════════════════════════════════
    // ① 逐物质 Airy
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void AiryFactor_PerMaterial_MaficIsNotFelsic()
    {
        float felsic = H3Isostasy.AiryFactorFor(Material.FelsicPlutonic, Material);
        float mafic = H3Isostasy.AiryFactorMafic(Material);
        Assert.AreEqual(1f - Material.FelsicPlutonic / Material.Mantle, felsic, 1e-6, "长英质 Airy 系数");
        Assert.AreEqual(1f - Material.MaficVolcanicMin / Material.Mantle, mafic, 1e-6, "镁铁质 Airy 系数");
        Assert.That(mafic, Is.InRange(0.05f, 0.07f), $"镁铁质系数 {mafic:F4} 应在 0.0602 量级");
        Assert.Less(mafic, felsic * 0.5f,
            $"镁铁质系数 {mafic:F4} 必须显著小于长英质 {felsic:F4}——旧口径两者共用一个值，"
            + "洋壳增厚的抬升被放大 2.57×（海底假山的成因之一）");
    }

    [Test]
    public void ColumnDisplacement_OceanThickening_RisesByMaficAiry_NotFelsic()
    {
        // 洋壳增厚 5000 m ⇒ 抬升 = 0.0602×5000 ≈ 301 m（旧口径给 0.1545×5000 ≈ 773 m）
        const float added = 5000f;
        float before = Displacement(PureOcean(H3Isostasy.OceanReferenceThicknessM, 0f));
        float after = Displacement(PureOcean(H3Isostasy.OceanReferenceThicknessM + added, 0f));
        float rise = after - before;
        float expected = H3Isostasy.AiryFactorMafic(Material) * added;
        Assert.AreEqual(expected, rise, 1f,
            $"洋壳增厚 {added:F0} m 的抬升 {rise:F0} m 应 = 镁铁质 Airy 当量 {expected:F0} m");
        Assert.Less(rise, H3Isostasy.AiryFactor(Material) * added * 0.5f,
            $"抬升 {rise:F0} m 不得接近长英质当量（旧口径的 2.57× 放大）");
    }

    // ═══════════════════════════════════════════════════════════════
    // ② 单一 datum：锚点 + **全程**单调（v1.18 的核心性质）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SingleDatum_ReferenceColumnIsZero_ContinentIsAiryStep()
    {
        // 参考洋柱（7.1 km 年轻洋壳、age 0）= 唯一 datum 的零点；35 km 纯陆柱 = 陆洋均衡阶跃。
        Assert.AreEqual(0f, Displacement(PureOcean(H3Isostasy.OceanReferenceThicknessM, 0f)), 1f,
            "参考洋柱（age=0）必须落在 datum 零点");
        float continent = Displacement(Continental(H3Isostasy.LandReferenceThicknessM));
        float airy = H3Isostasy.AiryFactor(Material);
        float airyMafic = H3Isostasy.AiryFactorMafic(Material);
        float step = airy * H3Isostasy.LandReferenceThicknessM - airyMafic * H3Isostasy.OceanReferenceThicknessM;
        Assert.AreEqual(step, continent, 1f,
            $"35 km 纯陆柱位移 {continent:F0} m 应 = 陆洋均衡阶跃 {step:F0} m（地球同口径 ~4.5 km）");
        Assert.That(continent, Is.InRange(3600f, 4100f),
            $"陆洋阶跃 {continent:F0} m 应在 3.6–4.1 km 量级（ρ_c 2700 物理化后；旧 2600 口径为 ~5.0 km）");
    }

    [Test]
    public void SingleDatum_FelsicSweep_IsMonotoneWithoutAnyStep()
    {
        // ⭐ 单一 datum 的核心不变量：在 7.1 km 参考洋壳上逐档加长英质（0 → 40 km），
        // 位移必须**全程严格单调不减且无台阶**——加浮力物质必须抬升，任何幅度都不许下沉。
        // 旧口径（v1.9.1~v1.17 的两 datum）做不到：跨"陆/洋基准"判据处差 ~3.9 km（v1.17 按主导物质二选一，
        // 实测 600 My 仍有 14800 条邻接边挂 ~4 km 台阶；用户 2026-09-17 直接质问"为什么会有单独格子下沉"）。
        const float step = 250f;
        float previous = float.MinValue;
        float worstStep = 0f, worstAt = 0f, worstDrop = 0f;
        for (float felsic = 0f; felsic <= 40000f; felsic += step)
        {
            var fields = PureOcean(H3Isostasy.OceanReferenceThicknessM, 0f);
            fields.FelsicPlutonic[0] = Material.FelsicPlutonic * felsic;
            float d = Displacement(fields);
            if (previous != float.MinValue)
            {
                float delta = d - previous;
                if (delta < -1f && -delta > worstDrop) worstDrop = -delta;
                if (MathF.Abs(delta) > worstStep) { worstStep = MathF.Abs(delta); worstAt = felsic; }
            }
            previous = d;
        }
        Assert.AreEqual(0f, worstDrop, 1f,
            $"扫描中出现 {worstDrop:F0} m 的下沉 —— 单一 datum 下加长英质不得让柱下沉");
        // 每档 250 m 长英质的抬升 = 0.122×250 ≈ 30 m；阈值取 200 m 留足余量（旧口径在此处跳 ~2500 m）
        Assert.Less(worstStep, 200f,
            $"相邻档（步长 {step:F0} m 长英质）出现 {worstStep:F0} m 的台阶（在 {worstAt:F0} m 处）");
    }

    [Test]
    public void SingleDatum_ThermalSubsidence_IsOneFormulaForAllColumns()
    {
        // v1.18 收口：热沉降是**唯一公式里的第二项** = 冷却岩石圈地幔的密度亏损 ÷ ρ_m
        // （`H3ThermalColumn.SubsidenceM`，与 02 §4 的 350 m/√My 同源、解析 354.6）。
        // 判据不是"洋壳/陆壳"分支，而是 `Age` = **岩石圈热年龄**：
        //   · 洋壳按离脊年龄拿到 √t 沉降剖面 ✓（这是纯地壳密度 Airy 做不到的：那种口径整片洋底只落 0.9 km）；
        //   · 陆壳 Age = 0（热稳态）⇒ 无冷却沉降 ✓（`BuildInitialCrust` 的陆格写入口径）；
        //   · 弧继承所在老洋底的年龄 ⇒ 跟着沉（见下一条测试）；
        //   · 带埋入板片的大陆只由 kAiry,M·(Mafic−7100) 计一次浮力，不得再叠热沉降 ✓。
        const float age = 50f;
        float thermal = H3ThermalColumn.SubsidenceM(age);
        float pureOcean = Displacement(PureOcean(H3Isostasy.OceanReferenceThicknessM, age));
        Assert.AreEqual(-thermal, pureOcean, 1f, "纯洋柱应等于 −H3ThermalColumn.SubsidenceM(age)（02 §4 律）");
        // 与旧口径常量的一致性（350 m/√My 取整 vs 解析 354.6，差 1.3% —— H3ThermalTests 另有互检）
        Assert.AreEqual(H3Isostasy.SubsidenceCoefM * MathF.Sqrt(age), -pureOcean, 40f,
            "热柱解析沉降应与 H3Isostasy.SubsidenceCoefM(350 m/√My) 的取整口径一致（差 ≤1.3%）");

        float airyStep = H3Isostasy.AiryFactor(Material) * H3Isostasy.LandReferenceThicknessM
            - H3Isostasy.AiryFactorMafic(Material) * H3Isostasy.OceanReferenceThicknessM;
        Assert.AreEqual(airyStep, Displacement(Continental(H3Isostasy.LandReferenceThicknessM)), 2f,
            "陆壳 Age=0 ⇒ 无热沉降（应恰好等于纯 Airy 陆洋阶跃）");

        var buriedSlabContinent = Continental(H3Isostasy.LandReferenceThicknessM);
        buriedSlabContinent.MaficVolcanic[0] = Material.MaficVolcanicMin * 7100f;   // 埋入一层俯冲板
        float withSlab = Displacement(buriedSlabContinent);
        float noSlab = Displacement(Continental(H3Isostasy.LandReferenceThicknessM));
        float airyMaficOnly = H3Isostasy.AiryFactorMafic(Material) * 7100f;         // 埋入板**只该**贡献这一项
        Assert.AreEqual(airyMaficOnly, withSlab - noSlab, 1f,
            $"带一层埋入板片的大陆位移差 {withSlab - noSlab:F0} m 应恰好等于 Airy 项 {airyMaficOnly:F0} m");

        // 年龄封顶（02 §4；v1.19(3a) 改由板模型渐近反推 ≈106 My）：封顶之后热沉降不再加深
        float capped = Displacement(PureOcean(H3Isostasy.OceanReferenceThicknessM, 400f));
        float atCap = Displacement(PureOcean(H3Isostasy.OceanReferenceThicknessM, H3Isostasy.AgeCapMy));
        Assert.AreEqual(atCap, capped, 1f, "超过 AgeCapMy（板模型渐近反推 ≈106 My）的年龄应封顶");
    }

    [Test]
    public void SingleDatum_ThinFelsicArc_IsSeamountLikeRise_NotDeepPit()
    {
        // 弧柱（06 §5.2 口径）：7.1 km 洋壳 + 30 km 弧物质（30% 长英质 = 9 km 长英质 + 21 km 镁铁质），
        // 坐落在 50 My 老洋底上。旧口径（v1.17 之前）该格 `IsLand = 长英质>0` ⇒ 走陆分支 ⇒ 位移 −3.1 km
        // （"陆格在水下"的自相矛盾）。现口径：弧是**海山式隆起**（比同龄洋底高几 km），且不打到深坑。
        var arc = new H3PlateFields(1);
        arc.PlateId[0] = 0;
        arc.MaficVolcanic[0] = Material.MaficVolcanicMin * (H3Isostasy.OceanReferenceThicknessM + 21000f);
        arc.FelsicPlutonic[0] = Material.FelsicPlutonic * 9000f;
        arc.Age[0] = 50f;
        float arcDisplacement = Displacement(arc);
        float plainOcean = Displacement(PureOcean(H3Isostasy.OceanReferenceThicknessM, 50f));
        Assert.Greater(arcDisplacement, plainOcean,
            $"弧格位移 {arcDisplacement:F0} m 应高于同龄洋底 {plainOcean:F0} m（弧是海底隆起）");
        Assert.Greater(arcDisplacement, -6000f, $"弧格位移 {arcDisplacement:F0} m 不应掉到深海以下");
        // 抬升 = 弧物质的 Airy 当量（0.122×9 km + 0.0602×21 km ≈ 2.36 km）− 热沉降占比差(0.24×2475)
        Assert.That(arcDisplacement - plainOcean, Is.InRange(2000f, 4000f),
            $"弧相对同龄洋底的抬升 {arcDisplacement - plainOcean:F0} m 应在 2–4 km 量级（海山/弧链）");
    }

    // ═══════════════════════════════════════════════════════════════
    // ③ 水位维度 + ④ 自由板由水量承载
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SeaLevel_FloodsThinContinentalCrust_AndVolumeLedgerHolds()
    {
        // 半区**起伏不均的陆壳**（薄格 20 km / 厚格 45 km ⇒ 均值 32.5 km）+ 半区洋壳。
        // ⚠️ 为什么陆壳厚度要**不均**：海平面由**定值水量**解出（v1.19），落在整块 hypsometry 的中间——
        // 若整块陆壳厚度一致且全部高出海平面，"被淹没的陆壳"就无从谈起。淹没发生在"比海平面低"的
        // 薄壳低地上（这正是真实大陆架/陆表海的成因）。旧口径（陆基准相对海平面 ⇒ 恒定自由板，
        // 与厚度无关）恰好没有这个性质，那正是"水位不管海陆"。
        int n = Ball.CellIds.Length;
        var fields = new H3PlateFields(n);
        var centers = Ball.CellCenters;
        int oceanBasin = 0;
        for (int i = 0; i < n; i++)
        {
            bool north = centers[i].Z >= 0f;
            fields.PlateId[i] = north ? 0 : 1;
            fields.ClearCell(i);
            if (north)
                fields.FelsicPlutonic[i] = Material.FelsicPlutonic * (i % 2 == 0 ? 20000f : 45000f);
            else
            {
                fields.MaficVolcanic[i] = Material.MaficVolcanicMin * H3Isostasy.OceanReferenceThicknessM;
                oceanBasin++;
            }
            fields.Age[i] = 0f;               // 热年龄：陆 0（热稳态）/ 洋 0（脊轴）
        }

        var isostasy = new H3Isostasy(n);
        // v1.19 水量口径：全球等效水层（地球实测 2700 m）× 全星格数摊到洋盆格数——与生产同式
        double tod = isostasy.TotalOceanDepth = H3Isostasy.GlobalWaterLayerM * (double)n / oceanBasin;
        isostasy.ComputeDisplacement(Ball, fields, Material);

        int floodedCrust = 0;
        for (int i = 0; i < n; i++)
        {
            if (fields.FelsicPlutonic[i] <= 0f) continue;
            if (isostasy.Displacement[i] < isostasy.SeaLevel) floodedCrust++;
        }
        Assert.Greater(floodedCrust, 0, "比均值薄的陆壳格应被淹没 —— 水位维度落地（02 §11A）");
        Assert.Greater(isostasy.SeaLevel, 0f,
            $"海平面 {isostasy.SeaLevel:F0} m 应高于 datum 零点（洋盆热沉降在 datum 下方让出容积）");

        // 水量账本：全体格 max(0, −位移) 之和 = 总水量（= 反解均深 × **洋壳格数**）
        double water = 0;
        for (int i = 0; i < n; i++)
        {
            float depth = isostasy.SeaLevel - isostasy.Displacement[i];
            if (depth > 0f) water += depth;
        }
        double total = tod * oceanBasin;
        Assert.Less(Math.Abs(water - total) / total, 1e-4,
            $"水量账本：容器（含被淹没的陆壳）装水 {water:F0} ≠ 总水量 {total:F0}");
    }

    [Test]
    public void CrustIsLand_FollowsElevation_NotFelsicPresence()
    {
        // ⚠️ v1.17 口径翻转：`Crust.IsLand` = **海拔 > 0**（不再是"长英质厚 > 0"）。
        // 长英质存在 = 陆壳（物质口径），但陆壳可以低于海平面（大陆架/浅海）。
        var crust = new World.NewHexWorld.Plate.Crust
        {
            PlateId = new[] { 0, 0, 0 },
            FelsicThick = new[] { 35000f, 25000f, 0f },
            MaficThick = new[] { 0f, 0f, 7100f },
            SedimentThick = new float[3],
            Age = new[] { 1000f, 1000f, 0f },
            Elevation = new[] { 800f, -600f, -3700f },     // 高原 / 大陆架浅海 / 深海
        };
        Assert.IsTrue(crust.IsLand(0), "海拔 +800 m 的长英质壳 = 陆");
        Assert.IsFalse(crust.IsLand(1), "海拔 −600 m 的长英质壳 = **海**（大陆架/浅海，旧口径判陆）");
        Assert.IsFalse(crust.IsLand(2), "洋壳 = 海");
        Assert.Greater(crust.FelsicThick[1], 0f, "该格仍是长英质壳（物质口径不因淹没而变）");
    }

    // ═══════════════════════════════════════════════════════════════
    // 端到端：初始化后大陆露出 = 均衡阶跃 − 海平面（涌现链）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Initialize_LandEmergence_MatchesIsostaticStepMinusSeaLevel()
    {
        // v1.19 全球水层口径：大陆露出不再是任何标定目标，而是涌现量 =
        // 陆洋均衡阶跃（常量）− 海平面（由"地球实测全球等效水层 2700 m"按容积守恒解出）。
        // 量级推演：洋底平均位移 ≈ −3.2 km（热沉降，年龄分布偏老）、洋盆占 60% ⇒
        // 海平面 ≈ −3.2 + 2.7/0.6 ≈ +1.3 km（datum）、陆均露出 ≈ 阶跃(3840) − 1300 ≈ +2.5 km
        // （剩余偏差源 = 洋底偏老，见 H3Isostasy v1.19 头注；ρ_c 已物理化 2700）。
        var plateOfCell = new H3Plate(Ball).SplitIntoPlates(4, 42);
        var sim = new H3DynamicTectonics(Ball);
        sim.Initialize(plateOfCell, 42);

        float airyStep = H3Isostasy.AiryFactor(Material) * H3Isostasy.LandReferenceThicknessM
            - H3Isostasy.AiryFactorMafic(Material) * H3Isostasy.OceanReferenceThicknessM;
        int crustCells = 0;
        double sum = 0;
        for (int i = 0; i < sim.Fields.Count; i++)
        {
            if (!sim.Fields.IsLand(i)) continue;                    // 物质口径：长英质壳
            crustCells++;
            sum += sim.Displacement[i] - sim.SeaLevel;
        }
        Assert.Greater(crustCells, 0, "初始世界应有长英质壳");
        double mean = sum / crustCells;
        double expected = airyStep - sim.SeaLevel;
        Assert.That(mean, Is.InRange(expected - 400.0, expected + 400.0),
            $"陆壳平均露出 {mean:F0} m 应 ≈ 均衡阶跃 {airyStep:F0} − 海平面 {sim.SeaLevel:F0} = {expected:F0} m"
            + "（±400 m 容差 = fBm 采样均值 + 陆格薄沉积 + 挠曲再分布）");
        // 水量锚：海平面必须由"全球等效水层"解出（洋盆占 60% ⇒ ≈ +1.3 km；带宽容洋占比的夹具差异）
        Assert.That(sim.SeaLevel, Is.InRange(800f, 1800f),
            $"初始海平面 {sim.SeaLevel:F0} m 应由全球等效水层(2700 m)涌现——任何海拔目标口径都会落出此带");
        // 露出海面的占比直接从位移场数（`LandFractionLastStep` 只在 Step() 里赋值，初始化后恒 0）
        int above = 0;
        var disp = sim.Displacement;
        for (int i = 0; i < disp.Length; i++)
            if (disp[i] > sim.SeaLevel) above++;
        float landFraction = above / (float)disp.Length;
        Assert.Greater(landFraction, 0f, "应有露出海面的大陆");
        Assert.Less(landFraction, 1f, "露出海面的占比必须小于 100%");
    }
}
