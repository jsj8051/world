using Godot;
using System;
using System.Collections.Generic;

namespace World.Diagnostics;

/// <summary>性能历史日志（2026-08-17 监督机制：防劣化）。
/// 每次生成（MapGen 分段 / CivSim 演化）自动追加一条记录 → user://perf_history.json，
/// 格式：kind|label|时间戳|k1=v1,k2=v2,...（每行一条；追加写，保留最近 MaxEntries 条）。
/// 查询：Stats(kind, key) 返回历史均值/最大/条数（不含最新——对比"上次以来"）。
/// 告警阈值：本次 > 历史均值 ×1.5 → 劣化提示（调用方打印）。</summary>
public static class PerfLog
{
    private const string Path = "user://perf_history.json";
    private const int MaxEntries = 200;   // 滚动窗口（~200 次生成）

    /// <summary>追加一条记录（kind=mapgen/civsim；label=参数摘要）。</summary>
    public static void Append(string kind, string label, Dictionary<string, long> timings)
    {
        var lines = new List<string>();
        if (FileAccess.FileExists(Path))
        {
            string existing = FileAccess.GetFileAsString(Path);
            if (!string.IsNullOrEmpty(existing))
            {
                foreach (var ln in existing.Split('\n'))
                    if (ln.Trim().Length > 0 && !ln.StartsWith("#")) lines.Add(ln.Trim());
            }
        }
        lines.Add($"{kind}|{label}|{DateTime.Now:yyyy-MM-dd HH:mm:ss}|{Serialize(timings)}");
        while (lines.Count > MaxEntries) lines.RemoveAt(0);   // 滚动窗口
        using var f = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PrintErr($"[PerfLog] 无法写 {Path}（仅本次计时）");
            return;
        }
        f.StoreString("# perf history: kind|label|ts|k=v,...\n" + string.Join("\n", lines) + "\n");
    }

    /// <summary>历史统计（kind+key；均值/最大/条数——不含本次追加的那条之外的语义：调用前 Append 已写入，
    /// 故 Stats 读的是含本次的历史——对比时用"历史均值"含本次无碍（窗口滚动））。</summary>
    public static (double avg, long max, int count) Stats(string kind, string key)
    {
        double sum = 0; long max = 0; int count = 0;
        foreach (var (k, v) in Enumerate(kind, key))
        {
            sum += v; if (v > max) max = v; count++;
        }
        return count > 0 ? (sum / count, max, count) : (0, 0, 0);
    }

    /// <summary>遍历历史中 kind+key 的值（时间序）。</summary>
    public static IEnumerable<(string kind, long v)> Enumerate(string kind, string key)
    {
        if (!FileAccess.FileExists(Path)) yield break;
        string text = FileAccess.GetFileAsString(Path);
        foreach (var ln in text.Split('\n'))
        {
            string line = ln.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var seg = line.Split('|');
            if (seg.Length < 3 || seg[0] != kind) continue;
            foreach (var kv in seg[3].Split(','))
            {
                var p = kv.Split('=');
                if (p.Length == 2 && p[0] == key && long.TryParse(p[1], out long v))
                    yield return (seg[0], v);
            }
        }
    }

    /// <summary>汇总打印（T41/生成完成时用）：kind 各 key 的 均值/最大/最近值。</summary>
    public static void Summarize(string kind, string title)
    {
        var keys = new Dictionary<string, (double sum, long max, int n, long last)>();
        if (!FileAccess.FileExists(Path)) { GD.Print($"[性能] {title}：无历史记录"); return; }
        string text = FileAccess.GetFileAsString(Path);
        foreach (var ln in text.Split('\n'))
        {
            string line = ln.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var seg = line.Split('|');
            if (seg.Length < 4 || seg[0] != kind) continue;
            foreach (var kv in seg[3].Split(','))
            {
                var p = kv.Split('=');
                if (p.Length != 2 || !long.TryParse(p[1], out long v)) continue;
                if (!keys.TryGetValue(p[0], out var st)) st = (0, 0, 0, 0);
                st.sum += v; if (v > st.max) st.max = v; st.n++; st.last = v;
                keys[p[0]] = st;
            }
        }
        if (keys.Count == 0) { GD.Print($"[性能] {title}：无记录"); return; }
        int total = 0;
        foreach (var kv in keys) { total = kv.Value.n; break; }   // 各 key 条数相同
        var sb = new System.Text.StringBuilder($"[性能] {title} 历史 {total} 条: ");
        foreach (var kv in keys)
            sb.Append($"{kv.Key}={kv.Value.sum / kv.Value.n:F0}ms(均)/{kv.Value.max}ms(峰) ");
        GD.Print(sb.ToString());
    }

    private static string Serialize(Dictionary<string, long> t)
    {
        var parts = new List<string>(t.Count);
        foreach (var kv in t) parts.Add($"{kv.Key}={kv.Value}");
        return string.Join(",", parts);
    }
}
