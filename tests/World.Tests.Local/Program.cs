using System;
using System.IO;
using System.Threading.Tasks;
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
        // 批量判读图分支（--maps=N）：逻辑层全链生成 + 等距圆柱投影出 PNG，绕过测试执行器
        if (Array.Exists(args, a => a.StartsWith("--maps=", StringComparison.Ordinal)))
            return RunMapBatch(args);
        // 连续图分支（--spin=seed）：同一世界正交投影旋转帧（绕轴 360°）+ 蒙太奇速览
        if (Array.Exists(args, a => a.StartsWith("--spin=", StringComparison.Ordinal)))
            return RunSpinBatch(args);

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

    // ── 批量判读图（--maps=N [--res=4] [--out=dir] [--w=1024] [--start=1] [--parallel=4]）──
    // 逻辑层全链（锚点→海陆场→投影→区域→骨架→地貌→合成）逐种子生成，等距圆柱投影出 PNG；
    // 色带 = ElevationBandMode.ElevationColor（与场景海拔模式同一单一事实源）。
    // Ball 同 res 只读共享（构建一次，100 张复用）；种子间完全独立可并行（各任务私有逻辑层）。
    static int RunMapBatch(string[] args)
    {
        int Parse(string key, int def)
        {
            var a = Array.Find(args, x => x.StartsWith("--" + key + "=", StringComparison.Ordinal));
            return a != null && int.TryParse(a.Substring(key.Length + 3), out var v) ? v : def;
        }
        int count = Parse("maps", 100);
        int res = Parse("res", 4);
        int width = Parse("w", 1024);
        int startSeed = Parse("start", 1);
        int parallel = Math.Clamp(Parse("parallel", 4), 1, 16);
        string outDir = "userdata/maps/batch100";
        var outArg = Array.Find(args, x => x.StartsWith("--out=", StringComparison.Ordinal));
        if (outArg != null) outDir = outArg.Substring(5);
        Directory.CreateDirectory(outDir);

        int height = width / 2;
        var swAll = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"== 批量判读图：{count} 张 res{res} {width}×{height} seed {startSeed}..{startSeed + count - 1} → {outDir}（并行 {parallel}）==");

        var ball = new World.NewHexWorld.Ball(res, 1f);

        // 像素 → 格索引表（一次预计算，全种子复用；等距圆柱：y=0 北极）
        var cellOfPixel = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            double lat = (90.0 - (y + 0.5) / height * 180.0) * Math.PI / 180.0;
            for (int x = 0; x < width; x++)
            {
                double lng = (-180.0 + (x + 0.5) / width * 360.0) * Math.PI / 180.0;
                ulong cell = World.Utils.H3.H3.LatLngToCell(new World.Utils.H3.LatLng(lat, lng), res);
                cellOfPixel[y * width + x] = ball.CellIndexOf(cell);
            }
        }

        int done = 0;
        object gate = new();
        var failures = new List<string>();
        Parallel.ForEach(Enumerable.Range(startSeed, count),
            new ParallelOptions { MaxDegreeOfParallelism = parallel },
            seed =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var layout = new World.NoiseWorld.WorldGen.ContinentLayout(seed, 7);
                    var field = new World.NoiseWorld.WorldGen.LandSeaField(layout,
                        new World.NoiseWorld.WorldGen.LandSeaParams { Seed = seed });
                    var proj = new World.NoiseWorld.WorldGen.H3LandSeaProjector();
                    proj.Generate(ball, field, 0.29f);
                    var regions = new World.NoiseWorld.WorldGen.GeologicalRegions(seed);
                    regions.Generate(ball, proj);
                    var mountains = new World.NoiseWorld.WorldGen.MountainSkeleton(seed);
                    mountains.Generate(ball, regions);
                    var landforms = new World.NoiseWorld.WorldGen.RegionalLandforms(seed);
                    landforms.Generate(ball, regions);
                    var composer = new World.NoiseWorld.WorldGen.HeightComposer(seed);
                    composer.Generate(ball, proj, regions, mountains, landforms);

                    var rgb = new byte[width * height * 3];
                    // relative relief（决策 05v2 §六/§七）：relief = h − 同域邻域均值 → 明暗调制
                    // （绝对高度给基础色，相对高度调明暗——盆地 380m 也能从色深读出"低洼"）
                    var h = composer.HeightM;
                    var relief = new float[h.Length];
                    var nbs = ball.CellNeighbors;
                    for (int i = 0; i < h.Length; i++)
                    {
                        float sum = h[i]; int cnt = 1;
                        foreach (int j in nbs[i])
                        {
                            if (proj.Land[j] != proj.Land[i]) continue;   // 同域（海/陆）内取均值
                            sum += h[j]; cnt++;
                        }
                        relief[i] = h[i] - sum / cnt;
                    }
                    for (int p = 0; p < cellOfPixel.Length; p++)
                    {
                        int i = cellOfPixel[p];
                        var c = World.NoiseWorld.ElevationBandMode.ElevationColor(h[i]);
                        float shade = Math.Clamp(1f + relief[i] / 1200f, 0.78f, 1.18f);   // 盆地偏暗、高地提亮
                        int o = p * 3;
                        rgb[o] = (byte)Math.Clamp(c.R * 255f * shade, 0f, 255f);
                        rgb[o + 1] = (byte)Math.Clamp(c.G * 255f * shade, 0f, 255f);
                        rgb[o + 2] = (byte)Math.Clamp(c.B * 255f * shade, 0f, 255f);
                    }
                    World.Utils.PngWriter.WriteRgb(Path.Combine(outDir, $"seed_{seed:D3}.png"), width, height, rgb);

                    lock (gate)
                    {
                        done++;
                        Console.WriteLine($"[{done:D3}/{count}] seed {seed} land={proj.LandFraction:P1} " +
                                          $"regions={regions.Regions.Length} ridges={mountains.Ridges.Length} {sw.ElapsedMilliseconds} ms");
                    }
                }
                catch (Exception ex)
                {
                    lock (gate) { failures.Add($"seed {seed}: {ex.Message}"); Console.WriteLine($"[FAIL] seed {seed}: {ex}"); }
                }
            });

        Console.WriteLine($"== 完成 {count - failures.Count}/{count}，总耗时 {swAll.Elapsed.TotalMinutes:F1} min → {outDir} ==");
        return failures.Count == 0 ? 0 : 1;
    }

    // ── 连续图（--spin=seed [--frames=36] [--w=512] [--tilt=18] [--res=4] [--out=dir]）──
    // 同一世界正交投影旋转帧：全链只生成一次，逐帧绕 Y 轴转 2π/frames 渲染（Orthographic，
    // 决策 05 §十六：全球观察用 Orthographic 而非等距圆柱）；色 = 海拔分档 × relief 明暗。
    // 产物 = 逐帧 PNG（spin_XX.png）+ 6×6 蒙太奇速览（montage.png）。
    static int RunSpinBatch(string[] args)
    {
        int Parse(string key, int def)
        {
            var a = Array.Find(args, x => x.StartsWith("--" + key + "=", StringComparison.Ordinal));
            return a != null && int.TryParse(a.Substring(key.Length + 3), out var v) ? v : def;
        }
        int seed = Parse("spin", 7);
        int frames = Math.Clamp(Parse("frames", 36), 2, 90);
        int size = Parse("w", 512);
        int res = Parse("res", 4);
        var tiltArg = Array.Find(args, x => x.StartsWith("--tilt=", StringComparison.Ordinal));
        float tiltDeg = tiltArg != null && float.TryParse(tiltArg.Substring(6), out var td) ? td : 18f;
        string outDir = $"userdata/maps/spin_seed{seed:D3}";
        var outArg = Array.Find(args, x => x.StartsWith("--out=", StringComparison.Ordinal));
        if (outArg != null) outDir = outArg.Substring(5);
        Directory.CreateDirectory(outDir);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"== 连续图：seed {seed} res{res} {frames} 帧 {size}×{size} 倾角 {tiltDeg}° → {outDir} ==");

        var ball = new World.NewHexWorld.Ball(res, 1f);
        var layout = new World.NoiseWorld.WorldGen.ContinentLayout(seed, 7);
        var field = new World.NoiseWorld.WorldGen.LandSeaField(layout,
            new World.NoiseWorld.WorldGen.LandSeaParams { Seed = seed });
        var proj = new World.NoiseWorld.WorldGen.H3LandSeaProjector();
        proj.Generate(ball, field, 0.29f);
        var regions = new World.NoiseWorld.WorldGen.GeologicalRegions(seed);
        regions.Generate(ball, proj);
        var mountains = new World.NoiseWorld.WorldGen.MountainSkeleton(seed);
        mountains.Generate(ball, regions);
        var landforms = new World.NoiseWorld.WorldGen.RegionalLandforms(seed);
        landforms.Generate(ball, regions);
        var composer = new World.NoiseWorld.WorldGen.HeightComposer(seed);
        composer.Generate(ball, proj, regions, mountains, landforms);
        Console.WriteLine($"生成完成 {sw.ElapsedMilliseconds} ms（regions={regions.Regions.Length} ridges={mountains.Ridges.Length}）");

        // 逐格色（海拔分档 × relief 明暗）——一次预计算，全部帧复用
        var h = composer.HeightM;
        var cellRgb = new byte[h.Length * 3];
        var nbs = ball.CellNeighbors;
        for (int i = 0; i < h.Length; i++)
        {
            float sum = h[i]; int cnt = 1;
            foreach (int j in nbs[i])
            {
                if (proj.Land[j] != proj.Land[i]) continue;
                sum += h[j]; cnt++;
            }
            float shade = Math.Clamp(1f + (h[i] - sum / cnt) / 1200f, 0.78f, 1.18f);
            var c = World.NoiseWorld.ElevationBandMode.ElevationColor(h[i]);
            cellRgb[i * 3] = (byte)Math.Clamp(c.R * 255f * shade, 0f, 255f);
            cellRgb[i * 3 + 1] = (byte)Math.Clamp(c.G * 255f * shade, 0f, 255f);
            cellRgb[i * 3 + 2] = (byte)Math.Clamp(c.B * 255f * shade, 0f, 255f);
        }

        float tilt = tiltDeg * MathF.PI / 180f;
        float cosT = MathF.Cos(tilt), sinT = MathF.Sin(tilt);
        var frame = new byte[size * size * 3];
        var bg = (byte)14;   // 太空底色
        var allFrames = new byte[frames][];

        for (int f = 0; f < frames; f++)
        {
            float lam = 2f * MathF.PI * f / frames;
            float cosL = MathF.Cos(lam), sinL = MathF.Sin(lam);
            for (int y = 0; y < size; y++)
            {
                float ny = 1f - 2f * (y + 0.5f) / size;
                for (int x = 0; x < size; x++)
                {
                    float nx = 2f * (x + 0.5f) / size - 1f;
                    float r2 = nx * nx + ny * ny;
                    int o = (y * size + x) * 3;
                    if (r2 > 1f)
                    {
                        frame[o] = bg; frame[o + 1] = bg; frame[o + 2] = bg;
                        continue;
                    }
                    float z = MathF.Sqrt(1f - r2);
                    // 相机系 → 倾角（绕 X）→ 自转（绕 Y）
                    float cy = ny * cosT - z * sinT;
                    float cz = ny * sinT + z * cosT;
                    float dx = nx * cosL + cz * sinL;
                    float dy = cy;
                    float dz = -nx * sinL + cz * cosL;
                    double lat = Math.Asin(Math.Clamp(dy, -1f, 1f));
                    double lng = Math.Atan2(dz, dx);
                    ulong cell = World.Utils.H3.H3.LatLngToCell(new World.Utils.H3.LatLng(lat, lng), res);
                    int ci = ball.CellIndexOf(cell);
                    if (ci < 0) { frame[o] = bg; frame[o + 1] = bg; frame[o + 2] = bg; continue; }
                    frame[o] = cellRgb[ci * 3];
                    frame[o + 1] = cellRgb[ci * 3 + 1];
                    frame[o + 2] = cellRgb[ci * 3 + 2];
                }
            }
            var copy = new byte[frame.Length];
            Array.Copy(frame, copy, frame.Length);
            allFrames[f] = copy;
            World.Utils.PngWriter.WriteRgb(Path.Combine(outDir, $"spin_{f:D2}.png"), size, size, frame);
        }

        // 6×6 蒙太奇速览（帧均匀抽样，最近邻半分辨率）
        int cols = 6, rows = (frames + 5) / 6;
        int cellPx = size / 2;
        var montage = new byte[cols * cellPx * rows * cellPx * 3];
        for (int f = 0; f < frames; f++)
        {
            int cx = f % cols, cy2 = f / cols;
            for (int y = 0; y < cellPx; y++)
                for (int x = 0; x < cellPx; x++)
                {
                    int sx = Math.Min(size - 1, x * 2);   // 半分辨率最近邻
                    int sy = Math.Min(size - 1, y * 2);
                    int so = (sy * size + sx) * 3;
                    int mo = ((cy2 * cellPx + y) * cols * cellPx + cx * cellPx + x) * 3;
                    montage[mo] = allFrames[f][so];
                    montage[mo + 1] = allFrames[f][so + 1];
                    montage[mo + 2] = allFrames[f][so + 2];
                }
        }
        World.Utils.PngWriter.WriteRgb(Path.Combine(outDir, "montage.png"), cols * cellPx, rows * cellPx, montage);

        Console.WriteLine($"== 完成 {frames} 帧 {sw.Elapsed.TotalSeconds:F0}s → {outDir}（spin_00..{frames - 1:D2}.png + montage.png）==");
        return 0;
    }
}
