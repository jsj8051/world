using System;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.NewHexWorld.UI.Modes;

namespace World.Tests;

/// <summary>
/// 水量均衡护栏（气候批次 1，2026-09-16）：用户口径【海水 + 云 + 河 = 定值总水量】：
///   · 通量闭合：全球 Σ降水 = 全球 Σ蒸发（λ 缩放恒等式）；
///   · 库存结构：云/河库存为正且"薄"（预留均深 &lt; 1 m——大气/河道本就是水循环的过路账）；
///   · 账本闭合（管线）：Crust 实测洋格水体积 + 云 + 河 ≈ TOD×洋格数（相对 1e-6）；
///   · 确定性：同入参逐位一致；
///   · 降水结构：ITCZ 带显著湿于副热带压制带；
///   · 分带/归一口：断点归右侧段、洋格 clamp。
/// 纪律（同 H3DynamicRunTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3WaterCycleTests
{
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
    static Ball Ball => SharedBall.Value;

    const float LandElevM = 800f;

    // 合成地壳：热带陆桥（|lat|<30° 为陆）+ 按"距海跳数"爬坡的高程（保证有汇流出海路径；
    // 指定格挖成内流洼地，覆盖洼地分支）。全洋模板打底。
    static Crust MakeCrustWithSlope(out float[] elevation)
    {
        int n = Ball.CellIds.Length;
        var crust = new Crust
        {
            PlateId = new int[n],
            FelsicThick = new float[n],
            MaficThick = new float[n],
            SedimentThick = new float[n],
            Age = new float[n],
            Elevation = new float[n],
            TemperatureC = new float[n],
            PrecipMmYear = new float[n],
        };
        elevation = crust.Elevation;
        for (int i = 0; i < n; i++)
        {
            Vector3Lat(Ball.CellCenters[i], out float absLatDeg);
            if (absLatDeg >= 30f) continue;                    // 洋格
            crust.FelsicThick[i] = 35000f;
            crust.Elevation[i] = LandElevM;
        }
        // 距海 BFS（跳数）→ 高程随跳数爬升（每跳 +200 m，海岸 +200）
        var hops = new int[n];
        Array.Fill(hops, int.MaxValue);
        var queue = new int[n];
        int head = 0, tail = 0;
        for (int i = 0; i < n; i++)
        {
            if (crust.IsLand(i)) continue;
            hops[i] = 0;
            queue[tail++] = i;
        }
        while (head < tail)
        {
            int i = queue[head++];
            foreach (int nb in Ball.CellNeighbors[i])
            {
                if (hops[nb] <= hops[i] + 1) continue;
                hops[nb] = hops[i] + 1;
                queue[tail++] = nb;
            }
        }
        for (int i = 0; i < n; i++)
            if (crust.IsLand(i)) crust.Elevation[i] = LandElevM + 200f * hops[i];

        // 内流洼地：任取一个距海 ≥3 跳的陆格压到近海面（低于全部邻居 → 汇流止于此，就地蒸发）
        for (int i = 0; i < n; i++)
        {
            if (crust.IsLand(i) && hops[i] >= 3) { crust.Elevation[i] = LandElevM + 10f; break; }
        }
        return crust;
    }

    static void Vector3Lat(Godot.Vector3 center, out float absLatDeg)
        => absLatDeg = MathF.Abs(MathF.Asin(Math.Clamp(center.Normalized().Y, -1f, 1f))) * 180f / MathF.PI;

    static (Crust crust, float[] temps, float[] raw, H3WaterCycle wc) RunSynthetic(int seed = 42)
    {
        var crust = MakeCrustWithSlope(out _);
        float[] temps = H3Climate.Compute(Ball, crust, seed);
        float[] raw = H3Precipitation.Compute(Ball, crust, seed);
        var wc = H3WaterCycle.Run(Ball, crust, temps, raw);
        return (crust, temps, raw, wc);
    }

    // ── 通量与库存 ──

    [Test]
    public void FluxClosure_GlobalPrecipEqualsOceanEvaporation()
    {
        var (_, _, _, wc) = RunSynthetic();
        Assert.Greater(wc.TotalEvapM, 0, "洋面蒸发总量应为正");
        Assert.Less(Math.Abs(wc.TotalPrecipM - wc.TotalEvapM) / wc.TotalEvapM, 1e-6,
            "λ 闭合失效：全球降水 ≠ 全球蒸发");
    }

    [Test]
    public void StorageStructure_ThinPositiveStorages_EvapOnlyOverOcean()
    {
        var (crust, _, _, wc) = RunSynthetic();
        Assert.Greater(wc.AtmStorageM, 0, "云库存应为正");
        Assert.Greater(wc.RiverStorageM, 0, "有汇流出海路径时河库存应为正");
        Assert.Less(wc.ReservedDepthPerOceanCellM, 1f,
            "云+河是水循环的过路账，预留均深应远小于 1 m（量级异常？）");
        Assert.Greater(wc.OceanCellCount, 0);

        for (int i = 0; i < wc.EvapMmYear.Length; i++)
        {
            if (crust.IsLand(i)) Assert.AreEqual(0f, wc.EvapMmYear[i], "陆格不应有洋面蒸发");
            else Assert.Greater(wc.EvapMmYear[i], 0f, "洋格蒸发应为正");
        }
    }

    [Test]
    public void Deterministic_SameInputsBitwiseIdentical()
    {
        var (crustA, tempsA, rawA, wcA) = RunSynthetic(42);
        var (crustB, tempsB, rawB, wcB) = RunSynthetic(42);

        CollectionAssert.AreEqual(tempsA, tempsB);
        CollectionAssert.AreEqual(rawA, rawB);
        CollectionAssert.AreEqual(wcA.PrecipMmYear, wcB.PrecipMmYear, "降水场逐位不一致");
        CollectionAssert.AreEqual(wcA.EvapMmYear, wcB.EvapMmYear, "蒸发场逐位不一致");
        CollectionAssert.AreEqual(wcA.RiverDischargeMmYear, wcB.RiverDischargeMmYear, "汇流场逐位不一致");
        Assert.AreEqual(wcA.AtmStorageM, wcB.AtmStorageM);
        Assert.AreEqual(wcA.RiverStorageM, wcB.RiverStorageM);
        GC.KeepAlive(crustA); GC.KeepAlive(crustB);
    }

    // ── 降水结构 ──

    [Test]
    public void PrecipStructure_ItczWetterThanSubtropicalSuppression()
    {
        var (crust, _, _, wc) = RunSynthetic();
        double itczSum = 0; int itczCount = 0;
        double subSum = 0; int subCount = 0;
        for (int i = 0; i < wc.PrecipMmYear.Length; i++)
        {
            Vector3Lat(Ball.CellCenters[i], out float absLatDeg);
            if (absLatDeg < 10f) { itczSum += wc.PrecipMmYear[i]; itczCount++; }
            else if (absLatDeg > 20f && absLatDeg < 35f) { subSum += wc.PrecipMmYear[i]; subCount++; }
        }
        Assert.Greater(itczCount, 0);
        Assert.Greater(subCount, 0);
        Assert.Greater(itczSum / itczCount - subSum / subCount, 100f,
            "ITCZ 带降水应显著高于副高压制带（纬度带骨架失效？）");
        GC.KeepAlive(crust);
    }

    // ── 管线接线 + 账本闭合 ──

    [Test]
    public void Pipeline_LedgerCloses_OceanPlusAtmPlusRiverEqualsFixedTotal()
    {
        var plate = new H3Plate(Ball);
        plate.CreatePlates(numPlates: 4, seed: 42);

        var crust = plate.Crust;
        var wc = plate.WaterCycle;
        Assert.IsNotNull(wc, "终态应有水循环账本");
        Assert.IsNotNull(crust.PrecipMmYear, "终态应写回降水场");
        Assert.AreEqual(Ball.CellIds.Length, crust.PrecipMmYear.Length);
        foreach (float p in crust.PrecipMmYear)
        {
            Assert.IsTrue(float.IsFinite(p), "降水出现非有限值");
            Assert.GreaterOrEqual(p, 0f, "降水为负");
        }

        // 实测三库存（米×格）：⚠️ **v1.17/v1.18 口径变了三处**——
        //   ① 海平面求解的分母是**洋盆格数**（镁铁质厚 > 长英质厚；对陆源撒尘稳健）：总水量 = TOD × 洋盆格数；
        //   ② 容器扩到**全体可淹没格**（含被淹没的陆壳 = 大陆架/内陆浅海）⇒ 海水体积 = **全表**
        //      max(0, −海拔) 之和（不再只扫"当前判为洋"的格，那样会漏掉浅海）；
        //   ③ 期望值读模拟实例的 `TotalOceanDepth` 而不是常量：v1.19 起 = 全球等效水层
        //      （`GlobalWaterLayerM` 2700 m）× 全星格数 / 洋盆格数（初始化摊派后守恒）。
        double oceanWater = 0;
        int oceanBasinCells = 0;
        for (int i = 0; i < crust.PlateId.Length; i++)
        {
            oceanWater += Math.Max(0f, -crust.Elevation[i]);
            if (crust.MaficThick[i] > crust.FelsicThick[i]) oceanBasinCells++;   // 洋盆格（镁铁质主导）
        }

        double total = plate.Simulation.TotalOceanDepth * (double)oceanBasinCells;
        double accounted = oceanWater + wc.AtmStorageM + wc.RiverStorageM;
        Assert.Less(Math.Abs(accounted - total) / total, 1e-6,
            $"账本不闭合：海 {oceanWater:F1} + 云 {wc.AtmStorageM:F1} + 河 {wc.RiverStorageM:F1} ≠ 总量 {total:F1}");
        Assert.Less(Math.Abs(wc.TotalPrecipM - wc.TotalEvapM) / wc.TotalEvapM, 1e-6, "通量闭合失效");
    }

    // ── 分带与归一口 ──

    [Test]
    public void AridityZoneName_RightSideBoundaryAttribution()
    {
        Assert.AreEqual("多雨带", PrecipitationMapMode.AridityZoneName(1500f));
        Assert.AreEqual("湿润带", PrecipitationMapMode.AridityZoneName(1499f));
        Assert.AreEqual("湿润带", PrecipitationMapMode.AridityZoneName(500f));
        Assert.AreEqual("半干旱带", PrecipitationMapMode.AridityZoneName(499f));
        Assert.AreEqual("半干旱带", PrecipitationMapMode.AridityZoneName(250f));
        Assert.AreEqual("干旱带", PrecipitationMapMode.AridityZoneName(249f));
    }

    [Test]
    public void Normalize_LandRangeDomain_ClampsOutside()
    {
        Assert.AreEqual(0f, PrecipitationMapMode.Normalize(100f, 100f, 2000f));
        Assert.AreEqual(1f, PrecipitationMapMode.Normalize(2000f, 100f, 2000f));
        Assert.AreEqual(0.5f, PrecipitationMapMode.Normalize(1050f, 100f, 2000f), 1e-4);
        Assert.AreEqual(0f, PrecipitationMapMode.Normalize(50f, 100f, 2000f), "低于域下限应 clamp 到 0");
        Assert.AreEqual(1f, PrecipitationMapMode.Normalize(900f, 0f, 0f), "退化域应恒 0（避免除零）");
    }
}
