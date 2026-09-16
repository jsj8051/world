using System;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 俯冲再循环（v1.14 陆增棘轮对冲）测试：埋入层守恒组按 `RecycleSedimentFraction` /
/// `RecycleFelsicFraction` 随俯冲回地幔，其余埋进上盘。
/// 断言的测量项：沉积类全额/长英质类按刮削比例的解析值、旋钮归零退回老口径、
/// "目标自身移走 = 唯一落位为埋入层"时整柱保留不回收（无俯冲消费就没有回收）、
/// 质量账本闭环（target + 回收 = source）。
/// 纪律（同 H3PlateJamTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService；浮点断言带容差。
/// </summary>
public class H3RecyclingTests
{
    private const int Res = 1;              // 842 格，构造便宜
    private const float OldAgeMy = 300f;    // 密度饱和 3300（> 目标年轻洋壳 2890 → 更密 = 俯冲）
    private const float YoungAgeMy = 0f;
    private const float SedimentMass = 500f;      // 俯冲板上的沉积物（侵蚀产物）
    private const float FelsicDustMass = 1000f;   // 俯冲板驮着的长英质薄层（陆源尘）

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;

    static readonly MaterialDensity Material = new();

    // ── 工具（同 H3PlateJamTests 口径）──

    static (int i, int j) AdjacentPair()
    {
        int i = 0;
        while (Ball.CellNeighbors[i].Length == 0) i++;
        return (i, Ball.CellNeighbors[i][0]);
    }

    /// <summary>老洋壳柱（密度 3300）+ 沉积物 + 长英质薄层：俯冲板 = 棘轮对冲的作用对象。</summary>
    static void FillSubductingSlab(H3PlateFields fields, int cell)
    {
        fields.ClearCell(cell);
        fields.MaficVolcanic[cell] = Material.MaficVolcanicMin * 7100f;
        fields.Age[cell] = OldAgeMy;
        fields.Sediment[cell] = SedimentMass;
        fields.FelsicPlutonic[cell] = FelsicDustMass;
    }

    /// <summary>年轻洋壳柱（密度 2890，更轻 → 被俯冲的上盘）。</summary>
    static void FillYoungOceanic(H3PlateFields fields, int cell)
    {
        fields.ClearCell(cell);
        fields.MaficVolcanic[cell] = Material.MaficVolcanicMin * 7100f;
        fields.Age[cell] = YoungAgeMy;
    }

    static H3PlateMotion ManualMotion(int cellCount, params (int cell, Vector3 dir)[] flows)
    {
        var motion = new H3PlateMotion(cellCount)
        {
            PlateFired = Enumerable.Repeat(true, 8).ToArray(),
        };
        foreach (var (cell, dir) in flows) motion.Velocity[cell] = dir * 0.01f;
        return motion;
    }

    static double TotalOf(H3PlateFields f)
    {
        double sum = 0;
        for (int i = 0; i < f.Count; i++) sum += f.TotalMass(i);
        return sum;
    }

    // ═══════════════════════════════════════════════════════════════
    // 主路径：埋入层守恒组按比例回收
    // ═══════════════════════════════════════════════════════════════

    static (H3PlateAdvection adv, H3PlateFields target) RunSubduction(float sedFraction, float felFraction)
    {
        var (i, j) = AdjacentPair();
        var source = new H3PlateFields(Ball.CellIds.Length);
        FillSubductingSlab(source, i);                   // 来料更密（3300）→ 俯冲进 j
        source.PlateId[i] = 0;
        FillYoungOceanic(source, j);                     // 上盘年轻洋壳（2890）
        source.PlateId[j] = 1;

        var toJ = Ball.CellCenters[j] - Ball.CellCenters[i];
        var motion = ManualMotion(Ball.CellIds.Length, (i, toJ));   // 只有 i 动：j 驻留承接埋入
        var target = new H3PlateFields(Ball.CellIds.Length);
        var advection = new H3PlateAdvection(Ball.CellIds.Length)
        {
            RecycleSedimentFraction = sedFraction,
            RecycleFelsicFraction = felFraction,
        };
        advection.Step(Ball, source, motion, target, Material, stepIndex: 0);
        return (advection, target);
    }

    [Test]
    public void Subduction_RecyclesBuriedConserved_ByKnob()
    {
        var (adv, target) = RunSubduction(sedFraction: 1f, felFraction: 0.2f);
        var j = AdjacentPair().j;

        // 解析锚点：沉积类全额回收 → 0；长英质按 20% 刮削 → 留 800
        Assert.AreEqual(0f, target.Sediment[j], 1e-3f, "沉积类全额随俯冲回地幔");
        Assert.AreEqual(0f, target.Sedimentary[j], 1e-3f, "沉积岩类按沉积口径全额回收");
        Assert.AreEqual(FelsicDustMass * 0.8f, target.FelsicPlutonic[j], FelsicDustMass * 1e-3f,
            "长英质按刮削比例留下（1 − 0.2）");
        Assert.AreEqual(SedimentMass + FelsicDustMass * 0.2f, adv.RecycledConservedMass, 1e-3,
            "守恒组回收判读口 = 沉积全额 + 长英质刮削量");
        Assert.Greater(adv.RecycledToMantleMass, adv.RecycledConservedMass, "总量还含俯冲 mafic 消减");

        // 埋入后顶层 = 目标板（不翻色）；俯冲链头腾空成空洞
        Assert.AreEqual(1, target.PlateId[j], "顶层归属保持上盘");
        Assert.AreEqual(1, adv.MixedCellCount, "埋入格 = 异板混合格");
        Assert.AreEqual(1, adv.HoleCount, "俯冲链头腾空");

        // 质量账本闭环：target + 回收 = source（俯冲是唯一出口）
        double sourceTotal = TotalOf(TestSource());
        double targetTotal = TotalOf(target);
        Assert.That(targetTotal + adv.RecycledToMantleMass,
            Is.EqualTo(sourceTotal).Within(sourceTotal * 1e-6), "质量守恒：目标 + 回地幔 = 来料");
    }

    [Test]
    public void Subduction_ZeroFractions_PreservesBuriedConserved()
    {
        var (adv, target) = RunSubduction(sedFraction: 0f, felFraction: 0f);
        var j = AdjacentPair().j;

        Assert.AreEqual(SedimentMass, target.Sediment[j], 1e-3f, "旋钮归零 = 老口径：沉积全额埋进上盘");
        Assert.AreEqual(FelsicDustMass, target.FelsicPlutonic[j], 1e-3f, "旋钮归零 = 老口径：长英质全额埋进上盘");
        Assert.AreEqual(0.0, adv.RecycledConservedMass, "守恒组回收判读口归零");
    }

    // ═══════════════════════════════════════════════════════════════
    // 无俯冲消费不回收：目标自身移走 → 唯一落位 = 埋入层 → 整柱保留
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Subduction_TargetMovedAway_FullColumnPreserved_NoRecycle()
    {
        // 三格流链 i→j→k 全员起跳：i 俯冲进 j，但 j 自己也前移走 ⇒ j 格唯一落位 = 埋入层
        //（无上盘顶层 = 无俯冲消费）⇒ 整柱保留、不回收——与 buriedLoss 的 allBuried 口径一致。
        var (i, j) = AdjacentPair();
        int k = Ball.CellNeighbors[j].First(nb => nb != i);
        int m = Ball.CellNeighbors[k].First(nb => nb != j && nb != i);

        var source = new H3PlateFields(Ball.CellIds.Length);
        FillSubductingSlab(source, i);
        source.PlateId[i] = 0;
        FillYoungOceanic(source, j);
        source.PlateId[j] = 1;
        FillYoungOceanic(source, k);
        source.PlateId[k] = 1;

        var motion = ManualMotion(Ball.CellIds.Length,
            (i, Ball.CellCenters[j] - Ball.CellCenters[i]),
            (j, Ball.CellCenters[k] - Ball.CellCenters[j]),
            (k, Ball.CellCenters[m] - Ball.CellCenters[k]));   // k 流向链外邻格（m 空格 → k 停驻原地）

        var target = new H3PlateFields(Ball.CellIds.Length);
        var adv = new H3PlateAdvection(Ball.CellIds.Length)
        {
            RecycleSedimentFraction = 1f,
            RecycleFelsicFraction = 0.2f,
        };
        adv.Step(Ball, source, motion, target, Material, stepIndex: 0);

        Assert.AreEqual(SedimentMass, target.Sediment[j], 1e-3f, "无上盘顶层 ⇒ 沉积不回收（整柱保留）");
        Assert.AreEqual(FelsicDustMass, target.FelsicPlutonic[j], 1e-3f, "无上盘顶层 ⇒ 长英质不回收");
        Assert.AreEqual(0.0, adv.RecycledConservedMass, "守恒组回收判读口 = 0");
        // mafic 确有消减，但来自同板多源"加权平均差额摊不出去"（j 已陆化、k 是洋 → 无同相同板邻居
        // 可摊，兜底回地幔）——用 OverflowToMantle 对账，证明它不是俯冲消费（04 批次 4/6 口径）。
        Assert.AreEqual(adv.OverflowToMantleMass, adv.RecycledToMantleMass, 1e-6,
            "mafic 消减应全部来自摊出兜底而非俯冲再循环");
        Assert.AreEqual(0, target.PlateId[j], "唯一落位 = 埋入层 ⇒ 归属 = 埋入板（04 批次 4 口径）");
    }

    // TestSource：与 RunSubduction 相同的初始世界（供账本对账取 source 总量）。
    static H3PlateFields TestSource()
    {
        var (i, j) = AdjacentPair();
        var source = new H3PlateFields(Ball.CellIds.Length);
        FillSubductingSlab(source, i);
        source.PlateId[i] = 0;
        FillYoungOceanic(source, j);
        source.PlateId[j] = 1;
        return source;
    }
}
