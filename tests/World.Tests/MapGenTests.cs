using System;
using System.Collections.Generic;
using Godot;
using NUnit.Framework;
using World.Biome;
using World.HexPlanet;
using World.LogicGrid;
using World.MapGen;
using World.MapGen.Model;

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

    /// <summary>全海洋网格。</summary>
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

    // ═════════════════════════════════════════════════════════════════
    // 2. WildCropsSystem
    // ═════════════════════════════════════════════════════════════════

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

    // ── ComputeLivestock（草原 biome + 年产 300-1200mm）──

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

    // ═════════════════════════════════════════════════════════════════
    // 3. SoilSystem（纯计算，无 Godot 对象依赖）
    // ═════════════════════════════════════════════════════════════════

    [TestCase((byte)BiomeType.Riparian, 5)]
    [TestCase((byte)BiomeType.HumidSubtropical, 4)]
    [TestCase((byte)BiomeType.Oceanic, 4)]
    [TestCase((byte)BiomeType.ContinentalHot, 4)]
    [TestCase((byte)BiomeType.TropicalRainforest, 3)]
    [TestCase((byte)BiomeType.HotSteppe, 3)]
    [TestCase((byte)BiomeType.Subarctic, 2)]
    [TestCase((byte)BiomeType.Tundra, 1)]
    [TestCase((byte)BiomeType.IceCap, 1)]
    [TestCase((byte)BiomeType.HotDesert, 1)]
    [TestCase((byte)BiomeType.DeepOcean, 0)]
    [TestCase((byte)BiomeType.Ocean, 0)]
    public void Soil_BiomeBase_Lookup(byte biome, int expected)
    {
        Assert.AreEqual(expected, SoilSystem.BiomeBase(biome));
    }

    [Test]
    public void ComputeSoil_OceanIsZero_LandInRange()
    {
        int n = BuildGrid(4, 1, true, 15f, 550f, (byte)BiomeType.TropicalRainforest).N;
        var elevNorm = new float[n];
        var biome = new byte[n];
        var precip = new float[n];
        var temp = new float[n];
        for (int i = 0; i < n; i++)
        {
            // 前 10 格海洋（elevNorm<0），其余陆地（elevNorm 0.5）
            elevNorm[i] = i < 10 ? -1f : 0.5f;
            biome[i] = (byte)BiomeType.TropicalRainforest;
            precip[i] = 1800f;
            temp[i] = 26f;
        }
        SoilSystem.ComputeSoil(elevNorm, biome, precip, temp, null, null, out byte[] soil);

        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f)
                Assert.AreEqual((byte)0, soil[i], "海洋格土壤应为 0");
            else
                Assert.That(soil[i], Is.InRange((byte)1, (byte)5), "陆地格肥力应 1-5");
        }
    }

    [Test]
    public void ComputeSoil_Deterministic()
    {
        int n = BuildGrid(4, 2, true, 15f, 550f, (byte)BiomeType.Oceanic).N;
        var elevNorm = new float[n];
        var biome = new byte[n];
        var precip = new float[n];
        var temp = new float[n];
        var flow = new int[n];
        for (int i = 0; i < n; i++)
        {
            elevNorm[i] = 0.4f;
            biome[i] = (byte)BiomeType.HumidSubtropical;
            precip[i] = 1000f + i % 7;
            temp[i] = 20f;
            flow[i] = i;
        }
        SoilSystem.ComputeSoil(elevNorm, biome, precip, temp, null, flow, out byte[] a);
        SoilSystem.ComputeSoil(elevNorm, biome, precip, temp, null, flow, out byte[] b);
        CollectionAssert.AreEqual(a, b);
    }

    // ═════════════════════════════════════════════════════════════════
    // 4. MineralSystem（纯计算，无 Godot 对象依赖；Crust 可空）
    // ═════════════════════════════════════════════════════════════════

    [TestCase((byte)0x00, 0, 0)]        // 无矿
    [TestCase((byte)0x11, 1, 1)]        // 富度1 铁
    [TestCase((byte)0x35, 3, 5)]        // 富度3 煤
    [TestCase((byte)0x28, 2, 8)]        // 富度2 宝石
    [TestCase((byte)0x47, 0, 7)]        // 越界富度码：RichnessOf 用 &0x03 掩码（富度 3 档设计），4 被截为 0
    public void Mineral_ByteEncoding_Decode(byte b, int rich, int type)
    {
        Assert.AreEqual(type, MineralSystem.TypeOf(b));
        Assert.AreEqual(rich, MineralSystem.RichnessOf(b));
    }

    [Test]
    public void ComputeMinerals_OceanIsZero_EncodingValid()
    {
        int n = BuildGrid(4, 5, true, 15f, 800f, (byte)BiomeType.HotSteppe).N;
        var verts = BuildGrid(4, 5, true, 15f, 800f, (byte)BiomeType.HotSteppe).Verts;
        var neighbors = BuildGrid(4, 5, true, 15f, 800f, (byte)BiomeType.HotSteppe).Neighbors;
        var elevNorm = new float[n];
        var precip = new float[n];
        var age = new float[n];
        var hydro = new float[n];
        var sedM = new float[n];
        var metaM = new float[n];
        for (int i = 0; i < n; i++)
        {
            elevNorm[i] = i < 10 ? -1f : 0.3f + 0.5f * (float)(i % 5) / 5f;
            precip[i] = 700f + i % 13;
            age[i] = 0.5f;
            hydro[i] = 0.4f;
            sedM[i] = 0.4f;
            metaM[i] = 0.4f;
        }
        // Crust 唯一构造需 SphereGrid（引擎对象，无引擎进程不可建）→ 传 null（代码 `crust?.` 安全）
        MineralSystem.ComputeMinerals(verts, neighbors, null, elevNorm, precip, age,
            hydro, sedM, metaM, null, 42, out byte[] minerals);

        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f)
            {
                Assert.AreEqual((byte)0, minerals[i], "海洋格不应有矿");
            }
            else
            {
                Assert.That(MineralSystem.TypeOf(minerals[i]), Is.InRange(0, 8));
                Assert.That(MineralSystem.RichnessOf(minerals[i]), Is.InRange(0, 3));
                if (MineralSystem.TypeOf(minerals[i]) != 0)
                    Assert.That(MineralSystem.RichnessOf(minerals[i]), Is.InRange(1, 3), "有矿必有富度");
            }
        }
    }

    [Test]
    public void ComputeMinerals_Deterministic()
    {
        int n = BuildGrid(4, 5, true, 15f, 800f, (byte)BiomeType.HotSteppe).N;
        var grid = BuildGrid(4, 5, true, 15f, 800f, (byte)BiomeType.HotSteppe);
        float Seed(int i) => 0.3f + 0.5f * ((float)(i % 7)) / 7f;

        MineralSystem.ComputeMinerals(grid.Verts, grid.Neighbors, null, Map(n, Seed),
            Map(n, i => 700f + i % 13), Map(n, i => 0.5f),
            Map(n, i => 0.4f), Map(n, i => 0.4f), Map(n, i => 0.4f),
            null, 42, out byte[] a);
        MineralSystem.ComputeMinerals(grid.Verts, grid.Neighbors, null, Map(n, Seed),
            Map(n, i => 700f + i % 13), Map(n, i => 0.5f),
            Map(n, i => 0.4f), Map(n, i => 0.4f), Map(n, i => 0.4f),
            null, 42, out byte[] b);
        CollectionAssert.AreEqual(a, b);
    }

    // ═════════════════════════════════════════════════════════════════
    // 5. RiverSystem（纯计算）
    // ═════════════════════════════════════════════════════════════════

    /// <summary>构造单调下坡世界：北半球陆地（Y&gt;0）向南（Y 减）流向海洋（Y&lt;0）。</summary>
    private static GameGrid BuildSlopeWorld(int n)
    {
        Icosahedron.Subdivide(n, 6000f, out var verts, out var indices);
        return new GameGrid { N = verts.Count, GridN = n, Verts = verts.ToArray() };
    }

    [Test]
    public void RiverCompute_FlowOceanSelf_LandDownhill()
    {
        var world = BuildSlopeWorld(4);
        int n = world.N;
        var elevNorm = new float[n];
        for (int i = 0; i < n; i++) elevNorm[i] = 0.6f * world.Verts[i].Y;   // Y>0 陆地，Y<0 海洋

        RiverSystem.Compute(world.Verts, world.Neighbors, elevNorm,
            out int[] flow, out float[] area, out byte[] riverLevel,
            out var riverPaths, out var lakeIds, out var lakeLevel,
            areaThreshold: 3f);

        for (int i = 0; i < n; i++)
        {
            Assert.That(flow[i], Is.InRange(0, n - 1), $"flow[{i}] 应始终为合法顶点 id");
            if (elevNorm[i] < 0f)
            {
                Assert.AreEqual(i, flow[i], "海洋格流向自身（终点）");
            }
            else if (flow[i] != i)
            {
                // 陆地非盆地：流向必须是最低邻居且严格更低（河流只从高处流向低处）
                Assert.IsTrue(Array.IndexOf(world.Neighbors[i], flow[i]) >= 0,
                    $"陆地格 {i} 流向 {flow[i]} 不是其邻居");
                Assert.Less(elevNorm[flow[i]], elevNorm[i],
                    $"陆地格 {i} 从低处流向高处，违反单调下坡");
            }
        }
        // ⚠️ 不在球面斜率世界断言"必成河"：n=4 半球水系被海岸线切成大量小出水口，
        //   单一出水口汇水 < 阈值——成河由下方链式流域确定性测试覆盖。
        // 湖泊候选 = 陆地盆地（flow==自身）
        foreach (int lk in lakeIds)
        {
            Assert.That(lk, Is.InRange(0, n - 1));
            Assert.AreEqual(lk, flow[lk], "湖泊候选必须是盆地（无出流）");
        }
    }

    /// <summary>
    /// 确定性链式流域：6 格陆地链 L0→L1→…→L4→O，O 入海（W）。全部水量汇聚到 O → 必成河。
    /// 契约：汇水 ≥ 阈值成河；流向单调下坡；海洋自指；湖泊候选=陆地盆地。
    /// </summary>
    [Test]
    public void RiverCompute_ChainWorld_AccumulatesAndFormsRiver()
    {
        // 手工构造图：链 L0..L4 → O（出口）→ W（海洋）
        var verts = new[] {
            new Vector3(0f, 1f, 0f),     // 0 L0 最高
            new Vector3(0f, 0.8f, 0f),   // 1 L1
            new Vector3(0f, 0.6f, 0f),   // 2 L2
            new Vector3(0f, 0.4f, 0f),   // 3 L3
            new Vector3(0f, 0.2f, 0f),   // 4 L4
            new Vector3(0f, 0.05f, 0f),  // 5 O 出海口（最低陆地）
            new Vector3(0f, -0.3f, 0f),  // 6 W 海洋
        };
        var neighbors = new[] {
            new[] { 1 },          // L0 → L1
            new[] { 0, 2 },       // L1
            new[] { 1, 3 },       // L2
            new[] { 2, 4 },       // L3
            new[] { 3, 5 },       // L4
            new[] { 4, 6 },       // O → L4 / W
            new[] { 5 },          // W
        };
        var elevNorm = new[] { 1.0f, 0.8f, 0.6f, 0.4f, 0.2f, 0.05f, -0.3f };

        RiverSystem.Compute(verts, neighbors, elevNorm,
            out int[] flow, out float[] area, out byte[] riverLevel,
            out var riverPaths, out var lakeIds, out var lakeLevel,
            areaThreshold: 3f);

        // 流向：L0..L4 单调下坡至 O；O 入海；W 自指
        Assert.AreEqual(1, flow[0]);
        Assert.AreEqual(2, flow[1]);
        Assert.AreEqual(3, flow[2]);
        Assert.AreEqual(4, flow[3]);
        Assert.AreEqual(5, flow[4]);
        Assert.AreEqual(6, flow[5], "出海口流向海洋");
        Assert.AreEqual(6, flow[6], "海洋格流向自身");

        // 汇水面积：L0 上游 1 + 自身 = 1；O = 6 全部汇聚；W = 6（O 汇入）
        Assert.AreEqual(1f, area[0], 1e-4f);
        Assert.AreEqual(2f, area[1], 1e-4f);
        Assert.AreEqual(6f, area[5], 1e-4f);

        // 成河：water ≥ 3 的陆地格 L2(3) L3(4) L4(5) O(6)
        Assert.AreEqual((byte)1, riverLevel[2]);
        Assert.AreEqual((byte)1, riverLevel[3]);
        Assert.AreEqual((byte)1, riverLevel[4]);
        Assert.AreEqual((byte)1, riverLevel[5], "汇聚最多的出海口必是河");
        Assert.AreEqual((byte)0, riverLevel[6], "海洋格不标河");
        // 河流路径：源头 L2（无上游河格，water=3 首次超阈）→ 沿流向到出海口
        // ⚠️ 海洋格不标河（riverLevel=0）→ 路径在出海口 O 断流，不含海洋格
        Assert.AreEqual(1, riverPaths.Count, "链式流域应恰好一条主河道");
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, riverPaths[0]);
        // 无陆地盆地 → 无湖泊
        Assert.AreEqual(0, lakeIds.Count);
    }

    [Test]
    public void RiverCompute_Deterministic()
    {
        var world = BuildSlopeWorld(4);
        int n = world.N;
        var elevNorm = new float[n];
        for (int i = 0; i < n; i++) elevNorm[i] = 0.6f * world.Verts[i].Y;

        RiverSystem.Compute(world.Verts, world.Neighbors, elevNorm,
            out int[] flowA, out float[] areaA, out byte[] rlA, out var pA, out var lIdA, out var llA);
        RiverSystem.Compute(world.Verts, world.Neighbors, elevNorm,
            out int[] flowB, out float[] areaB, out byte[] rlB, out var pB, out var lIdB, out var llB);

        CollectionAssert.AreEqual(flowA, flowB);
        CollectionAssert.AreEqual(areaA, areaB);
        CollectionAssert.AreEqual(rlA, rlB);
        CollectionAssert.AreEqual(lIdA, lIdB);
        CollectionAssert.AreEqual(llA, llB);
        Assert.AreEqual(pA.Count, pB.Count);
    }

    [Test]
    public void RiverRebuildPaths_MonotonicToSink()
    {
        var world = BuildSlopeWorld(4);
        int n = world.N;
        var elevNorm = new float[n];
        for (int i = 0; i < n; i++) elevNorm[i] = 0.6f * world.Verts[i].Y;

        RiverSystem.Compute(world.Verts, world.Neighbors, elevNorm,
            out int[] flow, out _, out byte[] riverLevel, out _, out _, out _);
        var paths = RiverSystem.RebuildPaths(flow, riverLevel, elevNorm);

        foreach (int[] path in paths)
        {
            Assert.GreaterOrEqual(path.Length, 3, "河流路径应为源头→入海/盆地（≥3 格）");
            Assert.That(path[0], Is.InRange(0, n - 1));
            for (int k = 0; k < path.Length; k++)
                Assert.That(path[k], Is.InRange(0, n - 1));
            // 沿流向单调下坡（或终止于海洋/盆地），见 Compute 同步逻辑
            for (int k = 0; k + 1 < path.Length; k++)
            {
                if (elevNorm[path[k + 1]] < 0f) break;
                Assert.Less(elevNorm[path[k + 1]], elevNorm[path[k]],
                    $"路径 {k}→{k + 1} 非单调下坡");
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════
    // 6. ClimateModel / Model 注册表（实例构造纯：只存 pipe，不触引擎）
    // ═════════════════════════════════════════════════════════════════

    [Test]
    public void Models_FieldAndLoopCounts()
    {
        var pipe = new PlanetPipeline();
        var models = ClimateModel.Models(pipe);

        int fields = 0, loops = 0, closed = 0, cut = 0, ignored = 0;
        foreach (var m in models)
        {
            if (m is IFieldRole) fields++;
            else if (m is ILoopRole l)
            {
                loops++;
                if (l.Status == "Closed") closed++;
                else if (l.Status == "Cut") cut++;
                else if (l.Status == "Ignored") ignored++;
            }
        }
        Assert.AreEqual(17, fields, "13 Stage1/Stage2 气候场 + 水文/资源/土壤 4 场");
        Assert.AreEqual(8, loops, "8 个反馈环");
        Assert.AreEqual(2, closed);
        Assert.AreEqual(4, cut);
        Assert.AreEqual(2, ignored);
    }

    [Test]
    public void Models_FirstFieldIsElevation()
    {
        var pipe = new PlanetPipeline();
        var models = ClimateModel.Models(pipe);
        Assert.IsInstanceOf<ElevationField>(models[0]);
    }

    [Test]
    public void ModelBase_VerifyTracksFieldPresence()
    {
        // 空 pipe：场未算 → Verify false；注入后 Verify true。
        var empty = new PlanetPipeline();
        Assert.IsFalse(new ElevationField(empty).Verify(), "未注入海拔时不应通过验证");

        var filled = new PlanetPipeline { Elev = new float[1] };
        Assert.IsTrue(new ElevationField(filled).Verify(), "注入海拔后应通过验证");

        // 环：ModelBase 默认 Verify=true（无产出状态需验证）
        Assert.IsTrue(new WetCoolingLoop(new PlanetPipeline()).Verify());
    }

    [Test]
    public void ModelBase_NameMagnitude_Populated()
    {
        var pipe = new PlanetPipeline();
        var models = ClimateModel.Models(pipe);
        foreach (var m in models)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(m.Name));
            Assert.GreaterOrEqual(m.Magnitude, 0f);
            Assert.IsFalse(string.IsNullOrWhiteSpace(m.ToString()));
        }
    }

    [Test]
    public void ModelBase_DependenciesResolvedInsideRegistry()
    {
        var pipe = new PlanetPipeline();
        var models = ClimateModel.Models(pipe);
        var byName = new Dictionary<string, ModelBase>();
        foreach (var m in models) byName[m.Name] = m;

        // 每个模型的依赖名都注册在册（环存在 → TopoSort 不抛）
        foreach (var m in models)
            foreach (var dep in m.DependsOn())
                Assert.IsTrue(byName.ContainsKey(dep), $"{m.Name} 依赖未注册：'{dep}'");
    }

    // ═════════════════════════════════════════════════════════════════
    // 7. 模块测试：FieldCodec 编解码 → WildCrops 的端到端（小网格）
    // ═════════════════════════════════════════════════════════════════

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

    // ═════════════════════════════════════════════════════════════════
    // 工具
    // ═════════════════════════════════════════════════════════════════

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
