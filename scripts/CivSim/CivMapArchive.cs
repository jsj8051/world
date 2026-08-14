using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using World.Biome;
using World.HexPlanet;
using World.LogicGrid;
using World.MapGen;

namespace World.CivSim;

/// <summary>
/// 游玩地图存档 .cmp v6（石器时代段；自包含——自然层快照 + 实体表，独立于 .mpa/.gmp）。
/// 源自然地图只读，演化不修改任何自然字段。
///
/// v6（2026-08-09）：头部 +4B CultureGroupKeyCount 独立计数——v5 只存合并 cultureKeyCount，
///   读档恢复的群计数器 ≠ 从头演化实际值 → 续跑群漂变 key 错位 → 读档续跑分叉（T04）。v5 旧档拒绝。
/// v5（2026-08-17，两层食物流）：公式变更（R 场/按部落增长/农业尖峰）→ v4 旧档续跑行为不同，拒绝。
/// v4（2026-08-06，纯实体模型）：
///   [4B]  magic "CMP1"
///   [2B]  version = 6
///   [4B]  seed | [4B] tick | [4B] years（= tick×100）
///   [8B]  rngState | [4B] cultKey | [4B] cultgKey（v6） | [4B] relKey
///   GameMapArchive.WriteBody（自然段，与 .gmp 布局一致，复用不变）
///   实体段（部落表）：
///     [4B] count
///     每个：[4B] P(float) | [1B] IsFarming | [2B] techKeyCount
///           [16B×n] key（定长 ASCII，\0 填充）
///           [4B] CultureShare | [4B] CultureGroupShare | [5B] ReligionShare
///           [4B] Cell | [4B] OriginCell | [4B] BornTick
///
/// 旧档放弃（用户拍板）：v3/v4 部落表 / biome 值 4-11 一律报错要求重新生成，不做兼容转换。ver>5 拒绝。
/// WildCrops 不存档（确定性重建：同 seed 同网格同结果）。
/// </summary>
/// <summary>存档版本分类：Current（本版）/ Compatible（兼容表内旧版）/ Older（版本过旧）/ Newer（版本过新）/ Unknown（读不出）。</summary>
public enum ArchiveVersionStatus { Unknown = 0, Current, Compatible, Older, Newer }

public static class CivMapArchive
{
    public const string Magic = "CMP1";
    public const ushort Version = 10;   // v10（2026-08-17 酋邦层）：实体段加声望/酋长/酋邦/贡赋/继承窗口（+18B/实体）；v9 旧档拒绝（模型变更）
    private const int KeyMaxLen = 16;

    /// <summary>本游戏版本可读的存档格式版本列表（向后兼容声明）。
    /// 升级格式时：旧档语义仍一致 → 加入列表（可读可进）；公式变更导致续跑行为不同 → 不加
    /// （旧档显示"旧版本存档"，可展示但禁止进入）。
    /// v6（2026-08-09）：头部 +4B 存 CultureGroupKeyCount 独立计数——v5 只存合并 cultureKeyCount，
    ///   读档恢复的群计数器 = max(合并值, 场推导) ≠ 从头演化实际值 → 续跑群漂变 key 编号错位 → 读档续跑分叉（T04）。</summary>
    public static readonly ushort[] CompatibleArchiveVersions = { Version };

    /// <summary>游戏版本号（project.godot application/config/version；仅供展示，兼容判断用 CompatibleArchiveVersions）。</summary>
    public static string GameVersion =>
        ProjectSettings.GetSetting("application/config/version", "0.0.0").AsString();

    /// <summary>版本分类：Current/Compatible 可读；Older/Newer/Unknown 拒绝（与 Read/Peek 共用，菜单据此区分文案）。</summary>
    public static ArchiveVersionStatus ClassifyVersion(ushort ver)
    {
        if (ver == Version) return ArchiveVersionStatus.Current;
        foreach (ushort v in CompatibleArchiveVersions)
            if (v == ver) return ArchiveVersionStatus.Compatible;
        if (ver == 0) return ArchiveVersionStatus.Unknown;
        return ver > Version ? ArchiveVersionStatus.Newer : ArchiveVersionStatus.Older;
    }

    public static bool Write(string path, GameGrid grid, CivSimResult result, bool log = true)
    {
        string dir = path.GetBaseDir();
        if (dir.Length > 0 && !DirAccess.DirExistsAbsolute(dir))
            DirAccess.MakeDirRecursiveAbsolute(dir);
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PrintErr($"[CivMapArchive] cannot open {path} for write: {FileAccess.GetOpenError()}");
            return false;
        }
        var ctx = result.Context;
        f.Store8((byte)'C'); f.Store8((byte)'M'); f.Store8((byte)'P'); f.Store8((byte)'1');
        f.Store16(Version);
        f.Store32((uint)ctx.Seed);
        f.Store32((uint)result.FinalTick);
        f.Store32((uint)(result.FinalTick * CivSimContext.TickYears));
        f.Store64((ctx.Rng as DeterministicRandom)?.State ?? (ulong)ctx.Seed);   // Rng 状态（读档续跑无分叉）
        f.Store32((uint)ctx.CultureKeyCount);   // 文化 key 计数（分裂分化接续 key 空间；推导不可靠——被同化掉的 key 不在份额场）
        f.Store32((uint)ctx.CultureGroupKeyCount);   // 文化群 key 计数（v6 独立入档：读档续跑群漂变无分叉——v5 只存合并值致 key 错位）
        f.Store32((uint)ctx.ReligionKeyCount);  // 宗教派别 key 计数
        f.Store32((uint)ctx.NextEntityId);      // 实体 Id 计数器（v8：存档只存活实体，Count 读档分叉）
        GameMapArchive.WriteBody(f, grid);      // 自然层（只读源，原样快照）

        int alive = 0;
        for (int k = 0; k < ctx.Entities.Count; k++) if (!ctx.Entities[k].Dead) alive++;
        f.Store32((uint)alive);
        foreach (var e in ctx.Entities)
        {
            if (e.Dead) continue;
            f.Store32((uint)e.Id);
            f.StoreFloat(e.P);
            f.Store8((byte)(e.IsFarming ? 1 : 0));
            f.Store16((ushort)e.TechKeys.Count);
            foreach (var key in e.TechKeys)
                StoreKey(f, key);
            StoreShare(f, e.CultureShare);          // (key16B + frac1B)×2
            StoreShare(f, e.CultureGroupShare);
            foreach (var s in e.ReligionShare) f.Store8(s.Frac);   // 宗教类型：固定 key 表 → 只存份额 5B
            StoreShare(f, e.ReligionCultShare);     // 宗教派别：(key16B + frac1B)×2
            f.Store32((uint)e.Cell);
            f.Store32((uint)e.OriginCell);
            f.Store32((uint)e.BornTick);
            f.Store32((uint)e.LastMigrateTick);   // 迁移冷却（v8）
            f.Store32((uint)e.LastSplitTick);     // 分裂冷却（v8）
            f.Store32((uint)e.LastConflictTick);  // 冲突冷却（v8 冲突机制 2026-08-10）
            for (int gi = 0; gi < 3; gi++) f.StoreFloat(e.Goods[gi]);   // 货物 3×float（v7）
            // 酋邦层（v10，2026-08-17）：声望/酋长标记/酋邦归属/贡赋累计/继承窗口
            f.StoreFloat(e.Prestige);
            f.Store8((byte)(e.IsBigMan ? 1 : 0));
            f.Store8((byte)(e.IsChief ? 1 : 0));
            f.Store32((uint)e.ChiefdomId);      // -1 → 0xFFFFFFFF
            f.StoreFloat(e.Contributed);
            f.Store32((uint)e.SuccessionUntil); // -1 → 0xFFFFFFFF
        }
        // 尾部：土地挂钩（v9）——开垦率场 + 格归属 + 实控锁定（读档续跑无分叉）
        // ⚠️ 2026-08-17：v8 的存量 Stock 段移除（砍存量再生），原位换开垦率 Cultivation
        for (int c = 0; c < ctx.Grid.N; c++) f.StoreFloat(ctx.Cultivation != null ? ctx.Cultivation[c] : 0f);
        for (int c = 0; c < ctx.Grid.N; c++) f.Store32((uint)ctx.CellOwner[c]);
        for (int c = 0; c < ctx.Grid.N; c++) f.Store32(ctx.LockedUntil != null && ctx.LockedUntil[c] > 0 ? (uint)ctx.LockedUntil[c] : 0u);   // 实控锁定（v8 冲突机制；0=无）
        if (log)
            GD.Print($"[CivMapArchive] wrote v{Version} {path} (ticks={result.FinalTick} " +
                     $"entities={alive} pop={ctx.TotalPopulation():F0} farm={CountFarming(ctx)} fission={ctx.Fissions} migrate={ctx.Migrations})");
        return true;
    }

    private static void StoreKey(FileAccess f, string key)
    {
        var bytes = Encoding.ASCII.GetBytes(key);
        int n = Mathf.Min(bytes.Length, KeyMaxLen);
        for (int i = 0; i < n; i++) f.Store8(bytes[i]);
        for (int i = n; i < KeyMaxLen; i++) f.Store8(0);
    }

    /// <summary>份额场序列化：(key 定长 16B + 份额 1B)×2。null key → 全 0。</summary>
    private static void StoreShare(FileAccess f, ShareEntry[] s)
    {
        for (int i = 0; i < 2; i++)
        {
            StoreKey(f, s[i].Key ?? "");
            f.Store8(s[i].Frac);
        }
    }

    private static ShareEntry[] ReadShare(FileAccess f)
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
        for (int i = 0; i < ctx.Entities.Count; i++)
            if (!ctx.Entities[i].Dead && ctx.Entities[i].IsFarming) c++;
        return c;
    }

    /// <summary>读 .cmp → （自然层 GameGrid + 文明结果）。v4 校验：版本、旧 biome 值。
    /// ⚠️ 2026-08-07：读档入口必须 TechTable.Load()——否则 _byKey 空 → 读档后 RefreshCellState/YFarm
    /// 里 Get(key) 全 null → NRE（CmpSelectMenu 只 Read 不 Run 的场景崩溃根因）。Load 幂等。</summary>
    public static bool Read(string path, out GameGrid grid, out CivSimResult result)
    {
        TechTable.Load();
        grid = null;
        result = null;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PrintErr($"[CivMapArchive] cannot open {path} for read: {FileAccess.GetOpenError()}");
            return false;
        }
        if (f.Get8() != 'C' || f.Get8() != 'M' || f.Get8() != 'P' || f.Get8() != '1')
        {
            GD.PrintErr($"[CivMapArchive] bad magic in {path}");
            return false;
        }
        ushort ver = f.Get16();
        switch (ClassifyVersion(ver))
        {
            case ArchiveVersionStatus.Newer:
                GD.PrintErr($"[CivMapArchive] unsupported version {ver} in {path} (need ≤{Version})");
                return false;
            case ArchiveVersionStatus.Older:
            case ArchiveVersionStatus.Unknown:
                GD.PrintErr($"[CivMapArchive] old version {ver} in {path}（旧档已放弃，请重新演化生成 v{Version}）");
                return false;
        }
        int seed = (int)f.Get32();
        int finalTick = (int)f.Get32();
        int years = (int)f.Get32();
        ulong rngState = f.Get64();   // Rng 状态（0=旧档无状态，用 seed 重建）
        int cultureKeyCount = (int)f.Get32();   // 文化 key 计数（读档续跑接续 key 空间）
        int cultureGroupKeyCount = (int)f.Get32();   // 文化群 key 计数（v6 独立入档——v5 只存合并值，读档恢复不可靠致续跑群漂变分叉）
        int religionKeyCount = (int)f.Get32();  // 宗教派别 key 计数
        int nextEntityId = ver >= 8 ? (int)f.Get32() : 0;   // 实体 Id 计数器（v8；读档续跑 Id 分配无分叉）
        var g = new GameGrid();
        if (!GameMapArchive.ReadBody(f, g))
            return false;   // 结构校验失败（正文错位/损坏）已在内部打印
        int n = g.N;

        // ── 旧档放弃：biome 4-11 化石值 → 报错 ──
        for (int i = 0; i < n; i++)
        {
            byte b = g.Biome[i];
            if (b >= 4 && b <= 11)
            {
                GD.PrintErr($"[CivMapArchive] {path} 含化石 biome 值 {b}（旧档已放弃，请重新生成）");
                return false;
            }
        }

        int count = (int)f.Get32();
        // ⚠️ 2026-08-07：实体表长度分配前校验——count 是正文错位后最易读爆的字段
        //   （map_seed42_n16 等旧中间态档 count=11.7 亿 → new List<CivEntity>(count) ≈ 9.4GB）。
        //   单实体最小 ~79B（Id+P+IsFarm+keyCnt+2×(16+1)×3+relig5+Cell×3），用 64B 保守下界；
        //   剩余文件字节数都不够 → 必为错位垃圾。
        ulong remaining = f.GetLength() - f.GetPosition();
        if (count < 0 || (ulong)count > remaining / 64)
        {
            GD.PrintErr($"[CivMapArchive] {path} 实体表长度异常：count={count}，剩余 {remaining}B（最小实体 64B 装不下）——正文错位或损坏，请重新生成。");
            return false;
        }
        var entities = new List<CivEntity>(count);
        var cellTribes = new List<CivEntity>[n];
        for (int i = 0; i < n; i++) cellTribes[i] = new List<CivEntity>();
        for (int k = 0; k < count; k++)
        {
            var e = new CivEntity
            {
                Id = (int)f.Get32(),
                P = f.GetFloat(),
                IsFarming = f.Get8() != 0,
            };
            int keyCount = f.Get16();   // 顺序与 Write/文档 §十 一致：P→IsFarming→keys→份额→Cell 系列
            for (int q = 0; q < keyCount; q++)
            {
                var kb = ReadBytes(f, KeyMaxLen);
                int len = 0;
                while (len < kb.Length && kb[len] != 0) len++;
                if (len > 0) e.TechKeys.Add(Encoding.ASCII.GetString(kb, 0, len));
            }
            e.CultureShare = ReadShare(f);
            e.CultureGroupShare = ReadShare(f);
            e.ReligionShare = ShareField.NewReligion(ReligionStage.Animism);   // 固定 key 重建，只读份额
            for (int q2 = 0; q2 < ReligionStage.Count; q2++) e.ReligionShare[q2].Frac = f.Get8();
            e.ReligionCultShare = ReadShare(f);
            e.Cell = (int)f.Get32();
            e.OriginCell = (int)f.Get32();
            e.BornTick = (int)f.Get32();
            e.LastMigrateTick = ver >= 8 ? (int)f.Get32() : -1;   // 迁移冷却（v8）
            e.LastSplitTick = ver >= 8 ? (int)f.Get32() : -1;     // 分裂冷却（v8）
            e.LastConflictTick = ver >= 8 ? (int)f.Get32() : -1;  // 冲突冷却（v8 冲突机制 2026-08-10）
            for (int gi = 0; gi < 3; gi++) e.Goods[gi] = f.GetFloat();   // 货物（v7）
            if (ver >= 10)   // 酋邦层（v10，2026-08-17）
            {
                e.Prestige = f.GetFloat();
                e.IsBigMan = f.Get8() != 0;
                e.IsChief = f.Get8() != 0;
                e.ChiefdomId = (int)f.Get32();
                e.Contributed = f.GetFloat();
                e.SuccessionUntil = (int)f.Get32();
            }
            entities.Add(e);
            if (e.Cell >= 0 && e.Cell < n) cellTribes[e.Cell].Add(e);
        }

        // 尾部：土地挂钩（v9）——开垦率场 + 格归属
        float[] cultivation = ver >= 9 ? new float[n] : null;
        if (ver >= 9)
        {
            for (int c = 0; c < n; c++) cultivation[c] = f.GetFloat();
        }
        int[] cellOwner = ver >= 8 ? new int[n] : null;
        if (ver >= 8)
        {
            for (int c = 0; c < n; c++) cellOwner[c] = (int)f.Get32();
        }
        int[] lockedUntil = ver >= 8 ? new int[n] : null;   // 实控锁定（v8 冲突机制）
        if (ver >= 8)
        {
            for (int c = 0; c < n; c++) lockedUntil[c] = (int)f.Get32();
        }

        // 文化 key 计数兜底：份额场推导（旧档无头部计数时；被同化掉的 key 可能使推导偏小，故取 max）
        // 2026-08-07：标签/群分开推导（群 "cultg_" 前缀独立空间，防标签挤占语言群 key）
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
        // 存档头部只存一个合并 cultureKeyCount（旧格式）；读档后标签/群各自取 max 兜底——
        //   新档群前缀 cultg_ 由 KeyNum 解析出独立计数；旧档群仍是 cult_ 与标签共享，取合并值无冲突。

        var ctx = new CivSimContext
        {
            Grid = g,
            CellTribes = cellTribes,
            Entities = entities,
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
            CultureGroupKeyCount = Math.Max(cultureGroupKeyCount, maxGroupId + 1),  // 群计数：v6 头部独立值优先（续跑无分叉）；场推导兜底
            ReligionKeyCount = Math.Max(religionKeyCount, maxReligId + 1),
            NextEntityId = nextEntityId,   // 实体 Id 计数器（v8；读档续跑 Id 分配无分叉）
            // 土地挂钩（v9）：Cultivation 从存档恢复；暂存/领地索引重建
            Cultivation = cultivation ?? new float[n],
            CellOwner = cellOwner ?? EnumerableRepeat(-1, n),
            LockedUntil = lockedUntil ?? EnumerableRepeat(0, n),   // 实控锁定（v8 冲突机制）
            CellBestOwner = EnumerableRepeat(-1, n),
            CellBestInf = new float[n],
            CellOwnerInf = new float[n],
        };
        ctx.EnsureTerritory();   // 惰性建领地索引（若长度不足 RebuildInfluence 内动态扩展）
        CivEngine.BuildLayer1(ctx);   // 层1 空间生产力 R（确定性重建，不存档）
        ctx.RebuildInfluence();       // 归属+领地重建（v8；旧档 Stock=0 → Harvest=0 → 饿死——旧档已拒，正常路径不达）
        TerritoryModel.Rebuild(ctx);  // ⚠️ 2026-08-17 领地凝聚（TerritoryModel 已注册演化 Order 45——
                                      //   读档路径同步重建 TerritoryId/Size，否则读档后全散兵）
        CivEngine.RefreshCellState(ctx);

        result = new CivSimResult { Context = ctx, FinalTick = finalTick };
        grid = g;
        GD.Print($"[CivMapArchive] read v{ver} {path} (ticks={finalTick} years={years} entities={count})");
        return true;
    }

    private static int[] EnumerableRepeat(int v, int n)
    {
        var a = new int[n];
        Array.Fill(a, v);
        return a;
    }

    private static byte[] ReadBytes(FileAccess f, int n)
    {
        var a = new byte[n];
        for (int i = 0; i < n; i++) a[i] = f.Get8();
        return a;
    }

    private static bool IsMagic(byte[] a, char c0, char c1, char c2, char c3) =>
        a.Length == 4 && a[0] == (byte)c0 && a[1] == (byte)c1 && a[2] == (byte)c2 && a[3] == (byte)c3;

    /// <summary>轻量摘要读取（CmpSelectMenu 存档列表用）：只读头部（seed/tick）+ 跳过自然段 + 统计实体段（人口/数量）。
    /// 不加载任何自然数组、不重建 WildCrops/R/RefreshCellState——n=64 档从全量 Read 的 ~1-2s 降到 ~50ms。
    /// 布局：CivMapArchive 头 38B + 自然段（WriteBody 直连，无 GMP1 magic，长度 = 53 + 94n） + 实体段（count + 每实体 130 + 16×keyCount B）。
    /// 版本不符/损坏 → false，但输出版本号+状态（菜单区分"旧版本存档"与"损坏"）；结构失败 → false 状态 Unknown。</summary>
    public static bool Peek(string path, out int seed, out int tick, out float pop, out int entities,
                            out ushort archiveVersion, out ArchiveVersionStatus status)
    {
        seed = 0; tick = 0; pop = 0f; entities = 0;
        archiveVersion = 0; status = ArchiveVersionStatus.Unknown;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null) return false;
        if (!IsMagic(ReadBytes(f, 4), 'C', 'M', 'P', '1')) return false;
        ushort ver = f.Get16();
        archiveVersion = ver;
        status = ClassifyVersion(ver);
        if (status is ArchiveVersionStatus.Older or ArchiveVersionStatus.Newer or ArchiveVersionStatus.Unknown)
            return false;   // 版本不符（菜单据此区分"旧版本存档"与"损坏"）
        seed = (int)f.Get32();
        tick = (int)f.Get32();
        f.Get32(); f.Get64(); f.Get32(); f.Get32(); f.Get32(); f.Get32();   // years / rngState / cultKey / grpKey(v6) / relKey / nextEntityId(v8)
        // 自然段（WriteBody 布局，无 GMP1 magic——.gmp 才有 magic+gVer 的 6B）：
        // 直接 GridN + N，验结构不变量（10n²+2，防伪造 N 让偏移爆炸）后整体跳过
        int gridN = (int)f.Get32();
        int n = (int)f.Get32();
        long expectN = Icosahedron.VertexCountForLong(gridN);
        if (gridN < 8 || gridN > 512 || expectN != n) return false;   // 与 ReadBody 同语义（N=顶点数=10n²+2）
        long naturalLen = 53L + 94L * n;              // WriteBody 固定 53B（GridN 起→Verts 前）+ 每格 94B
        f.Seek((ulong)(42 + naturalLen));             // 实体段起点（CivMapArchive 头 42B：v8 含 NextEntityId 4B + 自然段）
        // 实体段：count + 每实体只取 P，其余 Seek 跳过
        long count = f.Get32();
        if (count < 0 || count > 2000000) return false;
        entities = (int)count;
        for (int i = 0; i < count; i++)
        {
            f.Get32();                  // Id
            pop += f.GetFloat();        // P
            f.Get8();                   // IsFarming
            int keyCount = f.Get16();
            long skip = 16L * keyCount + 34 + 34 + 5 + 34 + 12 + 24;   // keys + 份额×3 + Cell/OriginCell/BornTick/冷却×3(v8冲突) + 货物3×float
            f.Seek(f.GetPosition() + (ulong)skip);
        }
        return true;
    }
}
