using Godot;
using System;
using World.Biome;
using World.Services;
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
    public float[] Psi;                        // 洋流流函数（环流圈提取；存档 v4 起，供显示层"每环最外圈"）
    public float MinElev, MaxElev, MinTemp, MaxTemp, MinPrecip, MaxPrecip;
    public int RiparianCount;
    /// <summary>上次 Run 消毒的 NaN 顶点数（调用方按需记录日志；Run 路径本身无日志）。</summary>
    public int NansSanitized { get; private set; }
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
        ClimateModel.Run(this, frac => onProgress?.Invoke(0.8f * frac));   // 气候段 0→0.8（每场上报，非死停）

        onProgress?.Invoke(0.8f);
        SanitizeNaNs();   // 浮点 NaN 消毒（防存档污染；Stage2 河流侵蚀后 Elev 可能含 NaN）
        ComputeStats();
        onProgress?.Invoke(1f);
    }

    /// <summary>NaN → 0 消毒（写档前最后防线；河流侵蚀等浮点链路偶发 0/0）。
    /// ⚠️ 2026-08-19 扩展：全字段扫描（原只查 Elev/Temp/Precip 3 个主字段——月场/洋流场
    /// 的 NaN 会静默写档，下游显示全错）。消毒后调用方按需记录日志。
    /// ⚠️ 引擎适配器重构（2026-08）：本方法不打印（Run 路径无引擎调用）——调用方读
    /// <see cref="NansSanitized"/> 决定是否记录。</summary>
    private void SanitizeNaNs()
    {
        int nan = 0;
        void Scan(float[] arr, string name)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
                if (float.IsNaN(arr[i])) { arr[i] = 0f; nan++; }
        }
        void Scan2D(float[][] arr, string name)
        {
            if (arr == null) return;
            foreach (var row in arr) Scan(row, name);
        }

        Scan(Elev, "海拔"); Scan(Temp, "温度"); Scan(Precip, "降水");
        Scan(TempBase, "基准温度"); Scan(MonsoonStrength, "季风");
        Scan2D(MonthTemp, "月温度"); Scan2D(MonthPrecip, "月降水");
        Scan(RiverVolume, "河流流量");
        Scan(CurrentWarmth, "洋流冷暖"); Scan(CurrentStrength, "洋流强度"); Scan(Psi, "流函数");
        Scan(ErosionNet, "侵蚀堆积");
        ScanV3(WindYear, "年风场");
        NansSanitized = nan;
    }

    private void ScanV3(Vector3[] arr, string name)
    {
        if (arr == null) return;
        for (int i = 0; i < arr.Length; i++)
        {
            if (float.IsNaN(arr[i].X)) { arr[i] = new Vector3(0, arr[i].Y, arr[i].Z); }
            if (float.IsNaN(arr[i].Y)) { arr[i] = new Vector3(arr[i].X, 0, arr[i].Z); }
            if (float.IsNaN(arr[i].Z)) { arr[i] = new Vector3(arr[i].X, arr[i].Y, 0); }
        }
    }

    /// <summary>写档前卫生检查：统计残留 NaN/全 0 异常场（防\"全 0 = 语义变更 bug\"静默写档）。</summary>
    public void HealthCheck()
    {
        int vn = Verts.Length;
        void Report(string name, float[] arr, bool zeroOk = false)
        {
            if (arr == null) return;
            int nan = 0, zero = 0; float mn = float.MaxValue, mx = float.MinValue;
            foreach (var v in arr)
            {
                if (float.IsNaN(v)) nan++;
                else if (v == 0f) zero++;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            string flag = "";
            if (nan > 0) flag += $"⚠️NaN{nan} ";
            if (!zeroOk && zero > vn * 0.99) flag += $"⚠️全0({zero}) ";
            if (flag.Length > 0)
                LogService.Log("PlanetPipeline", $"卫生: {name} [{mn:F2},{mx:F2}] {flag}");
        }
        Report("海拔", Elev); Report("温度", Temp); Report("降水", Precip);
        Report("季风", MonsoonStrength, zeroOk: true);   // 无季风区可以全 0
        Report("河流流量", RiverVolume, zeroOk: true);   // 干星球可以无河
        Report("洋流强度", CurrentStrength, zeroOk: true);
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
