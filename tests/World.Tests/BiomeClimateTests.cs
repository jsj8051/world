using NUnit.Framework;
using Godot;
using World.Biome;

namespace World.Tests;

/// <summary>
/// Biome 模块测试（保险覆盖：BiomeClassifier / WindField / BiomeColors / BiomeType / OceanCurrent）。
/// 全部为纯托管路径（Godot 数学类型安全），不触碰引擎原生调用（GD.*/LogService/FastNoiseLite）。
/// </summary>
public class BiomeClimateTests
{
    // ═══════════════════════════════════════════════════════════════
    // BiomeClassifier.Classify —— 柯本气候分类（含海拔/海陆分支）
    // ═══════════════════════════════════════════════════════════════

    [TestCase(-0.2f, 15f, 500f, 20f, 10f, 80f, BiomeType.DeepOcean)]       // 深海（e < -0.1）
    [TestCase(-0.05f, -5f, 500f, 0f, -10f, 40f, BiomeType.FrigidOcean)]    // 海冰带（t < -2）
    [TestCase(-0.05f, 22f, 1800f, 28f, 24f, 90f, BiomeType.TropicalOcean)] // 热带海洋（t ≥ 18）
    [TestCase(-0.05f, 12f, 900f, 20f, 5f, 70f, BiomeType.Ocean)]           // 温带海洋
    [TestCase(0.6f, 5f, 300f, 12f, -2f, 40f, BiomeType.Alpine)]            // 高山（e > 0.5 且 t ≥ -8）
    [TestCase(0.6f, -12f, 300f, -5f, -18f, 40f, BiomeType.IceCap)]         // 高山冰雪（e > 0.5 且 t < -8）
    public void Classify_ElevationBranches(float elev, float t, float p, float tHot, float tCold, float dry, BiomeType expected)
    {
        Assert.AreEqual(expected, BiomeClassifier.Classify(elev, t, p, tHot, tCold, dry));
    }

    [TestCase(0.2f, 25f, 100f, 32f, 20f, 40f, BiomeType.HotDesert)]        // BWh（P < 5×2(t+7)）
    [TestCase(0.2f, 10f, 100f, 20f, -1f, 40f, BiomeType.ColdDesertKoppen)] // BWk
    [TestCase(0.2f, 25f, 400f, 32f, 20f, 40f, BiomeType.HotSteppe)]        // BSh（5×≤ P < 10×）
    [TestCase(0.2f, 10f, 220f, 20f, -1f, 40f, BiomeType.ColdSteppe)]       // BSk
    public void Classify_DesertSteppeBelt(float elev, float t, float p, float tHot, float tCold, float dry, BiomeType expected)
    {
        Assert.AreEqual(expected, BiomeClassifier.Classify(elev, t, p, tHot, tCold, dry));
    }

    [TestCase(0.2f, 26f, 1500f, 30f, 24f, 80f, BiomeType.TropicalRainforest)]  // Af（最干月 ≥ 60）
    [TestCase(0.2f, 26f, 1200f, 30f, 24f, 30f, BiomeType.TropicalMonsoon)]     // Am（P ≥ 1000−dry/2.5）
    [TestCase(0.2f, 26f, 800f, 30f, 24f, 30f, BiomeType.TropicalSavanna)]      // Aw（其余）
    public void Classify_TropicalBand(float elev, float t, float p, float tHot, float tCold, float dry, BiomeType expected)
    {
        Assert.AreEqual(expected, BiomeClassifier.Classify(elev, t, p, tHot, tCold, dry));
    }

    [TestCase(0.2f, -8f, 200f, 5f, -15f, 30f, BiomeType.Tundra)]          // ET（最热月 0~10）
    [TestCase(0.2f, -15f, 100f, -5f, -25f, 30f, BiomeType.IceCap)]        // EF（最热月 < 0）
    [TestCase(0.2f, 8f, 500f, 25f, -5f, 50f, BiomeType.ContinentalHot)]   // Dfa（润+热夏）
    [TestCase(0.2f, 8f, 500f, 15f, -5f, 50f, BiomeType.ContinentalWarm)]  // Dfb（润+暖夏）
    [TestCase(0.2f, 5f, 400f, 15f, -18f, 50f, BiomeType.Subarctic)]       // Dfc（冬极寒）
    public void Classify_PolarAndContinental(float elev, float t, float p, float tHot, float tCold, float dry, BiomeType expected)
    {
        Assert.AreEqual(expected, BiomeClassifier.Classify(elev, t, p, tHot, tCold, dry));
    }

    [Test]
    public void Classify_ContinentalDry_RequiresWinterDrySeason()
    {
        // Dwa：最冷月 ≤ -3 + 冬干（北半球 1 月）+ 降水足够（10×pThr=2×(8+14)×10=440）
        Assert.AreEqual(BiomeType.ContinentalDry,
            BiomeClassifier.Classify(0.2f, 8f, 500f, 25f, -5f, 10f, dryMonthIndex: 1, latDeg: 0f));
        // 同样的干季放在夏季（指数 7，北半球非冬干）→ 不应判 Dwa
        Assert.AreEqual(BiomeType.ContinentalWarm,
            BiomeClassifier.Classify(0.2f, 8f, 500f, 15f, -5f, 10f, dryMonthIndex: 7, latDeg: 0f));
    }

    [TestCase(0.2f, 15f, 800f, 25f, -1f, 10f, 1, 0f, BiomeType.MonsoonSubtropical)]   // Cwa（北半球冬干）
    [TestCase(0.2f, 15f, 800f, 25f, -1f, 10f, 6, -30f, BiomeType.MonsoonSubtropical)] // Cwa（南半球冬干 6-7 月）
    [TestCase(0.2f, 15f, 500f, 25f, -1f, 10f, 7, 0f, BiomeType.MediterraneanHot)]     // Csa（夏干）
    [TestCase(0.2f, 15f, 500f, 18f, -1f, 10f, 7, 0f, BiomeType.MediterraneanCool)]    // Csb
    [TestCase(0.2f, 15f, 800f, 25f, -1f, 50f, 3, 0f, BiomeType.HumidSubtropical)]     // Cfa（全年湿+热夏）
    [TestCase(0.2f, 15f, 800f, 18f, -1f, 50f, 3, 0f, BiomeType.Oceanic)]              // Cfb（全年湿+凉夏）
    public void Classify_TemperateBand(float elev, float t, float p, float tHot, float tCold, float dry, int dryIdx, float lat, BiomeType expected)
    {
        Assert.AreEqual(expected, BiomeClassifier.Classify(elev, t, p, tHot, tCold, dry, dryIdx, lat));
    }

    [Test]
    public void Classify_ExactBoundaryElevations()
    {
        // e == -0.1 不算深海（< 才算）→ 海洋分支；e == 0 不算海洋（< 才算）→ 陆地方支
        Assert.AreEqual(BiomeType.Ocean, BiomeClassifier.Classify(-0.1f, 10f, 900f, 20f, 5f, 70f));
        Assert.AreEqual(BiomeType.Oceanic, BiomeClassifier.Classify(0f, 15f, 800f, 18f, -1f, 50f));
        // e == 0.5 不算高山（> 才算）
        Assert.AreEqual(BiomeType.Oceanic, BiomeClassifier.Classify(0.5f, 15f, 800f, 18f, -1f, 50f));
    }

    [Test]
    public void Classify_ThresholdBoundaryIsSteppeVsDesert()
    {
        // P 恰等于 5×pThr 阈值：不属于沙漠（< 才算）→ 草原
        // tempC=25 → pThr=2×(25+7)=64 → 5×=320
        Assert.AreEqual(BiomeType.HotSteppe, BiomeClassifier.Classify(0.2f, 25f, 320f, 32f, 20f, 40f));
        // P 恰等于 10×pThr=640：不属于草原（< 才算）→ 热带带继续（tCold=24 → A 带）
        Assert.AreEqual(BiomeType.TropicalSavanna, BiomeClassifier.Classify(0.2f, 25f, 640f, 32f, 24f, 30f));
    }

    // ═══════════════════════════════════════════════════════════════
    // BiomeType —— 枚举序列化范围（byte 直接写存档 0-31）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void BiomeType_KeyValuesStable()
    {
        Assert.AreEqual(0, (byte)BiomeType.DeepOcean);
        Assert.AreEqual(1, (byte)BiomeType.Ocean);
        Assert.AreEqual(2, (byte)BiomeType.IceCap);
        Assert.AreEqual(3, (byte)BiomeType.Tundra);
        Assert.AreEqual(12, (byte)BiomeType.Alpine);
        Assert.AreEqual(13, (byte)BiomeType.Riparian);
        Assert.AreEqual(14, (byte)BiomeType.TropicalRainforest);
        Assert.AreEqual(31, (byte)BiomeType.TropicalOcean);
    }

    // ═══════════════════════════════════════════════════════════════
    // BiomeColors —— 色板（纯托管 Color）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void BiomeToColor_AllEnumsMapped_NoMagentaFallback()
    {
        foreach (var b in System.Enum.GetValues<BiomeType>())
        {
            var c = BiomeColors.BiomeToColor(b);
            Assert.AreEqual(1f, c.A, 1e-6f, $"枚举 {b} 应有不透明专色");
            Assert.False(c.IsEqualApprox(Colors.Magenta), $"枚举 {b} 不应落入默认 Magenta 兜底色");
        }
    }

    [Test]
    public void TemperatureToColor_ClampsAndInterpolates()
    {
        // 低于最低断点 → 极寒色；高于最高断点 → 高温色
        Assert.IsTrue(BiomeColors.TemperatureToColor(-100f).IsEqualApprox(new Color(0.08f, 0.12f, 0.45f)));
        Assert.IsTrue(BiomeColors.TemperatureToColor(100f).IsEqualApprox(new Color(0.88f, 0.30f, 0.15f)));
        // 断点处连续（属于上一段终点 = 下一段起点）
        Assert.IsTrue(BiomeColors.TemperatureToColor(-30f).IsEqualApprox(new Color(0.10f, 0.28f, 0.62f)));
        Assert.IsTrue(BiomeColors.TemperatureToColor(0f).IsEqualApprox(new Color(0.22f, 0.52f, 0.72f)));
        Assert.IsTrue(BiomeColors.TemperatureToColor(30f).IsEqualApprox(new Color(0.92f, 0.78f, 0.28f)));
        // 段内插值：0°C 与 15°C 中点 ≈ 绿与黄的 50/50
        var mid = BiomeColors.TemperatureToColor(7.5f);
        Assert.Greater(mid.G, mid.R, "7.5°C 应偏绿（中间色）");
    }

    [Test]
    public void PrecipitationToColor_Monotonic()
    {
        var dry = BiomeColors.PrecipitationToColor(0f);
        var wet = BiomeColors.PrecipitationToColor(3000f);
        Assert.Greater(dry.R, wet.R, "降水越多 R 通道越低（黄→蓝）");
        Assert.Less(dry.B, wet.B, "降水越多 B 通道越高");
        // 端点值精确
        Assert.IsTrue(dry.IsEqualApprox(new Color(0.90f, 0.80f, 0.40f)));
        Assert.IsTrue(wet.IsEqualApprox(new Color(0.10f, 0.30f, 0.70f)));
    }

    // ═══════════════════════════════════════════════════════════════
    // WindField —— 三圈环流（纯托管 Vector3）
    // ═══════════════════════════════════════════════════════════════

    [TestCase(0f, WindField.Belt.Hadley)]
    [TestCase(29.9f, WindField.Belt.Hadley)]
    [TestCase(30f, WindField.Belt.Ferrel)]
    [TestCase(59.9f, WindField.Belt.Ferrel)]
    [TestCase(60f, WindField.Belt.Polar)]
    [TestCase(89f, WindField.Belt.Polar)]
    public void BeltAt_Thresholds(float lat, WindField.Belt expected)
    {
        Assert.AreEqual(expected, WindField.BeltAt(lat));
    }

    [Test]
    public void WindAt_TangentAndUnitLength()
    {
        foreach (var dir in new[] { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
                                    new Vector3(0.3f, 0.8f, 0.5f).Normalized(),
                                    new Vector3(-0.7f, -0.4f, 0.6f).Normalized() })
        {
            Vector3 w = WindField.WindAt(dir);
            // 切于球面（与径向量正交）且为单位长（或极点退化零向量）
            Assert.Less(Mathf.Abs(w.Dot(dir)), 1e-4f, $"风向应切于球面（pos={dir}）");
            if (w.LengthSquared() > 1e-9f)
                Assert.AreEqual(1f, w.Length(), 1e-4f, "风向应为单位向量");
        }
    }

    [Test]
    public void WindAt_PoleDoesNotCrash()
    {
        Vector3 north = WindField.WindAt(new Vector3(0f, 1f, 0f));
        Vector3 south = WindField.WindAt(new Vector3(0f, -1f, 0f));
        // 极点无经线方向 → 退化（零向量），不得 NaN/崩溃
        Assert.True(north.IsFinite() && south.IsFinite());
    }

    [Test]
    public void WindAt_ProgradeFlipsZonalComponent()
    {
        var pos = new Vector3(1f, 0.5f, 0f).Normalized();
        bool prev = WindField.Prograde;
        try
        {
            WindField.Prograde = true;
            float z1 = WindField.WindAt(pos).Z;
            WindField.Prograde = false;
            float z2 = WindField.WindAt(pos).Z;
            Assert.AreNotEqual(Mathf.Sign(z1), Mathf.Sign(z2), "自转方向反转应翻转纬向分量符号");
        }
        finally
        {
            WindField.Prograde = prev;   // 静态状态必须还原（同进程测试隔离）
        }
    }

    [Test]
    public void MaritimeScore_AllOceanIsPlusOne_AllLandIsMinusOne()
    {
        var pos = new Vector3(1f, 0f, 0f);
        float allOcean = WindField.MaritimeScore(pos, -0.5f, _ => -1f);
        float allLand = WindField.MaritimeScore(pos, 0.5f, _ => 0.5f);
        Assert.AreEqual(1f, allOcean, 1e-4f, "全海采样应得 +1");
        Assert.AreEqual(-1f, allLand, 1e-4f, "全陆采样应得 -1");
        // 混合海陆 → 严格介于 (-1, 1)
        float mixed = WindField.MaritimeScore(pos, -0.5f, i => i.X > 0f ? -1f : 0.5f);
        Assert.That(mixed, Is.InRange(-1f, 1f));
    }

    // ═══════════════════════════════════════════════════════════════
    // OceanCurrent —— 流函数法洋流（纯计算；小网格收敛用例，避免 GD.PushWarning）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void OceanCurrent_Compute_AllOceanRing_Invariants()
    {
        // 赤道平面 8 顶点环（全部海洋）：SOR 小网格必收敛（iterations=2000，maxErr<1e-5）
        int n = 8;
        var verts = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.Tau / n;
            verts[i] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
        }
        var neighbors = new int[n][];
        for (int i = 0; i < n; i++)
            neighbors[i] = new[] { (i + 1) % n, (i + n - 1) % n };
        var elevNorm = new float[n];
        for (int i = 0; i < n; i++) elevNorm[i] = -0.5f;   // 全海洋

        OceanCurrent.Compute(verts, neighbors, elevNorm,
            out var dirs, out var warmth, out var strength, out var psi,
            betaScale: 0f, iterations: 2000);

        for (int i = 0; i < n; i++)
        {
            Assert.True(dirs[i].IsFinite(), $"dirs[{i}] 应有限");
            Assert.True(float.IsFinite(psi[i]) && float.IsFinite(warmth[i]) && float.IsFinite(strength[i]),
                $"场[{i}] 应有限（psi/warmth/strength）");
            float dl = dirs[i].LengthSquared();
            if (dl > 1e-12f)
            {
                Assert.AreEqual(1f, Mathf.Sqrt(dl), 1e-4f, $"dirs[{i}] 应为单位向量");
                Assert.That(strength[i], Is.InRange(0.3f, 1.0f), "有向=有强度（环流权重范围）");
            }
            else
            {
                Assert.AreEqual(0f, strength[i], 1e-6f, "无向 = 强度 0");
            }
            Assert.That(warmth[i], Is.InRange(-1f, 1f), "冷暖应在 [-1,1]");
            Assert.That(dirs[i].Dot(verts[i]), Is.LessThan(1e-3f), "洋流应切于球面");
        }
    }

    [Test]
    public void OceanCurrent_Compute_LandCellsZero()
    {
        var verts = new[] { new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f) };
        var neighbors = new[] { new[] { 1 }, new[] { 0 } };
        var elevNorm = new[] { 0.3f, -0.5f };   // 0 陆地 1 海洋

        OceanCurrent.Compute(verts, neighbors, elevNorm,
            out var dirs, out var warmth, out var strength, out var psi,
            betaScale: 0f, iterations: 100);

        Assert.AreEqual(Vector3.Zero, dirs[0], "陆地格无洋流方向");
        Assert.AreEqual(0f, strength[0], 1e-6f, "陆地格强度 0");
        Assert.AreEqual(0f, psi[0], 1e-6f, "陆地格 ψ 恒 0（边界条件）");
    }
}