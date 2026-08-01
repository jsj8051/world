using Godot;
using System;
using World.Biome;

namespace World.MapGen;

/// <summary>
/// 地图存档格式（二进制，版本化）。
///
/// v3（球面直通，2026-08-02）：
///   [4B]  magic "MPA1"
///   [2B]  version = 3
///   [4B]  seed (int)
///   [4B]  vertexCount (int)
///   [4B×3×N] 顶点单位方向 xyz（float，球面）
///   [4B]  minElev / [4B] maxElev
///   [4B×N] elev 每顶点海拔（米）
///   [4B]  minTemp / [4B] maxTemp
///   [4B×N] temp 年均温场（°C）
///   [4B]  minPrecip / [4B] maxPrecip
///   [4B×N] precip 年降水场（mm）
///   [1B×N] biome 生物群系场（BiomeType）
///   无投影、无平面中转——数据直接在球面顶点上。
///
/// v2（等距柱状平面）：width×height 场。读时向后兼容（转成球面 0 顶点 + 平面标记）。
/// v1 = 只有海拔场。
/// </summary>
public static class MapArchive
{
    public const string Magic = "MPA1";
    public const ushort Version = 3;

    /// <summary>v3 球面存档写入。</summary>
    public static bool WriteSpherical(
        string path, int seed, Vector3[] verts,
        float minElev, float maxElev, float[] elev,
        float[] temp, float[] precip, byte[] biome,
        float minTemp, float maxTemp, float minPrecip, float maxPrecip)
    {
        string dir = path.GetBaseDir();
        if (dir.Length > 0 && !DirAccess.DirExistsAbsolute(dir))
            DirAccess.MakeDirRecursiveAbsolute(dir);
        using var f = FileAccess.Open(path, FileAccess.ModeFlags.Write);
        if (f == null)
        {
            GD.PrintErr($"[MapArchive] cannot open {path} for write: {FileAccess.GetOpenError()}");
            return false;
        }
        int n = verts.Length;
        f.Store8((byte)'M');
        f.Store8((byte)'P');
        f.Store8((byte)'A');
        f.Store8((byte)'1');
        f.Store16(Version);
        f.Store32((uint)seed);
        f.Store32((uint)n);
        foreach (var v in verts) { f.StoreFloat(v.X); f.StoreFloat(v.Y); f.StoreFloat(v.Z); }
        f.StoreFloat(minElev);
        f.StoreFloat(maxElev);
        foreach (var e in elev) f.StoreFloat(e);
        f.StoreFloat(minTemp);
        f.StoreFloat(maxTemp);
        foreach (var t in temp) f.StoreFloat(t);
        f.StoreFloat(minPrecip);
        f.StoreFloat(maxPrecip);
        foreach (var p in precip) f.StoreFloat(p);
        foreach (var b in biome) f.Store8(b);
        GD.Print($"[MapArchive] wrote v{Version} {path} (spherical {n} verts, elev[{minElev:F0},{maxElev:F0}] " +
                 $"temp[{minTemp:F1},{maxTemp:F1}] precip[{minPrecip:F0},{maxPrecip:F0}])");
        return true;
    }

    /// <summary>v2 平面存档写入（兼容保留：供调试/对比导出用）。</summary>
    public static bool Write(
        string path, int seed, int width, int height,
        float minElev, float maxElev, float[] elev,
        float[] temp, float[] precip, byte[] biome,
        float minTemp, float maxTemp, float minPrecip, float maxPrecip)
    {
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
        f.Store16(2);   // v2 平面
        f.Store32((uint)seed);
        f.Store32((uint)width);
        f.Store32((uint)height);
        f.StoreFloat(minElev);
        f.StoreFloat(maxElev);
        foreach (var e in elev) f.StoreFloat(e);
        f.StoreFloat(minTemp);
        f.StoreFloat(maxTemp);
        foreach (var t in temp) f.StoreFloat(t);
        f.StoreFloat(minPrecip);
        f.StoreFloat(maxPrecip);
        foreach (var p in precip) f.StoreFloat(p);
        foreach (var b in biome) f.Store8(b);
        GD.Print($"[MapArchive] wrote v2 {path} ({width}x{height})");
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
        map.Version = ver;

        if (ver >= 3)
        {
            // ── v3 球面直通 ──
            int n = (int)f.Get32();
            map.Verts = new Vector3[n];
            for (int i = 0; i < n; i++)
                map.Verts[i] = new Vector3(f.GetFloat(), f.GetFloat(), f.GetFloat());
            map.MinElev = f.GetFloat();
            map.MaxElev = f.GetFloat();
            map.Elev = new float[n];
            for (int i = 0; i < n; i++) map.Elev[i] = f.GetFloat();
            map.MinTemp = f.GetFloat();
            map.MaxTemp = f.GetFloat();
            map.Temp = new float[n];
            for (int i = 0; i < n; i++) map.Temp[i] = f.GetFloat();
            map.MinPrecip = f.GetFloat();
            map.MaxPrecip = f.GetFloat();
            map.Precip = new float[n];
            for (int i = 0; i < n; i++) map.Precip[i] = f.GetFloat();
            map.Biome = new byte[n];
            for (int i = 0; i < n; i++) map.Biome[i] = f.Get8();
            map.Width = 0;
            map.Height = 0;
            GD.Print($"[MapArchive] read v{ver} {path} (spherical {n} verts)");
        }
        else
        {
            // ── v1/v2 平面（向后兼容：转成"平面模式"标记，Width/Height 保留）──
            map.Width = (int)f.Get32();
            map.Height = (int)f.Get32();
            map.MinElev = f.GetFloat();
            map.MaxElev = f.GetFloat();
            int n = map.Width * map.Height;
            map.Elev = new float[n];
            for (int i = 0; i < n; i++) map.Elev[i] = f.GetFloat();
            if (ver >= 2)
            {
                map.MinTemp = f.GetFloat();
                map.MaxTemp = f.GetFloat();
                map.Temp = new float[n];
                for (int i = 0; i < n; i++) map.Temp[i] = f.GetFloat();
                map.MinPrecip = f.GetFloat();
                map.MaxPrecip = f.GetFloat();
                map.Precip = new float[n];
                for (int i = 0; i < n; i++) map.Precip[i] = f.GetFloat();
                map.Biome = new byte[n];
                for (int i = 0; i < n; i++) map.Biome[i] = f.Get8();
            }
            GD.Print($"[MapArchive] read v{ver} {path} (planar {map.Width}x{map.Height})");
        }
        return true;
    }
}

/// <summary>加载后的地图数据。v3 = 球面顶点场；v1/v2 = 等距柱状平面场。</summary>
public struct MapData
{
    public int Seed;
    public ushort Version;

    // v3 球面
    public Vector3[] Verts;   // 单位方向（球面顶点，N 个）
    public float[] Elev;      // 每顶点海拔（米）
    public float[] Temp;      // 每顶点年均温 °C
    public float[] Precip;    // 每顶点年降水 mm
    public byte[] Biome;      // 每顶点 BiomeType

    // v1/v2 平面
    public int Width;
    public int Height;

    public float MinElev;
    public float MaxElev;
    public float MinTemp;
    public float MaxTemp;
    public float MinPrecip;
    public float MaxPrecip;

    public bool IsSpherical => Version >= 3;

    /// <summary>归一化海拔 0..1（球面顶点或平面场）。</summary>
    public float NormalizedElev(float raw)
    {
        float range = MaxElev - MinElev;
        return range > 1e-6f ? (raw - MinElev) / range : 0.5f;
    }

    /// <summary>球面点 → 最近顶点 id（线性扫描；v3 顶点 ~10k，采样次数多时调用方应缓存）。</summary>
    public int NearestVertex(Vector3 p)
    {
        Vector3 dir = p.Normalized();
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < Verts.Length; i++)
        {
            float d = (Verts[i] - dir).LengthSquared();
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>球面 Shepard 插值：最近顶点 + 其邻居按 cos⁴ 加权（v3 用）。</summary>
    /// <param name="field">顶点字段数组（Elev/Temp/Precip）。</param>
    public float SampleSpherical(Vector3 p, float[] field)
    {
        Vector3 dir = p.Normalized();
        int id = NearestVertex(dir);
        // 邻居 = 与最近顶点角距最小的 6 个顶点（线性扫描近邻集）
        float sumW = 0f, sumV = 0f;
        var cands = new int[8];
        var candD = new float[8];
        for (int i = 0; i < 8; i++) { cands[i] = -1; candD[i] = float.MaxValue; }
        for (int i = 0; i < Verts.Length; i++)
        {
            float d = (Verts[i] - dir).LengthSquared();
            if (i == id) continue;
            // 保留 7 个最近（排除最近顶点本身）
            for (int k = 0; k < 7; k++)
            {
                if (d < candD[k])
                {
                    for (int j = 6; j > k; j--) { cands[j] = cands[j - 1]; candD[j] = candD[j - 1]; }
                    cands[k] = i; candD[k] = d;
                    break;
                }
            }
        }
        // 最近顶点本身
        float wSelf = Mathf.Max(Verts[id].Dot(dir), 0f);
        wSelf = wSelf * wSelf * wSelf * wSelf;
        sumW += wSelf; sumV += wSelf * field[id];
        for (int k = 0; k < 7; k++)
        {
            if (cands[k] < 0) break;
            float w = Mathf.Max(Verts[cands[k]].Dot(dir), 0f);
            w = w * w * w * w;
            sumW += w; sumV += w * field[cands[k]];
        }
        return sumW > 1e-12f ? sumV / sumW : field[id];
    }

    /// <summary>球面点 → 等距柱状像素（v1/v2 平面存档用）。</summary>
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

    /// <summary>归一化海拔 0..1（球面点采样，v3 球面插值 / v2 平面最近邻）。</summary>
    public float SampleElevation(Vector3 p)
    {
        if (IsSpherical)
            return NormalizedElev(SampleSpherical(p, Elev));
        PixelFromPoint(p, out int x, out int y);
        return NormalizedElev(Elev[y * Width + x]);
    }

    /// <summary>年均温 °C。</summary>
    public float SampleTemperature(Vector3 p)
    {
        if (Temp == null) return 0f;
        if (IsSpherical) return SampleSpherical(p, Temp);
        PixelFromPoint(p, out int x, out int y);
        return Temp[y * Width + x];
    }

    /// <summary>年降水 mm。</summary>
    public float SamplePrecipitation(Vector3 p)
    {
        if (Precip == null) return 0f;
        if (IsSpherical) return SampleSpherical(p, Precip);
        PixelFromPoint(p, out int x, out int y);
        return Precip[y * Width + x];
    }

    /// <summary>生物群系。</summary>
    public BiomeType SampleBiome(Vector3 p)
    {
        if (Biome == null) return BiomeType.DeepOcean;
        if (IsSpherical)
        {
            // biome 是离散类别：取最近顶点（不插值）
            return (BiomeType)Biome[NearestVertex(p)];
        }
        PixelFromPoint(p, out int x, out int y);
        return (BiomeType)Biome[y * Width + x];
    }
}
