using System;
using NUnit.Framework;
using World.NewHexWorld.Plate;

namespace World.Tests;

/// <summary>
/// 岩石圈热柱 + 地幔热状态（设计-05）测试。断言的测量项：
///   · **解析值与既有常量互检**：热柱推出的沉降律系数 == `H3Isostasy.SubsidenceCoefM`（350 m/√My）；
///     深度积分密度亏损 N == (ρ_m − ρ_w)·沉降（同一解的两种写法，严格相等）；
///   · **与老口径的差登记**：N(250 My) 比"7 km 洋壳密度链"口径高 5–10×（本批存在的理由，钉住防漂移）；
///   · 岩石圈厚度/地表热流落在半空间冷却的地球量级；
///   · 黏度：参考温度处 == 参考黏度（保证初态与旧口径逐位一致）、降温 ⇒ 单调变稠、40 K ⇒ ×2 量级；
///   · 势温演化：t=0 = 地球现值、600 My 降 ~40 K、同位素衰变因子单调降、同序列两次逐位一致；
///   · 热收支审计：单步 ΔTp 与"净散热·Δt/C"恒等（结算口径自洽）。
/// 纪律（同其余逻辑层测试）：只用 [Test]；不写文件；不触碰 GD.*/LogService；纯托管（无引擎宿主可跑）。
/// </summary>
public class H3ThermalTests
{
    const float LegacyLumpBuoyancyKgPerM2 = 225f * 7000f;   // 老口径：Δρ(250 My) 225 kg/m³ × 壳厚 7000 m

    // ── 热柱解析值与既有常量互检 ──

    [Test]
    public void ThermalColumn_SubsidenceCoef_MatchesIsostasyConstant()
    {
        float derived = H3ThermalColumn.SubsidenceCoefMPerSqrtMy();
        Assert.That(derived, Is.InRange(340f, 370f),
            $"热柱推出的沉降律系数 {derived:F1} m/√My 应落在 350 邻域（半空间冷却解析解）");
        Assert.That(MathF.Abs(derived - H3Isostasy.SubsidenceCoefM) / H3Isostasy.SubsidenceCoefM, Is.LessThan(0.03f),
            $"与 H3Isostasy.SubsidenceCoefM({H3Isostasy.SubsidenceCoefM}) 应一致（差 1.3% 是取整）——"
            + "两条沉降律漂移必须当场打到断言");
    }

    [Test]
    public void ThermalColumn_Buoyancy_EqualsWaterLoadBalance()
    {
        // 同一解的两种写法必须严格相等：N = ρ_m α T_m·2√(κt/π)  ⟺  (ρ_m − ρ_w)·d(t)
        // （前者是"深度积分密度亏损"，后者是"均衡代价"——漂移说明 erf 剖面的积分口径被改坏了）
        foreach (float age in new[] { 1f, 10f, 50f, 100f, 150f, 250f })
        {
            float fromIntegral = H3ThermalColumn.NegativeBuoyancyKgPerM2(age);
            float fromLoad = (H3ThermalColumn.MantleDensityKgM3 - H3ThermalColumn.WaterDensityKgM3)
                * H3ThermalColumn.SubsidenceM(age);
            Assert.AreEqual(fromIntegral, fromLoad, fromIntegral * 1e-4f, $"age {age} My 处两式应相等");
        }
    }

    [Test]
    public void ThermalColumn_NegativeBuoyancy_GrowsAsSqrtAge()
    {
        float n25 = H3ThermalColumn.NegativeBuoyancyKgPerM2(25f);
        float n100 = H3ThermalColumn.NegativeBuoyancyKgPerM2(100f);
        Assert.AreEqual(2f, n100 / n25, 0.02f, "密度亏损应随 √age 增长（半空间冷却的标志）");
        Assert.That(n100, Is.InRange(7e6f, 9e6f), $"N(100 My) = {n100:E2} kg/m² 应 ≈ 8e6（= 水载均衡量级）");
    }

    [Test]
    public void ThermalColumn_PerLengthDrive_MatchesLiteratureSlabPull()
    {
        // 逐长度口径的驱动力（05-B）：F/L = N·g·l_dip。地球文献板片拉力 ~3.3e13 N/m 量级
        // （Forsyth & Uyeda 1975；冷板片密度盈余 1–2% × 100 km × 600 km 下潜长度）。
        // ⚠️ 本模型 N ∝ √t 无"老洋底变平"（真实在 ~70–100 My 后走 plate model 变平）⇒ 老洋壳侧偏高
        // 是已知的（250 My 处 1.3e7 kg/m² ⇒ F/L ≈ 7.6e13），故只按**同量级**钉。
        const float g = 9.8f;
        float perLength100 = H3ThermalColumn.NegativeBuoyancyKgPerM2(100f) * g * H3PlateMotion.SlabDipLengthM;
        float perLength60 = H3ThermalColumn.NegativeBuoyancyKgPerM2(60f) * g * H3PlateMotion.SlabDipLengthM;
        Assert.That(perLength100, Is.InRange(2.5e13f, 7e13f),
            $"100 My 板片拉力/海沟长度 {perLength100:E2} N/m 应落文献量级 3.3e13");
        Assert.That(perLength60, Is.InRange(1.5e13f, 4.5e13f),
            $"60 My 处 {perLength60:E2} N/m（≈ 文献典型值，对应地球常见洋盆年龄）");
    }

    [Test]
    public void ThermalColumn_Drive_IsOrderOfMagnitudeAboveLegacyLump()
    {
        // 老口径把负浮力挂在 7 km 洋壳上（密度 2890→3300 的 +14% 是标定出来的）⇒ N 比热柱低 5–10×，
        // 且 age < 113 My 时恒为 0（阈值型），热柱从脊轴起连续增长。这两条是本批要修的根因，钉住。
        float thermal150 = H3ThermalColumn.NegativeBuoyancyKgPerM2(150f);
        Assert.Greater(thermal150 / LegacyLumpBuoyancyKgPerM2, 5f,
            $"热柱 N(150 My) = {thermal150:E2} 应比老口径 {LegacyLumpBuoyancyKgPerM2:E2} 高 5× 以上（驱动被低估的量级）");
        Assert.Less(thermal150 / LegacyLumpBuoyancyKgPerM2, 12f, "但也不该离谱（同量级校验）");
    }

    [Test]
    public void ThermalColumn_LithosphereThickness_AndHeatFlow_MatchEarthRanges()
    {
        float h100 = H3ThermalColumn.LithosphereThicknessM(100f) / 1000f;
        Assert.That(h100, Is.InRange(115f, 145f), $"100 My 岩石圈厚 {h100:F0} km 应 ≈ 125–130 km（1300℃ 等温线）");

        // 热流是**诊断量**（驱动与沉降才是判据量）：同参数集给 10 My ≈ 124 / 100 My ≈ 39 mW/m²，
        // 而 Parsons & Sclater 独立拟合的 473/√t 给 150 / 47——差 ~1.2× 是因为参数集被**沉降律**
        // （350 m/√My）钉住，不是被热流拟合钉住；且实测洋壳热流本身因热液对流低于半空间预测。
        // 故断言按本模型自洽值，同时守住"量级与 √t 递减"这两条物理特征。
        float q10 = H3ThermalColumn.HeatFlowWPerM2(10f) * 1000f;
        float q100 = H3ThermalColumn.HeatFlowWPerM2(100f) * 1000f;
        Assert.That(q10, Is.InRange(110f, 140f), $"10 My 热流 {q10:F0} mW/m²");
        Assert.That(q100, Is.InRange(33f, 48f), $"100 My 热流 {q100:F0} mW/m²");
        float q400 = H3ThermalColumn.HeatFlowWPerM2(400f) * 1000f;
        Assert.AreEqual(2f, q100 / q400, 0.05f, "热流按 1/√t 递减（100 → 400 My 应降 2×）");
        Assert.Greater(q10, q100, "热流随年龄递减");
    }

    [Test]
    public void ThermalColumn_ZeroAge_IsCappedAtMinAge()
    {
        // 半空间解 t→0 发散（q→∞、h→0）：脊轴格按 MinAgeMy 计，必须有限且与 1 My 相同
        float q0 = H3ThermalColumn.HeatFlowWPerM2(0f);
        Assert.IsTrue(float.IsFinite(q0), "零龄热流应有限（下限封顶）");
        Assert.AreEqual(q0, H3ThermalColumn.HeatFlowWPerM2(H3ThermalColumn.MinAgeMy), 1e-6f);
        // 沉降与密度亏损在 t→0 有界（→0）：脊轴格不产生负浮力（封顶只加给热流那一条）
        Assert.AreEqual(0f, H3ThermalColumn.SubsidenceM(0f), 0.01f, "脊轴沉降基准 = 0");
        Assert.AreEqual(0f, H3ThermalColumn.NegativeBuoyancyKgPerM2(0f), 0.01f, "脊轴格无负浮力（驱动力为 0）");
    }

    // ── 黏度 ──

    [Test]
    public void Viscosity_AtReferenceTemperature_EqualsReferenceViscosity()
    {
        float eta = H3ThermalColumn.ViscosityPaS(H3ThermalState.ReferencePotentialTemperatureK,
            H3ThermalState.ReferencePotentialTemperatureK, H3ThermalState.ReferenceViscosityPaS,
            H3ThermalState.ActivationEnergyJPerMol);
        Assert.AreEqual(H3ThermalState.ReferenceViscosityPaS, eta, 1f,
            "参考温度处必须严格回到参考黏度（t = 0 与旧口径逐位一致的前提）");
    }

    [Test]
    public void Viscosity_Cooling40K_DoublesAndIsMonotonic()
    {
        const float refK = H3ThermalState.ReferencePotentialTemperatureK;
        float eta0 = H3ThermalColumn.ViscosityPaS(refK, refK, H3ThermalState.ReferenceViscosityPaS, H3ThermalState.ActivationEnergyJPerMol);
        float cold = H3ThermalColumn.ViscosityPaS(refK - 40f, refK, H3ThermalState.ReferenceViscosityPaS, H3ThermalState.ActivationEnergyJPerMol);
        float hot = H3ThermalColumn.ViscosityPaS(refK + 40f, refK, H3ThermalState.ReferenceViscosityPaS, H3ThermalState.ActivationEnergyJPerMol);
        Assert.That(cold / eta0, Is.InRange(1.8f, 2.4f), $"降 40 K ⇒ η ×{cold / eta0:F2}（E=400 kJ/mol 量级 ×2）");
        Assert.Less(hot, eta0, "变热应更稀（单调）");
        Assert.Greater(cold, eta0, "变冷应更稠（单调）");
    }

    // ── 地幔热状态 ──

    [Test]
    public void ThermalState_InitialState_IsEarthPresentDay()
    {
        var state = new H3ThermalState();
        Assert.AreEqual(H3ThermalState.ReferencePotentialTemperatureK, state.PotentialTemperatureK);
        Assert.AreEqual(H3ThermalState.ReferenceViscosityPaS, state.MantleViscosityPaS, 1f);
        Assert.AreEqual(H3ThermalState.PresentRadiogenicTW, state.RadiogenicHeatTW, 1e-4f);
        Assert.AreEqual(1f, H3ThermalState.IsotopeDecayFactor(0f), 1e-4f, "t = 0 的衰变因子应为 1（现值归一）");
        Assert.That(state.DeclineKPerGa, Is.InRange(60.0, 80.0), $"t=0 降温率 {state.DeclineKPerGa:F1} K/Ga 应钉在地质约束邻域");
    }

    [Test]
    public void ThermalState_After600My_CoolsAbout40K_ViscosityAboutDoubles()
    {
        var fields = OceanWorld(cells: 400, oceanAgeMy: 60f);
        var state = new H3ThermalState();
        float eta0 = state.MantleViscosityPaS;
        for (int s = 0; s < 150; s++) state.Step(fields, 4f);      // 600 My / 4 My

        Assert.That(H3ThermalState.ReferencePotentialTemperatureK - state.PotentialTemperatureK, Is.InRange(25f, 55f),
            $"600 My 势温应降 ~39 K（地质约束 70 K/Ga × 燃料衰减），实际降 "
            + $"{H3ThermalState.ReferencePotentialTemperatureK - state.PotentialTemperatureK:F1} K");
        Assert.That(state.MantleViscosityPaS / eta0, Is.InRange(1.6f, 2.6f),
            $"600 My 后黏度应 ×2 量级（实际 ×{state.MantleViscosityPaS / eta0:F2}）—— 这是「越跑越弱」的响应半边");
        Assert.Less(state.DeclineKPerGa, H3ThermalState.TargetDeclineKPerGa + 1.0,
            "降温率应随燃料衰减不升（同位素耗掉 ⇒ 越到后面降得越慢）");
    }

    [Test]
    public void ThermalState_IsotopeDecayFactor_DecreasesMonotonically()
    {
        float previous = float.MaxValue;
        for (float t = 0f; t <= 600f; t += 50f)
        {
            float factor = H3ThermalState.IsotopeDecayFactor(t);
            Assert.Less(factor, previous + 1e-6f, $"衰变因子应在 t = {t} My 处不升");
            previous = factor;
        }
        Assert.That(H3ThermalState.IsotopeDecayFactor(600f), Is.InRange(0.85f, 0.99f),
            "600 My 内同位素总量只掉几个百分点（U235/K40 主导）");
    }

    [Test]
    public void ThermalState_SingleStep_TemperatureChangeMatchesHeatBudget()
    {
        // 结算口径自洽（审计）：ΔTp 必须恒等于 −净散热·Δt/C
        var fields = OceanWorld(cells: 400, oceanAgeMy: 60f);
        var state = new H3ThermalState();
        double before = state.PotentialTemperatureK;
        state.Step(fields, 4f);
        double netCoolingTW = H3ThermalState.MantleHeatCapacityJPerK
            * (H3ThermalState.TargetDeclineKPerGa / 3.1558e16) * H3ThermalState.IsotopeDecayFactor(0f) / 1e12;
        double expectedDeltaK = -netCoolingTW * 1e12 * (4f * 3.1558e13) / H3ThermalState.MantleHeatCapacityJPerK;
        Assert.AreEqual(expectedDeltaK, state.PotentialTemperatureK - before, 1e-3,
            "单步 ΔTp 与热收支结算应恒等");
    }

    [Test]
    public void ThermalState_AllLandWorld_NoOceanHeatFlow_AndGapIsExposed()
    {
        var fields = new H3PlateFields(200);
        Array.Fill(fields.PlateId, 0);
        for (int i = 0; i < fields.Count; i++)
        {
            fields.FelsicPlutonic[i] = 2700f * 1000f;      // 全陆（IsLand = 长英质 > 0）
            fields.Age[i] = 1000f;
        }
        var state = new H3ThermalState();
        state.Step(fields, 4f);
        Assert.AreEqual(0f, state.OceanHeatFlowTW, 1e-6f, "全陆世界洋岩石圈散热应为 0");

        // 有洋世界：散热应落在地球量级，且收支缺口被显式暴露（Urey 比悖论登记在案，不是可调参数）
        var ocean = OceanWorld(cells: 400, oceanAgeMy: 60f);
        var state2 = new H3ThermalState();
        state2.Step(ocean, 4f);
        Assert.Greater(state2.OceanHeatFlowTW, 0f);
        Assert.IsTrue(float.IsFinite((float)state2.HeatBalanceGapTW), "缺口应是有限数（诊断口，不是 NaN）");
    }

    [Test]
    public void ThermalState_SameSequenceTwice_BitwiseIdentical()
    {
        var fields = OceanWorld(cells: 300, oceanAgeMy: 80f);
        var a = new H3ThermalState();
        var b = new H3ThermalState();
        for (int s = 0; s < 40; s++) { a.Step(fields, 4f); b.Step(fields, 4f); }
        Assert.AreEqual(a.PotentialTemperatureK, b.PotentialTemperatureK);
        Assert.AreEqual(a.MantleViscosityPaS, b.MantleViscosityPaS);
        Assert.AreEqual(a.HeatBalanceGapTW, b.HeatBalanceGapTW);
    }

    // 造一个给定规模的"洋世界"场（只需 Age/PlateId/IsLand 三件——热状态不读别的）
    static H3PlateFields OceanWorld(int cells, float oceanAgeMy)
    {
        var fields = new H3PlateFields(cells);
        Array.Fill(fields.PlateId, 0);
        for (int i = 0; i < cells; i++) fields.Age[i] = oceanAgeMy;
        return fields;
    }
}
