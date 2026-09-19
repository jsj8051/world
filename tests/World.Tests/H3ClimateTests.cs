using System;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.NewHexWorld.UI.Modes;

namespace World.Tests;

/// <summary>
/// 年均温场护栏（气候批次 0，2026-09-16）：合成地壳直算 H3Climate + 管线接线各一档：
///   · 纬度梯度：赤道带显著暖于极带（基准曲线的骨架断言，不钉噪声具体值）；
///   · 直减率：同格海拔 +1000 m 恰降 6°C（同向同种子 → 噪声项不变，唯直减项动）；
///   · 确定性：同种子逐位一致；异种子（噪声盐）必有差异；
///   · 管线接线：CreatePlates 终态 Crust.TemperatureC 铺满、有限，且与直算逐位一致；
///   · 温度带名：断点归右侧段（与海拔分带同一纪律）。
/// 纪律（同 H3DynamicRunTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3ClimateTests
{
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
    static Ball Ball => SharedBall.Value;

    // 合成空地壳（全洋模板；测试按需改 Elevation/FelsicThick）
    static Crust MakeCrust()
    {
        int n = Ball.CellIds.Length;
        return new Crust
        {
            PlateId = new int[n],
            FelsicThick = new float[n],
            MaficThick = new float[n],
            SedimentThick = new float[n],
            Age = new float[n],
            Elevation = new float[n],
            TemperatureC = new float[n],
        };
    }

    [Test]
    public void LatitudeGradient_EquatorWarmerThanPole()
    {
        var crust = MakeCrust();
        var temps = H3Climate.Compute(Ball, crust, seed: 42);

        double equatorSum = 0; int equatorCount = 0;
        double poleSum = 0; int poleCount = 0;
        for (int i = 0; i < temps.Length; i++)
        {
            float absLatDeg = MathF.Abs(MathF.Asin(Math.Clamp(Ball.CellCenters[i].Normalized().Y, -1f, 1f))) * 180f / MathF.PI;
            if (absLatDeg < 10f) { equatorSum += temps[i]; equatorCount++; }
            else if (absLatDeg > 60f) { poleSum += temps[i]; poleCount++; }
        }
        Assert.Greater(equatorCount, 0, "res1 球应有赤道带格（|lat|<10°）");
        Assert.Greater(poleCount, 0, "res1 球应有极带格（|lat|>60°）");

        // 模型基准差 ≈ 52°C（30 − (−22)），噪声 ±7°C 抹不平骨架——带宽 20°C 留足余量
        Assert.Greater(equatorSum / equatorCount - poleSum / poleCount, 20f,
            "赤道带年均温应显著高于极带（纬度基准失效？）");
    }

    [Test]
    public void LapseRate_PlusOneKm_DropsExactlySixDegrees()
    {
        var crust = MakeCrust();
        var sea = H3Climate.Compute(Ball, crust, seed: 42);

        crust.Elevation.AsSpan().Fill(1000f);
        var high = H3Climate.Compute(Ball, crust, seed: 42);

        for (int i = 0; i < sea.Length; i++)
            Assert.AreEqual(sea[i] - 6f, high[i], 1e-3f,
                $"格 {i}：+1000 m 应恰降 6.0°C（同种子噪声项必须不变）");
    }

    [Test]
    public void Deterministic_SameSeedBitwise_DifferentSeedDiffers()
    {
        var crust = MakeCrust();
        var a = H3Climate.Compute(Ball, crust, seed: 42);
        var b = H3Climate.Compute(Ball, crust, seed: 42);
        CollectionAssert.AreEqual(a, b, "同种子两算应逐位一致");

        var c = H3Climate.Compute(Ball, crust, seed: 43);
        bool anyDiff = false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != c[i]) { anyDiff = true; break; }
        Assert.IsTrue(anyDiff, "异种子应改噪声场（种子盐失效？）");
    }

    [Test]
    public void Pipeline_CreatePlates_FillsTemperatureField()
    {
        var plate = new H3Plate(Ball);
        plate.CreatePlates(numPlates: 4, seed: 42);

        var temps = plate.Crust.TemperatureC;
        Assert.IsNotNull(temps, "终态应写回温度场");
        Assert.AreEqual(Ball.CellIds.Length, temps.Length, "温度场应与格数对齐");
        foreach (float t in temps)
            Assert.IsTrue(float.IsFinite(t), "温度出现非有限值");

        // 管线口径 = 直算口径（同 seed 同旋钮 → 逐位一致；防两路口径漂移）
        var direct = H3Climate.Compute(Ball, plate.Crust, seed: 42);
        CollectionAssert.AreEqual(direct, temps, "管线温度场与 H3Climate 直算不一致");
    }

    [Test]
    public void TemperatureZoneName_RightSideBoundaryAttribution()
    {
        Assert.AreEqual("热带", TemperatureMapMode.TemperatureZoneName(25f));
        Assert.AreEqual("亚热带", TemperatureMapMode.TemperatureZoneName(24.9f));
        Assert.AreEqual("亚热带", TemperatureMapMode.TemperatureZoneName(15f));
        Assert.AreEqual("温带", TemperatureMapMode.TemperatureZoneName(14.9f));
        Assert.AreEqual("温带", TemperatureMapMode.TemperatureZoneName(5f));
        Assert.AreEqual("寒温带", TemperatureMapMode.TemperatureZoneName(4.9f));
        Assert.AreEqual("寒温带", TemperatureMapMode.TemperatureZoneName(-5f));
        Assert.AreEqual("寒带", TemperatureMapMode.TemperatureZoneName(-5.1f));
    }
}
