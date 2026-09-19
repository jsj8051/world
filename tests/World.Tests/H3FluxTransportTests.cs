using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 通量化运输行为测试（设计-07 P3 第一片验收）：
///   · 均匀平移：全池总量守恒（纯转移零记账）；
///   · 顶死阻断：大陆来料撞洋格 = 通量阻断（来料格质量原地保留 = 造山加厚）；
///   · 俯冲极性：老洋来料撞年轻洋格 = 俯冲汇（回地幔 + 板片账户 + 弧回流）；
///   · 薄柱注壳：缝边被俯冲掏薄的格注入 age=0 新洋壳（创建账）；
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
    public void Subduction_OldOceanicIntoYoung_RecyclesWithSlabAndArc()
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
        Assert.Greater(transport.ArcFelsicReturnedMassLastStep, 0, "弧回流长英质应落在上盘格");
        Assert.Greater(transport.SlabInflow.Count, 0, "板片账户应有入账条目");
        Assert.AreEqual(0, transport.SlabInflow[0].plate, "俯冲质量记给来料板（板 0）");
        Assert.Less(transport.SlabInflow[0].dir.X, 0f, "板片方向应指向 −x（来料流向）");
    }

    [Test]
    public void DivergentThinning_InjectsNewCrust_WithZeroAge()
    {
        var src = TwoPlateOcean();
        var tgt = new H3PlateFields(src.Count);
        var transport = new H3FluxTransport(Ball);
        transport.Step(src, tgt, VelocityToward(new Vector3(-1f, 0f, 0f)), Material, 4f, 0);
        transport.Step(src, tgt, VelocityToward(new Vector3(-1f, 0f, 0f)), Material, 4f, 1);

        Assert.Greater(transport.CreatedMassLastStep, 0, "被俯冲掏薄的缝边格应注入新洋壳（创建账）");
        bool anyYoungInjected = false;
        for (int i = 0; i < tgt.Count; i++)
        {
            float mafic = tgt.MaficVolcanic[i] + tgt.MaficPlutonic[i];
            float thickness = mafic / Material.MaficVolcanicMin;
            if (thickness < transport.NewCrustThicknessThresholdM + 1f && tgt.Age[i] == 0f)
                anyYoungInjected = true;
        }
        Assert.IsTrue(anyYoungInjected, "注壳格应带 age=0（脊轴新生洋壳）");
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
