using Godot;
using System;
using World.Services;

namespace World.Diagnostics;

/// <summary>
/// 场级对比工具（2026-08-19，P1-④ 公共校验）。
///
/// 历史教训：
///   · 存档往返校验 NaN 假 FAIL——diff 函数必须把 NaN 视为相等（NaN 位级无损往返，
///     Math.Abs(NaN−NaN)=NaN 让 maxDiff&lt;1e-3 恒 false）。全部 MaxDiff* 已内置此语义。
///   · 各 diag 各写一套 diff 曾出现参数交叉比较（假 FAIL）；此处统一，勿在 diag 里再造轮子。
///
/// ★2026-10-09：`World.Domain.Calendar` 已随 `World.Domain` 解散而删除 ⇒ 一年月数就地内联为
///   本类私有常量（原 `Calendar.MonthsPerYear` 迁出的唯一用途就是下面的 `Bytes2DDiff`）。
/// </summary>
public static class FieldCompare
{
    /// <summary>一年月数 = 12（原 `World.Domain.Calendar.MonthsPerYear`；月字段第二维长度）。</summary>
    const int MonthsPerYear = 12;

    /// <summary>标量相等（1e-6 容差）。</summary>
    public static bool Eq(string name, float a, float b)
    {
        if (Mathf.Abs(a - b) < 1e-6f) return true;
        LogService.LogErr("FieldCompare", $"{name} 不一致: {a} vs {b}");
        return false;
    }

    /// <summary>int 相等（支持成对：GridN/N 等不变量）。</summary>
    public static bool Eq(string name, int a, int b)
    {
        if (a == b) return true;
        LogService.LogErr("FieldCompare", $"{name} 不一致: {a} vs {b}");
        return false;
    }

    public static bool Eq(string name, int a, int b, int c, int d)
    {
        if (a == b && c == d) return true;
        LogService.LogErr("FieldCompare", $"{name} 不一致: {a}/{b} vs {c}/{d}");
        return false;
    }

    public static bool Eq(string name, bool a, bool b)
    {
        if (a == b) return true;
        LogService.LogErr("FieldCompare", $"{name} 不一致: {a} vs {b}");
        return false;
    }

    /// <summary>float 数组最大绝对差（NaN↔NaN 视为相等）。返回差值供日志。</summary>
    public static double MaxDiff(string name, float[] a, float[] b, out double diff)
    {
        diff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double da = a[i], db = b[i];
            if (double.IsNaN(da) && double.IsNaN(db)) continue;   // NaN 往返位级一致（存档里可能天然含 NaN）
            diff = Math.Max(diff, Math.Abs(da - db));
        }
        return diff;
    }

    /// <summary>Vector3 数组最大绝对差（分量级 NaN 视为相等）。</summary>
    public static double MaxDiff3(string name, Vector3[] a, Vector3[] b, out double diff)
    {
        diff = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double ax = a[i].X, ay = a[i].Y, az = a[i].Z;
            double bx = b[i].X, by = b[i].Y, bz = b[i].Z;
            if (double.IsNaN(ax) && double.IsNaN(bx)) { } else diff = Math.Max(diff, Math.Abs(ax - bx));
            if (double.IsNaN(ay) && double.IsNaN(by)) { } else diff = Math.Max(diff, Math.Abs(ay - by));
            if (double.IsNaN(az) && double.IsNaN(bz)) { } else diff = Math.Max(diff, Math.Abs(az - bz));
        }
        return diff;
    }

    /// <summary>byte 数组不同元素计数。</summary>
    public static int ByteDiff(string name, byte[] a, byte[] b, out int diff)
    {
        diff = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) diff++;
        return diff;
    }

    /// <summary>byte[][]（12 月 × n）不同元素计数。</summary>
    public static int Bytes2DDiff(string name, byte[][] a, byte[][] b, out int diff)
    {
        diff = 0;
        for (int m = 0; m < MonthsPerYear; m++)
            for (int i = 0; i < a[m].Length; i++)
                if (a[m][i] != b[m][i]) diff++;
        return diff;
    }

    /// <summary>int 数组不同元素计数。</summary>
    public static int IntDiff(string name, int[] a, int[] b, out int diff)
    {
        diff = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) diff++;
        return diff;
    }
}
