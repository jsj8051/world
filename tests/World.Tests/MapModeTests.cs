using System;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.MapView.Layers;                       // 老树 ElevationLayer（色带照搬一致性对账）
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.NewHexWorld.UI.Modes;
using World.Utils;                                // ColorRamp 停点类型

namespace World.Tests;

/// <summary>
/// 地图模式策略层（MapMode：海拔/板块 + 注册表）投影正确性测试。
/// 断言：海拔色带与老树 ElevationLayer 停点逐点一致（照搬防漂移）且取色 = 通用平滑采样、
/// 板块模式板色均布纯色 + 边界描边带叠加、注册表查重/查询。
/// （海陆模式 2026-09-09 删除：海陆观感由海拔色带 0m 硬台阶承载。）
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService；不依赖场景。
/// </summary>
public class MapModeTests
{
    private const int Res = 3;   // res3（与本仓库其它 new_HexWorld 测试同档；不用 res2）
    private const int Plates = 15;
    private const int Seed = 42;

    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static readonly Lazy<H3Plate> SharedPlate = new(() =>
    {
        var p = new H3Plate(SharedBall.Value);
        p.CreatePlates(Plates, Seed);
        return p;
    });

    static H3Plate Plate => SharedPlate.Value;
    static Crust Crust => Plate.Crust;
    static int PlateCount => Plate.NumPlates;

    // ═══════════════════════════════════════════════════════════════
    // 海拔模式（Id 0 默认）：色带照搬老树逐点一致 + 取色平滑采样
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ElevationStops_MatchLegacyLayer_NoColorDrift()
    {
        var theirs = ElevationLayer.ElevationStops;
        var ours = ElevationMapMode.ElevationStops;
        Assert.AreEqual(theirs.Length, ours.Length, "停点数须一致（照搬整表）");
        for (int i = 0; i < theirs.Length; i++)
        {
            Assert.AreEqual(theirs[i].Pos, ours[i].Pos, $"停点 {i} 位置漂移");
            Assert.AreEqual(theirs[i].C, ours[i].C, $"停点 {i} 颜色漂移");
        }
    }

    [Test]
    public void ElevationMode_RampSamplingConsistent_LandBaseAndRidgeAxisColors()
    {
        // 本测试断言 ① 取色 = 通用平滑采样（全格、模式无关）；② 海平面两侧取色分属冷/暖两类
        // （0 m 色相硬台阶）。⚠️ 03 动态路线下不再有"模板基准格"（+800 / −2500 是老两级模板的值，
        // 现在的海拔由 600 My 演化算出），故改为按**实际分布**校验色带语义。
        var mode = new ElevationMapMode(Crust);
        Assert.AreEqual(0, mode.Id);
        Assert.AreEqual("海拔", mode.Name);
        Assert.IsTrue(mode.ShowPlateBoundaries, "海拔模式应叠加板块边界线（海岸线 = 0m 线双重视觉对照）");

        int landCells = 0, oceanCells = 0;
        for (int i = 0; i < Crust.PlateId.Length; i++)
        {
            float elev = Crust.Elevation[i];
            Color c = mode.CellColorAt(i);
            Color expect = ColorRamp.RampSampleSmooth(ElevationMapMode.ElevationStops, elev);
            Assert.AreEqual(expect, c, $"格 {i} 海拔取色应与平滑采样一致");

            if (elev < 0f) oceanCells++; else landCells++;
        }
        Assert.Greater(landCells, 0, "应有陆格（演化后海拔 > 0）");
        Assert.Greater(oceanCells, 0, "应有海洋格（演化后海拔 < 0）");

        // 0 m 硬台阶（09-09 拍板）：海平面两侧代表海拔的取色必须分属冷/暖两类。
        // 不逐格判通道大小 —— 0~200 m 那段是从"海平面浅蓝"插值过来的过渡色，逐格断言会误伤岸边格。
        Color deepSea = ColorRamp.RampSampleSmooth(ElevationMapMode.ElevationStops, -1000f);
        Color inland = ColorRamp.RampSampleSmooth(ElevationMapMode.ElevationStops, 1000f);
        Assert.Greater(deepSea.B, deepSea.R, "−1000 m 应冷色系（B > R）");
        Assert.Greater(inland.G, inland.B, "+1000 m 应暖色系（G > B）");
        Assert.AreNotEqual(deepSea, inland, "海平面两侧色带取值必须不同（0 m 硬台阶）");
    }

    // ═══════════════════════════════════════════════════════════════
    // 板块模式（Id 1）：板色均布纯色 + 边界描边带叠加（09-09 拍板，弃整格暗化）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void PlateMode_PurePlateColors_WithOutlineOverlay()
    {
        var ball = SharedBall.Value;
        var mode = new PlateMapMode(Crust, PlateCount,
            Enumerable.Range(0, PlateCount).Select(p => Crust.PlateId.Count(x => x == p)).ToArray());
        Assert.AreEqual(1, mode.Id);
        Assert.AreEqual("板块", mode.Name);
        Assert.IsTrue(mode.ShowPlateBoundaries, "板块模式应叠加边界描边带（09-09 拍板，弃板色自带界）");

        for (int i = 0; i < Crust.PlateId.Length; i++)
        {
            Color baseCol = PlateMapMode.PlateColor(Crust.PlateId[i], PlateCount);
            Assert.AreEqual(baseCol, mode.CellColorAt(i),
                $"格 {i} 应为板基准纯色（边界观感由描边带负责，取色不掺暗化）");
        }
        // 板色相均布：相邻板号色相差 ≈ 1/P（只差色相、同 S/L → 色相差即 |Δh|）
        for (int p = 0; p < PlateCount - 1; p++)
        {
            var a = PlateMapMode.PlateColor(p, PlateCount);
            var b = PlateMapMode.PlateColor(p + 1, PlateCount);
            Assert.AreEqual(1f / PlateCount, Mathf.Abs(a.H - b.H), 1e-5f, $"板 {p}/{p + 1} 色相应均布");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 注册表
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Registry_RegisterQuery_ByIdWorks_DuplicateThrows()
    {
        var registry = new MapModeRegistry();
        registry.Register(new ElevationMapMode(Crust));
        registry.Register(new PlateMapMode(Crust, PlateCount,
            Enumerable.Range(0, PlateCount).Select(p => Crust.PlateId.Count(x => x == p)).ToArray()));

        Assert.AreEqual(2, registry.Modes.Count, "模式清单 = 海拔/板块");
        Assert.AreEqual(0, registry.Modes[0].Id);
        Assert.AreEqual(1, registry.Modes[1].Id);
        Assert.AreEqual("海拔", registry.ById(0).Name);
        Assert.AreEqual("板块", registry.ById(1).Name);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new ElevationMapMode(Crust)),
            "重复 Id 注册须当场暴露");
        Assert.Throws<InvalidOperationException>(() => registry.ById(9), "未注册 Id 查询须当场暴露");
    }
}
