using NUnit.Framework;
using World.Biome;
using World.CivSim;

namespace World.Tests;

/// <summary>
/// 模型层纯函数测试（L0）。全部为静态纯函数，只依赖 BCL + Godot.Mathf（托管实现），
/// 可在 dotnet test 下直接运行，不触碰 Godot 原生调用。
/// 公式出处：docs/石器时代设计.md 与 scripts/CivSim/CivSimContext.cs 注释。
/// </summary>
public class CivSimModelTests
{
    // ── Miami NPP（Lieth 1975，干旱/寒冷最小因子律）──

    [TestCase(-100f, 0f)]
    [TestCase(30f, 0f)]
    [TestCase(25f, 0f)]
    public void MiamiNpp_NoRain_IsZero(float tempC, float precipMm)
    {
        Assert.AreEqual(0f, CivSimContext.MiamiNpp(tempC, precipMm), 0.0001f);
    }

    [Test]
    public void MiamiNpp_GrowsWithTempAndRain()
    {
        Assert.True(CivSimContext.MiamiNpp(25f, 1500f) > CivSimContext.MiamiNpp(5f, 1500f));
        Assert.True(CivSimContext.MiamiNpp(20f, 2000f) > CivSimContext.MiamiNpp(20f, 200f));
    }

    [Test]
    public void MiamiNpp_LiebigMinRule()
    {
        // 干旱极值：温度最优但降水趋零 → NPP 趋近 0
        Assert.True(CivSimContext.MiamiNpp(30f, 1f) < 50f);
        // 严寒极值：降水充足但温度限制 → NPP 很小
        Assert.True(CivSimContext.MiamiNpp(-50f, 3000f) < 50f);
        // 饱和上限：最优条件不超过 3000
        Assert.True(CivSimContext.MiamiNpp(30f, 3000f) <= 3000f);
    }

    // ── 冲积土因子 ──

    [TestCase((byte)0, 1f)]
    [TestCase((byte)3, 1f)]
    [TestCase((byte)4, 2f)]
    [TestCase((byte)5, 3f)]
    public void AlluvFactor_BySoil(byte soil, float expected)
    {
        Assert.AreEqual(expected, CivSimContext.AlluvFactor(soil), 0.0001f);
    }

    // ── 人均收益（选择判据）──

    [Test]
    public void EHunt_ScalesWithYield()
    {
        Assert.AreEqual(0f, CivSimContext.EHunt(0f, 5f));
        Assert.True(CivSimContext.EHunt(20f, 5f) > CivSimContext.EHunt(10f, 5f));
        Assert.True(CivSimContext.EHunt(10f, 5f) > 0f);
    }

    [Test]
    public void EFarm_DeductsWorkCost()
    {
        Assert.AreEqual(0f, CivSimContext.EFarm(0f, 5f));
        // 10 产出 / 2 人 − W(0.2) = 4.8
        Assert.AreEqual(4.8f, CivSimContext.EFarm(10f, 2f), 0.0001f);
        Assert.True(CivSimContext.EFarm(2f, 2f) < CivSimContext.EFarm(20f, 2f));
    }

    // ── 影响力/产出距离权重查表 ──

    [TestCase(0f, 1f)]
    [TestCase(1f, 0.544f)]
    [TestCase(2f, 0.192f)]
    [TestCase(3f, 0f)]
    [TestCase(4f, 0f)]
    [TestCase(-1f, 0f)]
    public void InfluenceWeight_Lut(float d, float expected)
    {
        Assert.AreEqual(expected, CivSimContext.InfluenceWeight(d), 0.0001f);
    }

    [TestCase(0f, 1f)]
    [TestCase(1f, 2.4f)]
    [TestCase(2f, 3f)]
    [TestCase(3f, 2.8f)]
    [TestCase(4f, 1.8f)]
    [TestCase(5f, 0f)]
    [TestCase(6f, 0f)]
    public void ProductionWeight_Lut(float d, float expected)
    {
        Assert.AreEqual(expected, CivSimContext.ProductionWeight(d), 0.0001f);
    }

    // ── 生物群系分类 ──

    [TestCase(BiomeType.HotSteppe, 0.7f)]
    [TestCase(BiomeType.ColdSteppe, 0.7f)]
    [TestCase(BiomeType.TropicalSavanna, 0.7f)]
    [TestCase(BiomeType.TropicalRainforest, 0.35f)]
    [TestCase(BiomeType.HumidSubtropical, 0.35f)]
    [TestCase(BiomeType.Riparian, 0.5f)]
    [TestCase(BiomeType.MediterraneanHot, 0.5f)]
    public void PreyFrac_ByBiome(BiomeType b, float expected)
    {
        Assert.AreEqual(expected, CivSimContext.PreyFrac(b), 0.0001f);
    }

    [TestCase(BiomeType.IceCap, true)]
    [TestCase(BiomeType.Tundra, true)]
    [TestCase(BiomeType.Subarctic, true)]
    [TestCase(BiomeType.Alpine, true)]
    [TestCase(BiomeType.HotDesert, false)]
    [TestCase(BiomeType.Riparian, false)]
    [TestCase(BiomeType.TropicalRainforest, false)]
    public void IsColdZone_ByBiome(BiomeType b, bool expected)
    {
        Assert.AreEqual(expected, CivSimContext.IsColdZone(b));
    }
}
