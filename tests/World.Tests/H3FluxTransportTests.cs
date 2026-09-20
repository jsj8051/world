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
///   · 注壳闭环：总量 ≤ 当步俯冲回地幔量，预算内真离散板缘优先、余量回填其余薄柱；
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
            if (Ball.CellCenters[i].X > 0f)
            {
                src.Age[i] = 300f;                                      // 板 0：老洋（密 3300）
                src.Sediment[i] = 20000f;                               // 来料带沉积（守恒组）
            }
            else src.Age[i] = 0f;                                       // 板 1：年轻（密 2890）
        }

        var tgt = new H3PlateFields(src.Count);
        var transport = new H3FluxTransport(Ball);
        transport.Step(src, tgt, VelocityToward(new Vector3(-1f, 0f, 0f)), Material, 4f, 0);

        Assert.Greater(transport.SubductEdgesLastStep, 0, "洋-洋汇聚应判俯冲（老撞年轻）");
        Assert.Greater(transport.RecycledToMantleLastStep, 0, "俯冲份额应回地幔（销毁账）");
        Assert.Greater(transport.RecycledConservedMassLastStep, 0,
            "沉积类全额回地幔应记守恒组分账（陆壳收支伺服的输入——漏记则长跑水世界防线哑火）");
        Assert.LessOrEqual(transport.RecycledConservedMassLastStep, transport.RecycledToMantleLastStep,
            "守恒组分账是销毁账的子集");
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
    public void Injection_BudgetCapped_RidgePriorityWhenScarce()
    {
        // P4 后修订：注壳资格回调为一切薄柱格（预算内真离散板缘优先、余量回填其余）。
        // 布防：缝格（X≈0.3 两侧）压薄到 1000 m（= 板缘薄柱，优先档需求）、+x 极区单格压薄到
        // 500 m（= 板内薄柱，非优先）、其余格 6000 m（厚，无需求）；板 2 = −x 极区俯冲汇。
        // 预算（极区+缝俯冲销毁）≪ 板缘需求 ⇒ 稀缺：注入全部落在板缘、板内格饿死。
        var src = RidgeTrenchScene(out int poleCell, out int eastPoleCell);
        var v = RidgeTrenchVelocity();
        for (int i = 0; i < src.Count; i++)
        {
            bool seam = false;
            foreach (int nb in Ball.CellNeighbors[i])
            {
                bool cross = (Ball.CellCenters[i].X - 0.3f) * (Ball.CellCenters[nb].X - 0.3f) < 0f;
                seam |= cross;
            }
            src.MaficVolcanic[i] = Material.MaficVolcanicMin * (seam ? 1000f : 6000f);
        }
        src.MaficVolcanic[eastPoleCell] = Material.MaficVolcanicMin * 500f;   // 靶格候选（通量后可能被喂肥）

        var tgt = new H3PlateFields(src.Count);
        var transport = new H3FluxTransport(Ball);
        transport.Step(src, tgt, v, Material, 4f, 0);

        Assert.Greater(transport.RecycledToMantleLastStep, 0, "布置检查：俯冲汇应有回地幔销毁（预算来源）");
        int injected = 0, ridgeThin = 0, ridgeInjected = 0;
        for (int i = 0; i < tgt.Count; i++)
        {
            float mafic = tgt.MaficVolcanic[i] + tgt.MaficPlutonic[i];
            bool thin = mafic / Material.MaficVolcanicMin < transport.NewCrustThicknessThresholdM;
            bool ridge = IsDivergentForeignEdge(src, i, v);
            if (thin && ridge) ridgeThin++;
            if (tgt.Age[i] == 0f)
            {
                injected++;
                if (ridge) ridgeInjected++;
            }
        }
        Assert.Greater(ridgeThin, 0, "布置检查：缝格应构成板缘薄柱需求");
        Assert.AreEqual(injected, ridgeInjected, "稀缺预算必须全部落在板缘优先档");
        Assert.Less(transport.CreatedMassLastStep, transport.InjectionDemandLastStep,
            "稀缺预算应不足以喂饱全部需求（欠账 = 分配在起作用）");
        Assert.AreEqual(50f, tgt.Age[eastPoleCell], "板内薄柱在稀缺预算下不得获注（板缘优先）");
        Assert.LessOrEqual(transport.CreatedMassLastStep, transport.RecycledToMantleLastStep * 1.000001,
            "注壳预算闭环：脊上增生 ≤ 海沟销毁（白化球防线，任何分配下都成立）");
    }

    [Test]
    public void Injection_RefillsAnyThinCell_WhenBudgetAllows()
    {
        // P4 后修订的另一半：预算充裕时余量必须回填非板缘薄柱（板内伪影掏薄区的回填通道——
        // 海平面缓漂/洋内金链白斑的修复）。布防：板内单格压薄到 500 m + 极区 donors 加厚 20×
        //（俯冲销毁预算 ×20）⇒ 板缘需求小、预算大 ⇒ 余量回填板内薄柱。
        var src = RidgeTrenchScene(out int poleCell, out int eastPoleCell);
        var v = RidgeTrenchVelocity();
        for (int i = 0; i < src.Count; i++)
        {
            src.MaficVolcanic[i] = Material.MaficVolcanicMin * 4000f;               // 厚柱（无需求）
            bool seam = false;
            foreach (int nb in Ball.CellNeighbors[i])
            {
                bool cross = (Ball.CellCenters[i].X - 0.3f) * (Ball.CellCenters[nb].X - 0.3f) < 0f;
                seam |= cross;
            }
            if (seam) src.MaficVolcanic[i] = Material.MaficVolcanicMin * 3490f;     // 缝格：缺口 60 m（小需求）
            if (Ball.CellCenters[i].X < -0.9f)                                      // 极区 donors：预算 ×60
                src.MaficVolcanic[i] = Material.MaficVolcanicMin * 200000f;
        }
        src.MaficVolcanic[eastPoleCell] = Material.MaficVolcanicMin * 500f;         // 板内格：缺口 3050 m
        Assert.IsFalse(IsDivergentForeignEdge(src, eastPoleCell, v), "布置检查：极区格非板缘");

        var tgt = new H3PlateFields(src.Count);
        var transport = new H3FluxTransport(Ball);
        transport.Step(src, tgt, v, Material, 4f, 0);

        // 断言：预算 ≥ 需求（充裕）且**存在非板缘薄柱获注入**——余量真的回填到了
        // 板内伪影掏薄区（海平面缓漂/洋内金链白斑的修复口），而不是只喂板缘。
        Assert.GreaterOrEqual(transport.InjectionBudgetLastStep, transport.InjectionDemandLastStep,
            "布置检查：预算应 ≥ 需求（充裕分支）");
        int injectedTotal = 0, injectedNonRidge = 0;
        for (int i = 0; i < tgt.Count; i++)
        {
            if (tgt.Age[i] != 0f) continue;
            injectedTotal++;
            if (!IsDivergentForeignEdge(src, i, v)) injectedNonRidge++;
        }
        Assert.Greater(injectedTotal, 0, "应有薄柱获注入");
        Assert.Greater(injectedNonRidge, 0, "预算余量应回填非板缘薄柱（回填通道的存在性）");
        Assert.LessOrEqual(transport.CreatedMassLastStep, transport.RecycledToMantleLastStep * 1.000001,
            "预算闭环在任何分配下都成立");
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
