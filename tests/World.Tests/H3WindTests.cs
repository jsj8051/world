using System;
using NUnit.Framework;
using Godot;
using World.NewHexWorld;
using World.NewHexWorld.Plate;

namespace World.Tests;

/// <summary>
/// 盛行风场护栏（气候批次 1.5，2026-09-17 自老树 WindField/MaritimeScore 迁移）：
///   · 三圈环流带：30°/60° 断点归上带（老树 BeltAt 同口径）；
///   · 解析风：切向、单位长、极点零向量不炸、金星式逆转翻转纬向分量、自转速度单调增强偏转；
///   · 上风向模板：构建确定性、落格合法；
///   · 海洋湿润度：全海 +1 / 全陆 −1；集成口全海风显著湿于全陆风（精确比值 1.55/0.45）；
///   · 地形雨影：迎风坡配置精确比值 (1+0.65)/(1−0.65) 湿于背风坡；
///   · 确定性：同入参降水场逐位一致。
/// 纪律（同 H3WaterCycleTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3WindTests
{
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
    static Ball Ball => SharedBall.Value;

    static float LatDegOf(int i)
        => MathF.Asin(Math.Clamp(Ball.CellDirs[i].Y, -1f, 1f)) * 180f / MathF.PI;

    // ── 三圈环流带 ──

    [TestCase(0f, H3Wind.Belt.Hadley)]
    [TestCase(29.9f, H3Wind.Belt.Hadley)]
    [TestCase(30f, H3Wind.Belt.Ferrel)]
    [TestCase(59.9f, H3Wind.Belt.Ferrel)]
    [TestCase(60f, H3Wind.Belt.Polar)]
    [TestCase(89f, H3Wind.Belt.Polar)]
    public void BeltAt_Thresholds(float latDeg, H3Wind.Belt expected)
        => Assert.AreEqual(expected, H3Wind.BeltAt(latDeg));

    // ── 解析风 ──

    [Test]
    public void WindAt_TangentAndUnitLength()
    {
        int checkedCells = 0;
        for (int i = 0; i < Ball.CellIds.Length; i++)
        {
            Vector3 dir = Ball.CellDirs[i];
            Vector3 w = H3Wind.WindAt(dir);
            if (w == Vector3.Zero) continue;   // 极点退化口单测
            Assert.Less(Math.Abs(w.Dot(dir)), 1e-4f, $"风向不切向：格 {i}");
            Assert.AreEqual(1f, w.Length(), 1e-4f, $"风向非单位长：格 {i}");
            checkedCells++;
        }
        Assert.Greater(checkedCells, Ball.CellIds.Length / 2, "非极点格应全部产出风");
    }

    [Test]
    public void WindAt_PolesReturnZeroVector_AllFinite()
    {
        Assert.AreEqual(Vector3.Zero, H3Wind.WindAt(new Vector3(0f, 1f, 0f)));
        Assert.AreEqual(Vector3.Zero, H3Wind.WindAt(new Vector3(0f, -1f, 0f)));
        for (int i = 0; i < Ball.CellIds.Length; i++)
        {
            Vector3 w = H3Wind.WindAt(Ball.CellDirs[i]);
            Assert.IsTrue(float.IsFinite(w.X) && float.IsFinite(w.Y) && float.IsFinite(w.Z),
                $"格 {i} 风向出现非有限值（老树极点 NaN 口不许回归）");
        }
    }

    [Test]
    public void WindAt_RetrogradeFlipsZonalKeepsMeridional()
    {
        int i0 = MidLatCell();
        Vector3 dir = Ball.CellDirs[i0];
        Vector3 east = new Vector3(-dir.Z, 0f, dir.X).Normalized();
        Vector3 pro = H3Wind.WindAt(dir, prograde: true);
        Vector3 ret = H3Wind.WindAt(dir, prograde: false);

        float eastPro = pro.Dot(east);
        float eastRet = ret.Dot(east);
        Assert.Less(eastPro * eastRet, 0f, "金星式逆转应翻转纬向分量符号");
        Assert.AreEqual(MathF.Abs(eastPro), MathF.Abs(eastRet), 1e-5f, "纬向分量幅值应不变");

        Vector3 northDir = dir.Cross(east).Normalized();
        Assert.AreEqual(pro.Dot(northDir), ret.Dot(northDir), 1e-5f,
            "经向分量（信风/西风带的向极向赤道部分）不应随自转方向翻转");
    }

    [Test]
    public void WindAt_FasterSpinStrongerZonalDeflection()
    {
        int i0 = MidLatCell();
        Vector3 dir = Ball.CellDirs[i0];
        Vector3 east = new Vector3(-dir.Z, 0f, dir.X).Normalized();

        float slow = MathF.Abs(H3Wind.WindAt(dir, true, 0.2f).Dot(east));
        float earth = MathF.Abs(H3Wind.WindAt(dir, true, 1f).Dot(east));
        float fast = MathF.Abs(H3Wind.WindAt(dir, true, 5f).Dot(east));
        Assert.Greater(slow, 0f, "慢自转也应有偏转（sin|lat| 项）");
        Assert.Greater(earth, slow, "自转加快应增强偏转（亚线性）");
        Assert.Greater(fast, earth, "自转加快应增强偏转");
    }

    // ── 上风向模板 ──

    [Test]
    public void BuildStencil_Deterministic_ValidIndices()
    {
        var a = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var b = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        int n = Ball.CellIds.Length;

        for (int i = 0; i < n; i++)
        {
            Assert.AreEqual(a.MaritimeCell[i], b.MaritimeCell[i], $"格 {i} 湿润度落格不确定");
            Assert.AreEqual(a.RainShadowDownwind[i], b.RainShadowDownwind[i], $"格 {i} 雨影下风落格不确定");
            Assert.AreEqual(a.RainShadowUpwind[i], b.RainShadowUpwind[i], $"格 {i} 雨影上风落格不确定");
            for (int k = 0; k < a.MaritimeCell[i].Length; k++)
            {
                Assert.GreaterOrEqual(a.MaritimeCell[i][k], 0);
                Assert.Less(a.MaritimeCell[i][k], n, "湿润度落格越界");
            }
            Assert.GreaterOrEqual(a.RainShadowDownwind[i], 0);
            Assert.Less(a.RainShadowDownwind[i], n, "雨影落格越界");
        }
        Assert.AreEqual(MaritimeSampleCount, a.MaritimeWeight.Length, "采样点数应与老树同值");
    }

    const int MaritimeSampleCount = 10;

    // ── 海洋湿润度 ──

    [Test]
    public void MaritimeScore_AllOceanPlusOne_AllLandMinusOne()
    {
        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        int n = Ball.CellIds.Length;
        var allOcean = new bool[n];
        var allLand = new bool[n];
        Array.Fill(allLand, true);

        var scores = new float[n];
        H3Wind.MaritimeScores(stencil, allOcean, scores);
        foreach (float s in scores)
            Assert.AreEqual(1f, s, 1e-5f, "上风向全海应 = +1（湿润海洋风）");

        H3Wind.MaritimeScores(stencil, allLand, scores);
        foreach (float s in scores)
            Assert.AreEqual(-1f, s, 1e-5f, "上风向全陆应 = −1（干燥大陆风）");
    }

    // ── 降水集成（H3Precipitation 挂盛行风两效应）──

    [Test]
    public void ComputeInto_OceanWindWetterThanLandWind_ExactGainRatio()
    {
        int n = Ball.CellIds.Length;
        var flatElev = new float[n];   // 全平：地形抬升/雨影中性，只剩湿润度差
        var allOcean = new bool[n];
        var allLand = new bool[n];
        Array.Fill(allLand, true);

        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var oceanWind = new float[n];
        var landWind = new float[n];
        H3Precipitation.ComputeInto(Ball, allOcean, flatElev, seed: 42,
            H3Climate.DefaultAxialTiltDeg, H3Climate.DefaultInsolation, true, 1f, oceanWind, stencil);
        H3Precipitation.ComputeInto(Ball, allLand, flatElev, seed: 42,
            H3Climate.DefaultAxialTiltDeg, H3Climate.DefaultInsolation, true, 1f, landWind, stencil);

        for (int i = 0; i < n; i++)
        {
            Assert.Greater(oceanWind[i], landWind[i], $"格 {i}：海洋来风应比大陆来风湿润多雨");
            float ratio = oceanWind[i] / landWind[i];
            Assert.AreEqual(1.55f / 0.45f, ratio, 0.01f,
                $"格 {i}：比值应精确 = (1+0.55)/(1−0.55)（标定挂错？）");
        }
    }

    [Test]
    public void ComputeInto_RainShadow_WindwardWetterThanLeeward_ExactRatio()
    {
        int n = Ball.CellIds.Length;
        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);

        // 中纬格 + 上下风三点互异（0.12 rad 角距在 res1 跨格；极点风向退化格跳过）
        int i0 = -1;
        for (int i = 0; i < n; i++)
        {
            float absLat = MathF.Abs(LatDegOf(i));
            if (absLat < 20f || absLat > 60f) continue;
            int upCell = stencil.RainShadowUpwind[i], downCell = stencil.RainShadowDownwind[i];
            if (upCell != i && downCell != i && upCell != downCell) { i0 = i; break; }
        }
        Assert.GreaterOrEqual(i0, 0, "找不到可用的中纬雨影格");
        int up = stencil.RainShadowUpwind[i0], down = stencil.RainShadowDownwind[i0];

        // 迎风配置：上风低、下风高（风爬坡）；背风配置对调。三点同陆 → 掩码/湿润度/骨架全同，
        // 唯一差异 = 雨影项，比值应精确 = (1+0.65)/(1−0.65)
        var elevWindward = new float[n];
        var elevLeeward = new float[n];
        elevWindward[i0] = 2000f; elevWindward[up] = 500f; elevWindward[down] = 5000f;
        elevLeeward[i0] = 2000f; elevLeeward[up] = 5000f; elevLeeward[down] = 500f;
        var isLand = new bool[n];
        for (int i = 0; i < n; i++) { isLand[i] = elevWindward[i] > 0f; }

        var stencil2 = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var windward = new float[n];
        var leeward = new float[n];
        H3Precipitation.ComputeInto(Ball, isLand, elevWindward, seed: 42,
            H3Climate.DefaultAxialTiltDeg, H3Climate.DefaultInsolation, true, 1f, windward, stencil2);
        H3Precipitation.ComputeInto(Ball, isLand, elevLeeward, seed: 42,
            H3Climate.DefaultAxialTiltDeg, H3Climate.DefaultInsolation, true, 1f, leeward, stencil2);

        Assert.Greater(windward[i0], leeward[i0], "迎风坡应显著湿于背风坡");
        float ratio = windward[i0] / leeward[i0];
        Assert.AreEqual(1.65f / 0.35f, ratio, 0.01f,
            $"比值应精确 = (1+0.65)/(1−0.65)（雨影截幅挂错？实得 {ratio:F3}）");
    }

    [Test]
    public void ComputeInto_Deterministic_SameInputsBitwiseIdentical()
    {
        int n = Ball.CellIds.Length;
        var isLand = new bool[n];
        var elev = new float[n];
        for (int i = 0; i < n; i++)
        {
            float absLat = MathF.Abs(LatDegOf(i));
            isLand[i] = absLat < 30f;                       // 热带陆桥（H3WaterCycleTests 同款模板）
            elev[i] = isLand[i] ? 800f : -3000f;
        }

        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        var a = new float[n];
        var b = new float[n];
        H3Precipitation.ComputeInto(Ball, isLand, elev, 42,
            H3Climate.DefaultAxialTiltDeg, H3Climate.DefaultInsolation, true, 1f, a, stencil);
        H3Precipitation.ComputeInto(Ball, isLand, elev, 42,
            H3Climate.DefaultAxialTiltDeg, H3Climate.DefaultInsolation, true, 1f, b, stencil);
        CollectionAssert.AreEqual(a, b, "降水场逐位不一致");
    }

    // ── 风速度场（v1.22：方向与大小是一个场）──

    [Test]
    public void SpeedMS_BeltOrdering_EarthReferenceAnchors()
    {
        // 地球档海平面：西风带 12 最强（= 归一参考锚）、极地 8、信风 7（低于中纬）
        Assert.AreEqual(12f, H3Wind.SpeedMS(H3Wind.Belt.Ferrel, 1f, 0f), 1e-4f);
        Assert.AreEqual(8f, H3Wind.SpeedMS(H3Wind.Belt.Polar, 1f, 0f), 1e-4f);
        Assert.AreEqual(7f, H3Wind.SpeedMS(H3Wind.Belt.Hadley, 1f, 0f), 1e-4f);
        Assert.AreEqual(H3Wind.FerrelBaseSpeedMS, H3Wind.ReferenceWindSpeedMS,
            "风速归一参考锚必须 = 西风带（风介质权重 1.0 的锚）");
    }

    [Test]
    public void SpeedMS_FasterSpinAndHigherElevationBlowHarder()
    {
        float slow = H3Wind.SpeedMS(H3Wind.Belt.Ferrel, 0.2f, 0f);
        float earth = H3Wind.SpeedMS(H3Wind.Belt.Ferrel, 1f, 0f);
        float fast = H3Wind.SpeedMS(H3Wind.Belt.Ferrel, 5f, 0f);
        Assert.Greater(earth, slow, "自转加快应增强风速");
        Assert.Greater(fast, earth, "自转加快应增强风速");

        float sea = H3Wind.SpeedMS(H3Wind.Belt.Ferrel, 1f, 0f);
        float peak = H3Wind.SpeedMS(H3Wind.Belt.Ferrel, 1f, 8000f);
        float below = H3Wind.SpeedMS(H3Wind.Belt.Ferrel, 1f, -3000f);
        Assert.AreEqual(1.5f, peak / sea, 1e-4f, "8 km 山顶应 ×1.5（海拔项锚点：1 + 0.5·z/8000）");
        Assert.AreEqual(sea, below, 1e-4f, "水下格（海拔 < 0）应钳到海平面风速");
    }

    [Test]
    public void VelocityAt_MagnitudeTimesDirection_OneField()
    {
        // 速度矢量 = 同一个场：大小 = SpeedMS、方向 = WindAt（切向）；极点 = 零矢量（无风）
        int i0 = MidLatCell();
        Vector3 dir = Ball.CellDirs[i0];
        foreach (float elev in new float[] { 0f, 2000f, 8000f })
        {
            Vector3 v = H3Wind.VelocityAt(dir, elev);
            Assert.AreEqual(H3Wind.SpeedMS(H3Wind.BeltAt(LatDegOf(i0)), 1f, elev), v.Length(), 1e-3f,
                $"海拔 {elev}：速度大小应 = SpeedMS");
            Vector3 unit = v.Normalized();
            Assert.Less(MathF.Abs(unit.Dot(dir)), 1e-3f, "速度方向应切向（与 WindAt 同源）");
        }
        Assert.AreEqual(Vector3.Zero, H3Wind.VelocityAt(new Vector3(0f, 1f, 0f), 5000f),
            "极点方向退化 → 零矢量");
    }

    [Test]
    public void SpeedField_Deterministic_AndConsistentWithVelocityAt()
    {
        // 场的两半（模板缓存方向 ⊗ SpeedField 大小）拼起来必须 = 单格口径 VelocityAt
        var stencil = H3Wind.BuildStencil(Ball, prograde: true, rotationSpeed: 1f);
        int n = Ball.CellIds.Length;
        var elev = new float[n];
        for (int i = 0; i < n; i++) elev[i] = (i % 7) * 1000f - 2000f;   // 海下/海面/山地混合

        var a = new float[n];
        var b = new float[n];
        H3Wind.SpeedField(stencil, elev, 1f, a);
        H3Wind.SpeedField(stencil, elev, 1f, b);
        CollectionAssert.AreEqual(a, b, "速度场大小分量逐位不一致");

        for (int i = 0; i < n; i++)
        {
            if (stencil.Direction[i] == Vector3.Zero) continue;   // 极点：方向未定，单测另钉
            Vector3 assembled = stencil.Direction[i] * a[i];
            Vector3 direct = H3Wind.VelocityAt(Ball.CellDirs[i], elev[i]);
            Assert.AreEqual(direct.X, assembled.X, 1e-3f, $"格 {i}：方向 × 大小 ≠ 单格速度矢量");
            Assert.AreEqual(direct.Y, assembled.Y, 1e-3f, $"格 {i}：方向 × 大小 ≠ 单格速度矢量");
            Assert.AreEqual(direct.Z, assembled.Z, 1e-3f, $"格 {i}：方向 × 大小 ≠ 单格速度矢量");
        }
    }

    // 中纬试验格（|lat| ∈ 20-60°：风非零、不在带边界）
    static int MidLatCell()
    {
        for (int i = 0; i < Ball.CellIds.Length; i++)
        {
            float absLat = MathF.Abs(LatDegOf(i));
            if (absLat > 20f && absLat < 60f) return i;
        }
        throw new InvalidOperationException("res1 球上找不到中纬格");
    }
}
