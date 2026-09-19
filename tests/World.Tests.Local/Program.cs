using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using World.Utils;    // DeterministicRandom（2026-09-03 迁至 World.Utils）

namespace World.Tests.Local;

/// <summary>
/// 零依赖本地测试执行器。
/// 用途：沙箱/离线环境无法启动 vstest testhost（父进程句柄被禁）时的替代入口；
/// 与 CI 的 `dotnet test` 跑**同一套** NUnit 测试——反射扫描 [Test]/[TestCase] 直接进程内执行。
/// 退出码：0 = 全绿；1 = 有失败。输出 PASS/FAIL 行（与 verify.sh 的 grep 约定兼容）。
/// </summary>
public static class Program
{
    /// <summary>可选类名过滤（子串，逗号分隔；如 `--filter=H3FlexureTests,H3ThermalTests`）。
    /// 省略 = 跑全量（原行为）。过滤只影响执行范围与耗时，不改任何测试逻辑。</summary>
    public static int Main(string[] args)
    {
        var asm = typeof(World.Tests.DeterministicRandomTests).Assembly;
        int pass = 0, fail = 0;
        var failures = new List<string>();

        var filters = args
            .Where(a => a.StartsWith("--filter=", StringComparison.Ordinal))
            .SelectMany(a => a.Substring("--filter=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        var testClasses = asm.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Tests"))
            .Where(t => filters.Length == 0 || filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.Name);

        foreach (var cls in testClasses)
        {
            foreach (var method in cls.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length > 0
                                  || m.GetCustomAttributes(typeof(TestCaseAttribute), false).Length > 0)
                         .OrderBy(m => m.Name))
            {
                var cases = method.GetCustomAttributes(typeof(TestCaseAttribute), false)
                    .Cast<TestCaseAttribute>()
                    .Select(a => a.Arguments)
                    .ToList();
                if (cases.Count == 0) cases.Add(Array.Empty<object>());

                object instance = null;
                if (!method.IsStatic) instance = Activator.CreateInstance(cls);

                foreach (var caseArgs in cases)
                {
                    string label = cases.Count > 1
                        ? $"{method.Name}({string.Join(", ", caseArgs.Select(a => a?.ToString() ?? "null"))})"
                        : method.Name;
                    try
                    {
                        method.Invoke(instance, caseArgs.Length == 0 ? null : caseArgs);
                        pass++;
                        Console.WriteLine($"  PASS {cls.Name}.{label}");
                    }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie && tie.InnerException != null
                            ? tie.InnerException
                            : ex;
                        fail++;
                        failures.Add($"{cls.Name}.{label}");
                        Console.WriteLine($"  FAIL {cls.Name}.{label}: {LastExpectation(inner.Message)}");
                        // 失败位置（第一条测试代码帧）——NUnit 的消息常不带行号，定位全靠它
                        var frame = (inner.StackTrace ?? "").Split('\n')
                            .FirstOrDefault(l => l.Contains("World.Tests") && !l.Contains("Program.cs"));
                        if (frame != null) Console.WriteLine($"       ↳{frame.Trim()}");
                        if (Environment.GetEnvironmentVariable("ZT_FULL_STACK") == "1")
                            Console.WriteLine(inner.StackTrace);
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"════════ {pass} 通过 / {fail} 失败 ════════");
        if (fail > 0)
        {
            foreach (var f in failures) Console.WriteLine($"  ❌ {f}");
            return 1;
        }
        Console.WriteLine("🎉 全部通过");
        return 0;
    }

    /// <summary>只取消息里**最后**一个 `Expected:` 块。
    /// ⚠️ 为什么需要：NUnit 3 把断言失败记在 `TestExecutionContext.CurrentContext.CurrentResult` 上，
    /// 而本执行器**不走** NUnit 的 setup/teardown ⇒ 上下文跨测试复用 ⇒ 前面的测试失败过之后，
    /// 后续测试的异常消息会把历史条目一起带上（"Multiple failures or warnings in test: 1) … 2) …"），
    /// 直接打印会看到**别的测试**的断言文本（2026-09-18 被这个坑掉两轮）。
    /// pass/fail 计数一直是对的（它只看抛没抛），歪的只是消息；**最后一块 = 本次测试的**。
    /// 非断言异常（越界/NaN 那类）没有 `Expected:` ⇒ 原样返回。</summary>
    static string LastExpectation(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        int last = message.LastIndexOf("Expected:", StringComparison.Ordinal);
        return last > 0 ? message.Substring(last).Replace("\n", " ").Trim() : message;
    }

    /// <summary>清掉 NUnit 当前 test context 里累积的断言失败。
    /// ⚠️ 为什么需要：NUnit 3 把每次断言失败记在 `TestExecutionContext.CurrentContext.CurrentResult` 上，
    /// 而本执行器**不走** NUnit 的 setup/teardown ⇒ 上下文跨测试复用 ⇒ 某个测试失败后，**后续**测试的
    /// 失败消息会带上历史条目（"Multiple failures or warnings in test: 1) 上一条 2) 本条"）。</summary>
    static void ResetNUnitFailures()
    {
        try
        {
            var result = NUnit.Framework.Internal.TestExecutionContext.CurrentContext?.CurrentResult;
            if (result == null) return;
            var field = result.GetType().GetField("_assertionResults",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(result) is System.Collections.IList list) list.Clear();
        }
        catch
        {
            // 探测失败不影响判分与执行，只影响消息可读性：直接放行
        }
    }
}
