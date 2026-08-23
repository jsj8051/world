using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using World.Biome;
using World.Services;
using World.Utils;
using IOFileAccess = System.IO.FileAccess;

namespace World.MapGen;

/// <summary>
/// 地图存档格式 .mpa（v6 段表格式，2026-08-23 段表化）。
///
/// 布局（docs/存档段表格式设计.md §2/§3.1）：
///   [4B]  magic "MPA1"
///   [2B]  skeletonVer = 6
///   [2B]  reserved
///   [..]  数据区（段：HEAD/VERT/ELEV/TEMP/PREC/BIOM/OCEN/RIVL/RIVF/RIVV/LAKE/MINE/SOIL/MONO/MPRC/MTMP）
///   [12B×K] 段表 + [12B] 尾目录（ZIP 式；ChunkWriter/ChunkReader 容器）
///
/// 原则：一系统一段，段边界 = 系统边界；段缺失 = 现场重算兜底（不信任存档）。
/// v1-v5 旧格式读取分支于 2026-08-23 删除（用户拍板：旧档全删，只支持段表格式）。
/// IO 层从 FileAccess 迁移到 System.IO（存档往返可进单元测试）。
/// </summary>
public static class MapArchive
{
    public const string Magic = "MPA1";
    public const ushort Version = 6;   // v6：段表容器骨架（2026-08-23 存档段表化）

    /// <summary>星球半径标准默认值（km，地球平均半径）。标度统一源（2026-08-10）：
    /// 所有"默认半径/旧档回退"引用此常量，禁止散落 6330/6367/6371 魔数。
    /// GameGrid.DefaultRadiusKm = 此值（LogicGrid 便捷别名）。</summary>
    public const float DefaultRadiusKm = 6371f;

    /// <summary>user:// 路径 → 绝对路径（System.IO 需要）。非 user:// 原样返回。</summary>
    private static string ResolvePath(string path) =>
        path.StartsWith("user://", StringComparison.Ordinal)
            ? ProjectSettings.GlobalizePath(path)
            : path;

    /// <summary>v6 段表球面存档写入。log：false = 后台线程调用（禁止 GD.Print）。
    /// 段存在即写入；null 段不写（读取端缺段 = null = 现场重算）。</summary>
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
        float radiusKm = DefaultRadiusKm,   // v5 头：星球半径（km；旧存档无=地球默认）
        bool log = true)
    {
        int n = verts.Length;
        try
        {
            string abs = ResolvePath(path);
            string dir = Path.GetDirectoryName(abs) ?? "";
            if (dir.Length > 0 && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            using var fs = new FileStream(abs, FileMode.Create, IOFileAccess.Write);
            using var w = new ChunkWriter(fs, Magic, Version);

            // ── HEAD：固定字段汇总 ──
            w.BeginSegment("HEAD", 1);
            w.Store32((uint)seed);
            w.StoreFloat(radiusKm);
            w.Store32((uint)n);
            w.StoreFloat(minElev); w.StoreFloat(maxElev);
            w.StoreFloat(minTemp); w.StoreFloat(maxTemp);
            w.StoreFloat(minPrecip); w.StoreFloat(maxPrecip);
            w.Store8((byte)(prograde ? 1 : 0));       // 自转方向（盛行风图层用）
            w.StoreFloat(rotationSpeed);              // 自转速度（科里奥利强度）
            w.StoreFloat(axialTilt);                  // 轴向倾角（季风月风场现场重算用）
            w.EndSegment();

            // ── VERT：顶点单位方向 ──
            w.BeginSegment("VERT", 1);
            foreach (var v in verts) { w.StoreFloat(v.X); w.StoreFloat(v.Y); w.StoreFloat(v.Z); }
            w.EndSegment();

            // ── ELEV / TEMP / PREC / BIOM ──
            w.BeginSegment("ELEV", 1);
            foreach (var e in elev) w.StoreFloat(e);
            w.EndSegment();
            w.BeginSegment("TEMP", 1);
            foreach (var t in temp) w.StoreFloat(t);
            w.EndSegment();
            w.BeginSegment("PREC", 1);
            foreach (var p in precip) w.StoreFloat(p);
            w.EndSegment();
            w.BeginSegment("BIOM", 1);
            foreach (var b in biome) w.Store8(b);
            w.EndSegment();

            // ── OCEN：洋流（段存在即完整；psi 无条件写满——沿用 v4 教训，读端无条件读）──
            if (currentDirs != null && currentWarmth != null)
            {
                w.BeginSegment("OCEN", 1);
                foreach (var cd in currentDirs) { w.StoreFloat(cd.X); w.StoreFloat(cd.Y); w.StoreFloat(cd.Z); }
                foreach (var cw in currentWarmth) w.StoreFloat(cw);
                if (currentStrength != null)
                    foreach (var cs in currentStrength) w.StoreFloat(cs);
                else
                    for (int i = 0; i < n; i++) w.StoreFloat(1f);   // 旧语义：无强度 = 默认 1
                if (psi != null)
                    foreach (var p in psi) w.StoreFloat(p);
                else
                    for (int i = 0; i < n; i++) w.StoreFloat(0f);
                w.EndSegment();
            }

            // ── 河流/湖泊/矿藏/土壤：独立段（缺 = 现场重算）──
            if (riverLevel != null) { w.BeginSegment("RIVL", 1); foreach (var rl in riverLevel) w.Store8(rl); w.EndSegment(); }
            if (riverFlow != null) { w.BeginSegment("RIVF", 1); foreach (var rf in riverFlow) w.Store32((uint)rf); w.EndSegment(); }
            if (riverVolume != null) { w.BeginSegment("RIVV", 1); foreach (var rv in riverVolume) w.StoreFloat(rv); w.EndSegment(); }
            if (lakeLevel != null) { w.BeginSegment("LAKE", 1); foreach (var ll in lakeLevel) w.Store8(ll); w.EndSegment(); }
            if (mineralLevel != null) { w.BeginSegment("MINE", 1); foreach (var ml in mineralLevel) w.Store8(ml); w.EndSegment(); }
            if (soilLevel != null) { w.BeginSegment("SOIL", 1); foreach (var sl in soilLevel) w.Store8(sl); w.EndSegment(); }

            // ── 季风 / 月降水 / 月温度 ──
            if (monsoonLevel != null) { w.BeginSegment("MONO", 1); foreach (var ml in monsoonLevel) w.Store8(ml); w.EndSegment(); }
            if (monthPrecip != null)
            {
                w.BeginSegment("MPRC", 1);
                for (int m = 0; m < MonsoonSystem.MonthCount; m++)
                    foreach (var v in monthPrecip[m]) w.Store8(v);
                w.EndSegment();
            }
            if (monthTemp != null)
            {
                w.BeginSegment("MTMP", 1);
                for (int m = 0; m < MonsoonSystem.MonthCount; m++)
                    foreach (var v in monthTemp[m]) w.Store8(v);
                w.EndSegment();
            }

            w.Finish();
        }
        catch (Exception ex)
        {
            LogService.LogErr("MapArchive", $"写入失败 {path}: {ex.Message}");
            return false;
        }
        if (log)
            LogService.Log("MapArchive", $"wrote v{Version} {path} (spherical {n} verts, elev[{minElev:F0},{maxElev:F0}] " +
                     $"temp[{minTemp:F1},{maxTemp:F1}] precip[{minPrecip:F0},{maxPrecip:F0}] prograde={prograde})");
        return true;
    }

    /// <summary>v6 段表读档。段缺失 → 对应字段 null（现场重算兜底）；VERT/HEAD 缺失 → 拒绝（必需段）。
    /// ⚠️ 桶索引必须在主线程立即构建（惰性构建 + Parallel 采样 = 并发修改集合崩溃）。</summary>
    public static bool Read(string path, out MapData map)
    {
        map = new MapData();
        try
        {
            string abs = ResolvePath(path);
            using var fs = new FileStream(abs, FileMode.Open, IOFileAccess.Read);
            using var r = new ChunkReader(fs);
            if (r.Magic != Magic)
            {
                LogService.LogErr("MapArchive", $"bad magic '{r.Magic}' in {path}");
                return false;
            }
            if (r.SkeletonVer != Version)
            {
                LogService.LogErr("MapArchive", $"不支持的存档版本 {r.SkeletonVer}（当前 {Version}；旧版 v1-v5 已于 2026-08-23 段表化移除，请重新生成）");
                return false;
            }
            map.Version = Version;

            // ── HEAD：固定字段 ──
            if (!r.SeekSegment("HEAD"))
            {
                LogService.LogErr("MapArchive", $"{path}: 缺 HEAD 段（必需段）");
                return false;
            }
            map.Seed = (int)r.Get32();
            map.RadiusKm = r.GetFloat();
            int n = (int)r.Get32();
            map.MinElev = r.GetFloat();
            map.MaxElev = r.GetFloat();
            map.MinTemp = r.GetFloat();
            map.MaxTemp = r.GetFloat();
            map.MinPrecip = r.GetFloat();
            map.MaxPrecip = r.GetFloat();
            map.ProgradeRotation = r.Get8() != 0;
            map.RotationSpeed = r.GetFloat();
            map.AxialTilt = r.GetFloat();
            map.Width = 0;
            map.Height = 0;

            // ── VERT / ELEV / TEMP / PREC / BIOM ──
            if (!r.SeekSegment("VERT"))
            {
                LogService.LogErr("MapArchive", $"{path}: 缺 VERT 段（必需段）");
                return false;
            }
            map.Verts = new Vector3[n];
            for (int i = 0; i < n; i++)
                map.Verts[i] = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());

            if (r.SeekSegment("ELEV"))
            {
                map.Elev = new float[n];
                for (int i = 0; i < n; i++) map.Elev[i] = r.GetFloat();
            }
            if (r.SeekSegment("TEMP"))
            {
                map.Temp = new float[n];
                for (int i = 0; i < n; i++) map.Temp[i] = r.GetFloat();
            }
            if (r.SeekSegment("PREC"))
            {
                map.Precip = new float[n];
                for (int i = 0; i < n; i++) map.Precip[i] = r.GetFloat();
            }
            if (r.SeekSegment("BIOM"))
            {
                map.Biome = new byte[n];
                for (int i = 0; i < n; i++) map.Biome[i] = r.Get8();
            }

            // ── OCEN：洋流（段存在即完整）──
            if (r.SeekSegment("OCEN"))
            {
                map.CurrentDirs = new Vector3[n];
                for (int i = 0; i < n; i++)
                    map.CurrentDirs[i] = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                map.CurrentWarmth = new float[n];
                for (int i = 0; i < n; i++) map.CurrentWarmth[i] = r.GetFloat();
                map.CurrentStrength = new float[n];
                for (int i = 0; i < n; i++) map.CurrentStrength[i] = r.GetFloat();
                map.Psi = new float[n];
                for (int i = 0; i < n; i++) map.Psi[i] = r.GetFloat();
            }

            // ── 河流/湖泊/矿藏/土壤：独立段 ──
            if (r.SeekSegment("RIVL"))
            {
                map.RiverLevel = new byte[n];
                for (int i = 0; i < n; i++) map.RiverLevel[i] = r.Get8();
            }
            if (r.SeekSegment("RIVF"))
            {
                map.RiverFlow = new int[n];
                for (int i = 0; i < n; i++) map.RiverFlow[i] = (int)r.Get32();
            }
            if (r.SeekSegment("RIVV"))
            {
                map.RiverVolume = new float[n];
                for (int i = 0; i < n; i++) map.RiverVolume[i] = r.GetFloat();
            }
            if (r.SeekSegment("LAKE"))
            {
                map.LakeLevel = new byte[n];
                for (int i = 0; i < n; i++) map.LakeLevel[i] = r.Get8();
            }
            if (r.SeekSegment("MINE"))
            {
                map.MineralLevel = new byte[n];
                for (int i = 0; i < n; i++) map.MineralLevel[i] = r.Get8();
            }
            if (r.SeekSegment("SOIL"))
            {
                map.SoilLevel = new byte[n];
                for (int i = 0; i < n; i++) map.SoilLevel[i] = r.Get8();
            }

            // ── 季风 / 月降水 / 月温度 ──
            if (r.SeekSegment("MONO"))
            {
                map.MonsoonLevel = new byte[n];
                for (int i = 0; i < n; i++) map.MonsoonLevel[i] = r.Get8();
            }
            if (r.SeekSegment("MPRC"))
            {
                map.MonthPrecip = new byte[MonsoonSystem.MonthCount][];
                for (int m = 0; m < MonsoonSystem.MonthCount; m++)
                {
                    map.MonthPrecip[m] = new byte[n];
                    for (int i = 0; i < n; i++) map.MonthPrecip[m][i] = r.Get8();
                }
            }
            if (r.SeekSegment("MTMP"))
            {
                map.MonthTemp = new byte[MonsoonSystem.MonthCount][];
                for (int m = 0; m < MonsoonSystem.MonthCount; m++)
                {
                    map.MonthTemp[m] = new byte[n];
                    for (int i = 0; i < n; i++) map.MonthTemp[m][i] = r.Get8();
                }
            }

            // ⚠️ 桶索引必须在主线程立即构建（惰性构建 + Parallel 采样 = 并发修改集合崩溃）
            map.EnsureBuckets();
            LogService.Log("MapArchive", $"read v{Version} {path} (spherical {n} verts, prograde={map.ProgradeRotation} speed={map.RotationSpeed} currents={(map.CurrentDirs != null ? "yes" : "no")} rivers={(map.RiverLevel != null ? "yes" : "no")} monsoon={(map.MonsoonLevel != null ? "yes" : "no")})");
            return true;
        }
        catch (Exception ex)
        {
            LogService.LogErr("MapArchive", $"读取失败 {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>轻量头部摘要读取（MapSelectMenu 存档列表用）：段表版只读 HEAD 段（随机访问），
    /// 不分配任何场数组、不建桶索引、不打日志——毫秒级。
    /// ⚠️ 2026-08-23：原列表 Describe 全量 Read 每个档（42 档共 532MB 主线程同步反序列化 + 42 次 EnsureBuckets）
    ///   → 进存档界面卡 10s+；同日 Peek 改用 Seek 跳顶点数组后 → 段表格式 HEAD 段直达。
    /// 输出：seed / vertexCount（球面顶点数）/ height（恒 0，v1/v2 平面格式已移除）/
    ///   minElev / maxElev / skeletonVer。版本不符或损坏 → false。</summary>
    public static bool Peek(string path, out int seed, out int vertexCount, out int height,
                            out float minElev, out float maxElev, out ushort version)
    {
        seed = 0; vertexCount = 0; height = 0; minElev = 0f; maxElev = 0f; version = 0;
        try
        {
            string abs = ResolvePath(path);
            using var fs = new FileStream(abs, FileMode.Open, IOFileAccess.Read);
            using var r = new ChunkReader(fs);
            if (r.Magic != Magic) return false;                 // 坏 magic
            if (r.SkeletonVer != Version) return false;         // 版本不符（旧 v1-v5 已移除）
            version = r.SkeletonVer;
            if (!r.SeekSegment("HEAD")) return false;           // 缺必需段
            seed = (int)r.Get32();
            r.GetFloat();                                       // radiusKm（列表不需要）
            vertexCount = (int)r.Get32();
            minElev = r.GetFloat();
            maxElev = r.GetFloat();
            return true;
        }
        catch
        {
            return false;   // 打不开/损坏（列表显示"(读取失败)"）
        }
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
    public float RadiusKm = MapArchive.DefaultRadiusKm;           // 星球半径（v5 头部；旧存档默认地球 6371）
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

    /// <summary>构建球面桶索引。⚠️ 惰性构建 + 并行采样会并发修改集合（Collection was modified 崩溃），
    /// 用锁固持单线程首建（2026-08 修正：不再以"非主线程首建"误报——Godot mono _Ready 托管线程 id≠引擎主线程）。</summary>
    private readonly object _bucketLock = new();
    private int _bucketsBuildThread = -1;   // 首次构建线程（并发检测：被另一线程闯入时告警）

    public void EnsureBuckets()
    {
        if (_buckets != null) return;
        // ⚠️ 桶构建：惰性 + 首次构建固持单线程（防"忘预构建 / 并发首建改 List 集合"崩溃）。
        // 2026-08 修正误报：Godot mono 节点 _Ready 的托管线程 id(=2) ≠ OS.GetMainThreadId()(=1)，
        //   旧守卫"非主线程首建即告警"会误报主线程同步读档。故改为并发检测：
        //   同一实例若被**另一线程**在首建线程仍持锁期间再次闯入 → 真并发首建（崩溃前兆），才告警；
        //   否则（仅单一线程首建）不告警——覆盖 Godot mono 托管线程 id 与引擎主线程判定差异。
        lock (_bucketLock)
        {
            if (_buckets != null) return;      // 双检锁：锁内再查一次
            int tid = System.Environment.CurrentManagedThreadId;
            if (_bucketsBuildThread != -1 && tid != _bucketsBuildThread)
                LogService.LogErr("MapArchive", $"⚠️ 桶索引并发首建：线程(tid={tid}) 与首建线程(tid={_bucketsBuildThread}) 同时构建——并发修改集合崩溃前兆");
            _bucketsBuildThread = tid;
        }
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
