using Godot;
using System;

namespace World.MapGen;

/// <summary>
/// headless 验证工具（2026-08-03）：
///   InstallFatalHandler() 注册未捕获异常 → headless 下立即 Exit(1)——
///   任务异常直接终止看问题，不空转等 --quit-after 帧数。
///   命令侧配合：$? 非 0 = 任务失败（不傻等）。
/// </summary>
public static class DiagUtil
{
    private static bool _installed;

    public static void InstallFatalHandler()
    {
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            GD.PrintErr($"[FATAL] 未捕获异常: {e.ExceptionObject}");
            if (e.ExceptionObject is Exception ex && ex.StackTrace != null)
                GD.PrintErr(ex.StackTrace);
            if (OS.HasFeature("headless"))
                System.Environment.Exit(1);   // 非零退出码 → 命令侧立即判定失败
        };
    }
}
