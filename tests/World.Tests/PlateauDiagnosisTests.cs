using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 诊断（非回归断言）："大陆大面积 ~2000 m 高原"成因排查。
/// 生产同款初始化（SplitIntoPlates + Initialize，陆洋混合 0.7、海占 0.6），跑 600 My，
/// 每 100 My 采样：干陆海拔分位数（p10/p50/p90）、陆壳厚度、侵蚀/输沙/水门通量、陆壳收支配平（弧回流/刮削）。
/// A/B：ErosionScale = 1（默认） vs 4 —— 终态均值显著下降 ⇒ 侵蚀速率瓶颈；
/// 纹丝不动 ⇒ 基准侧（陆壳加厚）或水门（干旱）主导。
/// 判读输出走 Console（`dotnet test --logger "console;verbosity=detailed"` 可见）。
/// </summary>
public class PlateauDiagnosisTests
{
    const int Res = 1;
    const int Plates = 8;
    const int Seed = 42;
    const float RunMy = 600f;
    const float StepMy = 4f;

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;
    static readonly MaterialDensity Material = new();

    [Test]
    public void PlateauDiagnosis_ErosionVsDatum()
    {
        RunOne("基线 ErosionScale=1", 1f);
        RunOne("A/B   ErosionScale=4", 4f);
    }

    /// <summary>res3（编辑器默认档）基线单跑：验证细层 fBm 起伏在高分辨率下是否解开。
    /// 标 Explicit——只在显式过滤时跑（`--filter FullyQualifiedName~PlateauRes3`），不进常规套件。</summary>
    [Test]
    [Explicit]
    public void PlateauRes3_Baseline()
    {
        var ball = new Ball(3, 1f);
        var splitter = new H3Plate(ball);
        int[] plateOfCell = splitter.SplitIntoPlates(Plates, Seed);
        float runMy = 1500f;   // 诊断专用：稳压器标定窗口（1.5 Ga，黏度 <×5；与生产默认解耦）
        var sim = new H3DynamicTectonics(ball)
        {
            RunMy = runMy,
            StepMy = StepMy,
            OceanFraction = 0.6f,
            LandOceanNoiseBlend = 0.7f,
        };
        sim.Repartition = k => splitter.SplitIntoPlates(k, Seed + 977 * sim.RestartCount);
        sim.Initialize(plateOfCell, Seed);

        int n = ball.CellIds.Length;
        var felsic = new float[n];
        var elev = new float[n];
        Console.WriteLine($"\n===== res3 基线 {runMy:F0}My N={n} seed={Seed} =====");
        Snapshot(sim, felsic, elev, 0, 0, 0, 0, 0, 0);
        int steps = (int)(runMy / StepMy);
        for (int s = 1; s <= steps; s++)
        {
            sim.Step();
            if (s % 100 == 0) Snapshot(sim, felsic, elev, s * (int)StepMy, 0, 0, 0, 0, 0);
        }
    }

    static void RunOne(string title, float erosionScale)
    {
        var splitter = new H3Plate(Ball);
        int[] plateOfCell = splitter.SplitIntoPlates(Plates, Seed);
        var sim = new H3DynamicTectonics(Ball)
        {
            RunMy = RunMy,
            StepMy = StepMy,
            OceanFraction = 0.6f,
            LandOceanNoiseBlend = 0.7f,
            ErosionScale = erosionScale,
        };
        // 周期重启的重分板口（与生产/HexDynamicDiag 同款；构造后赋值以引用 sim.RestartCount）
        sim.Repartition = k => splitter.SplitIntoPlates(k, Seed + 977 * sim.RestartCount);
        sim.Initialize(plateOfCell, Seed);

        int n = Ball.CellIds.Length;
        var felsic = new float[n];
        var elev = new float[n];

        Console.WriteLine($"\n===== {title} | res={Res} N={n} P={Plates} seed={Seed} =====");
        Snapshot(sim, felsic, elev, 0, acc: 0, fluvEro: 0, fluvDep: 0, fluvSea: 0, aeolianEro: 0);

        int steps = (int)(RunMy / StepMy);
        const int chunk = 25;   // 100 My
        double fluvEro = 0, fluvDep = 0, fluvSea = 0, aeolianEro = 0;
        for (int s = 1; s <= steps; s++)
        {
            sim.Step();
            fluvEro += sim.Fluvial.ErodedMassLastStep;
            fluvDep += sim.Fluvial.DepositedMassLastStep;
            fluvSea += sim.Fluvial.DeliveredToOceanLastStep;
            aeolianEro += sim.Aeolian.ErodedMassLastStep;
            if (s % chunk == 0)
                Snapshot(sim, felsic, elev, s * (int)StepMy, 0, fluvEro, fluvDep, fluvSea, aeolianEro);
        }

        double auditGap = sim.TotalCrustMass() - (sim.InitialCrustMass + sim.CrustCreatedTotal - sim.CrustDestroyedTotal);
        Console.WriteLine($"[守恒] 账面差 = {auditGap:E2} kg/m²·格（相对 {Math.Abs(auditGap) / sim.InitialCrustMass:E1}）");
    }

    static void Snapshot(H3DynamicTectonics sim, float[] felsic, float[] elev,
        int my, double acc, double fluvEro, double fluvDep, double fluvSea, double aeolianEro)
    {
        int n = sim.Fields.Count;
        float sea = sim.SeaLevel;
        var disp = sim.Displacement;
        var landElev = new List<float>();
        double felsicSum = 0;
        int landCells = 0;
        for (int i = 0; i < n; i++)
        {
            bool materialLand = sim.Fields.FelsicTotalMass(i) > 0f;
            if (!materialLand) continue;
            landCells++;
            felsicSum += (sim.Fields.FelsicPlutonic[i] + sim.Fields.FelsicVolcanic[i]) / Material.FelsicPlutonic;
            float e = disp[i] - sea;
            elev[i] = e;
            if (e > 0f) landElev.Add(e);          // 渲染口径的"大陆" = 露出海面的陆壳格
        }
        landElev.Sort();
        float P(float q) => landElev.Count > 0 ? landElev[Math.Clamp((int)(q * (landElev.Count - 1)), 0, landElev.Count - 1)] : 0f;
        float p10 = P(0.10f), p50 = P(0.50f), p90 = P(0.90f);
        float mean = landElev.Count > 0 ? landElev.Average() : 0f;
        int within500 = landElev.Count(e => MathF.Abs(e - p50) < 500f);

        Console.WriteLine(
            $"[{my,4} My] 海平面 {sea,7:F0} m | 陆格 {landCells,4}（露干 {landElev.Count,4}，占比 {landElev.Count / (float)n:P0}）| " +
            $"干陆海拔 均{mean,6:F0} p10 {p10,6:F0} p50 {p50,6:F0} p90 {p90,6:F0} m | 中位±500m 内 {within500 / Math.Max(1f, landElev.Count):P0} | " +
            $"陆壳长英质均厚 {felsicSum / Math.Max(1, landCells),6:F0} m | " +
            $"板均速 {sim.MeanRealizedSpeedKmPerMyLastStep:F3} km/My 重启×{sim.RestartCount} 黏度×{sim.Thermal.MantleViscosityPaS / H3ThermalState.ReferenceViscosityPaS:F1}");
        if (my > 0)
            Console.WriteLine(
                $"[通量/步] 侵蚀 {sim.Surface.ErosionMovedMassLastStep:E1} 风化 {sim.Surface.WeatheredMassLastStep:E1} | " +
                $"河 E{sim.Fluvial.ErodedMassLastStep:E1}/D{sim.Fluvial.DepositedMassLastStep:E1}/出海 {sim.Fluvial.DeliveredToOceanLastStep:E1} | " +
                $"风蚀 {sim.Aeolian.ErodedMassLastStep:E1} | 弧回流/刮削累计 {sim.FelsicArcReturnedCum:E1}/{sim.FelsicScrapedCum:E1} | " +
                $"降水 {sim.Surface.PrecipDomainMeanMmYearLastStep:F0} 产流 {sim.Surface.RunoffDomainMeanMmYearLastStep:F0} mm/yr " +
                $"零产流格 {sim.Surface.ZeroRunoffCellFractionLastStep:P0} | " +
                $"[累计] 河出海 {fluvSea:E1} 楔F {acc:E1} 河E {fluvEro:E1}");
    }
}
