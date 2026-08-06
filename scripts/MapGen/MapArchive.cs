using Godot;
using System;
using System.Collections.Generic;
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
    public const ushort Version = 4;   // v4：洋流加流函数 psi（环流圈"每环最外圈"显示）

    /// <summary>v3 球面存档写入。log：false = 后台线程调用（禁止 GD.Print）。</summary>
    public static bool WriteSpherical(
        string path, int seed, Vector3[] verts,
        float minElev, float maxElev, float[] elev,
        float[] temp, float[] precip, byte[] biome,
        float minTemp, float maxTemp, float minPrecip, float maxPrecip,
        bool prograde = true, float rotationSpeed = 1f, float axialTilt = 23.4f,
        Vector3[] currentDirs = null, float[] currentWarmth = null, float[] currentStrength = null,
        float[] psi = null,
        byte[] riverLevel = null, int[] riverFlow = null, float[] riverVolume = null, byte[] lakeLevel = null,
        byte[] mineralLevel = null, byte[] soilLevel = null,
        byte[] monsoonLevel = null, byte[][] monthPrecip = null, byte[][] monthTemp = null,
        bool log = true)
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
        f.Store8((byte)(prograde ? 1 : 0));   // 尾部扩展1：自转方向（盛行风图层用；旧存档无此字节=默认顺转）
        f.StoreFloat(rotationSpeed);          // 尾部扩展2：自转速度（科里奥利强度；旧存档无=1.0 地球）
        f.StoreFloat(axialTilt);              // 尾部扩展2b（v3.8）：轴向倾角（季风月风场现场重算用；旧存档无=23.4）
        // 尾部扩展3（2026-08-02）：洋流方向+冷暖+强度（流函数法；MapViewer 洋流图层用）
        //   每顶点：方向 3 float（零=内陆无洋流）+ 冷暖 1 float + 强度 1 float。
        //   旧存档无此段=默认无洋流。
        if (currentDirs != null && currentWarmth != null)
        {
            foreach (var cd in currentDirs) { f.StoreFloat(cd.X); f.StoreFloat(cd.Y); f.StoreFloat(cd.Z); }
            foreach (var cw in currentWarmth) f.StoreFloat(cw);
            if (currentStrength != null)
                foreach (var cs in currentStrength) f.StoreFloat(cs);
            // 尾部扩展7（2026-08-06，v4）：流函数 psi（每格 1 float；环流圈"每环最外圈"显示用）
            if (psi != null)
                foreach (var p in psi) f.StoreFloat(p);
        }
        // 尾部扩展4（2026-08-02）：河流（级别 n bytes + 流向 n×4 bytes [+ 流量 n×4 bytes]；旧存档无=null）
        // 尾部扩展5（2026-08-02）：湖泊（级别 n bytes；旧存档无=null）
        if (riverLevel != null && riverFlow != null)
        {
            foreach (var rl in riverLevel) f.Store8(rl);
            foreach (var rf in riverFlow) f.Store32((uint)rf);
            if (riverVolume != null)
                foreach (var rv in riverVolume) f.StoreFloat(rv);
            if (lakeLevel != null)
                foreach (var ll in lakeLevel) f.Store8(ll);
            // 尾部扩展6（2026-08-02）：矿藏（级别 n bytes；(富度<<4)|矿种；旧存档无=null）
            if (mineralLevel != null)
                foreach (var ml in mineralLevel) f.Store8(ml);
            // 尾部扩展7（2026-08-03）：土壤肥力（n bytes 1-5；旧存档无=null）
            if (soilLevel != null)
                foreach (var sl in soilLevel) f.Store8(sl);
        }
        // 尾部扩展8（2026-08-16）：季风强度（n bytes 0-255 → 0-1；MonsoonSystem；旧存档无=null）
        if (monsoonLevel != null)
            foreach (var ml in monsoonLevel) f.Store8(ml);
        // 尾部扩展9（2026-08-16）：月降水比例（12×n bytes；MonsoonSystem；旧存档无=null）
        //   每顶点 12 个月比例 0-255（×年降水 = 月降水 mm）；海洋格 0
        if (monthPrecip != null)
            for (int m = 0; m < 12; m++)
                foreach (var v in monthPrecip[m]) f.Store8(v);
        // 尾部扩展10（2026-08-16）：月温度（12×n bytes，−60~60°C → 0-255；温度系统月度化）
        if (monthTemp != null)
            for (int m = 0; m < 12; m++)
                foreach (var v in monthTemp[m]) f.Store8(v);
        if (log)
            GD.Print($"[MapArchive] wrote v{Version} {path} (spherical {n} verts, elev[{minElev:F0},{maxElev:F0}] " +
                     $"temp[{minTemp:F1},{maxTemp:F1}] precip[{minPrecip:F0},{maxPrecip:F0}] prograde={prograde})");
        return true;
    }

    public static bool Read(string path, out MapData map)
    {
        map = new MapData();
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
            // 尾部扩展：自转方向（旧存档没有此字节 → 默认顺转）+ 自转速度（旧存档没有 → 1.0）
            map.ProgradeRotation = f.GetPosition() < f.GetLength() ? f.Get8() != 0 : true;
            map.RotationSpeed = f.GetPosition() + 4 <= f.GetLength() ? f.GetFloat() : 1f;
            // 尾部扩展2b（v3.8）：轴向倾角（旧存档没有 → 23.4 地球式；季风月风场现场重算用）
            map.AxialTilt = f.GetPosition() + 4 <= f.GetLength() ? f.GetFloat() : 23.4f;
            // 尾部扩展3（v3.1）：洋流段（方向 3n floats + 冷暖 n floats [+ 强度 n floats]；旧存档无 = null）
            ulong currentBytes16 = (ulong)n * 16u;   // 3 方向 + 1 冷暖 = 16 字节/顶点
            ulong currentBytes20 = (ulong)n * 20u;   // + 1 强度 = 20 字节/顶点
            if (f.GetPosition() + currentBytes16 <= f.GetLength())
            {
                map.CurrentDirs = new Vector3[n];
                for (int i = 0; i < n; i++)
                    map.CurrentDirs[i] = new Vector3(f.GetFloat(), f.GetFloat(), f.GetFloat());
                map.CurrentWarmth = new float[n];
                for (int i = 0; i < n; i++) map.CurrentWarmth[i] = f.GetFloat();
                // 强度段（v3.1 加；⚠️ 2026-08-02 修复：判断必须用"剩余 ≥ 4n"——
                //   原用 currentBytes20（整段 20n）从当前位置加必然超长 → strength 永不读
                //   → 河流段错位读取全乱。旧存档只有 16n 字节 → 无强度，默认 1）
                if (f.GetPosition() + (ulong)n * 4u <= f.GetLength())
                {
                    map.CurrentStrength = new float[n];
                    for (int i = 0; i < n; i++) map.CurrentStrength[i] = f.GetFloat();
                }
                // 尾部扩展7（v4）：流函数 psi（v3 旧档无——必须用版本判断，长度检测会误读河流段）
                if (ver >= 4 && f.GetPosition() + (ulong)n * 4u <= f.GetLength())
                {
                    map.Psi = new float[n];
                    for (int i = 0; i < n; i++) map.Psi[i] = f.GetFloat();
                }
            }
            // 尾部扩展4：河流（级别 n bytes + 流向 n×4 bytes [+ 流量 n×4 bytes]；旧存档无 = null）
            ulong riverBytes = (ulong)n * 5u;
            if (f.GetPosition() + riverBytes <= f.GetLength())
            {
                map.RiverLevel = new byte[n];
                for (int i = 0; i < n; i++) map.RiverLevel[i] = f.Get8();
                map.RiverFlow = new int[n];
                for (int i = 0; i < n; i++) map.RiverFlow[i] = (int)f.Get32();
                // 流量段（v3.3 加；⚠️ 判断用"剩余 ≥ 4n"——同 strength 修复，防错位）
                if (f.GetPosition() + (ulong)n * 4u <= f.GetLength())
                {
                    map.RiverVolume = new float[n];
                    for (int i = 0; i < n; i++) map.RiverVolume[i] = f.GetFloat();
                }
                // 湖泊段（v3.4 加；判断用"剩余 ≥ n"）
                if (f.GetPosition() + (ulong)n <= f.GetLength())
                {
                    map.LakeLevel = new byte[n];
                    for (int i = 0; i < n; i++) map.LakeLevel[i] = f.Get8();
                }
                // 矿藏段（v3.5 加；判断用"剩余 ≥ n"）
                if (f.GetPosition() + (ulong)n <= f.GetLength())
                {
                    map.MineralLevel = new byte[n];
                    for (int i = 0; i < n; i++) map.MineralLevel[i] = f.Get8();
                }
                // 土壤段（v3.6 加；判断用"剩余 ≥ n"）
                if (f.GetPosition() + (ulong)n <= f.GetLength())
                {
                    map.SoilLevel = new byte[n];
                    for (int i = 0; i < n; i++) map.SoilLevel[i] = f.Get8();
                }
            }
            // 季风段（v3.7 加；n bytes；判断用"剩余 ≥ n"）
            if (f.GetPosition() + (ulong)n <= f.GetLength())
            {
                map.MonsoonLevel = new byte[n];
                for (int i = 0; i < n; i++) map.MonsoonLevel[i] = f.Get8();
            }
            // 月降水段（v3.8 加；12×n bytes）
            if (f.GetPosition() + (ulong)(12 * n) <= f.GetLength())
            {
                map.MonthPrecip = new byte[12][];
                for (int m = 0; m < 12; m++)
                {
                    map.MonthPrecip[m] = new byte[n];
                    for (int i = 0; i < n; i++) map.MonthPrecip[m][i] = f.Get8();
                }
            }
            // 月温度段（v3.8 加；12×n bytes，−60~60°C → 0-255）
            if (f.GetPosition() + (ulong)(12 * n) <= f.GetLength())
            {
                map.MonthTemp = new byte[12][];
                for (int m = 0; m < 12; m++)
                {
                    map.MonthTemp[m] = new byte[n];
                    for (int i = 0; i < n; i++) map.MonthTemp[m][i] = f.Get8();
                }
            }
            // ⚠️ 桶索引必须在主线程立即构建（惰性构建 + Parallel 采样 = 并发修改集合崩溃）
            map.EnsureBuckets();
            GD.Print($"[MapArchive] read v{ver} {path} (spherical {n} verts, prograde={map.ProgradeRotation} speed={map.RotationSpeed} currents={(map.CurrentDirs != null ? "yes" : "no")} rivers={(map.RiverLevel != null ? "yes" : "no")} monsoon={(map.MonsoonLevel != null ? "yes" : "no")})");
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

/// <summary>加载后的地图数据。v3 = 球面顶点场；v1/v2 = 等距柱状平面场。
/// ⚠️ 2026-08-02：必须是 class（非 struct）——球面桶索引 _buckets 是惰性构建的
///   可变缓存，struct 值传递会让每次采样都重建桶（65 万格 × 4 次采样 × 512 桶
///   = 6.6 亿次 List.Add → 进入游戏/切图层极慢）。</summary>
public class MapData
{
    public int Seed;
    public ushort Version;
    public bool ProgradeRotation = true;   // 自转方向（v3 尾部字节；旧存档默认顺转）
    public float RotationSpeed = 1f;       // 自转速度（v3 尾部 float；旧存档默认 1.0 地球）
    public float AxialTilt = 23.4f;        // 轴向倾角（v3.8 尾部 float；旧存档默认 23.4；季风月风场现场重算用）
    public Vector3[] CurrentDirs;          // 洋流方向（v3.1 尾部；null=旧存档无）
    public float[] CurrentWarmth;          // 洋流冷暖（v3.1 尾部；null=旧存档无）
    public float[] CurrentStrength;        // 洋流强度 0.3~1.0（v3.1 尾部；null=旧存档无，默认 1）
    public float[] Psi;                    // 洋流流函数（v4；环流圈"每环最外圈"显示；null=旧存档无）
    public byte[] RiverLevel;              // 河流级别（v3.2 尾部；null=旧存档无）
    public int[] RiverFlow;                // 河流流向（v3.2 尾部；null=旧存档无）
    public float[] RiverVolume;            // 河流流量 mm（v3.3 尾部；null=旧存档无）
    public byte[] LakeLevel;               // 湖泊级别（v3.4 尾部；null=旧存档无）
    public byte[] MineralLevel;            // 矿藏（v3.5 尾部；(富度<<4)|矿种；null=旧存档无）
    public byte[] SoilLevel;               // 土壤肥力 1-5（v3.6 尾部；null=旧存档无）
    public byte[] MonsoonLevel;            // 季风强度 0-255→0-1（v3.7 尾部；MonsoonSystem；null=旧存档无）
    public byte[][] MonthPrecip;           // [12][n] 月降水比例 0-255（v3.8 尾部；×年降水=月降水 mm；null=旧存档无）
    public byte[][] MonthTemp;             // [12][n] 月温度 −60~60°C→0-255（v3.8 尾部；null=旧存档无）

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

    // ── 球面桶索引（采样加速，加载后惰性构建）──
    // ⚠️ 2026-08-02：线性扫描最近邻在 65 万 hex 格 × 10242 顶点 = 1300 亿次距离计算
    //   （进入游戏/切图层极慢）。桶索引把最近邻降到 O(~180)。
    // ⚠️ 2026-08-16 修复：桶数固定 16×32 不随 n 缩放 → n=128（163842 顶点）每桶 ~320
    //   顶点，3×3 邻桶 ~2880 次/查询 × 3 处调用 ≈ 14 亿次距离计算 → 地图打不开（卡 80%/100%）。
    //   改为按顶点数缩放（目标每桶 ~30 顶点，lat:lon ≈ 1:2）→ 任意 n 查询成本恒 O(~270)。
    private int BucketsLat;
    private int BucketsLon;
    private List<int>[,] _buckets;
    private int[][] _neighbors;   // BuildNeighbors 缓存（懒构建一次；主线程调用）

    /// <summary>归一化海拔 0..1（球面顶点或平面场）。</summary>
    public float NormalizedElev(float raw)
    {
        float range = MaxElev - MinElev;
        return range > 1e-6f ? (raw - MinElev) / range : 0.5f;
    }

    /// <summary>构建球面桶索引。⚠️ 必须在主线程调用一次（Read 后），
    /// 惰性构建 + 并行采样会并发修改集合（Collection was modified 崩溃）。</summary>
    public void EnsureBuckets()
    {
        if (_buckets != null) return;
        // 目标每桶 ~30 顶点 → 总桶数 ≈ V/30；保持 lat:lon = 1:2（球面面积均匀分）。
        // 极区单桶逻辑（BucketOf）在 lat=0/末桶时 bx=0，缩放后仍成立。
        int targetPerBucket = 30;
        int totalBuckets = Math.Max(2, Verts.Length / targetPerBucket);
        BucketsLat = Mathf.Clamp((int)Mathf.Round(Mathf.Sqrt(totalBuckets / 2f)), 4, 512);
        BucketsLon = BucketsLat * 2;
        _buckets = new List<int>[BucketsLat, BucketsLon];
        for (int y = 0; y < BucketsLat; y++)
            for (int x = 0; x < BucketsLon; x++)
                _buckets[y, x] = new List<int>();
        for (int i = 0; i < Verts.Length; i++)
        {
            (int by, int bx) = BucketOf(Verts[i]);
            _buckets[by, bx].Add(i);
        }
    }

    private (int, int) BucketOf(Vector3 v)
    {
        float lat = Mathf.Asin(Mathf.Clamp(v.Y, -1f, 1f));
        float lon = Mathf.Atan2(v.Z, v.X);
        int by = (int)Mathf.Clamp((lat / Mathf.Pi + 0.5f) * BucketsLat, 0, BucketsLat - 1);
        // ⚠️ 极区（最北/最南纬桶）：经度在极点汇聚，按经度分桶会让 3×3 邻桶
        //   查不到真正最近顶点（经度弧长 = 11.25°×cos(85°) ≈ 0.98° < 顶点间距 1.6°）
        //   → 采样错乱 → 3D 球体两极出现辐射条纹（2026-08-02 修复）。
        //   极区单桶：所有经度放同一桶，3×3 邻桶自然覆盖全部极区顶点。
        int bx;
        if (by == 0 || by == BucketsLat - 1)
            bx = 0;
        else
            bx = (int)(((lon / Mathf.Pi + 1f) * 0.5f * BucketsLon) % BucketsLon);
        return (by, bx);
    }

    /// <summary>球面点 → 最近顶点 id（桶查询，3×3 邻桶）。</summary>
    public int NearestVertex(Vector3 p)
    {
        Vector3 dir = p.Normalized();
        EnsureBuckets();
        (int by, int bx) = BucketOf(dir);
        int best = -1;
        float bestD = float.MaxValue;
        for (int dy = -1; dy <= 1; dy++)
        {
            int y = (by + dy + BucketsLat) % BucketsLat;
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = (bx + dx + BucketsLon) % BucketsLon;
                foreach (int id in _buckets[y, x])
                {
                    float d = (Verts[id] - dir).LengthSquared();
                    if (d < bestD) { bestD = d; best = id; }
                }
            }
        }
        return best;
    }

    /// <summary>读档后现场重建邻接表（Icosahedron 拓扑：桶内球面距离 < 1.5×平均格距）。
    /// 存档不存拓扑，流域合并等需要邻接的操作用此方法（纯计算，毫秒级）。
    /// ⚠️ 2026-08-16：结果缓存（懒构建一次）——MapViewer 的 EnsureMonthWind 和
    ///   BuildCurrentRingsFromPsi 各调一次，n=128 每次 O(V²) 是主线程卡 100% 的元凶之一。
    ///   双检锁：EnsureMonthWind 后台线程与主线程可能并发首次构建。</summary>
    private readonly object _neighborsLock = new object();
    public int[][] BuildNeighbors()
    {
        if (_neighbors != null) return _neighbors;
        lock (_neighborsLock)
        {
            if (_neighbors != null) return _neighbors;
            return BuildNeighborsUnlocked();
        }
    }

    private int[][] BuildNeighborsUnlocked()
    {
        EnsureBuckets();
        int n = Verts.Length;
        float cell = Mathf.Sqrt(4f * Mathf.Pi / n);        // 平均格距（rad）
        float cosR = Mathf.Cos(cell * 1.5f);               // 邻居半径 1.5×格距
        var result = new int[n][];
        for (int i = 0; i < n; i++)
        {
            var list = new System.Collections.Generic.List<int>();
            (int by, int bx) = BucketOf(Verts[i]);
            for (int dy = -1; dy <= 1; dy++)
            {
                int y = (by + dy + BucketsLat) % BucketsLat;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = (bx + dx + BucketsLon) % BucketsLon;
                    foreach (int j in _buckets[y, x])
                    {
                        if (j == i) continue;
                        if (Verts[i].Dot(Verts[j]) > cosR)
                            list.Add(j);
                    }
                }
            }
            result[i] = list.ToArray();
        }
        _neighbors = result;
        return result;
    }

    /// <summary>球面 Shepard 插值：最近顶点 + 其邻居按 cos⁴ 加权（v3 用）。</summary>
    /// <param name="field">顶点字段数组（Elev/Temp/Precip）。</param>
    public float SampleSpherical(Vector3 p, float[] field)
    {
        Vector3 dir = p.Normalized();
        int id = NearestVertex(dir);
        // 邻居 = 同一桶+邻桶内的近邻（桶查询，O(~180) 而非 O(N)）
        EnsureBuckets();
        float sumW = 0f, sumV = 0f;
        var cands = new int[8];
        var candD = new float[8];
        for (int i = 0; i < 8; i++) { cands[i] = -1; candD[i] = float.MaxValue; }
        (int by, int bx) = BucketOf(dir);
        for (int dy = -1; dy <= 1; dy++)
        {
            int y = (by + dy + BucketsLat) % BucketsLat;
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = (bx + dx + BucketsLon) % BucketsLon;
                foreach (int vi in _buckets[y, x])
                {
                    if (vi == id) continue;
                    float d = (Verts[vi] - dir).LengthSquared();
                    // 保留 7 个最近（排除最近顶点本身）
                    for (int k = 0; k < 7; k++)
                    {
                        if (d < candD[k])
                        {
                            for (int j = 6; j > k; j--) { cands[j] = cands[j - 1]; candD[j] = candD[j - 1]; }
                            cands[k] = vi; candD[k] = d;
                            break;
                        }
                    }
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
