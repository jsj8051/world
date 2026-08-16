using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace World.Tests.Local;

/// <summary>
/// 零依赖本地测试执行器。
/// 用途：沙箱/离线环境无法启动 vstest testhost（父进程句柄被禁）时的替代入口；
/// 与 CI 的 `dotnet test` 跑**同一套** NUnit 测试——反射扫描 [Test]/[TestCase] 直接进程内执行。
/// 退出码：0 = 全绿；1 = 有失败。输出 PASS/FAIL 行（与 verify.sh 的 grep 约定兼容）。
/// </summary>
public static class Program
{
    public static int Main()
    {
        var asm = typeof(World.Tests.DeterministicRandomTests).Assembly;
        int pass = 0, fail = 0;
        var failures = new List<string>();

        var testClasses = asm.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("Tests"))
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

                foreach (var args in cases)
                {
                    string label = cases.Count > 1
                        ? $"{method.Name}({string.Join(", ", args.Select(a => a?.ToString() ?? "null"))})"
                        : method.Name;
                    try
                    {
                        method.Invoke(instance, args.Length == 0 ? null : args);
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
                        Console.WriteLine($"  FAIL {cls.Name}.{label}: {inner.Message}");
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
}
