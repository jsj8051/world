using System;
using NUnit.Framework;
using Godot;
using World.NewHexWorld;
using World.NewHexWorld.Plate;

namespace World.Tests;

/// <summary>
/// 方向性风沙护栏（气候批次 3，2026-09-19 v1.23，H3AeolianTransport）：侵蚀→搬运→沉积**沿风向**走：
///   · 力度 = Bagnold 立方律 (|u|−u_t)³ × 干燥度 × 裸露度（解析锚点）；
///   · 方向 = 风模板的落格邻居（风往哪吹沙往哪搬）；
///   · 落沙 = 风变弱沿途沉降、爬坡不动（山挡沙）、入海落大陆架（海格汇）；
///   · 守恒：只动 sediment 池，组总量严格不变、Σ侵蚀 = Σ沉积；
///   · 确定性：同入参逐位一致。
/// 地形 = 全平小球；风速场/降水/温度全部测试直喂（不掺气候骨架）。
/// 纪律（同 H3FluvialTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3AeolianTests
{
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
    static Ball Ball => SharedBall.Value;

    const float Stock = 1e6f;

    static H3PlateFields MakeFields(int n, int sourceCell)
    {
        var f = new H3PlateFields(n);
        f.Sediment[sourceCell] = Stock;        // 只在源格铺松散沙 ⇒ 源 = 唯一侵蚀点
        return f;
    }

    /// <summary>试验格对（源格 + 其风沙落格邻居）：方向性 aeolian 测试的公共前置。</summary>
    static (int src, int down) SourcePair(H3Wind.UpwindStencil stencil)
    {
        for (int i = 0; i < Ball.CellIds.Length; i++)
            if (stencil.AeolianDownwind[i] >= 0)
            {
                int down = stencil.AeolianDownwind[i];
                Assert.AreNotEqual(i, down, "落格邻居不应是自身");
                return (i, down);
            }
        throw new InvalidOperationException("res1 球上应有风沙落格方向明确的格");
    }

    static float[] Uniform(int n, float v)
    {
        var a = new float[n];
        Array.Fill(a, v);
        return a;
    }

    /// <summary>解析锚点（与实现同式）：源格一步的风蚀量 = min(效率 × 能力, 存量)，
    /// 能力 = Ka·(|u|−u_t)³·ρ空气·干燥度·裸露度（干燥全裸平地 = 1）。</summary>
    static float ExpectedErode(float windMS, float elevationM)
    {
        float rhoAir = MathF.Exp(-MathF.Max(elevationM, 0f) / H3AeolianTransport.AirDensityScaleHeightM);
        float excess = windMS - H3AeolianTransport.WindStartThresholdMS;
        float capacity = H3AeolianTransport.CapacityCoefKgPerM2 * excess * excess * excess * rhoAir;
        return MathF.Min(H3AeolianTransport.ErodeEfficiency * capacity, Stock);
    }

    [Test]
    public void Transport_MovesDownwind_AnalyticBagnoldAnchor()
    {
        // 干燥全裸平地 + 源格 12 m/s 单点供沙：源格失量 = Bagnold 锚点，全部落到**下风邻居**格
        // （下风格无风 ⇒ 能力 0 ⇒ 沉降率满额）。风往哪吹沙往哪搬——方向性 aeolian 的核心断言。
        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var (src, down) = SourcePair(stencil);

        int n = Ball.CellIds.Length;
        var fields = MakeFields(n, src);
        var height = Uniform(n, 100f);                       // 全陆平地（无汇、无爬坡）
        var wind = Uniform(n, 0f);
        wind[src] = 12f;                                     // Ferrel 海平面档，只有源格起风
        var precip = Uniform(n, 0f);                         // P = 0 ⇒ 干燥度 1、裸露度 1
        var temp = Uniform(n, 20f);

        var aeolian = new H3AeolianTransport(Ball);
        aeolian.Apply(fields, height, wind, stencil, precip, temp, scale: 1f);

        float expected = ExpectedErode(12f, 100f);
        Assert.Greater(expected, 0f, "12 m/s 干燥全裸格应产沙");
        Assert.That(fields.Sediment[src], Is.EqualTo(Stock - expected).Within(expected * 1e-3f),
            "源格失量应 = Bagnold 解析锚点");
        Assert.That(fields.Sediment[down], Is.EqualTo(expected).Within(expected * 1e-3f),
            "沙应全部落到下风邻居格（下风无风 ⇒ 沉降率满额）");
        Assert.That(aeolian.DepositedMassLastStep,
            Is.EqualTo(aeolian.ErodedMassLastStep).Within(aeolian.ErodedMassLastStep * 1e-4),
            "Σ侵蚀 = Σ沉积（回合内全部落定）");
    }

    [Test]
    public void Conservation_GroupTotalInvariant_OnlySedimentPoolMoves()
    {
        // 沙丘链世界：全图铺沙 + 全图起风——搬运在 sediment 池内部流动，组总量严格不变。
        int n = Ball.CellIds.Length;
        var fields = new H3PlateFields(n);
        for (int i = 0; i < n; i++) fields.Sediment[i] = 5000f;
        double before = 0;
        foreach (var pool in fields.ConservedPools())
            for (int i = 0; i < pool.Length; i++) before += pool[i];

        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var height = Uniform(n, 300f);
        var wind = Uniform(n, 12f);
        var precip = Uniform(n, 0f);
        var temp = Uniform(n, 20f);

        var aeolian = new H3AeolianTransport(Ball);
        aeolian.Apply(fields, height, wind, stencil, precip, temp, scale: 1f);

        double after = 0;
        foreach (var pool in fields.ConservedPools())
            for (int i = 0; i < pool.Length; i++) after += pool[i];
        Assert.That(after, Is.EqualTo(before).Within(before * 1e-5), "风沙只在守恒组内搬运：组总量必须严格不变");
        Assert.Greater(aeolian.ErodedMassLastStep, 0.0, "干燥大风世界应有风蚀");
        Assert.That(aeolian.DepositedMassLastStep,
            Is.EqualTo(aeolian.ErodedMassLastStep).Within(aeolian.ErodedMassLastStep * 1e-6),
            "在途负载回合内必须全部落定（float 记账精度内 Σ侵蚀 = Σ沉积）");
    }

    [Test]
    public void Gates_WeakWindOrWetWorld_KillsAeolian()
    {
        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var (src, _) = SourcePair(stencil);
        int n = Ball.CellIds.Length;
        var height = Uniform(n, 100f);
        var temp = Uniform(n, 20f);

        // 弱风（4 m/s < 起动阈值 5）⇒ 严格 0
        var weakFields = MakeFields(n, src);
        var weakWind = Uniform(n, 4f);
        new H3AeolianTransport(Ball).Apply(weakFields, height, weakWind, stencil,
            Uniform(n, 0f), temp, 1f);
        Assert.AreEqual(0.0, H3AeolianEroded(weakWind, height, weakFields, stencil, Uniform(n, 0f), temp), 1e-9,
            "风速低于起动阈值 ⇒ 不卷沙");
        Assert.AreEqual(Stock, weakFields.Sediment[src], 1f, "弱世界源格存量纹丝不动");

        // 湿世界（P = 5000 ⇒ 干燥度 0.22、裸露度 0.1）⇒ 被压到干燥世界的 ~2 成以下
        var wetWind = Uniform(n, 12f);
        var dryWind = Uniform(n, 12f);
        double wet = H3AeolianEroded(wetWind, height, MakeFields(n, src), stencil, Uniform(n, 5000f), temp);
        double dry = H3AeolianEroded(dryWind, height, MakeFields(n, src), stencil, Uniform(n, 0f), temp);
        Assert.Less(wet, dry * 0.3, "湿润世界风蚀必须被干燥度×裸露度压死（干燥度 0.22 × 裸露度 0.1）");
    }

    double H3AeolianEroded(float[] wind, float[] height, H3PlateFields fields,
        H3Wind.UpwindStencil stencil, float[] precip, float[] temp)
    {
        var aeolian = new H3AeolianTransport(Ball);
        aeolian.Apply(fields, height, wind, stencil, precip, temp, 1f);
        return aeolian.ErodedMassLastStep;
    }

    [Test]
    public void MountainBlocksSand_ClimbLimitDepositsAtFoot()
    {
        // 下风格比源格高 > MaxClimbM ⇒ 沙爬不上山，就地落（山挡沙/山前堆积）：源格存量净额不变、
        // 下风格分毫未得，但蚀/淤照记（物理上"起了沙又落回原地"）。
        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var (src, down) = SourcePair(stencil);
        int n = Ball.CellIds.Length;

        var fields = MakeFields(n, src);
        var height = Uniform(n, 100f);
        height[down] = 100f + H3AeolianTransport.MaxClimbM + 500f;   // 挡沙墙
        var wind = Uniform(n, 12f);
        wind[src] = 12f;

        var aeolian = new H3AeolianTransport(Ball);
        aeolian.Apply(fields, height, wind, stencil, Uniform(n, 0f), Uniform(n, 20f), 1f);

        Assert.Greater(aeolian.ErodedMassLastStep, 0.0, "起风应卷沙");
        Assert.That(aeolian.DepositedMassLastStep, Is.EqualTo(aeolian.ErodedMassLastStep).Within(aeolian.ErodedMassLastStep * 1e-4), "全部落回");
        Assert.AreEqual(Stock, fields.Sediment[src], 1f, "山挡沙 ⇒ 沙落回坡脚（源格净额不变）");
        Assert.AreEqual(0f, fields.Sediment[down], 1f, "下风格翻不过山 ⇒ 分毫未得");
    }

    [Test]
    public void OceanSink_DeliversSandToShelf()
    {
        // 下风是海格 ⇒ 全部落大陆架（陆源尘上大陆架，与河流三角洲同款汇）。
        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var (src, down) = SourcePair(stencil);
        int n = Ball.CellIds.Length;

        var fields = MakeFields(n, src);
        var height = Uniform(n, 0f);                         // 全海
        height[src] = 100f;                                  // 源格 = 岸上孤岛
        var wind = Uniform(n, 12f);
        wind[src] = 12f;

        var aeolian = new H3AeolianTransport(Ball);
        aeolian.Apply(fields, height, wind, stencil, Uniform(n, 0f), Uniform(n, 20f), 1f);

        float expected = ExpectedErode(12f, 100f);
        Assert.That(fields.Sediment[down], Is.EqualTo(expected).Within(expected * 1e-3f),
            "下风海格应接住全部来沙（大陆架料）");
        Assert.That(aeolian.DeliveredToOceanLastStep, Is.EqualTo(aeolian.ErodedMassLastStep).Within(aeolian.ErodedMassLastStep * 1e-4),
            "入海判读口 = 全部侵蚀量");
    }

    [Test]
    public void Deterministic_SameInputsBitwiseIdentical()
    {
        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var (src, _) = SourcePair(stencil);
        int n = Ball.CellIds.Length;
        var height = Uniform(n, 300f);
        var wind = Uniform(n, 12f);
        var precip = Uniform(n, 0f);
        var temp = Uniform(n, 20f);

        var a = new H3PlateFields(n);
        var b = new H3PlateFields(n);
        for (int i = 0; i < n; i++) { a.Sediment[i] = 5000f; b.Sediment[i] = 5000f; }

        new H3AeolianTransport(Ball).Apply(a, height, wind, stencil, precip, temp, 1f);
        new H3AeolianTransport(Ball).Apply(b, height, wind, stencil, precip, temp, 1f);

        CollectionAssert.AreEqual(a.ConservedPools(), b.ConservedPools(), "五场逐位不一致");
    }
}
