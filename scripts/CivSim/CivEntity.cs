using System;
using System.Collections.Generic;

namespace World.CivSim;

/// <summary>
/// 时代 = 反应性统计标签（只显示/统计，不驱动机制）。
/// 判定：生产方式=农（IsFarming）→ 新石器；否则旧石器。无硬切换。
/// </summary>
public enum EpochKind
{
    StoneAge = 0,   // 旧石器：狩猎采集
    Neolithic = 1,  // 新石器：农业（实体异步进入，金字塔分布）
}

/// <summary>宗教阶段（固定 5 段 key；升级链：泛灵→萨满→祖先→多神→一神）。
/// key 与科技/文化同风格（字符串可读）；固定表 → 存档只存份额，key 由常量表重建。</summary>
public static class ReligionStage
{
    public const string Animism = "animism";
    public const string Shaman = "shaman";
    public const string Ancestor = "ancestor";
    public const string Polytheism = "polytheism";
    public const string Monotheism = "monotheism";
    public static readonly string[] All = { Animism, Shaman, Ancestor, Polytheism, Monotheism };
    public const int Count = 5;
}

/// <summary>
/// 社会单元实体（CivEntity，v4 纯实体模型）。
/// 身份 = 人口份额，不是实体标签：文化/文化群/宗教都是人口上的分布场（top-2 存储，Σ=1）。
/// 文化/文化群用字符串 key 标识（与科技一致：存档/诊断可读，如 "cult_3"）；宗教为固定 5 段份额。
/// 分裂时份额等比例继承（人口分走，身份随人口走）；合并按人口加权融合。
/// </summary>
public class CivEntity
{
    public int Id;
    public int Cell;                  // 所在格（部落领地 = 1 格）
    public float P;                   // 人口
    public HashSet<string> TechKeys = new();   // 已获科技 key 集合（字符串可读，非位掩码）
    public int OriginCell;
    public int BornTick;
    public int LastMigrateTick = -1;   // 最近迁移 tick（迁移冷却；入档——读档续跑无分叉）
    public int LastSplitTick = -1;     // 最近分裂 tick（分裂冷却；入档）
    public bool Dead;
    public bool IsFarming;            // 生产方式（入档——读档续跑滞回无分叉）

    // ── 领地派生状态（TerritoryModel 凝聚重算填充；不存档——从实体表确定性重算，读档后重建）──
    public int TerritoryId = -1;     // 领地 id = 分量内最小实体 Id（连通分量标号，确定性）
    public int TerritorySize = 1;    // 领地内 band 数（≥2 = 正式领地，触发加成）

    // ── 能力位图缓存（CapabilityTable.MaskOf；RefreshCellState 每 tick；不存档——从科技/状态确定性重算）──
    public uint CapMask;

    // ── 货物库存（副产品累积；入档 .cmp v7——每实体 3×float 12B）──
    public float[] Goods = new float[3];   // 0=皮革 1=羊毛 2=秸秆（CivSimContext 索引常量）

    // ── 生产方式 F 分量（派生缓存：RefreshCellState 每 tick；不存档——货物分解用）──
    public float FHuntLast, FHerdLast, FFarmLast;   // 各方式当 tick 产出

    // ── 身份份额场（Σ=1，255 归一）──
    public ShareEntry[] CultureShare = NewEmpty();        // top-2：{key,份额}×2（具体文化，快）
    public ShareEntry[] CultureGroupShare = NewEmpty();   // top-2：{key,份额}×2（文化群，慢）
    public ShareEntry[] ReligionShare = ShareField.NewReligion(ReligionStage.Animism);   // 宗教类型：固定 5 段 key（机制层）
    public ShareEntry[] ReligionCultShare = NewEmpty();   // 具体宗教派别：top-2 动态 key "relig_N"（身份层，同文化群规则）

    // ── 运行时缓存（不存档，RefreshCellState 每 tick 重算）──
    public float EPerCap;    // 人均能量 e = Y/P
    public float Surplus;    // 盈余 s = e − 1
    public float CarryMult = 0f;   // 工具乘数链缓存（0=未算，FHunt fallback 实时算；两层模型 2026-08-17）
    public float FLast;      // 当 tick 实际产出 F_i 缓存（增长/核算直读，避免重复算）

    public EpochKind Epoch => IsFarming ? EpochKind.Neolithic : EpochKind.StoneAge;

    internal static ShareEntry[] NewEmpty() => new[] { new ShareEntry(), new ShareEntry() };
}

/// <summary>份额场条目：key 字符串 + 份额（255 归一）。</summary>
public struct ShareEntry
{
    public string Key;   // null = 无
    public byte Frac;
}

/// <summary>
/// 份额场工具（top-2 存储的份额守恒操作 + 宗教 5 段份额操作）。
/// 份额单位：255 = 1.0。所有转移守恒（扣多少加多少）。
/// </summary>
public static class ShareField
{
    public const int Unit = 255;

    // ── 文化 top-2 场（ShareEntry[2]）──

    public static ShareEntry[] NewCulture(string key) => new[] { new ShareEntry { Key = key, Frac = 255 }, new ShareEntry() };

    public static string DomKey(ShareEntry[] s) => s[0].Key;
    public static byte DomFrac(ShareEntry[] s) => s[0].Frac;
    public static string SecKey(ShareEntry[] s) => s[1].Key;
    public static byte SecFrac(ShareEntry[] s) => s[1].Frac;

    public static float DomFrac01(ShareEntry[] s) => s[0].Frac / 255f;

    /// <summary>key 稳定哈希（FNV-1a；显示层色带索引用——string.GetHashCode 进程间随机化不可用）。</summary>
    public static int KeyHash(string key)
    {
        if (key == null) return 0;
        uint h = 2166136261u;
        for (int i = 0; i < key.Length; i++)
        {
            h ^= key[i];
            h *= 16777619u;
        }
        return (int)(h & 0x7FFFFFFF);
    }

    /// <summary>主导份额同化其余：x' = x + rate·(1−x)（格级每 tick 一次）。</summary>
    public static void Assimilate(ShareEntry[] s, float rate)
    {
        int amt = (int)MathF.Round(SecFrac(s) * rate);
        if (amt <= 0) return;
        s[0].Frac = (byte)Math.Min(Unit, s[0].Frac + amt);
        s[1].Frac = (byte)Math.Max(0, s[1].Frac - amt);
        if (s[0].Frac == Unit) s[1] = new ShareEntry();   // 全同化 → 清第二位
    }

    /// <summary>把 srcKey 的份额向 domKey 转移 amt（相邻格 Axelrod 互动）。
    /// ⚠️ 2026-08-07 重写：旧版无条件 SwapDom 把单文化实体 [cult:255] 交换成 [null:0,cult:255]
    ///   （主导变 null，文化灭失——起源格"n:0,cult:255"畸形根因）。现按主导/次席分路，位置不交换。</summary>
    public static void Shift(ShareEntry[] s, string srcKey, string domKey, int amt)
    {
        if (amt <= 0) return;
        if (srcKey == DomKey(s))
        {
            // 主导是 srcKey（weak 自己的文化）：份额减 → 归给 domKey（次席/新次席）
            int take = Math.Min(amt, s[0].Frac);
            if (take <= 0) return;
            s[0].Frac = (byte)(s[0].Frac - take);
            if (domKey == SecKey(s))
                s[1].Frac = (byte)Math.Min(Unit, s[1].Frac + take);
            else if (s[1].Frac == 0)
                s[1] = new ShareEntry { Key = domKey, Frac = (byte)take };
            else if (domKey != DomKey(s))
                s[1].Frac = (byte)Math.Min(Unit, s[1].Frac + take);   // domKey 不在场 → 并入次席
            if (s[0].Frac == 0 && s[1].Frac > 0) SwapDom(s);   // 主导清零 → 次席上位
        }
        else if (srcKey == SecKey(s))
        {
            // 次席是 srcKey：份额转移给主导（domKey 应为主导）
            int take = Math.Min(amt, s[1].Frac);
            if (take <= 0) return;
            s[1].Frac = (byte)(s[1].Frac - take);
            s[0].Frac = (byte)Math.Min(Unit, s[0].Frac + take);
        }
        if (s[0].Frac == Unit) s[1] = new ShareEntry();   // 全占 → 清次席
    }

    /// <summary>份额等比例克隆（分裂继承：人口分走，身份随人口走）。</summary>
    public static ShareEntry[] CloneShare(ShareEntry[] s) => new[] { s[0], s[1] };

    /// <summary>格级聚合：各实体份额按人口加权合并（其余并入第 2 位，保 Σ=255）。</summary>
    public static ShareEntry[] PopMerge(IReadOnlyList<CivEntity> entities, Func<CivEntity, ShareEntry[]> getShare)
    {
        float total = 0f;
        var frac = new Dictionary<string, float>();
        foreach (var e in entities)
        {
            if (e.Dead || e.P <= 0f) continue;
            total += e.P;
            var s = getShare(e);
            AddFrac(frac, s[0].Key, s[0].Frac / 255f * e.P);
            if (s[1].Frac > 0) AddFrac(frac, s[1].Key, s[1].Frac / 255f * e.P);
        }
        if (total <= 0f) return NewCulture("cult_0");
        var list = new List<KeyValuePair<string, float>>(frac);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        var r = new[] { new ShareEntry(), new ShareEntry() };
        if (list.Count == 0) { r[0].Key = "cult_0"; r[0].Frac = 255; return r; }
        r[0].Key = list[0].Key;
        r[0].Frac = (byte)Math.Min(Unit, MathF.Round(list[0].Value / total * Unit));
        if (list.Count > 1)
        {
            r[1].Key = list[1].Key;
            r[1].Frac = (byte)Math.Max(0, Unit - r[0].Frac);   // 其余全部并入第 2 位（保 Σ=255）
            // ⚠️ 主导份额必须最大：并入后次席可能反超（三等分 85/85/85 → 次席 170）→
            //   下 tick 聚合反超 → 主导 key 振荡（S3 曾抓）。交换保 DomFrac ≥ SecFrac。
            if (r[1].Frac > r[0].Frac) (r[0], r[1]) = (r[1], r[0]);
        }
        // [临时调试] 畸形输出检测（主导 null + 次席满额——交换把 null 提到主导？）
        if (r[0].Key == null && r[1].Key != null)
            Godot.GD.Print($"[PopMerge调试] 畸形输出 total={total:F0} 实体数={entities.Count} list0=({list[0].Key},{list[0].Value:F0}) list1=({(list.Count > 1 ? list[1].Key : "-")},{(list.Count > 1 ? list[1].Value : 0):F0}) r0=({r[0].Key},{r[0].Frac}) r1=({r[1].Key},{r[1].Frac})");
        return r;
    }

    private static void AddFrac(Dictionary<string, float> d, string key, float v)
    {
        if (key == null || v <= 0f) return;
        d.TryGetValue(key, out float cur);
        d[key] = cur + v;
    }

    private static void SwapDom(ShareEntry[] s)
    {
        (s[0], s[1]) = (s[1], s[0]);
    }

    // ── 宗教 5 段份额场（ShareEntry[5]，key 固定 ReligionStage.All；Σ=255）──

    public static ShareEntry[] NewReligion(string stageKey)
    {
        var r = new ShareEntry[ReligionStage.Count];
        for (int k = 0; k < ReligionStage.Count; k++)
            r[k] = new ShareEntry { Key = ReligionStage.All[k], Frac = 0 };
        int idx = ReligionIndex(stageKey);
        if (idx >= 0) r[idx].Frac = 255;
        return r;
    }

    /// <summary>key → 固定段索引（-1=未知）。</summary>
    public static int ReligionIndex(string key)
    {
        for (int k = 0; k < ReligionStage.Count; k++)
            if (ReligionStage.All[k] == key) return k;
        return -1;
    }

    public static int RelFrac(ShareEntry[] r, string key)
    {
        int idx = ReligionIndex(key);
        return idx >= 0 && r != null && idx < r.Length ? r[idx].Frac : 0;
    }

    /// <summary>宗教份额转移（守恒）：from → to 转 amt。返回是否发生。</summary>
    public static bool RelTransfer(ShareEntry[] r, string from, string to, int amt)
    {
        int fi = ReligionIndex(from), ti = ReligionIndex(to);
        if (fi < 0 || ti < 0) return false;
        int take = Math.Min(amt, r[fi].Frac);
        if (take <= 0) return false;
        r[fi].Frac = (byte)(r[fi].Frac - take);
        r[ti].Frac = (byte)Math.Min(Unit, r[ti].Frac + take);
        return true;
    }

    /// <summary>主导宗教（份额最大段的 key）。</summary>
    public static string DomReligion(ShareEntry[] r)
    {
        int best = 0;
        for (int k = 1; k < ReligionStage.Count; k++)
            if (r[k].Frac > r[best].Frac) best = k;
        return ReligionStage.All[best];
    }

    /// <summary>宗教格级聚合（按人口加权，Σ 归一 255；key 固定不变）。</summary>
    public static ShareEntry[] RelPopMerge(IReadOnlyList<CivEntity> entities)
    {
        float total = 0f;
        var sum = new float[ReligionStage.Count];
        foreach (var e in entities)
        {
            if (e.Dead || e.P <= 0f) continue;
            total += e.P;
            for (int k = 0; k < ReligionStage.Count; k++)
                sum[k] += e.ReligionShare[k].Frac / 255f * e.P;
        }
        var r = NewReligion(ReligionStage.Animism);
        if (total <= 0f) { r[ReligionIndex(ReligionStage.Animism)].Frac = 255; return r; }
        for (int k = 0; k < ReligionStage.Count; k++)
            r[k].Frac = (byte)Math.Min(Unit, MathF.Round(sum[k] / total * Unit));
        return r;
    }
}
