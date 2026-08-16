using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using World.Biome;
using World.HexPlanet;
using World.LogicGrid;
using World.MapGen;
using World.Services;

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
    public const ushort Version = 13;   // v13（2026-08-19 阶段3 聚落设计）：部落 +2 字段（SettledSince/PlaceId）
                                        //   + 新段 Settlements[]（聚落实体：场所比人长寿——粮仓归聚落）。
                                        //   v12 旧档可读可进（无聚落——仅新演化生成，用户拍板）。v9 及更旧拒绝。
    private const int KeyMaxLen = 16;

    /// <summary>本游戏版本可读的存档格式版本列表（向后兼容声明）。
    /// 升级格式时：旧档语义仍一致 → 加入列表（可读可进）；公式变更导致续跑行为不同 → 不加
    /// （旧档显示"旧版本存档"，可展示但禁止进入）。
    /// v13（2026-08-19）：聚落设计——部落 +2 字段 + 新段 Settlements[]——v12 档读入无聚落
    ///   （仅新演化生成，用户拍板；语义一致 → 可读可进）。
    /// v12（2026-08-18）：Goods[3]→Stocks[6] 商品目录扩展——v11 档读入 Goods→Stocks 映射，
    ///   Food 槽默认 0，存储池为空起步（语义一致，可读可进）。
    /// v11（2026-08-18）：IsBigMan/IsChief/ChiefdomId 移出入档改派生——v10 档读入后 SettleDerived 重算覆盖，
    ///   语义一致 → 可读可进。
    /// v6（2026-08-09）：头部 +4B 存 CultureGroupKeyCount 独立计数——v5 只存合并 cultureKeyCount，
    ///   读档恢复的群计数器 = max(合并值, 场推导) ≠ 从头演化实际值 → 续跑群漂变 key 编号错位 → 读档续跑分叉（T04）。</summary>
    public static readonly ushort[] CompatibleArchiveVersions = { Version, 12, 11, 10 };

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
        // ⚠️ 2026-08-18 阶段3：清单自检（字段改名/删除后清单过期 → 写档拒绝，防静默漏字段）
        if (!CivArchiveSchema.Validate()) return false;
        string dir = path.GetBaseDir();
        if (dir.Length > 0 && !DirAccess.DirExistsAbsolute(dir))
            DirAccess.MakeDirRecursiveAbsolute(dir);
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            LogService.LogErr("CivMapArchive", $"cannot open {path} for write: {FileAccess.GetOpenError()}");
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
        f.Store32((uint)ctx.NextTribeId);      // 实体 Id 计数器（v8：存档只存活实体，Count 读档分叉）
        GameMapArchive.WriteBody(f, grid);      // 自然层（只读源，原样快照）

        int alive = 0;
        for (int k = 0; k < ctx.Tribes.Count; k++) if (!ctx.Tribes[k].Dead) alive++;
        f.Store32((uint)alive);
        // ⚠️ 2026-08-18 阶段3：实体段由 CivArchiveSchema 清单驱动（单源，防漏字段）。
        //   TechKeys 变长特例内联；其余遍历清单按当前版本过滤（SinceVer ≤ Version）。
        foreach (var e in ctx.Tribes)
        {
            if (e.Dead) continue;
            foreach (var def in CivArchiveSchema.TribeFields)
            {
                if (def.SinceVer > Version) continue;
                if (def.Name == "TechKeys") { StoreTechKeys(f, e); continue; }
                def.Write(f, e);
            }
        }
        // 尾部：土地挂钩（v9）——开垦率场 + 格归属 + 实控锁定（读档续跑无分叉）
        // ⚠️ 2026-08-17：v8 的存量 Stock 段移除（砍存量再生），原位换开垦率 Cultivation
        for (int c = 0; c < ctx.Grid.N; c++) f.StoreFloat(ctx.Cultivation != null ? ctx.Cultivation[c] : 0f);
        for (int c = 0; c < ctx.Grid.N; c++) f.Store32((uint)ctx.CellOwner[c]);
        for (int c = 0; c < ctx.Grid.N; c++) f.Store32(ctx.LockedUntil != null && ctx.LockedUntil[c] > 0 ? (uint)ctx.LockedUntil[c] : 0u);   // 实控锁定（v8 冲突机制；0=无）
        // 尾部：聚落段（v13——追加在土地挂钩后，旧档布局不变）
        f.Store32((uint)ctx.NextSettlementId);
        f.Store32((uint)ctx.Settlements.Count);
        foreach (var s in ctx.Settlements)
        {
            f.Store32((uint)s.Id);
            f.Store32((uint)s.Cell);
            f.Store32((uint)s.BornTick);
            f.Store32((uint)s.Level);
            f.Store32((uint)s.LastLevelUpTick);
            f.Store32((uint)s.DwellFrom);
            f.Store32((uint)s.OccupantId);   // -1 → uint 全 1（读回 (int) 还原）
            f.Store32((uint)s.RuinFrom);
            for (int k = 0; k < CommodityTable.Count; k++)
                f.StoreFloat(s.Stocks != null && k < s.Stocks.Length ? s.Stocks[k] : 0f);
        }
        if (log)
            LogService.Log("CivMapArchive", $"wrote v{Version} {path} (ticks={result.FinalTick} " +
                     $"entities={alive} pop={ctx.TotalPopulation():F0} farm={CountFarming(ctx)} fission={ctx.Fissions} migrate={ctx.Migrations}" +
                     $" settlements={ctx.Settlements.Count})");
        return true;
    }

    private static void StoreKey(FileAccess f, string key)
    {
        var bytes = Encoding.ASCII.GetBytes(key);
        int n = Mathf.Min(bytes.Length, KeyMaxLen);
        for (int i = 0; i < n; i++) f.Store8(bytes[i]);
        for (int i = n; i < KeyMaxLen; i++) f.Store8(0);
    }

    // ── 2026-08-18 阶段3：CivArchiveSchema 清单委托实现（Write/Read 由表驱动，布局严格对齐）──

    private static void StoreTechKeys(FileAccess f, Tribe e)
    {
        f.Store16((ushort)e.TechKeys.Count);
        foreach (var key in e.TechKeys)
            StoreKey(f, key);
    }
    private static bool ReadTechKeys(FileAccess f, Tribe e)
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

    internal static void StoreReligionShare(FileAccess f, Tribe e)
    {
        foreach (var s in e.ReligionShare) f.Store8(s.Frac);   // 固定 key 表 → 只存份额 5B
    }
    internal static bool ReadReligionShare(FileAccess f, Tribe e)
    {
        e.ReligionShare = ShareField.NewReligion(ReligionStage.Animism);   // 固定 key 重建，只读份额
        for (int q2 = 0; q2 < ReligionStage.Count; q2++) e.ReligionShare[q2].Frac = f.Get8();
        return true;
    }
    internal static void StoreStocks(FileAccess f, Tribe e)
    {
        // v12：写全部商品槽（含 Food）。旧 Goods[3] 槽位（皮革/羊毛/秸秆）现在位于目录的 Material 槽——
        // 字节序 = CommodityTable.All 顺序（grain/berry/meat/leather/wool/straw）。
        if (e.Stocks == null || e.Stocks.Length != CommodityTable.Count) e.Stocks = CommodityTable.NewStocks();
        for (int s = 0; s < CommodityTable.Count; s++) f.StoreFloat(e.Stocks[s]);
    }
    internal static bool ReadStocks(FileAccess f, Tribe e)
    {
        // ⚠️ 2026-08-18 版本分支：ReadStocks 在 ver>=12 时被 schema 调用（写 Count 个）；
        //   ver<12 时 schema 跳过（SinceVer=12>ver），但旧档 Goods[3] 3 个 float 仍在字节流——
        //   由 Read 的 v11 兼容分支读掉并映射（见 CivMapArchive.Read）。
        e.Stocks = CommodityTable.NewStocks();
        for (int s = 0; s < CommodityTable.Count; s++) e.Stocks[s] = f.GetFloat();
        return true;
    }

    /// <summary>份额场序列化：(key 定长 16B + 份额 1B)×2。null key → 全 0。
    /// 2026-08-18 阶段3：internal 供 CivArchiveSchema 清单委托调用。</summary>
    internal static void StoreShare(FileAccess f, ShareEntry[] s)
    {
        for (int i = 0; i < 2; i++)
        {
            StoreKey(f, s[i].Key ?? "");
            f.Store8(s[i].Frac);
        }
    }

    internal static ShareEntry[] ReadShare(FileAccess f)
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
        for (int i = 0; i < ctx.Tribes.Count; i++)
            if (!ctx.Tribes[i].Dead && ctx.Tribes[i].IsFarming) c++;
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
            LogService.LogErr("CivMapArchive", $"cannot open {path} for read: {FileAccess.GetOpenError()}");
            return false;
        }
        if (f.Get8() != 'C' || f.Get8() != 'M' || f.Get8() != 'P' || f.Get8() != '1')
        {
            LogService.LogErr("CivMapArchive", $"bad magic in {path}");
            return false;
        }
        ushort ver = f.Get16();
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
                LogService.LogErr("CivMapArchive", $"{path} 含化石 biome 值 {b}（旧档已放弃，请重新生成）");
                return false;
            }
        }

        int count = (int)f.Get32();
        // ⚠️ 2026-08-07：实体表长度分配前校验——count 是正文错位后最易读爆的字段
        //   （map_seed42_n16 等旧中间态档 count=11.7 亿 → new List<Tribe>(count) ≈ 9.4GB）。
        //   单实体最小 ~79B（Id+P+IsFarm+keyCnt+2×(16+1)×3+relig5+Cell×3），用 64B 保守下界；
        //   剩余文件字节数都不够 → 必为错位垃圾。
        ulong remaining = f.GetLength() - f.GetPosition();
        if (count < 0 || (ulong)count > remaining / 64)
        {
            LogService.LogErr("CivMapArchive", $"{path} 实体表长度异常：count={count}，剩余 {remaining}B（最小实体 64B 装不下）——正文错位或损坏，请重新生成。");
            return false;
        }
        var entities = new List<Tribe>(count);
        var cellTribes = new Tribe[n];
        for (int i = 0; i < n; i++) cellTribes[i] = null;
        for (int k = 0; k < count; k++)
        {
            var e = new Tribe();
            // ⚠️ 2026-08-18 阶段3：实体段由 CivArchiveSchema 清单驱动（单源；按存档版本过滤 SinceVer ≤ ver）。
            //   TechKeys 变长特例内联；v10 酋邦块字节序与 v11 不同（v10: Prestige→IsBigMan→IsChief→
            //   ChiefdomId→Contributed→SuccessionUntil；v11: Prestige→Contributed→SuccessionUntil）→
            //   Contributed/SuccessionUntil 在 v10 分支内联读（Prestige 两版同位，由清单读）。
            foreach (var def in CivArchiveSchema.TribeFields)
            {
                if (def.SinceVer > ver) continue;
                if (def.Name == "TechKeys") { ReadTechKeys(f, e); continue; }
                if (ver == 10 && (def.Name == "Contributed" || def.Name == "SuccessionUntil")) continue;   // v10 内联
                def.Read(f, e);
            }
            if (ver < 12)
            {
                // ⚠️ 2026-08-18 阶段3 兼容：v7-v11 旧档 Goods[3]（皮革/羊毛/秸秆 3 float，在 LastConflictTick 后）
                //   schema 的 Stocks(SinceVer=12) 被跳过 → 此处读掉并映射到动态目录 Material 槽。
                e.Stocks = CommodityTable.NewStocks();
                e.Stocks[CommodityTable.Index(CommodityTable.Leather)] = f.GetFloat();
                e.Stocks[CommodityTable.Index(CommodityTable.Wool)] = f.GetFloat();
                e.Stocks[CommodityTable.Index(CommodityTable.Straw)] = f.GetFloat();
                // Food 槽默认 0（旧档无食物存储概念）
            }
            if (ver == 10)
            {
                // v10 兼容：跳过 IsBigMan(1)+IsChief(1)+ChiefdomId(4)，读 Contributed/SuccessionUntil
                //（SettleDerived 的 DeriveLeadership/ChiefdomModel.Rebuild 重算覆盖被跳过的 3 字段）
                f.Get8(); f.Get8(); f.Get32();
                e.Contributed = f.GetFloat();
                e.SuccessionUntil = (int)f.Get32();
            }
            entities.Add(e);
            if (e.Cell >= 0 && e.Cell < n) cellTribes[e.Cell] = e;   // 一格一实体
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

        // 尾部：聚落段（v13——土地挂钩后；v12 旧档无聚落——仅新演化生成，用户拍板）
        var settlements = new List<Settlement>();
        int nextSettlementId = 0;
        if (ver >= 13)
        {
            nextSettlementId = (int)f.Get32();
            int sCount = (int)f.Get32();
            // 长度校验（同实体表：最小聚落 ~56B——8×I32 + 6×F32；用 48B 保守下界防错位读爆）
            ulong sRemaining = f.GetLength() - f.GetPosition();
            if (sCount < 0 || (ulong)sCount > sRemaining / 48)
            {
                LogService.LogErr("CivMapArchive", $"{path} 聚落段长度异常：count={sCount}，剩余 {sRemaining}B——正文错位或损坏。");
                return false;
            }
            for (int k = 0; k < sCount; k++)
            {
                var s = new Settlement
                {
                    Id = (int)f.Get32(),
                    Cell = (int)f.Get32(),
                    BornTick = (int)f.Get32(),
                    Level = (int)f.Get32(),
                    LastLevelUpTick = (int)f.Get32(),
                    DwellFrom = (int)f.Get32(),
                    OccupantId = (int)f.Get32(),
                    RuinFrom = (int)f.Get32(),
                };
                s.Stocks = CommodityTable.NewStocks();
                for (int q = 0; q < CommodityTable.Count; q++) s.Stocks[q] = f.GetFloat();
                settlements.Add(s);
            }
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
            Tribes = entities,
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
            NextTribeId = nextEntityId,   // 实体 Id 计数器（v8；读档续跑 Id 分配无分叉）
            Settlements = settlements,    // 聚落段（v13；v12 旧档空列表）
            NextSettlementId = nextSettlementId,
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
        // ⚠️ 2026-08-18 阶段3 方案 D：边界态统一重建（唯一入口，与 Run 结尾/Continue 同式）——
        //   取代旧的手写拼装（RebuildInfluence→TerritoryModel.Rebuild→RecomputeProduction→RefreshCellState），
        //   消除"读档重算路径 ≠ 演化重算路径"缺陷（T04 类分叉根治）。
        CivEngine.SettleDerived(ctx);

        result = new CivSimResult { Context = ctx, FinalTick = finalTick };
        grid = g;
        LogService.Log("CivMapArchive", $"read v{ver} {path} (ticks={finalTick} years={years} entities={count})");
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
    /// 布局：CivMapArchive 头 38B + 自然段（WriteBody 直连，无 GMP1 magic，长度 = ArchiveLayout.BodyLength 单源） + 实体段（count + 每实体 130 + 16×keyCount B）。
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
        long naturalLen = ArchiveLayout.BodyLength(n, 2);   // WriteBody 布局单源（2026-08-19：原硬编码 53+94n 与 WriteBody 断链）
        f.Seek((ulong)(42 + naturalLen));             // 实体段起点（CivMapArchive 头 42B：v8 含 NextTribeId 4B + 自然段）
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
            // ⚠️ 2026-08-18：skip 按版本区分尾部字段——v10 含酋邦 IsBigMan/IsChief/ChiefdomId（+18B），
            //   v11 移出（+12B），v12 Stocks 扩到 6 槽（+24B），v13 聚落关联 SettledSince/PlaceId（+8B）。
            //   旧版恒跳 143B 漏尾部 → 错位。143 = keys 后固定（份额×4 107 + Cell系列24 + 货物/存储基础 12）。
            long tailB = ver == 10 ? 143L + 18L : ver == 11 ? 143L + 12L : ver == 12 ? 143L + 24L : 143L + 24L + 8L;
            long skip = 16L * keyCount + tailB;
            f.Seek(f.GetPosition() + (ulong)skip);
        }
        return true;
    }
}
