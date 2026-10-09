using Godot;
using System;
using System.Collections.Generic;
using World.Domain;
using World.Planet;
using World.Services;

namespace World.Archive;

/// <summary>加载后的地图数据。v3 = 球面顶点场；v1/v2 = 等距柱状平面场。
/// ★2026-10-09 存档清退：本类原与 `MapArchive`（.mpa 编解码）同处 `Archive/MapArchive.cs`；
///   存档编解码已作为 Legacy 资产删除（待新线 WorldGen 重做），本数据容器因被
///   `GameGrid` / `WildCropsSystem` 等生产逻辑继续使用，故独立成文件保留；
///   默认半径顺带收口至 `PlanetConstants.EarthRadiusKm`（消除第 3 份 6371 重复）。
/// ⚠️ 2026-08-02：必须是 class（非 struct）——球面桶索引 _buckets 是惰性构建的
///   可变缓存，struct 值传递会让每次采样都重建桶（65 万格 × 4 次采样 × 512 桶
///   = 6.6 亿次 List.Add → 进入游戏/切图层极慢）。</summary>
public class MapData
{
    public int Seed;
    public ushort Version;
    public float RadiusKm = PlanetConstants.EarthRadiusKm;           // 星球半径（v5 头部；旧存档默认地球 6371）
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

    // v8 单存档化：文明演化结果（无 CIVI 段 = 纯自然地图 = null）
    public World.CivSim.CivSimResult Civilization;

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
                LogService.LogErr("MapData", $"⚠️ 桶索引并发首建：线程(tid={tid}) 与首建线程(tid={_bucketsBuildThread}) 同时构建——并发修改集合崩溃前兆");
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
