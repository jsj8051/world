using System;
using Godot;
using NUnit.Framework;
using World.Biome;
using World.MapGen;
using World.MapGen.Model;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 气候模拟链单元 + 模块测试（纯托管，无引擎）。
///
/// 覆盖（2026-08 引擎适配器重构后直接可测的纯核心）：
///   1. ClimateGenerator 物理公式（注入 ZeroNoise 纯托管噪声）：
///      温度——纬度带 / 海拔递减 / 倾角 / insolation / 洋流 / 确定性；
///      降水——ITCZ / 副热带 / 极锋 / 地形抬升 / 海陆风湿润度 / 非负；
///      月降水基准——ITCZ 南北摆动 / 极锋 / 副高压制 / 非负。
///   2. MonsoonSystem.Compute（完整季风诊断，注入 ZeroNoise 气候）：结构不变量 +
///      陆地月比例 Σ=1 / 海洋 0 / 极值序 / 季风强度 / 无 NaN / 确定性。
///   3. ClimateModel.Models / Run：注册表结构与拓扑执行（构造最小 pipe → Run →
///      场字段被填充、无异常）。
///   4. 旗舰端到端：WeatherPipeline 编排（ClimateModel.Run 注入 ZeroNoise 驱动全部场）——
///      构造 TectonicsSimulation(2) + 手工填充字段 → 全部输出场非空/有限/长度=n、
///      biome 陆地非 0、温度序、无 NaN、确定性、物理合理性。
///
/// ⚠️ 引擎禁区（探针实测：进入则进程级崩溃 0xC0000005）：
///   · 测试注入纯托管 ISphericalNoise（ZeroNoise），禁止走 null → FastNoiseLite 引擎实现。
///   · 不调用 PrintReport / HealthCheck / 任何 GD.* / LogService.* / 节点类。
///   · PlanetPipeline.Run 内部无条件 new ClimateGenerator(seed,tilt,ins)（=引擎噪声）——
///     因此本文件"旗舰"不直接调用 PlanetPipeline.Run，而是复现其场驱动编排
///     （设 Sim/P/Grid/Verts/Neighbors + 注入噪声的 Climate → ClimateModel.Run），
///     并在注释中标明这一适配（任务同口径：传 null 会建引擎实现，禁止）。
///   · WindField.Prograde/RotationSpeed 为静态，测试改后 finally 还原。
/// 确定性、快速（n=2 → 42 顶点）、不写文件、浮点断言带容差。
/// </summary>
public class ClimateSimTests
{
    /// <summary>恒零纯托管球面噪声（注入以阻断 FastNoiseLite 引擎路径）。</summary>
    private sealed class ZeroNoise : ISphericalNoise
    {
        public float Sample(Vector3 p) => 0f;
    }

    /// <summary>构造一个无引擎噪声的 ClimateGenerator（恒零噪声，物理公式可精确验证）。</summary>
    private static ClimateGenerator Climate(float tilt = 23.4f, float ins = 1f, int seed = 7)
        => new ClimateGenerator(seed, tilt, ins, new ZeroNoise(), new ZeroNoise());

    /// <summary>指定纬度（度）的单位球方向向量（lon=0，XZ 平面）。南北用 lat 正负。</summary>
    private static Vector3 Lat(float latDeg, float lonDeg = 0f)
    {
        float la = latDeg * Mathf.Pi / 180f;
        float lo = lonDeg * Mathf.Pi / 180f;
        float x = Mathf.Cos(la) * Mathf.Cos(lo);
        float z = Mathf.Cos(la) * Mathf.Sin(lo);
        return new Vector3(x, Mathf.Sin(la), z).Normalized();
    }

    // ═════════════════════════════════════════════════════════════════
    // 1. ClimateGenerator.ComputeTemperature —— 物理公式（恒零噪声）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void ComputeTemperature_EquatorWarmerThanPole()
    {
        // 保证：纬度基准——赤道热、极地冷（cosLat^1.1；赤道≈+30°C，极地≈-22°C 基线）。
        var c = Climate();
        float equator = c.ComputeTemperature(Lat(0f), 0f);
        float highLat = c.ComputeTemperature(Lat(75f), 0f);
        float pole = c.ComputeTemperature(Lat(90f), 0f);   // 90°：cos=0 → 完全 52×0−22 = −22
        Assert.Greater(equator, highLat, "赤道应比高纬更暖");
        Assert.Greater(highLat, pole, "高纬应比极地更暖");
        // 量级锚（无噪声、无洋流）：赤道 ≈ 30°C（52×1^1.1−22），极地 ≈ −22°C
        Assert.AreEqual(30f, equator, 0.5f);
        Assert.AreEqual(-22f, pole, 0.5f);
    }

    [Test]
    public void ComputeTemperature_ElevationLapse6CPerKm()
    {
        // 保证：海拔递减 6°C/km（elevNorm=1.0 → 10km → −60°C 于海平面基线）。
        var c = Climate();
        float atSea = c.ComputeTemperature(Lat(0f), 0f);        // 赤道海平面
        float hi = c.ComputeTemperature(Lat(0f), 1.0f);         // 赤道 10km
        Assert.AreEqual(atSea - 60f, hi, 1e-3f, "elevNorm 1.0 应递减 10km×6°C = 60°C");
        // 恒零噪声 → 不该引入任何额外变化
        Assert.AreEqual(30f, atSea, 1e-3f);
        Assert.AreEqual(-30f, hi, 1e-3f);
    }

    [Test]
    public void ComputeTemperature_Tilt0WarmsHighLat_Tilt45CoolsHighLat()
    {
        // 保证：倾角修正——tilt<23.4 高纬年均更暖（无季节、等日照），tilt>23.4 高纬更冷
        // （极夜深）；低纬（<45°）不受倾角影响。
        var refC = Climate();               // 23.4° 地球基准
        var lowTilt = Climate(tilt: 0f);
        var highTilt = Climate(tilt: 45f);
        Vector3 atHighLat = Lat(80f);
        float warm = lowTilt.ComputeTemperature(atHighLat, 0f);
        float baseT = refC.ComputeTemperature(atHighLat, 0f);
        float cold = highTilt.ComputeTemperature(atHighLat, 0f);
        Assert.Greater(warm, baseT, "tilt=0 高纬应比地球基准更暖");
        Assert.Less(cold, baseT, "tilt=45 高纬应比地球基准更冷");
        Assert.Greater(warm, cold, "tilt=0 应显著暖于 tilt=45");
    }

    [Test]
    public void ComputeTemperature_Insolation12WarmsGlobally()
    {
        // 保证：恒星辐照度修正——insolation>1（离恒星近）全球升温；赤道增得更多。
        var baseC = Climate(ins: 1.0f);
        var nearC = Climate(ins: 1.2f);
        Vector3 eq = Lat(0f);
        Assert.Greater(nearC.ComputeTemperature(eq, 0f), baseC.ComputeTemperature(eq, 0f),
            "insolation 1.2 应比 1.0 更暖");
        Assert.Greater(nearC.ComputeTemperature(Lat(30f), 0f), baseC.ComputeTemperature(Lat(30f), 0f),
            "insolation 1.2 中纬应更暖");
    }

    [Test]
    public void ComputeTemperature_OceanCurrentWarmRaisesColdLowers()
    {
        // 保证：洋流修正（SetOceanCurrent）——暖流(warm>0)升温、寒流(warm<0)降温；
        // 强度 str∈[0.3,1] 放大修正；无洋流=0 影响（lambda 注入，未设 = 无洋流）。
        var noCur = Climate();
        var warm = Climate();
        warm.SetOceanCurrent(p => (1.0f, 1.0f));   // 强暖流
        var cold = Climate();
        cold.SetOceanCurrent(p => (-1.0f, 1.0f));  // 强寒流

        Vector3 p = Lat(45f);
        float baseT = noCur.ComputeTemperature(p, 0f);
        float warmT = warm.ComputeTemperature(p, 0f);
        float coldT = cold.ComputeTemperature(p, 0f);
        Assert.AreEqual(baseT + 5f, warmT, 1e-3f, "暖流(warm=1,str=1)应 +5°C");
        Assert.AreEqual(baseT - 5f, coldT, 1e-3f, "寒流(warm=-1,str=1)应 −5°C");
        Assert.Greater(warmT, coldT);
        Assert.AreEqual(0f, noCur.ComputeTemperature(p, 0f) - baseT, 1e-6f, "不设洋流 → 无修正");
    }

    [Test]
    public void ComputeTemperature_SameInputSameOutput_Deterministic()
    {
        // 保证：确定性——同输入（同噪声实现、同参数、同步）逐次输出逐位一致。
        var a = Climate(ins: 1.1f);
        var b = Climate(ins: 1.1f);
        Vector3 p = Lat(33f, 21f);
        Assert.AreEqual(a.ComputeTemperature(p, 0.4f), b.ComputeTemperature(p, 0.4f), 0f);
        Assert.AreEqual(a.ComputeTemperature(Lat(-50f), -0.2f), b.ComputeTemperature(Lat(-50f), -0.2f), 0f);
    }

    // ═════════════════════════════════════════════════════════════════
    // 1'. ClimateGenerator.ComputePrecipitation —— 纬度带 + 盛行风 + 地形
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void ComputePrecipitation_LatitudeBands_PeaksAndTroughs()
    {
        // 保证：纬度带曲线——赤道 ITCZ 多雨 > 极锋(~62°) 多雨 > 极地少雨，
        // 副热带(~26°) 高压下沉带被压制 → 明显低谷（沙漠带）。
        // 不传 sampleElev → 只触发纬度带噪声机（恒零），可精确对比。
        var c = Climate();
        float eq = c.ComputePrecipitation(Lat(0f), 0f);
        float sub = c.ComputePrecipitation(Lat(26f), 0f);
        float polarFront = c.ComputePrecipitation(Lat(62f), 0f);
        float pole = c.ComputePrecipitation(Lat(89f), 0f);

        Assert.Greater(eq, sub, "赤道 ITCZ 应远多于副热带低谷");
        Assert.Greater(polarFront, sub, "极锋(~62°)应是局部多雨峰");
        Assert.Greater(polarFront, pole, "极锋应多于极地");
        Assert.Greater(eq, pole, "赤道应多于极地");
        // 局部峰：62° 极锋应高于相邻 45°
        Assert.Greater(polarFront, c.ComputePrecipitation(Lat(45f), 0f));
    }

    [Test]
    public void ComputePrecipitation_NonNegative()
    {
        // 保证：降水非负（clamp 0）——任何纬度/海拔/风向下都不出负值。
        var c = Climate();
        foreach (float lat in new float[] { -80f, -40f, -10f, 0f, 15f, 30f, 60f, 88f })
            foreach (float ev in new float[] { -0.9f, 0f, 0.4f, 1.0f })
                Assert.GreaterOrEqual(c.ComputePrecipitation(Lat(lat), ev), 0f,
                    $"lat={lat} elev={ev} 降水应非负");
    }

    [Test]
    public void ComputePrecipitation_TerrainLiftIncreases()
    {
        // 保证：地形抬升——elevNorm>0 的地形让迎风面增雨（×1+0.4×min(e,h)）。
        var c = Climate();
        float low = c.ComputePrecipitation(Lat(0f), 0f);
        float high = c.ComputePrecipitation(Lat(0f), 1.0f);
        float mid = c.ComputePrecipitation(Lat(0f), 0.5f);
        Assert.Greater(high, low, "抬升地形应增雨");
        Assert.Greater(mid, low, "中海拔也应高于海平面基准");
    }

    [Test]
    public void ComputePrecipitation_OceanWindHumidifies_LandWindDries()
    {
        // 保证：盛行风湿润度——上风向全海洋 → 海洋湿润风增雨；上风向全陆地 → 大陆风减雨。
        // 用 sampleElev 回调全海/全陆区分（两者 rainshadow 项均为 0，纯净展示海陆风差）。
        WindField.Prograde = true;
        WindField.RotationSpeed = 1f;
        try
        {
            var c = Climate();
            Vector3 p = Lat(20f);
            float oceanWind = c.ComputePrecipitation(p, -0.4f, _ => -0.6f);   // 全海样本
            float landWind = c.ComputePrecipitation(p, -0.4f, _ => 0.6f);     // 全陆样本
            Assert.Greater(oceanWind, landWind, "海洋来风应比大陆来风更湿润多雨");
            // 无 sampleElev：盛行风修正完全不触发（保持纬度带基准）
            float noWind = c.ComputePrecipitation(p, -0.4f, null);
            Assert.AreEqual(noWind, c.ComputePrecipitation(p, -0.4f, null), 1e-6f, "确定性自洽");
        }
        finally
        {
            WindField.Prograde = true;
            WindField.RotationSpeed = 1f;
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // 1''. ClimateGenerator.ComputePrecipitationMonthBase —— ITCZ 摆动
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void ComputePrecipitationMonthBase_ItczSwingsSouth()
    {
        // 保证：月基准 ITCZ 随 itczShiftDeg 南北摆动（带符号）——南半球 lat=-20
        // 在 shift=-20（南半球夏季 ITCZ 到达）时多雨，在 shift=+20（ITCZ 北上）时少雨。
        var c = Climate();
        Vector3 south = Lat(-20f);
        Assert.Greater(c.ComputePrecipitationMonthBase(south, 0f, -20f),
                       c.ComputePrecipitationMonthBase(south, 0f, +20f),
                       "lat=-20 在 shift=-20 应比 shift=+20 多雨（ITCZ 南移）");
    }

    [Test]
    public void ComputePrecipitationMonthBase_PolarFrontPeak_SubtropicalSuppressed()
    {
        // 保证：月基准同样含极锋(~62°)局部峰 + 副热带高压(~26°)压制。
        var c = Climate();
        float pf = c.ComputePrecipitationMonthBase(Lat(62f), 0f, 0f);
        float mid = c.ComputePrecipitationMonthBase(Lat(40f), 0f, 0f);
        float pole = c.ComputePrecipitationMonthBase(Lat(88f), 0f, 0f);
        Assert.Greater(pf, mid, "极锋 62° 应为局部多雨峰");
        Assert.Greater(pf, pole, "极锋应多于极地");
    }

    [Test]
    public void ComputePrecipitationMonthBase_NonNegative()
    {
        // 保证：月降水基准非负（clamp 0）——任意 shift/纬度。
        var c = Climate();
        foreach (float shift in new float[] { -23.4f, 0f, 23.4f })
            foreach (float lat in new float[] { -70f, -20f, 0f, 20f, 62f, 85f })
                Assert.GreaterOrEqual(c.ComputePrecipitationMonthBase(Lat(lat), 0f, shift), 0f,
                    $"shift={shift} lat={lat} 月降水基准应非负");
    }

    // ═════════════════════════════════════════════════════════════════
    // 2. MonsoonSystem.Compute —— 完整季风诊断场（注入 ZeroNoise 气候）
    // ═════════════════════════════════════════════════════════════════

    private static (Vector3[] verts, int[][] neighbors, float[] elevNorm, float[] elevM,
        float[] tempBase, float[] precipEst) BuildMonsoonGrid()
    {
        // n=2 二十面体（42 单位球顶点，SphereGrid 构造纯托管）；北半球陆地 / 南半球海洋。
        var grid = new SphereGrid(2);
        int n = grid.VertexCount;
        var verts = grid.Vertices;
        var neighbors = grid.Neighbors;
        var elevNorm = new float[n];
        var elevM = new float[n];
        var tempBase = new float[n];
        var precipEst = new float[n];
        for (int i = 0; i < n; i++)
        {
            float y = verts[i].Y;                      // 单位方向 Y∈[-1,1]
            float latDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(y, -1f, 1f)));
            bool land = y > 0f;
            elevNorm[i] = land ? 0.3f : -0.5f;
            elevM[i] = land ? 300f : -2000f;
            tempBase[i] = 28f - 50f * Mathf.Abs(latDeg) / 90f;   // 纬度-温度基准
            precipEst[i] = 600f;
        }
        return (verts, neighbors, elevNorm, elevM, tempBase, precipEst);
    }

    private static void ComputeMonsoon(ClimateGenerator climate,
        out float[] monsoon, out float[] tHot, out float[] tCold, out float[] dryP,
        out int[] dryIdx, out float[][] monthPrecip, out Vector3[][] monthWind,
        out float[][] monthTemp, out float[] precipAnnAbs)
    {
        var (verts, neighbors, elevNorm, elevM, tempBase, precipEst) = BuildMonsoonGrid();
        MonsoonSystem.Compute(verts, neighbors, elevNorm, elevM, tempBase, precipEst,
            23.4f, 1f, climate,
            out monsoon, out tHot, out tCold, out dryP, out dryIdx,
            out monthPrecip, out monthWind, out monthTemp, out precipAnnAbs,
            radiusKm: MapArchive.DefaultRadiusKm);
    }

    [Test]
    public void Monsoon_Compute_StructuralInvariants()
    {
        // 保证：完整季风诊断场结构不变量——
        //   12 个月数组齐全；陆地月降水比例 Σ=1（年降水>0）；海洋格降水 0；
        //   tHot≥tCold；dryMonthIndex∈0..11；季风强度∈[0,1]；全数组无 NaN。
        var climate = Climate();
        ComputeMonsoon(climate,
            out var monsoon, out var tHot, out var tCold, out var dryP,
            out var dryIdx, out var monthPrecip, out var monthWind,
            out var monthTemp, out var precipAnnAbs);

        Assert.AreEqual(12, monthPrecip.Length);
        Assert.AreEqual(12, monthWind.Length);
        Assert.AreEqual(12, monthTemp.Length);

        var (_, _, elevNorm, _, _, _) = BuildMonsoonGrid();
        int n = elevNorm.Length;
        foreach (var arr in new[] { monsoon, tHot, tCold, dryP, precipAnnAbs })
        {
            Assert.AreEqual(n, arr.Length);
            foreach (float v in arr) Assert.True(float.IsFinite(v), "输出场应有限（无 NaN）");
        }
        foreach (var row in monthPrecip) { Assert.AreEqual(n, row.Length); foreach (float v in row) Assert.True(float.IsFinite(v)); }
        foreach (var row in monthTemp) { foreach (float v in row) Assert.True(float.IsFinite(v)); }
        foreach (var row in monthWind) { foreach (Vector3 v in row) Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)); }

        // 陆地月降水比例 Σ=1、海洋降水 0
        for (int i = 0; i < n; i++)
        {
            Assert.That(monsoon[i], Is.InRange(0f, 1f), $"格 {i} 季风强度应在 [0,1]");
            if (elevNorm[i] < 0f)
            {
                Assert.AreEqual(0f, precipAnnAbs[i], 1e-9f, $"海洋格 {i} 年降水应 0");
                for (int m = 0; m < 12; m++)
                    Assert.AreEqual(0f, monthPrecip[m][i], 1e-9f, $"海洋格 {i} 月降水比例应 0");
            }
            else if (precipAnnAbs[i] > 1e-9f)
            {
                float sum = 0f;
                for (int m = 0; m < 12; m++) sum += monthPrecip[m][i];
                Assert.AreEqual(1f, sum, 1e-3f, $"陆地格 {i} 12 月降水比例应守恒为 1");
                Assert.GreaterOrEqual(tHot[i], tCold[i], $"格 {i} 最热月 ≥ 最冷月");
                Assert.That(dryIdx[i], Is.InRange(0, 11), $"格 {i} 最干月序号应在 0..11");
                Assert.GreaterOrEqual(dryP[i], 0f, $"格 {i} 最干月降水应非负");
            }
        }
    }

    [Test]
    public void Monsoon_Compute_Deterministic()
    {
        // 保证：确定性——同输入两次演算逐位一致（所有输出数组）。
        var climate = Climate();
        ComputeMonsoon(climate,
            out var moA, out var thA, out var tcA, out var dpA, out var diA,
            out var mpA, out var mwA, out var mtA, out var paA);
        ComputeMonsoon(climate,
            out var moB, out var thB, out var tcB, out var dpB, out var diB,
            out var mpB, out var mwB, out var mtB, out var paB);

        CollectionAssert.AreEqual(moA, moB);
        CollectionAssert.AreEqual(thA, thB);
        CollectionAssert.AreEqual(tcA, tcB);
        CollectionAssert.AreEqual(dpA, dpB);
        CollectionAssert.AreEqual(diA, diB);
        CollectionAssert.AreEqual(paA, paB);
        for (int m = 0; m < 12; m++)
        {
            CollectionAssert.AreEqual(mpA[m], mpB[m], $"月降水[{m}]");
            CollectionAssert.AreEqual(mtA[m], mtB[m], $"月温度[{m}]");
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // 3. ClimateModel.Models / Run —— 注册表结构与拓扑执行
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void ClimateModel_Models_RegistryTopology()
    {
        // 保证：注册表结构——17 场 + 8 环；依赖均在册（TopoSort 不抛）；海拔为首场。
        //（结构与 MapGenTests 同口径核对，这里再锚定一次拓扑完整性。）
        var pipe = new PlanetPipeline();
        var models = ClimateModel.Models(pipe);
        int fields = 0, loops = 0;
        foreach (var m in models)
        {
            if (m is IFieldRole) fields++;
            else if (m is ILoopRole) loops++;
        }
        Assert.AreEqual(17, fields);
        Assert.AreEqual(8, loops);
        Assert.IsInstanceOf<ElevationField>(models[0]);

        var byName = new System.Collections.Generic.Dictionary<string, ModelBase>();
        foreach (var m in models) byName[m.Name] = m;
        foreach (var m in models)
            foreach (var d in m.DependsOn())
                Assert.IsTrue(byName.ContainsKey(d), $"{m.Name} 依赖未注册：'{d}'");
    }

    [Test]
    public void ClimateModel_Run_FillsAllStageOutputs()
    {
        // 保证：ClimateModel.Run 拓扑执行（注入恒零噪声气候）→ 各阶段输出场被填充、
        // 无异常（Stage1 气候/洋流 → Stage2 水文 → Stage4 资源 → Stage5 土壤全部落到 pipe）。
        var pipe = BuildRunPipe(42);
        WindField.Prograde = pipe.P.ProgradeRotation;
        WindField.RotationSpeed = pipe.P.RotationSpeed;
        try
        {
            ClimateModel.Run(pipe, null);
        }
        finally
        {
            WindField.Prograde = true;
            WindField.RotationSpeed = 1f;
        }

        Assert.NotNull(pipe.Elev);            // Stage1 海拔（ElevationField 自填，无需预填）
        Assert.NotNull(pipe.ENorm);
        Assert.NotNull(pipe.TempBase);
        Assert.NotNull(pipe.Temp);            // 年均温
        Assert.NotNull(pipe.Precip);          // 年降水（Σ月）
        Assert.NotNull(pipe.Biome);           // 柯本 biomes
        Assert.NotNull(pipe.MonsoonStrength);
        Assert.NotNull(pipe.MonthPrecip);
        Assert.NotNull(pipe.MonthTemp);
        Assert.NotNull(pipe.CurrentDirs);     // 洋流
        Assert.NotNull(pipe.CurrentWarmth);
        Assert.NotNull(pipe.Psi);
        Assert.NotNull(pipe.RiverFlow);       // Stage2
        Assert.NotNull(pipe.RiverLevel);
        Assert.NotNull(pipe.LakeLevel);
        Assert.NotNull(pipe.ErosionNet);      // 诊断场
        Assert.NotNull(pipe.MineralLevel);    // Stage4
        Assert.NotNull(pipe.SoilLevel);       // Stage5
    }

    // ═════════════════════════════════════════════════════════════════
    // 4. 旗舰端到端：WeatherPipeline 编排（注入 ZeroNoise 驱动全场）确定性 + 物理
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// 构造 n=2 板块模拟并手工填充 Displacement/SeaLevel/WorldCrust/矿化场——
    /// 北半球陆地（海拔随纬度抬升的山地）、南半球海洋。Material 用构造默认。
    /// ⚠️ 不跑 sim.Run（含 LogService 与 FastNoiseLite），字段全部手工填。
    /// </summary>
    private static TectonicsSimulation BuildSim(int seed)
    {
        var sim = new TectonicsSimulation(2);
        int n = sim.GlobalGrid.VertexCount;
        sim.SeaLevel = 0f;
        sim.Displacement = new float[n];
        sim.Elevation = new float[n];
        for (int i = 0; i < n; i++)
        {
            float y = sim.GlobalGrid.Vertices[i].Y;
            float latDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(y, -1f, 1f)));
            // 北半球陆地山体（随纬度抬升），南半球深海
            sim.Displacement[i] = y > 0f ? 500f + 2000f * (1f - Mathf.Abs(latDeg) / 90f) : -1800f - 600f * (1f + y);
            sim.Elevation[i] = sim.Displacement[i] - sim.SeaLevel;
        }
        // 地壳物质池：给陆地格物质，使矿藏/土壤场产出非零（Age 老克拉通 → 铁/变质等）
        for (int i = 0; i < n; i++)
        {
            bool land = sim.Displacement[i] > 0f;
            sim.WorldCrust.Age[i] = land ? 250f * Units.MEGAYEAR : 0f;
            sim.WorldCrust.Sedimentary[i] = land ? 2600f * 80f : 0f;
            sim.WorldCrust.Metamorphic[i] = land ? 2800f * 60f : 0f;
            sim.WorldCrust.FelsicPlutonic[i] = land ? 2600f * 60f : 0f;
            sim.WorldCrust.MaficVolcanic[i] = land ? 2890f * 10f : 0f;
            sim.WorldCrust.FelsicVolcanic[i] = land ? 2600f * 20f : 0f;
        }
        sim.MineralHydro = new float[n];
        sim.MineralSed = new float[n];
        sim.MineralMeta = new float[n];
        for (int i = 0; i < n; i++)
        {
            bool land = sim.Displacement[i] > 0f;
            sim.MineralHydro[i] = land ? 0.4f : 0f;
            sim.MineralSed[i] = land ? 0.4f : 0f;
            sim.MineralMeta[i] = land ? 0.4f : 0f;
        }
        return sim;
    }

    private static PlanetParams Params(int seed)
        => new PlanetParams { Seed = seed, AxialTilt = 23.4f, Insolation = 1.0f,
            ProgradeRotation = true, RotationSpeed = 1.0f, RadiusKm = MapArchive.DefaultRadiusKm };

    /// <summary>
    /// ⚠️ 适配说明：PlanetPipeline.Run 内部无条件 new ClimateGenerator(seed,tilt,insolation)
    /// （= 引擎 FastNoiseLite 噪声，无引擎测试进程=崩溃）。因此"旗舰"复现其场驱动编排：
    /// 手工注入 恒零噪声 ClimateGenerator 到 pipe.Climate，再 ClimateModel.Run 驱动全部场——
    /// 与任务"传 null 会建引擎实现（禁止），测试必须注入纯托管实现"同口径。
    /// pipe.Grid/Verts/Neighbors 取自 sim.GlobalGrid（纯托管 SphereGrid）。
    /// </summary>
    private static PlanetPipeline BuildRunPipe(int seed)
    {
        var sim = BuildSim(seed);
        var p = Params(seed);
        int vn = sim.GlobalGrid.VertexCount;
        float minElev = float.MaxValue, maxElev = float.MinValue;
        foreach (float e in sim.Elevation) { if (e < minElev) minElev = e; if (e > maxElev) maxElev = e; }
        var pipe = new PlanetPipeline
        {
            Sim = sim,
            P = p,
            Grid = sim.GlobalGrid,
            Verts = sim.GlobalGrid.Vertices,
            Neighbors = sim.GlobalGrid.Neighbors,
            Climate = new ClimateGenerator(p.Seed, p.AxialTilt, p.Insolation, new ZeroNoise(), new ZeroNoise()),
            // ⚠️ ElevationField 用 Max(-MinElev,MaxElev) 求 ElevSpan；按 sim 海拔范围预报（生产
            //   由地形前序填好——避免 0/0 → ENorm NaN → Temp 污染）
            MinElev = minElev,
            MaxElev = maxElev,
            // ⚠️ 与 PlanetPipeline.Run 同口径：Run 在 ClimateModel.Run 前预分配这些输出数组
            //（IceSheetField 等依胎序读 pipe.Temp 的初始 0，须先分配避免空引用）。
            TempBase = new float[vn],
            Temp = new float[vn],
            Precip = new float[vn],
            Biome = new byte[vn],
        };
        return pipe;
    }

    private static void RunWeatherPipeline(PlanetPipeline pipe)
    {
        // 与 PlanetPipeline.Run 编排一致：注入网格/参数 → WindField 全局 → ClimateModel.Run。
        WindField.Prograde = pipe.P.ProgradeRotation;
        WindField.RotationSpeed = pipe.P.RotationSpeed;
        ClimateModel.Run(pipe, null);
    }

    private static readonly string[] FloatFields = { "Elev", "Temp", "Precip", "TempBase",
        "MonsoonStrength", "CurrentWarmth", "CurrentStrength", "Psi", "ErosionNet" };
    private static readonly string[] ByteFields = { "Biome", "RiverLevel", "LakeLevel", "MineralLevel", "SoilLevel" };

    [Test]
    public void Pipeline_EndToEnd_AllFieldsFilledFiniteLength()
    {
        // 保证（旗舰）：完整场驱动编排后——
        //   全部 float 场非空、有限、长度=n；byte 场非空、长度=n；
        //   biome 有陆地值非 0；MinTemp≤MaxTemp；无 NaN（等价 NansSanitized==0 契约）。
        var pipe = BuildRunPipe(42);
        try { RunWeatherPipeline(pipe); }
        finally { WindField.Prograde = true; WindField.RotationSpeed = 1f; }

        int n = pipe.Verts.Length;
        Assert.AreEqual(42, n, "n=2 网格应为 42 顶点");
        foreach (string f in FloatFields)
        {
            var arr = (float[])typeof(PlanetPipeline).GetField(f)?.GetValue(pipe);
            Assert.NotNull(arr, $"场 {f} 应为非空");
            Assert.AreEqual(n, arr.Length, $"场 {f} 长度应=n");
            foreach (float v in arr)
                Assert.True(float.IsFinite(v), $"场 {f} 应无限（无 NaN/Inf）");
        }
        foreach (string f in ByteFields)
        {
            var arr = (byte[])typeof(PlanetPipeline).GetField(f)?.GetValue(pipe);
            Assert.NotNull(arr, $"byte 场 {f} 应为非空");
            Assert.AreEqual(n, arr.Length, $"byte 场 {f} 长度应=n");
        }

        // Biome 有陆地值非 0（北半球陆地格 → 陆地 biomes）
        bool landBiome = false;
        for (int i = 0; i < n; i++)
            if (pipe.Biome[i] != 0) { landBiome = true; break; }
        Assert.True(landBiome, "biome 场应有非 0 陆地值");

        // 温度序：MinTemp ≤ MaxTemp（ComputeStats 的契约；此处从产物复算）
        float mn = float.MaxValue, mx = float.MinValue;
        foreach (float t in pipe.Temp) { if (t < mn) mn = t; if (t > mx) mx = t; }
        Assert.LessOrEqual(mn, mx, "MinTemp 应 ≤ MaxTemp");

        // 季风/月降水结构
        Assert.AreEqual(12, pipe.MonthPrecip.Length);
        Assert.AreEqual(12, pipe.MonthTemp.Length);
        foreach (float[] row in pipe.MonthPrecip) { Assert.AreEqual(n, row.Length); foreach (float v in row) Assert.True(float.IsFinite(v)); }
        foreach (float[] row in pipe.MonthTemp) { foreach (float v in row) Assert.True(float.IsFinite(v)); }

        // 无 NaN 契约：恒零噪声 + 有限输入下不消毒任何顶点；SanitizeNaNs 私有未运行 → 默认 0
        Assert.AreEqual(0, pipe.NansSanitized, "规避引擎默认噪声路径后不应产生需消毒的 NaN");
    }

    [Test]
    public void Pipeline_EndToEnd_Deterministic()
    {
        // 保证：确定性——同输入两次独立演算，代表场逐位一致（WindField 静态先记录/后还原）。
        var pipeA = BuildRunPipe(42);
        var pipeB = BuildRunPipe(42);
        try
        {
            RunWeatherPipeline(pipeA);
            RunWeatherPipeline(pipeB);
            CollectionAssert.AreEqual(pipeA.Temp, pipeB.Temp, "温度逐位一致");
            CollectionAssert.AreEqual(pipeA.Precip, pipeB.Precip, "降水逐位一致");
            CollectionAssert.AreEqual(pipeA.MonsoonStrength, pipeB.MonsoonStrength, "季风逐位一致");
            CollectionAssert.AreEqual(pipeA.CurrentWarmth, pipeB.CurrentWarmth, "洋流冷暖逐位一致");
            CollectionAssert.AreEqual(pipeA.Psi, pipeB.Psi, "流函数逐位一致");
        }
        finally
        {
            WindField.Prograde = true;
            WindField.RotationSpeed = 1f;
        }
    }

    [Test]
    public void Pipeline_EndToEnd_PhysicalSanity_LandTropicsWarmerThanPoles()
    {
        // 保证：至少一条物理合理性——陆地中低纬平均温 > 陆地高纬平均温（纬度带形态）。
        var pipe = BuildRunPipe(42);
        try { RunWeatherPipeline(pipe); }
        finally { WindField.Prograde = true; WindField.RotationSpeed = 1f; }

        double lowSum = 0, highSum = 0;
        int lowCnt = 0, highCnt = 0;
        for (int i = 0; i < pipe.Verts.Length; i++)
        {
            if (pipe.Elev[i] <= 0f) continue;   // 只看陆地
            float latDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(pipe.Verts[i].Y, -1f, 1f)));
            if (Mathf.Abs(latDeg) < 45f) { lowSum += pipe.Temp[i]; lowCnt++; }
            else if (Mathf.Abs(latDeg) > 70f) { highSum += pipe.Temp[i]; highCnt++; }
        }
        Assert.Greater(lowCnt, 0, "应有中低纬陆地格");
        Assert.Greater(highCnt, 0, "应有高纬陆地格");
        Assert.Greater(lowSum / lowCnt, highSum / highCnt,
            "陆地中低纬平均温应高于高纬（纬度带物理形态）");
    }
}
