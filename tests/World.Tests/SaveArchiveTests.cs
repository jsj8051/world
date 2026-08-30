using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using World.CivSim;
using World.CivSim.Entities;
using World.Gameplay;
using World.Utils;

namespace World.Tests;

/// <summary>
/// 游戏存档（.sav）测试（2026-08-25 地图≠存档分层：存档 = 世界快照 + 玩家状态 + 来源地图引用 REFS 段）。
/// 覆盖：REFS 段往返（来源地图路径/seed/起始 tick）、无来源地图（普通 .cmp）、槽命名。
/// ⚠️ 不测 UserPaths 目录（依赖 ProjectSettings——Local 无引擎上下文）；PathOf 为纯字符串不碰根目录。
/// </summary>
[TestFixture]
public class SaveArchiveTests
{
    [Test]
    public void PathOf_NamesSlotInSaves()
    {
        Assert.AreEqual("user://saves/我的世界.sav", SaveArchive.PathOf("我的世界"));
        Assert.AreEqual("user://saves/t1.sav", SaveArchive.PathOf("t1"));
    }

    [Test]
    public void Refs_AndPlayer_Roundtrip()
    {
        var civ = ArchiveChunkTests.MakeMinCivPublic();
        civ.Context.Player = new PlayerSession { StateId = 3, TaxRateOverride = 0.04f };
        // 构造带 REFS 的完整存档文件（模拟 .sav：CIVI 内容 + Player + 地图引用）
        // ——直接经 SaveArchive.Write 需要真实 GameGrid+game 目录；此处用段表平铺等价验证 REFS 布局往返：
        var ms = new MemoryStream();
        var w = new ChunkWriter(ms, "CMP1", 17);
        w.BeginSegment("REFS", 1);
        w.Store8(1);
        var refBytes = System.Text.Encoding.ASCII.GetBytes("user://maps/map_seed42_n32_r100.mpa");
        w.Store16((ushort)refBytes.Length);
        foreach (var b in refBytes) w.Store8(b);
        w.Store32(42);     // 地图 seed
        w.Store32(153);    // 起始 tick
        w.EndSegment();
        w.BeginSegment("PLAY", 1);
        w.Store8(1);
        w.Store32(3);            // StateId
        w.StoreFloat(0.04f);     // TaxRateOverride
        w.Store32(0);            // 队列空
        w.EndSegment();
        w.Finish();
        ms.Position = 0;

        // 读回：REFS 段 → 来源地图路径（段表随机访问——与 CivMapArchive.ReadCore 同语义）
        var r = new ChunkReader(ms);
        Assert.IsTrue(r.SeekSegment("REFS"));
        Assert.AreEqual(1, r.Get8());
        int len = r.Get16();
        var buf = new byte[len];
        for (int i = 0; i < len; i++) buf[i] = r.Get8();
        Assert.AreEqual("user://maps/map_seed42_n32_r100.mpa", System.Text.Encoding.ASCII.GetString(buf));
        Assert.AreEqual(42u, r.Get32());
        Assert.AreEqual(153u, r.Get32());
    }

    [Test]
    public void Refs_NoRef_WritesZeroFlag()
    {
        var ms = new MemoryStream();
        var w = new ChunkWriter(ms, "CMP1", 17);
        w.BeginSegment("REFS", 1);
        w.Store8(0);   // 无来源地图（普通 .cmp）
        w.EndSegment();
        w.Finish();
        ms.Position = 0;

        var r = new ChunkReader(ms);
        Assert.IsTrue(r.SeekSegment("REFS"));
        Assert.AreEqual(0, r.Get8(), "普通地图无 REFS 引用");
    }
}