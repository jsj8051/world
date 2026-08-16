using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.Biome;
using World.CivSim;
using World.HexPlanet;
using World.LogicGrid;
using World.MapGen;

namespace World.Tests;

/// <summary>
/// 演示性验证：构造一个具体网格（n=2 → 42 顶点胞，北半球陆地/南半球海洋），
/// 然后**逐个功能单独验证**——每个功能一个独立 [Test]，断言该功能在该网格上的契约。
/// 运行本地执行器会逐行输出 PASS <功能名>，即"逐功能验证"的结果。
/// 全部纯托管（Godot 数学类型安全）；OceanCurrent/ClimateGenerator 等引擎依赖路径不在此列。
/// </summary>
public class GridFeatureVerifyTests
{
    private const int DemoSeed = 20260816;

    /// <summary>
    /// 演示网格：Icosahedron.Subdivide(2, 6371) → 42 单位球顶点。
    /// 陆地 = Y&gt;0（海拔 300~2300m），海洋 = Y&lt;0（-1500~-2700m）；
    /// 温度赤道 28°C → 极点 -17°C；降水 400~1800mm（含干湿）；陆地 biome = HotSteppe。
    /// MonsoonSystem.MonthCount 个月温/月降水按 FieldCodec 编码（与生产管线同口径）。
    /// </summary>
    private static GameGrid BuildDemoGrid()
    {
        Icosahedron.Subdivide(2, 6371f, out var verts, out _);
        int n = verts.Count;
        var unit = new Vector3[n];
        for (int i = 0; i < n; i++) unit[i] = verts[i].Normalized();

        var g = new GameGrid
        {
            N = n, GridN = 2, Seed = DemoSeed, RadiusKm = 6371f,
            ProgradeRotation = true, RotationSpeed = 1f, AxialTilt = 23.4f, Insolation = 1f,
            Verts = unit,
            Elev = new float[n],
            Temp = new float[n],
            Precip = new float[n],
            Biome = new byte[n],
            RiverLevel = new byte[n],
            RiverFlow = new int[n],
            RiverVolume = new float[n],
            LakeLevel = new byte[n],
            MineralLevel = new byte[n],
            SoilLevel = new byte[n],
            MonsoonLevel = new byte[n],
            MonthPrecip = new byte[MonsoonSystem.MonthCount][],
            MonthTemp = new byte[MonsoonSystem.MonthCount][],
            CurrentDirs = new Vector3[n],
            CurrentWarmth = new float[n],
            CurrentStrength = new float[n],
            Province = new int[n],
            Country = new int[n],
        };
        for (int i = 0; i < n; i++)
        {
            // ⚠️ Y≥0 算陆地：与场函数口径统一（elevNorm<0 才算海洋——Y==0 的赤道格
            //   MineralSystem/RiverSystem 视为可成矿/可流经，不能判为海洋）
            bool land = unit[i].Y >= 0f;
            float lat = Mathf.Asin(Mathf.Clamp(unit[i].Y, -1f, 1f));          // rad
            float latDeg = lat / Mathf.Pi * 180f;
            g.Elev[i] = land ? 300f + 40f * (i % 6) * 100f : -1500f - 300f * (i % 5);
            g.Temp[i] = 28f - 45f * Mathf.Abs(latDeg) / 90f;                  // 赤道 28 → 极点 -17
            g.Precip[i] = 400f + (i * 37) % 1400f;                            // 400..1800mm
            g.Biome[i] = land ? (byte)BiomeType.HotSteppe : (byte)BiomeType.Ocean;
            g.RiverFlow[i] = -1;
        }
        for (int m = 0; m < MonsoonSystem.MonthCount; m++)
        {
            g.MonthPrecip[m] = new byte[n];
            g.MonthTemp[m] = new byte[n];
            for (int i = 0; i < n; i++)
            {
                g.MonthPrecip[m][i] = FieldCodec.RatioToByte(1f / MonsoonSystem.MonthCount);
                g.MonthTemp[m][i] = FieldCodec.TempToByte(g.Temp[i]);
            }
        }
        return g;
    }

    /// <summary>归一化海拔（= 单位球 Y；&lt;0 海洋），各网格功能的标准输入。</summary>
    private static float[] ElevNorm(GameGrid g) => Array.ConvertAll(g.Verts, v => v.Y);

    // ─── 功能 1：邻接表结构 ───

    [Test]
    public void Grid_Neighbors_Structure()
    {
        var g = BuildDemoGrid();
        for (int i = 0; i < g.N; i++)
        {
            Assert.That(g.Neighbors[i].Length, Is.InRange(3, 12), $"格 {i} 度 {g.Neighbors[i].Length}");
            foreach (int nb in g.Neighbors[i])
            {
                Assert.AreNotEqual(i, nb, "无自环");
                Assert.Contains(i, g.Neighbors[nb], $"对称：{i}→{nb} 但 {nb} 不含 {i}");
            }
        }
    }

    // ─── 功能 2：海陆判定 / 沿海 ───

    [Test]
    public void Grid_LandOceanAndCoast()
    {
        var g = BuildDemoGrid();
        int land = 0, coast = 0;
        for (int i = 0; i < g.N; i++)
        {
            Assert.AreEqual(g.Elev[i] > 0f, g.IsLandCell(i), $"IsLandCell({i}) 应等于 Elev>0");
            if (g.IsLandCell(i)) land++;
            if (g.IsLandCell(i) && g.IsCoast(i)) coast++;
        }
        Assert.That(land, Is.InRange(15, 27), $"半球陆地格约 21（实测 {land}）");
        Assert.GreaterOrEqual(coast, 3, "陆海交界处应有沿海格");
    }

    // ─── 功能 3：球面距离 / 胞面积 ───

    [Test]
    public void Grid_DistancesAndArea()
    {
        var g = BuildDemoGrid();
        Assert.Less(g.DistKm(0, 0), 5f, "同类点距离 ≈0（浮点残差）");
        for (int i = 0; i < 20; i++)
        {
            int j = (i * 7 + 3) % g.N;
            Assert.AreEqual(g.DistKm(i, j), g.DistKm(j, i), 1e-4f, "距离对称");
        }
        float area = 4f * Mathf.Pi * 6371f * 6371f / g.N;
        Assert.AreEqual(area, g.CellAreaKm2, 1e-3f, "胞面积 = 4πR²/N");
        float d = g.DistKm(0, g.Neighbors[0][0]);
        Assert.That(d, Is.InRange(100f, 5000f), $"邻居间距 {d}（n=2 平均格距≈3500km）");
    }

    // ─── 功能 4：层1 空间生产力（CivEngine.BuildLayer1）───

    [Test]
    public void Grid_Layer1_SpatialProductivity()
    {
        var g = BuildDemoGrid();
        var ctx = new CivSimContext { Grid = g, R = new float[g.N] };
        CivEngine.BuildLayer1(ctx);
        int rPositive = 0;
        for (int i = 0; i < g.N; i++)
        {
            if (!g.IsLandCell(i)) { Assert.AreEqual(0f, ctx.R[i], 1e-6f, $"海洋格 {i} R 应为 0"); continue; }
            if (ctx.R[i] > 0f) rPositive++;
        }
        Assert.Greater(rPositive, 0, "适中温湿陆地应有正生产力");
        Assert.Greater(ctx.RMax, 0f, "RMax 应为正（殖民落点归一化参考）");
        // 确定性：重跑一次逐位一致
        var ctx2 = new CivSimContext { Grid = g, R = new float[g.N] };
        CivEngine.BuildLayer1(ctx2);
        CollectionAssert.AreEqual(ctx.R, ctx2.R, "同网格同输入 R 场应逐位一致");
    }

    // ─── 功能 5：野生作物（适宜度 + 分布位图）───

    [Test]
    public void Grid_WildCrops_Deterministic_OceanZero()
    {
        var g = BuildDemoGrid();
        var suit = WildCropsSystem.Suitability(g);
        bool anyRich = false;
        for (int i = 0; i < g.N; i++)
        {
            if (!g.IsLandCell(i))
                for (int k = 0; k < WildCropsSystem.SeedCount; k++)
                    Assert.AreEqual(0f, suit[i, k], $"海洋格 {i} 种子 {k} 适宜度应 0");
            else
                for (int k = 0; k < WildCropsSystem.SeedCount; k++)
                {
                    Assert.That(suit[i, k], Is.InRange(0f, 1f));
                    if (suit[i, k] > 0.3f) anyRich = true;
                }
        }
        Assert.True(anyRich, "温湿草原应存在适宜作物的陆地格");

        byte[] a = WildCropsSystem.Compute(g, g.Seed);
        byte[] b = WildCropsSystem.Compute(g, g.Seed);
        CollectionAssert.AreEqual(a, b, "同 seed 同网格两次一致");
        for (int i = 0; i < g.N; i++)
        {
            if (!g.IsLandCell(i)) Assert.AreEqual(0, a[i], $"海洋格 {i} 无作物位");
            else Assert.AreEqual(a[i], a[i] & 0x1F, $"格 {i} 位掩码限于 5 位");
        }
    }

    // ─── 功能 6：野生畜牧（草原 biome × 降水带）───

    [Test]
    public void Grid_WildLivestock_SteppeRainBand()
    {
        var g = BuildDemoGrid();
        byte[] bits = WildCropsSystem.ComputeLivestock(g, g.Seed);
        byte[] again = WildCropsSystem.ComputeLivestock(g, g.Seed);
        CollectionAssert.AreEqual(bits, again, "确定性");
        for (int i = 0; i < g.N; i++)
        {
            bool land = g.IsLandCell(i);
            bool inBand = g.Precip[i] >= 300f && g.Precip[i] <= 1200f;   // HotSteppe 全部陆地
            Assert.AreEqual(land && inBand ? (byte)1 : (byte)0, bits[i], $"格 {i}（precip={g.Precip[i]}）");
        }
    }

    // ─── 功能 7：土壤肥力（海洋 0 / 陆地 1-5）───

    [Test]
    public void Grid_Soil_AllLandFertile_OceanZero()
    {
        var g = BuildDemoGrid();
        SoilSystem.ComputeSoil(ElevNorm(g), g.Biome, g.Precip, g.Temp, null, null, out byte[] soil);
        for (int i = 0; i < g.N; i++)
        {
            if (!g.IsLandCell(i)) Assert.AreEqual(0, soil[i], $"海洋格 {i} 肥力 0");
            else Assert.That(soil[i], Is.InRange((byte)1, (byte)5), $"陆地格 {i} 肥力 1-5");
        }
    }

    // ─── 功能 8：矿藏（海洋 0 / 编码合法 / 成矿）───

    [Test]
    public void Grid_Minerals_OceanZero_EncodingValid()
    {
        var g = BuildDemoGrid();
        float[] age = Enumerable.Repeat(1f, g.N).ToArray();
        float[] hydro = Enumerable.Repeat(0.5f, g.N).ToArray();
        float[] sedM = Enumerable.Repeat(0.5f, g.N).ToArray();
        float[] metaM = Enumerable.Repeat(0.5f, g.N).ToArray();
        MineralSystem.ComputeMinerals(g.Verts, g.Neighbors, null, ElevNorm(g), g.Precip,
            age, hydro, sedM, metaM, null, g.Seed, out byte[] minerals);
        int oreCount = 0;
        for (int i = 0; i < g.N; i++)
        {
            if (!g.IsLandCell(i)) { Assert.AreEqual(0, minerals[i], $"海洋格 {i} 无矿"); continue; }
            int type = MineralSystem.TypeOf(minerals[i]);
            int rich = MineralSystem.RichnessOf(minerals[i]);
            Assert.That(type, Is.InRange(0, 8), $"格 {i} 矿种 {type} 非法");
            Assert.That(rich, Is.InRange(0, 3), $"格 {i} 富度 {rich} 非法");
            if (minerals[i] != 0) oreCount++;
        }
        Assert.Greater(oreCount, 0, "该星球按百分位阈值应出现矿点");
    }

    // ─── 功能 9：河流（流向单调下坡 / 海洋汇点）───

    [Test]
    public void Grid_Rivers_FlowMonotonic_OceanSink()
    {
        var g = BuildDemoGrid();
        float[] en = ElevNorm(g);
        RiverSystem.Compute(g.Verts, g.Neighbors, en,
            out int[] flow, out _, out byte[] riverLevel, out var paths, out _, out _, areaThreshold: 3f);
        for (int i = 0; i < g.N; i++)
        {
            Assert.That(flow[i], Is.InRange(0, g.N - 1), $"flow[{i}] 必须合法");
            if (en[i] < 0f) { Assert.AreEqual(i, flow[i], "海洋格流向自身（汇点）"); continue; }
            if (flow[i] != i)
            {
                Assert.True(Array.IndexOf(g.Neighbors[i], flow[i]) >= 0, $"流向 {i}→{flow[i]} 非邻居");
                Assert.Less(en[flow[i]], en[i], $"格 {i} 流向高处（违反单调）");
            }
        }
        foreach (int[] p in paths)
            for (int k = 1; k < p.Length; k++)
                Assert.Less(en[p[k]], en[p[k - 1]], "河道必须单调下坡");
    }

    // ─── 功能 10：洋流（内陆全零 / 海洋有限 / 切球面）───

    [Test]
    public void Grid_OceanCurrent_LandZero_TangentUnit()
    {
        var g = BuildDemoGrid();
        float[] en = ElevNorm(g);
        // 2000 次 SOR 小网格必收敛（maxErr<1e-5<1e-4）→ 不触发 GD.PushWarning（触发即崩溃）
        OceanCurrent.Compute(g.Verts, g.Neighbors, en,
            out Vector3[] dirs, out float[] warmth, out float[] strength, out float[] psi,
            betaScale: 0.5f, iterations: 2000);
        for (int i = 0; i < g.N; i++)
        {
            if (en[i] >= 0f)
            {
                Assert.AreEqual(Vector3.Zero, dirs[i], $"内陆 {i} 无流向");
                Assert.AreEqual(0f, strength[i], 1e-6f, $"内陆 {i} 强度 0");
                Assert.AreEqual(0f, psi[i], 1e-6f, $"内陆 {i} ψ=0（边界条件）");
            }
            else
            {
                Assert.True(dirs[i].IsFinite() && float.IsFinite(psi[i]) &&
                            float.IsFinite(warmth[i]) && float.IsFinite(strength[i]), $"海洋 {i} 场有限（收敛）");
                float len2 = dirs[i].LengthSquared();
                if (len2 > 1e-12f)
                {
                    Assert.AreEqual(1f, dirs[i].Length(), 1e-4f, $"海洋 {i} 单位切向");
                    Assert.Less(Mathf.Abs(dirs[i].Dot(g.Verts[i])), 1e-3f, $"海洋 {i} 切于球面");
                    Assert.That(strength[i], Is.InRange(0.3f, 1.0f), "环流权重范围");
                }
                else Assert.AreEqual(0f, strength[i], 1e-6f, $"无流 {i} 强度 0");
                Assert.That(warmth[i], Is.InRange(-1f, 1f), "冷暖范围");
            }
        }
    }

    // ─── 功能 11：风场（对网格每个顶点切平面 / 单位长）───

    [Test]
    public void Grid_WindOnVerts_TangentUnit()
    {
        var g = BuildDemoGrid();
        foreach (Vector3 v in g.Verts)
        {
            Vector3 w = WindField.WindAt(v);
            Assert.Less(Mathf.Abs(w.Dot(v)), 1e-4f, "风向切于球面");
            if (w.LengthSquared() > 1e-9f) Assert.AreEqual(1f, w.Length(), 1e-4f, "风向单位长");
        }
    }

    // ─── 功能 12：存档布局（BodyLength 与网格规模一致）───

    [Test]
    public void Grid_ArchiveLayout_BodyLength()
    {
        // 头 53B + 每顶点 94B（v2 含 Psi）；演示网格 n=2 → 42 顶点
        Assert.AreEqual(241L, ArchiveLayout.BodyLength(2, 2));
        Assert.AreEqual(233L, ArchiveLayout.BodyLength(2, 1));   // v1 无 Psi
        Assert.AreEqual(53L + 94L * 42L, ArchiveLayout.BodyLength(42, 2));
        Assert.AreEqual(ArchiveLayout.BodyLength(42, 2) - 4L * 42L, ArchiveLayout.BodyLength(42, 1));
    }

    // ─── 功能 13：网格→存档数据→网格 往返一致性（模块级）───

    [Test]
    public void Grid_ToMapData_RoundTrip()
    {
        var g = BuildDemoGrid();
        var g2 = GameGrid.FromMapData(g.ToMapData());
        Assert.AreEqual(g.N, g2.N);
        Assert.AreEqual(g.Seed, g2.Seed);
        Assert.AreEqual(g.RadiusKm, g2.RadiusKm, 1e-4f);
        for (int i = 0; i < g.N; i++)
        {
            Assert.AreEqual(g.Verts[i].X, g2.Verts[i].X, 1e-6f);
            Assert.AreEqual(g.Elev[i], g2.Elev[i], 1e-4f);
            Assert.AreEqual(g.Biome[i], g2.Biome[i]);
            Assert.AreEqual(g.Temp[i], g2.Temp[i], 1e-4f);
            Assert.AreEqual(g.Province[i], g2.Province[i]);
        }
        // 邻接为确定性重建——往返后必须逐格一致（模块不变量）
        Assert.AreEqual(g.Neighbors.Length, g2.Neighbors.Length);
        for (int i = 0; i < g.N; i++)
            CollectionAssert.AreEqual(g.Neighbors[i], g2.Neighbors[i], $"格 {i} 邻接重建不一致");
    }
}