using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.Biome;
using World.Services;
using World.Surface;
using World.Tectonics;

namespace World.MapGen;

/// <summary>
/// 地图生成器（生成阶段，离线）：海拔场（板块构造） + 气候场（温度/降水）→ 生物群系 →
/// 球面顶点存档（v3，无投影无平面中转）。
///
/// 生成与游玩解耦：生成可花费数十分钟，产出存档；游玩只读存档。
///
/// 2026-08-16：板块生成（SotE 模式）已推倒删除。
/// 2026-08-02：接入 tectonics.js 移植的球面板块模拟（M1-M3 全部完成），
///   海拔/气候/biome 全部计算在球面顶点上，直接存 v3 球面存档（无平面中转）。
/// </summary>
public partial class MapGenerator : Node
{
    /// <summary>性能分段基线（2026-08-17 T40 防劣化）：最近一次 Generate() 的各段耗时（板块/管线/存档/总）。</summary>
    public static readonly Dictionary<string, long> LastTimings = new();
    [Export] public int Seed = 42;
    [Export] public float RadiusKm = MapArchive.DefaultRadiusKm;   // 星球半径（默认地球 6371；UI/headless 覆盖）
    [Export] public string OutputPath = "user://maps/map1.mpa";
    [Export] public bool AutoQuit = false; // true=生成后退出；false=切到查看场景
    [Export] public bool ExportPreview = true; // 生成后导出海拔预览 PNG（headless 调参可视化）
    [Export] public int TectonicsGridN = 32;   // 板块模拟 Icosahedron 细分（32→10242 顶点）
    [Export] public float SimMegayears = 600f; // 板块模拟时长（百万年）
    [Export] public float SimStepMy = 4f;      // 模拟时间步（百万年）——2026-08-03：2→4 已验证质量一致（板块 7 块后性能）
    [Export] public float OceanScale = 1f;     // 海洋水量系数（×2000m 基准平均深度；0.6=少水多陆，1.4=水世界）
    [Export] public float SupercontinentCycleMy = 150f; // 超级大陆聚合-裂解周期（百万年）
    [Export] public float ErosionScale = 1f;   // 侵蚀/风化强度倍率（0.5=温和，2=剧烈夷平）
    [Export] public int NumPlates = 8;         // 初始板块数
    [Export] public int NumContinents = 6;     // 大陆块数（≈球面噪声波长；2=超大陆/20=碎陆，2026-08-10）
    [Export] public bool ProgradeRotation = true; // 自转方向：true=顺转（地球式），false=逆转（金星式）
    [Export] public float AxialTilt = 23.4f;   // 轴向倾角（度）：0=无季节，23.4=地球，90=极端季节
    [Export] public float Insolation = 1.0f;    // 恒星辐照度（相对地球 1AU）：0.7=远、冷，1.3=近、热
    [Export] public float RotationSpeed = 1.0f; // 自转速度（相对地球 24h=1.0）：0.2=慢（金星式），5=快（木星式）

    // 洋流场（生成时算好，WriteSpherical 存档 + 气候修正用）
    private Vector3[] _curDirs;
    private float[] _curWarmth;
    private float[] _curStrength;

    // 河流（生成时算好，WriteSpherical 存档；MapViewer 河流图层用）
    private byte[] _riverLevel;   // 每顶点：0=无河，1-3=级别
    private int[] _riverFlow;     // 每顶点流向（MapViewer 重建路径用）
    private float[] _riverVolume; // 每顶点累积水量 mm（河流图层/断流判定用）
    private byte[] _lakeLevel;    // 每顶点湖泊标记（0/1）
    private byte[] _mineralLevel; // 每顶点矿藏（v3.5：(富度<<4)|矿种；0=无）
    private byte[] _soilLevel;    // 每顶点土壤肥力 1-5（0=海洋；v3.6）

    public override void _Ready()
    {
        // headless 调参支持：-- --seed=7 / -- --seed 7 / --seed=7 覆盖 [Export]
        // 支持：seed/TectonicsGridN/SimMegayears/NumPlates/AutoQuit/OutputPath
        var ua = OS.GetCmdlineUserArgs();
        for (int i = 0; i < ua.Length; i++)
        {
            string a = ua[i];
            string v = a.StartsWith("--") ? a.Substring(2) : a; // 兼容 --seed=X 与 seed=X
            bool TryInt(string key, Action<int> set)
            {
                if (v.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(v.AsSpan(key.Length + 1), out int val)) { set(val); return true; }
                if ((v == "--" + key || v == key) && i + 1 < ua.Length
                    && int.TryParse(ua[i + 1], out int val2)) { set(val2); i++; return true; }
                return false;
            }
            bool TryFloat(string key, Action<float> set)
            {
                if (v.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(v.AsSpan(key.Length + 1), out float val)) { set(val); return true; }
                if ((v == "--" + key || v == key) && i + 1 < ua.Length
                    && float.TryParse(ua[i + 1], out float val2)) { set(val2); i++; return true; }
                return false;
            }
            if (TryInt("seed", s => Seed = s)) { }
            else if (TryInt("TectonicsGridN", g => TectonicsGridN = g)) { }
            else if (TryFloat("SimMegayears", m => SimMegayears = m)) { }
            else if (TryFloat("OceanScale", o => OceanScale = o)) { }
            else if (TryFloat("SupercontinentCycleMy", c => SupercontinentCycleMy = c)) { }
            else if (TryFloat("ErosionScale", e => ErosionScale = e)) { }
            else if (TryInt("NumPlates", p => NumPlates = p)) { }
            else if (TryInt("NumContinents", c => NumContinents = c)) { }
            else if (TryFloat("AxialTilt", t => AxialTilt = t)) { }
            else if (TryFloat("Insolation", i => Insolation = i)) { }
            else if (TryFloat("RotationSpeed", r => RotationSpeed = r)) { }
            else if (TryFloat("RadiusKm", r => RadiusKm = r)) { }
            else if (v.StartsWith("ExportPreview", StringComparison.OrdinalIgnoreCase))
            {
                // 支持 --ExportPreview false/true/0/1（headless 验证常关预览，防导出 PNG 卡进程）
                if (v.Contains("false") || v.Contains("=0")) ExportPreview = false;
                else if (v.Contains("true") || v.Contains("=1")) ExportPreview = true;
            }
            else if (v.StartsWith("OutputPath=", StringComparison.OrdinalIgnoreCase)) { OutputPath = v.Substring("OutputPath=".Length); }
            else if (v.StartsWith("--OutputPath=", StringComparison.OrdinalIgnoreCase)) { OutputPath = v.Substring("--OutputPath=".Length); }
            else if (v == "AutoQuit" || v == "--AutoQuit" || v == "AutoQuit=true" || v == "--AutoQuit=true") AutoQuit = true;
            else if (v.StartsWith("ProgradeRotation", StringComparison.OrdinalIgnoreCase))
            {
                // 支持：--ProgradeRotation false/true/0/1（空格或 =）或裸参数（=顺转）
                // ⚠️ else if 链会短路：此分支必须单独处理，内部自己消费后续参数
                if (v.Contains("false") || v.Contains("=0")) ProgradeRotation = false;
                else if (v.Contains("true") || v.Contains("=1")) ProgradeRotation = true;
                else if (i + 1 < ua.Length)
                {
                    string nv = ua[i + 1].ToLowerInvariant();
                    if (nv == "false" || nv == "0") { ProgradeRotation = false; i++; }
                    else if (nv == "true" || nv == "1") { ProgradeRotation = true; i++; }
                    else ProgradeRotation = true;   // 后续参数不是 bool → 裸参数=顺转
                }
                else ProgradeRotation = true;
            }
        }
        LogService.Log("MapGenerator", $"user args: {string.Join(" | ", ua)}  -> seed={Seed} n={TectonicsGridN} {NumPlates}plates {SimMegayears}My ProgradeRotation={ProgradeRotation}");
        Generate();
        if (AutoQuit)
            GetTree().Quit();
        else
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/core/MapViewer.tscn");
    }

    public void Generate()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ── 海拔生成：球面板块模拟（tectonics.js 移植，M1-M3）──
        var swTec = System.Diagnostics.Stopwatch.StartNew();
        LogService.Log("MapGenerator", $"板块模拟开始 seed={Seed} n={TectonicsGridN} {SimMegayears}My ...");
        var sim = new TectonicsSimulation(TectonicsGridN);
        sim.NumContinents = NumContinents;    // 大陆块数（构造格局；2=超大陆/20=碎陆）
        sim.GenerateInitialCrust(Seed, 0.6f, RadiusKm);   // ⚠️ 2026-08-18 行星标度（A：hypsography 按 sqrt(R/R⊕)——小星球地壳薄、均衡山低）
        sim.SplitIntoPlates(NumPlates, Seed);
        sim.OceanScale = OceanScale;              // 海洋水量（海陆比）
        sim.SupercontinentCycleMy = SupercontinentCycleMy;  // 超级大陆周期
        sim.ErosionScale = ErosionScale * Mathf.Sqrt(MapArchive.DefaultRadiusKm / RadiusKm);   // ⚠️ 2026-08-18 行星侵蚀标度（B：小星球格距小/搬运快——侵蚀相对效率高——平衡海拔低）
        sim.Run(SimMegayears, SimStepMy);
        sim.ComputeSubductionZones();   // 俯冲带检测（2026-08-18：主动边缘——大陆架场跳过）
        {
            int subCnt = 0; if (sim.SubductionMask != null) foreach (var b in sim.SubductionMask) if (b == 1) subCnt++;
            LogService.Log("MapGenerator", $"俯冲带（主动边缘）={subCnt} 格");
        }
        var disp = sim.Displacement;
        float sea = sim.SeaLevel;
        float minD = float.MaxValue, maxD = float.MinValue;
        foreach (var d in disp) { if (d < minD) minD = d; if (d > maxD) maxD = d; }
        LogService.Log("MapGenerator", $"板块模拟完成 disp[{minD:F0},{maxD:F0}]m sealevel={sea:F0}m land={sim.LandFractionAboveSea() * 100:F1}%");
        swTec.Stop();

        // ── 阶段化管线（2026-08-03 重构：气候→水文→生态→资源→统计；同步/异步共用）──
        var swPipe = System.Diagnostics.Stopwatch.StartNew();
        var pipe = new PlanetPipeline();
        pipe.Run(sim, new PlanetParams
        {
            Seed = Seed,
            AxialTilt = AxialTilt,
            Insolation = Insolation,
            ProgradeRotation = ProgradeRotation,
            RotationSpeed = RotationSpeed,
            RadiusKm = RadiusKm,
        });
        var simVerts = sim.GlobalGrid.Vertices;   // 单位方向
        int vn = simVerts.Length;
        _riverFlow = pipe.RiverFlow; _riverVolume = pipe.RiverVolume;
        _riverLevel = pipe.RiverLevel; _lakeLevel = pipe.LakeLevel; _mineralLevel = pipe.MineralLevel;
        _soilLevel = pipe.SoilLevel;
        _curDirs = pipe.CurrentDirs; _curWarmth = pipe.CurrentWarmth; _curStrength = pipe.CurrentStrength;

        LogService.Log("MapGenerator", $"河岸带 {pipe.RiparianCount} 格");
        int mineralCount = 0;
        var mdist = new int[9];
        for (int i = 0; i < vn; i++)
            if (_mineralLevel[i] != 0)
            {
                mineralCount++;
                mdist[MineralSystem.TypeOf(_mineralLevel[i])]++;
            }
        LogService.Log("MapGenerator", $"矿藏 {mineralCount} 格 ({mineralCount * 100f / vn:F1}%)" +
            $" 铁={mdist[1]} 铜={mdist[2]} 锡={mdist[3]} 金={mdist[4]} 煤={mdist[5]} 盐={mdist[6]} 石料={mdist[7]} 宝石={mdist[8]}");

        // 侵蚀堆积场统计（诊断：山脊侵蚀/谷底堆积）
        if (pipe.ErosionNet != null)
        {
            float eMin = float.MaxValue, eMax = float.MinValue; int ero = 0, dep = 0;
            for (int i = 0; i < vn; i++)
            {
                float v = pipe.ErosionNet[i];
                if (v < eMin) eMin = v;
                if (v > eMax) eMax = v;
                if (v < 0f) ero++; else if (v > 0f) dep++;
            }
            LogService.Log("MapGenerator", $"侵蚀堆积场: 侵蚀区{ero}格({ero * 100f / vn:F1}%) 堆积区{dep}格({dep * 100f / vn:F1}%) 净速率[{eMin:F0},{eMax:F0}]m");
        }

        sw.Stop();
        swPipe.Stop();
        LogService.Log("MapGenerator", $"seed={Seed} 球面顶点 {vn} elev[{pipe.MinElev:F4},{pipe.MaxElev:F4}] " +
            $"temp[{pipe.MinTemp:F1},{pipe.MaxTemp:F1}]°C precip[{pipe.MinPrecip:F0},{pipe.MaxPrecip:F0}]mm took {sw.ElapsedMilliseconds}ms");
        // ⚠️ 2026-08-17 性能分段基线（T40 防劣化）：板块/管线/存档各段耗时记录
        long archiveMs = sw.ElapsedMilliseconds - swTec.ElapsedMilliseconds - swPipe.ElapsedMilliseconds;
        LastTimings["tectonics_ms"] = swTec.ElapsedMilliseconds;
        LastTimings["pipeline_ms"] = swPipe.ElapsedMilliseconds;
        LastTimings["archive_ms"] = archiveMs;
        LastTimings["total_ms"] = sw.ElapsedMilliseconds;
        LogService.Log("MapGenTiming", $"板块={swTec.ElapsedMilliseconds}ms 管线={swPipe.ElapsedMilliseconds}ms 存档={archiveMs}ms 总={sw.ElapsedMilliseconds}ms (n={TectonicsGridN} seed={Seed})");
        // ⚠️ 2026-08-17 监督机制：历史对比 + 劣化告警（每次生成自动记录）
        var (hisAvg, hisMax, hisCnt) = World.Diagnostics.PerfLog.Stats("mapgen", "total_ms");
        World.Diagnostics.PerfLog.Append("mapgen", $"n{TectonicsGridN}/s{Seed}", LastTimings);
        if (hisCnt > 0)
        {
            if (sw.ElapsedMilliseconds > hisAvg * 1.5)
                LogService.Log("性能", $"⚠️ MapGen 劣化告警：总={sw.ElapsedMilliseconds}ms > 历史均值 {hisAvg:F0}ms ×1.5（峰值 {hisMax}ms / {hisCnt} 次）——检查近期算法改动");
            else
                LogService.Log("性能", $"MapGen 本次总={sw.ElapsedMilliseconds}ms（历史均值 {hisAvg:F0}ms / 峰值 {hisMax}ms / {hisCnt} 次 → 正常）");
        }
        long total = vn;
        var sb = new System.Text.StringBuilder("biome dist: ");
        var dist = new int[32];   // biome 0..31（旧 0-13 + 柯本 14-31）
        foreach (var b in pipe.Biome) dist[b]++;
        for (int i = 0; i < dist.Length; i++)
        {
            var name = ((BiomeType)i).ToString();
            sb.Append($"{name}={dist[i]}({dist[i] * 100.0 / total:F1}%) ");
        }
        LogService.Log("MapGenerator", sb.ToString());

        // 季风/月降水/月温度 byte 化（v3.7/v3.8 存档）
        var monsoonLevel = new byte[vn];
        for (int i = 0; i < vn; i++)
            monsoonLevel[i] = FieldCodec.RatioToByte(pipe.MonsoonStrength[i]);
        var monthPrecip = new byte[MonsoonSystem.MonthCount][];
        for (int m = 0; m < MonsoonSystem.MonthCount; m++)
        {
            monthPrecip[m] = new byte[vn];
            for (int i = 0; i < vn; i++)
            {
                float ratio = pipe.MonthPrecip[m][i];   // ⚠️ 2026-08-05 修：月→年改造后已是比例(Σ=1)，勿再除年降水（双重归一化→byte≈0→图层全黄）
                monthPrecip[m][i] = FieldCodec.RatioToByte(ratio);
            }
        }
        // 月温度（−60~60°C → 0-255；温度系统月度化 v3.8）
        var monthTemp = new byte[MonsoonSystem.MonthCount][];
        if (pipe.MonthTemp != null)
            for (int m = 0; m < MonsoonSystem.MonthCount; m++)
            {
                monthTemp[m] = new byte[vn];
                for (int i = 0; i < vn; i++)
                    monthTemp[m][i] = FieldCodec.TempToByte(pipe.MonthTemp[m][i]);
            }

        // 写档前卫生检查（2026-08-19：NaN/全 0 异常场在写档前暴露，防静默写坏档）
        pipe.HealthCheck();
        // ⚠️ 引擎适配器重构（2026-08）：Run 路径无日志——NaN 消毒计数与模型状态报告由调用层记录
        if (pipe.NansSanitized > 0)
            LogService.Log("PlanetPipeline", $"⚠️ NaN 消毒：{pipe.NansSanitized} 顶点 → 0");
        ClimateModel.PrintReport(pipe);

        MapArchive.WriteSpherical(OutputPath, Seed, simVerts, pipe.MinElev, pipe.MaxElev, pipe.Elev,
            pipe.Temp, pipe.Precip, pipe.Biome, pipe.MinTemp, pipe.MaxTemp, pipe.MinPrecip, pipe.MaxPrecip,
            prograde: ProgradeRotation, rotationSpeed: RotationSpeed, axialTilt: AxialTilt,
            currentDirs: _curDirs, currentWarmth: _curWarmth, currentStrength: _curStrength,
            psi: pipe.Psi,   // ⚠️ 必须传：读取端 ver≥4 无条件读 psi 段（2026-08-10 修复，曾致河流段错位）
            riverLevel: _riverLevel, riverFlow: _riverFlow, riverVolume: _riverVolume, lakeLevel: _lakeLevel,
            mineralLevel: _mineralLevel, soilLevel: _soilLevel,
            monsoonLevel: monsoonLevel, monthPrecip: monthPrecip, monthTemp: monthTemp,
            radiusKm: RadiusKm);

        // 季风区统计（headless 验证用）
        int monsoonCells = 0;
        for (int i = 0; i < vn; i++) if (monsoonLevel[i] >= 64) monsoonCells++;
        LogService.Log("MapGenerator", $"季风区（强度≥0.25）：{monsoonCells} 格 ({monsoonCells * 100f / vn:F1}%)");

        // 大陆块统计（2026-08-10：验证 NumContinents 参数——陆地格球面连通分量。
        // 注意：600My 演化后大陆会被裂解/侵蚀切割成许多小块，连通块数 ≠ 初始 N；
        // 真正反映格局的是最大块面积占比（N=2 应 ~70%+ 超大陆，N=20 应分散）。
        var (masses, maxFrac) = CountLandMasses(sim.GlobalGrid.Neighbors, pipe.Elev);
        LogService.Log("MapGenerator", $"陆地连通块：{masses} 块（最大块占陆地 {maxFrac * 100f:F0}%；参数 NumContinents={NumContinents}，碎渣风险当 N≫n/2）");

        if (ExportPreview)
            ExportSphericalPreview(simVerts, pipe.Elev, pipe.MinElev, pipe.MaxElev);
    }

    /// <summary>陆地连通块统计：elev&gt;0 按球面邻接 BFS 分块，返回（块数, 最大块面积占比）。</summary>
    private static (int masses, float maxFrac) CountLandMasses(int[][] neighbors, float[] elev)
    {
        int n = elev.Length;
        var visited = new bool[n];
        int masses = 0, maxSize = 0, landCells = 0;
        for (int i = 0; i < n; i++)
        {
            if (visited[i] || elev[i] <= 0f) continue;
            masses++;
            int size = 0;
            var stack = new System.Collections.Generic.Stack<int>();
            stack.Push(i);
            visited[i] = true;
            while (stack.Count > 0)
            {
                int v = stack.Pop();
                size++;
                foreach (int nb in neighbors[v])
                    if (!visited[nb] && elev[nb] > 0f) { visited[nb] = true; stack.Push(nb); }
            }
            if (size > maxSize) maxSize = size;
        }
        for (int i = 0; i < n; i++) if (elev[i] > 0f) landCells++;
        return (masses, landCells > 0 ? (float)maxSize / landCells : 0f);
    }

    /// <summary>
    /// 后台生成（UI 用）：纯数据计算跑后台线程（模拟+气候+biome，不碰 Godot 对象），
    /// 完成后回调主线程写存档。进度 0..1：模拟 0-0.7，气候 0.7-1.0。
    /// </summary>
    /// <param name="onProgress">主线程进度回调（0..1）。</param>
    /// <param name="onDone">主线程完成回调（true=成功写出存档）。</param>
    public void GenerateAsync(Action<float> onProgress, Action<bool, string> onDone)
    {
        int seed = Seed, n = TectonicsGridN, plates = NumPlates;
        float my = SimMegayears, step = SimStepMy, radius = RadiusKm;
        string outPath = OutputPath;
        bool exportPreview = ExportPreview;

        Task.Run(() =>
        {
            // ── 板块模拟（纯数据）──
            var sim = new TectonicsSimulation(n);
            sim.NumContinents = NumContinents;    // 大陆块数（构造格局；2=超大陆/20=碎陆）
            sim.GenerateInitialCrust(seed, 0.6f, radius);
            sim.SplitIntoPlates(plates, seed);
            int totalSteps = (int)(my / step);
            sim.RunWithProgress(my, step, frac => onProgress(frac * 0.7f));
            sim.ComputeSubductionZones();   // 俯冲带检测（2026-08-18）
            var disp = sim.Displacement;
            float sea = sim.SeaLevel;

            var simVerts = sim.GlobalGrid.Vertices;
            int vn = simVerts.Length;
            var svElev = new float[vn];
            float minE = float.MaxValue, maxE = float.MinValue;
            for (int i = 0; i < vn; i++)
            {
                svElev[i] = disp[i] - sea;
                if (svElev[i] < minE) minE = svElev[i];
                if (svElev[i] > maxE) maxE = svElev[i];
            }

            // ── 阶段化管线（气候→水文→生态→资源；后台线程纯计算，共用同步逻辑）──
            var pipe = new PlanetPipeline();
            pipe.Run(sim, new PlanetParams
            {
                Seed = seed,
                AxialTilt = AxialTilt,
                Insolation = Insolation,
                ProgradeRotation = ProgradeRotation,
                RotationSpeed = RotationSpeed,
                RadiusKm = radius,
            }, frac => onProgress(0.7f + 0.3f * frac));
            _riverFlow = pipe.RiverFlow; _riverVolume = pipe.RiverVolume;
            _riverLevel = pipe.RiverLevel; _lakeLevel = pipe.LakeLevel; _mineralLevel = pipe.MineralLevel;
            _soilLevel = pipe.SoilLevel;
            _curDirs = pipe.CurrentDirs; _curWarmth = pipe.CurrentWarmth; _curStrength = pipe.CurrentStrength;

            // 季风/月降水/月温度 byte 化（v3.7/v3.8 存档；后台线程禁止 GD.Print 但可算）
            var monsoonLevel = new byte[vn];
            for (int i = 0; i < vn; i++)
                monsoonLevel[i] = FieldCodec.RatioToByte(pipe.MonsoonStrength[i]);
            var monthPrecip = new byte[MonsoonSystem.MonthCount][];
            for (int m = 0; m < MonsoonSystem.MonthCount; m++)
            {
                monthPrecip[m] = new byte[vn];
                for (int i = 0; i < vn; i++)
                {
                    float ratio = pipe.MonthPrecip[m][i];   // ⚠️ 2026-08-05 修：月→年改造后已是比例(Σ=1)，勿再除年降水（双重归一化→byte≈0→图层全黄）
                    monthPrecip[m][i] = FieldCodec.RatioToByte(ratio);
                }
            }
            // 月温度（−60~60°C → 0-255）
            var monthTemp = new byte[MonsoonSystem.MonthCount][];
            if (pipe.MonthTemp != null)
                for (int m = 0; m < MonsoonSystem.MonthCount; m++)
                {
                    monthTemp[m] = new byte[vn];
                    for (int i = 0; i < vn; i++)
                        monthTemp[m][i] = FieldCodec.TempToByte(pipe.MonthTemp[m][i]);
                }

            // 后台线程不调 pipe.HealthCheck()（含 GD.Print 禁止后台调用；同步路径已覆盖卫生检查）

            bool ok = MapArchive.WriteSpherical(outPath, seed, simVerts, pipe.MinElev, pipe.MaxElev, pipe.Elev,
                pipe.Temp, pipe.Precip, pipe.Biome, pipe.MinTemp, pipe.MaxTemp, pipe.MinPrecip, pipe.MaxPrecip,
                prograde: ProgradeRotation, rotationSpeed: RotationSpeed, axialTilt: AxialTilt,
                currentDirs: _curDirs, currentWarmth: _curWarmth, currentStrength: _curStrength,
                psi: pipe.Psi,
                riverLevel: _riverLevel, riverFlow: _riverFlow, riverVolume: _riverVolume, lakeLevel: _lakeLevel,
                mineralLevel: _mineralLevel, soilLevel: _soilLevel,
                monsoonLevel: monsoonLevel, monthPrecip: monthPrecip, monthTemp: monthTemp,
                radiusKm: radius, log: false);   // 后台线程禁止 GD.Print
            if (exportPreview)
                ExportSphericalPreview(simVerts, pipe.Elev, pipe.MinElev, pipe.MaxElev);
            return (ok, outPath);
        }).ContinueWith(t =>
        {
            // 线程池回调：线程安全的事（打印错误）直接做，UI 更新必须主线程
            if (t.IsFaulted)
                // 后台线程回调：LogService 纪律禁止，保持 GD.Print 直调（ADR-0004 §决策4）
                GD.PrintErr($"[MapGenerator] async failed: {t.Exception?.GetBaseException().Message}");
            CallDeferred(nameof(FinishAsync), t.IsCompletedSuccessfully && t.Result.ok, t.IsCompletedSuccessfully ? t.Result.outPath : "");
        });
        // 注意：onProgress 在后台线程被调用（UI 进度条读写 volatile 由 Godot 主线程 _Process 驱动更安全，
        // 但 ProgressBar.Value 属性主线程写后台线程读会竞争——这里直接回调，Godot Control 属性非线程安全。
        // 稳妥做法：onProgress 只记录 volatile 字段，主线程 _Process 读。此处简化：回调里 QueueRedraw 有风险，
        // 由调用方（MapGenMenu）保证只更新 volatile float。见 MapGenMenu.cs 注释。
    }

    private void FinishAsync(bool ok, string path)
    {
        _asyncDone?.Invoke(ok, path);
    }
    private Action<bool, string> _asyncDone;

    /// <summary>后台生成完成回调（主线程）。</summary>
    public void SetAsyncDoneCallback(Action<bool, string> cb) => _asyncDone = cb;

    /// <summary>球面预览导出：等距柱状投影渲染（仅调试可视化，非存档格式）。</summary>
    private void ExportSphericalPreview(Vector3[] verts, float[] elev, float minE, float maxE)
    {
        const int w = 1024, h = 512;
        float range = maxE - minE;
        float hSea = -minE / range;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            float lat = 90f - 180f * y / (h - 1);
            float la = Mathf.DegToRad(lat);
            float sinLa = Mathf.Sin(la), cosLa = Mathf.Cos(la);
            for (int x = 0; x < w; x++)
            {
                float lon = -180f + 360f * x / (w - 1);
                float lo = Mathf.DegToRad(lon);
                var p = new Vector3(cosLa * Mathf.Cos(lo), sinLa, cosLa * Mathf.Sin(lo));
                // 最近顶点（预览用，够快）
                int best = -1; float bd = float.MaxValue;
                for (int i = 0; i < verts.Length; i++)
                {
                    float d = (verts[i] - p).LengthSquared();
                    if (d < bd) { bd = d; best = i; }
                }
                float e = (elev[best] - minE) / range;
                float e1 = (e - hSea) / (hSea > 0.5f ? hSea : 1f - hSea);
                img.SetPixel(x, y, PlanetColors.ElevationToColor(e1));
            }
        }
        img.SavePng("user://maps/elev_preview.png");
        // ⚠️ 后台线程禁止 GD.Print——日志由调用方（主线程）打
    }
}
