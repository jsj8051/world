using NUnit.Framework;
using Godot;
using World.Domain;
using World.WorldGen;
using static World.Utils.ColorRamp;

namespace World.Tests;

/// <summary>地图坞契约测试（2026-10-07 地图坞拍板）：
/// ① 降水色带 = **固定物理域** 0–3000 mm + 对数式显示变换（禁止 min-max 自动拉伸——
///    颜色须有稳定世界意义，跨世界可比较）；归一单调、域端点钉死、停点升序；
/// ② 温度地图色带 = **地图专用**固定物理域 −40…+90 °C（2026-10-08 用户拍板"地图另立宽域"）——
///    不复用 Domain.BiomeColors.TempStops（那张域 −85…45 °C 供未来 Biome；本模型 meanT≈+76 °C
///    会整图夹红）；域端 clamp、停点升序、冷蓝热红；
/// ③ BiomeColors.TempStops 自身域端 clamp 仍钉住（Domain 资产，供未来 Biome）。
/// ★色带/派生表归属 = 各模式类自身（2026-10-08 地图模式拆分为独立文件：Precip 表在
///    PrecipitationMode、Temperature 表在 TemperatureMode）——本测试即其单一事实源的护栏。
/// 纯静态标/色表测试：不写文件、不碰 GD.*。</summary>
public class WorldGenMapModeTests
{
    // ── 降水归一（对数式显示变换；仅显示，不改数据）──

    [Test]
    public void PrecipNorm_DomainEnds_AreExact()
    {
        Assert.AreEqual(0f, PrecipitationMode.PrecipNorm(0f), 1e-6f);
        Assert.AreEqual(1f, PrecipitationMode.PrecipNorm(3000f), 1e-6f);
    }

    [Test]
    public void PrecipNorm_IsMonotonic_AndClamps()
    {
        float prev = -1f;
        for (float mm = 0f; mm <= 3000f; mm += 50f)
        {
            float t = PrecipitationMode.PrecipNorm(mm);
            Assert.That(t, Is.InRange(0f, 1f), $"mm={mm}");
            Assert.That(t, Is.GreaterThanOrEqualTo(prev), $"mm={mm} 非单调");
            prev = t;
        }
        Assert.AreEqual(PrecipitationMode.PrecipNorm(3000f), PrecipitationMode.PrecipNorm(99999f), 1e-6f);   // 超域夹末
        Assert.AreEqual(0f, PrecipitationMode.PrecipNorm(-100f), 1e-6f);   // 负值夹 0
    }

    [Test]
    public void PrecipStops_AreStrictlyAscending()
    {
        var stops = PrecipitationMode.PrecipStops;
        Assert.That(stops.Length, Is.GreaterThanOrEqualTo(2));
        for (int i = 1; i < stops.Length; i++)
            Assert.That(stops[i].Pos, Is.GreaterThan(stops[i - 1].Pos), $"停点 {i}");
    }

    // ── 温度地图色带（地图专用固定域 −40…+90 °C；不复用 BiomeColors.TempStops）──

    [Test]
    public void TemperatureMapStops_AreStrictlyAscending_AndSpanDomain()
    {
        var stops = TemperatureMode.TemperatureStops;
        Assert.That(stops.Length, Is.GreaterThanOrEqualTo(2));
        for (int i = 1; i < stops.Length; i++)
            Assert.That(stops[i].Pos, Is.GreaterThan(stops[i - 1].Pos), $"停点 {i}");
        // 域端须与声明的固定域一致（跨世界可比的前提：域是常量不是自适应）
        Assert.AreEqual(TemperatureMode.TempDomainMinC, stops[0].Pos, 1e-3f);
        Assert.AreEqual(TemperatureMode.TempDomainMaxC, stops[^1].Pos, 1e-3f);
    }

    [Test]
    public void TemperatureMapRamp_ClampsFixedDomain()
    {
        var stops = TemperatureMode.TemperatureStops;
        // 超域夹端色（不随世界最冷/最热值拉伸）
        Assert.IsTrue(RampSampleSmooth(stops, -500f)
            .IsEqualApprox(RampSampleSmooth(stops, TemperatureMode.TempDomainMinC)));
        Assert.IsTrue(RampSampleSmooth(stops, 500f)
            .IsEqualApprox(RampSampleSmooth(stops, TemperatureMode.TempDomainMaxC)));
    }

    [Test]
    public void TemperatureMapRamp_ColdIsBlue_HotIsRed()
    {
        var stops = TemperatureMode.TemperatureStops;
        var cold = RampSampleSmooth(stops, TemperatureMode.TempDomainMinC);
        var hot = RampSampleSmooth(stops, TemperatureMode.TempDomainMaxC);
        Assert.That(cold.B, Is.GreaterThan(cold.R), "冷端应偏蓝");
        Assert.That(hot.R, Is.GreaterThan(hot.B), "热端应偏红");
    }

    // ── 温度色带（复用 TempStops；固定物理域，端点 clamp 即"永不自动拉伸"）──

    [Test]
    public void TemperatureRamp_ClampsFixedDomain()
    {
        // 低温端：-85 °C 以下同色（不随世界最冷值拉伸）
        Assert.IsTrue(BiomeColors.TemperatureToColor(-200f)
            .IsEqualApprox(BiomeColors.TemperatureToColor(-85f)));
        // 高温端：45 °C 以上同色
        Assert.IsTrue(BiomeColors.TemperatureToColor(100f)
            .IsEqualApprox(BiomeColors.TemperatureToColor(45f)));
    }
}
