using Godot;
using System;
using System.IO;

namespace World.Services;

/// <summary>
/// 用户数据路径（2026-08-25 用户拍板：存档和地图都跟游戏放一起，不落 C 盘）。
/// 标准 Godot user:// 落在 %APPDATA%/Godot/app_userdata（C 盘）——改为游戏本体旁 userdata/ 目录：
///   开发期 = 项目目录（res:// 实体化）；发布期 = exe 所在目录（res:// 即 exe 旁，globalize 同效）。
/// 目录：游戏目录/userdata/maps（地图素材：.mpa/.cmp）与 /saves（游戏存档：.sav）。
/// 全部磁盘 IO 统一经 UserPaths.Resolve（根因修复——各 ResolvePath 是唯一汇聚点）。
/// </summary>
public static class UserPaths
{
    /// <summary>游戏本体旁 userdata 根（目录可能存在；CreateDirs 确保）。</summary>
    public static readonly string Root =
        Path.Combine(ProjectSettings.GlobalizePath("res://"), "userdata");

    /// <summary>地图目录（创建世界/演化产物：.mpa/.cmp——只读素材池）。</summary>
    public static readonly string MapDir = Path.Combine(Root, "maps");

    /// <summary>存档目录（游玩进程：.sav——多槽可读写）。</summary>
    public static readonly string SaveDir = Path.Combine(Root, "saves");

    /// <summary>确保根目录存在（写档前调用；幂等）。</summary>
    public static void EnsureDirs()
    {
        Directory.CreateDirectory(MapDir);
        Directory.CreateDirectory(SaveDir);
    }

    /// <summary>user:// 路径 → 游戏目录旁绝对路径（非 user:// 原样返回——诊断可给任意绝对路径）。</summary>
    public static string Resolve(string path)
    {
        if (path == null) return null;
        if (path.StartsWith("user://", StringComparison.Ordinal))
            return Path.Combine(Root, path.Substring("user://".Length).Replace('/', Path.DirectorySeparatorChar));
        return path;
    }

    /// <summary>一次性迁移旧数据（2026-08-25 路径改制：旧 user:// 在 C 盘 app_userdata——用户拍板不落 C 盘）。
    /// 旧目录（%APPDATA%/Godot/app_userdata/world/maps…）存在且新目录为空 → 移动全部文件过去。
    /// 幂等：新目录已有文件则跳过（防止重复移动覆盖）。启动入口（MainMenu._Ready）调用一次。</summary>
    public static void MigrateLegacyData()
    {
        try
        {
            EnsureDirs();
            string legacyRoot = ProjectSettings.GlobalizePath("user://");   // 旧 C 盘根部
            if (string.IsNullOrEmpty(legacyRoot) || string.Equals(legacyRoot, Root, StringComparison.OrdinalIgnoreCase))
                return;
            MigrateDir(Path.Combine(legacyRoot, "maps"), MapDir);
            MigrateDir(Path.Combine(legacyRoot, "saves"), SaveDir);
            LogService.Log("UserPaths", $"legacy migration done (from {legacyRoot})");
        }
        catch (Exception ex)
        {
            LogService.LogErr("UserPaths", $"迁移旧数据失败（可忽略——新数据目录照常工作）: {ex.Message}");
        }
    }

    /// <summary>移动单个子目录（旧 → 新；新目录已有文件则跳过）。</summary>
    private static void MigrateDir(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        if (Directory.Exists(to) && Directory.GetFiles(to).Length > 0) return;   // 新目录非空 → 已迁移过
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            string dest = Path.Combine(to, Path.GetFileName(f));
            if (!File.Exists(dest)) File.Move(f, dest);
        }
    }
}