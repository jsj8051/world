using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using World.Biome;
using World.HexPlanet;
using World.LogicGrid;
using World.MapGen;
using World.Services;
using World.Utils;
using IOFileAccess = System.IO.FileAccess;

using World.CivSim.Entities;
using World.CivSim.Events;
namespace World.CivSim;

/// <summary>
/// 游玩地图存档 .cmp v17（段表格式；2026-08-23 段表化 v15 → Phase 3 概念分组 v16 → 功能定性 v17）。
///
/// 布局（docs/存档段表格式设计.md §2/§3.2）：
///   [4B]  magic "CMP1"
///   [2B]  skeletonVer = 17
///   [2B]  reserved
///   [..]  数据区：
///     HEAD —— seed / finalTick / years / rngState / cultKey / cultgKey / relKey / nextPolityId
///     NATR —— GameMapArchive.WriteBody 全量（自然层快照，与 .gmp BODY 段布局一致，复用不变）
///     TRIB —— 实体段（alive count + 部落表，CivArchiveSchema 清单驱动）
///     LAND —— 土地挂钩：Cultivation n + CellOwner n + LockedUntil n
///     STTL —— 聚落段（NextHabitationId + count + Habitations[]）
///     WARS —— 战争段（Wars[]）
///   [12B×K] 段表 + [12B] 尾目录
///
/// 段缺失语义 = 该系统不存在（旧档无）→ 现场派生/空列表。旧格式 v1-v14 读分支于
/// 2026-08-23 删除（用户拍板：旧档全删，只支持段表格式；CompatibleArchiveVersions 移除）。
/// WildCrops 不存档（确定性重建：同 seed 同网格同结果）。
/// </summary>
/// <summary>存档版本分类：Current（本版）/ Older（版本过旧）/ Newer（版本过新）/ Unknown（读不出）。
/// 2026-08-23 段表化：Compatible 已移除（旧档全删，无兼容列表）。</summary>
public enum ArchiveVersionStatus { Unknown = 0, Current, Older, Newer }

public static class CivMapArchive
{
    public const string Magic = "CMP1";
    public const ushort Version = 17;   // v17：聚集地功能定性（2026-08-23——STTL 删 Level/LastLevelUpTick，加 3 职能条件字节；v16 及更早作废）
    private const int KeyMaxLen = 16;

    /// <summary>游戏版本号（project.godot application/config/version；仅供展示）。</summary>
    public static string GameVersion =>
        ProjectSettings.GetSetting("application/config/version", "0.0.0").AsString();

    /// <summary>版本分类：Current 可读；Older/Newer/Unknown 拒绝（菜单据此区分文案）。
    /// 2026-08-23 段表化：只有骨架版本 17 可读，其余一律拒绝（旧档全删——v16 及更早布局作废）。</summary>
    public static ArchiveVersionStatus ClassifyVersion(ushort ver)
    {
        if (ver == Version) return ArchiveVersionStatus.Current;
        if (ver == 0) return ArchiveVersionStatus.Unknown;
        return ver > Version ? ArchiveVersionStatus.Newer : ArchiveVersionStatus.Older;
    }

    /// <summary>顺序流 EVNT 哨兵（"EVNT" ASCII；旧档 WARS 后无此哨兵 → 空历史，读端回退不破坏流）。</summary>
    private const int EvntMagic = 0x45564E54;
    private const int StatMagic = 0x53544154;   // "STAT"——国家档案哨兵块（CIVI 顺序流尾部；旧档无 → 空档案）
    private const int PlayMagic = 0x504C4159;   // "PLAY"——玩家会话哨兵块（STAT 后；旧档无 → 纯自动模式）

    /// <summary>user:// 路径 → 绝对路径（System.IO 需要）。非 user:// 原样返回。</summary>
    private static string ResolvePath(string path) =>
        path.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(path)
            : path;

    // ── 文明段编解码（2026-08-23 单存档化：.mpa v8 CIVI 段顺序编码——外层已 BeginSegment("CIVI")，
    //    内部不用子段表：.mpa 自然层已有 HEAD 段，文明内部再用子段会重名冲突；且 ChunkWriter 禁嵌套段）──

    /// <summary>文明存档中间数据（顺序流解码结果；自然层网格由调用方提供
    /// ——.cmp 读档从 NATR 段、.mpa v8 读档用 GameGrid.FromMapData）。两种格式共用此包 → 共用 BuildResult 重建。</summary>
    private sealed class CivRawRecord
    {
        public int Seed;
        public int FinalTick;
        public int Years;
        public ulong RngState;
        public int CultureKeyCount, CultureGroupKeyCount, ReligionKeyCount, NextEntityId, NextHabitationId;
        public List<Polity> Entities;
        public Polity[] CellPolities;
        public float[] Cultivation;
        public int[] CellOwner;
        public int[] LockedUntil;
        public List<Habitation> Habitations;
        public List<War> Wars;
        public List<CivEventRecord> Events;   // 文明事件（哨兵块；旧档缺失 → 空历史）
        public List<StateEntity> States;      // 国家档案（哨兵块；旧档缺失 → 空列表 → 自愈建档）
        public World.Gameplay.PlayerSession Player;   // 玩家会话（哨兵块；旧档缺失 → null——纯自动模式）
    }

    /// <summary>用自然层网格 + 文明中间数据重建完整 CivSimContext（确定性派生态重建，
    /// 与 Run 结尾/Continue/原 .cmp 读档同式）。TechTable 须已 Load（调用方负责）。</summary>
    private static CivSimResult BuildResult(GameGrid g, CivRawRecord rec)
    {
        int n = g.N;
        // 文化 key 计数兜底：份额场推导（被同化掉的 key 可能使推导偏小，故取 max）
        int maxCultId = 0, maxGroupId = 0, maxReligId = 0;
        for (int k = 0; k < rec.Entities.Count; k++)
        {
            var e = rec.Entities[k];
            maxCultId = Math.Max(maxCultId, KeyNum(e.CultureShare[0].Key));
            maxCultId = Math.Max(maxCultId, KeyNum(e.CultureShare[1].Key));
            maxGroupId = Math.Max(maxGroupId, KeyNum(e.CultureGroupShare[0].Key));
            maxGroupId = Math.Max(maxGroupId, KeyNum(e.CultureGroupShare[1].Key));
            maxReligId = Math.Max(maxReligId, KeyNumRelig(e.ReligionCultShare[0].Key));
            maxReligId = Math.Max(maxReligId, KeyNumRelig(e.ReligionCultShare[1].Key));
        }
        var ctx = new CivSimContext
        {
            Grid = g,
            CellPolities = rec.CellPolities,
            Polities = rec.Entities,
            Seed = rec.Seed,
            OriginCount = 3,
            Tick = rec.FinalTick,          // 读档续跑从存档 tick 继续（T04 验证）
            Rng = rec.RngState != 0 ? new DeterministicRandom(rec.RngState) : new DeterministicRandom(rec.Seed),   // 状态恢复
            R = new float[n],
            CellF = new float[n],
            CellPop = new float[n],
            CellFarmPop = new float[n],
            BfsStamp = new int[n],
            BfsStampValue = 1,
            WildCrops = g.EnsureWildCrops(),
            Suit = WildCropsSystem.Suitability(g),
            FirstFarmTick = -1,
            CultureKeyCount = Math.Max(rec.CultureKeyCount, maxCultId + 1),   // 标签计数：存档合并值优先，份额推导兜底
            CultureGroupKeyCount = Math.Max(rec.CultureGroupKeyCount, maxGroupId + 1),
            ReligionKeyCount = Math.Max(rec.ReligionKeyCount, maxReligId + 1),
            NextPolityId = rec.NextEntityId,
            Habitations = rec.Habitations,
            NextHabitationId = rec.NextHabitationId,
            Wars = rec.Wars,
            Events = rec.Events ?? new List<CivEventRecord>(),
            States = rec.States ?? new List<StateEntity>(),
            Player = rec.Player,
            Cultivation = rec.Cultivation ?? new float[n],
            CellOwner = rec.CellOwner ?? EnumerableRepeat(-1, n),
            LockedUntil = rec.LockedUntil ?? EnumerableRepeat(0, n),
            CellBestOwner = EnumerableRepeat(-1, n),
            CellBestInf = new float[n],
            CellOwnerInf = new float[n],
        };
        ctx.EnsureTerritory();   // 惰性建领地索引
        CivEngine.BuildLayer1(ctx);   // 层1 空间生产力 R（确定性重建，不存档）
        CivEngine.SettleDerived(ctx);   // 边界态统一重建（唯一入口，与 Run 结尾/Continue 同式）
        return new CivSimResult { Context = ctx, FinalTick = rec.FinalTick };
    }

    /// <summary>读文明顺序流（HEAD 固定字段 + TRIB 实体 + 长度校验）→ 中间数据。
    /// LAND/STTL/WARS 由调用方按 n 另读（顺序流位置已推进到 TRIB 末尾，调用方继续）。</summary>
    private static CivRawRecord ReadRawRecord(ChunkReader r)
    {
        var rec = new CivRawRecord
        {
            Seed = (int)r.Get32(),
            FinalTick = (int)r.Get32(),
            Years = (int)r.Get32(),
            RngState = r.Get64(),
            CultureKeyCount = (int)r.Get32(),
            CultureGroupKeyCount = (int)r.Get32(),
            ReligionKeyCount = (int)r.Get32(),
            NextEntityId = (int)r.Get32(),
        };
        int count = (int)r.Get32();
        long remaining = r.Length - r.Position;
        if (count < 0 || count > remaining / 64) return null;
        var entities = new List<Polity>(count);
        for (int k = 0; k < count; k++)
        {
            var e = new Polity();
            foreach (var def in CivArchiveSchema.PolityFields)
            {
                if (def.Name == "TechKeys") { ReadTechKeys(r, e); continue; }
                def.Read(r, e);
            }
            entities.Add(e);
        }
        rec.Entities = entities;
        return rec;
    }

    /// <summary>从 .mpa v8 CIVI 段读文明结果（顺序流：HEAD 固定字段 + TRIB/LAND/STTL/WARS；
    /// 任何长度异常 → corrupted=true=该档文明段损坏）。grid 由 .mpa 读档侧用 GameGrid.FromMapData(natural)
    /// 提供（文明派生态重建需要真实自然层）。TechTable 须已 Load（调用方）。</summary>
    public static CivSimResult ReadCivilization(ChunkReader r, GameGrid grid, out bool corrupted)
    {
        corrupted = false;
        var rec = ReadRawRecord(r);
        if (rec == null) { corrupted = true; return null; }
        int n = grid.N;
        // LAND 顺序读（定长 n×（4+4+4）B；按 n 分配）
        rec.Cultivation = new float[n];
        for (int c = 0; c < n; c++) rec.Cultivation[c] = r.GetFloat();
        rec.CellOwner = new int[n];
        for (int c = 0; c < n; c++) rec.CellOwner[c] = (int)r.Get32();
        rec.LockedUntil = new int[n];
        for (int c = 0; c < n; c++) rec.LockedUntil[c] = (int)r.Get32();
        // STTL 顺序读
        rec.NextHabitationId = (int)r.Get32();
        int sCount = (int)r.Get32();
        if (sCount < 0 || sCount > (r.Length - r.Position) / 55) { corrupted = true; return null; }   // v17: 15B 核心+3 条件+Stocks24
        var habitations = new List<Habitation>(sCount);
        for (int k = 0; k < sCount; k++)
        {
            var s = new Habitation
            {
                Id = (int)r.Get32(),
                Cell = (int)r.Get32(),
                BornTick = (int)r.Get32(),
                DwellFrom = (int)r.Get32(),
                OccupantId = (int)r.Get32(),
                RuinFrom = (int)r.Get32(),
                HasAdmin = r.Get8() != 0,      // v17 职能条件（2026-08-23 功能定性）
                HasMarket = r.Get8() != 0,
                HasRitual = r.Get8() != 0,
            };
            s.Stocks = CommodityTable.NewStocks();
            for (int q = 0; q < CommodityTable.Count; q++) s.Stocks[q] = r.GetFloat();
            habitations.Add(s);
        }
        rec.Habitations = habitations;
        // WARS 顺序读
        int wCount = (int)r.Get32();
        if (wCount < 0 || wCount > 4096) { corrupted = true; return null; }
        var wars = new List<War>(wCount);
        for (int k = 0; k < wCount; k++)
        {
            var w = new War
            {
                StateIdA = (int)r.Get32(),
                StateIdB = (int)r.Get32(),
                Defender = (int)r.Get32(),
                StartTick = (int)r.Get32(),
                WinsA = (int)r.Get32(),
                WinsB = (int)r.Get32(),
                LastBattleTick = (int)r.Get32(),
                TributeTo = (int)r.Get32(),
                TributeFrom = (int)r.Get32(),
                TributesLeft = (int)r.Get32(),
            };
            wars.Add(w);
        }
        rec.Wars = wars;
        // ── EVNT（哨兵块；旧档 WARS 后无哨兵 → 回退，空历史不阻断）──
        var events = new List<CivEventRecord>();
        if (r.Length - r.Position >= 4)
        {
            long mark = r.Position;
            if ((int)r.Get32() == EvntMagic)
            {
                int evCount = (int)r.Get32();
                if (evCount < 0 || evCount > (r.Length - r.Position) / 16) { corrupted = true; return null; }
                for (int k = 0; k < evCount; k++)
                    events.Add(new CivEventRecord((int)r.Get32(), r.Get16(), (int)r.Get32(), (int)r.Get32(), r.GetFloat()));
            }
            else
                r.Seek(mark);   // 旧档：非哨兵 → 回退到段尾（流结束语义）
        }
        rec.Events = events;
        // ── STAT（哨兵块；旧档无 → 空档案，机制下 tick 自愈建档——国库从 0 起）──
        var states = new List<StateEntity>();
        if (r.Length - r.Position >= 4)
        {
            long mark = r.Position;
            if ((int)r.Get32() == StatMagic)
            {
                int stCount = (int)r.Get32();
                if (stCount < 0 || stCount > (r.Length - r.Position) / 24) { corrupted = true; return null; }
                for (int k = 0; k < stCount; k++)
                    states.Add(new StateEntity
                    {
                        Id = (int)r.Get32(),
                        CapitalHabId = (int)r.Get32(),
                        MonarchId = (int)r.Get32(),
                        Treasury = r.GetFloat(),
                        Stability = r.GetFloat(),
                        Legitimacy = r.GetFloat(),
                        BornTick = (int)r.Get32(),
                    });
            }
            else
                r.Seek(mark);   // 旧档：非哨兵 → 回退（流结束语义）
        }
        rec.States = states;
        // ── PLAY（哨兵块；旧档无 → 纯自动模式——玩家绑定/命令保留仅对 .cmp 有意义的整体一致性）──
        World.Gameplay.PlayerSession player = null;
        if (r.Length - r.Position >= 4)
        {
            long mark = r.Position;
            if ((int)r.Get32() == PlayMagic)
            {
                if (r.Get8() != 0)
                {
                    player = new World.Gameplay.PlayerSession
                    {
                        StateId = (int)r.Get32(),
                        TaxRateOverride = r.GetFloat(),
                    };
                    int cmdCount = (int)r.Get32();
                    if (cmdCount < 0 || cmdCount > (r.Length - r.Position) / 16) { corrupted = true; return null; }
                    for (int q = 0; q < cmdCount; q++)
                        player.Queue.Add(new World.Gameplay.PlayerCommand
                        {
                            Kind = (World.Gameplay.PlayerCommandKind)r.Get8(),
                            TargetA = (int)r.Get32(),
                            TargetB = (int)r.Get32(),
                            Value = r.GetFloat(),
                            IssuedTick = (int)r.Get32(),
                        });
                }
            }
            else
                r.Seek(mark);   // 旧档：非哨兵 → 回退（流结束语义）
        }
        rec.Player = player;
        // cellPolities 按 n 重映射（一格一实体）
        rec.CellPolities = new Polity[n];
        for (int i = 0; i < n; i++) rec.CellPolities[i] = null;
        for (int k = 0; k < rec.Entities.Count; k++)
        {
            var e = rec.Entities[k];
            if (e.Cell >= 0 && e.Cell < n) rec.CellPolities[e.Cell] = e;
        }
        return BuildResult(grid, rec);
    }

    /// <summary>写文明顺序流到当前段（HEAD 固定字段 + TRIB/LAND/STTL/WARS 顺序编码；
    /// 调用方已 BeginSegment——.cmp 顶层段 or .mpa v8 CIVI 段）。CivArchiveSchema 清单驱动防漏字段。log 由调用方打。</summary>
    public static void WriteCivilization(ChunkWriter w, CivSimResult result)
    {
        // ⚠️ 清单自检（与 .cmp Write 同源）：字段改名/删除后清单过期 → 拒绝写，防静默漏字段
        if (!CivArchiveSchema.Validate()) return;
        var ctx = result.Context;
        int alive = 0;
        for (int k = 0; k < ctx.Polities.Count; k++) if (!ctx.Polities[k].Dead) alive++;
        // ── HEAD 固定字段 ──
        w.Store32((uint)ctx.Seed);
        w.Store32((uint)result.FinalTick);
        w.Store32((uint)(result.FinalTick * CivSimContext.TickYears));
        w.Store64((ctx.Rng as DeterministicRandom)?.State ?? (ulong)ctx.Seed);   // Rng 状态
        w.Store32((uint)ctx.CultureKeyCount);
        w.Store32((uint)ctx.CultureGroupKeyCount);
        w.Store32((uint)ctx.ReligionKeyCount);
        w.Store32((uint)ctx.NextPolityId);
        // ── TRIB ──
        w.Store32((uint)alive);
        foreach (var e in ctx.Polities)
        {
            if (e.Dead) continue;
            foreach (var def in CivArchiveSchema.PolityFields)
            {
                if (def.Name == "TechKeys") { StoreTechKeys(w, e); continue; }
                def.Write(w, e);
            }
        }
        // ── LAND ──
        for (int c = 0; c < ctx.Grid.N; c++) w.StoreFloat(ctx.Cultivation != null ? ctx.Cultivation[c] : 0f);
        for (int c = 0; c < ctx.Grid.N; c++) w.Store32((uint)ctx.CellOwner[c]);
        for (int c = 0; c < ctx.Grid.N; c++) w.Store32(ctx.LockedUntil != null && ctx.LockedUntil[c] > 0 ? (uint)ctx.LockedUntil[c] : 0u);
        // ── STTL ──
        w.Store32((uint)ctx.NextHabitationId);
        w.Store32((uint)ctx.Habitations.Count);
        foreach (var s in ctx.Habitations)
        {
            w.Store32((uint)s.Id);
            w.Store32((uint)s.Cell);
            w.Store32((uint)s.BornTick);
            w.Store32((uint)s.DwellFrom);
            w.Store32((uint)s.OccupantId);   // -1 → uint 全 1（读回 (int) 还原）
            w.Store32((uint)s.RuinFrom);
            w.Store8((byte)(s.HasAdmin ? 1 : 0));   // v17 职能条件（2026-08-23 功能定性）
            w.Store8((byte)(s.HasMarket ? 1 : 0));
            w.Store8((byte)(s.HasRitual ? 1 : 0));
            for (int k = 0; k < CommodityTable.Count; k++)
                w.StoreFloat(s.Stocks != null && k < s.Stocks.Length ? s.Stocks[k] : 0f);
        }
        // ── WARS ──
        w.Store32((uint)ctx.Wars.Count);
        for (int k = 0; k < ctx.Wars.Count; k++)
        {
            var war = ctx.Wars[k];
            w.Store32((uint)war.StateIdA);
            w.Store32((uint)war.StateIdB);
            w.Store32((uint)war.Defender);        // -1 → uint 全 1
            w.Store32((uint)war.StartTick);
            w.Store32((uint)war.WinsA);
            w.Store32((uint)war.WinsB);
            w.Store32((uint)war.LastBattleTick);
            w.Store32((uint)war.TributeTo);       // -1 = 交战中
            w.Store32((uint)war.TributeFrom);
            w.Store32((uint)war.TributesLeft);
        }
        // ── EVNT（顺序流尾部哨兵块——2026-08-24 ⑪；旧档无哨兵 → 读端回退为空历史）──
        w.Store32(EvntMagic);
        var events = ctx.Events;
        w.Store32((uint)(events?.Count ?? 0));
        if (events != null)
            foreach (var ev in events)
            {
                w.Store32((uint)ev.Tick);
                w.Store16((ushort)ev.TypeIndex);
                w.Store32((uint)ev.SubjectId);
                w.Store32((uint)ev.TargetId);
                w.StoreFloat(ev.Value);
            }
        // ── STAT（哨兵块——同 EVNT；旧档无哨兵 → 读端回退空档案，自愈建档）──
        w.Store32(StatMagic);
        var states = ctx.States;
        w.Store32((uint)(states?.Count ?? 0));
        if (states != null)
            foreach (var st in states)
            {
                w.Store32((uint)st.Id);
                w.Store32((uint)st.CapitalHabId);   // -1 → uint 全 1
                w.Store32((uint)st.MonarchId);
                w.StoreFloat(st.Treasury);
                w.StoreFloat(st.Stability);
                w.StoreFloat(st.Legitimacy);
                w.Store32((uint)st.BornTick);
            }
        // ── PLAY（哨兵块——同 STAT；旧档无哨兵 → 读端回退纯自动模式）──
        w.Store32(PlayMagic);
        var player = ctx.Player;
        if (player == null || !player.IsBound)
        {
            w.Store8(0);
        }
        else
        {
            w.Store8(1);
            w.Store32((uint)player.StateId);
            w.StoreFloat(player.TaxRateOverride);
            w.Store32((uint)player.Queue.Count);
            for (int q = 0; q < player.Queue.Count; q++)
            {
                var cmd = player.Queue[q];
                w.Store8((byte)cmd.Kind);
                w.Store32((uint)cmd.TargetA);
                w.Store32((uint)cmd.TargetB);
                w.StoreFloat(cmd.Value);
                w.Store32((uint)cmd.IssuedTick);
            }
        }
    }

    /// <summary>写 .cmp（v17 段表：HEAD/NATR/TRIB/LAND/STTL/WARS 六段）。log=false：后台线程（禁 GD.Print）。</summary>
    public static bool Write(string path, GameGrid grid, CivSimResult result, bool log = true)
    {
        // ⚠️ 2026-08-18 阶段3：清单自检（字段改名/删除后清单过期 → 写档拒绝，防静默漏字段）
        if (!CivArchiveSchema.Validate()) return false;
        var ctx = result.Context;
        int alive = 0;
        for (int k = 0; k < ctx.Polities.Count; k++) if (!ctx.Polities[k].Dead) alive++;
        try
        {
            string abs = ResolvePath(path);
            string dir = Path.GetDirectoryName(abs) ?? "";
            if (dir.Length > 0 && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            using var fs = new FileStream(abs, FileMode.Create, IOFileAccess.Write);
            using var w = new ChunkWriter(fs, Magic, Version);

            // ── HEAD：固定字段 ──
            w.BeginSegment("HEAD", 1);
            w.Store32((uint)ctx.Seed);
            w.Store32((uint)result.FinalTick);
            w.Store32((uint)(result.FinalTick * CivSimContext.TickYears));
            w.Store64((ctx.Rng as DeterministicRandom)?.State ?? (ulong)ctx.Seed);   // Rng 状态（读档续跑无分叉）
            w.Store32((uint)ctx.CultureKeyCount);   // 文化 key 计数（分裂分化接续 key 空间；推导不可靠——被同化掉的 key 不在份额场）
            w.Store32((uint)ctx.CultureGroupKeyCount);   // 文化群 key 计数（v6 独立入档：读档续跑群漂变无分叉）
            w.Store32((uint)ctx.ReligionKeyCount);  // 宗教派别 key 计数
            w.Store32((uint)ctx.NextPolityId);      // 实体 Id 计数器（v8：存档只存活实体，Count 读档分叉）
            w.EndSegment();

            // ── NATR：自然层快照（GameMapArchive 布局单源）──
            w.BeginSegment("NATR", 1);
            GameMapArchive.WriteBody(w, grid);
            w.EndSegment();

            // ── TRIB：实体段（CivArchiveSchema 清单驱动，单源防漏字段）──
            w.BeginSegment("TRIB", 1);   // 段名 TRIB = Tribe 遗产名（存档格式标识，不改——改动需 bump 版本）
            w.Store32((uint)alive);
            foreach (var e in ctx.Polities)
            {
                if (e.Dead) continue;
                foreach (var def in CivArchiveSchema.PolityFields)
                {
                    if (def.Name == "TechKeys") { StoreTechKeys(w, e); continue; }
                    def.Write(w, e);
                }
            }
            w.EndSegment();

            // ── LAND：土地挂钩（开垦率场 + 格归属 + 实控锁定）──
            w.BeginSegment("LAND", 1);
            for (int c = 0; c < ctx.Grid.N; c++) w.StoreFloat(ctx.Cultivation != null ? ctx.Cultivation[c] : 0f);
            for (int c = 0; c < ctx.Grid.N; c++) w.Store32((uint)ctx.CellOwner[c]);
            for (int c = 0; c < ctx.Grid.N; c++) w.Store32(ctx.LockedUntil != null && ctx.LockedUntil[c] > 0 ? (uint)ctx.LockedUntil[c] : 0u);
            w.EndSegment();

            // ── STTL：聚落段 ──
            w.BeginSegment("STTL", 1);
            w.Store32((uint)ctx.NextHabitationId);
            w.Store32((uint)ctx.Habitations.Count);
            foreach (var s in ctx.Habitations)
            {
                w.Store32((uint)s.Id);
                w.Store32((uint)s.Cell);
                w.Store32((uint)s.BornTick);
                w.Store32((uint)s.DwellFrom);
                w.Store32((uint)s.OccupantId);   // -1 → uint 全 1（读回 (int) 还原）
                w.Store32((uint)s.RuinFrom);
                w.Store8((byte)(s.HasAdmin ? 1 : 0));   // v17 职能条件（2026-08-23 功能定性）
                w.Store8((byte)(s.HasMarket ? 1 : 0));
                w.Store8((byte)(s.HasRitual ? 1 : 0));
                for (int k = 0; k < CommodityTable.Count; k++)
                    w.StoreFloat(s.Stocks != null && k < s.Stocks.Length ? s.Stocks[k] : 0f);
            }
            w.EndSegment();

            // ── WARS：战争段（过程状态不可派生重建，读档必须恢复原样）──
            w.BeginSegment("WARS", 1);
            w.Store32((uint)ctx.Wars.Count);
            for (int k = 0; k < ctx.Wars.Count; k++)
            {
                var war = ctx.Wars[k];
                w.Store32((uint)war.StateIdA);
                w.Store32((uint)war.StateIdB);
                w.Store32((uint)war.Defender);        // -1 → uint 全 1（读回 (int) 还原）
                w.Store32((uint)war.StartTick);
                w.Store32((uint)war.WinsA);
                w.Store32((uint)war.WinsB);
                w.Store32((uint)war.LastBattleTick);
                w.Store32((uint)war.TributeTo);       // -1 = 交战中
                w.Store32((uint)war.TributeFrom);
                w.Store32((uint)war.TributesLeft);
            }
            w.EndSegment();

            // ── EVNT：文明事件段（2026-08-24 ⑪——一系统一段；段缺失 = 无历史，读端兜底为空）──
            w.BeginSegment("EVNT", 1);
            var events = ctx.Events;
            w.Store32((uint)(events?.Count ?? 0));
            if (events != null)
                foreach (var ev in events)
                {
                    w.Store32((uint)ev.Tick);
                    w.Store16((ushort)ev.TypeIndex);   // 短整型——注册表序入档，不存字符串
                    w.Store32((uint)ev.SubjectId);
                    w.Store32((uint)ev.TargetId);
                    w.StoreFloat(ev.Value);
                }
            w.EndSegment();

            // ── STAT：国家档案段（2026-08-25 通用国家机制——EU4 式制度层持久状态；段缺失 = 空 → 自愈重建）──
            w.BeginSegment("STAT", 1);
            w.Store32((uint)(ctx.States?.Count ?? 0));
            var stList = ctx.States;
            if (stList != null)
                for (int k = 0; k < stList.Count; k++)
                {
                    var st = stList[k];
                    w.Store32((uint)st.Id);
                    w.Store32((uint)st.CapitalHabId);   // -1 → uint 全 1（读回 (int) 还原）
                    w.Store32((uint)st.MonarchId);
                    w.StoreFloat(st.Treasury);
                    w.StoreFloat(st.Stability);
                    w.StoreFloat(st.Legitimacy);
                    w.Store32((uint)st.BornTick);
                }
            w.EndSegment();

            // ── PLAY：玩家会话段（2026-08-25 第二阶段"实际游玩"——玩家绑定国家 + 待处理命令队列）──
            // 0 = 无玩家（纯自动存档）；1 = 有玩家（StateId/税率覆盖/命令队列入档——读档续玩不丢）。
            w.BeginSegment("PLAY", 1);
            var player = ctx.Player;
            if (player == null || !player.IsBound)
            {
                w.Store8(0);
            }
            else
            {
                w.Store8(1);
                w.Store32((uint)player.StateId);
                w.StoreFloat(player.TaxRateOverride);   // -1 = 未设置（默认税率）
                w.Store32((uint)player.Queue.Count);
                for (int q = 0; q < player.Queue.Count; q++)
                {
                    var cmd = player.Queue[q];
                    w.Store8((byte)cmd.Kind);
                    w.Store32((uint)cmd.TargetA);       // -1 → uint 全 1（读回 (int) 还原）
                    w.Store32((uint)cmd.TargetB);
                    w.StoreFloat(cmd.Value);
                    w.Store32((uint)cmd.IssuedTick);
                }
            }
            w.EndSegment();

            w.Finish();
        }
        catch (Exception ex)
        {
            LogService.LogErr("CivMapArchive", $"写入失败 {path}: {ex.Message}");
            return false;
        }
        if (log)
            LogService.Log("CivMapArchive", $"wrote v{Version} {path} (ticks={result.FinalTick} " +
                     $"entities={alive} pop={ctx.TotalPopulation():F0} farm={CountFarming(ctx)} fission={ctx.Fissions} migrate={ctx.Migrations}" +
                     $" habitations={ctx.Habitations.Count} wars={ctx.Wars.Count})");
        return true;
    }

    private static void StoreKey(ChunkWriter f, string key)
    {
        var bytes = Encoding.ASCII.GetBytes(key);
        int n = Mathf.Min(bytes.Length, KeyMaxLen);
        for (int i = 0; i < n; i++) f.Store8(bytes[i]);
        for (int i = n; i < KeyMaxLen; i++) f.Store8(0);
    }

    // ── 2026-08-18 阶段3：CivArchiveSchema 清单委托实现（Write/Read 由表驱动，布局严格对齐）──

    private static void StoreTechKeys(ChunkWriter f, Polity e)
    {
        f.Store16((ushort)e.TechKeys.Count);
        foreach (var key in e.TechKeys)
            StoreKey(f, key);
    }
    private static bool ReadTechKeys(ChunkReader f, Polity e)
    {
        int keyCount = f.Get16();
        for (int q = 0; q < keyCount; q++)
        {
            var kb = ReadBytes(f, KeyMaxLen);
            int len = 0;
            while (len < kb.Length && kb[len] != 0) len++;
            if (len > 0) e.TechKeys.Add(Encoding.ASCII.GetString(kb, 0, len));
        }
        return true;
    }

    internal static void StoreReligionShare(ChunkWriter f, Polity e)
    {
        foreach (var s in e.ReligionShare) f.Store8(s.Frac);   // 固定 key 表 → 只存份额 5B
    }
    internal static bool ReadReligionShare(ChunkReader f, Polity e)
    {
        e.ReligionShare = ShareField.NewReligion(ReligionStage.Animism);   // 固定 key 重建，只读份额
        for (int q2 = 0; q2 < ReligionStage.Count; q2++) e.ReligionShare[q2].Frac = f.Get8();
        return true;
    }
    internal static void StoreStocks(ChunkWriter f, Polity e)
    {
        // 字节序 = CommodityTable.All 顺序（grain/berry/meat/leather/wool/straw）
        if (e.Stocks == null || e.Stocks.Length != CommodityTable.Count) e.Stocks = CommodityTable.NewStocks();
        for (int s = 0; s < CommodityTable.Count; s++) f.StoreFloat(e.Stocks[s]);
    }
    internal static bool ReadStocks(ChunkReader f, Polity e)
    {
        e.Stocks = CommodityTable.NewStocks();
        for (int s = 0; s < CommodityTable.Count; s++) e.Stocks[s] = f.GetFloat();
        return true;
    }

    /// <summary>份额场序列化：(key 定长 16B + 份额 1B)×2。null key → 全 0。
    /// 2026-08-18 阶段3：internal 供 CivArchiveSchema 清单委托调用。</summary>
    internal static void StoreShare(ChunkWriter f, ShareEntry[] s)
    {
        for (int i = 0; i < 2; i++)
        {
            StoreKey(f, s[i].Key ?? "");
            f.Store8(s[i].Frac);
        }
    }

    internal static ShareEntry[] ReadShare(ChunkReader f)
    {
        var r = new[] { new ShareEntry(), new ShareEntry() };
        for (int i = 0; i < 2; i++)
        {
            var kb = ReadBytes(f, KeyMaxLen);
            int len = 0;
            while (len < kb.Length && kb[len] != 0) len++;
            r[i].Key = len > 0 ? Encoding.ASCII.GetString(kb, 0, len) : null;
            r[i].Frac = f.Get8();
        }
        return r;
    }

    /// <summary>解析 "cult_N" / "cultg_N" → N（非该格式 → 0；用于 key 计数兜底推导，2026-08-07 双前缀兼容旧档）。</summary>
    private static int KeyNum(string key)
    {
        if (key != null && key.StartsWith("cult_") && int.TryParse(key.AsSpan(5), out int n))
            return n;
        if (key != null && key.StartsWith("cultg_") && int.TryParse(key.AsSpan(6), out int m))
            return m;
        return 0;
    }

    /// <summary>解析 "relig_N" → N（宗教派别 key 计数兜底）。</summary>
    private static int KeyNumRelig(string key)
    {
        if (key != null && key.StartsWith("relig_") && int.TryParse(key.AsSpan(6), out int n))
            return n;
        return 0;
    }

    private static int CountFarming(CivSimContext ctx)
    {
        int c = 0;
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (!ctx.Polities[i].Dead && ctx.Polities[i].IsFarming) c++;
        return c;
    }

    /// <summary>读 .cmp → （自然层 GameGrid + 文明结果）。v17 段表：HEAD/NATR/TRIB/LAND/STTL/WARS。
    /// ⚠️ 2026-08-07：读档入口必须 TechTable.Load()——否则 _byKey 空 → 读档后 RefreshCellState/YFarm
    /// 里 Get(key) 全 null → NRE（CmpSelectMenu 只 Read 不 Run 的场景崩溃根因）。Load 幂等。</summary>
    public static bool Read(string path, out GameGrid grid, out CivSimResult result)
    {
        TechTable.Load();
        grid = null;
        result = null;
        try
        {
            string abs = ResolvePath(path);
            using var fs = new FileStream(abs, FileMode.Open, IOFileAccess.Read);
            using var r = new ChunkReader(fs);
            if (r.Magic != Magic)
            {
                LogService.LogErr("CivMapArchive", $"bad magic in {path}");
                return false;
            }
            ushort ver = r.SkeletonVer;
            switch (ClassifyVersion(ver))
            {
                case ArchiveVersionStatus.Newer:
                    LogService.LogErr("CivMapArchive", $"unsupported version {ver} in {path} (need ≤{Version})");
                    return false;
                case ArchiveVersionStatus.Older:
                case ArchiveVersionStatus.Unknown:
                    LogService.LogErr("CivMapArchive", $"old version {ver} in {path}（旧档已放弃，请重新演化生成 v{Version}）");
                    return false;
            }

            // ── HEAD ──
            if (!r.SeekSegment("HEAD"))
            {
                LogService.LogErr("CivMapArchive", $"{path}: 缺 HEAD 段");
                return false;
            }
            int seed = (int)r.Get32();
            int finalTick = (int)r.Get32();
            int years = (int)r.Get32();
            ulong rngState = r.Get64();   // Rng 状态（0=旧档无状态，用 seed 重建）
            int cultureKeyCount = (int)r.Get32();   // 文化 key 计数（读档续跑接续 key 空间）
            int cultureGroupKeyCount = (int)r.Get32();   // 文化群 key 计数（读档续跑群漂变无分叉）
            int religionKeyCount = (int)r.Get32();  // 宗教派别 key 计数
            int nextEntityId = (int)r.Get32();   // 实体 Id 计数器（读档续跑 Id 分配无分叉）

            // ── NATR：自然层 ──
            var g = new GameGrid();
            if (!r.SeekSegment("NATR"))
            {
                LogService.LogErr("CivMapArchive", $"{path}: 缺 NATR 段");
                return false;
            }
            if (!GameMapArchive.ReadBody(r, g))
                return false;   // 结构校验失败（正文错位/损坏）已在内部打印
            int n = g.N;

            // ── TRIB：实体段 ──
            if (!r.SeekSegment("TRIB"))
            {
                LogService.LogErr("CivMapArchive", $"{path}: 缺 TRIB 段");
                return false;
            }
            int count = (int)r.Get32();
            // ⚠️ 2026-08-07：实体表长度分配前校验——count 是正文错位后最易读爆的字段
            //   （map_seed42_n16 等旧中间态档 count=11.7 亿 → new List<Polity>(count) ≈ 9.4GB）。
            //   单实体最小 ~79B，用 64B 保守下界；剩余文件字节数都不够 → 必为错位垃圾。
            long remaining = r.Length - r.Position;
            if (count < 0 || count > remaining / 64)
            {
                LogService.LogErr("CivMapArchive", $"{path} 实体表长度异常：count={count}，剩余 {remaining}B（最小实体 64B 装不下）——正文错位或损坏，请重新生成。");
                return false;
            }
            var entities = new List<Polity>(count);
            var cellPolities = new Polity[n];
            for (int i = 0; i < n; i++) cellPolities[i] = null;
            for (int k = 0; k < count; k++)
            {
                var e = new Polity();
                // 实体段由 CivArchiveSchema 清单驱动（单源）；TechKeys 变长特例内联
                foreach (var def in CivArchiveSchema.PolityFields)
                {
                    if (def.Name == "TechKeys") { ReadTechKeys(r, e); continue; }
                    def.Read(r, e);
                }
                entities.Add(e);
                if (e.Cell >= 0 && e.Cell < n) cellPolities[e.Cell] = e;   // 一格一实体
            }

            // ── LAND：土地挂钩（开垦率场 + 格归属 + 实控锁定）──
            float[] cultivation = null;
            int[] cellOwner = null;
            int[] lockedUntil = null;
            if (r.SeekSegment("LAND"))
            {
                cultivation = new float[n];
                for (int c = 0; c < n; c++) cultivation[c] = r.GetFloat();
                cellOwner = new int[n];
                for (int c = 0; c < n; c++) cellOwner[c] = (int)r.Get32();
                lockedUntil = new int[n];
                for (int c = 0; c < n; c++) lockedUntil[c] = (int)r.Get32();
            }

            // ── STTL：聚落段（段缺失 = 旧档无聚落 → 空列表）──
            var habitations = new List<Habitation>();
            int nextHabitationId = 0;
            if (r.SeekSegment("STTL"))
            {
                nextHabitationId = (int)r.Get32();
                int sCount = (int)r.Get32();
                // 长度校验（同实体表：最小聚落 ~56B；用 48B 保守下界防错位读爆）
                long sRemaining = r.Length - r.Position;
                if (sCount < 0 || sCount > sRemaining / 48)
                {
                    LogService.LogErr("CivMapArchive", $"{path} 聚落段长度异常：count={sCount}，剩余 {sRemaining}B——正文错位或损坏。");
                    return false;
                }
                for (int k = 0; k < sCount; k++)
                {
                    var s = new Habitation
                    {
                        Id = (int)r.Get32(),
                        Cell = (int)r.Get32(),
                        BornTick = (int)r.Get32(),
                        DwellFrom = (int)r.Get32(),
                        OccupantId = (int)r.Get32(),
                        RuinFrom = (int)r.Get32(),
                        HasAdmin = r.Get8() != 0,      // v17 职能条件（2026-08-23 功能定性）
                        HasMarket = r.Get8() != 0,
                        HasRitual = r.Get8() != 0,
                    };
                    s.Stocks = CommodityTable.NewStocks();
                    for (int q = 0; q < CommodityTable.Count; q++) s.Stocks[q] = r.GetFloat();
                    habitations.Add(s);
                }
            }

            // ── WARS：战争段（段缺失 = 旧档无战争 → 空列表）──
            var wars = new List<War>();
            if (r.SeekSegment("WARS"))
            {
                int wCount = (int)r.Get32();
                if (wCount < 0 || wCount > 4096)
                {
                    LogService.LogErr("CivMapArchive", $"{path} 战争段长度异常：count={wCount}——正文错位或损坏。");
                    return false;
                }
                for (int k = 0; k < wCount; k++)
                {
                    var w = new War
                    {
                        StateIdA = (int)r.Get32(),
                        StateIdB = (int)r.Get32(),
                        Defender = (int)r.Get32(),
                        StartTick = (int)r.Get32(),
                        WinsA = (int)r.Get32(),
                        WinsB = (int)r.Get32(),
                        LastBattleTick = (int)r.Get32(),
                        TributeTo = (int)r.Get32(),
                        TributeFrom = (int)r.Get32(),
                        TributesLeft = (int)r.Get32(),
                    };
                    wars.Add(w);
                }
            }

            // ── EVNT：文明事件段（段缺失 = 旧档无事件 → 空历史，不阻断——同 STTL/WARS 缺段惯例）──
            var events = new List<CivEventRecord>();
            if (r.SeekSegment("EVNT"))
            {
                int evCount = (int)r.Get32();
                // 长度校验（同实体段：单记录 18B，用 16B 保守下界——防正文错位读爆）
                long evRemaining = r.Length - r.Position;
                if (evCount < 0 || evCount > evRemaining / 16)
                {
                    LogService.LogErr("CivMapArchive", $"{path} 事件段长度异常：count={evCount}——正文错位或损坏。");
                    return false;
                }
                for (int k = 0; k < evCount; k++)
                    events.Add(new CivEventRecord(
                        (int)r.Get32(), r.Get16(),
                        (int)r.Get32(), (int)r.Get32(), r.GetFloat()));
            }

            // ── STAT：国家档案段（段缺失 = 旧档无国家档案 → 空列表，机制下 tick 自愈建档）──
            var states = new List<StateEntity>();
            if (r.SeekSegment("STAT"))
            {
                int stCount = (int)r.Get32();
                // 长度校验（单记录 28B：4×u32 + 3×f32；用 24B 保守下界防错位读爆）
                long stRemaining = r.Length - r.Position;
                if (stCount < 0 || stCount > stRemaining / 24)
                {
                    LogService.LogErr("CivMapArchive", $"{path} 国家档案段长度异常：count={stCount}——正文错位或损坏。");
                    return false;
                }
                for (int k = 0; k < stCount; k++)
                {
                    var st = new StateEntity
                    {
                        Id = (int)r.Get32(),
                        CapitalHabId = (int)r.Get32(),
                        MonarchId = (int)r.Get32(),
                        Treasury = r.GetFloat(),
                        Stability = r.GetFloat(),
                        Legitimacy = r.GetFloat(),
                        BornTick = (int)r.Get32(),
                    };
                    states.Add(st);
                }
            }

            // ── PLAY：玩家会话段（段缺失/无玩家 = 纯自动模式 → null）──
            World.Gameplay.PlayerSession player = null;
            if (r.SeekSegment("PLAY"))
            {
                if (r.Get8() != 0)
                {
                    player = new World.Gameplay.PlayerSession
                    {
                        StateId = (int)r.Get32(),
                        TaxRateOverride = r.GetFloat(),
                    };
                    int cmdCount = (int)r.Get32();
                    // 长度校验（单命令 17B：1×u8 + 3×u32 + 1×f32；用 16B 保守下界防错位读爆）
                    long cmdRemaining = r.Length - r.Position;
                    if (cmdCount < 0 || cmdCount > cmdRemaining / 16)
                    {
                        LogService.LogErr("CivMapArchive", $"{path} 玩家命令队列长度异常：count={cmdCount}——正文错位或损坏。");
                        return false;
                    }
                    for (int q = 0; q < cmdCount; q++)
                        player.Queue.Add(new World.Gameplay.PlayerCommand
                        {
                            Kind = (World.Gameplay.PlayerCommandKind)r.Get8(),
                            TargetA = (int)r.Get32(),
                            TargetB = (int)r.Get32(),
                            Value = r.GetFloat(),
                            IssuedTick = (int)r.Get32(),
                        });
                }
            }

            // 文化 key 计数兜底：份额场推导（被同化掉的 key 可能使推导偏小，故取 max）
            int maxCultId = 0, maxGroupId = 0, maxReligId = 0;
            for (int k = 0; k < entities.Count; k++)
            {
                var e = entities[k];
                maxCultId = Math.Max(maxCultId, KeyNum(e.CultureShare[0].Key));
                maxCultId = Math.Max(maxCultId, KeyNum(e.CultureShare[1].Key));
                maxGroupId = Math.Max(maxGroupId, KeyNum(e.CultureGroupShare[0].Key));
                maxGroupId = Math.Max(maxGroupId, KeyNum(e.CultureGroupShare[1].Key));
                maxReligId = Math.Max(maxReligId, KeyNumRelig(e.ReligionCultShare[0].Key));
                maxReligId = Math.Max(maxReligId, KeyNumRelig(e.ReligionCultShare[1].Key));
            }

            var ctx = new CivSimContext
            {
                Grid = g,
                CellPolities = cellPolities,
                Polities = entities,
                Seed = seed,
                OriginCount = 3,
                Tick = finalTick,          // 读档续跑从存档 tick 继续（T04 验证）
                Rng = rngState != 0 ? new DeterministicRandom(rngState) : new DeterministicRandom(seed),   // 状态恢复：随机序列与从头跑对齐
                R = new float[n],
                CellF = new float[n],
                CellPop = new float[n],
                CellFarmPop = new float[n],
                BfsStamp = new int[n],
                BfsStampValue = 1,
                WildCrops = g.EnsureWildCrops(),
                Suit = WildCropsSystem.Suitability(g),
                FirstFarmTick = -1,
                CultureKeyCount = Math.Max(cultureKeyCount, maxCultId + 1),   // 标签计数：存档合并值优先，标签份额推导兜底
                CultureGroupKeyCount = Math.Max(cultureGroupKeyCount, maxGroupId + 1),  // 群计数
                ReligionKeyCount = Math.Max(religionKeyCount, maxReligId + 1),
                NextPolityId = nextEntityId,   // 实体 Id 计数器（读档续跑 Id 分配无分叉）
                Habitations = habitations,    // 聚落段（段缺失 → 空列表）
                NextHabitationId = nextHabitationId,
                Wars = wars,                  // 战争段（段缺失 → 空列表——无战争起步）
                Events = events,              // 文明事件段（段缺失 → 空历史——旁路记录，不阻断）
                States = states,              // 国家档案段（段缺失 → 空列表——机制下 tick 自愈建档）
                Player = player,              // 玩家会话段（段缺失/无玩家 → null——纯自动模式）
                // 土地挂钩：Cultivation 从存档恢复；暂存/领地索引重建
                Cultivation = cultivation ?? new float[n],
                CellOwner = cellOwner ?? EnumerableRepeat(-1, n),
                LockedUntil = lockedUntil ?? EnumerableRepeat(0, n),   // 实控锁定
                CellBestOwner = EnumerableRepeat(-1, n),
                CellBestInf = new float[n],
                CellOwnerInf = new float[n],
            };
            ctx.EnsureTerritory();   // 惰性建领地索引（若长度不足 RebuildInfluence 内动态扩展）
            CivEngine.BuildLayer1(ctx);   // 层1 空间生产力 R（确定性重建，不存档）
            // 边界态统一重建（唯一入口，与 Run 结尾/Continue 同式）——读档重算路径 = 演化重算路径
            CivEngine.SettleDerived(ctx);

            result = new CivSimResult { Context = ctx, FinalTick = finalTick };
            grid = g;
            LogService.Log("CivMapArchive", $"read v{Version} {path} (ticks={finalTick} years={years} entities={count})");
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogErr("CivMapArchive", $"读取失败 {path}: {ex.Message}");
            return false;
        }
    }

    private static int[] EnumerableRepeat(int v, int n)
    {
        var a = new int[n];
        Array.Fill(a, v);
        return a;
    }

    private static byte[] ReadBytes(ChunkReader f, int n)
    {
        var a = new byte[n];
        for (int i = 0; i < n; i++) a[i] = f.Get8();
        return a;
    }

    private static bool IsMagic(byte[] a, char c0, char c1, char c2, char c3) =>
        a.Length == 4 && a[0] == (byte)c0 && a[1] == (byte)c1 && a[2] == (byte)c2 && a[3] == (byte)c3;

    /// <summary>轻量摘要读取（CmpSelectMenu 存档列表用）：v17 段表版——只读 HEAD 段（seed/tick）
    /// + SeekSegment("TRIB") 直达实体段统计人口/数量——**不再手算自然段长度**（段表随机访问的
    /// 核心红利；旧版需要 ArchiveLayout.BodyLength 计算自然段偏移，段一多就难维护）。
    /// 不加载自然数组、不重建 WildCrops/R/RefreshCellState——毫秒级。
    /// 版本不符/损坏 → false，但输出版本号+状态（菜单区分"旧版本存档"与"损坏"）。</summary>
    public static bool Peek(string path, out int seed, out int tick, out float pop, out int entities,
                            out ushort archiveVersion, out ArchiveVersionStatus status)
    {
        seed = 0; tick = 0; pop = 0f; entities = 0;
        archiveVersion = 0; status = ArchiveVersionStatus.Unknown;
        try
        {
            string abs = ResolvePath(path);
            using var fs = new FileStream(abs, FileMode.Open, IOFileAccess.Read);
            using var r = new ChunkReader(fs);
            if (r.Magic != Magic) return false;
            ushort ver = r.SkeletonVer;
            archiveVersion = ver;
            status = ClassifyVersion(ver);
            if (status != ArchiveVersionStatus.Current)
                return false;   // 版本不符（菜单据此区分"旧版本存档"与"损坏"）
            if (!r.SeekSegment("HEAD")) return false;
            seed = (int)r.Get32();
            tick = (int)r.Get32();
            // 实体段：count + 每实体只取 P，其余 Seek 跳过（段表直达，无需手算自然段长度）
            if (!r.SeekSegment("TRIB")) return false;
            long count = r.Get32();
            if (count < 0 || count > 2000000) return false;
            entities = (int)count;
            for (int i = 0; i < count; i++)
            {
                r.Get32();                  // Id
                pop += r.GetFloat();        // P
                r.Get8();                   // IsFarming
                int keyCount = r.Get16();
                // 段表格式固定布局（v17，无版本分支）：keys 后固定尾部。
                // 183 = 份额×4 107 + Relig5 + Cell系列24 + Stocks24 + SettledSince/PlaceId 8
                //       + Prestige/Contributed/Succession 12 + ConqueredBy/LastWarTick 8
                //       （v16 概念分组布局，单一常数——重排后总长不变，Peek 只跳过不解析）。
                long skip = 16L * keyCount + 183L;
                r.Seek(r.Position + skip);
            }
            return true;
        }
        catch
        {
            return false;   // 打不开/损坏（列表显示"存档已损坏"）
        }
    }
}
