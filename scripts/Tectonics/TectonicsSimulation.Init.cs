using Godot;
using System;
using World.CivSim;   // DeterministicRandom（确定性随机工具——跨 .NET 运行时序列稳定，2026-08-19）
using World.MapGen;

namespace World.Tectonics
{
    // 职责：初始地壳生成（CrustGenerator 移植）与初始板块分割（球面种子 Voronoi）。
    //   GenerateInitialCrust —— 按 hypsography 模板生成各物质厚度 + 洋壳年龄梯度
    //   Normal / ArgMax     —— 初始地壳生成的私有辅助
    //   SplitIntoPlates     —— N 个球面均匀种子做最近邻分割，每板继承全局地壳
    public partial class TectonicsSimulation
    {
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
        public void GenerateInitialCrust(int seed, float oceanFraction = 0.6f, float radiusKm = MapArchive.DefaultRadiusKm)
        {
            int n = GlobalGrid.VertexCount;
            Seed = seed;
            // ⚠️ 2026-08-18 行星标度（用户拍板 A：初始地壳按 R——自然涌现非固定缩放）：
            //   hypsography 模板是地球标定（大陆 +797±1169m）——小星球地壳薄/分异弱，
            //   均衡山高按 sqrt(R/R⊕) 一阶标度（行星冷却/分异尺度律）——R=128km → ×0.142
            float hypsoscale = Mathf.Sqrt(radiusKm / MapArchive.DefaultRadiusKm);

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
            noise2.Frequency = freq1 * 3.5f;                 // 次尺度：山脊/山谷尺度中频细节（×5→×3.5 折中——碎渣 191 块→回 6 块级）
            noise2.Seed = seed + 100;

            var ranks = new float[n];
            float minR = float.MaxValue, maxR = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = GlobalGrid.Vertices[i];   // 单位球坐标（频率已按 N/2π 标定，2026-08-10）
                float v = 0.65f * noise1.GetNoise3D(p.X, p.Y, p.Z)
                        + 0.35f * noise2.GetNoise3D(p.X, p.Y, p.Z);   // ⚠️ 2026-08-18 0.47→0.35：中频细节保留但大陆块不碎
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
            var rng = new DeterministicRandom(seed + 999);   // 独立于噪声排名，仅 hypsography 采样用（2026-08-19：System.Random→DeterministicRandom，防 .NET 跨版本序列漂移）
            var elevations = new float[n];
            for (int i = 0; i < n; i++)
            {
                bool ocean = rng.NextDouble() < oceanFraction;
                // ⚠️ 2026-08-18：海洋采样不缩（物质厚度映射阈值 -1500/-950/840 是地球标定——
                //   海洋采样缩到 -571m 会落入陆壳映射区间 → 全陆）；只缩大陆（薄陆壳→均衡山低）
                elevations[i] = ocean
                    ? (float)Normal(rng, -4019, 1113)
                    : (float)Normal(rng, 797 * hypsoscale, 1169 * hypsoscale);
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
            var rng = new DeterministicRandom(seed);   // 2026-08-19：System.Random→DeterministicRandom（跨运行时稳定）
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
    }
}
