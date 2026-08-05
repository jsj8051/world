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
    // 季风环流诊断场（v3.7/v3.8 存档 + 元数据校验）
    public float[] MonsoonStrength;
    public float[][] MonthPrecip;   // [12][n] 月降水比例
    public float[][] MonthTemp;     // [12][n] 月温度
    public Vector3[][] MonthWind;   // [12][n] 月风场（统一风场；不存档，校验/调试用）

    // ── 运行时缓存（2026-08-16 抽象框架迁移：场类 Compute 共享的中间量）──
    public TectonicsSimulation Sim;          // Run 注入（Stage2-5 消费 WorldCrust/MineralHydro 等）
    public PlanetParams P;                   // Run 注入（tilt/rot/seed）
    public SphereGrid Grid;                  // 全局网格
    public Vector3[] Verts;
    public int[][] Neighbors;
    public float[] ENorm;                    // 归一化海拔（-1..1）
    public float ElevSpan;                   // 海拔跨度（米）
    public System.Func<Vector3, float> ElevSampler;   // 球面点 → 归一化海拔（降水盛行风/雨影用）
    public World.Biome.ClimateGenerator Climate;      // 年温/年降水算法实例
    // MonsoonSystem 月数据中间量（柯本分类消费）
    public float[] HotM, ColdM, DryP;
    public int[] DryIdx;
    public float[] ErosionNet;             // 侵蚀堆积场：每格净沉积趋势（m/演化期，正=堆积/负=侵蚀）
    public Vector3[] WindYear;             // 年合成风场（12月平均；洋流/侵蚀堆积共享，P1 优化）
    public float[] TempBase;               // 气候基准温度（纬度+海拔+洋流+噪声，无季节项；月温度公式的 base）

    public void Run(TectonicsSimulation sim, PlanetParams p, Action<float> onProgress = null)
    {
        // ⚠️ 2026-08-16 抽象框架迁移：Run 只做"环境注入 + 编排"，计算全部在场类 Compute()。
        Sim = sim; P = p;
        Grid = sim.GlobalGrid;
        Verts = Grid.Vertices;
        Neighbors = Grid.Neighbors;
        int vn = Verts.Length;

        // 风场全局参数 + 气候算法实例
        World.Biome.WindField.Prograde = p.ProgradeRotation;   // 自转方向 → 盛行风科里奥利偏转
        World.Biome.WindField.RotationSpeed = p.RotationSpeed; // 自转速度 → 科里奥利强度
        Climate = new ClimateGenerator(p.Seed, p.AxialTilt, p.Insolation);
        TempBase = new float[vn];
        Temp = new float[vn];
        Precip = new float[vn];
        Biome = new byte[vn];

        // ⚠️ 抽象框架：拓扑排序执行全部场 Compute（海拔→温度→降水→湿润降温→月温度→…→土壤）
        //   + 闭环 Apply + 全流水线校验
        ClimateModel.Run(this);

        onProgress?.Invoke(0.8f);
        SanitizeNaNs();   // 浮点 NaN 消毒（防存档污染；Stage2 河流侵蚀后 Elev 可能含 NaN）
        ComputeStats();
        onProgress?.Invoke(1f);
    }

    /// <summary>NaN → 0 消毒（写档前最后防线；河流侵蚀等浮点链路偶发 0/0）。</summary>
    private void SanitizeNaNs()
    {
        int nan = 0;
        for (int i = 0; i < Elev.Length; i++)
        {
            if (float.IsNaN(Elev[i])) { Elev[i] = 0f; nan++; }
            if (Temp != null && float.IsNaN(Temp[i])) Temp[i] = 0f;
            if (Precip != null && float.IsNaN(Precip[i])) Precip[i] = 0f;
        }
        if (nan > 0)
            GD.Print($"[PlanetPipeline] ⚠️ NaN 消毒：海拔 {nan} 顶点 → 0");
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
