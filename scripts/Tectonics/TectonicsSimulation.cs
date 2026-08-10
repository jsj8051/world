using Godot;
using System;
using System.Collections.Generic;

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
    /// </summary>
    public class TectonicsSimulation
    {
        public SphereGrid GlobalGrid;
        public MaterialDensity Material = new MaterialDensity();
        public float SurfaceGravity = 9.8f;      // m/s²
        public float SeaLevel = 0f;
        public List<Plate> Plates = new List<Plate>();
        public Crust WorldCrust;                 // 合并后的全局地壳
        public float[] Displacement;             // 均衡位移（m，相对基准面）
        public float[] Elevation;                // 表面高度（m，相对海平面，≥-海深）

        public float[] TopPlateMap;              // 每全局顶点 → 顶层板块 id
        public int[] PlateCount;                 // 每全局顶点 → 覆盖的板块数（M3-2）
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

        // ── 初始地壳生成（CrustGenerator.js）──

        /// <summary>
        /// 初始地壳：按海拔模板生成各物质厚度。
        /// 简化：现代地球地形分布（海洋 60% / 大陆 40%），
        /// 高度排名用球面低频噪声（空间连续大陆），排序后映射 hypsography 分布。
        ///
        /// ⚠️ 2026-08-02 修复：原版用 rng.NextDouble() 逐格随机排名 → 噪点图
        /// （相邻格完全无关，无大陆块）。tectonics.js 的 height_ranks 来自低频
        /// 噪声场（空间连续），这里用 3D Simplex 低频（波长 ~5000km）还原。
        /// </summary>
        public void GenerateInitialCrust(int seed, float oceanFraction = 0.6f)
        {
            int n = GlobalGrid.VertexCount;
            Seed = seed;

            // 1. 每格高度排名：球面低频噪声（多路独立求和，块形完整）
            //    对应 JS World 初始化的 height_ranks（噪声驱动，非逐格随机）
            // ⚠️ 2026-08-10 大陆块数参数化：坐标用单位球（不乘半径），波长数 = 频率 × 球面周长 2π。
            // 令 freq1 = NumContinents/2π → 球面恰 NumContinents 个波长（大陆块尺度）。
            //    （此前固定 0.00016@6371km 等价于 6.4 块；此改动消除噪声链路的 6371 标度魔数）
            float freq1 = NumContinents / (2f * Mathf.Pi);   // 主尺度：N 个波长/球
            var noise1 = new FastNoiseLite();
            noise1.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            noise1.Frequency = freq1;
            noise1.Seed = seed;
            var noise2 = new FastNoiseLite();
            noise2.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            noise2.Frequency = freq1 * 1.625f;              // 次尺度：保持 6250/3850 波长比例（细碎纹理）
            noise2.Seed = seed + 100;

            var ranks = new float[n];
            float minR = float.MaxValue, maxR = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = GlobalGrid.Vertices[i];   // 单位球坐标（频率已按 N/2π 标定，2026-08-10）
                float v = 0.53f * noise1.GetNoise3D(p.X, p.Y, p.Z)
                        + 0.47f * noise2.GetNoise3D(p.X, p.Y, p.Z);
                ranks[i] = v;
                if (v < minR) minR = v;
                if (v > maxR) maxR = v;
            }
            for (int i = 0; i < n; i++)
                ranks[i] = (ranks[i] - minR) / Mathf.Max(maxR - minR, 1e-6f); // 0..1

            var sortedIds = new int[n];
            for (int i = 0; i < n; i++) sortedIds[i] = i;
            Array.Sort(sortedIds, (a, b) => ranks[a].CompareTo(ranks[b]));

            // 2. 按 hypsography 采样真实海拔（正态分布：海洋 -4019±1113，大陆 +797±1169）
            var rng = new Random(seed + 999);   // 独立于噪声排名，仅 hypsography 采样用
            var elevations = new float[n];
            for (int i = 0; i < n; i++)
            {
                bool ocean = rng.NextDouble() < oceanFraction;
                elevations[i] = ocean
                    ? (float)Normal(rng, -4019, 1113)
                    : (float)Normal(rng, 797, 1169);
            }
            Array.Sort(elevations);
            // 排名低的格 → 低海拔
            // ⚠️ 2026-08-02 修复：原代码 elevations[sortedIds[i]] = elevations[i] 是原地赋值，
            //   当 sortedIds[j]==i (j<i) 时 elevations[i] 已被覆盖 → 数据错乱
            //   （实测 felsic 仅 238/2562 格=9%，大陆几乎没生成，全部挤成洋壳）。
            //   必须用排序后的副本赋值。
            var sortedElevations = (float[])elevations.Clone();
            for (int i = 0; i < n; i++)
                elevations[sortedIds[i]] = sortedElevations[i];

            // 3. 海拔 → 各物质厚度（modern_earth_attribute_height_maps 移植）
            var crust = WorldCrust;
            // 诊断：洋壳厚度分布（检查是否平坦）
            float maficMin = float.MaxValue, maficMax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float d = elevations[i];
                Vector3 p = GlobalGrid.Vertices[i];   // 单位球坐标（age 噪声用，频率同 N/2π 标定）
                // 洋壳：mafic_volcanic 在 -1500..-950 之间线性
                // ⚠️ 2026-08-02 观察：深海全 7100m 恒定 → isostasy 位移 -455~+427m（真实洋底 -2000~-6000m）。
                //   洋壳厚度需"距中脊越远越厚"梯度，此处先统计确认。
                float mv = FieldOps.Lerp(new[] { -1500f, -950f }, new[] { 2890f * 7100f, 0f }, d);
                crust.MaficVolcanic[i] = mv;
                if (d < -950f) { if (mv < maficMin) maficMin = mv; if (mv > maficMax) maficMax = mv; }
                // 陆壳：felsic 在 -1500..8848 之间线性（85% 深成 + 15% 火山）
                crust.FelsicPlutonic[i] = FieldOps.Lerp(
                    new[] { -1500f, -950f, 840f, 8848f },
                    new[] { 0f, 2700f * 0.85f * 28300f, 2700f * 0.85f * 36900f, 2700f * 0.85f * 70000f }, d);
                crust.FelsicVolcanic[i] = FieldOps.Lerp(
                    new[] { -1500f, -950f, 840f, 8848f },
                    new[] { 0f, 2700f * 0.15f * 28300f, 2700f * 0.15f * 36900f, 2700f * 0.15f * 70000f }, d);
                // 沉积物：深海 -3200..-1500
                crust.Sediment[i] = FieldOps.Lerp(
                    new[] { -3200f, -1500f }, new[] { 0f, 2500f * 5f }, d);
                // 年龄：按海拔（老地壳海拔高）+ 洋壳年龄噪声梯度
                // ⚠️ 2026-08-02：原版 age 映射海洋 -4000~-2000 全 0（年轻洋壳），
                //   无裂谷时 300My 后所有洋壳老化到密度上限 3300 → 位移全部相同
                //   （平坦 -455m）→ 海平面二分失效 → "全是海洋"。
                //   改为：洋壳 age = 0~200My 噪声梯度（真实洋壳年龄不均匀），
                //   陆壳保持 1000My。这样即使无裂谷，模拟 150My 后洋壳密度仍有梯度。
                float landAge = FieldOps.Lerp(
                    new[] { -11000f, -5000f, -4000f, -2000f, -1500f, -900f, 840f },
                    new[] { 250f, 100f, 0f, 0f, 100f, 1000f, 1000f }, d);
                if (d < 840f)
                {
                    // 海洋：0~200My 球面噪声梯度（低频）
                    float t = 0.5f * (noise1.GetNoise3D(p.X, p.Y, p.Z) * 0.5f + 0.5f)
                            + 0.5f * (noise2.GetNoise3D(p.X, p.Y, p.Z) * 0.5f + 0.5f);
                    landAge = Mathf.Clamp(t, 0f, 1f) * 200f;
                }
                crust.Age[i] = landAge * Units.MEGAYEAR;
            }
            if (maficMin < float.MaxValue)
                GD.Print($"[Tectonics] 洋壳 mafic_volcanic 厚度: {maficMin / 2890f:F0}~{maficMax / 2890f:F0} m（深海区）");

            // 诊断：质量统计（felsic 大陆 / mafic 洋壳 是否正常）
            double felsicMass = 0, maficMass = 0;
            int felsicCells = 0, maficCells = 0;
            for (int i = 0; i < n; i++)
            {
                double f = crust.FelsicPlutonic[i] + crust.FelsicVolcanic[i];
                if (f > 1e6) { felsicMass += f; felsicCells++; }
                if (crust.MaficVolcanic[i] > 1e6) { maficMass += crust.MaficVolcanic[i]; maficCells++; }
            }
            GD.Print($"[Tectonics] felsic: {felsicCells}格 总质量{felsicMass / 1e9:F0}Gt | mafic: {maficCells}格 总质量{maficMass / 1e9:F0}Gt");

            // 诊断：初始位移直方图（确认海陆分离度）
            ComputeDisplacement();
            int[] bins = new int[7];   // <-3k, -3~-1k, -1~0, 0~1k, 1~3k, 3~6k, >6k
            foreach (var d in Displacement)
            {
                if (d < -3000) bins[0]++;
                else if (d < -1000) bins[1]++;
                else if (d < 0) bins[2]++;
                else if (d < 1000) bins[3]++;
                else if (d < 3000) bins[4]++;
                else if (d < 6000) bins[5]++;
                else bins[6]++;
            }
            GD.Print($"[Tectonics] init disp hist(<-3k,-3~-1k,-1~0,0~1k,1~3k,3~6k,>6k): {string.Join(",", bins)}");

            GD.Print($"[Tectonics] initial crust: minDisp={FieldOps.Min(Displacement):F0} maxDisp={FieldOps.Max(Displacement):F0} m");
        }

        private static double Normal(Random rng, double mean, double stddev)
        {
            // Box-Muller
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            return mean + stddev * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static int ArgMax(float[] f)
        {
            int best = 0;
            for (int i = 1; i < f.Length; i++) if (f[i] > f[best]) best = i;
            return best;
        }

        // ── 初始板块分割（简化：球面均匀种子 + 最近邻）──

        /// <summary>
        /// 用 N 个球面均匀分布种子做 Voronoi 分割，每板继承全局地壳。
        /// 替代 JS 的软流圈速度图像分割（Tectonophysics.guess_plate_map）。
        /// </summary>
        public void SplitIntoPlates(int numPlates, int seed)
        {
            var rng = new Random(seed);
            int n = GlobalGrid.VertexCount;
            Plates.Clear();

            // 球面均匀种子（斐波那契球）
            var seeds = new Vector3[numPlates];
            for (int i = 0; i < numPlates; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / numPlates;
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float theta = (float)(Math.PI * (1 + Math.Sqrt(5)) * i);
                seeds[i] = new Vector3(Mathf.Cos(theta) * r, y, Mathf.Sin(theta) * r);
            }

            // 每格 → 最近种子板块
            var plateId = new int[n];
            for (int i = 0; i < n; i++)
            {
                int best = 0;
                float bestD = float.MaxValue;
                for (int p = 0; p < numPlates; p++)
                {
                    float d = (GlobalGrid.Vertices[i] - seeds[p]).LengthSquared();
                    if (d < bestD) { bestD = d; best = p; }
                }
                plateId[i] = best;
            }

            // 每板：mask + crust（从全局复制本板区域）
            InitialPlateCount = numPlates;   // 记录初始板块数（重分割时固定请求数，见 ResetPlates）
            for (int p = 0; p < numPlates; p++)
            {
                var mask = new byte[n];
                var crust = new Crust(GlobalGrid);
                for (int i = 0; i < n; i++)
                {
                    if (plateId[i] == p)
                    {
                        mask[i] = 1;
                        var pools = WorldCrust.AllPools();
                        var cpools = crust.AllPools();
                        for (int k = 0; k < 8; k++) cpools[k][i] = pools[k][i];
                    }
                }
                // 速度由 Move 时按浮力/边界法线实时计算（Schellart 模型），无需预置
                Plates.Add(new Plate(p, GlobalGrid, crust, mask));
            }

            GD.Print($"[Tectonics] split into {numPlates} plates");
        }

        // ── 主循环 ──

        /// <summary>跑 N 百万年。每步：老化 → 移动 → 合并 → 计算位移 → 按体积重解海平面。</summary>
        public void Run(float megayears, float stepMy) => RunWithProgress(megayears, stepMy, null);

        /// <summary>
        /// 跑 N 百万年（带进度回调）。onProgress 在【调用线程】被调用（模拟是纯数据，
        /// UI 场景下由后台线程驱动，回调只更新 volatile 字段，主线程 _Process 读）。
        /// </summary>
        public void RunWithProgress(float megayears, float stepMy, Action<float> onProgress)
        {
            ComputeDisplacement();         // 先算初始位移场
            InitializeOceanVolume(2000f * OceanScale);  // M3-5：水量守恒常量（原版 average_ocean_depth；用户可调水量）
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
                    GD.Print($"[Tectonics] hist(深<-4k, 海-4~0, 低0~1k, 中1~3k, 高3~6k, 峰>6k): " +
                             $"{bins[0]},{bins[1]},{bins[2]},{bins[3]},{bins[4]},{bins[5]}");
                    long totalMs = swMove.ElapsedMilliseconds + swMerge.ElapsedMilliseconds
                        + swRift.ElapsedMilliseconds + swErode.ElapsedMilliseconds + swOther.ElapsedMilliseconds;
                    GD.Print($"[Tectonics] 耗时 move={swMove.ElapsedMilliseconds}ms merge={swMerge.ElapsedMilliseconds}ms " +
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
                    GD.Print($"[Tectonics] step {s}/{steps} ({s * stepMy:F0}My) plates={Plates.Count} " +
                             $"disp[{FieldOps.Min(Displacement):F0},{FieldOps.Max(Displacement):F0}]m land={LandFractionAboveSea() * 100:F1}%");
                onProgress?.Invoke((s + 1f) / steps);
            }
            SolveSeaLevelByVolume();
        }

        /// <summary>
        /// 合并各板块到全局（Lithosphere.merge_plates_to_master 移植，M2 简化）。
        /// 规则：密度最小的板在顶层（浮力大的覆盖），felsic 类守恒叠加（碰撞增厚），
        /// mafic/age 由顶层板决定。同时维护 PlateCount（每格覆盖板块数，裂谷/俯冲用）。
        /// 简化俯冲（2026-08-02）：叠加后每格地壳总厚度设上限（70km），超出部分
        /// 按比例削减——模拟俯冲带走多余地壳。无此限制碰撞处地壳无限堆叠
        /// （maxDisp 6923→11790m，land 40%→9.5% 崩盘）。
        /// </summary>
        public void MergePlatesToMaster()
        {
            int n = GlobalGrid.VertexCount;
            WorldCrust.Reset();
            Array.Fill(TopPlateMap, -1f);
            Array.Clear(PlateCount, 0, n);

            // ⚠️ GC 优化 C3（2026-08-02）：masterDensity 字段复用（每 merge 160KB × 300 步）
            if (_mergeMasterDensity == null) _mergeMasterDensity = new float[n];
            var masterDensity = _mergeMasterDensity;
            Array.Fill(masterDensity, float.MaxValue);

            // ⚠️ GC 优化 C2（2026-08-02）：原每步 3 次 new Crust（8 数组 × 40962 ≈ 1.3MB）——
            //   300 步 × 3 = 1.2GB 分配 → 老年代 GC 压力随时间涨（Merge 21.6→31.7s/段）。
            //   复用字段（ResampleCrustToGlobal 每次全量覆盖，无残留）。
            if (_mergeGlobalizedCrust == null) _mergeGlobalizedCrust = new Crust(GlobalGrid);
            var globalizedCrust = _mergeGlobalizedCrust;

            foreach (var plate in Plates)
            {
                // 重采样板 crust 到全局
                plate.ResampleCrustToGlobal(globalizedCrust);
                // 板 mask 全局化（局部 mask → 全局）
                if (_mergeGlobalMask == null) _mergeGlobalMask = new byte[n];   // GC 优化 C：复用
                var globalMask = _mergeGlobalMask;
                for (int i = 0; i < n; i++)
                    globalMask[i] = plate.Mask[plate.LocalIdsOfGlobalCells[i]];

                // ⚠️ GC 优化 C4（2026-08-02）：plateDensity 系列复用（GetTotalMass/Thickness/
                //   Density 原每 plate 各 new 160KB → 每步 7×3 = 3.4MB × 300 步 ≈ 1GB）
                if (_mergeMassBuf == null) _mergeMassBuf = new float[n];
                if (_mergeThickBuf == null) _mergeThickBuf = new float[n];
                if (_mergeDensityBuf == null) _mergeDensityBuf = new float[n];
                var plateDensity = plate.Crust.GetDensity(
                    plate.Crust.GetTotalMass(_mergeMassBuf),
                    plate.Crust.GetThickness(Material, _mergeThickBuf),
                    Material.MaficVolcanicMin, _mergeDensityBuf);

                // ⚠️ GC 优化 C（2026-08-02）：ConservedPools 每次 new 5 元素引用数组——
                //   内循环 40962 次 × 2 调用 = 8 万次小分配/merge × 3 merge/步 = GC 风暴。
                //   移出循环（每 plate 一次）。
                var c1 = WorldCrust.ConservedPools();
                var c2 = globalizedCrust.ConservedPools();
                for (int i = 0; i < n; i++)
                {
                    if (globalMask[i] == 0) continue;
                    PlateCount[i]++;   // M3-2：覆盖板块数 +1
                    bool onTop = plateDensity[plate.LocalIdsOfGlobalCells[i]] < masterDensity[i];
                    // 守恒组叠加（felsic 类，碰撞增厚）
                    for (int k = 0; k < 5; k++) c1[k][i] += c2[k][i];
                    if (onTop)
                    {
                        masterDensity[i] = plateDensity[plate.LocalIdsOfGlobalCells[i]];
                        TopPlateMap[i] = plate.Id;
                        WorldCrust.MaficVolcanic[i] = globalizedCrust.MaficVolcanic[i];
                        WorldCrust.MaficPlutonic[i] = globalizedCrust.MaficPlutonic[i];
                        WorldCrust.Age[i] = globalizedCrust.Age[i];
                    }
                }
            }

            // 简化俯冲：每格地壳厚度上限（70km，地球大陆根 ~70km），超出削减。
            // ⚠️ GetThickness 返回米（kg/m² ÷ kg/m³ = m）——上限必须 70000 而非 70，
            //   否则全图地壳被削减到 70m（disp[4,11] 崩盘，2026-08-02 修复）。
            float maxThickness = 70000f;   // 70km，单位米
            var thickness = WorldCrust.GetThickness(Material);
            for (int i = 0; i < n; i++)
            {
                if (thickness[i] <= maxThickness) continue;
                float scale = maxThickness / thickness[i];
                var pools = WorldCrust.AllPools();
                for (int k = 0; k < 8; k++) pools[k][i] *= scale;
            }
        }

        /// <summary>
        /// 裂谷（Lithosphere.update_rifting 移植，M3-2）：离散边界生成新洋壳。
        ///
        /// 原理：板块分离时露出的空当（plate_count==0）或单板边缘（count==1 且顶层=本板）
        /// 就是洋中脊位置。把这些格判为"可裂谷"，重采样到局部后：
        ///   1. 腐蚀 1 层（will_stay_riftable：裂谷区内部，避免边缘误判）
        ///   2. 与板块 mask 外扩 1 层（margin）取交集 → 真正的裂谷格
        ///   3. 这些格填入新洋壳（rifting_crust：mafic_volcanic 7100m，白垩纪洋壳）
        ///   4. 板块 mask 扩展到这些格
        ///
        /// 新洋壳 age=0（年轻），密度低（2890）→ 洋中脊隆起 → 洋底有地形。
        /// 这是洋壳"再循环"的来源：裂谷生成新洋壳 + 俯冲回收老洋壳。
        /// </summary>
        public void UpdateRifting()
        {
            int n = GlobalGrid.VertexCount;
            const float RiftMafic = 7100f * 2890f;   // mafic_volcanic 质量面密度（kg/m²）

            // 全局：is_riftable = (count==0) || (count==1 && top==i)
            // ⚠️ 2026-08-02：并行版已回滚——n=64 实测更慢（82s/段 vs 33s）+ 模拟结果改变
            //   （湖 62→1484）——并行体与串行语义不等价（FieldOps 内部状态/竞态待查），
            //   性能优先方案改用 GC 复用（C3/C4/Erode）。
            foreach (var plate in Plates)
            {
                // 全局可裂谷掩码（对每块板独立判断）
                var globalRiftable = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    if (PlateCount[i] == 0) globalRiftable[i] = 1;
                    else if (PlateCount[i] == 1 && (int)TopPlateMap[i] == plate.Id) globalRiftable[i] = 1;
                }

                // 重采样到局部：全局格 → 板局部最近顶点
                var localRiftable = new byte[LocalGridCount];
                for (int i = 0; i < n; i++)
                    if (globalRiftable[i] == 1)
                        localRiftable[plate.LocalIdsOfGlobalCells[i]] = 1;

                // 腐蚀 1 层（will_stay_riftable：裂谷区内部）
                var willStay = FieldOps.Erode(GlobalGrid, localRiftable, 1);

                // 板块 mask 外扩 1 层（just_outside_border）
                var justOutside = FieldOps.Margin(GlobalGrid, plate.Mask, 1);

                // 裂谷格 = will_stay ∩ just_outside_border
                var isRifting = new byte[LocalGridCount];
                for (int i = 0; i < LocalGridCount; i++)
                    isRifting[i] = (willStay[i] == 1 && justOutside[i] == 1) ? (byte)1 : (byte)0;

                // 填入新洋壳 + 扩展 mask
                for (int i = 0; i < LocalGridCount; i++)
                {
                    if (isRifting[i] == 0) continue;
                    plate.Mask[i] = 1;
                    // ⚠️ 2026-08-03 矿藏模拟化：裂谷新洋壳 = 洋中脊热液活动（铜硫化物矿化）
                    int gi = plate.GlobalIdsOfLocalCells[i];
                    if (gi >= 0 && gi < n) MineralHydro[gi] += 1f;
                    plate.Crust.MaficVolcanic[i] = RiftMafic;
                    plate.Crust.MaficPlutonic[i] = 0;
                    plate.Crust.Sediment[i] = 0;
                    plate.Crust.Sedimentary[i] = 0;
                    plate.Crust.Metamorphic[i] = 0;
                    plate.Crust.FelsicPlutonic[i] = 0;
                    plate.Crust.FelsicVolcanic[i] = 0;
                    plate.Crust.Age[i] = 0;   // 新洋壳年轻
                }
            }
        }

        /// <summary>
        /// 俯冲（Lithosphere.update_subducted 移植，M3-3）：
        /// 被压在下面的板块（count>1 且非顶层）在俯冲带处消减：
        ///   1. 被覆盖格：sediment/sedimentary/felsic → 全部转 metamorphic（深埋变质）
        ///   2. 密度 > 地幔 的板内边界格：从板块 mask 移除（消减回地幔）
        ///   3. 移除格的守恒质量 → 全局 Accretion（增生楔），由 ApplyAccretion
        ///      加到顶层板（俯冲上盘的增生造山带）。
        /// ⚠️ 2026-08-02 修复：旧版把消减质量加回【本板】再 mask=0 → 质量凭空消失
        ///   （全球 felsic 流失 → 600My 后地形磨平到 641m）。原版写入全局 accretion。
        /// </summary>
        public void UpdateSubducted()
        {
            int n = GlobalGrid.VertexCount;
            Accretion.Reset();
            foreach (var plate in Plates)
            {
                // 全局：被俯冲 = count>1 且 非顶层
                var globalSubducted = new byte[n];
                for (int i = 0; i < n; i++)
                {
                    if (PlateCount[i] > 1 && (int)TopPlateMap[i] != plate.Id)
                        globalSubducted[i] = 1;
                }
                // 重采样到局部
                var localSubducted = new byte[LocalGridCount];
                for (int i = 0; i < n; i++)
                    if (globalSubducted[i] == 1)
                        localSubducted[plate.LocalIdsOfGlobalCells[i]] = 1;

                // 深埋变质：被覆盖格的 felsic 全转 metamorphic
                for (int i = 0; i < LocalGridCount; i++)
                {
                    if (localSubducted[i] == 0) continue;
                    // ⚠️ 2026-08-03 矿藏模拟化：俯冲带深埋 = 变质矿化（宝石/铁前体）+ 岩浆弧热液
                    int gi = plate.GlobalIdsOfLocalCells[i];
                    if (gi >= 0 && gi < n)
                    {
                        MineralMeta[gi] += 1f;
                        MineralHydro[gi] += 0.5f;   // 岩浆弧热液（俯冲上盘，金/铜）
                    }
                    plate.Crust.Metamorphic[i] += plate.Crust.Sediment[i] + plate.Crust.Sedimentary[i]
                        + plate.Crust.FelsicPlutonic[i] + plate.Crust.FelsicVolcanic[i];
                    plate.Crust.Sediment[i] = 0;
                    plate.Crust.Sedimentary[i] = 0;
                    plate.Crust.FelsicPlutonic[i] = 0;
                    plate.Crust.FelsicVolcanic[i] = 0;
                }

                // 消减：腐蚀1层 & mask 内扩1层 & 密度>mantle → 从板移除（回地幔）
                var willStay = FieldOps.Erode(GlobalGrid, localSubducted, 1);
                var justInside = FieldOps.Margin(GlobalGrid, plate.Mask, 1);   // 板内边界层
                var density = plate.Crust.GetDensity(
                    plate.Crust.GetTotalMass(), plate.Crust.GetThickness(Material), Material.MaficVolcanicMin);
                for (int i = 0; i < LocalGridCount; i++)
                {
                    if (willStay[i] == 1 && justInside[i] == 1 && density[i] > Material.Mantle)
                    {
                        // 消减：守恒质量 → 全局增生楔（felsic 85/15）
                        float conserved = plate.Crust.Sedimentary[i] + plate.Crust.Metamorphic[i]
                                        + plate.Crust.FelsicPlutonic[i] + plate.Crust.FelsicVolcanic[i];
                        // 全局格 id（本板局部格 → 全局格）
                        int gi = plate.GlobalIdsOfLocalCells[i];
                        Accretion.FelsicPlutonic[gi] += conserved * 0.85f;
                        Accretion.FelsicVolcanic[gi] += conserved * 0.15f;
                        // 本板该格清空（质量已转移到 accretion）
                        plate.Crust.Sedimentary[i] = 0;
                        plate.Crust.Metamorphic[i] = 0;
                        plate.Crust.FelsicPlutonic[i] = 0;
                        plate.Crust.FelsicVolcanic[i] = 0;
                        plate.Mask[i] = 0;
                    }
                }
            }
        }

        /// <summary>
        /// 应用增生楔：把 Accretion 加到顶层板（俯冲上盘增生造山带）。
        /// 对应原版 integrate_deltas 对 accretion 的处理（按 top_plate_map 分派）。
        /// 在 Merge 之后调用（需要 TopPlateMap）。
        /// </summary>
        public void ApplyAccretion()
        {
            int n = GlobalGrid.VertexCount;
            var aPools = Accretion.AllPools();
            for (int p = 0; p < Plates.Count; p++)
            {
                var plate = Plates[p];
                var pPools = plate.Crust.AllPools();
                for (int i = 0; i < n; i++)
                {
                    if ((int)TopPlateMap[i] != p) continue;
                    int li = plate.LocalIdsOfGlobalCells[i];
                    // ⚠️ 2026-08-03 矿藏模拟化：增生楔 = 造山带热液（金/铜）
                    if (aPools[1][i] > 0f || aPools[2][i] > 0f)
                        MineralHydro[i] += 1f;
                    for (int k = 0; k < 8; k++)
                        pPools[k][li] += aPools[k][i];
                }
            }
            Accretion.Reset();
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

        /// <summary>
        /// 碰撞合并（2026-08-16 方案 B 前提）：板块接触边界够长 → 缝合（碰撞合并）。
        /// 真实地球：印度-欧亚碰撞缝合 → 大陆拼大。旧模拟缺此机制（plates 恒 7，
        /// 大陆永远拼不大 → 超大陆永不形成 → 裂解永不触发）。
        /// 用 MergePlatesToMaster 后的 TopPlateMap 检测相邻板块接触长度。
        /// </summary>
        public void TryMergeCollidingPlates()
        {
            // ⚠️ 2026-08-16 v2：缝合下限——真实地球 8 大板块 + 洋中脊持续分裂维持数量。
            //   实测无下限时全部吞并成 1 块（100% 超大陆，失真）；下限 5 保留多板块格局，
            //   与超大陆裂解（→7）形成 5-8 动态平衡。
            if (Plates.Count <= 5) return;
            int n = GlobalGrid.VertexCount;
            int minContact = Math.Max(8, n / 50);   // ≥2% 全球接触才缝合
            var contact = new System.Collections.Generic.Dictionary<(int, int), int>();
            for (int i = 0; i < n; i++)
            {
                int a = (int)TopPlateMap[i];
                if (a < 0) continue;
                foreach (var nb in GlobalGrid.Neighbors[i])
                {
                    int b = (int)TopPlateMap[nb];
                    if (b < 0 || b == a) continue;
                    var key = a < b ? (a, b) : (b, a);
                    contact[key] = contact.GetValueOrDefault(key) + 1;
                }
            }
            int bestA = -1, bestB = -1, bestC = minContact;
            foreach (var kv in contact)
                if (kv.Value > bestC) { bestC = kv.Value; bestA = kv.Key.Item1; bestB = kv.Key.Item2; }
            if (bestA < 0) return;
            MergeTwoPlates(bestA, bestB);
        }

        /// <summary>合并板块 b 进 a（a 坐标系为准；物质从 WorldCrust 全局视图取——先 MergePlatesToMaster）。</summary>
        private void MergeTwoPlates(int idA, int idB)
        {
            int n = GlobalGrid.VertexCount;
            Plate pa = null, pb = null;
            for (int p = 0; p < Plates.Count; p++)
            {
                if (Plates[p].Id == idA) pa = Plates[p];
                if (Plates[p].Id == idB) pb = Plates[p];
            }
            if (pa == null || pb == null) return;

            var merged = new Plate(idA, GlobalGrid, new Crust(GlobalGrid), new byte[n]);
            Array.Copy(pa.LocalToGlobal, merged.LocalToGlobal, 9);
            Array.Copy(pa.GlobalToLocal, merged.GlobalToLocal, 9);
            Array.Copy(pa.LocalIdsOfGlobalCells, merged.LocalIdsOfGlobalCells, n);
            Array.Copy(pa.GlobalIdsOfLocalCells, merged.GlobalIdsOfLocalCells, n);
            Array.Copy(pa.Velocity, merged.Velocity, n);
            var mPools = merged.Crust.AllPools();
            var gPools = WorldCrust.AllPools();
            for (int l = 0; l < n; l++)
            {
                int g = pa.GlobalIdsOfLocalCells[l];
                if (TopPlateMap[g] == idA || TopPlateMap[g] == idB)
                {
                    merged.Mask[l] = 1;
                    for (int q = 0; q < 8; q++) mPools[q][l] = gPools[q][g];
                }
            }
            Plates.Remove(pb);
            for (int p = 0; p < Plates.Count; p++)
                if (Plates[p].Id == idA) { Plates[p] = merged; break; }
            GD.Print($"[Tectonics] 碰撞缝合: 板块{idA}+{idB} → {idA}（板块数→{Plates.Count}）");
        }

        /// <summary>
        /// 方案 B（2026-08-16 用户拍板）：超大陆【继承式裂解】——替代旧固定周期全局洗牌。
        /// 最大板块占全球 &gt;25% 时视为超大陆，沿内部最远点种子裂成 2-3 块；其他板块不动
        /// （位置/演化历史继承），板块漂移碰撞自然聚合拼大 → 大陆大小有自然差异。
        /// 无超大陆返回 false（继续聚合，不洗牌）。
        /// </summary>
        public bool TrySplitSupercontinent()
        {
            int n = GlobalGrid.VertexCount;
            int bestIdx = -1, bestCount = 0;
            for (int p = 0; p < Plates.Count; p++)
            {
                int c = Plates[p].TileCount;
                if (c > bestCount) { bestCount = c; bestIdx = p; }
            }
            float frac = (float)bestCount / n;
            if (bestIdx < 0 || frac < 0.25f) return false;   // 无超大陆 → 继续聚合
            int splitK = frac > 0.4f ? 3 : 2;

            var big = Plates[bestIdx];
            var cells = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++) if (big.Mask[i] == 1) cells.Add(i);
            if (cells.Count < splitK * 2) return false;

            // 最远点种子（球面分散）：种子0 = 离质心最远；其后 = 离已选种子最远
            Vector3 centroid = Vector3.Zero;
            foreach (var c in cells) centroid += GlobalGrid.Vertices[c];
            centroid = centroid.Normalized();
            var seeds = new System.Collections.Generic.List<int>();
            int bestC = cells[0]; float bestD = -1f;
            foreach (var c in cells)
            {
                float d = 1f - GlobalGrid.Vertices[c].Dot(centroid);
                if (d > bestD) { bestD = d; bestC = c; }
            }
            seeds.Add(bestC);
            while (seeds.Count < splitK)
            {
                int bestS = cells[0]; float bestSD = -1f;
                foreach (var c in cells)
                {
                    float minD = float.MaxValue;
                    foreach (var s in seeds)
                    {
                        float d = 1f - GlobalGrid.Vertices[c].Dot(GlobalGrid.Vertices[s]);
                        if (d < minD) minD = d;
                    }
                    if (minD > bestSD) { bestSD = minD; bestS = c; }
                }
                seeds.Add(bestS);
            }

            // 每格归最近种子 → 裂片 mask
            var newMasks = new byte[splitK][];
            for (int k = 0; k < splitK; k++) newMasks[k] = new byte[n];
            foreach (var c in cells)
            {
                int bestS = 0; float bestSD = float.MaxValue;
                for (int k = 0; k < splitK; k++)
                {
                    float d = 1f - GlobalGrid.Vertices[c].Dot(GlobalGrid.Vertices[seeds[k]]);
                    if (d < bestSD) { bestSD = d; bestS = k; }
                }
                newMasks[bestS][c] = 1;
            }

            // 重建 Plates：其他板块保留（演化历史继承），超大陆裂片继承 big 的 crust/坐标系
            var newPlates = new System.Collections.Generic.List<Plate>();
            for (int p = 0; p < Plates.Count; p++)
                if (p != bestIdx) newPlates.Add(Plates[p]);
            int nextId = 0;
            foreach (var pl in Plates) nextId = Math.Max(nextId, pl.Id + 1);
            var bigPools = big.Crust.AllPools();
            for (int k = 0; k < splitK; k++)
            {
                var crust = new Crust(GlobalGrid);
                var cpools = crust.AllPools();
                for (int i = 0; i < n; i++)
                    if (newMasks[k][i] == 1)
                        for (int q = 0; q < 8; q++) cpools[q][i] = bigPools[q][i];
                var plate = new Plate(nextId + k, GlobalGrid, crust, newMasks[k]);
                // 继承 big 的坐标系/速度场（裂片原地分裂，不跳变）
                Array.Copy(big.LocalToGlobal, plate.LocalToGlobal, 9);
                Array.Copy(big.GlobalToLocal, plate.GlobalToLocal, 9);
                Array.Copy(big.LocalIdsOfGlobalCells, plate.LocalIdsOfGlobalCells, n);
                Array.Copy(big.GlobalIdsOfLocalCells, plate.GlobalIdsOfLocalCells, n);
                Array.Copy(big.Velocity, plate.Velocity, n);
                newPlates.Add(plate);
            }
            Plates = newPlates;
            GD.Print($"[Tectonics] 超大陆裂解: 占比{frac:P0} → {splitK}块（板块数→{Plates.Count}）");
            return true;
        }

        public void ResetPlates(int numPlates, int minSegmentSize = 200)
        {
            int n = GlobalGrid.VertexCount;

            // 1. 软流圈压力场：pressure = -buoyancy，然后拉普拉斯扩散（全场连续）
            //    对应 FluidMechanics.get_fluid_pressures：细网格 3 次 + 粗网格 30 次 + 细 3 次。
            //    两级扩散：粗网格（n=8，642 格）建立大尺度涡旋结构，细网格保留边界细节。
            //    ⚠️ 单级细网格扩散：5 次太毛糙（碎片）、30 次过度平滑（单一涡旋），
            //    都分割失败。粗细两级是原版设计（2026-08-02 验证）。
            var thickness = WorldCrust.GetThickness(Material);
            var mass = WorldCrust.GetTotalMass();
            var density = WorldCrust.GetDensity(mass, thickness, Material.MaficVolcanicMin);
            var buoyancy = WorldCrust.GetBuoyancy(density, Material, SurfaceGravity);

            var pressure = new float[n];
            for (int i = 0; i < n; i++) pressure[i] = -buoyancy[i];

            // 细网格 3 次（去局部噪声）
            pressure = FieldOps.Diffuse(GlobalGrid, pressure, 0.5f, 3);
            // 粗网格（n=8）30 次：建立大尺度结构
            var coarse = new SphereGrid(8);
            // 压力场重采样到粗网格（最近邻）
            var coarseIds = new int[coarse.VertexCount];
            for (int i = 0; i < coarse.VertexCount; i++)
                coarseIds[i] = GlobalGrid.NearestId(coarse.Vertices[i]);
            var coarsePressure = new float[coarse.VertexCount];
            for (int i = 0; i < coarse.VertexCount; i++)
                coarsePressure[i] = pressure[coarseIds[i]];
            coarsePressure = FieldOps.Diffuse(coarse, coarsePressure, 0.5f, 30);
            // 重采样回细网格
            for (int i = 0; i < n; i++)
                pressure[i] = coarsePressure[coarse.NearestId(GlobalGrid.Vertices[i])];
            // 细网格再 3 次（平滑重采样的台阶）
            pressure = FieldOps.Diffuse(GlobalGrid, pressure, 0.5f, 3);

            // 2. 软流圈速度 = 压力梯度（对应 get_fluid_velocities）
            var velocity = new Vector3[n];
            FieldOps.Gradient(GlobalGrid, pressure, velocity);

            // 3. 角速度 = v × pos（切向分量 = 旋转）
            var angular = new Vector3[n];
            Tectonophysics.CrossToAngularVelocity(velocity, GlobalGrid.Vertices, angular);

            // 诊断：角速度场统计
            float maxAng = 0, avgAng = 0, nzCount = 0, dirSpread = 0;
            Vector3 dirSum = Vector3.Zero;
            for (int i = 0; i < n; i++)
            {
                float len = angular[i].Length();
                if (len > 0) { avgAng += len; nzCount++; dirSum += angular[i] / len; }
                if (len > maxAng) maxAng = len;
            }
            if (nzCount > 0) { avgAng /= nzCount; dirSpread = dirSum.Length() / nzCount; }
            GD.Print($"[Tectonics] 角速度场: max={maxAng:E2} avg={avgAng:E2} 非零格={nzCount}/{n} 方向一致性={dirSpread:F2}(1=全同向)");

            // 3. 图像分割
            var plateMap = Tectonophysics.GuessPlateMap(GlobalGrid, angular, numPlates, minSegmentSize);

            // 4. 重建板块（继承 WorldCrust）
            // ⚠️ 2026-08-03 板块数收敛根因（控制变量实验 ResetDiag 结论）：
            //   "洋壳年龄饱和"不是主因（v1 只改年龄：饱和率 92%→57% 板块数轨迹不变；
            //   298My 未重置 94% 饱和的场反而分割出 6 块）——主因是**请求棘轮**：
            //   旧实现 ResetPlates(Mathf.Max(Plates.Count,4)) 请求数每周期只降不升，
            //   分割结果 ≤ 请求数 → 单调掉块（8→7→6→5→4→3，900My 固定 2-3 块）。
            //   请求恒 8 时 592My 侵蚀开仍分割出 7 块（ResetDiag 实验3）。
            //   修复：请求数固定 = 初始板块数（RunWithProgress 已改）。
            //   年龄重置（本函数）：超级大陆裂解 = 威尔逊旋回，纯洋壳格重新注入 0~200My
            //   年龄梯度（与初始地壳同结构低频噪声）——洋壳不再只老不新（原版无重置时
            //   600My 全饱和 → 海底过深过均匀），恢复密度差驱动的物理。大陆/造山带 age 不动
            //   （felsic 密度固定，age 只影响 mafic）。
            Plates.Clear();
            var plateIds = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < n; i++) if (plateMap[i] > 0) plateIds.Add(plateMap[i]);

            var ageNoise1 = new FastNoiseLite();
            ageNoise1.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            ageNoise1.Frequency = NumContinents / (2f * Mathf.Pi);   // 同 GenerateInitialCrust 主尺度（N 波长/球）
            ageNoise1.Seed = Seed;
            var ageNoise2 = new FastNoiseLite();
            ageNoise2.NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex;
            ageNoise2.Frequency = ageNoise1.Frequency * 1.625f;      // 次尺度（同比例）
            ageNoise2.Seed = Seed + 100;

            var wPools = WorldCrust.AllPools();
            foreach (int pid in plateIds)
            {
                var mask = new byte[n];
                var crust = new Crust(GlobalGrid);
                var cPools = crust.AllPools();
                for (int i = 0; i < n; i++)
                {
                    if (plateMap[i] != pid) continue;
                    mask[i] = 1;
                    for (int k = 0; k < 8; k++) cPools[k][i] = wPools[k][i];
                    // 洋壳年龄重置：mafic 为主的格（排除大陆核心 felsic≥1e7）
                    float felsic = wPools[3][i] + wPools[4][i];
                    if (wPools[5][i] > 1e6f && felsic < 1e7f)
                    {
                        Vector3 p = GlobalGrid.Vertices[i];   // 单位球坐标（噪声频率标定，同 N/2π）
                        float t = 0.5f * (ageNoise1.GetNoise3D(p.X, p.Y, p.Z) * 0.5f + 0.5f)
                                + 0.5f * (ageNoise2.GetNoise3D(p.X, p.Y, p.Z) * 0.5f + 0.5f);
                        crust.Age[i] = Mathf.Clamp(t, 0f, 1f) * 200f * Units.MEGAYEAR;
                    }
                }
                Plates.Add(new Plate(pid, GlobalGrid, crust, mask));
            }
            GD.Print($"[Tectonics] 超级大陆循环: 重新分割为 {Plates.Count} 块板（洋壳年龄已重置）");
        }

        /// <summary>
        /// 地表过程（M3，Crust.js model_* 移植）：侵蚀/风化/成岩/变质。
        /// 原版流程：calculate_deltas（算 delta 场）→ integrate_deltas（按 top_plate 应用回板块）。
        /// 返回 delta（净变化量），由 SyncWorldToPlates(delta) 写回各板块。
        ///
        /// ⚠️ 2026-08-02 修复：旧版把整个 WorldCrust 覆盖回板块 → 碰撞处叠加的
        ///   felsic（2×）被写回板块 → 下次 Merge 再叠加 → 每步 +1× 无限增长
        ///   （有侵蚀 maxDisp 暴涨到 10642m）。原版只写回 delta（净变化量）。
        /// </summary>
        public Crust ApplySurfaceProcesses(float stepMy)
        {
            int n = GlobalGrid.VertexCount;
            float seconds = stepMy * Units.MEGAYEAR;   // My → 秒

            // 表面高度（相对海平面，≥0）——⚠️ 2026-08-02 性能：字段复用（每步 new 1.3MB × 300 步）
            if (_erodeSurface == null) _erodeSurface = new float[n];
            var surfaceHeight = _erodeSurface;
            for (int i = 0; i < n; i++)
                surfaceHeight[i] = Mathf.Max(Displacement[i] - SeaLevel, 0f);

            // 侵蚀（先生成 delta）——⚠️ 2026-08-02 性能：字段复用 Crust（8 数组 × n）
            if (_erodeDeltaReusable == null) _erodeDeltaReusable = new Crust(GlobalGrid);
            var delta = _erodeDeltaReusable;
            delta.Reset();
            Crust.ModelErosion(GlobalGrid, surfaceHeight, seconds, Material, WorldCrust, delta, ErosionScale);
            Crust.ModelWeathering(GlobalGrid, surfaceHeight, seconds, Material, WorldCrust, delta, ErosionScale);
            Crust.ModelLithification(surfaceHeight, seconds, Material, WorldCrust, delta);
            Crust.ModelMetamorphosis(surfaceHeight, seconds, Material, WorldCrust, delta);

            // ⚠️ 2026-08-03 矿藏模拟化：沉积增厚/变质累积 = 矿化事件
            var dPools = delta.AllPools();   // 0=Sediment 1=Sedimentary 2=Metamorphic
            var dSed = dPools[1];
            var dMeta = dPools[2];
            for (int i = 0; i < n; i++)
            {
                if (dSed[i] > 0f) MineralSed[i] += 1f;    // 沉积盆地（煤/盐前体）
                if (dMeta[i] > 0f) MineralMeta[i] += 1f;  // 变质矿化（宝石/铁）
            }

            // 应用 delta（质量守恒：侵蚀/风化把 felsic 转 sediment，成岩/变质反向）
            Crust.AddDelta(WorldCrust, delta);
            return delta;
        }

        /// <summary>
        /// 把地表过程 delta 写回各板块局部 crust（原版 integrate_deltas 移植）。
        /// 只写回**净变化量**，不覆盖整块 crust——避免碰撞叠加物被写回后无限累积。
        /// </summary>
        public void SyncWorldToPlates(Crust delta)
        {
            int n = GlobalGrid.VertexCount;
            var dPools = delta.AllPools();
            for (int p = 0; p < Plates.Count; p++)
            {
                var plate = Plates[p];
                var pPools = plate.Crust.AllPools();
                for (int i = 0; i < n; i++)
                {
                    if ((int)TopPlateMap[i] != p) continue;
                    int li = plate.LocalIdsOfGlobalCells[i];   // 全局格 → 板局部格
                    for (int k = 0; k < 8; k++)
                        pPools[k][li] += dPools[k][i];
                }
            }
        }

        /// <summary>计算均衡位移（m）。displacement = thickness - thickness×density/mantle。</summary>
        public float[] ComputeDisplacement()
        {
            var thickness = WorldCrust.GetThickness(Material);
            var mass = WorldCrust.GetTotalMass();
            var density = WorldCrust.GetDensity(mass, thickness, Material.MaficVolcanicMin);
            Displacement = WorldCrust.GetIsostaticDisplacement(thickness, density, Material);
            return Displacement;
        }

        /// <summary>陆地占比（displacement > 0，相对基准面；海平面校准后更准）。</summary>
        public float LandFraction()
        {
            int land = 0;
            foreach (var d in Displacement) if (d > 0) land++;
            return (float)land / Displacement.Length;
        }

        /// <summary>
        /// 求解海平面（面积二分，仅初始化用）：二分找 sealevel 使陆地占比 ≈ 1-oceanFraction。
        /// 对应 JS Hydrology.solve_sealevel 的简化版（不约束水量）。
        /// </summary>
        public float SolveSeaLevel(float oceanFraction = 0.6f)
        {
            float lo = FieldOps.Min(Displacement), hi = FieldOps.Max(Displacement);
            float targetLand = 1f - oceanFraction;
            for (int iter = 0; iter < 40; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                int land = 0;
                foreach (var d in Displacement) if (d > mid) land++;
                float frac = (float)land / Displacement.Length;
                if (frac > targetLand) lo = mid; else hi = mid;
            }
            SeaLevel = (lo + hi) * 0.5f;
            return SeaLevel;
        }

        /// <summary>
        /// 初始化海洋体积（M3-5）：水量守恒常量 = average_ocean_depth 参数。
        /// 对应原版 Hydrosphere.js：average_ocean_depth = 2000（米，守恒量）。
        ///
        /// ⚠️ 2026-08-02 修复：不能从初始地形反推（洋壳 isostasy 位移仅 -177~+426m，
        ///   反推水量只有 ~40m → 海平面 81m → land 漂移到 58%）。原版水量是固定参数
        ///   2000m，海平面解在覆盖洋壳(+426m)、露出大陆(+5000m)的位置。
        /// </summary>
        public void InitializeOceanVolume(float averageOceanDepth = 2000f)
        {
            TotalOceanDepth = averageOceanDepth;
            GD.Print($"[Tectonics] 海洋体积初始化: 平均深度 {TotalOceanDepth:F0} m（水量守恒常量）");
        }

        /// <summary>
        /// 按海洋体积约束解海平面（M3-5，Hydrology.solve_sealevel 移植）：
        /// 二分找 sealevel 使平均海洋深度 = TotalOceanDepth（水量守恒）。
        /// land% 随地形演化自然浮动，不再锁定面积比例。
        /// </summary>
        public float SolveSeaLevelByVolume()
        {
            if (TotalOceanDepth <= 0) return SeaLevel;
            float lo = 0f;
            float hi = FieldOps.Max(Displacement) + TotalOceanDepth + 1f;
            for (int iter = 0; iter < 30; iter++)
            {
                float mid = (lo + hi) * 0.5f;
                double sum = 0;
                foreach (var d in Displacement)
                {
                    float depth = mid - d;
                    if (depth > 0) sum += depth;
                }
                float avg = (float)(sum / Displacement.Length);
                if (avg < TotalOceanDepth) lo = mid; else hi = mid;
            }
            SeaLevel = (lo + hi) * 0.5f;
            return SeaLevel;
        }

        /// <summary>海平面归一化陆地占比（disp > SeaLevel）。</summary>
        public float LandFractionAboveSea()
        {
            int land = 0;
            foreach (var d in Displacement) if (d > SeaLevel) land++;
            return (float)land / Displacement.Length;
        }
    }
}
