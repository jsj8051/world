using Godot;
using System;
using System.Collections.Generic;
using World.Services;

namespace World.Tectonics
{
    /// <summary>
    /// 板块构造主模拟（tectonics.js Lithosphere.js + CrustGenerator.js + Simulation.js 的
    /// C# 移植，2026-08-02，M1 最小可跑原型）。
    ///
    /// M1 范围（验证"板块移动 + 边界形态"）：
    ///   ✅ 初始地壳生成（CrustGenerator：海洋/大陆厚度模板）
    ///   ✅ 初始板块分割（简化：球面均匀种子 + 最近邻，替代图像分割）
    ///   ✅ 板块速度（简化：每板随机欧拉旋转角速度）
    ///   ✅ 板块移动（欧拉旋转，Plate.move）
    ///   ✅ 合并回全局（merge_plates_to_master：密度决定顶层）
    ///   ✅ 均衡位移输出（isostasy → elevation）
    ///   ⏳ 俯冲/裂谷/侵蚀/变质/成岩（M3 补全）
    ///
    /// 源码参考：docs/tectonics-ref/noncompiled/models/lithosphere/Lithosphere.js
    ///
    /// 职责分片（partial class）：
    ///   TectonicsSimulation.cs        —— 字段/构造函数/主循环入口 Run、RunWithProgress
    ///   TectonicsSimulation.Init.cs   —— 初始地壳生成、初始板块分割、裂谷带/俯冲带检测
    ///   TectonicsSimulation.Convergent.cs —— 裂谷/俯冲/增生楔（离散/汇聚边界）
    ///   TectonicsSimulation.Supercontinent.cs —— 碰撞缝合/超大陆裂解/重分割
    ///   TectonicsSimulation.Merge.cs  —— MergePlatesToMaster 合并到全局
    ///   TectonicsSimulation.Erosion.cs —— 地表过程（侵蚀/风化/成岩/变质）
    ///   TectonicsSimulation.Isostasy.cs —— 均衡位移/岩石圈挠曲/海平面求解
    /// </summary>
    public partial class TectonicsSimulation
    {
        public SphereGrid GlobalGrid;
        public MaterialDensity Material = new MaterialDensity();
        public float SurfaceGravity = 9.8f;      // m/s²
        /// <summary>行星标度因子 = √(R/R⊕)（2026-08-18 拍板 A：地形幅度按 sqrt(R) 一阶标度）。
        /// GenerateInitialCrust 赋值；R=6371km → 1（地球档行为逐位不变）。
        /// 所有"地球标定的绝对米数"常量（采样均值/厚度映射阈值与厚度值/洋壳厚度/海平面水量/
        /// 地壳厚度上限）统一乘此因子——小星球地壳薄、均衡山低、海洋浅，坡度保持与地球相当。</summary>
        public float RadiusScale = 1f;
        public float SeaLevel = 0f;
        public List<Plate> Plates = new List<Plate>();
        public Crust WorldCrust;                 // 合并后的全局地壳
        public float[] Displacement;             // 均衡位移（m，相对基准面）
        public float[] Elevation;                // 表面高度（m，相对海平面，≥-海深）

        public float[] TopPlateMap;              // 每全局顶点 → 顶层板块 id
        public int[] PlateCount;                 // 每全局顶点 → 覆盖的板块数（M3-2）
        public byte[] SubductionMask;            // 1=俯冲带（2026-08-18：板块汇聚边界——邻板负浮力——主动边缘）
        private byte[] _continentalRiftMask;     // 大陆裂谷带（2026-08-18：低频结构——地堑盆地——东非裂谷型）
        private byte[] _mergeGlobalMask;         // Merge 复用缓冲区（GC 优化 C）
        private float[] _erodeSurface;           // 侵蚀复用（GC 优化：每步 1.3MB × 300 步）
        private Crust _erodeDeltaReusable;       // 侵蚀 delta 复用（8 数组 × n）
        private float[] _mergeMasterDensity;     // Merge masterDensity 复用（C3）
        private float[] _mergeMassBuf;           // Merge plate 密度复用（C4）
        private float[] _mergeThickBuf;          // C4
        private float[] _mergeDensityBuf;        // C4
        private Crust _mergeGlobalizedCrust;     // Merge 复用板重采样缓冲（GC 优化 C2）
        public Crust Accretion;                  // 俯冲增生楔（M3-3，全局 delta，加到顶层板）
        // ⚠️ 2026-08-03 矿藏模拟化：矿化强度（事件驱动累积——矿在板块演化中"长出来"）
        public float[] MineralHydro;             // 热液矿化强度（裂谷/俯冲/增生事件累积 → 铜/金）
        public float[] MineralSed;               // 沉积矿化强度（沉积增厚累积 → 煤/盐）
        public float[] MineralMeta;              // 变质矿化强度（变质累积 → 宝石/铁）
        public int GridN = 16;                   // Icosahedron 细分（verts≈2562）
        public int NumContinents = 6;            // 大陆块数（≈球面噪声波长数；2=超大陆/20=碎陆，2026-08-10 参数化）
        public bool EnableErosion = true;        // M3：地表过程（侵蚀/风化/成岩/变质）开关
        public bool EnableRifting = true;        // M3-2：裂谷（离散边界生成新洋壳）开关
        public bool EnableSupercontinent = true; // M3-4：超级大陆循环（150My 重新分割）开关
        public float SupercontinentCycleMy = 150f;  // 裂解冷却时间（百万年；方案 B：超大陆裂解后 N My 内不再裂）
        public float OceanScale = 1f;       // 海洋水量系数（× 基准平均深度 2000m；<1 少水多陆，>1 多水少陆）
        public float ErosionScale = 1f;     // 侵蚀/风化强度倍率（0.5=温和山地保留，2=剧烈夷平）
        public float TotalOceanDepth = 0f;       // M3-5：总水量（平均海洋深度 m，守恒常量）
        public int Seed = 0;                     // 初始地壳种子（ResetPlates 洋壳年龄重置用，方案 A）
        public int InitialPlateCount = 8;        // 初始板块数（ResetPlates 固定请求数，防棘轮收缩）

        public TectonicsSimulation(int gridN = 16)
        {
            GridN = gridN;
            GlobalGrid = new SphereGrid(gridN);
            WorldCrust = new Crust(GlobalGrid);
            Accretion = new Crust(GlobalGrid);
            int n = GlobalGrid.VertexCount;
            Displacement = new float[n];
            Elevation = new float[n];
            TopPlateMap = new float[n];
            PlateCount = new int[n];
        }

        // ── 主循环 ──

        /// <summary>跑 N 百万年。每步：老化 → 移动 → 合并 → 计算位移 → 按体积重解海平面。</summary>
        public void Run(float megayears, float stepMy) => RunWithProgress(megayears, stepMy, null);

        /// <summary>大陆裂谷带初始化（2026-08-18 B：地堑盆地——低频结构，大陆尺度波长）。
        /// 球面低频噪声带（freq = NumContinents/2π × 2.5——裂谷带尺度），noise > 0.7 为裂谷带。
        /// UpdateRifting 内对大陆格 felsic 减薄（张裂地堑沉降）——不填洋壳（东非裂谷型）。</summary>
        public void InitContinentalRiftMask()
        {
            int n = GlobalGrid.VertexCount;
            _continentalRiftMask = new byte[n];
            var noise = new FastNoiseLite();
            noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            noise.Frequency = NumContinents / (2f * Mathf.Pi) * 2.5f;
            noise.Seed = Seed + 777;
            for (int i = 0; i < n; i++)
            {
                var p = GlobalGrid.Vertices[i];
                if (noise.GetNoise3D(p.X, p.Y, p.Z) > 0.7f) _continentalRiftMask[i] = 1;
            }
        }

        /// <summary>俯冲带检测（2026-08-18 用户拍板 A：主动边缘无大陆架——自然涌现）。
        /// 边界格（邻居板不同）且邻板该处浮力负（BuoyancyVec 与径向反向 = 下沉俯冲板片）→ 俯冲带。
        /// 大陆架场跳过俯冲带海岸（智利/日本型——无大陆架）。</summary>
        public void ComputeSubductionZones()
        {
            int n = GlobalGrid.VertexCount;
            SubductionMask = new byte[n];
            // ⚠️ TopPlateMap 非重叠格默认 0（只合并重叠时更新）——不可靠。
            // 改用板 Mask 边界（BoundaryNormal≠0）+ 该处负浮力（下沉俯冲板片）→ 俯冲带。
            int boundaryCells = 0, negBuoy = 0;
            for (int p = 0; p < Plates.Count; p++)
            {
                var plate = Plates[p];
                for (int local = 0; local < plate.Mask.Length; local++)
                {
                    if (plate.BoundaryNormal[local].LengthSquared() <= 1e-12f) continue;   // 非边界格
                    boundaryCells++;
                    if (plate.BuoyancyVec[local].Dot(plate.LocalGrid.Vertices[local]) < 0f)
                    {
                        int g = plate.GlobalIdsOfLocalCells[local];
                        if (g >= 0 && g < n) { SubductionMask[g] = 1; negBuoy++; }
                    }
                }
            }
            LogService.Log("Tectonics", $"俯冲检测: 板边界格={boundaryCells} 负浮力命中={negBuoy} Plates.Count={Plates.Count}");
        }

        /// <summary>
        /// 跑 N 百万年（带进度回调）。onProgress 在【调用线程】被调用（模拟是纯数据，
        /// UI 场景下由后台线程驱动，回调只更新 volatile 字段，主线程 _Process 读）。
        /// </summary>
        public void RunWithProgress(float megayears, float stepMy, Action<float> onProgress)
        {
            if (_continentalRiftMask == null) InitContinentalRiftMask();   // 大陆裂谷带（2026-08-18）
            ComputeDisplacement();         // 先算初始位移场
            InitializeOceanVolume(2000f * OceanScale * RadiusScale);  // M3-5：水量守恒常量（原版 average_ocean_depth；用户可调水量；行星标度：小星球总水量按 sqrt(R) 缩）
            int steps = (int)(megayears / stepMy);
            // 矿化强度初始化（矿藏模拟化：事件累积）
            if (MineralHydro == null || MineralHydro.Length != GlobalGrid.VertexCount)
            {
                MineralHydro = new float[GlobalGrid.VertexCount];
                MineralSed = new float[GlobalGrid.VertexCount];
                MineralMeta = new float[GlobalGrid.VertexCount];
            }
            else
            {
                Array.Clear(MineralHydro, 0, MineralHydro.Length);
                Array.Clear(MineralSed, 0, MineralSed.Length);
                Array.Clear(MineralMeta, 0, MineralMeta.Length);
            }
            var swMove = new System.Diagnostics.Stopwatch();
            var swMerge = new System.Diagnostics.Stopwatch();
            var swRift = new System.Diagnostics.Stopwatch();
            var swErode = new System.Diagnostics.Stopwatch();
            var swOther = new System.Diagnostics.Stopwatch();
            for (int s = 0; s < steps; s++)
            {
                swOther.Start();
                // 洋壳老化：age += stepMy（My→秒单位）。密度随年龄增大，
                // 超过地幔密度后产生负浮力 → 驱动俯冲/板块运动（Schellart 模型核心）。
                // 放在 Merge 前：裂谷生成的新洋壳（age=0）本步不老化，保留年轻态。
                foreach (var plate in Plates)
                    for (int i = 0; i < plate.Crust.Age.Length; i++)
                        plate.Crust.Age[i] += stepMy * Units.MEGAYEAR;
                swOther.Stop();

                swMove.Start();
                // ⚠️ 优化 B（2026-08-02）：各板块 Move 完全独立（不同局部网格）→ 并行。
                //   n=64 有 ~8 板块，多核 CPU 理论 ×4-8。NearestId 桶索引只读（并行安全，
                //   但桶必须已构建——Move 前由 ResampleCrustToGlobal 等预热或首次构建，
                //   ⚠️ 惰性构建在并行时并发修改集合会崩溃，见 EnsureBuckets 注释）。
                System.Threading.Tasks.Parallel.For(0, Plates.Count, pi =>
                {
                    Plates[pi].Move(stepMy, GlobalGrid, Material, SurfaceGravity);
                });
                swMove.Stop();

                swMerge.Start();
                MergePlatesToMaster();
                swMerge.Stop();
                // ⚠️ 2026-08-16 方案 B：碰撞缝合（聚合）——旧模拟板块从不合并（plates 恒 7），
                //   大陆永远拼不大。接触边界 ≥2% 全球 → 合并（印度-欧亚式缝合）。
                if (EnableSupercontinent)
                {
                    swOther.Start();
                    TryMergeCollidingPlates();
                    swOther.Stop();
                }
                if (EnableRifting)
                {
                    swRift.Start();
                    UpdateRifting();      // M3-2：裂谷生成新洋壳（洋中脊）
                    UpdateSubducted();    // M3-3：俯冲回收老洋壳（洋沟），写入 Accretion
                    swRift.Stop();
                    swMerge.Start();
                    MergePlatesToMaster();// 重新合并（裂谷/俯冲改变了板块）
                    ApplyAccretion();     // 增生楔加到顶层板（俯冲上盘造山带）
                    MergePlatesToMaster();// 重新合并（增生楔加入板块后）
                    swMerge.Stop();
                }
                if (EnableSupercontinent && (s * stepMy) - _lastSplitMy >= SupercontinentCycleMy * 0.25f)
                {
                    swOther.Start();
                    // ⚠️ 2026-08-16 方案 B（用户拍板）：继承式裂解，替代固定周期全局洗牌。
                    //   旧实现每 SupercontinentCycleMy（150My）无条件 ResetPlates 全盘重分割 →
                    //   板块位置随机化、聚合历史清零 → 大陆永远小碎块（用户观察"不会有像地球
                    //   的大陆"）。B：只在【超大陆存在】（最大板块占全球 >25%）时裂解，且只切
                    //   大板块（保留位置继承演化）；平时不裂解 → 板块漂移碰撞自然聚合拼大。
                    if (TrySplitSupercontinent())
                    {
                        _lastSplitMy = s * stepMy;
                        MergePlatesToMaster();
                    }
                    swOther.Stop();
                }
                swOther.Start();
                ComputeDisplacement();
                SolveSeaLevelByVolume();   // M3-5：按水量守恒重解（land% 自然浮动）
                swOther.Stop();
                if (EnableErosion)
                {
                    swErode.Start();
                    var delta = ApplySurfaceProcesses(stepMy);  // M3：侵蚀/风化/成岩/变质
                    SyncWorldToPlates(delta);                   // 净变化量写回各板块（原版 integrate_deltas）
                    ComputeDisplacement();                      // 重算（侵蚀后位移变了）
                    SolveSeaLevelByVolume();
                    swErode.Stop();
                }
                // 诊断：每 50My 打印位移直方图（陆地高度分布）+ 耗时
                if (s % 25 == 0)
                {
                    int[] bins = new int[6];   // <-4km, -4~0, 0~1km, 1~3km, 3~6km, >6km
                    foreach (var d in Displacement)
                    {
                        if (d < -4000) bins[0]++;
                        else if (d < 0) bins[1]++;
                        else if (d < 1000) bins[2]++;
                        else if (d < 3000) bins[3]++;
                        else if (d < 6000) bins[4]++;
                        else bins[5]++;
                    }
                    LogService.Log("Tectonics", $"hist(深<-4k, 海-4~0, 低0~1k, 中1~3k, 高3~6k, 峰>6k): " +
                             $"{bins[0]},{bins[1]},{bins[2]},{bins[3]},{bins[4]},{bins[5]}");
                    long totalMs = swMove.ElapsedMilliseconds + swMerge.ElapsedMilliseconds
                        + swRift.ElapsedMilliseconds + swErode.ElapsedMilliseconds + swOther.ElapsedMilliseconds;
                    LogService.Log("Tectonics", $"耗时 move={swMove.ElapsedMilliseconds}ms merge={swMerge.ElapsedMilliseconds}ms " +
                             $"rift={swRift.ElapsedMilliseconds}ms erode={swErode.ElapsedMilliseconds}ms " +
                             $"other={swOther.ElapsedMilliseconds}ms total={totalMs}ms");
                }
                if (EnableErosion)
                {
                    var delta = ApplySurfaceProcesses(stepMy);  // M3：侵蚀/风化/成岩/变质
                    SyncWorldToPlates(delta);                   // 净变化量写回各板块（原版 integrate_deltas）
                    ComputeDisplacement();                      // 重算（侵蚀后位移变了）
                    SolveSeaLevelByVolume();
                }
                if (s % 10 == 0 || s == steps - 1)
                    LogService.Log("Tectonics", $"step {s}/{steps} ({s * stepMy:F0}My) plates={Plates.Count} " +
                             $"disp[{FieldOps.Min(Displacement):F0},{FieldOps.Max(Displacement):F0}]m land={LandFractionAboveSea() * 100:F1}%");
                onProgress?.Invoke((s + 1f) / steps);
            }
            SolveSeaLevelByVolume();
        }

        private int LocalGridCount => GlobalGrid.VertexCount;

        /// <summary>
        /// 超级大陆循环（Lithosphere.resetPlates 移植，M3-4）：
        /// 用软流圈速度场重新分割板块（聚合-裂解循环）。
        ///
        /// 流程（原版）：
        ///   1. pressure = 流体压力（buoyancy 扩散）→ asthenosphere_velocity
        ///   2. angular_velocity = velocity × pos（旋转最快的区域 = 板块中心）
        ///   3. guess_plate_map(angular_velocity, 7, 200)：图像分割 → 新板块
        ///   4. 每块新板：mask + 继承当前 WorldCrust
        ///
        /// 简化：流体压力用 buoyancy 场直接当速度（扩散细节省略），
        /// 分割参数 7 块板 / 最小 200 格（n=16 时 ≈5% 全球）。
        /// </summary>
        private float _lastSplitMy = -1000f;   // 上次超大陆裂解时间（方案 B：冷却期内不再裂）
    }
}
