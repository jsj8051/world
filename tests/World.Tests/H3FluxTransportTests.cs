using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 通量化运输行为测试（设计-07 P3 第一片验收；P3 第二批.5 注壳预算闭环补钉）：
///   · 均匀平移：全池总量守恒（纯转移零记账）；
///   · 顶死阻断：大陆来料撞洋格 = 通量阻断（来料格质量原地保留 = 造山加厚）；
///   · 俯冲极性：老洋来料撞年轻洋格 = 俯冲汇（回地幔 + 板片账户 + 弧回流）；
///   · 注壳闭环：注壳只落真离散板缘（异板邻边发散）格，总量 ≤ 当步俯冲回地幔量；
///   · 确定性：同输入两跑逐位一致。
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3FluxTransportTests
{
    const int Res = 1;
    const float Speed = 9.2e-3f;                     // rad/My ≈ 5.9 cm/yr（跨缝份额 ~0.3）
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;
    static readonly MaterialDensity Material = new();
    const float OceanColumnM = 2890f * 7100f;        // 标准洋壳质量面密度

    static H3PlateFields TwoPlateOcean()
    {
        var f = new H3PlateFields(Ball.CellIds.Length);
        for (int i = 0; i < f.Count; i++)
        {
            f.PlateId[i] = Ball.CellCenters[i].X > 0f ? 0 : 1;
            f.MaficVolcanic[i] = OceanColumnM;
        }
        return f;
    }

    /// <summary>逐格切向速度：指向 dirWorld 的切向投影 × Speed。</summary>
    static Vector3[] VelocityToward(Vector3 dirWorld)
    {
        var v = new Vector3[Ball.CellIds.Length];
        for (int i = 0; i < v.Length; i++)
        {
            var radial = Ball.CellDirs[i];
            var d = dirWorld - radial * dirWorld.Dot(radial);
            v[i] = d.Normalized() * Speed;
        }
        return v;
    }

    static double TotalMass(H3PlateFields f)
    {
        double sum = 0;
        for (int i = 0; i < f.Count; i++) sum += f.TotalMass(i);
        return sum;
    }

    [Test]
    public void UniformTranslation_ConservesTotalMass_AndMovesIt()
    {
        var src = TwoPlateOcean();
        for (int i = 0; i < src.Count; i++) src.PlateId[i] = 0;      // 单板：无_foreign 边，纯转移
        double before = TotalMass(src);
        double westBefore = 0;
        for (int i = 0; i < src.Count; i++)
            if (Ball.CellCenters[i].X <= 0f) westBefore += src.MaficVolcanic[i];

        var tgt = new H3PlateFields(src.Count);
        new H3FluxTransport(Ball).Step(src, tgt, VelocityToward(new Vector3(-1f, 0f, 0f)), Material, 4f, 0);

        Assert.Less(Math.Abs(TotalMass(tgt) - before), before * 1e-4, "纯转移零记账：全池总量守恒（float 舍入容差）");
        double westAfter = 0;
        for (int i = 0; i < tgt.Count; i++)
            if (Ball.CellCenters[i].X <= 0f) westAfter += tgt.MaficVolcanic[i];
        Assert.Greater(westAfter, westBefore, "向 −x 的平移应把质量搬到 −x 侧");
    }

    [Test]
    public void Jam_ContinentalIntoOcean_BlocksTransfer()
    {
        var src = TwoPlateOcean();
        for (int i = 0; i < src.Count; i++)
        {
            if (Ball.CellCenters[i].X > 0f)
            {
                src.ClearCell(i);                                       // 大陆：轻（2700）
                src.FelsicPlutonic[i] = Material.FelsicPlutonic * 35000f;
            }
            else
            {
                src.Age[i] = 300f;                                      // 老洋：重（3300）
            }
        }

        // 找跨缝大陆格（来料方）
        int donor = -1;
        for (int i = 0; i < src.Count && donor < 0; i++)
        {
            if (Ball.CellCenters[i].X <= 0f) continue;
            foreach (int nb in Ball.CellNeighbors[i])
                if (Ball.CellCenters[nb].X <= 0f) { donor = i; break; }
        }
        double donorMass = src.TotalMass(donor);

        var tgt = new H3PlateFields(src.Count);
        var transport = new H3FluxTransport(Ball);
        transport.Step(src, tgt, VelocityToward(new Vector3(-1f, 0f, 0f)), Material, 4f, 0);

        // 顶死阻断：自身陆壳分毫未失（出流被阻断；同板邻居的流入堆积 = 造山加厚，允许增）；
        // 背后大洋的俯冲给大陆送来弧岩浆（安第斯式增生）
        Assert.GreaterOrEqual(tgt.FelsicPlutonic[donor], src.FelsicPlutonic[donor] - 1f,
            "顶死阻断：大陆来料的自身陆壳不应流失");
        Assert.Greater(tgt.Sediment[donor] + tgt.MaficVolcanic[donor], src.Sediment[donor] + src.MaficVolcanic[donor],
            "背后大洋的俯冲/通量应给大陆缝边送来质量（安第斯式增生）");
        Assert.Greater(transport.SlabInflow.Count, 0, "大洋俯冲应有板片账户入账");
    }

    [Test]
    public void Subduction_OldOceanicIntoYoung_RecyclesWithSlab()
    {
        var src = TwoPlateOcean();
        for (int i = 0; i < src.Count; i++)
        {
            if (Ball.CellCenters[i].X > 0f) src.Age[i] = 300f;          // 板 0：老洋（密 3300）
            else src.Age[i] = 0f;                                       // 板 1：年轻（密 2890）
        }

        var tgt = new H3PlateFields(src.Count);
        var transport = new H3FluxTransport(Ball);
        transport.Step(src, tgt, VelocityToward(new Vector3(-1f, 0f, 0f)), Material, 4f, 0);

        Assert.Greater(transport.SubductEdgesLastStep, 0, "洋-洋汇聚应判俯冲（老撞年轻）");
        Assert.Greater(transport.RecycledToMantleLastStep, 0, "俯冲份额应回地幔（销毁账）");
        Assert.AreEqual(0.0, transport.ArcFelsicReturnedMassLastStep,
            "洋-洋俯冲不得触发弧回流（与跳格路径同口径；白化球教训——通量路径曾对一切俯冲边付弧）");
        Assert.Greater(transport.SlabInflow.Count, 0, "板片账户应有入账条目");
        Assert.AreEqual(0, transport.SlabInflow[0].plate, "俯冲质量记给来料板（板 0）");
        Assert.Less(transport.SlabInflow[0].dir.X, 0f, "板片方向应指向 −x（来料流向）");
    }

    [Test]
    public void ArcReturn_OceanicIntoContinental_MintsFelsicOnLand()
    {
        // 板 0 = 老洋（西，密 3300，向 +x 撞）、板 1 = 大陆（东，长英质）：洋→陆俯冲 ⇒ 弧回流
        var src = TwoPlateOcean();
        for (int i = 0; i < src.Count; i++)
        {
            if (Ball.CellCenters[i].X > 0f)
            {
                src.ClearCell(i);
                src.FelsicPlutonic[i] = Material.FelsicPlutonic * 35000f;   // 大陆上盘
            }
            else src.Age[i] = 300f;                                         // 老洋来料（俯冲方）
        }

        var tgt = new H3PlateFields(src.Count);
        var transport = new H3FluxTransport(Ball);
        transport.Step(src, tgt, VelocityToward(new Vector3(-1f, 0f, 0f)), Material, 4f, 0);

        Assert.Greater(transport.SubductEdgesLastStep, 0, "布置检查：洋→陆应判俯冲（洋壳密度大）");
        Assert.Greater(transport.ArcFelsicReturnedMassLastStep, 0, "洋→陆俯冲应在上盘陆格新生弧岩浆（创建账）");
    }

    [Test]
    public void Injection_ConfinedToDivergentEdges_AndBudgetCapped()
    {
        // 场景：板 0（东带 X>0.3）向 +x 退离缝、板 1（其余）向 −x 退离缝 ⇒ 缝 X≈0.3 两侧相互远离
        // = 真离散板缘；板 2 = −x 极区单格，周围洋壳流向它 = 俯冲汇（预算来源）。
        var src = RidgeTrenchScene(out int poleCell, out int eastPoleCell);
        var v = RidgeTrenchVelocity();

        var tgt = new H3PlateFields(src.Count);
        var transport = new H3FluxTransport(Ball);
        transport.Step(src, tgt, v, Material, 4f, 0);

        Assert.Greater(transport.RecycledToMantleLastStep, 0, "布置检查：极区俯冲汇应有回地幔销毁（预算来源）");
        Assert.Greater(transport.RidgeCellsLastStep, 0, "布置检查：离散缝上应有合格薄柱格");
        Assert.Greater(transport.CreatedMassLastStep, 0, "离散缝薄柱应注入新洋壳（创建账）");

        // 预算闭环（白化球教训）：注壳不得超过当步俯冲回地幔量——威尔逊旋回按构造平衡
        Assert.LessOrEqual(transport.CreatedMassLastStep, transport.RecycledToMantleLastStep * 1.000001,
            "注壳预算闭环：脊上增生 ≤ 海沟销毁（无差别注壳的净增生通道被堵死）");

        // 板缘限定：注入格（Age 翻 0）必须都是"异板邻边发散"格；板内薄柱（+x 极区）与
        // 汇聚边上盘（−x 极区）一律无注壳资格
        for (int i = 0; i < tgt.Count; i++)
        {
            if (tgt.Age[i] != 0f)
                continue;
            Assert.IsTrue(IsDivergentForeignEdge(src, i, v),
                $"格 {i} 被注入但无发散异板边——注壳必须只落真离散板缘");
        }
        Assert.AreEqual(50f, tgt.Age[eastPoleCell], "板内薄柱（无异板边）不得注壳");
        Assert.AreEqual(50f, tgt.Age[poleCell], "汇聚边上盘格不得注壳（发散资格不含收敛边）");
    }

    [Test]
    public void Injection_DemandBelowBudget_InjectsExactlyDemand()
    {
        // 阈值压到略高于初始柱厚 ⇒ 需求远小于预算 ⇒ 按需注入、结余不花（闭环的另一半）
        var src = RidgeTrenchScene(out _, out _);
        var v = RidgeTrenchVelocity();

        var tgt = new H3PlateFields(src.Count);
        var transport = new H3FluxTransport(Ball) { NewCrustThicknessThresholdM = 2900f };
        transport.Step(src, tgt, v, Material, 4f, 0);

        Assert.Greater(transport.CreatedMassLastStep, 0, "阈值略抬即应产生注壳需求");
        Assert.LessOrEqual(transport.InjectionDemandLastStep, transport.InjectionBudgetLastStep,
            "布置检查：需求应小于预算（按需注入分支）");
        Assert.Less(Math.Abs(transport.CreatedMassLastStep - transport.InjectionDemandLastStep),
            transport.InjectionDemandLastStep * 1e-4 + 1f,
            "需求不足预算时按需全额注入（不多花预算）");
    }

    /// <summary>三板场景：板 0 = 东带（X>0.3，向 +x 退离缝）、板 1 = 其余（向 −x 退离缝）、
    /// 板 2 = −x 极区单格（俯冲汇）。全格薄柱（2890 m &lt; 阈值 3550 m）+ Age=50（注壳翻 0 可检）。</summary>
    static H3PlateFields RidgeTrenchScene(out int poleCell, out int eastPoleCell)
    {
        var centers = Ball.CellCenters;
        var f = new H3PlateFields(Ball.CellIds.Length);
        poleCell = -1;
        eastPoleCell = -1;
        float minX = float.MaxValue, maxX = float.MinValue;
        for (int i = 0; i < f.Count; i++)
        {
            if (centers[i].X < minX) { minX = centers[i].X; poleCell = i; }
            if (centers[i].X > maxX) { maxX = centers[i].X; eastPoleCell = i; }
        }
        for (int i = 0; i < f.Count; i++)
        {
            f.PlateId[i] = centers[i].X > 0.3f ? 0 : (i == poleCell ? 2 : 1);
            f.MaficVolcanic[i] = OceanColumnM;
            f.Age[i] = 50f;
        }
        return f;
    }

    /// <summary>三板场景速度场：板 0 格流向 +x、其余流向 −x（切向投影；退化处为零）。</summary>
    static Vector3[] RidgeTrenchVelocity()
    {
        var centers = Ball.CellCenters;
        var v = new Vector3[Ball.CellIds.Length];
        for (int i = 0; i < v.Length; i++)
        {
            var radial = Ball.CellDirs[i];
            Vector3 dir = centers[i].X > 0.3f ? new Vector3(1f, 0f, 0f) : new Vector3(-1f, 0f, 0f);
            var t = dir - radial * dir.Dot(radial);
            v[i] = t.LengthSquared() > 1e-12f ? t.Normalized() * Speed : Vector3.Zero;
        }
        return v;
    }

    /// <summary>格 i 是否有"异板邻边发散"边（与 H3FluxTransport 第①步同式：法向分离 ≥ 阈值）。</summary>
    static bool IsDivergentForeignEdge(H3PlateFields f, int i, Vector3[] v)
    {
        var centers = Ball.CellCenters;
        foreach (int j in Ball.CellNeighbors[i])
        {
            if (f.PlateId[j] < 0 || f.PlateId[j] == f.PlateId[i]) continue;
            Vector3 radial = (centers[i] + centers[j]).Normalized();
            Vector3 t = centers[j] - centers[i];
            t -= radial * t.Dot(radial);
            float dl = t.Length();
            if (dl <= 1e-12f) continue;
            t /= dl;
            float vi = (v[i] - radial * v[i].Dot(radial)).Dot(t);
            float vj = (v[j] - radial * v[j].Dot(radial)).Dot(t);
            if ((vj - vi) * H3PlateMotion.EarthRadiusKm >= 0.05f) return true;
        }
        return false;
    }

    [Test]
    public void Deterministic_SameInputTwice_BitwiseIdentical()
    {
        var src = TwoPlateOcean();
        var t1 = new H3PlateFields(src.Count);
        var t2 = new H3PlateFields(src.Count);
        var v = VelocityToward(new Vector3(-1f, 0f, 0f));
        new H3FluxTransport(Ball).Step(src, t1, v, Material, 4f, 0);
        new H3FluxTransport(Ball).Step(src, t2, v, Material, 4f, 0);

        for (int i = 0; i < src.Count; i++)
        {
            Assert.AreEqual(t1.MaficVolcanic[i], t2.MaficVolcanic[i]);
            Assert.AreEqual(t1.FelsicVolcanic[i], t2.FelsicVolcanic[i]);
            Assert.AreEqual(t1.Age[i], t2.Age[i]);
        }
    }
}
