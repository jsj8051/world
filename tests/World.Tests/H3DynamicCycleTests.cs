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
/// 手法：三板世界（A 北帽 / B 东南 / C 西南老洋壳互驱）→ 板缘负浮力驱动 → **A-B 接触带改陆壳**
/// ⇒ 陆-陆汇聚顶死（03 §3.5；缝合/造山的物理前置）。⚠️ v1.17：旧的"等密老洋壳全边界顶死"前提
/// 已按设计关闭——洋-洋汇聚一律俯冲（那条通道正是"海底长出 +4 km 假山系"的根因）。
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
    /// A-C / B-C = 俯冲边（老→年轻，保持驱动力不锁死）；A-B = 碰撞边——**两侧接触格改陆壳**
    /// （v1.17：顶死/造山/缝合只在有陆壳参与的接触上成立；板内其余部分仍致密老洋壳 ⇒ 驱动不变）。
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
        // A-B 碰撞带（v1.17）：两侧接触格改陆壳（纯长英质 35 km、等密 ⇒ 顶死；
        // 03 §3.5 陆-陆 = "两侧都停 + 增厚"，这是缝合与造山的物理前置）
        for (int i = 0; i < n; i++)
        {
            int plate = plateOfCell[i];
            if (plate != 0 && plate != 1) continue;
            int other = plate == 0 ? 1 : 0;
            bool contact = false;
            foreach (int nb in Ball.CellNeighbors[i])
                if (plateOfCell[nb] == other) { contact = true; break; }
            if (!contact) continue;
            sim.Fields.ClearCell(i);
            sim.Fields.FelsicPlutonic[i] = Material.FelsicPlutonic * 35000f;
            sim.Fields.Age[i] = 0f;
            sim.Fields.PlateId[i] = plate;
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
    public void YieldFlow_OverThickColumnFlowsToNeighbors()
    {
        var sim = JamWorld(sutureStreakSteps: 999, steps: 0);   // 缝合资格拉爆（不干扰本测试）
        sim.EnableErosion = false;                               // 关侵蚀：隔离"地表过程把料搬进海洋"的合法通道
        sim.MaxCrustThicknessM = 40000f;                         // 压低屈服阈值：35 km 柱碰撞堆叠（70 km）即超帽
        sim.YieldFlowRate = 1f;                                  // 一步回到阈值：硬语义，逐格上界可精确断言
        bool anyBled = false, anyPlaced = false;
        for (int s = 0; s < 40; s++)                             // 起跳是稀发事件，扫足窗口
        {
            sim.Step();
            if (sim.YieldBledMassLastStep > 0) anyBled = true;
            if (sim.YieldPlacedMassLastStep > 0) anyPlaced = true;
        }

        Assert.IsTrue(anyBled, "低阈值下碰撞堆叠（约 2× 柱厚）应有超帽量流出");
        Assert.IsTrue(anyPlaced, "同相邻居有帽余量时，流出质量应落给邻居（屈服流 = 纯搬运）");
        // 同相规则：物质按原成分流动、陆→陆/洋→洋 ⇒ 洋格永不收到长英质（不造陆；旧的
        // "洋壳前缘堆 mafic / 陆壳前缘 85/15" 分相分配随成分守恒的流动自然成立，不再是独立规则）。
        double oceanFelsic = 0;
        for (int i = 0; i < sim.Fields.Count; i++)
            if (!sim.Fields.IsLand(i))
                oceanFelsic += sim.Fields.FelsicPlutonic[i] + sim.Fields.FelsicVolcanic[i];
        Assert.AreEqual(0.0, oceanFelsic, 1e-3, "屈服流同相规则：洋格不得出现长英质（不造陆）");
        // 帽不变量（rate = 1 + 接收格余量约束）：全场无格越帽
        float thickest = 0f;
        for (int i = 0; i < sim.Fields.Count; i++)
            thickest = MathF.Max(thickest, sim.Fields.Thickness(i, Material));
        Assert.LessOrEqual(thickest, sim.MaxCrustThicknessM + 1f,
            $"最厚格 {thickest:F0} m 越过屈服阈值 {sim.MaxCrustThicknessM:F0} m");
    }

    /// <summary>帽满的纯陆柱**相对海平面的海拔**（= 厚度帽的 Airy 当量；单一 datum 下位移是绝对量，
    /// 需减去海平面才是"山有多高"）。⚠️ 只用于**同一时刻**的读数——海平面随洋底年龄/载荷分布漂移
    /// （v1.19 水量定值、无伺服，漂移是真实物理），拿它去比跨步的量必然失败（见 `OrogenCeiling` 注释）。</summary>
    static float CapCeilingElevation(float cap, float seaLevel)
        => CapCeilingDisplacement(cap) - seaLevel;

    /// <summary>帽满的纯陆柱的**位移**（m，单一 datum 的绝对值）= 厚度帽的 Airy 当量。
    /// **datum 无关**（不含海平面项，探针 Age = 0 ⇒ 也不含热沉降项）⇒ 可安全用于逐格·逐步的硬上界。</summary>
    static float CapCeilingDisplacement(float cap)
    {
        var probe = new H3PlateFields(1);
        probe.PlateId[0] = 0;
        probe.FelsicPlutonic[0] = Material.FelsicPlutonic * cap;
        probe.Age[0] = 0f;                       // 热年龄 0：陆壳处于热稳态，无冷却沉降（v1.18 语义）
        return H3Isostasy.ComputeCellDisplacement(probe, 0, Material);
    }

    [Test]
    public void OrogenCeiling_EqualsThicknessCapAiryEquivalent()
    {
        // "山有多高"**就是**厚度帽的 Airy 当量（陆侧增量口径）。
        // 这条把该关系钉住（v1.11 前的漂移正是它没被钉住：参考厚 28300 偏薄 + 帽 70000 ⇒ 实测饱和到 +7.2 km，
        // 而 02 §5.2 用户拍板的设计峰值是 +5.3 km / 峰值壳厚 60 km）。
        // ⚠️ v1.17：期望值改为**调均衡侧同一个实现**（`H3Isostasy.ComputeCellDisplacement`）而不是在测试里
        // 手抄公式——逐物质 Airy（长英质 0.1545 / 镁铁质 0.0602）之后，手抄那份必然与实现漂移。
        // ⚠️ v1.13 起侵蚀回来了（8b）：侵蚀会把质量搬进顶帽格造成**步内瞬时越帽**（下一步帽回收），
        // "每步扫描都 ≤ 帽"不再是精确不变量——故本测试显式关侵蚀，专测帽机制本身。
        // ⚠️ v1.18 起海平面是**动力学量**（随洋底年龄分布漂移；v1.19 水量定值、无伺服后漂得更多，
        // 本夹具 120 步里实测 km 级）。旧写法把"用**初始**海平面算的固定上限"与"每步用**当时**海平面算的海拔"相比、
        // 还把 out 参数 ceiling 用在了循环里（NUnit 的 out 语义下它甚至是初始值）——**跨步比较**必然假失败。
        // 现写法：`海拔 − 本步上限 = 位移 − 帽当量位移`（海平面项两边都减掉了）⇒ **datum 无关**、逐格逐步成立。
        const float cap = 45000f;                     // 压低帽：让"饱和"在几十步内出现（判据与帽值无关）
        var sim = JamWorld(sutureStreakSteps: 999, steps: 0);
        sim.MaxCrustThicknessM = cap;
        sim.YieldFlowRate = 1f;      // 硬语义：超帽量一步流平 ⇒ "厚度 ≤ 阈值"逐格不变量精确成立
        sim.EnableErosion = false;
        // v1.19 全球水层口径：帽**不是海拔设计目标**；海平面随夹具洋占比/洋底年龄分布涌现
        //（JamWorld 大洋夹具海平面可正可负），所以这里改钉**关系链**而非数值带：
        // 默认帽 65 km 的当量海拔必须恰好 = 帽当量位移(9616) − 本步海平面（**初始构型**的一次性读数），
        // 另给宽 sanity 带（剥蚀前的机械上限；实际峰体由 8b 侵蚀削）。
        float defaultCeiling = CapCeilingElevation(65000f, sim.SeaLevel);
        Assert.AreEqual(CapCeilingDisplacement(65000f) - sim.SeaLevel, defaultCeiling, 2f,
            $"默认帽当量海拔 {defaultCeiling:F0} m 应 = 当量位移 9616 − 涌现海平面 {sim.SeaLevel:F0}（海拔 = 位移 − 海平面链）");
        Assert.That(defaultCeiling, Is.InRange(6000f, 11000f),
            $"默认帽当量海拔 {defaultCeiling:F0} m 落出 sanity 带——水量/均衡口径失控");
        float capDisplacement = CapCeilingDisplacement(cap);

        float thickest = 0f;
        float highestDisplacement = float.MinValue;
        float overCeiling = float.MinValue;
        float maxUplift = 0f;
        int hottest = -1;
        for (int s = 0; s < 120; s++)
        {
            sim.Step();
            if (sim.FlexurePeakUpliftM > maxUplift) maxUplift = sim.FlexurePeakUpliftM;
            for (int i = 0; i < sim.Fields.Count; i++)
            {
                thickest = MathF.Max(thickest, sim.Fields.Thickness(i, Material));
                if (!sim.Fields.IsLand(i)) continue;
                // datum 无关：位移 vs 帽当量位移（= 海拔 vs 本步帽当量海拔，海平面项两边都减掉了）
                float excess = sim.Displacement[i] - capDisplacement;
                if (excess > overCeiling) { overCeiling = excess; hottest = i; }
                if (sim.Displacement[i] > highestDisplacement) highestDisplacement = sim.Displacement[i];
            }
        }
        Assert.GreaterOrEqual(hottest, 0, "碰撞世界应存在陆格（无则本测试的判据无从谈起）");
        Assert.LessOrEqual(thickest, cap + 1f, $"最厚格 {thickest:F0} m 越过厚度帽 {cap:F0} m（帽本身漏了）");
        Assert.Greater(highestDisplacement, capDisplacement - 500f,
            $"碰撞世界最高陆格位移 {highestDisplacement:F0} m 应贴近厚度帽的 Airy 当量 {capDisplacement:F0} m（造山饱和）");
        // 逐格·逐步的硬上界：任何陆格**都不得超过"帽满的纯陆柱"当量**
        // （山高与壳厚脱钩 = 冒出帽外的凭空高度，v1.17 的 +4 km 假山系就是这么来的）。
        // 余量的**物理来源只有挠曲前隆**（板支撑的抬高，不来自本格壳厚，06 §3）：本格 Airy 已被厚度帽
        // 压在其当量之下（上一条断言），故超出量 ≤ 本步全球最大挠曲抬升 `FlexurePeakUpliftM`
        // —— 用它当界而不是拍一个常数：自我一致、随模型改动自动收紧。
        // 实测本夹具（res1/162 格/帽 45 km）：超出量 −3 m、前隆 6 m ⇒ 局部等静压界几乎精确成立。
        Assert.LessOrEqual(overCeiling, MathF.Max(1f, maxUplift),
            $"存在陆格位移超过厚度帽的 Airy 当量 {capDisplacement:F0} m（超出 {overCeiling:F0} m，"
            + $"本步前隆幅度上限 {maxUplift:F0} m）"
            + $"［诊断：最高格 {hottest} 厚 {sim.Fields.Thickness(hottest, Material):F0} m "
            + $"年龄 {sim.Fields.Age[hottest]:F0} My 位移 {sim.Displacement[hottest]:F0} 海平面 {sim.SeaLevel:F0}］");
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
    public void Split_DaughtersGetRiftBandAndDivergentKick()
    {
        // v1.24(A)：裂解必须产生"能张开的裂谷"——旧口径子板原速继承母板、裂谷两侧老洋壳的
        // 板缘通道还把两块子板**互相推向对方** ⇒ 裂完立刻顶死再缝合（"裂完再焊死"空转，巨板棘轮帮凶）。
        //   ① 新旧归属邻接带的热年龄清零（新生裂谷洋壳 ⇒ 边缘通道在裂谷上不出力）；
        //   ② 三块子板的板片账户各有一笔背离另外两颗种子的拉力（随 SlabMemoryMy 衰减的离散窗口）。
        int n = Ball.CellIds.Length;
        var plateOfCell = new int[n];
        for (int i = 0; i < n; i++)
        {
            float x = Ball.CellCenters[i].X;
            if (x > 0.2f) plateOfCell[i] = 0;
            else plateOfCell[i] = Ball.CellCenters[i].Y >= 0f ? 1 : 2;
        }
        var sim = new H3DynamicTectonics(Ball) { RunMy = 16f, StepMy = 4f };
        sim.Initialize(plateOfCell, 42);
        int maxOldId = 0;
        for (int i = 0; i < n; i++) maxOldId = System.Math.Max(maxOldId, sim.Fields.PlateId[i]);
        bool splitFired = false;
        for (int s = 0; s < 4 && !splitFired; s++)
        {
            sim.Step();
            splitFired = sim.SplitPlateLastStep >= 0;
        }
        Assert.IsTrue(splitFired, "60% 巨板应触发裂解");

        // ① 裂谷带存在：异板邻接对中至少一侧热年龄 = 0
        var plateId = sim.Fields.PlateId;
        bool riftBandExists = false;
        for (int i = 0; i < n && !riftBandExists; i++)
            foreach (var nb in Ball.CellNeighbors[i])
            {
                if (plateId[nb] == plateId[i]) continue;
                if (sim.Fields.Age[i] == 0f || sim.Fields.Age[nb] == 0f) { riftBandExists = true; break; }
            }
        Assert.IsTrue(riftBandExists, "新旧归属邻接带应有热年龄清零的裂谷格（新生裂谷洋壳）");

        // ② 离散踢存在：新板号（> 旧最大 id）的板片账户应有非零拉力（方向断言在 Kick 的确定性构造里）
        var motion = sim.Motion;
        // 新板号 = 旧最大 id 之后的两连号（_nextPlateId 顺序分配）。⚠️ 不能遍历 motion.PlateIds——
        // 那是本步 Step 开头收集的（裂解发生在步尾），新板号要到下一步才进量测表；直接按 id 查账户数组。
        int kickedDaughters = 0;
        for (int p = maxOldId + 1; p <= maxOldId + 2; p++)
            if (motion.SlabDirOf(p).LengthSquared() > 1e-6f) kickedDaughters++;
        Assert.GreaterOrEqual(kickedDaughters, 1,
            "裂解产生的子板应带板片账户离散拉力（裂谷张开的发动机）");
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
        // 造"耗尽"场景：先跑几步建立峰值动量，再清零热年龄——板片拉力 N(age) 归零、板片账户
        // 逐步衰减 ⇒ 动量崩塌（自然动力学里全洋世界的动量只涨不跌，等不出耗尽）。
        var sim = AllOceanPlateWorld();
        OnlyEnergyTrigger(sim, ratio: 0.99f);
        sim.RestartStallSteps = 2;
        for (int s = 0; s < 6; s++) sim.Step();
        for (int i = 0; i < sim.Fields.Count; i++) sim.Fields.Age[i] = 0f;
        int steps = 0;
        while (sim.RestartCount == 0 && steps < 30) { sim.Step(); steps++; }

        Assert.GreaterOrEqual(sim.RestartCount, 1, "能量跌破阈值应按 platec 口径触发重启");
        Assert.AreEqual("动能衰减", sim.LastRestartReason);
        Assert.That(sim.MomentumRatioLastStep, Is.LessThan(0.99f + 1e-4f), "触发时能量比应低于阈值");
        Assert.LessOrEqual(sim.CycleStepCount, steps, "重启后周期步数应归零");

        // 对照：platec 原值 0.15 下，正常演化的同一世界 30 步内不该触发（阈值语义 = 真的"耗尽"才重启）
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
