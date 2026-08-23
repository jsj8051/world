// Slice: CivSimDiag.Builders.cs - verbatim member extraction from CivSimDiag.cs (pure refactor, 2026-08-19).
using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using World.Biome;
using World.CivSim;
using World.LogicGrid;
using World.MapGen;

using World.CivSim.Entities;
namespace World.Diagnostics;

public partial class CivSimDiag
{

    /// <summary>小网格（N=2 赤道相邻两点，Neighbors 连通；RadiusKm 决定胞面积）。</summary>
    private static GameGrid MakeGrid(float radiusKm, byte biome, float temp, float precip, byte soil = 3, int nCells = 2)
    {
        // 顶点：2 格 = (1,0,0)/(0,1,0)（90° 互邻，历史默认）；nCells=4 = 赤道均分（相邻 90° 邻接，BuildNeighbors 可用）
        var verts = nCells == 2
            ? new[] { new Vector3(1, 0, 0), new Vector3(0, 1, 0) }
            : BuildRing(nCells);
        int m = verts.Length;
        var g = new GameGrid { N = m, GridN = 1, Seed = 42, RadiusKm = radiusKm };
        g.Verts = verts;
        g.Elev = Repeat(m, 100f);
        g.Temp = Repeat(m, temp);
        g.Precip = Repeat(m, precip);
        g.Biome = new byte[m];
        g.RiverLevel = new byte[m];
        g.RiverFlow = new int[m];
        g.RiverVolume = new float[m];
        g.LakeLevel = new byte[m];
        g.MineralLevel = new byte[m];
        g.SoilLevel = new byte[m];
        g.MonsoonLevel = new byte[m];
        for (int c = 0; c < m; c++)
        {
            g.Biome[c] = biome;
            g.RiverFlow[c] = -1;
            g.SoilLevel[c] = soil;
        }
        g.MonthPrecip = new byte[MonsoonSystem.MonthCount][];
        g.MonthTemp = new byte[MonsoonSystem.MonthCount][];
        for (int mm = 0; mm < MonsoonSystem.MonthCount; mm++)
        {
            g.MonthPrecip[mm] = new byte[m];
            g.MonthTemp[mm] = new byte[m];
            for (int c = 0; c < m; c++)
            {
                g.MonthPrecip[mm][c] = (byte)(255 / 12);
                g.MonthTemp[mm][c] = FieldCodec.TempToByte(temp);
            }
        }
        g.CurrentDirs = new Vector3[m];
        g.CurrentWarmth = new float[m];
        g.CurrentStrength = new float[m];
        g.Province = new int[m];
        g.Country = new int[m];
        return g;
    }


    private static float[] Repeat(int n, float v)
    {
        var a = new float[n];
        Array.Fill(a, v);
        return a;
    }


    private static Vector3[] BuildRing(int n)
    {
        var a = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float lon = Mathf.Tau * i / n;
            a[i] = new Vector3(Mathf.Cos(lon), Mathf.Sin(lon), 0f);
        }
        return a;
    }


    /// <summary>显式"真环"邻接（i↔i±1）——覆盖 BuildNeighbors 的桶重建（BuildRing 在 XY 平面 → 极区桶
    /// 经度折叠 → 邻接残缺不对称 → BFS 跳数≠几何距离）。酋邦庇护等依赖跳数的测试必须用精确图。</summary>
    private static void RingLinks(GameGrid g)
    {
        int n = g.N;
        var nb = new int[n][];
        for (int i = 0; i < n; i++)
            nb[i] = new[] { (i + n - 1) % n, (i + 1) % n };
        g.OverrideNeighbors(nb);
    }


    private static CivSimContext MakeCtx(GameGrid g, int seed = 42, int origins = 3)
    {
        TechTable.Load();   // S 场景/测试手动构造 ctx，不经过 CivEngine.Run 的 Load
        int n = g.N;
        var ctx = new CivSimContext
        {
            Grid = g,
            CellBands = new Band[n],
            Bands = new List<Band>(),
            Seed = seed,
            OriginCount = origins,
            Rng = new DeterministicRandom(seed),
            R = new float[n],
            CellF = new float[n],
            CellPop = new float[n],
            CellFarmPop = new float[n],
            BfsStamp = new int[n],
            BfsStampValue = 1,
            NextBandId = 0,   // 实体 Id 计数器（测试构造从 0 起）
            WildCrops = g.EnsureWildCrops(),
            Suit = WildCropsSystem.Suitability(g),
            FirstFarmTick = -1,
            // 影响力场模型（2026-08-10）：S 场景也走新字段
            CellOwner = EnumerableFill(-1, n),
            CellBestOwner = EnumerableFill(-1, n),
            CellBestInf = new float[n],
            CellOwnerInf = new float[n],
            LockedUntil = EnumerableFill(0, n),   // 实控锁定（v8 冲突机制）
            Cultivation = new float[n],           // 开垦率场（2026-08-17 土地挂钩）
        };
        ctx.TerritoryCells = new List<int>[4096];
        ctx.TerritoryDists = new List<byte>[4096];
        for (int i = 0; i < ctx.TerritoryCells.Length; i++)
        {
            ctx.TerritoryCells[i] = new List<int>();
            ctx.TerritoryDists[i] = new List<byte>();
        }
        for (int i = 0; i < n; i++) ctx.CellBands[i] = null;
        CivEngine.BuildLayer1(ctx);   // 层1 空间生产力 R（两层模型 2026-08-17）
        // ⚠️ 2026-08-17：砍存量再生——无 InitStock；开垦率场已在构造建好（全 0）
        return ctx;
    }


    private static int[] EnumerableFill(int v, int n)
    {
        var a = new int[n];
        Array.Fill(a, v);
        return a;
    }


    private static void RunTicks(CivSimContext ctx, int ticks)
    {
        var reg = CivModelRegistry.StoneAge();
        for (int k = 0; k < ticks; k++, ctx.Tick++)   // ⚠️ 必须递增 Tick：OriginModel 只在 tick 0 播种
        {
            CivEngine.RefreshCellState(ctx);
            reg.ExecuteAll(ctx);
        }
    }


    private static Band AddBand(CivSimContext ctx, int cell, float pop, params string[] techs)
    {
        var e = new Band
        {
            Id = ctx.NextBandId++,   // 独立计数器（与 Origin/分裂一致，2026-08-10）
            Cell = cell,
            P = pop,
            OriginCell = cell,
            BornTick = ctx.Tick,
            CultureShare = ShareField.NewCulture("test_cult"),
            CultureGroupShare = ShareField.NewCulture("test_grp"),
            ReligionShare = ShareField.NewReligion(ReligionStage.Animism),
        };
        foreach (var t in techs) e.TechKeys.Add(t);
        ctx.Bands.Add(e);
        ctx.CellBands[cell] = e;   // 一格一实体
        return e;
    }


    /// <summary>手造聚落（测试辅助——SettlementModel 形成逻辑的等价物）：给部落建粮仓并关联。</summary>
    private static Settlement AddSettlement(CivSimContext ctx, Band occupant)
    {
        var s = new Settlement
        {
            Id = ctx.NextSettlementId++,
            Cell = occupant.Cell,
            BornTick = ctx.Tick,
            Level = 0,
            LastLevelUpTick = ctx.Tick,
            DwellFrom = ctx.Tick,
            OccupantId = occupant.Id,
        };
        ctx.Settlements.Add(s);
        occupant.PlaceId = s.Id;
        return s;
    }


    /// <summary>T64 辅助：手动构造酋邦成员关系（ChiefdomId 全部指向酋长 a；ChiefdomCells 成员表）。
    /// ⚠️ 不跑 ChiefdomModel（测试直接构造酋邦状态——StateModel 只读成员表）。</summary>
    private static void SetupStateChiefdom(CivSimContext ctx, Band chief, Band m1, Band m2)
    {
        foreach (var e in new[] { chief, m1, m2 })
        {
            e.ChiefdomId = chief.Id;
            e.ChiefdomSize = 3;
            e.TerritoryId = chief.Id;   // 同领地（StateModel 不查领地，此处仅一致性）
        }
        ctx.ChiefdomCells = new List<int>[8];
        for (int i = 0; i < ctx.ChiefdomCells.Length; i++) ctx.ChiefdomCells[i] = new List<int>();
        ctx.ChiefdomCells[chief.Id].Add(chief.Id);
        ctx.ChiefdomCells[chief.Id].Add(m1.Id);
        ctx.ChiefdomCells[chief.Id].Add(m2.Id);
    }


    /// <summary>写一个坏版本档（ver=5 → 应拒绝）。</summary>
    private static void WriteBadVersion(string path, ushort ver)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null) return;
        f.Store8((byte)'C'); f.Store8((byte)'M'); f.Store8((byte)'P'); f.Store8((byte)'1');
        f.Store16(ver);
        f.Store32(42); f.Store32(0); f.Store32(0);
    }


    /// <summary>写一个含化石 biome 4 的档 → 应拒绝（最小自然段，与 GameMapArchive.ReadBody 严格对应）。</summary>
    private static void WriteBadBiome(string path, GameGrid src)
    {
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null) return;
        f.Store8((byte)'C'); f.Store8((byte)'M'); f.Store8((byte)'P'); f.Store8((byte)'1');
        f.Store16(4);
        f.Store32(42); f.Store32(0); f.Store32(0);
        f.Store64(0);   // rngState（v4 头部字段）
        f.Store32(0);   // cultureKeyCount（v4 头部字段）
        f.Store32(0);   // religionKeyCount（v4 头部字段）
        // 最小自然段：GridN=1, N=2, seed, radius, 标志, 各字段 2 格
        f.Store32(1); f.Store32(2); f.Store32(42); f.StoreFloat(MapArchive.DefaultRadiusKm);
        f.Store8(1); f.StoreFloat(1f); f.StoreFloat(23.4f); f.StoreFloat(1f);
        for (int i = 0; i < 6; i++) f.StoreFloat(0f);   // min/max
        for (int i = 0; i < 2; i++) { f.StoreFloat(0); f.StoreFloat(1); f.StoreFloat(0); }   // verts
        for (int i = 0; i < 2; i++) f.StoreFloat(100f);   // elev
        for (int i = 0; i < 2; i++) f.StoreFloat(20f);    // temp
        for (int i = 0; i < 2; i++) f.StoreFloat(800f);   // precip
        for (int i = 0; i < 2; i++) f.Store8(4);          // biome ← 化石值 4！
        for (int i = 0; i < 2; i++) f.Store8(0);          // river
        for (int i = 0; i < 2; i++) f.Store32(0xFFFFFFFF); // riverFlow -1
        for (int i = 0; i < 2; i++) f.StoreFloat(0f);     // riverVolume
        for (int i = 0; i < 2; i++) f.Store8(0);          // lake
        for (int i = 0; i < 2; i++) f.Store8(0);          // mineral
        for (int i = 0; i < 2; i++) f.Store8(3);          // soil
        for (int i = 0; i < 2; i++) f.Store8(0);          // monsoon
        for (int m = 0; m < MonsoonSystem.MonthCount; m++) for (int i = 0; i < 2; i++) f.Store8(21);   // monthPrecip
        for (int m = 0; m < MonsoonSystem.MonthCount; m++) for (int i = 0; i < 2; i++) f.Store8(170);  // monthTemp
        for (int i = 0; i < 2; i++) { f.StoreFloat(0); f.StoreFloat(0); f.StoreFloat(0); }   // currentDirs
        for (int i = 0; i < 2; i++) f.StoreFloat(0f);     // warmth
        for (int i = 0; i < 2; i++) f.StoreFloat(0f);     // strength
        for (int i = 0; i < 2; i++) f.StoreFloat(0f);     // psi（v2+ 字段）
        for (int i = 0; i < 2; i++) f.Store32(0);         // province
        for (int i = 0; i < 2; i++) f.Store32(0);         // country
        f.Store32(0);                                     // 实体数 0
    }

}
