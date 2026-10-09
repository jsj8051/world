using Godot;
using System;
using System.Collections.Generic;
using World.Services;

namespace World.CivSim;

/// <summary>
/// 技术定义（来自 res://data/techs.csv，数据驱动）。
/// v4 能力-科技框架：key 字符串标识（存档/诊断可读，非位掩码）；使用 = 实体每 tick 收益选择。
/// 无时代字段——科技按依赖链排位；时代 = 反应性标签（IsFarming → 新石器）。
/// </summary>
public sealed class TechDef
{
    public string Key;
    public string Name;
    public string[] InvEnv;      // 发明环境硬门槛（any/coast/seed/...；§6.1 判定函数）
    public float InvRate;        // Kremer k_i（发明速度软因子）
    public float PRef;           // 参考人口 P_ref_i
    public float SpreadBase;     // 传播基础概率/tick/接触（极易 0.08/易 0.05/中 0.02/难 0.008）
    public string[] Requires;    // 依赖 key（硬门槛）
    public string[] Effects;     // "effect:value" 分号分隔

    public bool IsSeed;          // 种子（压力触发发明，仅起源区）
    public int SeedIndex;        // WildCrops 位 0-4（种子行序）
    public float AgriBase;       // agri:value → 种子基线倍数 ×Y_猎0（母科技=1）
    public float CarryMult;      // carry:value → 狩猎产量乘数链
    public float MilitMult;      // milit:value → 军事实力乘数链（2026-08-10 冲突机制：武器科技只进军事，与影响力解耦——"人口少但武器精良"的 band = 影响力弱但军事强）
    public bool UnlockCold; public float ColdMult;    // 火：寒冷区 K 下限 ×3
    public bool UnlockCold2; public float ColdMult2;   // 皮毛：再 ×3
    public bool UnlockSea;       // 独木舟：跨海
    public bool IsAgricultureConcept;   // agriculture 母科技·概念位（派生置位）
}

/// <summary>技术表：key 字符串索引；提供查询工具（加载后只读）。</summary>
public static class TechTable
{
    public const string CsvPath = "res://data/techs.csv";

    public const string StoneCore = "stone_core";
    public const string Fire = "fire";
    public const string Handaxe = "handaxe";
    public const string Clothing = "clothing";
    public const string Microlith = "microlith";
    public const string Bow = "bow";
    public const string Canoe = "canoe";
    public const string Storage = "storage";
    public const string Pottery = "pottery";
    public const string Livestock = "livestock";
    public const string Grinding = "grinding";
    public const string Agriculture = "agriculture";
    public const string SeedWheat = "seed_wheat";
    public const string SeedMillet = "seed_millet";
    public const string SeedRice = "seed_rice";
    public const string SeedCorn = "seed_corn";
    public const string SeedPotato = "seed_potato";

    public static readonly string[] SeedKeys =
        { SeedWheat, SeedMillet, SeedRice, SeedCorn, SeedPotato };

    private static TechDef[] _techs = Array.Empty<TechDef>();
    private static Dictionary<string, TechDef> _byKey = new();
    private static bool _loaded;

    public static IReadOnlyList<TechDef> All => _techs;
    public static int Count => _techs.Length;

    public static TechDef Get(string key) => _byKey.TryGetValue(key, out var t) ? t : null;

    public static bool Has(HashSet<string> keys, string key) => keys.Contains(key);

    /// <summary>已获种子 key 列表（实体内，诊断/母科技判定用）。</summary>
    public static List<string> HeldSeeds(HashSet<string> keys)
    {
        var r = new List<string>(2);
        foreach (var s in SeedKeys) if (keys.Contains(s)) r.Add(s);
        return r;
    }

    /// <summary>军事实力乘数链 Π milit（持武器科技；2026-08-10 冲突机制——与采集 CarryMult 解耦，
    /// 武器科技只进军事。冲突胜率 = P×MilitMult 对比）。</summary>
    public static float MilitaryMult(HashSet<string> keys)
    {
        float f = 1f;
        foreach (var t in _techs)
            if (t.MilitMult > 0f && keys.Contains(t.Key))
                f *= t.MilitMult;
        return f;
    }

    /// <summary>狩猎产量乘数链 Π carry（持科技，含研磨器；种子/母科技不计入）。</summary>
    public static float HuntingCarry(HashSet<string> keys)
    {
        float f = 1f;
        foreach (var t in _techs)
            if (t.CarryMult > 0f && keys.Contains(t.Key))
                f *= t.CarryMult;
        return f;
    }

    /// <summary>知识累积度（已获科技数，Kremer 累积性项）。</summary>
    public static int Knowledge(HashSet<string> keys) => keys.Count;

    /// <summary>母科技派生置位：持有任一种子 → agriculture 自动入集。</summary>
    public static void SyncAgriculture(HashSet<string> keys)
    {
        if (HeldSeeds(keys).Count > 0) keys.Add(Agriculture);
    }

    /// <summary>加载技术表（幂等；CSV 解析失败打印错误）。</summary>
    public static void Load()
    {
        if (_loaded) return;
        var list = new List<TechDef>();
        using var f = FileAccess.Open(CsvPath, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            LogService.LogErr("TechTable", $"cannot open {CsvPath}: {FileAccess.GetOpenError()}");
            _loaded = true;
            return;
        }
        bool first = true;
        int seedIdx = 0;
        while (!f.EofReached())
        {
            string line = f.GetLine().Trim();
            if (first) { first = false; continue; }
            if (line.Length == 0) continue;
            var c = line.Split(',');
            if (c.Length < 8) continue;
            var t = new TechDef
            {
                Key = c[0].Trim(),
                Name = c[1].Trim(),
                InvEnv = c[2].Trim() is "" or "any" ? Array.Empty<string>() : c[2].Trim().Split(';'),
                InvRate = float.TryParse(c[3], out var kr) ? kr : 0f,
                PRef = float.TryParse(c[4], out var pr) ? pr : 0f,
                SpreadBase = float.TryParse(c[5], out var sp) ? sp : 0f,
                Requires = c[6].Trim().Length > 0 ? c[6].Trim().Split(';') : Array.Empty<string>(),
                Effects = c[7].Trim().Length > 0 ? c[7].Trim().Split(';') : Array.Empty<string>(),
            };
            foreach (var e in t.Effects)
            {
                var p = e.Split(':');
                string kind = p[0].Trim();
                float v = p.Length > 1 && float.TryParse(p[1], out var fv) ? fv : 0f;
                switch (kind)
                {
                    case "carry": t.CarryMult = v; break;
                    case "milit": t.MilitMult = v; break;   // 武器科技军事乘数（2026-08-10 冲突机制）
                    case "unlock_cold": t.UnlockCold = true; t.ColdMult = v > 0f ? v : 3f; break;
                    case "unlock_cold2": t.UnlockCold2 = true; t.ColdMult2 = v > 0f ? v : 3f; break;
                    case "unlock_sea": t.UnlockSea = true; break;
                    case "agri":
                        if (t.Key == Agriculture) t.IsAgricultureConcept = true;
                        else { t.IsSeed = true; t.AgriBase = v; t.SeedIndex = seedIdx; seedIdx++; }
                        break;
                }
            }
            list.Add(t);
        }
        _techs = list.ToArray();
        _byKey = new Dictionary<string, TechDef>();
        foreach (var t in _techs) _byKey[t.Key] = t;
        _loaded = true;
        LogService.Log("TechTable", $"loaded {_techs.Length} techs: {string.Join(" / ", Array.ConvertAll(_techs, t => t.Key))}");
    }

    /// <summary>内存注入（仅测试——World.Tests 经 InternalsVisibleTo 调用；无 res:// 时构造迷你表）。
    /// ⚠️ 测试进程一次性：注入后其他用例共享此表（空表/迷你表对既有用例均安全——默认倍率 1/空遍历）。</summary>
    internal static void LoadForTest(IReadOnlyList<TechDef> defs)
    {
        _techs = defs == null ? Array.Empty<TechDef>() : new List<TechDef>(defs).ToArray();
        _byKey = new Dictionary<string, TechDef>();
        foreach (var t in _techs) _byKey[t.Key] = t;
        _loaded = true;
    }
}
