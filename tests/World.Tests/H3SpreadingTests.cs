using System;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 洋底扩张（v1.15 方案①，威尔逊旋回引擎）测试：离散边界上的空洞注 age=0 脊轴新洋壳。
/// 断言的测量项：两板分离 ⇒ 边界空洞长出 0 龄洋壳（判读口 SpreadingFilledCount &gt; 0）、
/// 汇聚边界不注壳、大陆裂谷长出窄洋（陆格被新洋格稀释）、创建账随注入增长。
/// 纪律（同 H3PlateJamTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService；浮点断言带容差。
/// </summary>
public class H3SpreadingTests
{
    private const int Res = 1;                      // 842 格
    private const float RidgeMaficMass = 2890f * 7100f;   // 脊轴新洋壳（与生产同参）

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;

    static readonly MaterialDensity Material = new();

    /// <summary>两板世界（Z≥0 板 0，Z<0 板 1），每格速度沿 Z 分离或汇聚。continental = 陆壳柱。</summary>
    static (H3PlateFields fields, H3PlateMotion motion) BuildWorld(bool continental, bool separating)
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        var motion = new H3PlateMotion(Ball.CellIds.Length)
        {
            PlateFired = Enumerable.Repeat(true, 8).ToArray(),
        };
        var centers = Ball.CellCenters;
        for (int c = 0; c < fields.Count; c++)
        {
            bool north = centers[c].Z >= 0f;
            fields.PlateId[c] = north ? 0 : 1;
            fields.ClearCell(c);
            if (continental)
            {
                fields.FelsicPlutonic[c] = Material.FelsicPlutonic * 35000f;
                fields.Age[c] = 50f;                               // 非零年龄：让 age==0 唯一标识注入的脊轴壳
            }
            else
            {
                fields.MaficVolcanic[c] = Material.MaficVolcanicMin * 7100f;
                fields.Age[c] = 100f;
            }
            float sign = separating ? 1f : -1f;                    // 分离：各自背离边界；汇聚：相向
            motion.Velocity[c] = new Vector3(0f, 0f, north ? sign : -sign) * 0.01f;
        }
        return (fields, motion);
    }

    static (H3PlateAdvection adv, H3PlateFields target) RunFill(H3PlateFields source, H3PlateMotion motion)
    {
        var target = new H3PlateFields(Ball.CellIds.Length);       // 双缓冲：与生产同构
        var advection = new H3PlateAdvection(Ball.CellIds.Length);
        advection.Step(Ball, source, motion, target, Material, stepIndex: 0);   // 平流产生链尾空洞
        advection.FillHolesWithNewOceanicCrust(Ball, target, motion, RidgeMaficMass);
        return (advection, target);
    }

    static int CountRidgeCells(H3PlateFields f)
    {
        int count = 0;
        for (int i = 0; i < f.Count; i++)
            if (f.Age[i] == 0f && f.MaficVolcanic[i] > 0f && f.FelsicPlutonic[i] + f.FelsicVolcanic[i] == 0f)
                count++;
        return count;
    }

    static int CountLand(H3PlateFields f)
    {
        int land = 0;
        for (int i = 0; i < f.Count; i++) if (f.IsLand(i)) land++;
        return land;
    }

    [Test]
    public void Spreading_DivergentBoundary_GrowsRidgeCrust()
    {
        var (source, motion) = BuildWorld(continental: false, separating: true);

        var (advection, target) = RunFill(source, motion);

        Assert.Greater(advection.SpreadingFilledCount, 0, "分离边界的空洞应注入脊轴新洋壳");
        Assert.Greater(CountRidgeCells(target), 0, "0 龄纯 mafic 格 = 注入的脊轴壳");
        Assert.GreaterOrEqual(advection.CrustCreatedMass,
            advection.SpreadingFilledCount * RidgeMaficMass * 0.999,
            "注入质量记创建账");
    }

    [Test]
    public void ConvergentBoundary_NoSpreading()
    {
        var (source, motion) = BuildWorld(continental: false, separating: false);

        var (advection, target) = RunFill(source, motion);

        Assert.AreEqual(0, advection.SpreadingFilledCount, "汇聚边界不得注入脊轴壳");
        Assert.AreEqual(0, CountRidgeCells(target), "无 0 龄新壳");
    }

    [Test]
    public void Spreading_ContinentalRift_GrowsNarrowOcean_DilutesLand()
    {
        // 威尔逊旋回起点：大陆被撕开的裂谷长出窄洋——陆格被新洋格稀释（对陆增棘轮的结构性对冲）
        var (source, motion) = BuildWorld(continental: true, separating: true);
        int landBefore = CountLand(source);

        var (advection, target) = RunFill(source, motion);

        Assert.Greater(advection.SpreadingFilledCount, 0, "大陆裂谷的空洞应注入窄洋");
        int landAfter = CountLand(target);
        Assert.Less(landAfter, landBefore,
            $"裂谷注洋应稀释陆格（前 {landBefore} → 后 {landAfter}）");
        int ridgeCells = 0;
        for (int i = 0; i < target.Count; i++)
        {
            if (target.Age[i] != 0f) continue;
            Assert.IsFalse(target.MaficVolcanic[i] > 0f && target.IsLand(i),
                "注入的 0 龄脊轴壳应为纯 mafic 洋格（不含长英质）");
            if (target.MaficVolcanic[i] > 0f && !target.IsLand(i)) ridgeCells++;
        }
        Assert.GreaterOrEqual(ridgeCells, advection.SpreadingFilledCount, "0 龄 mafic 洋格数 ≥ 注入数");
    }
}
