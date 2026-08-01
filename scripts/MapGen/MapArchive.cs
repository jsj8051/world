using Godot;
using System;
using World.Biome;

namespace World.MapGen;

/// <summary>
/// 地图存档格式（二进制，版本化）。
///
/// v2 布局（小端）：
///   [4B]  magic "MPA1"
///   [2B]  version = 2
///   [4B]  seed (int)
///   [4B]  width (int)
///   [4B]  height (int)
///   [4B]  minElev / [4B] maxElev (float)
///   [4B×W×H] elev 海拔场（row-major，y 从北纬 90° 到南纬 -90°）
///   [4B]  minTemp / [4B] maxTemp
///   [4B×W×H] temp 年均温场（°C）
///   [4B]  minPrecip / [4B] maxPrecip
///   [4B×W×H] precip 年降水场（mm）
///   [1B×W×H] biome 生物群系场（BiomeType）
///
/// v1 = 只有海拔场（version=1，读时向后兼容，气候场为 null）。
/// 后续追加场（气候/生态/资源）= version 递增，追加数据块。
/// </summary>
public static class MapArchive
{
    public const string Magic = "MPA1";
    public const ushort Version = 2;

    public static bool Write(
        string path, int seed, int width, int height,
        float minElev, float maxElev, float[] elev,
        float[] temp, float[] precip, byte[] biome,
        float minTemp, float maxTemp, float minPrecip, float maxPrecip)
    {
        // FileAccess does not create parent directories — ensure they exist.
        string dir = path.GetBaseDir();
        if (dir.Length > 0 && !DirAccess.DirExistsAbsolute(dir))
            DirAccess.MakeDirRecursiveAbsolute(dir);
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PrintErr($"[MapArchive] cannot open {path} for write: {FileAccess.GetOpenError()}");
            return false;
        }
        f.Store8((byte)'M');
        f.Store8((byte)'P');
        f.Store8((byte)'A');
        f.Store8((byte)'1');
        f.Store16(Version);
        f.Store32((uint)seed);
        f.Store32((uint)width);
        f.Store32((uint)height);
        f.StoreFloat(minElev);
        f.StoreFloat(maxElev);
        foreach (var e in elev)
            f.StoreFloat(e);
        f.StoreFloat(minTemp);
        f.StoreFloat(maxTemp);
        foreach (var t in temp)
            f.StoreFloat(t);
        f.StoreFloat(minPrecip);
        f.StoreFloat(maxPrecip);
        foreach (var p in precip)
            f.StoreFloat(p);
        foreach (var b in biome)
            f.Store8(b);
        GD.Print($"[MapArchive] wrote v{Version} {path} ({width}x{height}, elev[{minElev:F3},{maxElev:F3}] " +
                 $"temp[{minTemp:F1},{maxTemp:F1}] precip[{minPrecip:F0},{maxPrecip:F0}], {elev.Length * 4L + 24} bytes)");
        return true;
    }

    public static bool Read(string path, out MapData map)
    {
        map = default;
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        if (f == null)
        {
            GD.PrintErr($"[MapArchive] cannot open {path}: {FileAccess.GetOpenError()}");
            return false;
        }
        string magic = "" + (char)f.Get8() + (char)f.Get8() + (char)f.Get8() + (char)f.Get8();
        if (magic != Magic)
        {
            GD.PrintErr($"[MapArchive] bad magic '{magic}' in {path}");
            return false;
        }
        ushort ver = f.Get16();
        if (ver > Version)
        {
            GD.PrintErr($"[MapArchive] unsupported version {ver} (expected ≤ {Version})");
            return false;
        }
        map.Seed = (int)f.Get32();
        map.Width = (int)f.Get32();
        map.Height = (int)f.Get32();
        map.MinElev = f.GetFloat();
        map.MaxElev = f.GetFloat();
        int n = map.Width * map.Height;
        map.Elev = new float[n];
        for (int i = 0; i < n; i++)
            map.Elev[i] = f.GetFloat();

        if (ver >= 2)
        {
            map.MinTemp = f.GetFloat();
            map.MaxTemp = f.GetFloat();
            map.Temp = new float[n];
            for (int i = 0; i < n; i++)
                map.Temp[i] = f.GetFloat();
            map.MinPrecip = f.GetFloat();
            map.MaxPrecip = f.GetFloat();
            map.Precip = new float[n];
            for (int i = 0; i < n; i++)
                map.Precip[i] = f.GetFloat();
            map.Biome = new byte[n];
            for (int i = 0; i < n; i++)
                map.Biome[i] = f.Get8();
        }
        return true;
    }
}

/// <summary>加载后的地图数据（场 + 元数据）。v1 存档的气候场为 null。</summary>
public struct MapData
{
    public int Seed;
    public int Width;
    public int Height;
    public float MinElev;
    public float MaxElev;
    public float[] Elev;   // 原始海拔（生成器输出，非归一化）

    public float MinTemp;  // v2
    public float MaxTemp;
    public float[] Temp;   // 年均温 °C（v2，v1 为 null）
    public float MinPrecip;
    public float MaxPrecip;
    public float[] Precip; // 年降水 mm（v2，v1 为 null）
    public byte[] Biome;   // BiomeType（v2，v1 为 null）

    /// <summary>归一化海拔 0..1（用于纹理/着色）。</summary>
    public float Normalized(int x, int y)
    {
        float e = Elev[y * Width + x];
        float range = MaxElev - MinElev;
        return range > 1e-6f ? (e - MinElev) / range : 0.5f;
    }

    /// <summary>球面点 → 等距柱状像素（最近邻）。</summary>
    public void PixelFromPoint(Vector3 p, out int x, out int y)
    {
        Vector3 dir = p.Normalized();
        float lon = Mathf.Atan2(dir.Z, dir.X);              // -π..π
        float lat = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f)); // -π/2..π/2
        float u = lon / Mathf.Tau + 0.5f;                   // 0..1（经度）
        float v = 0.5f - lat / Mathf.Pi;                    // 0(北)..1(南)
        x = Mathf.Clamp((int)(u * Width), 0, Width - 1);
        y = Mathf.Clamp((int)(v * Height), 0, Height - 1);
    }

    /// <summary>归一化海拔 0..1（球面点采样）。</summary>
    public float SampleElevation(Vector3 p)
    {
        PixelFromPoint(p, out int x, out int y);
        return Normalized(x, y);
    }

    /// <summary>年均温 °C（v1 存档返回 0，调用方需回退到 ClimateGenerator）。</summary>
    public float SampleTemperature(Vector3 p)
    {
        if (Temp == null)
            return 0f;
        PixelFromPoint(p, out int x, out int y);
        return Temp[y * Width + x];
    }

    /// <summary>年降水 mm（v1 存档返回 0，调用方需回退）。</summary>
    public float SamplePrecipitation(Vector3 p)
    {
        if (Precip == null)
            return 0f;
        PixelFromPoint(p, out int x, out int y);
        return Precip[y * Width + x];
    }

    /// <summary>生物群系（v1 存档返回 DeepOcean，调用方需回退计算）。</summary>
    public BiomeType SampleBiome(Vector3 p)
    {
        if (Biome == null)
            return BiomeType.DeepOcean;
        PixelFromPoint(p, out int x, out int y);
        return (BiomeType)Biome[y * Width + x];
    }
}
