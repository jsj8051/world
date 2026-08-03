using Godot;
using System;
using World.Biome;
using World.Tectonics;

namespace World.MapGen;

/// <summary>星球生成参数（管线输入；来自 MapGenerator 导出属性）。</summary>
public class PlanetParams
{
    public int Seed;
    public float AxialTilt;
    public float Insolation;
    public bool ProgradeRotation;
    public float RotationSpeed;
    public float RadiusKm;
}

/// <summary>
/// 星球生成管线（2026-08-03 阶段化重构）：模拟后派生阶段
///   Stage1 气候：温度/降水/biome + 洋流场
///   Stage2 水文：河流迭代（动态流向+输沙侵蚀沉积，改海拔）→ 湖泊
///   Stage3 生态：河岸带（沿岸 Riparian）
///   Stage4 资源：矿藏（矿化强度+分位数）
///   Stage5 统计：min/max 存档范围
/// 纯计算（无 Godot 对象依赖、无 GD.Print）——同步/异步两路径共用，
/// 后台线程安全。板块模拟（Run）在管线外（MapGenerator 负责进度 UI）。
/// </summary>
public class PlanetPipeline
{
    // ── 输出（阶段结果；写档/显示用）──
    public float[] Elev;          // 海拔（米，0=海平面；含河流侵蚀修正）
    public float[] Temp, Precip;
    public byte[] Biome;
    public int[] RiverFlow;
    public float[] RiverVolume;
    public byte[] RiverLevel, LakeLevel, MineralLevel;
    public byte[] SoilLevel;       // 土壤肥力 1-5（0=海洋；2026-08-03）
    public Vector3[] CurrentDirs;
    public float[] CurrentWarmth, CurrentStrength;
    public float MinElev, MaxElev, MinTemp, MaxTemp, MinPrecip, MaxPrecip;
    public int RiparianCount;

    public void Run(TectonicsSimulation sim, PlanetParams p, Action<float> onProgress = null)
    {
        var verts = sim.GlobalGrid.Vertices;
        var grid = sim.GlobalGrid;
        int vn = verts.Length;
        float sea = sim.SeaLevel;

        // 海拔（相对海平面）
        var disp = sim.Displacement;
        Elev = new float[vn];
        MinElev = float.MaxValue; MaxElev = float.MinValue;
        for (int i = 0; i < vn; i++)
        {
            Elev[i] = disp[i] - sea;   // 米，0=海平面
            if (Elev[i] < MinElev) MinElev = Elev[i];
            if (Elev[i] > MaxElev) MaxElev = Elev[i];
        }
        float span = Mathf.Max(-MinElev, MaxElev);

        StageClimate(sim, p, span, onProgress);
        onProgress?.Invoke(0.8f);
        StageHydrology(sim, p, sea, span);
        onProgress?.Invoke(0.9f);
        StageRiparian(sim, p, sea);
        StageMinerals(sim, p, span);
        StageSoil(sim, p, span);
        onProgress?.Invoke(0.97f);
        ComputeStats();
    }

    // ── Stage1 气候：温度/降水/biome + 洋流场 ──
    private void StageClimate(TectonicsSimulation sim, PlanetParams p, float span, Action<float> onProgress)
    {
        var verts = sim.GlobalGrid.Vertices;
        var grid = sim.GlobalGrid;
        int vn = verts.Length;
        World.Biome.WindField.Prograde = p.ProgradeRotation;   // 自转方向 → 盛行风科里奥利偏转
        World.Biome.WindField.RotationSpeed = p.RotationSpeed; // 自转速度 → 科里奥利强度
        var climate = new ClimateGenerator(p.Seed, p.AxialTilt, p.Insolation);
        Temp = new float[vn];
        Precip = new float[vn];
        Biome = new byte[vn];

        // 盛行风降水回调：球面点 → 归一化海拔（最近顶点，桶查询）
        System.Func<Vector3, float> elevSampler = point =>
        {
            Vector3 dir = point.Normalized();
            int id = grid.NearestId(dir);
            return span > 1e-6f ? Elev[id] / span : 0f;
        };

        // 洋流场（v2 风应力旋度 + 流函数 → 闭合环流）
        var eNorm = new float[vn];
        for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? Elev[i] / span : 0f;
        World.Biome.OceanCurrent.Compute(verts, grid.Neighbors, eNorm,
            out CurrentDirs, out CurrentWarmth, out CurrentStrength);
        // 沿岸采样：球面点 → 最近海洋顶点的冷暖+强度（陆地点查邻居，距离衰减）
        System.Func<Vector3, (float warm, float str)> warmthSampler = point =>
        {
            Vector3 dir = point.Normalized();
            int id = grid.NearestId(dir);
            if (CurrentWarmth[id] != 0f)
                return (CurrentWarmth[id], CurrentStrength != null ? CurrentStrength[id] : 1f);
            float best = 0f, bestD = 1e9f, bestStr = 1f;
            foreach (var nb in grid.Neighbors[id])
            {
                if (CurrentWarmth[nb] != 0f)
                {
                    float d = Mathf.Acos(Mathf.Clamp(verts[id].Dot(verts[nb]), -1f, 1f));
                    if (d < bestD) { bestD = d; best = CurrentWarmth[nb]; bestStr = CurrentStrength != null ? CurrentStrength[nb] : 1f; }
                }
            }
            float decay = Mathf.Exp(-bestD / 0.08f);   // 距岸衰减（0.08rad ≈ 500km）
            return (best * decay, bestStr);
        };
        climate.SetOceanCurrent(warmthSampler);

        System.Threading.Tasks.Parallel.For(0, vn, i =>
        {
            Vector3 pos = verts[i] * p.RadiusKm;
            float e1 = span > 1e-6f ? Elev[i] / span : 0f;
            float t = climate.ComputeTemperature(pos, e1);
            float pp = climate.ComputePrecipitation(pos, e1, elevSampler);
            Temp[i] = t;
            Precip[i] = pp;
            Biome[i] = (byte)BiomeClassifier.Classify(e1, t, pp);
            if ((i & 0xFF) == 0)
                onProgress?.Invoke(0.7f + 0.3f * i / vn);
        });
    }

    // ── Stage2 水文：河流迭代（动态流向+输沙侵蚀沉积）→ 湖泊 ──
    private void StageHydrology(TectonicsSimulation sim, PlanetParams p, float sea, float span)
    {
        var verts = sim.GlobalGrid.Vertices;
        var grid = sim.GlobalGrid;
        int vn = verts.Length;
        var eNorm = new float[vn];
        for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? Elev[i] / span : 0f;
        RiverSystem.ComputeIterative(verts, grid.Neighbors, eNorm, Elev,
            Precip, Temp, waterThreshold: 5000f, lakeThreshold: 200f,
            seaLevelM: 0f, elevSpan: span, rounds: 4,
            out RiverFlow, out RiverVolume, out RiverLevel, out LakeLevel, out _);
        // 侵蚀后更新范围（存档用；Elev 含河谷/三角洲）
        MinElev = float.MaxValue; MaxElev = float.MinValue;
        foreach (var e in Elev) { if (e < MinElev) MinElev = e; if (e > MaxElev) MaxElev = e; }
    }

    // ── Stage3 生态：河岸带（沿岸陆地格 → Riparian）──
    private void StageRiparian(TectonicsSimulation sim, PlanetParams p, float sea)
    {
        var grid = sim.GlobalGrid;
        int vn = Elev.Length;
        RiparianCount = 0;
        for (int i = 0; i < vn; i++)
        {
            if (Elev[i] <= sea) continue;                        // 海洋不算
            if (RiverLevel[i] > 0 || LakeLevel[i] > 0) continue; // 水格本身不算
            bool wet = false;
            foreach (var nb in grid.Neighbors[i])
                if (RiverLevel[nb] > 0 || LakeLevel[nb] > 0) { wet = true; break; }
            if (wet) { Biome[i] = (byte)BiomeType.Riparian; RiparianCount++; }
        }
    }

    // ── Stage4 资源：矿藏（矿化强度增强 + 分位数通用模型）──
    private void StageMinerals(TectonicsSimulation sim, PlanetParams p, float span)
    {
        var verts = sim.GlobalGrid.Vertices;
        var grid = sim.GlobalGrid;
        int vn = verts.Length;
        var eNorm = new float[vn];
        for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? Elev[i] / span : 0f;
        MineralSystem.ComputeMinerals(verts, grid.Neighbors, RiverFlow, eNorm, Precip,
            sim.WorldCrust?.Age, sim.MineralHydro, sim.MineralSed, sim.MineralMeta,
            sim.WorldCrust, p.Seed, out MineralLevel);
    }

    // ── Stage5 土壤肥力：biome 基础 + 冲积/火山加成 − 坡度/气候惩罚 ──
    private void StageSoil(TectonicsSimulation sim, PlanetParams p, float span)
    {
        var grid = sim.GlobalGrid;
        int vn = Elev.Length;
        var eNorm = new float[vn];
        for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? Elev[i] / span : 0f;
        SoilSystem.ComputeSoil(eNorm, Biome, Precip, Temp,
            sim.WorldCrust?.MaficVolcanic, RiverFlow, out SoilLevel);
    }

    // ── Stage6 统计：存档范围 ──
    private void ComputeStats()
    {
        MinTemp = float.MaxValue; MaxTemp = float.MinValue;
        MinPrecip = float.MaxValue; MaxPrecip = float.MinValue;
        foreach (var t in Temp) { if (t < MinTemp) MinTemp = t; if (t > MaxTemp) MaxTemp = t; }
        foreach (var pp in Precip) { if (pp < MinPrecip) MinPrecip = pp; if (pp > MaxPrecip) MaxPrecip = pp; }
    }
}
