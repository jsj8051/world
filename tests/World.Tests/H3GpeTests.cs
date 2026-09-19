using NUnit.Framework;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// GPE 锚点测试（设计-07 P0 验收）：手推的解析柱积分对照——
///   · 洋脊推力：年轻洋柱（age 1 My，Parsons–Sclater 水深 2850 m）GPE 应高于老洋翼
///     （age 80 My，水深 5630 m）约 3e12 N/m（文献洋脊推力量级 1–4e12 N/m）——
///     这是 B 方案"洋脊推力 = 脊-翼 GPE 差"成立的物理锚；
///   · 高原：3 km 抬升 + 50 km 厚壳的补偿高原 GPE 高于老洋底 ~1e13 N/m（高原垮塌/陆内拉张的源）；
///   · 大陆：正常海拔 35 km 陆壳 GPE 高于老洋底（陆内应力以拉张为主的口径）。
/// 关键方向性：岩石圈冷收缩是【密度盈余】（H3ThermalColumn"负浮力在冷岩石圈上"）——
/// 它压低老洋柱 GPE；写反成亏损则洋脊推力反向（曾有此疑，测试钉住）。
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3GpeTests
{
    static readonly MaterialDensity Material = new();
    const float MaficMassKgM2 = 2890f * 7100f;       // 7 km 标准洋壳模板（质量面密度口径）

    static float OceanGpe(float elevationM, float ageMy)
        => H3Gpe.ColumnGpe(Material, elevationM, 0f, 0f, 0f, 0f, MaficMassKgM2, ageMy);

    [Test]
    public void RidgePush_YoungRidge_HigherThanOldFlank_AtTextbookMagnitude()
    {
        float ridge = OceanGpe(-2850f, 1f);           // 脊轴：水深 2.5 km + 350√1 热沉降
        float flank = OceanGpe(-5630f, 80f);          // 翼：2.5 km + 350√80 ≈ 5.63 km

        Assert.Greater(ridge, flank, "GPE 应从脊向翼衰减（洋脊推力方向），反向 = 盈余/亏损写反");
        float push = ridge - flank;
        Assert.Greater(push, 1e12f, $"脊-翼 GPE 差 {push:E2} N/m 低于洋脊推力量级下限");
        Assert.Less(push, 6e12f, $"脊-翼 GPE 差 {push:E2} N/m 超出洋脊推力量级上限");
    }

    [Test]
    public void Plateau_HigherThanOldOcean_ByElevationLever()
    {
        float plateau = H3Gpe.ColumnGpe(Material, 3000f, 0f, 0f, 0f, 2700f * 50000f, 0f, 0f);
        float ocean = OceanGpe(-5630f, 80f);

        Assert.Greater(plateau, ocean, "补偿高原 GPE 应高于老洋底（高原垮塌的驱动源）");
        float diff = plateau - ocean;
        Assert.Greater(diff, 2e12f, $"高原-洋底 GPE 差 {diff:E2} 低于抬升杠杆量级");
        Assert.Less(diff, 3e13f, $"高原-洋底 GPE 差 {diff:E2} 超出抬升杠杆量级");
    }

    [Test]
    public void Continent_HigherThanOldOcean_ContinentalExtensionRegime()
    {
        float land = H3Gpe.ColumnGpe(Material, 500f, 0f, 0f, 0f, 2700f * 35000f, 0f, 0f);
        float ocean = OceanGpe(-5630f, 80f);

        Assert.Greater(land, ocean,
            "正常陆壳 GPE 应高于老洋底（陆内应力以拉张为主的口径；显著为负 = 柱结构有错）");
    }
}
