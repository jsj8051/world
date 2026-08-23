using Godot;
using System;
using System.IO;
using World.Biome;
using World.HexPlanet;
using World.Services;
using World.Utils;
using IOFileAccess = System.IO.FileAccess;

namespace World.LogicGrid;

/// <summary>
/// 游戏地图存档格式 .gmp（v3 段表，2026-08-23 段表化）。
///
/// 布局（docs/存档段表格式设计.md §2/§4）：
///   [4B]  magic "GMP1"
///   [2B]  skeletonVer = 3
///   [2B]  reserved
///   [..]  BODY 段（唯一段；内容 = GameMapArchive.WriteBody 全量，与 .cmp NATR 段共用）
///   [12B×1] 段表 + [12B] 尾目录
///
/// BODY 内部布局（v2 不变）：gridN/N/seed/radiusKm/自转+倾角+光强/场族/elev/temp/precip/
/// biome/river/lake/mineral/soil/monsoon/monthPrecip/monthTemp/currentDirs/warmth/strength/psi/
/// province/country——由 ArchiveLayout 字段表单源描述（BodyLength）。
///
/// 邻接不存档（确定性重建，见 GameGrid.BuildNeighbors）——省 ~1MB（n=64）且永不与顶点不一致。
/// v1/v2 旧格式读取分支于 2026-08-23 删除（用户拍板：旧档全删，只支持段表格式）。
/// IO 层从 FileAccess 迁移到 System.IO（存档往返可进单元测试）。
/// </summary>
public static class GameMapArchive
{
    public const string Magic = "GMP1";
    public const ushort Version = 3;   // v3：段表容器骨架（2026-08-23）；BODY 内部布局仍为 v2（psi 有）

    /// <summary>user:// 路径 → 绝对路径（System.IO 需要）。非 user:// 原样返回。</summary>
    private static string ResolvePath(string path) =>
        path.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(path)
            : path;

    /// <summary>写 .gmp（v3 段表：单一 BODY 段）。log=false：后台线程调用（禁止 GD.Print）。</summary>
    public static bool Write(string path, GameGrid g, bool log = true)
    {
        try
        {
            string abs = ResolvePath(path);
            string dir = Path.GetDirectoryName(abs) ?? "";
            if (dir.Length > 0 && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            using var fs = new FileStream(abs, FileMode.Create, IOFileAccess.Write);
            using var w = new ChunkWriter(fs, Magic, Version);
            w.BeginSegment("BODY", 1);
            WriteBody(w, g);
            w.EndSegment();
            w.Finish();
        }
        catch (Exception ex)
        {
            LogService.LogErr("GameMapArchive", $"写入失败 {path}: {ex.Message}");
            return false;
        }
        if (log)
            LogService.Log("GameMapArchive", $"wrote v{Version} {path} (gridN={g.GridN} tiles={g.N} land={LandCount(g)} " +
                     $"elev[{g.MinElev:F0},{g.MaxElev:F0}] province={CountNonZero(g.Province)})");
        return true;
    }

    /// <summary>BODY 段内容序列化（.gmp 唯一段 / .cmp NATR 段复用，布局与 ArchiveLayout 字段表严格一致）。</summary>
    public static void WriteBody(ChunkWriter f, GameGrid g)
    {
        int n = g.N;
        f.Store32((uint)g.GridN);
        f.Store32((uint)n);
        f.Store32((uint)g.Seed);
        f.StoreFloat(g.RadiusKm);
        f.Store8((byte)(g.ProgradeRotation ? 1 : 0));
        f.StoreFloat(g.RotationSpeed);
        f.StoreFloat(g.AxialTilt);
        f.StoreFloat(g.Insolation);
        f.StoreFloat(g.MinElev); f.StoreFloat(g.MaxElev);
        f.StoreFloat(g.MinTemp); f.StoreFloat(g.MaxTemp);
        f.StoreFloat(g.MinPrecip); f.StoreFloat(g.MaxPrecip);
        foreach (var v in g.Verts) { f.StoreFloat(v.X); f.StoreFloat(v.Y); f.StoreFloat(v.Z); }
        foreach (var v in g.Elev) f.StoreFloat(v);
        foreach (var v in g.Temp) f.StoreFloat(v);
        foreach (var v in g.Precip) f.StoreFloat(v);
        foreach (var v in g.Biome) f.Store8(v);
        foreach (var v in g.RiverLevel) f.Store8(v);
        foreach (var v in g.RiverFlow) f.Store32((uint)v);
        foreach (var v in g.RiverVolume) f.StoreFloat(v);
        foreach (var v in g.LakeLevel) f.Store8(v);
        foreach (var v in g.MineralLevel) f.Store8(v);
        foreach (var v in g.SoilLevel) f.Store8(v);
        foreach (var v in g.MonsoonLevel) f.Store8(v);
        for (int m = 0; m < MonsoonSystem.MonthCount; m++)
            foreach (var v in g.MonthPrecip[m]) f.Store8(v);
        for (int m = 0; m < MonsoonSystem.MonthCount; m++)
            foreach (var v in g.MonthTemp[m]) f.Store8(v);
        foreach (var v in g.CurrentDirs) { f.StoreFloat(v.X); f.StoreFloat(v.Y); f.StoreFloat(v.Z); }
        foreach (var v in g.CurrentWarmth) f.StoreFloat(v);
        foreach (var v in g.CurrentStrength) f.StoreFloat(v);
        // ⚠️ Psi 必须无条件写满（null → 补零）：ReadBody 无条件读该段，
        //   条件写会让无 Psi 的网格写档后自然段错位（实体段 count 读爆 → 卡死）
        if (g.Psi != null)
            foreach (var v in g.Psi) f.StoreFloat(v);
        else
            for (int i = 0; i < n; i++) f.StoreFloat(0f);
        foreach (var v in g.Province) f.Store32((uint)v);
        foreach (var v in g.Country) f.Store32((uint)v);
    }

    /// <summary>读 .gmp → GameGrid（自然层 + 人文层完整恢复，不依赖 .mpa）。</summary>
    public static bool Read(string path, out GameGrid g)
    {
        g = null;
        try
        {
            string abs = ResolvePath(path);
            using var fs = new FileStream(abs, FileMode.Open, IOFileAccess.Read);
            using var r = new ChunkReader(fs);
            if (r.Magic != Magic)
            {
                LogService.LogErr("GameMapArchive", $"bad magic in {path}");
                return false;
            }
            if (r.SkeletonVer != Version)
            {
                LogService.LogErr("GameMapArchive", $"不支持的存档版本 {r.SkeletonVer}（当前 {Version}；旧版 v1-v2 已于 2026-08-23 段表化移除）");
                return false;
            }
            if (!r.SeekSegment("BODY"))
            {
                LogService.LogErr("GameMapArchive", $"{path}: 缺 BODY 段");
                return false;
            }
            var grid = new GameGrid();
            if (!ReadBody(r, grid))
                return false;
            g = grid;
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogErr("GameMapArchive", $"读取失败 {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>BODY 段反序列化（与 WriteBody 严格对应；.cmp NATR 段复用）。
    /// 结构校验（GridN/N 不变量）：任何分配前拦截错位档。
    /// ⚠️ 布局长度断言（ArchiveLayout.BodyLength 单源）：读后必须 == 布局长度——
    ///   布局新增/漏读字段会立刻暴露（错位类 bug 的最后防线）。</summary>
    public static bool ReadBody(ChunkReader f, GameGrid grid)
    {
        long startPos = f.Position;
        grid.GridN = (int)f.Get32();
        grid.N = (int)f.Get32();
        // ⚠️ 2026-08-07：任何分配前先验结构不变量（球面 Goldberg：顶点数 ≡ 10n²+2，n∈[8,512]）。
        //   错位档 N 读到 11.7 亿 → new Vector3[N] = 14GB 卡死。用 long 防 10×n² 溢出回绕。
        long expectN = Icosahedron.VertexCountForLong(grid.GridN);
        if (grid.GridN < 8 || grid.GridN > 512 || (long)grid.N != expectN)
        {
            LogService.LogErr("GameMapArchive", $"结构校验失败：GridN={grid.GridN} N={grid.N}（期望 10n²+2={expectN}）。" +
                        $"存档正文错位或损坏，请重新生成（旧中间态 v4 档同样拒绝）。");
            return false;
        }
        grid.Seed = (int)f.Get32();
        grid.RadiusKm = f.GetFloat();
        grid.ProgradeRotation = f.Get8() != 0;
        grid.RotationSpeed = f.GetFloat();
        grid.AxialTilt = f.GetFloat();
        grid.Insolation = f.GetFloat();
        int n = grid.N;
        grid.MinElev = f.GetFloat(); grid.MaxElev = f.GetFloat();
        grid.MinTemp = f.GetFloat(); grid.MaxTemp = f.GetFloat();
        grid.MinPrecip = f.GetFloat(); grid.MaxPrecip = f.GetFloat();
        grid.Verts = new Vector3[n];
        for (int i = 0; i < n; i++)
            grid.Verts[i] = new Vector3(f.GetFloat(), f.GetFloat(), f.GetFloat());
        grid.Elev = ReadFloats(f, n);
        grid.Temp = ReadFloats(f, n);
        grid.Precip = ReadFloats(f, n);
        grid.Biome = ReadBytes(f, n);
        grid.RiverLevel = ReadBytes(f, n);
        grid.RiverFlow = ReadInts(f, n);
        grid.RiverVolume = ReadFloats(f, n);
        grid.LakeLevel = ReadBytes(f, n);
        grid.MineralLevel = ReadBytes(f, n);
        grid.SoilLevel = ReadBytes(f, n);
        grid.MonsoonLevel = ReadBytes(f, n);
        grid.MonthPrecip = new byte[MonsoonSystem.MonthCount][];
        for (int m = 0; m < MonsoonSystem.MonthCount; m++) grid.MonthPrecip[m] = ReadBytes(f, n);
        grid.MonthTemp = new byte[MonsoonSystem.MonthCount][];
        for (int m = 0; m < MonsoonSystem.MonthCount; m++) grid.MonthTemp[m] = ReadBytes(f, n);
        grid.CurrentDirs = new Vector3[n];
        for (int i = 0; i < n; i++)
            grid.CurrentDirs[i] = new Vector3(f.GetFloat(), f.GetFloat(), f.GetFloat());
        grid.CurrentWarmth = ReadFloats(f, n);
        grid.CurrentStrength = ReadFloats(f, n);
        grid.Psi = ReadFloats(f, n);   // v2 布局：流函数（环流圈显示；无条件写满）
        grid.Province = ReadInts(f, n);
        grid.Country = ReadInts(f, n);
        // ⚠️ 布局长度断言（单源 ArchiveLayout.BodyLength）：读后位置必须 == 布局长度
        long bodyLen = ArchiveLayout.BodyLength(n, 2);
        if (f.Position - startPos != bodyLen)
        {
            LogService.LogErr("GameMapArchive", $"布局长度断言失败：读 {f.Position - startPos}B ≠ 布局 {bodyLen}B（n={n}）——" +
                        $"写入器与布局表不同步，请检查 ArchiveLayout 字段表与 WriteBody 一致性");
            return false;
        }
        return true;
    }

    private static float[] ReadFloats(ChunkReader f, int n)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = f.GetFloat();
        return a;
    }

    private static int[] ReadInts(ChunkReader f, int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = (int)f.Get32();
        return a;
    }

    private static byte[] ReadBytes(ChunkReader f, int n)
    {
        var a = new byte[n];
        for (int i = 0; i < n; i++) a[i] = f.Get8();
        return a;
    }

    private static int LandCount(GameGrid g)
    {
        int c = 0;
        for (int i = 0; i < g.N; i++) if (g.Elev[i] > 0f) c++;
        return c;
    }

    private static int CountNonZero(int[] a)
    {
        int c = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != 0) c++;
        return c;
    }
}