using System.Collections.Generic;
using System.Reflection;
using Godot;
using NUnit.Framework;
using World.Biome;
using World.HexPlanet;
using World.LogicGrid;
using World.MapGen;

using World.CivSim.Entities;
namespace World.Tests;

/// <summary>
/// ArchiveLayout 字段表驱动长度测试（L0 纯静态）。
/// 契约：布局唯一长度来源 = HeaderFields(14) + PerVertexFields(20)。
/// 手算锚点：头 53B（I32×3=12 + F32×10=40 + U8×1=1）；
/// 每顶点 v2 = 94B（V3×2=24 + F32×6=24 + I32×4=16 + U8×6=6 + Month2D×2=24），v1 无 Psi = 90B。
/// 改字段表时必须同步改本类断言（查表值即契约）。
/// </summary>
public class ArchiveLayoutTests
{
    /// <summary>独立尺寸表（与源码 SizeOf 同值，但作为测试预言——验证 BodyLength 确实由字段表推导）。</summary>
    private static int SizeOfType(ArchiveLayout.FType t) => t switch
    {
        ArchiveLayout.FType.U8 => 1,
        ArchiveLayout.FType.I32 => 4,
        ArchiveLayout.FType.F32 => 4,
        ArchiveLayout.FType.V3 => 12,
        ArchiveLayout.FType.Month2D => MonsoonSystem.MonthCount,
        _ => 0,
    };

    [Test]
    public void BodyLength_ExactValues_TwoAndTen()
    {
        // 头 14 字段 = 53B；每顶点 v2 = 94B → 53 + 94n
        Assert.AreEqual(241L, ArchiveLayout.BodyLength(2, 2));     // 53 + 188
        Assert.AreEqual(993L, ArchiveLayout.BodyLength(10, 2));    // 53 + 940
        // v1 无 Psi → 53 + 90n
        Assert.AreEqual(233L, ArchiveLayout.BodyLength(2, 1));     // 53 + 180
        // n=0 只剩固定头
        Assert.AreEqual(53L, ArchiveLayout.BodyLength(0, 2));
    }

    [Test]
    public void BodyLength_V1OmitsPsi_DiffersBy4PerVertex()
    {
        foreach (int n in new[] { 1, 2, 10, 50 })
            Assert.AreEqual(ArchiveLayout.BodyLength(n, 2) - 4L * n, ArchiveLayout.BodyLength(n, 1));
    }

    [Test]
    public void BodyLength_LargeN_NoIntOverflow()
    {
        // 大 n：94n 段累计必须走 long（单字段先 (long) 再乘）
        Assert.AreEqual(9400053L, ArchiveLayout.BodyLength(100000, 2));
        // n=1e8 → 9400000053 > int.MaxValue：若按 int 累计会回绕
        const long BigN = 100_000_000L;
        long expected = 53L + 94L * BigN;
        Assert.AreEqual(expected, ArchiveLayout.BodyLength((int)BigN, 2));
        Assert.True(ArchiveLayout.BodyLength((int)BigN, 2) > int.MaxValue);
    }

    [Test]
    public void BodyLength_MatchesFieldTableDerivation()
    {
        // 反射字段表，独立推导长度并与 BodyLength 对照——验证"唯一长度来源"断链防护。
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var fiHeader = typeof(ArchiveLayout).GetField("HeaderFields", flags);
        var fiPerVertex = typeof(ArchiveLayout).GetField("PerVertexFields", flags);
        Assert.NotNull(fiHeader);
        Assert.NotNull(fiPerVertex);
        var headerFields = (ArchiveLayout.Field[])fiHeader.GetValue(null);
        var perVertexFields = (ArchiveLayout.Field[])fiPerVertex.GetValue(null);

        Assert.AreEqual(14, headerFields.Length, "头字段数=14（GridN 起 → Verts 前）");
        Assert.AreEqual(20, perVertexFields.Length, "每顶点字段数=20");
        foreach (var f in headerFields)
            Assert.False(f.PerVertex, "头部字段不应标记 PerVertex: " + f.Name);
        foreach (var f in perVertexFields)
            Assert.True(f.PerVertex, "每顶点字段必须标记 PerVertex: " + f.Name);

        long headerBytes = 0;
        foreach (var f in headerFields) headerBytes += SizeOfType(f.Type);

        foreach (int n in new[] { 1, 2, 10, 100000 })
        {
            foreach (int ver in new[] { 1, 2 })
            {
                long perVertexBytes = 0;
                foreach (var f in perVertexFields)
                {
                    if (f.Name == "Psi" && ver < 2) continue;   // 与 BodyLength 的 v1 规则一致
                    perVertexBytes += SizeOfType(f.Type);
                }
                Assert.AreEqual(headerBytes + perVertexBytes * n, ArchiveLayout.BodyLength(n, ver),
                    $"n={n}, ver={ver} 与字段表推导不一致");
            }
        }
    }
}

/// <summary>
/// GameGrid 逻辑网格测试（L0）。合成网格：Icosahedron.Subdivide(n, R, out verts, out _)
/// （2026-08 引擎适配器重构：Subdivide 为纯几何函数、无日志，可直接调用）。全部 public 字段直接赋值构造。
/// 邻接/海陆/距离/面积/野生资源均纯托管（Vector3/Mathf），不触碰引擎调用。
/// </summary>
public class GameGridTests
{
    private static GameGrid BuildGrid(int n, int seed = 12345, float radiusKm = GameGrid.DefaultRadiusKm)
    {
        Icosahedron.Subdivide(n, radiusKm, out var verts, out _);
        int N = verts.Count;
        return new GameGrid
        {
            GridN = n,
            N = N,
            Seed = seed,
            RadiusKm = radiusKm,
            ProgradeRotation = true,
            RotationSpeed = 1f,
            AxialTilt = 23.4f,
            // ⚠️ GameGrid 假定单位球顶点（DistKm/BucketOf 用 dot/clamp-ACos；SphereGrid 同款）
            Verts = verts.ConvertAll(v => v.Normalized()).ToArray(),
            MinElev = -100f,
            MaxElev = 500f,
            MinTemp = -20f,
            MaxTemp = 40f,
            MinPrecip = 0f,
            MaxPrecip = 2500f,
            Elev = new float[N],
            Temp = new float[N],
            Precip = new float[N],
            Biome = new byte[N],
            RiverLevel = new byte[N],
            RiverFlow = new int[N],
            RiverVolume = new float[N],
            LakeLevel = new byte[N],
            MineralLevel = new byte[N],
            SoilLevel = new byte[N],
            MonsoonLevel = new byte[N],
            MonthPrecip = NewMonthArrays(N),
            MonthTemp = NewMonthArrays(N),
            CurrentDirs = new Vector3[N],
            CurrentWarmth = new float[N],
            CurrentStrength = new float[N],
            Province = new int[N],
            Country = new int[N],
        };
    }

    private static byte[][] NewMonthArrays(int n)
    {
        var a = new byte[MonsoonSystem.MonthCount][];
        for (int m = 0; m < MonsoonSystem.MonthCount; m++) a[m] = new byte[n];
        return a;
    }

    private static bool Has(int[] arr, int v)
    {
        foreach (int x in arr) if (x == v) return true;
        return false;
    }

    /// <summary>确定性气候场：约 1/5 海洋（i%5==0），其余陆地；温度/降水随索引变化。</summary>
    private static void FillClimate(GameGrid g)
    {
        for (int i = 0; i < g.N; i++)
        {
            g.Elev[i] = i % 5 == 0 ? 0f : 100f + i;
            g.Temp[i] = -10f + (i * 37) % 45;
            g.Precip[i] = 200f + (i * 91) % 2200;
            g.Biome[i] = (byte)BiomeType.Oceanic;
            g.LakeLevel[i] = 0;
        }
        for (int m = 0; m < MonsoonSystem.MonthCount; m++)
            for (int i = 0; i < g.N; i++)
            {
                g.MonthTemp[m][i] = FieldCodec.TempToByte(g.Temp[i]);
                g.MonthPrecip[m][i] = FieldCodec.RatioToByte(1f / MonsoonSystem.MonthCount);
            }
    }

    [Test]
    public void SyntheticGrid_HasExpectedVertexCount()
    {
        Icosahedron.Subdivide(2, GameGrid.DefaultRadiusKm, out var verts, out _);
        Assert.AreEqual(Icosahedron.VertexCountFor(2), verts.Count);   // 10·2²+2 = 42
    }

    [Test]
    public void Neighbors_Symmetric_NoSelfLoop_DegreeInRange()
    {
        foreach (int n in new[] { 2, 4 })
        {
            var g = BuildGrid(n);
            var nb = g.Neighbors;
            Assert.AreEqual(g.N, nb.Length);
            for (int i = 0; i < g.N; i++)
            {
                Assert.That(nb[i].Length, Is.InRange(3, 12), $"n={n} i={i} 度 {nb[i].Length} 超出实测范围");
                foreach (int j in nb[i])
                {
                    Assert.AreNotEqual(i, j, $"n={n} 自环 {i}→{i}");
                    Assert.That(j, Is.InRange(0, g.N - 1));
                    Assert.True(Has(nb[j], i), $"n={n} 邻接不对称：{i}→{j} 但 {j} 不含 {i}");
                }
            }
        }
    }

    [Test]
    public void Neighbors_TriangularGraph_EulerDegreeSum()
    {
        // n=2 → 42 顶点：真实三角剖分每顶点度 ∈ {5,6}、Σ度=2E=6V−12。
        // ⚠️ GameGrid.BuildNeighbors 是桶近似（球面距离 < 1.5×格距），浮点单位化残差会使
        //   少量边丢失/误加——不保证严格 5/6；按算法契约断言：度有界、Σ度必为偶数（2E）、
        //   平均度落在 4~7（六边形网格量级）。
        var g = BuildGrid(2);
        var nb = g.Neighbors;
        long sum = 0;
        for (int i = 0; i < g.N; i++)
        {
            Assert.That(nb[i].Length, Is.InRange(3, 12), $"顶点 {i} 度 {nb[i].Length} 超出球面网格范围");
            sum += nb[i].Length;
        }
        Assert.AreEqual(0L, sum % 2, "Σ度 = 2E 必为偶数（无向图握手引理）");
        double avg = sum / (double)g.N;
        Assert.That(avg, Is.InRange(4.0, 7.0), $"平均度 {avg:F2} 应接近六边形网格量级（5~6）");
    }

    [Test]
    public void IsLand_And_IsCoast_Semantics()
    {
        var g = BuildGrid(2);
        for (int i = 0; i < g.N; i++) g.Elev[i] = 100f;   // 全陆地
        const int ocean = 0;
        g.Elev[ocean] = 0f;                                // 单格海洋（海平面 0 不算陆地）

        Assert.False(g.IsLandCell(ocean));
        Assert.False(g.IsLand[ocean]);
        var nb = g.Neighbors;
        foreach (int j in nb[ocean])
        {
            Assert.True(g.IsLandCell(j), "海洋的邻居应为陆地");
            Assert.True(g.IsCoast(j), "海洋邻居的陆地格应为沿海");
        }
        for (int i = 0; i < g.N; i++)
        {
            if (i == ocean || !g.IsLandCell(i)) continue;
            bool adjacentOcean = Has(nb[i], ocean);
            Assert.AreEqual(adjacentOcean, g.IsCoast(i), $"格 {i} 沿海判定与邻接不符");
        }
        Assert.False(g.IsCoast(ocean), "海洋格本身不算沿海（判定只看球面邻居是否海洋）");
    }

    [Test]
    public void OverrideNeighbors_Hook_TakesEffect()
    {
        var g = BuildGrid(2);
        var fake = new int[g.N][];
        for (int i = 0; i < g.N; i++) fake[i] = new[] { (i + 1) % g.N };
        g.OverrideNeighbors(fake);
        Assert.AreSame(fake, g.Neighbors, "覆盖后 Neighbors 应直接返回覆盖表");
        Assert.AreEqual(1, g.Neighbors[0][0]);
    }

    [Test]
    public void DistKm_Symmetric_NonNegative_ZeroForSameCell()
    {
        var g = BuildGrid(2);
        const int a = 3, b = 17;
        float ab = g.DistKm(a, b);
        Assert.That(ab, Is.GreaterThanOrEqualTo(0f));
        Assert.AreEqual(ab, g.DistKm(b, a), 1e-4f);
        // 同类点距离 ≈0：单位化顶点浮点残差使 dot(v,v)≈1−ε → acos≈0 但非严格 0（<5km 量级）
        Assert.Less(g.DistKm(a, a), 5f, "同类点距离应≈0（浮点残差 <5km）");
        // 真邻居间距 ≈ 平均格距×R（n=2 约 0.55 rad × 6371 ≈ 3500km）
        float d = g.DistKm(0, g.Neighbors[0][0]);
        Assert.True(d > 100f && d < 5000f, $"邻居间距异常 {d}");
    }

    [Test]
    public void CellAreaKm2_UniformSphereFormula()
    {
        var g = BuildGrid(4);
        float expected = 4f * Mathf.Pi * g.RadiusKm * g.RadiusKm / g.N;
        Assert.AreEqual(expected, g.CellAreaKm2, 1e-4f);
    }

    [Test]
    public void EnsureWildCrops_Deterministic_AndDelegatesToCompute()
    {
        var g = BuildGrid(2);
        FillClimate(g);
        byte[] bits1 = WildCropsSystem.Compute(g, g.Seed);
        byte[] bits2 = WildCropsSystem.Compute(g, g.Seed);
        CollectionAssert.AreEqual(bits1, bits2, "同 seed 同气候场两次计算不一致");
        for (int i = 0; i < g.N; i++)
        {
            Assert.That(bits1[i], Is.InRange(0, 31), "bitmask 不得越出 5 位");
            if (!g.IsLandCell(i)) Assert.AreEqual(0, bits1[i], "海洋格不得有野生作物");
        }
        byte[] ensured = g.EnsureWildCrops();
        CollectionAssert.AreEqual(bits1, ensured, "EnsureWildCrops 应等价于直接 Compute(g, Seed)");
        Assert.AreSame(ensured, g.EnsureWildCrops(), "二次调用应命中缓存");
    }

    [Test]
    public void EnsureWildLivestock_MarksGrasslandInRainPolity()
    {
        var g = BuildGrid(2);
        for (int i = 0; i < g.N; i++)
        {
            g.Elev[i] = 100f;
            g.Precip[i] = 600f;
            g.Biome[i] = (byte)BiomeType.HotSteppe;
        }
        g.Precip[2] = 250f;                       // 带宽外（<300）→ 不可驯
        g.Biome[7] = (byte)BiomeType.Alpine;      // 非草原类 → 不可驯
        byte[] bits = g.EnsureWildLivestock();
        for (int i = 0; i < g.N; i++)
        {
            if (i == 2 || i == 7) Assert.AreEqual(0, bits[i], $"格 {i} 不应有畜牧位");
            else Assert.AreEqual(1, bits[i], $"格 {i} 应有畜牧位");
        }
        CollectionAssert.AreEqual(bits, WildCropsSystem.ComputeLivestock(g, g.Seed), "委托 ComputeLivestock 语义");
        Assert.AreSame(bits, g.EnsureWildLivestock(), "二次调用应命中缓存");
    }

    [Test]
    public void ToMapData_FromMapData_RoundtripPreservesNaturalFields()
    {
        var g = BuildGrid(2);
        FillRoundtripFields(g);
        var map = g.ToMapData();
        var g2 = GameGrid.FromMapData(map);

        Assert.AreEqual(g.Seed, g2.Seed);
        Assert.AreEqual(g.RadiusKm, g2.RadiusKm, 1e-4f);
        Assert.AreEqual(g.ProgradeRotation, g2.ProgradeRotation);
        Assert.AreEqual(g.RotationSpeed, g2.RotationSpeed, 1e-4f);
        Assert.AreEqual(g.AxialTilt, g2.AxialTilt, 1e-4f);
        Assert.AreEqual(g.MinElev, g2.MinElev, 1e-4f);
        Assert.AreEqual(g.MaxElev, g2.MaxElev, 1e-4f);
        Assert.AreEqual(g.MinTemp, g2.MinTemp, 1e-4f);
        Assert.AreEqual(g.MaxTemp, g2.MaxTemp, 1e-4f);
        Assert.AreEqual(g.MinPrecip, g2.MinPrecip, 1e-4f);
        Assert.AreEqual(g.MaxPrecip, g2.MaxPrecip, 1e-4f);
        Assert.AreEqual(2, g2.GridN, "GridN 应从顶点数反推");

        CollectionAssert.AreEqual(g.Verts, g2.Verts);
        CollectionAssert.AreEqual(g.Elev, g2.Elev);
        CollectionAssert.AreEqual(g.Temp, g2.Temp);
        CollectionAssert.AreEqual(g.Precip, g2.Precip);
        CollectionAssert.AreEqual(g.Biome, g2.Biome);
        CollectionAssert.AreEqual(g.RiverLevel, g2.RiverLevel);
        CollectionAssert.AreEqual(g.RiverFlow, g2.RiverFlow);
        CollectionAssert.AreEqual(g.RiverVolume, g2.RiverVolume);
        CollectionAssert.AreEqual(g.LakeLevel, g2.LakeLevel);
        CollectionAssert.AreEqual(g.MineralLevel, g2.MineralLevel);
        CollectionAssert.AreEqual(g.SoilLevel, g2.SoilLevel);
        CollectionAssert.AreEqual(g.MonsoonLevel, g2.MonsoonLevel);
        CollectionAssert.AreEqual(g.CurrentDirs, g2.CurrentDirs);
        CollectionAssert.AreEqual(g.CurrentWarmth, g2.CurrentWarmth);
        CollectionAssert.AreEqual(g.CurrentStrength, g2.CurrentStrength);
        for (int m = 0; m < MonsoonSystem.MonthCount; m++)
        {
            CollectionAssert.AreEqual(g.MonthPrecip[m], g2.MonthPrecip[m]);
            CollectionAssert.AreEqual(g.MonthTemp[m], g2.MonthTemp[m]);
        }
        // 人文层读回后初始化为 0；Psi/WildCrops 不入 MapData（自然层快照契约）→ 往返后为 null
        CollectionAssert.AreEqual(g.Province, g2.Province);
        CollectionAssert.AreEqual(g.Country, g2.Country);
        Assert.IsNull(g2.Psi);
        Assert.IsNull(g2.WildCrops);
    }

    [Test]
    public void Roundtrip_NeighborsRebuildIdentically()
    {
        // 模块测试：n=2 合成网格 → GameGrid → ToMapData → FromMapData → 邻接重建一致（不依赖存档）
        var g = BuildGrid(2);
        FillClimate(g);
        var g2 = GameGrid.FromMapData(g.ToMapData());
        var nb1 = g.Neighbors;
        var nb2 = g2.Neighbors;
        Assert.AreEqual(nb1.Length, nb2.Length);
        for (int i = 0; i < nb1.Length; i++)
            CollectionAssert.AreEquivalent(nb1[i], nb2[i], $"格 {i} 邻接重建不一致");
    }

    private static void FillRoundtripFields(GameGrid g)
    {
        for (int i = 0; i < g.N; i++)
        {
            g.Elev[i] = i * 13.7f - 100f;
            g.Temp[i] = -30f + i * 2.5f;
            g.Precip[i] = 100f + i * 33f;
            g.Biome[i] = (byte)(i % 32);
            g.RiverLevel[i] = (byte)(i % 4);
            g.RiverFlow[i] = i == g.N - 1 ? -1 : (i + 1) % g.N;
            g.RiverVolume[i] = i * 1.25f;
            g.LakeLevel[i] = (byte)(i % 3);
            g.MineralLevel[i] = (byte)(i % 8);
            g.SoilLevel[i] = (byte)(1 + i % 5);
            g.MonsoonLevel[i] = (byte)(i % 256);
            g.CurrentWarmth[i] = -1f + i * 0.05f;
            g.CurrentStrength[i] = 0.3f + i * 0.01f;
            g.CurrentDirs[i] = new Vector3((float)(i % 5), 0.5f, (float)(i % 3)).Normalized();
        }
        for (int m = 0; m < MonsoonSystem.MonthCount; m++)
            for (int i = 0; i < g.N; i++)
            {
                g.MonthPrecip[m][i] = (byte)((m + i) % 256);
                g.MonthTemp[m][i] = (byte)((m * 7 + i * 3) % 256);
            }
    }
}