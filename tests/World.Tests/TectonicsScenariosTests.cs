using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.HexPlanet;
using World.MapGen;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// Tectonics 深场景 + RiverSystem 迭代演化的模块测试（L0 纯托管，dotnet test / 本地执行器可运行）。
///
/// 定位（与既有 TectonicsTests.cs / MapGenTests.cs 互补，不重复）：
///   TectonicsTests —— 单方法单元契约（Merge 守恒、Rifting 空转、Subduction 变质、Accretion 手工增生）
///   MapGenTests  —— RiverSystem.Compute（单次）与 RebuildPaths
///   本文件        —— ⚠️ 按"构造模拟场景 → 单独验证"的组合场景：
///       Tectonics：
///         · UpdateRifting 真实裂谷（单板留洞 → 新洋壳填充 + mask 扩展 + 裂谷后 Merge 到 WorldCrust 一致）
///         · UpdateSubducted 真实俯冲消减（密度>地幔边界格被移除 → 守恒质量入 Accretion，前后质量守恒）
///         · UpdateSubducted→ApplyAccretion 端到端（俯冲产物落地顶层板的增生楔 + Accretion 复位）
///         · TryMergeCollidingPlates 阈值分支（>5 板但接触低于阈值的 NoOp 安全路径；聚合分支的
///           MergeTwoPlates 因无条件日志不可测——见下）。
///       RiverSystem 迭代模块（MapGenTests 未覆盖的新拆分 API）：
///         · ComputeIterative 球面世界多轮演化（确定性 / 输出合法 / 流向终止 / 无 NaN）
///         · ComputeFlow / ComputeWater / MarkRiversLakes 拆分步单测
///         · ApplyErosionDepositionV2 输沙：侵蚀下限（下游+minSlope 保单调）与沉积上限（海平面+depoCap）
///         · ComputeWatersheds 三类归属：河格流域 / 内流盆地 / 边缘排水区(-1)
///
/// ⚠️ 不可测（引擎依赖，无引擎进程 0xC0000005，全部避开 / 仅测安全前置路径）：
///   · TryMergeCollidingPlates 的聚合分支内部调用私有 MergeTwoPlates → LogService.Log(无条件 GD.Print)。
///     只能测 Plates.Count>5 但接触 < minContact 时返回（不触发合并）的安全判定路径。
///   · Run / RunWithProgress / GenerateInitialCrust / InitContinentalRiftMask / ComputeSubductionZones
///     / TrySplitSupercontinent(成功) / GuessPlateMap —— 均含日志或 FastNoiseLite。
///
/// 纪律：只用 [Test] / [TestCase(字面量)]；确定性输入；n≤3 小球；不写文件；浮点断言带容差。
/// 由于本地执行器可能把先前失败附加进后续失败消息，判定只看每个测试的实时失败项。
/// </summary>
public class TectonicsScenariosTests
{
    /// <summary>NUnit 无引擎测内不适用 Godot 时间单位常量引用，直接用 Crust 的 Units.MEGAYEAR。</summary>
    private const float My = Units.MEGAYEAR;

    private const float RiftMafic = 7100f * 2890f;   // 源码 UpdateRifting 常量

    // ═════════════════════════════════════════════════════════════════
    // 工具（构造场景）
    // ═════════════════════════════════════════════════════════════════

    /// <summary>球面网格（构造内部 log:false → 无引擎安全）。</summary>
    private static SphereGrid Sphere(int n) => new SphereGrid(n);

    /// <summary>模拟实例 + 矿藏数组初始化（UpdateRifting/UpdateSubducted/ApplyAccretion 需要非 null）。</summary>
    private static TectonicsSimulation NewSim(int n)
    {
        var sim = new TectonicsSimulation(n);
        int count = sim.GlobalGrid.VertexCount;
        sim.MineralHydro = new float[count];
        sim.MineralSed = new float[count];
        sim.MineralMeta = new float[count];
        return sim;
    }

    /// <summary>空 crust + mask；局部=全局恒等映射。cells 为覆盖的全局格 id。</summary>
    private static Plate BuildPlate(int id, SphereGrid grid, params int[] cells)
    {
        var crust = new Crust(grid);
        var mask = new byte[grid.VertexCount];
        foreach (var c in cells) mask[c] = 1;
        return new Plate(id, grid, crust, mask);
    }

    private static Plate BuildPlate(int id, SphereGrid grid, IEnumerable<int> cells)
        => BuildPlate(id, grid, cells.ToArray());

    /// <summary>某 crust 的 5 种 felsic 守恒池全球总量。</summary>
    private static double SumConserved5(Crust c)
    {
        double s = 0;
        foreach (var pool in c.ConservedPools())
            foreach (var v in pool) s += v;
        return s;
    }

    private static bool AllFinite(Crust c)
    {
        foreach (var pool in c.AllPools())
            foreach (var v in pool)
                if (float.IsNaN(v) || float.IsInfinity(v)) return false;
        return true;
    }

    private static bool AllFinite(float[] a)
    {
        foreach (var v in a)
            if (float.IsNaN(v) || float.IsInfinity(v)) return false;
        return true;
    }

    // ═════════════════════════════════════════════════════════════════
    // 1. UpdateRifting —— 真实裂谷：单板留洞 → 新洋壳填充 + mask 扩展 + 裂谷后 Merge 到 WorldCrust 一致
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// 保证：裂谷把板块边界的未覆盖格填成新洋壳（MaficVolcanic=RiftMafic、Age=0、其余清 0）、
    /// 板块 mask 扩展到这些格、MineralHydro 随裂谷热液累积；
    /// 并且裂谷后再次 Merge 时 WorldCrust 与板块内容一致（本组合既有单测未覆盖）。
    /// 场景：单块板覆盖 0..nv-3，留最后 2 格（{40,41}）为洞 → 裂谷填充洞。
    /// </summary>
    [Test]
    public void UpdateRifting_SinglePlateWithHole_FillsNewOceanicCrust()
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int n = g.VertexCount;
        int[] gap = new[] { n - 2, n - 1 };   // 最后 2 格 = 板块空洞
        var plate = BuildPlate(0, g, Enumerable.Range(0, n - 2));
        // 给板块一些初始地壳（非裂隙格不受影响）
        for (int i = 0; i < n - 2; i++) plate.Crust.FelsicPlutonic[i] = 2600f * 100f;
        sim.Plates.Add(plate);
        sim.MergePlatesToMaster();
        // 裂谷前确认洞存在（count==0）
        foreach (int c in gap) Assert.AreEqual(0, sim.PlateCount[c], $"裂谷前空洞格 {c} 未覆盖");

        var maskBefore = (byte[])plate.Mask.Clone();
        double hydroBefore = sim.MineralHydro.Sum(v => (double)Math.Abs(v));

        sim.UpdateRifting();

        // 记录真正被裂谷填充的格（mask 由 0→1 者）
        var rifted = new List<int>();
        for (int i = 0; i < n; i++)
            if (maskBefore[i] == 0 && plate.Mask[i] == 1) rifted.Add(i);
        Assert.Greater(rifted.Count, 0, "裂谷应至少填充一个空洞格");

        foreach (int i in rifted)
        {
            Assert.AreEqual(RiftMafic, plate.Crust.MaficVolcanic[i], 1e-3f, $"裂谷填新洋壳质量 {i}");
            Assert.AreEqual(0f, plate.Crust.Age[i], 1e-6f, $"新洋壳 Age=0 {i}");
            Assert.AreEqual(0f, plate.Crust.Sediment[i], 1e-6f, $"其余物质清 0 {i}");
            Assert.AreEqual(0f, plate.Crust.Sedimentary[i], 1e-6f, $"其余物质清 0 {i}");
            Assert.AreEqual(0f, plate.Crust.Metamorphic[i], 1e-6f, $"其余物质清 0 {i}");
            Assert.AreEqual(0f, plate.Crust.FelsicPlutonic[i], 1e-6f, $"其余物质清 0 {i}");
            Assert.AreEqual(0f, plate.Crust.FelsicVolcanic[i], 1e-6f, $"其余物质清 0 {i}");
            Assert.AreEqual(0f, plate.Crust.MaficPlutonic[i], 1e-6f, $"其余物质清 0 {i}");
            Assert.AreEqual(1f, sim.MineralHydro[i], 1e-6f, $"裂谷热液矿化累积 {i}");
        }
        Assert.Greater(sim.MineralHydro.Sum(v => (double)Math.Abs(v)), hydroBefore,
            "裂谷应产生新的热液矿化事件");

        // 裂谷后 Merge → WorldCrust 与板块一致（本场景组合点）
        sim.MergePlatesToMaster();
        foreach (int i in rifted)
        {
            Assert.AreEqual(1, sim.PlateCount[i], $"裂谷后空洞填实 {i}");
            Assert.AreEqual(0, (int)sim.TopPlateMap[i], $"空洞顶层=本板 {i}");
            Assert.AreEqual(RiftMafic, sim.WorldCrust.MaficVolcanic[i], 1e-3f, $"WorldCrust 新洋壳 {i}");
            Assert.AreEqual(0f, sim.WorldCrust.Age[i], 1e-6f, $"WorldCrust 年龄 0 {i}");
        }
        Assert.IsTrue(AllFinite(sim.WorldCrust));
    }

    // ═════════════════════════════════════════════════════════════════
    // 2. UpdateSubducted —— 深埋变质 + 事件记录（多板叠置）
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// 场景：3 块板。板 0（俯冲板，高密度岩成分，覆盖格 0..20）+ 板 1/板 2（低密度顶层，全覆盖）
    /// → 每格 PlateCount≥2 且顶层=板1 → 板 0 与板 2 均为"被俯冲"板（顶层≠自身）。
    /// 保证（2026-08 生产修复后按设计语义）：
    ///   · 每块被俯冲板把其 sediment/sedimentary/felsic 全转自身 metamorphic（深埋变质）；
    ///   · 矿化事件按【每板每次】无条件计数：0..20 被 p0、p2 两块俯冲板变质 → MineralMeta=2、Hydro=1.0；
    ///   · **消减移除生效**（修复：justInside 由 mask 外扩层 → mask 内边界层）：埋板(0..20) 的
    ///     mask 边界内层格 密度(≈3167)>地幔(3075) 且在被俯冲内部 → 移除回地幔，
    ///     守恒岩（变质产物）按 85/15 入全局 Accretion；
    ///   · 质量守恒：板0剩余守恒 + Accretion 获得 == 原守恒（换池不消灭）。
    /// </summary>
    [Test]
    public void UpdateSubducted_DeepBurialMetamorphism_RecordsEventsPerPlate()
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int n = g.VertexCount;
        int[] sub = Enumerable.Range(0, n / 2).ToArray();   // 板 0 覆盖北半区 0..20

        var p0 = BuildPlate(0, g, sub);
        foreach (int i in sub)
        {
            p0.Crust.MaficVolcanic[i] = 3300f * 300f;
            p0.Crust.Sedimentary[i] = 2600f * 50f;
            p0.Crust.FelsicPlutonic[i] = 2600f * 20f;
            p0.Crust.Age[i] = 300f * My;
        }
        // 板 1 / 板 2：低密度（2600）顶层，全覆盖 → 每处 count≥2
        var p1 = BuildPlate(1, g, Enumerable.Range(0, n));
        var p2 = BuildPlate(2, g, Enumerable.Range(0, n));
        for (int i = 0; i < n; i++)
        {
            p1.Crust.Sedimentary[i] = 2600f * 200f;
            p2.Crust.Sedimentary[i] = 2600f * 200f;
        }
        sim.Plates.Add(p0); sim.Plates.Add(p1); sim.Plates.Add(p2);
        sim.MergePlatesToMaster();

        double conservedBefore = SumConserved5(p0.Crust);
        Assert.Greater(conservedBefore, 0);

        sim.UpdateSubducted();

        // ── 1) 深埋变质（未消减格）：felsic→meta；已消减格地壳清空、守恒岩转入 Accretion ──
        foreach (int i in sub)
        {
            if (p0.Mask[i] != 1) continue;   // 消减格 → 地壳已清空（见 Accretion 守恒断言）
            // p0（俯冲板）：sediment 0 + sedi 50 + felsicP 20 = 70 单位 → 全部转 p0.Metamorphic
            Assert.AreEqual(0f, p0.Crust.Sediment[i], 1e-3f, $"俯冲板 sediment 清零 {i}");
            Assert.AreEqual(0f, p0.Crust.Sedimentary[i], 1e-3f, $"俯冲板 sedi 清零 {i}");
            Assert.AreEqual(0f, p0.Crust.FelsicPlutonic[i], 1e-3f, $"俯冲板 felsicP 清零 {i}");
            Assert.AreEqual(0f, p0.Crust.FelsicVolcanic[i], 1e-3f, $"俯冲板 felsicV 清零 {i}");
            Assert.AreEqual(2600f * 70f, p0.Crust.Metamorphic[i], 1e-3f, $"俯冲板变质产物 {i}");
        }
        // p2 也是被俯冲板（顶层=板1）→ 它也遍历全部格。矿化事件**无条件按"板×格"计数**
        // （UpdateSubducted 130-131：localSubducted 格不限板内岩性）→ 0..41 全被 p0 与 p2 各计一次
        for (int i = 0; i < n; i++)
        {
            Assert.AreEqual(2f, sim.MineralMeta[i], 1e-6f, $"矿化事件计数（p0+p2 两俯冲板各计一次）{i}");
            Assert.AreEqual(1.0f, sim.MineralHydro[i], 1e-6f, $"热液事件累积 {i}");
        }

        // ── 2) 消减移除（生产修复后应生效）：埋板 mask 内边界层 ∩ 被俯冲内部 ∩ 密度>地幔 ──
        int removed = sub.Count(i => p0.Mask[i] == 0);
        double accCons = SumConserved5(sim.Accretion);
        Assert.Greater(removed, 0, "高密度埋板边缘（mask 内边界层）应被消减移除");
        Assert.Greater(accCons, 0, "消减守恒岩应入全局 Accretion（增生楔）");
        Assert.AreEqual(conservedBefore, SumConserved5(p0.Crust) + accCons, 1.0,
            "消减守恒：板0剩余守恒 + Accretion 获得 == 原守恒（382.2万量级上 float 0.85/0.15 分流舍入漂移 <1）");
        Assert.IsTrue(AllFinite(p0.Crust) && AllFinite(sim.Accretion));
        Assert.IsTrue(AllFinite(sim.MineralMeta) && AllFinite(sim.MineralHydro));
    }

    // ═════════════════════════════════════════════════════════════════
    // 3. UpdateSubducted → ApplyAccretion 端到端：俯冲产物落地顶层板增生楔 + Accretion 复位
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// ApplyAccretion 端到端：手工注入 Accretion（因为 UpdateSubducted 的消减是死路径不会产生增生，
    /// 见上文 ⚠️），验证增生楔按 TopPlateMap 落到各自顶层板的对应格，且 Accretion 全部 8 池复位。
    /// 场景：两块互不重叠的轻板 p0(0..20) / p1(21..41) → Merge 后顶层=板0/板1 各管一半。
    /// </summary>
    [Test]
    public void ApplyAccretion_AppliesWedgeToTopPlatePerCell_ThenResets()
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int n = g.VertexCount;
        var p0 = BuildPlate(0, g, Enumerable.Range(0, n / 2).ToArray());
        var p1 = BuildPlate(1, g, Enumerable.Range(n / 2, n - n / 2).ToArray());
        for (int i = 0; i < n; i++)
        {
            p0.Crust.Sedimentary[i] = 2600f * 200f;   // 轻板
            p1.Crust.Sedimentary[i] = 2600f * 200f;
        }
        sim.Plates.Add(p0); sim.Plates.Add(p1);
        sim.MergePlatesToMaster();

        // 手工注入增生楔：FelsicPlutonic=100 / FelsicVolcanic=20 于全部全局格
        for (int i = 0; i < n; i++)
        {
            sim.Accretion.FelsicPlutonic[i] = 100f;
            sim.Accretion.FelsicVolcanic[i] = 20f;
        }
        double accTotal = SumConserved5(sim.Accretion);
        Assert.Greater(accTotal, 0, "注入的增生楔非空");

        double p0Before = SumConserved5(p0.Crust);
        double p1Before = SumConserved5(p1.Crust);
        sim.ApplyAccretion();

        // 按 TopPlateMap 分派：0..20 → p0，21..41 → p1（恒等映射 li==i）
        for (int i = 0; i < n; i++)
        {
            var top = (int)sim.TopPlateMap[i] == 0 ? p0 : p1;
            int li = top.LocalIdsOfGlobalCells[i];
            Assert.AreEqual(100f, top.Crust.FelsicPlutonic[li], 1e-3f, $"顶板增生 felsicP 格 {i}");
            Assert.AreEqual(20f, top.Crust.FelsicVolcanic[li], 1e-3f, $"顶板增生 felsicV 格 {i}");
        }
        Assert.AreEqual(p0Before + accTotal / 2.0, SumConserved5(p0.Crust), 5.0, "p0 守恒增加 = 其顶层格的增生量");
        Assert.AreEqual(p1Before + accTotal / 2.0, SumConserved5(p1.Crust), 5.0, "p1 守恒增加 = 其顶层格的增生量");
        // Accretion 复位：全部 8 池清零
        foreach (float[] pool in sim.Accretion.AllPools())
            Assert.AreEqual(0.0, pool.Sum(v => Math.Abs(v)), 1e-9f, "Accretion 应用后必须复位（清零）");
        Assert.AreEqual(0.0, SumConserved5(sim.Accretion), 1e-9f);
    }

    // ═════════════════════════════════════════════════════════════════
    // 4. TryMergeCollidingPlates 阈值分支（聚合分支的 MergeTwoPlates 含日志 → 只测安全路径）
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// 保证：Plates.Count>5 但两两接触面积 < minContact(=max(8, n/50)) 时，TryMergeCollidingPlates
    /// 判定无效并安全返回（板块数不变）——这是聚合机制的"阈值下限"分支（既不误并也绝不崩溃）。
    /// 场景：6 块互不接触的小板（各覆盖一小组互不相邻的格），Merge 后接触计数极低。
    /// ⚠️ 聚合成功分支调用私有 MergeTwoPlates → 无条件 LogService.Log → 无引擎进程崩溃，
    ///    故无法在本测试进程触发合并；此处只验证判定路径 + 按源码阈值构造。
    /// </summary>
    [Test]
    public void TryMergeCollidingPlates_SixSparsePlates_BelowThreshold_NoMerge()
    {
        var sim = NewSim(2);
        var g = sim.GlobalGrid;
        int n = g.VertexCount;
        // 6 块小板：每块只覆盖一个格子 → 任两板共享接触边数 ≤ 5（格最大度数），
        // 恒小于关底 minContact=max(8, n/50)=8 → 必然判定"低于阈值"而安全返回。
        var picked = new[] { 0, 7, 14, 21, 28, 35 };
        int id = 0;
        foreach (int c in picked)
        {
            var p = BuildPlate(id++, g, new[] { c });
            p.Crust.Sedimentary[c] = 2600f * 100f;
            sim.Plates.Add(p);
        }
        Assert.AreEqual(6, sim.Plates.Count, "板块数须 > 5 才进入接触判定（而非提前返回）");
        sim.MergePlatesToMaster();

        int countBefore = sim.Plates.Count;
        sim.TryMergeCollidingPlates();

        Assert.AreEqual(countBefore, sim.Plates.Count, "低于阈值不得合并任何板块");
    }

    // ═════════════════════════════════════════════════════════════════
    // 5. RiverSystem 迭代模块 —— 拆分步单测（MapGenTests 只覆盖旧 Compute/RebuildPaths）
    // ═════════════════════════════════════════════════════════════════

    /// <summary>手工链式世界：L0→L1→O(出海口)→W(海洋)，与 MapGenTests 的链式世界一致。</summary>
    private static void ChainWorld(out Vector3[] verts, out int[][] nbs, out float[] elevNorm, out int n)
    {
        verts = new[] {
            new Vector3(0, 1f, 0), new Vector3(0, 0.8f, 0), new Vector3(0, 0.6f, 0),
            new Vector3(0, 0.4f, 0), new Vector3(0, 0.2f, 0), new Vector3(0, 0.05f, 0),
            new Vector3(0, -0.3f, 0),
        };
        nbs = new[] {
            new[] { 1 }, new[] { 0, 2 }, new[] { 1, 3 }, new[] { 2, 4 },
            new[] { 3, 5 }, new[] { 4, 6 }, new[] { 5 },
        };
        elevNorm = new[] { 1.0f, 0.8f, 0.6f, 0.4f, 0.2f, 0.05f, -0.3f };
        n = 7;
    }

    /// <summary>
    /// ComputeFlow 保证：陆地格流向严格更低的最低邻居；无更低则为盆地（自指）；海洋自指终点。
    /// 场景：链式世界（唯一单调下坡路径）。
    /// </summary>
    [Test]
    public void ComputeFlow_ChainWorld_StrictlyDownhillAndSeaSelf()
    {
        ChainWorld(out var verts, out var nbs, out var elevNorm, out int n);
        var flow = new int[n];
        RiverSystem.ComputeFlow(verts, nbs, elevNorm, flow);

        for (int i = 0; i < n; i++)
            Assert.That(flow[i], Is.InRange(0, n - 1), $"flow[{i}] 合法顶点 id");
        Assert.AreEqual(1, flow[0]);
        Assert.AreEqual(2, flow[1]);
        Assert.AreEqual(3, flow[2]);
        Assert.AreEqual(4, flow[3]);
        Assert.AreEqual(5, flow[4]);
        Assert.AreEqual(6, flow[5], "出海口流向海洋");
        Assert.AreEqual(6, flow[6], "海洋格流向自身");
    }

    /// <summary>
    /// ComputeWater 保证：净水量=降水-蒸发，按海拔降序沿流向累积（每格收到全部上游水）。
    /// 场景：3 格链 0(陆高一)→1(陆低)→2(海)；恒 precip=100、temp=0 → 每格净 80。
    /// </summary>
    [Test]
    public void ComputeWater_AccumulatesNetDownstream()
    {
        var verts = new[] { new Vector3(0, 1, 0), new Vector3(0, 0, 0), new Vector3(0, -1, 0) };
        var nbs = new[] { new[] { 1 }, new[] { 0, 2 }, new[] { 1 } };
        var elevNorm = new[] { 1f, 0.5f, -0.5f };
        var flow = new[] { 1, 2, 2 };
        var precip = new[] { 100f, 100f, 100f };
        var temp = new[] { 0f, 0f, 0f };
        var water = new float[3];
        RiverSystem.ComputeWater(verts, elevNorm, flow, precip, temp, water);
        // 净水量 100-(20+0)=80；0→1→2 逐级累积
        Assert.AreEqual(80f, water[0], 1e-4f, "源发生成净 80");
        Assert.AreEqual(160f, water[1], 1e-4f, "接收上游 1 格 = 80×2");
        Assert.AreEqual(240f, water[2], 1e-4f, "入海口累积全部 = 80×3");
    }

    /// <summary>
    /// MarkRiversLakes 保证：水量/阈值 ≥1→河级1、≥16→级2、≥64→级3；陆地盆地且水量≥湖阈值→湖。
    /// 场景：给定 water 数组与阈值直接定点校验。
    /// </summary>
    [Test]
    public void MarkRiversLakes_ThresholdLevelsAndLake()
    {
        var water = new[] { 5f, 5f, 16f, 160f, 640f };
        var flow = new[] { 0, 0, 1, 0, 0 };   // 仅格 0 自指 = 陆地盆地；格 4 非盆地（向 0 排水）
        // ⚠️ 全陆地（原格 4 被误标海洋 -0.2——MarkRiversLakes 对海洋格直接跳过，河级恒 0）
        var elevNorm = new[] { 0.9f, 0.6f, 0.3f, 0.8f, 0.2f };
        float wt = 10f, lt = 3f;
        var rl = new byte[5]; var ll = new byte[5]; var ids = new List<int>();
        RiverSystem.MarkRiversLakes(water, flow, elevNorm, wt, lt, rl, ll, ids);

        // 关卡划分（比例 water/10）：0.5→级0；0.5→级0；1.6→级1；16→级2；64→级3
        Assert.AreEqual((byte)0, rl[0]);
        Assert.AreEqual((byte)0, rl[1]);
        Assert.AreEqual((byte)1, rl[2]);
        Assert.AreEqual((byte)2, rl[3]);
        Assert.AreEqual((byte)3, rl[4], "陆地格 water=640 ≥ 64×10 → 级 3");
        // 湖：唯一陆地盆地格 0，water=5 ≥ 湖阈值 3 → 标湖
        Assert.AreEqual((byte)1, ll[0], "盆地蓄水≥湖阈值 → 湖");
        Assert.AreEqual(1, ids.Count);
        Assert.AreEqual(0, ids[0]);
    }

    // ═════════════════════════════════════════════════════════════════
    // 6. ApplyErosionDepositionV2 —— 输沙单步定点断言（侵蚀下限 / 沉积上限）
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// 保证（沉积分支）：入流泥沙超携带能力 → 沉积，且高度封顶 = 海平面 + depoCap（冲积平原不无限堆高）。
    /// 场景：上游格 0 泄出 carry=50 → 格 1（carry=0）入流 50 > 0 → 沉积；cap-elevM 的余量设为 1m，
    ///        故格 1 恰好抬到 300 = 0(海平面)+300(depoCap)。
    /// </summary>
    [Test]
    public void ApplyErosionDepositionV2_DepositionCap_ClampsToSeaPlusDepoCap()
    {
        var nbs = new[] { new[] { 1 }, new[] { 0, 2 }, new[] { 1 } };
        var flow = new[] { 1, 2, 2 };
        var elevNorm = new[] { 1.0f, 0.5f, -0.3f };
        var elevM = new[] { 1000f, 299f, -300f };
        var water = new[] { 2000f, 0f, 0f };
        var sedIn = new float[3]; var sedOut = new float[3];
        RiverSystem.ApplyErosionDepositionV2(elevM, flow, nbs, elevNorm, water, sedIn, sedOut,
            seaLevelM: 0f);
        // 格 0：sIn=0<carry→侵蚀；carry=0.05·0.5·2000=50，erode=min(50·0.4,40)=20
        Assert.AreEqual(980f, elevM[0], 1e-4f, "上游下切 20m");
        // 格 1：sIn=50>carry0→沉积；min((50)·0.3, 300-299)=min(15,1)=1 → 恰好到 300 上限
        Assert.AreEqual(300f, elevM[1], 1e-4f, "沉积封顶=海平面+depoCap=300");
    }

    /// <summary>
    /// 保证（侵蚀分支下限）：下切不得低于"下游海拔 + minSlope×(自身-海平面)"，从而保持河道单调下降且不挖穿。
    /// 场景：格 0→1，下游 995m；pot 下切 20m 到 980 会低于 996(995+0.001·1000) → 被抬高到 996 下限。
    /// </summary>
    [Test]
    public void ApplyErosionDepositionV2_ErosionFloor_PreservesMonotonicDownstream()
    {
        var nbs = new[] { new[] { 1 }, new[] { 0, 2 }, new[] { 1 } };
        var flow = new[] { 1, 2, 2 };
        var elevNorm = new[] { 1.0f, 0.5f, -0.3f };
        var elevM = new[] { 1000f, 995f, -300f };
        var water = new[] { 2000f, 0f, 0f };
        var sedIn = new float[3]; var sedOut = new float[3];
        RiverSystem.ApplyErosionDepositionV2(elevM, flow, nbs, elevNorm, water, sedIn, sedOut,
            seaLevelM: 0f);
        // 格 1 入流 50>carry0 → 沉积；cap-995=0 → 不动
        Assert.AreEqual(996f, elevM[0], 1e-4f, "下切被下游+minSlope 下限抬高到 996");
        Assert.Greater(elevM[0], elevM[1], "侵蚀沉积后仍保持河道单调（下游更低）");
        Assert.AreEqual(995f, elevM[1], 1e-4f, "下游无沉积余量则不变");
    }

    // ═════════════════════════════════════════════════════════════════
    // 7. ComputeIterative —— 球面世界多轮演化（确定性 / 输出合法 / 流向终止 / 无 NaN）与
    //    ComputeWatersheds 三类归属
    // ═════════════════════════════════════════════════════════════════

    /// <summary>构造一个北高南低的球面陆地世界（n=2，42 格），预热流水线所需全部数组。</summary>
    private static void SlopeBallWorld(out SphereGrid g, out float[] elevNorm, out float[] elevM,
        out float[] precip, out float[] temp)
    {
        g = Sphere(2);
        int n = g.VertexCount;
        var verts = g.Vertices;
        elevNorm = new float[n];
        elevM = new float[n];
        precip = new float[n];
        temp = new float[n];
        const float span = 1000f;
        for (int i = 0; i < n; i++)
        {
            float y = verts[i].Y;              // 北半>0 陆地，南半<0 海洋
            elevNorm[i] = y;
            elevM[i] = y * span;
            precip[i] = 2000f;                 // 充足降水
            temp[i] = y * 20f;                 // 均温随纬度，0~20°C
        }
    }

    /// <summary>
    /// ComputeIterative 保证：多轮侵蚀沉积后输出合法（flow/water/riverLevel/lakeLevel 无 NaN、界内），
    /// 且确定性（同输入两次逐项一致）；每个陆地格沿流向最终终止于盆地或海洋（无环）。
    /// 场景：n=2 球面斜坡世界 + 给定降水/气温，跑多轮。
    /// </summary>
    [Test]
    public void ComputeIterative_SlopeBall_DeterministicLegalAndTerminates()
    {
        SlopeBallWorld(out var gA, out var eNA, out var eMA, out var preA, out var tmpA);
        int n = gA.VertexCount;
        RiverSystem.ComputeIterative(gA.Vertices, gA.Neighbors, eNA, eMA, preA, tmpA,
            waterThreshold: 2000f, lakeThreshold: 200f, seaLevelM: 0f, elevSpan: 1000f, rounds: 5,
            out int[] flowA, out float[] waterA, out byte[] rlA, out byte[] llA, out var pathsA);

        // 合法 & 无 NaN
        for (int i = 0; i < n; i++)
        {
            Assert.That(flowA[i], Is.InRange(0, n - 1), $"flow[{i}] 界内");
            Assert.IsFalse(float.IsNaN(waterA[i]) || float.IsInfinity(waterA[i]), $"water[{i}] 有限");
            Assert.IsFalse(float.IsNaN(eMA[i]) || float.IsInfinity(eMA[i]), $"elevM[{i}] 有限");
            Assert.IsFalse(float.IsNaN(eNA[i]) || float.IsInfinity(eNA[i]), $"elevNorm[{i}] 有限");
        }
        foreach (byte b in rlA) Assert.That(b, Is.InRange((byte)0, (byte)3), "河流级别 0..3");
        foreach (byte b in llA) Assert.That(b, Is.InRange((byte)0, (byte)1), "湖泊级别 0..1");
        // 大陆上至少应形成河（水量充分）——保证场景真实走通侵蚀/成河路径
        Assert.Greater(rlA.Sum(v => v), 0, "充足降水下应形成河流");

        // 流向终止性：每个陆地非盆地格沿 flow 必然终止于盆地/海洋（无死环）
        for (int start = 0; start < n; start++)
        {
            if (eNA[start] < 0f) continue;   // 海洋跳过
            int cur = start, steps = 0;
            while (steps++ <= n)
            {
                int nxt = flowA[cur];
                if (nxt == cur) break;                       // 盆地终点
                if (eNA[nxt] < 0f) { /* 入海 */ break; }
                Assert.Less(eNA[nxt], eNA[cur], $"格 {start}→{cur} 沿流向必须严格下坡（无环）");
                cur = nxt;
            }
            Assert.LessOrEqual(steps, n + 1, $"格 {start} 流向链应在有限步内终止");
        }

        // 确定性：重新构造一个相同世界跑一遍 → 逐项一致
        SlopeBallWorld(out _, out var eNB, out var eMB, out var preB, out var tmpB);
        RiverSystem.ComputeIterative(gA.Vertices, gA.Neighbors, eNB, eMB, preB, tmpB,
            waterThreshold: 2000f, lakeThreshold: 200f, seaLevelM: 0f, elevSpan: 1000f, rounds: 5,
            out int[] flowB, out float[] waterB, out byte[] rlB, out byte[] llB, out _);
        CollectionAssert.AreEqual(flowA, flowB);
        for (int i = 0; i < n; i++) Assert.AreEqual(waterA[i], waterB[i], 1e-4f, $"water[{i}]");
        CollectionAssert.AreEqual(rlA, rlB);
        CollectionAssert.AreEqual(llA, llB);
    }

    /// <summary>
    /// ComputeWatersheds 保证三类归属：河格（含汇入河的非河格 → 同一河流域）、内流盆地（新独立 id）、
    /// 边缘排水区（-1）。场景：7 格显式图 —— 河链 0→1→2 入海；格 4 汇入河格 2；格 5 内流盆地；格 6 直接入海。
    /// </summary>
    [Test]
    public void ComputeWatersheds_RiverInlandAndEdgeDrainage()
    {
        var elevNorm = new[] { 0.9f, 0.6f, 0.3f, -0.2f, 0.5f, 0.1f, 0.2f };
        var flow = new[] { 1, 2, 3, 3, 2, 5, 3 };   // 3=海该自指；链 0→1→2→海3
        var riverLevel = new byte[] { 1, 1, 1, 0, 0, 0, 0 };

        RiverSystem.ComputeWatersheds(elevNorm, flow, riverLevel, out int[] ws, out var outlets);

        // 河格流域：0/1/2 同一条河的流域
        Assert.AreEqual(ws[0], ws[1], "河链同流域");
        Assert.AreEqual(ws[1], ws[2]);
        Assert.GreaterOrEqual(ws[0], 0, "河格流域非负");
        // 汇入河的非河格 4 继承河流域
        Assert.AreEqual(ws[2], ws[4], "汇入河格的非河格继承河流域");
        // 内流盆地：格 5 得独立新 id
        Assert.GreaterOrEqual(ws[5], 0);
        Assert.AreNotEqual(ws[0], ws[5], "内流盆地独立于河渠流域");
        // 边缘排水区：格 6 直接入海 → -1
        Assert.AreEqual(-1, ws[6], "直接入海非河格 = 边缘排水区(-1)");
        // 海洋格 3 = -1（不计入任何流域）
        Assert.AreEqual(-1, ws[3]);
        // outlets：河出口(2) + 内流盆地(5)；边缘排水区不设出口
        CollectionAssert.AreEquivalent(new[] { 2, 5 }, outlets);
    }
}
