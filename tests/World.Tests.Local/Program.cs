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
/// 退出码：0 = 全绿；1 = 有失败；3 = 挂死看门狗触发（用例超时未返回）。
/// 输出 PASS/FAIL 行（与 verify.sh 的 grep 约定兼容）；另在**每个用例调用前**打印 `RUN` 行（见下）。
/// 用法：`World.Tests.Local.exe [--filter=类名子串,..] [--include-deep] [--timeout=秒] [--no-timeout]`。
///
/// 挂死看门狗（2026-09-27 补）：本执行器**不能**用 `dotnet test --blame-hang-timeout`——本套件不是 VSTest
/// 跑的（csproj 是 OutputType=Exe、无 Test.Sdk/NUnit3TestAdapter、无 .runsettings）。旧实现只有逐测试
/// Stopwatch、**无任何超时**，且用例名要等通过后才打印 ⇒ 自旋用例会把整条链占死（2026-09-27 两次 8+ 分钟
/// 单核满跑），日志里还看不出是谁。现在：调用前打印用例名 ⇒ 挂住时日志最后一条 `RUN` 即元凶；
/// 超时由**看门狗线程**就地 `Environment.Exit(3)`（.NET Core 上 [Timeout]/Thread.Abort 均不可用，
/// 托管线程无法安全终结 ⇒ 就地退出是唯一能真正终结 CPU 自旋的手段），并打印人名 + 就地计数 + 非零退出。
/// 兼容性：PASS/FAIL/SKIP 行格式与末尾计数行**一字未动**（历史日志可比），新增的只有 `RUN` 行与 HANG 块。
/// </summary>
public static class Program
{
    /// <summary>可选类名过滤（子串，逗号分隔；如 `--filter=H3FlexureTests,H3ThermalTests`）。
    /// 省略 = 跑全量（原行为）。过滤只影响执行范围与耗时，不改任何测试逻辑。
    /// Deep 分层：默认跳过 [Category("Deep")] 的长跑测试（分钟级模拟大户）；--include-deep 才跑。
    /// 看门狗：`--timeout=秒` 覆盖默认 120s（Deep 用例自动 ×5）；`--no-timeout` 整关。</summary>
    public static int Main(string[] args)
    {
        var asm = typeof(World.Tests.DeterministicRandomTests).Assembly;
        int pass = 0, fail = 0, skip = 0;
        var failures = new List<string>();

        var filters = args
            .Where(a => a.StartsWith("--filter=", StringComparison.Ordinal))
            .SelectMany(a => a.Substring("--filter=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
        // Deep 分层（测试分级纪律）：打 [Category("Deep")] 的类/方法 = 长跑大户（分钟级模拟），
        // 默认跳过，保证默认全套 ≤3 分钟；--include-deep 才跑（提交前验证口径）。
        bool includeDeep = args.Any(a => a == "--include-deep");

        // ── 挂死看门狗（P1，2026-09-27）─────────────────────────────────────────────
        // 阈值：默认 120 s（正常用例毫秒-秒级，120 s 足够宽；改成更小值前先看一遍慢用例的实测耗时）。
        // Deep 用例预算 ×5：分钟级长跑大户本就该慢，别误杀（仍可被 --timeout 直接覆盖）。
        int timeoutSec = 120;
        bool timeoutEnabled = true;                 // --no-timeout 整关（防误判 / 深跑用）
        foreach (var a in args)
        {
            if (a.StartsWith("--timeout=", StringComparison.Ordinal)
                && int.TryParse(a.Substring("--timeout=".Length), out var tv) && tv > 0)
                timeoutSec = tv;
            else if (a == "--no-timeout") timeoutEnabled = false;
        }

        string currentTest = null;                  // 当前用例名（null = 空闲）
        long currentStart = 0, currentBudgetMs = 0;
        var hangFired = 0;
        if (timeoutEnabled)
        {
            var watchdog = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(250);
                    var name = System.Threading.Volatile.Read(ref currentTest);
                    if (name == null) continue;
                    long budget = System.Threading.Interlocked.Read(ref currentBudgetMs);
                    long elapsedMs = (DateTime.UtcNow.Ticks - System.Threading.Interlocked.Read(ref currentStart))
                                     / TimeSpan.TicksPerMillisecond;
                    if (elapsedMs <= budget) continue;
                    if (System.Threading.Interlocked.Exchange(ref hangFired, 1) != 0) continue;
                    Console.WriteLine();
                    Console.WriteLine($"HANG: {name} —— 超过 {budget / 1000}s 未返回（自旋/死循环）。");
                    Console.WriteLine($"      已跑计数（就地落地）：PASS={pass} FAIL={fail} SKIP={skip}");
                    Console.WriteLine($"      复现：--filter={name.Split('.')[0]} 单类跑；退出码 3 = 挂死（1 = 有失败）。");
                    Console.Out.Flush();
                    Environment.Exit(3);
                }
            })
            { IsBackground = true, Name = "hang-watchdog" };
            watchdog.Start();
        }

        var testClasses = asm.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Tests"))
            .Where(t => filters.Length == 0 || filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(t => t.Name);

        foreach (var cls in testClasses)
        {
            // 类级 Deep：整个类都是长跑大户 → 一行说明后整类跳过（默认口径 ≤3 分钟）
            if (!includeDeep && IsDeep(null, cls))
            {
                skip++;                                          // 计数只进 HANG 块，不改末尾计数行口径
                Console.WriteLine($"  SKIP {cls.Name}（Deep 长跑分层，默认不跑；--include-deep 开启）");
                continue;
            }
            // 类边界标记：全量卡死时最后打印的类名 = 元凶类（跨类状态污染定位口，2026-09-24）
            Console.WriteLine($"── {cls.Name}");

            foreach (var method in cls.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length > 0
                                  || m.GetCustomAttributes(typeof(TestCaseAttribute), false).Length > 0)
                         .Where(m => includeDeep || !IsDeep(m, cls))      // 方法级 Deep：只跳过打标的方法
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
                    string caseName = $"{cls.Name}.{label}";
                    // ① 调用前打印用例名：挂住时日志里最后一条 RUN 即元凶（旧实现通过后才打印 ⇒ 挂住即失明）
                    Console.WriteLine($"  RUN  {caseName}");
                    // 看门狗计时：先写起始时刻、再写用例名（看门狗见名必有起始值，不会拿 0 当起点）
                    currentBudgetMs = (IsDeep(method, cls) ? timeoutSec * 5 : timeoutSec) * 1000L;
                    currentStart = DateTime.UtcNow.Ticks;
                    System.Threading.Volatile.Write(ref currentTest, caseName);
                    var sw = System.Diagnostics.Stopwatch.StartNew();   // 逐测试耗时：3 分钟预算的计量基础
                    try
                    {
                        method.Invoke(instance, caseArgs.Length == 0 ? null : caseArgs);
                        pass++;
                        Console.WriteLine($"  PASS {caseName} [{FormatMs(sw.ElapsedMilliseconds)}]");
                    }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie && tie.InnerException != null
                            ? tie.InnerException
                            : ex;
                        fail++;
                        failures.Add(caseName);
                        Console.WriteLine($"  FAIL {caseName} [{FormatMs(sw.ElapsedMilliseconds)}]: {LastExpectation(inner.Message)}");
                        // 失败位置（第一条测试代码帧）——NUnit 的消息常不带行号，定位全靠它
                        var frame = (inner.StackTrace ?? "").Split('\n')
                            .FirstOrDefault(l => l.Contains("World.Tests") && !l.Contains("Program.cs"));
                        if (frame != null) Console.WriteLine($"       ↳{frame.Trim()}");
                        if (Environment.GetEnvironmentVariable("ZT_FULL_STACK") == "1")
                            Console.WriteLine(inner.StackTrace);
                    }
                    finally
                    {
                        System.Threading.Volatile.Write(ref currentTest, null);   // 结束即撤标记
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

    /// <summary>Deep 分层判定（测试分级纪律）：类或方法打了 [Category("Deep")] = 长跑大户。
    /// 类级判定传 method=null 只看类特性；方法级判定 = 方法特性 ∪ 类特性（类打了标则全体都算）。
    /// 为什么不用命名约定：Category 是 NUnit 原生语义，dotnet test/vstest 同样识别，两层口径一致。</summary>
    static bool IsDeep(MethodInfo method, Type type)
    {
        const string DeepCategory = "Deep";
        var methodCategories = method?.GetCustomAttributes(typeof(CategoryAttribute), false)
            .Cast<CategoryAttribute>().ToList();
        if (methodCategories != null && methodCategories.Any(c => c.Name == DeepCategory)) return true;
        return type.GetCustomAttributes(typeof(CategoryAttribute), false)
            .Cast<CategoryAttribute>().Any(c => c.Name == DeepCategory);
    }

    /// <summary>耗时格式化：秒以下给 ms，秒以上给 s（一位小数）。</summary>
    static string FormatMs(long ms) => ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:F1}s";

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
    /// 失败消息会带上历史条目（"Multiple failures or warnings in test: 1) 上一条 2) 本条"）。
    /// ⚠️ 现状（2026-09-27 复核登记，未改）：本方法**当前无调用点**（死代码）——实际靠 LastExpectation
    /// 取"最后一块"绕过；接线会改变 FAIL 行的消息内容 ⇒ 属"改输出口径"，需单独一批并同步基线。</summary>
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
