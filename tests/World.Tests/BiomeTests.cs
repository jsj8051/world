using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using NUnit.Framework;
using World.Biome;
using World.HexPlanet;

using World.CivSim.Entities;
namespace World.Tests;

/// <summary>
/// Biome 模块补充测试（L0 纯逻辑；与 BiomeClimateTests 互补，不重复其覆盖面）。
/// 聚焦四类增量：
///   1. BiomeClassifier 阈值的【恰好等于边界】——按源码 &lt;/&gt;= 严格语义断言（哪些值落在哪边是契约）；
///   2. BiomeType 枚举穷举（byte 基类型 / 0-31 / 无重复）与 BiomeColors 边界连续性；
///   3. WindField 的经纬对称不变量（南北半球镜像、环流带→经向分量符号）与
///      OceanCurrent 在真实细分网格（icosahedron n=2 + 陆地边界条件）上的非平凡环流；
///   4. MonsoonSystem：Compute 依赖 ClimateGenerator（构造即建 FastNoiseLite = 引擎类，
///      测试进程必崩 0xC0000005）→ 不测；其内部唯一纯静态辅助 TraceUpstream（private）
///      用反射驱动（只做 Vector3 点积/比较，纯托管）。
///
/// 纪律：只用 [Test]/[TestCase(字面量)]；不写文件；不触碰 GD.*/LogService/FastNoiseLite；
/// 涉及 WindField 静态状态（Prograde/RotationSpeed）的用例先设定、用后还原。
/// </summary>
public class BiomeTests
{
    // ═══════════════════════════════════════════════════════════════
    // BiomeClassifier.Classify —— 恰好等于阈值的边界语义（>=/&lt; 精确断言）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>精简调用：默认 dryMonthIndex=3、latDeg=0（北半球非干季月）。</summary>
    private static BiomeType Cls(float elev, float t, float p, float hot, float cold, float dry,
        int di = 3, float lat = 0f)
        => BiomeClassifier.Classify(elev, t, p, hot, cold, dry, di, lat);

    [Test]
    public void Classify_SeaIceTemp_BoundaryExactly()
    {
        // 海冰判据：tempC &lt; -2 才算（== 不算）
        Assert.AreEqual(BiomeType.FrigidOcean, Cls(-0.05f, -2.01f, 500f, 0f, -10f, 40f));
        Assert.AreEqual(BiomeType.Ocean, Cls(-0.05f, -2f, 500f, 0f, -10f, 40f));   // 恰好 = SeaIceTempC → 温带海洋
        // 热带海洋判据：tempC &gt;= 18 才算（== 算）
        Assert.AreEqual(BiomeType.TropicalOcean, Cls(-0.05f, 18f, 500f, 30f, 24f, 40f)); // 恰好 = TropicalMinTempC
        Assert.AreEqual(BiomeType.Ocean, Cls(-0.05f, 17.99f, 500f, 30f, 24f, 40f));
    }

    [Test]
    public void Classify_DeepOceanLevel_BoundaryExactly()
    {
        // 深海：elevNorm &lt; -0.1（== 不算，落入海洋分支再按其温度分带）
        Assert.AreEqual(BiomeType.DeepOcean, Cls(-0.1001f, -5f, 100f, 0f, -10f, 40f));
        Assert.AreEqual(BiomeType.FrigidOcean, Cls(-0.1f, -5f, 100f, 0f, -10f, 40f)); // 恰好 = DeepOceanLevel
    }

    [Test]
    public void Classify_AlpineTemp_BoundaryExactly()
    {
        // 高山冰雪：tempC &lt; -8 才算（== 不算）
        Assert.AreEqual(BiomeType.IceCap, Cls(0.6f, -8.01f, 100f, 5f, -10f, 40f));
        Assert.AreEqual(BiomeType.Alpine, Cls(0.6f, -8f, 100f, 5f, -10f, 40f));      // 恰好 = -8 → 高山
        Assert.AreEqual(BiomeType.Alpine, Cls(0.6f, 0f, 100f, 5f, -10f, 40f));
    }

    [Test]
    public void Classify_AfDryMonth_BoundaryExactly()
    {
        // Af：最干月 &gt;= 60mm（== 算）
        Assert.AreEqual(BiomeType.TropicalRainforest, Cls(0.2f, 25f, 1000f, 30f, 20f, 60f));
        // 59.99 → Am 判据（P=1000 足够）→ 热带季风林
        Assert.AreEqual(BiomeType.TropicalMonsoon, Cls(0.2f, 25f, 1000f, 30f, 20f, 59.99f));
    }

    [Test]
    public void Classify_AmPrecip_BoundaryExactly()
    {
        // Am 分界：P &gt;= 1000 − 最干月/2.5（dry=50 → 阈值恰 980）
        Assert.AreEqual(BiomeType.TropicalMonsoon, Cls(0.2f, 25f, 980f, 30f, 20f, 50f));   // 恰好 = 阈值
        Assert.AreEqual(BiomeType.TropicalSavanna, Cls(0.2f, 25f, 979.99f, 30f, 20f, 50f)); // 差 0.01 → Aw
        Assert.AreEqual(BiomeType.TropicalSavanna, Cls(0.2f, 25f, 900f, 30f, 20f, 50f));
    }

    [Test]
    public void Classify_TropicalThreshold_TcoldBoundary()
    {
        // A 带：tCold &gt;= 18（== 算）；17.99 → 温带带
        Assert.AreEqual(BiomeType.TropicalMonsoon, Cls(0.2f, 26f, 1800f, 28f, 18f, 40f));
        Assert.AreEqual(BiomeType.HumidSubtropical, Cls(0.2f, 26f, 1800f, 28f, 17.99f, 40f));
    }

    [Test]
    public void Classify_PolarTemperatures_Boundaries()
    {
        // ET/EF 分界：tHot &gt;= 0（== 算 ET）
        Assert.AreEqual(BiomeType.Tundra, Cls(0.2f, -10f, 260f, 0f, -20f, 40f));
        Assert.AreEqual(BiomeType.IceCap, Cls(0.2f, -10f, 260f, -0.01f, -20f, 40f));
        // E/D 分界：tHot &lt; 10（== 不算 E → 落入 D 亚寒带）
        Assert.AreEqual(BiomeType.Tundra, Cls(0.2f, -10f, 260f, 9.99f, -20f, 40f));
        Assert.AreEqual(BiomeType.Subarctic, Cls(0.2f, -10f, 260f, 10f, -20f, 40f)); // 恰好 = 10 → Dfc
    }

    [Test]
    public void Classify_ContinentalThreshold_TcoldBoundary()
    {
        // D 带：tCold &lt;= -3（== 算）；-2.99 → C 带
        Assert.AreEqual(BiomeType.ContinentalHot, Cls(0.2f, 14f, 700f, 24f, -3f, 50f));
        Assert.AreEqual(BiomeType.HumidSubtropical, Cls(0.2f, 14f, 700f, 24f, -2.99f, 50f));
    }

    [Test]
    public void Classify_CDryMonth30mm_BoundaryExactly()
    {
        // C 带 f 判据：最干月 &gt;= 30mm 算"全年湿"（== 算 Cfa）
        Assert.AreEqual(BiomeType.HumidSubtropical, Cls(0.2f, 16f, 900f, 25f, 5f, 30f, 3, 0f));
        // 29.99 → 存在干季：dIdx=3 非冬月 → 冬雨型（夏干）→ 地中海热夏
        Assert.AreEqual(BiomeType.MediterraneanHot, Cls(0.2f, 16f, 900f, 25f, 5f, 29.99f, 3, 0f));
    }

    [Test]
    public void Classify_BDrySeason30mm_SwitchesPThrFormula()
    {
        // B 带 P_thr 公式切换发生在"无明显干季"边界 30mm：
        //   dry=30 → 均匀型 2×(T+7)；dry=29.99（北半球冬干月）→ 夏雨型 2×(T+14)
        // 同一降水 300mm：均匀型 P=300 ≥ 5×54=270 → 草原；夏雨型 P=300 &lt; 5×68=340 → 沙漠
        Assert.AreEqual(BiomeType.HotSteppe, Cls(0.2f, 20f, 300f, 30f, 15f, 30f, 0, 30f));
        Assert.AreEqual(BiomeType.HotDesert, Cls(0.2f, 20f, 300f, 30f, 15f, 29.99f, 0, 30f));
    }

    [Test]
    public void Classify_SeasonalPThr_ExactFiveTimes()
    {
        // 季节型（冬干→夏雨型）5×pThr 精确边界：2×(20+14)=68 → 5×=340
        Assert.AreEqual(BiomeType.HotSteppe, Cls(0.2f, 20f, 340f, 30f, 15f, 10f, 0, 30f));
        Assert.AreEqual(BiomeType.HotDesert, Cls(0.2f, 20f, 339.99f, 30f, 15f, 10f, 0, 30f));
    }

    [Test]
    public void Classify_BWHvsBWk_Temperature18Boundary()
    {
        // B 带内部冷暖分界：tempC &gt;= 18 → 热沙漠（== 算）
        Assert.AreEqual(BiomeType.HotDesert, Cls(0.2f, 18f, 100f, 30f, 20f, 30f));
        Assert.AreEqual(BiomeType.ColdDesertKoppen, Cls(0.2f, 17.99f, 100f, 30f, 20f, 30f));
    }

    [Test]
    public void Classify_SouthernDrySeason_MirrorsNorthern()
    {
        // 南半球干季判定：dryMonthIndex 5-7 = 冬干（北半球等价 11/0/1）。
        // (a) 冬干（夏雨型）→ pThr=2×(T+14)：南北同月类同果
        Assert.AreEqual(BiomeType.HotDesert, Cls(0.2f, 20f, 300f, 25f, 12f, 10f, 0, 20f));   // 北半球 1 月冬干
        Assert.AreEqual(BiomeType.HotDesert, Cls(0.2f, 20f, 300f, 25f, 12f, 10f, 6, -20f));  // 南半球 7 月冬干
        // (b) 相同的月份指数放到对方半球 = 夏季干（冬雨型）→ pThr=2×T：300 → 半干旱
        Assert.AreEqual(BiomeType.HotSteppe, Cls(0.2f, 20f, 300f, 25f, 12f, 10f, 6, 20f));   // 北半球 7 月夏干
        Assert.AreEqual(BiomeType.HotSteppe, Cls(0.2f, 20f, 300f, 25f, 12f, 10f, 1, -20f));  // 南半球 1 月夏干
        // (c) 温带冬干（Cwa）镜像：北 1 月 == 南 7 月
        Assert.AreEqual(BiomeType.MonsoonSubtropical, Cls(0.2f, 15f, 800f, 25f, -1f, 10f, 0, 35f));
        Assert.AreEqual(BiomeType.MonsoonSubtropical, Cls(0.2f, 15f, 800f, 25f, -1f, 10f, 6, -35f));
        // (d) 温带夏干（Csa）镜像：北 7 月 == 南 1 月
        Assert.AreEqual(BiomeType.MediterraneanHot, Cls(0.2f, 15f, 800f, 25f, -1f, 10f, 6, 35f));
        Assert.AreEqual(BiomeType.MediterraneanHot, Cls(0.2f, 15f, 800f, 25f, -1f, 10f, 1, -35f));
    }

    [Test]
    public void Classify_Precedence_AlpineOverDryOceanOverAll()
    {
        // 垂直带优先于干旱带：高海拔干旱格 → 高山而非沙漠
        Assert.AreEqual(BiomeType.Alpine, Cls(0.6f, 20f, 3f, 28f, 10f, 5f));
        // 海洋分支优先：极寒海水 → 海冰带而非冰盖；高温海水 → 热带海洋而非沙漠
        Assert.AreEqual(BiomeType.FrigidOcean, Cls(-0.05f, -30f, 3f, -5f, -40f, 5f));
        Assert.AreEqual(BiomeType.TropicalOcean, Cls(-0.05f, 45f, 1f, 50f, 40f, 5f));
        // 干旱带优先于 A 带：热带温度 + 极旱 → 沙漠而非稀树草原
        Assert.AreEqual(BiomeType.HotDesert, Cls(0.2f, 26f, 50f, 30f, 24f, 30f));
    }

    // ═══════════════════════════════════════════════════════════════
    // BiomeType —— 枚举穷举（byte 基类型 / 全部值 0-31 / 无重复 / 无化石值）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void BiomeType_UnderlyingByte_AllValuesDistinctInRange()
    {
        // byte 直接写存档（0-31 全部有效）——基类型必须 byte
        Assert.AreEqual(typeof(byte), Enum.GetUnderlyingType(typeof(BiomeType)));

        var seen = new HashSet<byte>();
        var values = Enum.GetValues<BiomeType>();
        foreach (var b in values)
        {
            byte v = (byte)b;
            Assert.LessOrEqual(v, 31, $"值 {v} 超出 0-31 存档范围");
            Assert.True(seen.Add(v), $"枚举值 {v} 重复（序列化歧义）");
        }
        // 定义成员数为 24（0-3、12-13、14-31）；无未定义化石值混入
        Assert.AreEqual(24, values.Length);
    }

    // ═══════════════════════════════════════════════════════════════
    // BiomeColors —— 色板扩展边界（Magenta 兜底 / 断点连续性 / 单调性）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void BiomeToColor_UndefinedValue_FallsBackToMagenta()
    {
        // default 兜底色：任何未定义值（含化石 4-11 与越界值）→ Magenta（存档契约：不得再产生）
        Assert.AreEqual(Colors.Magenta, BiomeColors.BiomeToColor((BiomeType)4));
        Assert.AreEqual(Colors.Magenta, BiomeColors.BiomeToColor((BiomeType)11));
        Assert.AreEqual(Colors.Magenta, BiomeColors.BiomeToColor((BiomeType)99));
    }

    [Test]
    public void BiomeToColor_AllDefinedBiomes_HaveDistinctColors()
    {
        // 每个已知枚举都有专色（!= Magenta），且两两不共享同一色
        var keys = new HashSet<string>();
        foreach (var b in Enum.GetValues<BiomeType>())
        {
            Color c = BiomeColors.BiomeToColor(b);
            Assert.AreNotEqual(Colors.Magenta, c, $"枚举 {b} 不应落入默认 Magenta 兜底色");
            string key = $"{c.R:F4},{c.G:F4},{c.B:F4}";
            Assert.True(keys.Add(key), $"颜色与其它生物群系重复：{b} → {key}");
        }
        Assert.AreEqual(24, keys.Count);
    }

    [Test]
    public void TemperatureToColor_Breakpoints_ExactContinuity()
    {
        // 每个断点恰好落在线性段的 f=1 端点（= 下一段首色）→ 跨断点颜色连续
        AssertColor(BiomeColors.TemperatureToColor(-85f), 0.08f, 0.12f, 0.45f); // 最低断点 = 第一色
        AssertColor(BiomeColors.TemperatureToColor(-30f), 0.10f, 0.28f, 0.62f);
        AssertColor(BiomeColors.TemperatureToColor(0f), 0.22f, 0.52f, 0.72f);
        AssertColor(BiomeColors.TemperatureToColor(15f), 0.38f, 0.72f, 0.42f);
        AssertColor(BiomeColors.TemperatureToColor(30f), 0.92f, 0.78f, 0.28f);
        AssertColor(BiomeColors.TemperatureToColor(45f), 0.88f, 0.30f, 0.15f); // 最高断点 = 最后一色
        // 跨断点连续性：±小量两侧几乎同色（每通道差 &lt; 1e-3）
        foreach (float br in new[] { -30f, 0f, 15f, 30f })
        {
            Color a = BiomeColors.TemperatureToColor(br - 0.01f);
            Color b = BiomeColors.TemperatureToColor(br + 0.01f);
            Assert.AreEqual(a.R, b.R, 1e-3f, $"R 通道在断点 {br} 不连续");
            Assert.AreEqual(a.G, b.G, 1e-3f, $"G 通道在断点 {br} 不连续");
            Assert.AreEqual(a.B, b.B, 1e-3f, $"B 通道在断点 {br} 不连续");
        }
    }

    [Test]
    public void TemperatureToColor_MidSegment_LinearInterpolation()
    {
        // 0~15 段中点 f=0.5：c2=(0.22,0.52,0.72) 与 c3=(0.38,0.72,0.42) 线性混合
        AssertColor(BiomeColors.TemperatureToColor(7.5f), 0.30f, 0.62f, 0.57f);
    }

    [Test]
    public void PrecipitationToColor_EndpointsAndClamp()
    {
        // 端点精确色；越界按 [0,2000] 夹取
        AssertColor(BiomeColors.PrecipitationToColor(0f), 0.90f, 0.80f, 0.40f);
        AssertColor(BiomeColors.PrecipitationToColor(2000f), 0.10f, 0.30f, 0.70f);
        AssertColor(BiomeColors.PrecipitationToColor(-500f), 0.90f, 0.80f, 0.40f);  // 负值夹到 0
        AssertColor(BiomeColors.PrecipitationToColor(99999f), 0.10f, 0.30f, 0.70f); // 超大值夹到 2000
    }

    [Test]
    public void PrecipitationToColor_MonotonicSeries()
    {
        // 黄(干) → 蓝(湿)：R、G 通道单调不增，B 通道单调不减（含夹取区）
        float[] ps = { 0f, 500f, 1000f, 1500f, 2000f, 3000f };
        Color prev = Colors.Transparent;
        for (int i = 0; i < ps.Length; i++)
        {
            Color c = BiomeColors.PrecipitationToColor(ps[i]);
            if (i > 0)
            {
                Assert.True(c.R <= prev.R + 1e-6f, $"R 应在 {ps[i]}mm 不升（{prev.R}→{c.R}）");
                Assert.True(c.G <= prev.G + 1e-6f, $"G 应在 {ps[i]}mm 不升");
                Assert.True(c.B >= prev.B - 1e-6f, $"B 应在 {ps[i]}mm 不降");
            }
            prev = c;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WindField —— 经纬对称不变量与环流带结构（静态状态用后还原）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void BeltAt_MirrorsAcrossEquator()
    {
        // |lat| 决定环流带：南北纬同带
        Assert.AreEqual(WindField.Belt.Hadley, WindField.BeltAt(-5f));
        Assert.AreEqual(WindField.Belt.Ferrel, WindField.BeltAt(-30f)); // 30 恰入 Ferrel（< 才算 Hadley）
        Assert.AreEqual(WindField.Belt.Polar, WindField.BeltAt(-60f));  // 60 恰入 Polar（< 才算 Ferrel）
        Assert.AreEqual(WindField.Belt.Polar, WindField.BeltAt(-90f));
    }

    [Test]
    public void WindAt_SouthernHemisphere_IsEquatorMirror()
    {
        // 对称不变量：南半球风 = 北半球风的赤道镜像（x,z 不变，y 取反）——与源码
        // mer 与 coriolisHemi 双双翻转的推导一致。这是风场的半球对称契约。
        WindField.Prograde = true;
        WindField.RotationSpeed = 1f;
        try
        {
            var p = new Vector3(0.5f, 0.4f, 0.6f).Normalized();   // |lat| ≈ 27°（Hadley 带）
            var ps = new Vector3(p.X, -p.Y, p.Z);
            Vector3 wn = WindField.WindAt(p);
            Vector3 ws = WindField.WindAt(ps);
            Assert.AreEqual(wn.X, ws.X, 1e-4f, "X 分量应镜像对称");
            Assert.AreEqual(-wn.Y, ws.Y, 1e-4f, "Y 分量应在南半球取反");
            Assert.AreEqual(wn.Z, ws.Z, 1e-4f, "Z 分量应镜像对称");
        }
        finally
        {
            WindField.Prograde = true;
            WindField.RotationSpeed = 1f;
        }
    }

    [Test]
    public void WindAt_BeltDeterminesMeridionalSign()
    {
        // 三圈环流结构契约：北半球 Hadley/极地带向赤道（经向分量 &lt; 0），Ferrel 带向极（&gt; 0）。
        // 用源码同款基向量重建（east = (-z,0,x) 归一化，north = dir×east）——不预设物理方向标签，
        // 只断言"环流带 → 经向符号"这一结构不变量。
        WindField.Prograde = true;
        WindField.RotationSpeed = 1f;
        try
        {
            Assert.Less(MeridionalComponent(20f), -0.5f, "Hadley(20N) 应含向赤道分量");
            Assert.Greater(MeridionalComponent(45f), 0.5f, "Ferrel(45N) 应含向极分量");
            Assert.Less(MeridionalComponent(70f), -0.5f, "极地带(70N) 应含向赤道分量");
        }
        finally
        {
            WindField.Prograde = true;
            WindField.RotationSpeed = 1f;
        }
    }

    private static float MeridionalComponent(float latDeg)
    {
        float rad = Mathf.DegToRad(latDeg);
        var p = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f);
        var east = new Vector3(-p.Z, 0f, p.X).Normalized();
        var north = p.Cross(east).Normalized();   // 与源码 WindAt 内 northDir 同构
        return WindField.WindAt(p).Dot(north);
    }

    [Test]
    public void MaritimeScore_MoreOceanSamples_HigherScore()
    {
        // 水汽贡献按上风向采样加权：前 k 个采样为海时，k 越大分数越高，且严格 ∈ (-1,1)
        WindField.Prograde = true;
        WindField.RotationSpeed = 1f;
        try
        {
            var pos = new Vector3(1f, 0f, 0f);
            float four = MaritimeScoreWithFirstK(pos, 4);
            float six = MaritimeScoreWithFirstK(pos, 6);
            Assert.True(four > -1f && four < 1f, $"部分海洋分数应严格 ∈ (-1,1)，实际 {four}");
            Assert.True(six > four, $"更多海洋采样应得分更高（{four} → {six}）");
        }
        finally
        {
            WindField.Prograde = true;
            WindField.RotationSpeed = 1f;
        }
    }

    private static float MaritimeScoreWithFirstK(Vector3 pos, int k)
    {
        int calls = 0;
        return WindField.MaritimeScore(pos, 0f, _ =>
        {
            calls++;
            return calls <= k ? -0.5f : 0.5f;
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // OceanCurrent —— 真实细分网格（icosahedron n=2 + 陆地 BC）上的非平凡环流
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void OceanCurrent_Compute_IcosahedronWithLand_NonTrivialGyre()
    {
        // n=2 二十面体（42 顶点，全部为海洋，3 个陆地格作为 ψ=0 边界）。
        // windField=null → 内部用 WindField.WindAt 解析风 → 非零旋度 → SOR 收敛出环流。
        // iterations=2000 小网格必收敛（finalErr &lt; 1e-4）→ 不触发 GD.PushWarning（触发即崩溃）。
        WindField.Prograde = true;
        WindField.RotationSpeed = 1f;
        try
        {
            Icosahedron.Subdivide(2, 6371f, out var vertList, out var idxList);
            Assert.AreEqual(42, vertList.Count, "n=2 网格应有 42 顶点（10×2²+2）");

            var verts = new Vector3[vertList.Count];
            for (int i = 0; i < verts.Length; i++)
                verts[i] = vertList[i].Normalized();   // 球面单位方向（洋流计算要求 |r|=1）

            var neighbors = BuildNeighborLists(verts.Length, idxList);
            var elev = new float[verts.Length];
            for (int i = 0; i < elev.Length; i++) elev[i] = -0.5f;   // 默认海洋
            elev[0] = 0.5f; elev[7] = 0.5f; elev[23] = 0.5f;         // 3 个陆地格

            OceanCurrent.Compute(verts, neighbors, elev,
                out var dirs, out var warmth, out var strength, out var psi,
                null, null, 1f, 2000);

            int active = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                if (elev[i] >= 0f)
                {
                    // 内陆：方向/强度/冷暖/流函数全零（边界条件 ψ=0）
                    Assert.AreEqual(Vector3.Zero, dirs[i], $"陆地格 {i} 无洋流方向");
                    Assert.AreEqual(0f, strength[i], 1e-6f, $"陆地格 {i} 强度 0");
                    Assert.AreEqual(0f, warmth[i], 1e-6f, $"陆地格 {i} 冷暖 0");
                    Assert.AreEqual(0f, psi[i], 1e-6f, $"陆地格 {i} ψ=0");
                    continue;
                }
                Assert.True(float.IsFinite(psi[i]) && float.IsFinite(warmth[i]) && float.IsFinite(strength[i]),
                    $"海洋格 {i} 场应有限（收敛；若未收敛会触发 GD.PushWarning 崩溃）");
                float dl = dirs[i].LengthSquared();
                if (dl > 1e-12f)
                {
                    active++;
                    Assert.AreEqual(1f, dirs[i].Length(), 1e-4f, $"海洋格 {i} 洋流应为单位向量");
                    Assert.Less(dirs[i].Dot(verts[i]), 1e-3f, $"海洋格 {i} 洋流应切于球面");
                    Assert.GreaterOrEqual(strength[i], 0.3f - 1e-6f, $"海洋格 {i} 强度下限 0.3");
                    Assert.LessOrEqual(strength[i], 1.0f + 1e-6f, $"海洋格 {i} 强度上限 1.0");
                    Assert.GreaterOrEqual(warmth[i], -1f - 1e-6f, $"海洋格 {i} 冷暖下限");
                    Assert.LessOrEqual(warmth[i], 1f + 1e-6f, $"海洋格 {i} 冷暖上限");
                }
                else
                {
                    // |∇ψ|≈0（开阔大洋/环流中心）→ 无向即无强度
                    Assert.AreEqual(0f, strength[i], 1e-6f, $"无向海洋格 {i} 强度应为 0");
                }
            }
            Assert.GreaterOrEqual(active, 1, "网格上应至少存在一处可测洋流（非平凡环流）");
        }
        finally
        {
            WindField.Prograde = true;
            WindField.RotationSpeed = 1f;
        }
    }

    /// <summary>由三角形索引表构建每顶点邻接表（无向图，顺序任意）。</summary>
    private static int[][] BuildNeighborLists(int n, List<int> tris)
    {
        var sets = new HashSet<int>[n];
        for (int i = 0; i < n; i++) sets[i] = new HashSet<int>();
        for (int t = 0; t < tris.Count; t += 3)
        {
            int a = tris[t], b = tris[t + 1], c = tris[t + 2];
            sets[a].Add(b); sets[a].Add(c);
            sets[b].Add(a); sets[b].Add(c);
            sets[c].Add(a); sets[c].Add(b);
        }
        var result = new int[n][];
        for (int i = 0; i < n; i++) result[i] = new List<int>(sets[i]).ToArray();
        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // MonsoonSystem —— Compute 不可测（依赖 ClimateGenerator→FastNoiseLite 引擎类）；
    // 其私有纯静态辅助 TraceUpstream（水汽上游追踪）用反射直测。
    // ═══════════════════════════════════════════════════════════════

    private static readonly MethodInfo TraceUpstream = typeof(MonsoonSystem)
        .GetMethod("TraceUpstream", BindingFlags.NonPublic | BindingFlags.Static);

    [Test]
    public void Monsoon_TraceUpstream_CountsStepsToOcean()
    {
        // 沿大圆小弧布 7 点（p0..p4 陆地、p5..p6 海洋），链式邻接。
        // 上游方向 = p0→p5：每步选与上游点积最大的邻居 → 恰走 5 步遇海。
        var ps = ArcChain(new Vector3(0.9f, 0.4f, 0.1f), new Vector3(0.4f, -0.7f, 0.6f), 6);
        var verts = ps.ToArray();
        var neighbors = ChainNeighbors(verts.Length);
        var elev = new float[verts.Length];
        for (int i = 0; i < 5; i++) elev[i] = 0.2f;      // 陆地
        for (int i = 5; i < verts.Length; i++) elev[i] = -0.2f; // 海洋

        Vector3 up = (verts[5] - verts[0]).Normalized();
        int steps = (int)TraceUpstream.Invoke(null, new object[] { 0, verts, neighbors, elev, -up });
        Assert.AreEqual(5, steps, "应恰在 5 步后到达海洋");
    }

    [Test]
    public void Monsoon_TraceUpstream_AdjacentOcean_IsOneStep()
    {
        var verts = new[] { new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
        var neighbors = new[] { new[] { 1 }, new[] { 0 } };
        var elev = new[] { 0.2f, -0.2f };   // 0 陆地 1 海洋
        Vector3 up = (verts[1] - verts[0]).Normalized();
        int steps = (int)TraceUpstream.Invoke(null, new object[] { 0, verts, neighbors, elev, -up });
        Assert.AreEqual(1, steps, "邻接海洋应为 1 步");
    }

    [Test]
    public void Monsoon_TraceUpstream_NoOceanIn25Steps_ReturnsMinusOne()
    {
        // 30 点全陆地链：25 步追踪内无海洋 → -1（非季风区）
        var ps = ArcChain(new Vector3(0.5f, 0.8f, 0.2f), new Vector3(0.7f, -0.5f, 0.6f), 29);
        var verts = ps.ToArray();
        var neighbors = ChainNeighbors(verts.Length);
        var elev = new float[verts.Length];
        for (int i = 0; i < elev.Length; i++) elev[i] = 0.2f;

        Vector3 up = (verts[1] - verts[0]).Normalized();
        int steps = (int)TraceUpstream.Invoke(null, new object[] { 0, verts, neighbors, elev, -up });
        Assert.AreEqual(-1, steps, "25 步内无海洋应返回 -1");
    }

    [Test]
    public void Monsoon_Constants_Contract()
    {
        Assert.AreEqual(12, MonsoonSystem.MonthCount);
        Assert.AreEqual(0.006f, MonsoonSystem.ElevLapseRatePerM, 1e-6f, "标准大气直减率 6°C/km");
    }

    /// <summary>大圆小弧链：q_k = normalize(a + (b−a)·k/m)，k=0..m（共 m+1 点）。</summary>
    private static List<Vector3> ArcChain(Vector3 a, Vector3 b, int m)
    {
        var list = new List<Vector3>(m + 1);
        for (int k = 0; k <= m; k++)
            list.Add((a + (b - a) * (k / (float)m)).Normalized());
        return list;
    }

    /// <summary>链式邻接：每点只连前后邻居。</summary>
    private static int[][] ChainNeighbors(int n)
    {
        var result = new int[n][];
        for (int i = 0; i < n; i++)
        {
            if (i == 0) result[i] = new[] { 1 };
            else if (i == n - 1) result[i] = new[] { n - 2 };
            else result[i] = new[] { i - 1, i + 1 };
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // 模块一致性：纬度剖面（BiomeClassifier 生物群系 ↔ WindField 环流带）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Module_LatitudeProfile_BiomeAndWindBeltCohere()
    {
        // 沿纬度的合成剖面：生物群系带与盛行风带必须按纬度单调对齐——
        // 赤道热带海洋/低纬雨林（Hadley）、中纬温带（Ferrel）、高纬苔原/海冰（Polar）。
        // 赤道（Hadley）：热带海洋
        Assert.AreEqual(BiomeType.TropicalOcean, Cls(-0.05f, 27f, 200f, 29f, 25f, 80f, 3, 0f));
        Assert.AreEqual(WindField.Belt.Hadley, WindField.BeltAt(0f));
        // 低纬陆地（Hadley）：A 带季风雨林
        Assert.AreEqual(BiomeType.TropicalMonsoon, Cls(0.2f, 26f, 1900f, 30f, 22f, 40f, 3, 8f));
        Assert.AreEqual(WindField.Belt.Hadley, WindField.BeltAt(8f));
        // 中纬（Ferrel）：湿润亚热带（Cfa）
        Assert.AreEqual(BiomeType.HumidSubtropical, Cls(0.2f, 16f, 900f, 25f, 5f, 60f, 3, 40f));
        Assert.AreEqual(WindField.Belt.Ferrel, WindField.BeltAt(40f));
        // 中纬海洋（Ferrel）：温带海洋
        Assert.AreEqual(BiomeType.Ocean, Cls(-0.05f, 12f, 500f, 18f, 8f, 70f, 3, -45f));
        Assert.AreEqual(WindField.Belt.Ferrel, WindField.BeltAt(-45f));
        // 高纬陆地（Polar）：苔原（tHot=5 &lt; 10）
        Assert.AreEqual(BiomeType.Tundra, Cls(0.2f, -8f, 260f, 5f, -15f, 40f, 3, 70f));
        Assert.AreEqual(WindField.Belt.Polar, WindField.BeltAt(70f));
        // 高纬海洋（Polar）：海冰带
        Assert.AreEqual(BiomeType.FrigidOcean, Cls(-0.05f, -6f, 100f, 2f, -12f, 40f, 3, 85f));
        Assert.AreEqual(WindField.Belt.Polar, WindField.BeltAt(85f));
    }

    // ─────────── 小工具 ───────────

    private static void AssertColor(Color c, float r, float g, float b, float tol = 5e-3f)
    {
        Assert.AreEqual(r, c.R, tol, "R 通道");
        Assert.AreEqual(g, c.G, tol, "G 通道");
        Assert.AreEqual(b, c.B, tol, "B 通道");
    }
}