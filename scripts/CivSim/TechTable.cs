using System;
using System.Collections.Generic;
using Godot;

namespace World.CivSim;

/// <summary>技术定义（来自 res://data/techs.csv，数据驱动）。</summary>
public sealed class TechDef
{
    public int Id;
    public string Name;
    public int Epoch;            // 0=石器 1=新石器 2=青铜 3=铁器 4=古典/中世纪
    public int[] Requires;       // 前置技术 id
    public float InvPop;         // 发明人口门槛（部落人口）；-1=特殊判定（农业：格 K 利用率 > 大陆 P80）
    public string[] InvEnv;      // 发明环境（部落所在格 biome 集合；any=不限）
    public float InvProb;        // 每 tick 发明概率（条件满足时）
    public float SpreadBase;     // 传播概率/tick/接触（贸易 ×2）
    public string Effect;        // carry=承载乘数 / unlock_cold / unlock_sea / unlock_settle / mod_*
    public float Value;          // 效果数值
}

/// <summary>技术表：从 CSV 加载，提供按 id 查询 + 位掩码工具。</summary>
public static class TechTable
{
    public const string CsvPath = "res://data/techs.csv";
    private static TechDef[] _techs;
    private static bool _loaded;

    /// <summary>全部技术（按 id 索引；CSV 顺序即 id）。</summary>
    public static TechDef[] All
    {
        get
        {
            if (!_loaded) Load();
            return _techs;
        }
    }

    public static int Count => All.Length;

    /// <summary>加载技术表（幂等；CSV 解析失败打印错误）。</summary>
    public static void Load()
    {
        if (_loaded) return;
        var list = new List<TechDef>();
        using var f = FileAccess.Open(CsvPath, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PrintErr($"[TechTable] cannot open {CsvPath}: {FileAccess.GetOpenError()}");
            _techs = Array.Empty<TechDef>();
            _loaded = true;
            return;
        }
        bool first = true;
        while (!f.EofReached())
        {
            string line = f.GetLine().Trim();
            if (first) { first = false; continue; }   // 跳过表头
            if (line.Length == 0) continue;
            var c = line.Split(',');
            if (c.Length < 10) continue;
            var t = new TechDef
            {
                Id = int.Parse(c[0]),
                Name = c[1],
                Epoch = int.Parse(c[2]),
                Requires = c[3].Length > 0 ? ParseInts(c[3]) : Array.Empty<int>(),
                InvPop = float.Parse(c[4]),
                InvEnv = c[5].Length > 0 && c[5] != "any" ? c[5].Split(';') : Array.Empty<string>(),
                InvProb = float.Parse(c[6]),
                SpreadBase = float.Parse(c[7]),
                Effect = c[8],
                Value = float.Parse(c[9]),
            };
            list.Add(t);
        }
        _techs = list.ToArray();
        _loaded = true;
        GD.Print($"[TechTable] loaded {_techs.Length} techs: {string.Join(" / ", Array.ConvertAll(_techs, t => t.Name))}");
    }

    private static int[] ParseInts(string s)
    {
        var p = s.Split(';');
        var r = new int[p.Length];
        for (int i = 0; i < p.Length; i++) r[i] = int.Parse(p[i]);
        return r;
    }

    // ── 位掩码工具（技术集合 = ulong 位掩码，25 项 < 64）──

    public static bool Has(ulong flags, int techId) => techId >= 0 && techId < 64 && (flags & (1UL << techId)) != 0;
    public static ulong Set(ulong flags, int techId) => techId >= 0 && techId < 64 ? flags | (1UL << techId) : flags;
    public static bool HasAll(ulong flags, int[] ids)
    {
        foreach (var id in ids) if (!Has(flags, id)) return false;
        return true;
    }

    /// <summary>部落最高技术时代（0-4；无技术=0）。</summary>
    public static int MaxEpoch(ulong flags)
    {
        int max = 0;
        for (int i = 0; i < Count; i++)
            if (Has(flags, i) && _techs[i].Epoch > max) max = _techs[i].Epoch;
        return max;
    }

    /// <summary>已获技术数。</summary>
    public static int CountTech(ulong flags)
    {
        int c = 0;
        for (int i = 0; i < Count; i++) if (Has(flags, i)) c++;
        return c;
    }

    /// <summary>技术位掩码的承载乘数（carry 效果连乘；极寒解锁单独处理）。</summary>
    public static float CarryFactor(ulong flags)
    {
        float f = 1f;
        for (int i = 0; i < Count; i++)
            if (Has(flags, i) && _techs[i].Effect == "carry")
                f *= _techs[i].Value;
        return f;
    }
}
