using System;
using System.IO;
using NUnit.Framework;
using World.Utils;

using World.CivSim.Entities;
namespace World.Tests;

/// <summary>
/// 段表容器（ChunkWriter/ChunkReader）往返测试（2026-08-23 存档段表化 P1）。
/// 契约：
/// 1. 头：magic(4) + skeletonVer(2) + reserved(2) = 8B。
/// 2. 每段：segMagic(4) + segVer(2) + 数据；段表条目 12B（type/offset/length）。
/// 3. 尾目录：文件尾 12B = 段表偏移(4) + 段表字节数(4) + tailMagic(4)。
/// 4. 未知段：读端按段表跳过，不影响已知段读取（随机访问 + 前向兼容的核心）。
/// 5. 损坏尾目录 / 过短文件 → InvalidDataException。
/// </summary>
public class ArchiveChunkTests
{
    private static MemoryStream WriteSample(string magic = "MPA1")
    {
        var ms = new MemoryStream();
        var w = new ChunkWriter(ms, magic, 6);
        w.BeginSegment("HEAD", 1);
        w.Store32(42);          // seed
        w.StoreFloat(6371f);    // radiusKm
        w.EndSegment();
        w.BeginSegment("ELEV", 1);
        w.Store8(7); w.Store8(9);   // elev n=2
        w.EndSegment();
        w.Finish();
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void Roundtrip_HeaderAndSegments()
    {
        var ms = WriteSample();
        var r = new ChunkReader(ms);

        Assert.AreEqual("MPA1", r.Magic);
        Assert.AreEqual(6, r.SkeletonVer);

        Assert.IsTrue(r.SeekSegment("HEAD"));
        Assert.AreEqual(42u, r.Get32());
        Assert.AreEqual(6371f, r.GetFloat());

        Assert.IsTrue(r.SeekSegment("ELEV"));
        Assert.AreEqual(7, r.Get8());
        Assert.AreEqual(9, r.Get8());
    }

    [Test]
    public void Roundtrip_SegmentOffsetsAreAccurate()
    {
        var ms = WriteSample();
        var r = new ChunkReader(ms);

        Assert.IsTrue(r.TryGetEntry("HEAD", out var head));
        Assert.IsTrue(r.TryGetEntry("ELEV", out var elev));

        // 段必须有 6B 段头；offset 相对文件头；长度 = 段头 + 数据
        Assert.AreEqual(6 + 4 + 4, head.Length);   // HEAD: 6B 头 + seed 4 + radius 4
        Assert.AreEqual(6 + 2, elev.Length);       // ELEV: 6B 头 + 2 字节
        // 布局：8B 文件头 → HEAD 段 → ELEV 段 → 段表 → 尾目录
        Assert.AreEqual(8, head.Offset);           // HEAD 紧跟文件头之后
        Assert.AreEqual(head.Offset + head.Length, elev.Offset);   // 段连续无间隙
        // 段表紧随最后一段之后，尾目录在文件末 12B
        Assert.AreEqual(elev.Offset + elev.Length, ms.Length - ChunkTable.TailBytes - ChunkTable.EntryBytes * 2);
        Assert.AreEqual(ms.Length, r.Length);
    }

    [Test]
    public void UnknownSegment_SkippedWithoutBreakingKnownOnes()
    {
        var ms = new MemoryStream();
        var w = new ChunkWriter(ms, "MPA1", 6);
        w.BeginSegment("HEAD", 1);
        w.Store32(42);
        w.EndSegment();
        w.BeginSegment("FUTR", 1);   // 未来版本的新段（读端不认识）
        for (int i = 0; i < 100; i++) w.Store8(3);
        w.EndSegment();
        w.BeginSegment("ELEV", 1);
        w.Store16(99);
        w.EndSegment();
        w.Finish();
        ms.Position = 0;

        var r = new ChunkReader(ms);
        // 已知段不受未知段干扰
        Assert.IsTrue(r.SeekSegment("HEAD"));
        Assert.AreEqual(42u, r.Get32());
        Assert.IsTrue(r.SeekSegment("ELEV"));
        Assert.AreEqual(99, r.Get16());
        // 未知段可见但可跳过
        Assert.IsTrue(r.HasSegment("FUTR"));
        Assert.IsFalse(r.HasSegment("NOPE"));
    }

    [Test]
    public void CorruptTail_Throws()
    {
        var ms = WriteSample();
        var bytes = ms.ToArray();
        // 破坏尾目录：改写 tailMagic 前 4 字节（文件末 12B 是尾目录，最后 4B 是 tailMagic）
        bytes[^1] = (byte)'X';
        var corrupt = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() => new ChunkReader(corrupt));
    }

    [Test]
    public void TruncatedFile_Throws()
    {
        var ms = WriteSample();
        var bytes = ms.ToArray();
        var shortBuf = new byte[bytes.Length - ChunkTable.TailBytes];
        Array.Copy(bytes, shortBuf, shortBuf.Length);

        Assert.Throws<InvalidDataException>(() => new ChunkReader(new MemoryStream(shortBuf)));
    }

    [Test]
    public void TypeNameAscii_PacksAndUnpacks()
    {
        Assert.AreEqual("HEAD", ChunkTable.UintAscii(ChunkTable.AsciiUint("HEAD")));
        Assert.AreEqual("FUTR", ChunkTable.UintAscii(ChunkTable.AsciiUint("FUTR")));
        Assert.AreEqual("ELEV", ChunkTable.UintAscii(ChunkTable.AsciiUint("ELEV")));
    }

    // ── 2026-08-23 单存档化：CIVI 文明段编解码往返（.mpa v8 / .cmp v16 共用路径；v8 起字段概念分组）──

    /// <summary>构造最小自然地图（n=5 星形顶点，仅满足 GameGrid.FromMapData 的最小字段）。</summary>
    private static World.LogicGrid.GameGrid MakeMinGrid() => MakeMinGridPublic();

    public static World.LogicGrid.GameGrid MakeMinGridPublic()
    {
        int n = 5;
        // 球面单位方向伪顶点（5 个任意单位向量——测试只做编解码，不要求真实几何）
        var verts = new Godot.Vector3[n];
        for (int i = 0; i < n; i++)
            verts[i] = new Godot.Vector3(1f, 0.3f * i, 0.5f).Normalized();
        var map = new World.MapGen.MapData
        {
            Verts = verts,
            Seed = 7,
            RadiusKm = 100f,
            Elev = new float[n],
            Temp = new float[n],
            Precip = new float[n],
            Biome = new byte[n],
        };
        return World.LogicGrid.GameGrid.FromMapData(map);
    }

    /// <summary>构造最小文明结果（1 部落 + 1 聚落 + 1 战争，字段齐全用于往返断言）。</summary>
    private static World.CivSim.CivSimResult MakeMinCiv() => MakeMinCivPublic();

    public static World.CivSim.CivSimResult MakeMinCivPublic()
    {
        var grid = MakeMinGrid();
        int n = grid.N;
        var band = new Band
        {
            Id = 3,
            Cell = 1,
            P = 42f,
            TerritoryId = 2,
        };
        var context = new World.CivSim.CivSimContext
        {
            Grid = grid,
            Seed = 7,
            Bands = new System.Collections.Generic.List<Band> { band },
            CellBands = new Band[n],
            NextBandId = 4,
            CultureKeyCount = 2,
            CultureGroupKeyCount = 1,
            ReligionKeyCount = 1,
            NextSettlementId = 9,
            Settlements = new System.Collections.Generic.List<Settlement>
            {
                new Settlement { Id = 8, Cell = 2, BornTick = 5, Level = 1, LastLevelUpTick = 5, DwellFrom = 5, OccupantId = 3, RuinFrom = -1, Stocks = World.CivSim.CommodityTable.NewStocks() },
            },
            Wars = new System.Collections.Generic.List<War>(),
            CellOwner = new int[n],
            CellBestOwner = new int[n],
            CellBestInf = new float[n],
            CellOwnerInf = new float[n],
        };
        for (int i = 0; i < n; i++) context.CellOwner[i] = -1;
        for (int i = 0; i < n; i++) context.CellBestOwner[i] = -1;
        return new World.CivSim.CivSimResult { Context = context, FinalTick = 100 };
    }

    [Test]
    public void CiviSegment_RoundtripsWithoutNATR()
    {
        var grid = MakeMinGrid();
        var civ = MakeMinCiv();

        var ms = new MemoryStream();
        var w = new ChunkWriter(ms, "MPA1", 8);
        // 模拟 .mpa v8：自然段（最小）+ CIVI 段（顺序流含文明全量，无内部子段表）
        w.BeginSegment("HEAD", 1);
        w.Store32((uint)grid.Seed);
        w.StoreFloat(grid.RadiusKm);
        w.Store32((uint)grid.N);
        w.EndSegment();
        w.BeginSegment("CIVI", 1);
        World.CivSim.CivMapArchive.WriteCivilization(w, civ);
        w.EndSegment();
        w.Finish();
        ms.Position = 0;

        // 读回：CIVI 段 → 文明结果（含重建派生场）；SeekSegment 后才能顺序读
        var r = new ChunkReader(ms);
        Assert.IsTrue(r.HasSegment("CIVI"));
        Assert.IsFalse(r.HasSegment("NOPE"));
        Assert.IsTrue(r.SeekSegment("CIVI"), "CIVI 可寻址");
        var back = World.CivSim.CivMapArchive.ReadCivilization(r, grid, out bool corrupted);
        Assert.IsFalse(corrupted, "CIVI 段解码不应报损坏");
        Assert.IsNotNull(back);
        Assert.AreEqual(100, back.FinalTick);
        Assert.AreEqual(7, back.Context.Seed);
        Assert.AreEqual(1, back.Context.Bands.Count);
        Assert.AreEqual(3, back.Context.Bands[0].Id);
        Assert.AreEqual(42f, back.Context.Bands[0].P);
        Assert.AreEqual(4, back.Context.NextBandId);
        Assert.AreEqual(2, back.Context.CultureKeyCount);
        Assert.AreEqual(9, back.Context.NextSettlementId);
        Assert.AreEqual(1, back.Context.Settlements.Count);
        Assert.AreEqual(8, back.Context.Settlements[0].Id);
        Assert.AreEqual(100, back.Context.Tick);   // 读档续跑从存档 tick 继续
    }

    [Test]
    public void CiviSegment_Missing_IsNoCiv()
    {
        // 纯自然 .mpa：无 CIVI 段 → HasSegment false（列表/查看器据此识别纯自然地图）
        var ms = WriteSample();
        var r = new ChunkReader(ms);
        Assert.IsFalse(r.HasSegment("CIVI"));
    }
}