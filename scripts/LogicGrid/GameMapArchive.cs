using Godot;

namespace World.LogicGrid;

/// <summary>
/// 游戏地图存档格式 .gmp（自包含，独立于 .mpa 世界地图存档）。
///
/// v1（2026-08-05）：
///   [4B]  magic "GMP1"
///   [2B]  version = 1
///   [4B]  gridN（原始网格参数 n）
///   [4B]  N（格数 = 顶点数）
///   [4B]  seed
///   [4B]  radiusKm (float)
///   [1B]  prograde | [4B] rotationSpeed | [4B] axialTilt | [4B] insolation
///   [4B×6] minElev maxElev minTemp maxTemp minPrecip maxPrecip
///   [4B×3×N] verts（球面单位方向）
///   [4B×N] elev（米）| [4B×N] temp | [4B×N] precip
///   [1B×N] biome | [1B×N] riverLevel | [4B×N] riverFlow | [4B×N] riverVolume
///   [1B×N] lakeLevel | [1B×N] mineralLevel | [1B×N] soilLevel | [1B×N] monsoonLevel
///   [1B×12×N] monthPrecip | [1B×12×N] monthTemp
///   [4B×3×N] currentDirs | [4B×N] currentWarmth | [4B×N] currentStrength
///   [4B×N] province | [4B×N] country（人文层；0=未分配/无主）
///
/// 邻接不存档（确定性重建，见 GameGrid.BuildNeighbors）——省 ~1MB（n=64）且永不与顶点不一致。
/// </summary>
public static class GameMapArchive
{
    public const string Magic = "GMP1";
    public const ushort Version = 1;

    /// <summary>写 .gmp。log=false：后台线程调用（禁止 GD.Print）。</summary>
    public static bool Write(string path, GameGrid g, bool log = true)
    {
        string dir = path.GetBaseDir();
        if (dir.Length > 0 && !DirAccess.DirExistsAbsolute(dir))
            DirAccess.MakeDirRecursiveAbsolute(dir);
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PrintErr($"[GameMapArchive] cannot open {path} for write: {FileAccess.GetOpenError()}");
            return false;
        }
        int n = g.N;
        f.Store8((byte)'G'); f.Store8((byte)'M'); f.Store8((byte)'P'); f.Store8((byte)'1');
        f.Store16(Version);
        WriteBody(f, g);
        if (log)
            GD.Print($"[GameMapArchive] wrote v{Version} {path} (gridN={g.GridN} tiles={n} land={LandCount(g)} " +
                     $"elev[{g.MinElev:F0},{g.MaxElev:F0}] province={CountNonZero(g.Province)})");
        return true;
    }

    /// <summary>主体序列化（magic/version 之后；.cmp 复用此段保证与 .gmp 布局完全一致）。</summary>
    public static void WriteBody(FileAccess f, GameGrid g)
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
        for (int m = 0; m < 12; m++)
            foreach (var v in g.MonthPrecip[m]) f.Store8(v);
        for (int m = 0; m < 12; m++)
            foreach (var v in g.MonthTemp[m]) f.Store8(v);
        foreach (var v in g.CurrentDirs) { f.StoreFloat(v.X); f.StoreFloat(v.Y); f.StoreFloat(v.Z); }
        foreach (var v in g.CurrentWarmth) f.StoreFloat(v);
        foreach (var v in g.CurrentStrength) f.StoreFloat(v);
        foreach (var v in g.Province) f.Store32((uint)v);
        foreach (var v in g.Country) f.Store32((uint)v);
    }

    /// <summary>读 .gmp → GameGrid（自然层 + 人文层完整恢复，不依赖 .mpa）。</summary>
    public static bool Read(string path, out GameGrid g)
    {
        g = null;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PrintErr($"[GameMapArchive] cannot open {path} for read: {FileAccess.GetOpenError()}");
            return false;
        }
        if (f.Get8() != 'G' || f.Get8() != 'M' || f.Get8() != 'P' || f.Get8() != '1')
        {
            GD.PrintErr($"[GameMapArchive] bad magic in {path}");
            return false;
        }
        ushort ver = f.Get16();
        if (ver != Version)
        {
            GD.PrintErr($"[GameMapArchive] unsupported version {ver} in {path} (need {Version})");
            return false;
        }
        var grid = new GameGrid();
        ReadBody(f, grid);
        g = grid;
        return true;
    }

    /// <summary>主体反序列化（magic/version 之后；与 WriteBody 严格对应）。</summary>
    public static void ReadBody(FileAccess f, GameGrid grid)
    {
        grid.GridN = (int)f.Get32();
        grid.N = (int)f.Get32();
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
        grid.MonthPrecip = new byte[12][];
        for (int m = 0; m < 12; m++) grid.MonthPrecip[m] = ReadBytes(f, n);
        grid.MonthTemp = new byte[12][];
        for (int m = 0; m < 12; m++) grid.MonthTemp[m] = ReadBytes(f, n);
        grid.CurrentDirs = new Vector3[n];
        for (int i = 0; i < n; i++)
            grid.CurrentDirs[i] = new Vector3(f.GetFloat(), f.GetFloat(), f.GetFloat());
        grid.CurrentWarmth = ReadFloats(f, n);
        grid.CurrentStrength = ReadFloats(f, n);
        grid.Province = ReadInts(f, n);
        grid.Country = ReadInts(f, n);
    }

    private static float[] ReadFloats(FileAccess f, int n)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = f.GetFloat();
        return a;
    }

    private static int[] ReadInts(FileAccess f, int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = (int)f.Get32();
        return a;
    }

    private static byte[] ReadBytes(FileAccess f, int n)
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
