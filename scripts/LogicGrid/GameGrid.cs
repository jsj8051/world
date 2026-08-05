using Godot;
using System.Collections.Generic;
using World.MapGen;

namespace World.LogicGrid;

/// <summary>
/// 逻辑网格 = 模拟网格本体（10n²+2 球面顶点胞，零重采样、零中转）。
/// 格 id = 模拟顶点 id —— 与气候模拟、MapViewer 显示完全同一套网格（用户拍板球面直通 v3 的延伸）。
/// 游戏人文层（省份/国家/城市/单位）直接落在此网格上。
///
/// 自包含：从 .gmp 读回不依赖 .mpa（自然层快照全量持有；邻接是确定性计算，读档现场重建）。
/// </summary>
public class GameGrid
{
    /// <summary>星球半径默认值（km；.mpa 存档未存半径，生成参数里 RadiusKm 默认地球）。</summary>
    public const float DefaultRadiusKm = 6371f;

    // ── 头部参数 ──
    public int GridN;              // 原始网格参数 n（顶点数 = 10n²+2）
    public int N;                  // 格数 = 顶点数
    public int Seed;
    public float RadiusKm = DefaultRadiusKm;
    public bool ProgradeRotation = true;
    public float RotationSpeed = 1f;
    public float AxialTilt = 23.4f;
    public float Insolation = 1f;  // .mpa 未存辐照度，默认地球（与气候默认一致）

    public Vector3[] Verts;        // 球面单位方向
    public float MinElev, MaxElev, MinTemp, MaxTemp, MinPrecip, MaxPrecip;

    // ── 自然层快照（自包含；从 MapData 全量复制或 .gmp 读回）──
    public float[] Elev;           // 每格海拔（米；&gt;0=陆地，海平面 0 与生成口径一致）
    public float[] Temp;           // 年均温 °C
    public float[] Precip;         // 年降水 mm
    public byte[] Biome;           // BiomeType
    public byte[] RiverLevel;      // 0=无河
    public int[] RiverFlow;        // 流向顶点 id，-1=无
    public float[] RiverVolume;    // 流量 mm
    public byte[] LakeLevel;       // 0=无湖
    public byte[] MineralLevel;    // (富度<<4)|矿种；0=无
    public byte[] SoilLevel;       // 1-5；0=海洋
    public byte[] MonsoonLevel;    // 0-255 → 0-1
    public byte[][] MonthPrecip;   // [12][n] 月降水比例
    public byte[][] MonthTemp;     // [12][n] 月温度 −60~60°C→0-255
    public Vector3[] CurrentDirs;  // 洋流方向（0=内陆/无流）
    public float[] CurrentWarmth;  // 洋流冷暖
    public float[] CurrentStrength;// 洋流强度

    // ── 派生量（现场算，不存档）──
    private int[][] _neighbors;    // 球面邻接（惰性）
    private bool[] _isLand;        // 海陆判定（惰性）

    // ── 人文层（游戏状态；0=未分配/无主，海洋格恒 0）──
    public int[] Province;         // 省份 id
    public int[] Country;          // 国家 id
    // 城市/单位/军队：人文层后续扩展

    // ── 构建 ──

    /// <summary>从世界地图存档（MapData）构建逻辑网格（自然层全量快照 + 人文层初始 0）。</summary>
    public static GameGrid FromMapData(MapData map)
    {
        int n = map.Verts.Length;
        var g = new GameGrid
        {
            N = n,
            GridN = (int)Mathf.Round(Mathf.Sqrt(Mathf.Max(0, (n - 2) / 10f))),
            Seed = map.Seed,
            ProgradeRotation = map.ProgradeRotation,
            RotationSpeed = map.RotationSpeed,
            AxialTilt = map.AxialTilt,
            Verts = (Vector3[])map.Verts.Clone(),
            MinElev = map.MinElev, MaxElev = map.MaxElev,
            MinTemp = map.MinTemp, MaxTemp = map.MaxTemp,
            MinPrecip = map.MinPrecip, MaxPrecip = map.MaxPrecip,
            Elev = (float[])map.Elev.Clone(),
            Temp = map.Temp != null ? (float[])map.Temp.Clone() : new float[n],
            Precip = map.Precip != null ? (float[])map.Precip.Clone() : new float[n],
            Biome = map.Biome != null ? (byte[])map.Biome.Clone() : new byte[n],
            RiverLevel = map.RiverLevel != null ? (byte[])map.RiverLevel.Clone() : new byte[n],
            RiverFlow = map.RiverFlow != null ? (int[])map.RiverFlow.Clone() : EnumerableFill(n, -1),
            RiverVolume = map.RiverVolume != null ? (float[])map.RiverVolume.Clone() : new float[n],
            LakeLevel = map.LakeLevel != null ? (byte[])map.LakeLevel.Clone() : new byte[n],
            MineralLevel = map.MineralLevel != null ? (byte[])map.MineralLevel.Clone() : new byte[n],
            SoilLevel = map.SoilLevel != null ? (byte[])map.SoilLevel.Clone() : new byte[n],
            MonsoonLevel = map.MonsoonLevel != null ? (byte[])map.MonsoonLevel.Clone() : new byte[n],
            MonthPrecip = map.MonthPrecip != null ? Clone2D(map.MonthPrecip) : Empty2D(n),
            MonthTemp = map.MonthTemp != null ? Clone2D(map.MonthTemp) : Empty2D(n),
            CurrentDirs = map.CurrentDirs != null ? (Vector3[])map.CurrentDirs.Clone() : new Vector3[n],
            CurrentWarmth = map.CurrentWarmth != null ? (float[])map.CurrentWarmth.Clone() : new float[n],
            CurrentStrength = map.CurrentStrength != null ? (float[])map.CurrentStrength.Clone() : new float[n],
            Province = new int[n],
            Country = new int[n],
        };
        return g;
    }

    private static int[] EnumerableFill(int n, int v)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = v;
        return a;
    }

    private static byte[][] Clone2D(byte[][] src)
    {
        var dst = new byte[src.Length][];
        for (int m = 0; m < src.Length; m++)
            dst[m] = src[m] != null ? (byte[])src[m].Clone() : null;
        return dst;
    }

    private static byte[][] Empty2D(int n)
    {
        var a = new byte[12][];
        for (int m = 0; m < 12; m++) a[m] = new byte[n];
        return a;
    }

    /// <summary>转回 MapData（MapViewer 等以 MapData 为输入的工具复用；自然层数据零改动）。
    /// ⚠️ 返回前主线程预构建桶索引（EnsureBuckets）——惰性构建 + 并行采样 = 并发修改集合崩溃
    ///   （MapArchive.Read 也做了同样的事，这里必须补，否则 ToMapData 产物会在 Parallel 里崩）。</summary>
    public MapData ToMapData()
    {
        var m = new MapData
        {
            Version = 3,
            Seed = Seed,
            ProgradeRotation = ProgradeRotation,
            RotationSpeed = RotationSpeed,
            AxialTilt = AxialTilt,
            Verts = (Vector3[])Verts.Clone(),
            Elev = (float[])Elev.Clone(),
            Temp = (float[])Temp.Clone(),
            Precip = (float[])Precip.Clone(),
            Biome = (byte[])Biome.Clone(),
            MinElev = MinElev, MaxElev = MaxElev,
            MinTemp = MinTemp, MaxTemp = MaxTemp,
            MinPrecip = MinPrecip, MaxPrecip = MaxPrecip,
            CurrentDirs = (Vector3[])CurrentDirs.Clone(),
            CurrentWarmth = (float[])CurrentWarmth.Clone(),
            CurrentStrength = (float[])CurrentStrength.Clone(),
            RiverLevel = (byte[])RiverLevel.Clone(),
            RiverFlow = (int[])RiverFlow.Clone(),
            RiverVolume = (float[])RiverVolume.Clone(),
            LakeLevel = (byte[])LakeLevel.Clone(),
            MineralLevel = (byte[])MineralLevel.Clone(),
            SoilLevel = (byte[])SoilLevel.Clone(),
            MonsoonLevel = (byte[])MonsoonLevel.Clone(),
            MonthPrecip = Clone2D(MonthPrecip),
            MonthTemp = Clone2D(MonthTemp),
        };
        m.EnsureBuckets();
        return m;
    }

    // ── 查询 ──

    /// <summary>球面邻接表（确定性重建：桶内球面距离 &lt; 1.5×平均格距；与 MapData 同算法）。</summary>
    public int[][] Neighbors => _neighbors ??= BuildNeighbors();

    /// <summary>海陆判定（elev &gt; 0 = 陆地，与生成器 land% 口径一致）。</summary>
    public bool[] IsLand => _isLand ??= BuildIsLand();

    public bool IsLandCell(int i) => Elev[i] > 0f;

    /// <summary>格是否沿海（任一球面邻居是海洋）。</summary>
    public bool IsCoast(int cell)
    {
        foreach (int nb in Neighbors[cell])
            if (!IsLandCell(nb)) return true;
        return false;
    }

    /// <summary>每格胞面积（km²；均匀近似 4πR²/N——Icosahedron 胞面积几乎相等）。</summary>
    public float CellAreaKm2 => 4f * Mathf.Pi * RadiusKm * RadiusKm / N;

    /// <summary>两格球面距离（km）。</summary>
    public float DistKm(int a, int b)
    {
        float ang = Mathf.Acos(Mathf.Clamp(Verts[a].Dot(Verts[b]), -1f, 1f));
        return ang * RadiusKm;
    }

    // ── 内部 ──

    private int[][] BuildNeighbors()
    {
        EnsureBuckets();
        float cell = Mathf.Sqrt(4f * Mathf.Pi / N);   // 平均格距（rad）
        float cosR = Mathf.Cos(cell * 1.5f);          // 邻居半径 1.5×格距
        var result = new int[N][];
        for (int i = 0; i < N; i++)
        {
            var list = new List<int>();
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
        return result;
    }

    private bool[] BuildIsLand()
    {
        var a = new bool[N];
        for (int i = 0; i < N; i++) a[i] = Elev[i] > 0f;
        return a;
    }

    // ── 球面桶索引（与 MapData 相同：采样/邻接加速）──
    private const int BucketsLat = 16;
    private const int BucketsLon = 32;
    private List<int>[,] _buckets;

    private void EnsureBuckets()
    {
        if (_buckets != null) return;
        _buckets = new List<int>[BucketsLat, BucketsLon];
        for (int y = 0; y < BucketsLat; y++)
            for (int x = 0; x < BucketsLon; x++)
                _buckets[y, x] = new List<int>();
        for (int i = 0; i < N; i++)
        {
            (int by, int bx) = BucketOf(Verts[i]);
            _buckets[by, bx].Add(i);
        }
    }

    private static (int, int) BucketOf(Vector3 v)
    {
        float lat = Mathf.Asin(Mathf.Clamp(v.Y, -1f, 1f));
        float lon = Mathf.Atan2(v.Z, v.X);
        int by = (int)Mathf.Clamp((lat / Mathf.Pi + 0.5f) * BucketsLat, 0, BucketsLat - 1);
        // ⚠️ 极区单桶（经度在极点汇聚，分桶会让 3×3 邻桶查不到最近顶点 → 两极条纹）
        int bx;
        if (by == 0 || by == BucketsLat - 1)
            bx = 0;
        else
            bx = (int)(((lon / Mathf.Pi + 1f) * 0.5f * BucketsLon) % BucketsLon);
        return (by, bx);
    }
}
