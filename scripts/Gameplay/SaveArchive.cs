using System;
using System.Collections.Generic;
using System.IO;
using World.CivSim;
using World.LogicGrid;
using World.Services;

namespace World.Gameplay;

/// <summary>
/// 游戏存档门面（2026-08-25 用户拍板：地图 ≠ 存档分层——地图是只读素材池，只有游玩产生的才叫存档）。
/// .sav = 世界快照（.cmp 同布局段表：HEAD/NATR/TRIB/…/STAT/PLAY）+ REFS 段（来源地图引用）。
/// 存档 = 选定一张地图后开始的那局游戏；多槽位；读档恢复到保存时 tick 继续。
/// 目录：游戏目录旁 userdata/saves/（UserPaths.SaveDir——不落 C 盘）。
/// </summary>
public static class SaveArchive
{
    public const string Ext = ".sav";

    /// <summary>槽名 → user:// 路径（写读统一经 CivMapArchive/UserPaths 解析）。</summary>
    public static string PathOf(string name) => $"user://saves/{name}{Ext}";

    /// <summary>写存档（世界快照 + 玩家状态 + 来源地图引用 REFS 段）。返回是否成功。</summary>
    public static bool Write(string name, GameGrid grid, CivSimResult result, string mapRefPath, bool log = true)
    {
        UserPaths.EnsureDirs();
        return CivMapArchive.Write(PathOf(name), grid, result, log, mapRefPath);
    }

    /// <summary>读存档（恢复世界 + 玩家状态；mapRefPath = 来源地图，普通 .cmp 无 REFS → null）。</summary>
    public static bool Read(string name, out GameGrid grid, out CivSimResult result, out string mapRefPath) =>
        CivMapArchive.Read(PathOf(name), out grid, out result, out mapRefPath);

    /// <summary>存档槽列表（*.sav 文件名；无目录 → 空）。</summary>
    public static List<string> ListSaves()
    {
        var names = new List<string>();
        string dir = UserPaths.SaveDir;
        if (!Directory.Exists(dir)) return names;
        foreach (var f in Directory.GetFiles(dir, "*" + Ext))
            names.Add(Path.GetFileNameWithoutExtension(f));
        names.Sort();
        return names;
    }

    /// <summary>删除存档槽（幂等；失败抛——调用方 UI 提示）。</summary>
    public static void Delete(string name) => ArchiveService.DeleteSave(PathOf(name));
}