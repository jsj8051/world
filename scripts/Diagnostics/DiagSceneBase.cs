using Godot;
using System;
using System.Collections.Generic;

namespace World.Diagnostics;

/// <summary>
/// 诊断场景统一基类（L3，ADR-0003）。
/// 收编各 diag 场景的重复样板：命令行参数解析、PASS/FAIL 报告、退出。
/// 子类只写"测什么"；新诊断场景一律继承本类。
/// 迁移进度：TectonicsTest 已迁移（2026-08-19）；其余 diag 场景增量迁移。
/// </summary>
public abstract partial class DiagSceneBase : Node
{
    /// <summary>
    /// 解析命令行用户参数（Godot -- 之后的部分）。
    /// 兼容既有两种写法：
    ///   · --key=value（CivSimDiag 风格）
    ///   · --key value 与开关 --flag（TectonicsTest 风格；`--` 前缀自动剥离，大小写不敏感）
    /// 语义差异（相对旧 TectonicsTest 解析器）：`--seed --init` 这类"值缺失"输入中，
    /// 旧解析器会误吞下一个开关，本实现不会——行为更符合直觉。
    /// </summary>
    public static Dictionary<string, string> ParseUserArgs()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ua = OS.GetCmdlineUserArgs();
        for (int i = 0; i < ua.Length; i++)
        {
            string a = ua[i];
            string key = a.StartsWith("--") ? a.Substring(2) : a;
            int eq = key.IndexOf('=');
            if (eq >= 0)
            {
                args[key.Substring(0, eq)] = key.Substring(eq + 1);
                continue;
            }
            if (i + 1 < ua.Length && !ua[i + 1].StartsWith("--"))
            {
                args[key] = ua[i + 1];
                i++;
            }
            else
            {
                args[key] = "true";   // 开关（如 --init / --compare）
            }
        }
        return args;
    }

    /// <summary>PASS/FAIL 报告（输出格式与 scripts/verify.sh 的 grep 约定兼容）。</summary>
    protected static void Report(string name, bool ok, string data = "")
    {
        GD.Print($"  {(ok ? "PASS" : "FAIL")} {name}{(data.Length > 0 ? " | " + data : "")}");
    }

    /// <summary>headless 场景统一退出出口。</summary>
    protected void Quit(int code = 0) => GetTree().Quit(code);
}
