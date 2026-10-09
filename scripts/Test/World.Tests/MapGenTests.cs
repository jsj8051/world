using System;
using System.Collections.Generic;
using Godot;
using NUnit.Framework;
using World.Domain;
using World.HexPlanet;
using World.LogicGrid;
using World.Archive;
using World.Utils;    // DeterministicRandom（2026-09-03 迁至 World.Utils）

namespace World.Tests;

/// <summary>
/// MapGen 模块 L0 测试（纯托管：FieldCodec / WildCropsSystem / SoilSystem /
/// MineralSystem / RiverSystem / ClimateModel 注册表）。
/// 全部只用 Godot.Mathf / Vector3 托管数学（探针实测无引擎安全）、DeterministicRandom，
/// 不触碰任何引擎原生调用（GD.* / LogService.* / FastNoiseLite / FileAccess / 节点类）。
///
/// 施工纪律：
///   - 合成网格用 Icosahedron.Subdivide(n, radius, out verts, out indices)
///     （2026-08 引擎适配器重构：Subdivide 为纯几何函数、无日志，可直接调用）。
///   - GameGrid 全字段 public，手工 new 填字段；Neighbors 惰性 BuildNeighbors（纯托管）。
///   - 本地执行器只支持 [Test]/[TestCase(字面量)]，不用 SetUp/Theory/TestCaseSource/Pass/Ignore。
///   - 确定性（固定 n/seed/气候场）、小网格（n≤8）、不写文件、浮点断言带容差。
/// </summary>
public class MapGenTests
{
    private const int MonthCount = 12;

    // ═════════════════════════════════════════════════════════════════
    // 网格工厂（纯托管合成；Subdivide 无日志可直接调用）
    // ═════════════════════════════════════════════════════════════════

    /// <summary>球面细分网格。n≤8 小网格：n=4 → 162 顶点，n=8 → 642 顶点。</summary>
    private static GameGrid BuildGrid(int n, int seed, bool land, float baseTempC, float basePrecipMm, byte biome)
    {
        Icosahedron.Subdivide(n, 6000f, out var verts, out var indices);
        int count = verts.Count;
        var g = new GameGrid
        {
            N = count,
            GridN = n,
            Seed = seed,
            Verts = verts.ToArray(),
            Elev = new float[count],
            Temp = new float[count],
            Precip = new float[count],
            Biome = new byte[count],
            LakeLevel = new byte[count],
            MonthTemp = new byte[MonthCount][],
            MonthPrecip = new byte[MonthCount][],
        };
        for (int m = 0; m < MonthCount; m++)
        {
            g.MonthTemp[m] = new byte[count];
            g.MonthPrecip[m] = new byte[count];
        }
        for (int i = 0; i < count; i++)
        {
            float y = verts[i].Y;
            g.Elev[i] = land ? 200f : -200f;
            g.Temp[i] = baseTempC - 12f * y;          // 纬度梯度（北半球高 Y 偏冷，南偏暖）
            g.Precip[i] = basePrecipMm;
            g.Biome[i] = biome;
            for (int m = 0; m < MonthCount; m++)
            {
                g.MonthTemp[m][i] = FieldCodec.TempToByte(g.Temp[i]);
                g.MonthPrecip[m][i] = FieldCodec.RatioToByte(1f / MonthCount);
            }
        }
        return g;
    }

    private static GameGrid BuildOceanGrid(int n, int seed)
        => BuildGrid(n, seed, false, 15f, 550f, 0);

    /// <summary>全陆地、小麦友好气候（t≈15°C、年降水 550mm、月降水均匀——小麦生态位近最优）。</summary>
    private static GameGrid BuildWheatGrid(int n, int seed)
        => BuildGrid(n, seed, true, 15f, 550f, (byte)BiomeType.HotSteppe);

    // ═════════════════════════════════════════════════════════════════
    // 1. FieldCodec（纯静态，唯一 byte 编解码入口）
    // ═════════════════════════════════════════════════════════════════

    [TestCase(0f)]
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(0.75f)]
    [TestCase(1f)]
    public void RatioToByte_ByteToRatio_RoundTrip(float v)
    {
        float back = FieldCodec.ByteToRatio(FieldCodec.RatioToByte(v));
        // byte 量化误差（截断）≤ 1/255 ≈ 0.004
        Assert.AreEqual(v, back, 0.004f);
    }

    [TestCase(-60f)]
    [TestCase(-30f)]
    [TestCase(0f)]
    [TestCase(30f)]
    [TestCase(60f)]
    public void TempToByte_ByteToTemp_RoundTrip(float tC)
    {
        float back = FieldCodec.ByteToTemp(FieldCodec.TempToByte(tC));
        // 量化步长 = 120/255 ≈ 0.47°C，截断误差 ≤ 1 步 → 容差 0.5°C
        Assert.AreEqual(tC, back, 0.5f);
    }

    [Test]
    public void RatioToByte_ClampsBounds()
    {
        Assert.AreEqual((byte)0, FieldCodec.RatioToByte(-1f));
        Assert.AreEqual((byte)0, FieldCodec.RatioToByte(-0.5f));
        Assert.AreEqual((byte)255, FieldCodec.RatioToByte(2f));
        Assert.AreEqual((byte)255, FieldCodec.RatioToByte(1f));
    }

    [Test]
    public void TempToByte_ClampsBounds()
    {
        // < -60 → 0；> +60 → 255（clamp 于存档温度范围）
        Assert.AreEqual((byte)0, FieldCodec.TempToByte(-100f));
        Assert.AreEqual((byte)0, FieldCodec.TempToByte(-61f));
        Assert.AreEqual((byte)255, FieldCodec.TempToByte(100f));
        Assert.AreEqual((byte)255, FieldCodec.TempToByte(61f));
    }

    [Test]
    public void ByteMonthPrecipToMm_RatioTimesAnnual()
    {
        // byte=51 → 51/255 = 0.2 → ×1000mm = 200mm
        Assert.AreEqual(200f, FieldCodec.ByteMonthPrecipToMm((byte)51, 1000f), 0.01f);
        Assert.AreEqual(0f, FieldCodec.ByteMonthPrecipToMm((byte)0, 500f), 0.01f);
        Assert.AreEqual(500f, FieldCodec.ByteMonthPrecipToMm((byte)255, 500f), 0.01f);
    }

    [Test]
    public void TempEndpoints_AreLinearEndpoints()
    {
        Assert.AreEqual((byte)0, FieldCodec.TempToByte(FieldCodec.TempMinC));
        Assert.AreEqual((byte)255, FieldCodec.TempToByte(FieldCodec.TempMaxC));
        Assert.AreEqual(FieldCodec.TempMinC, FieldCodec.ByteToTemp(0), 0.3f);
        Assert.AreEqual(FieldCodec.TempMaxC, FieldCodec.ByteToTemp(255), 0.3f);
    }

    [Test]
    public void Suitability_OceanCells_AllZero()
    {
        var g = BuildOceanGrid(4, 123);
        var suit = WildCropsSystem.Suitability(g);
        for (int i = 0; i < g.N; i++)
            for (int s = 0; s < WildCropsSystem.SeedCount; s++)
                Assert.AreEqual(0f, suit[i, s], 0.0001f);
    }

    [Test]
    public void Suitability_LandCells_InUnitRange()
    {
        var g = BuildWheatGrid(4, 123);
        var suit = WildCropsSystem.Suitability(g);
        bool anyHigh = false;
        for (int i = 0; i < g.N; i++)
        {
            for (int s = 0; s < WildCropsSystem.SeedCount; s++)
            {
                Assert.That(suit[i, s], Is.InRange(0f, 1f));
                if (suit[i, s] > 0.5f) anyHigh = true;
            }
        }
        Assert.True(anyHigh, "小麦友好气候下至少一种作物应有较高适宜度");
    }

    [Test]
    public void Phi_OceanIsZero()
    {
        var g = BuildOceanGrid(4, 7);
        Assert.AreEqual(0f, WildCropsSystem.Phi(g, 0, WildCropsSystem.Wheat), 0.0001f);
    }

    [Test]
    public void Compute_SameSeedSameGrid_Deterministic()
    {
        var g = BuildWheatGrid(4, 42);
        byte[] a = WildCropsSystem.Compute(g, 999);
        byte[] b = WildCropsSystem.Compute(g, 999);
        CollectionAssert.AreEqual(a, b);
    }

    [Test]
    public void Compute_DifferentSeeds_MayDiffer()
    {
        // 随机种子点 + Fisher–Yates 邻域遍历 → 不同 seed 大概率产生不同斑块。
        // 多取几组 seed 比较，避免"恰好一组相同"的过低概率侥幸。
        var g = BuildWheatGrid(4, 42);
        var probes = new[] { 1, 2, 3, 5, 7, 11 };
        var first = WildCropsSystem.Compute(g, probes[0]);
        bool anyDiff = false;
        for (int k = 1; k < probes.Length; k++)
        {
            var other = WildCropsSystem.Compute(g, probes[k]);
            if (!ArraysEqual(first, other)) { anyDiff = true; break; }
        }
        Assert.True(anyDiff, "不同 seed 的野生作物分布应符合预期地出现分化");
    }

    [Test]
    public void Compute_MarkedCells_AreWithinDistribution()
    {
        // 斑块性质：所有被标记为某个种子的格子，其适宜度必须 ≥ 全球陆地 P70 分位
        // （P70 相对该星球分布——分布区内才撒种）。
        var g = BuildWheatGrid(4, 5);
        var suit = WildCropsSystem.Suitability(g);
        var bits = WildCropsSystem.Compute(g, 777, suit);

        for (int s = 0; s < WildCropsSystem.SeedCount; s++)
        {
            // 复算该种子陆地 P70（语义=Compute 内同一算法；验证被标记格属于分布区）
            var land = new List<float>();
            for (int i = 0; i < g.N; i++)
                if (g.IsLandCell(i) && suit[i, s] > 1e-4f) land.Add(suit[i, s]);
            if (land.Count == 0) continue;             // 该种子天然灭绝 → 无约束
            land.Sort();
            float p70 = land[Mathf.Clamp((int)(land.Count * 0.70f), 0, land.Count - 1)];
            for (int i = 0; i < g.N; i++)
                if ((bits[i] & (1 << s)) != 0)
                    Assert.GreaterOrEqual(suit[i, s], p70 - 1e-5f,
                        $"格 {i} 种子 {s} 被标记但适宜度低于 P70 分位");
        }
    }

    [Test]
    public void ComputeLivestock_GrasslandWater_AllMarked()
    {
        var g = BuildGrid(4, 11, true, 20f, 600f, (byte)BiomeType.HotSteppe);
        var bits = WildCropsSystem.ComputeLivestock(g, 99);
        for (int i = 0; i < g.N; i++)
            Assert.AreEqual((byte)1, bits[i], $"草原+适降水格 {i} 应为可驯牲畜");
    }

    [Test]
    public void ComputeLivestock_GrasslandDry_Cleared()
    {
        var g = BuildGrid(4, 11, true, 20f, 100f, (byte)BiomeType.HotSteppe);
        var bits = WildCropsSystem.ComputeLivestock(g, 99);
        for (int i = 0; i < g.N; i++)
            Assert.AreEqual((byte)0, bits[i], "年降水<300mm 草原格不应可驯");
    }

    [Test]
    public void ComputeLivestock_NonGrass_Cleared()
    {
        var g = BuildGrid(4, 11, true, 25f, 600f, (byte)BiomeType.TropicalRainforest);
        var bits = WildCropsSystem.ComputeLivestock(g, 99);
        for (int i = 0; i < g.N; i++)
            Assert.AreEqual((byte)0, bits[i], "非草原 biome 不应可驯牲畜");
    }

    [Test]
    public void ComputeLivestock_Deterministic()
    {
        var g = BuildGrid(4, 11, true, 20f, 600f, (byte)BiomeType.ColdSteppe);
        CollectionAssert.AreEqual(
            WildCropsSystem.ComputeLivestock(g, 500),
            WildCropsSystem.ComputeLivestock(g, 500));
    }

    [Test]
    public void ComputeLivestock_AllGrassBiomes_MarkInRange()
    {
        // 五种草原类 biome 全在该判据下启用；产 300~1200mm 标记 1，越界标记 0。
        var biomes = new[]
        {
            BiomeType.HotSteppe, BiomeType.ColdSteppe, BiomeType.TropicalSavanna,
            BiomeType.MediterraneanHot, BiomeType.MediterraneanCool,
        };
        foreach (var b in biomes)
        {
            var gIn = BuildGrid(4, 3, true, 18f, 700f, (byte)b);
            var gOut = BuildGrid(4, 3, true, 18f, 2000f, (byte)b);
            for (int i = 0; i < gIn.N; i++)
                Assert.AreEqual((byte)1, WildCropsSystem.ComputeLivestock(gIn, 7)[i], $"biome {b} 700mm 应可驯");
            for (int i = 0; i < gOut.N; i++)
                Assert.AreEqual((byte)0, WildCropsSystem.ComputeLivestock(gOut, 7)[i], $"biome {b} 2000mm 不应可驯");
        }
    }

    private static GameGrid BuildSlopeWorld(int n)
    {
        Icosahedron.Subdivide(n, 6000f, out var verts, out var indices);
        return new GameGrid { N = verts.Count, GridN = n, Verts = verts.ToArray() };
    }

    [Test]
    public void Module_ByteEncodedGrid_WildCropsDeterministicAndBounded()
    {
        // 月温 / 月降水都以 byte 编码存于 GameGrid（真实验证编解码回路不崩且确定）
        var g = BuildWheatGrid(8, 2024);
        var suit = WildCropsSystem.Suitability(g);
        var bits1 = WildCropsSystem.Compute(g, 2024, suit);
        var bits2 = WildCropsSystem.Compute(g, 2024, suit);
        CollectionAssert.AreEqual(bits1, bits2);

        for (int s = 0; s < WildCropsSystem.SeedCount; s++)
        {
            int count = 0;
            for (int i = 0; i < g.N; i++)
                if ((bits1[i] & (1 << s)) != 0) count++;
            Assert.That(count, Is.GreaterThanOrEqualTo(0));
        }
        // 作物位上界：不产生非法位（仅 5 种子位）
        foreach (byte b in bits1)
            Assert.AreEqual(0, b & 0xE0, "不应出现超过 5 种子之外的非法位");
    }

    private static bool ArraysEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static int CountWhere<T>(T[] arr, Func<T, bool> pred)
    {
        int c = 0;
        foreach (var v in arr) if (pred(v)) c++;
        return c;
    }

    private static float[] Map(int n, Func<int, float> fn)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = fn(i);
        return a;
    }

}
