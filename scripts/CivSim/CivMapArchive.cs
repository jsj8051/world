using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using World.Biome;
using World.LogicGrid;
using World.MapGen;

namespace World.CivSim;

/// <summary>
/// 游玩地图存档 .cmp v4（石器时代段；自包含——自然层快照 + 实体表，独立于 .mpa/.gmp）。
/// 源自然地图只读，演化不修改任何自然字段。
///
/// v4（2026-08-06，纯实体模型）：
///   [4B]  magic "CMP1"
///   [2B]  version = 4
///   [4B]  seed | [4B] tick | [4B] years（= tick×100）
///   GameMapArchive.WriteBody（自然段，与 .gmp 布局一致，复用不变）
///   实体段（部落表）：
///     [4B] count
///     每个：[4B] P(float) | [1B] IsFarming | [2B] techKeyCount
///           [16B×n] key（定长 ASCII，\0 填充）
///           [4B] CultureShare | [4B] CultureGroupShare | [5B] ReligionShare
///           [4B] Cell | [4B] OriginCell | [4B] BornTick
///
/// 旧档放弃（用户拍板）：v3 部落表 / biome 值 4-11 一律报错要求重新生成，不做兼容转换。ver>4 拒绝。
/// WildCrops 不存档（确定性重建：同 seed 同网格同结果）。
/// </summary>
public static class CivMapArchive
{
    public const string Magic = "CMP1";
    public const ushort Version = 4;
    private const int KeyMaxLen = 16;

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
        f.Store32((uint)ctx.ReligionKeyCount);  // 宗教派别 key 计数
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
        }
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

    /// <summary>解析 "cult_N" → N（非该格式 → 0；用于 key 计数兜底推导）。</summary>
    private static int KeyNum(string key)
    {
        if (key != null && key.StartsWith("cult_") && int.TryParse(key.AsSpan(5), out int n))
            return n;
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

    /// <summary>读 .cmp → （自然层 GameGrid + 文明结果）。v4 校验：版本、旧 biome 值。</summary>
    public static bool Read(string path, out GameGrid grid, out CivSimResult result)
    {
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
        if (ver > Version)
        {
            GD.PrintErr($"[CivMapArchive] unsupported version {ver} in {path} (need ≤{Version})");
            return false;
        }
        if (ver < Version)
        {
            GD.PrintErr($"[CivMapArchive] old version {ver} in {path}（旧档已放弃，请重新演化生成 v{Version}）");
            return false;
        }
        int seed = (int)f.Get32();
        int finalTick = (int)f.Get32();
        int years = (int)f.Get32();
        ulong rngState = f.Get64();   // Rng 状态（0=旧档无状态，用 seed 重建）
        int cultureKeyCount = (int)f.Get32();   // 文化 key 计数（读档续跑接续 key 空间）
        int religionKeyCount = (int)f.Get32();  // 宗教派别 key 计数
        var g = new GameGrid();
        GameMapArchive.ReadBody(f, g);
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
            entities.Add(e);
            if (e.Cell >= 0 && e.Cell < n) cellTribes[e.Cell].Add(e);
        }

        // 文化 key 计数兜底：份额场推导（旧档无头部计数时；被同化掉的 key 可能使推导偏小，故取 max）
        int maxCultId = 0, maxReligId = 0;
        for (int k = 0; k < entities.Count; k++)
        {
            var e = entities[k];
            maxCultId = Math.Max(maxCultId, KeyNum(e.CultureShare[0].Key));
            maxCultId = Math.Max(maxCultId, KeyNum(e.CultureShare[1].Key));
            maxCultId = Math.Max(maxCultId, KeyNum(e.CultureGroupShare[0].Key));
            maxCultId = Math.Max(maxCultId, KeyNum(e.CultureGroupShare[1].Key));
            maxReligId = Math.Max(maxReligId, KeyNumRelig(e.ReligionCultShare[0].Key));
            maxReligId = Math.Max(maxReligId, KeyNumRelig(e.ReligionCultShare[1].Key));
        }

        var ctx = new CivSimContext
        {
            Grid = g,
            CellTribes = cellTribes,
            Entities = entities,
            Seed = seed,
            OriginCount = 3,
            Tick = finalTick,          // 读档续跑从存档 tick 继续（T04 验证）
            Rng = rngState != 0 ? new DeterministicRandom(rngState) : new DeterministicRandom(seed),   // 状态恢复：随机序列与从头跑对齐
            BaseK = new float[n],
            CellK = new float[n],
            CellPop = new float[n],
            BfsStamp = new int[n],
            BfsStampValue = 1,
            WildCrops = g.EnsureWildCrops(),
            Suit = WildCropsSystem.Suitability(g),
            FirstFarmTick = -1,
            CultureKeyCount = Math.Max(cultureKeyCount, maxCultId + 1),   // 存档值优先，份额场推导兜底（旧档）
            ReligionKeyCount = Math.Max(religionKeyCount, maxReligId + 1),
        };
        for (int i = 0; i < n; i++)
            ctx.BaseK[i] = ctx.YHunter0(i);
        CivEngine.RefreshCellState(ctx);

        result = new CivSimResult { Context = ctx, FinalTick = finalTick };
        grid = g;
        GD.Print($"[CivMapArchive] read v{ver} {path} (ticks={finalTick} years={years} entities={count})");
        return true;
    }

    private static byte[] ReadBytes(FileAccess f, int n)
    {
        var a = new byte[n];
        for (int i = 0; i < n; i++) a[i] = f.Get8();
        return a;
    }
}
