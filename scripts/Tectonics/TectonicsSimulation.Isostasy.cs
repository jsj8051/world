using Godot;

namespace World.Tectonics
{
    // 职责：均衡位移/岩石圈挠曲与海平面求解。
    //   ComputeDisplacement   —— 均衡位移（isostasy → displacement，含挠曲）
    //   ApplyFlexure          —— 岩石圈挠曲（前陆盆地，山体负载 → 邻域沉降）
    //   LandFraction / LandFractionAboveSea —— 陆地占比统计
    //   SolveSeaLevel         —— 面积二分求海平面（初始化用）
    //   InitializeOceanVolume —— 水量守恒常量初始化（M3-5）
    //   SolveSeaLevelByVolume —— 按水量守恒二分求海平面（M3-5）
    public partial class TectonicsSimulation
    {
        /// <summary>计算均衡位移（m）。displacement = thickness - thickness×density/mantle。</summary>
        public float[] ComputeDisplacement()
        {
            var thickness = WorldCrust.GetThickness(Material);
            var mass = WorldCrust.GetTotalMass();
            var density = WorldCrust.GetDensity(mass, thickness, Material.MaficVolcanicMin);
            Displacement = WorldCrust.GetIsostaticDisplacement(thickness, density, Material);
            ApplyFlexure();   // ⚠️ 2026-08-18 岩石圈挠曲（前陆盆地）
            return Displacement;
        }

        /// <summary>岩石圈挠曲（2026-08-18 用户拍板 C：盆地自然涌现——A 前陆盆地）。
        /// 山脉负载 → 邻域地壳挠曲下沉（薄板挠曲的迭代松弛近似——波长由轮数控制）。
        /// flex 初始 = 正位移负载，8 轮邻域平滑扩散；Displacement -= flex×flexK。
        /// flexK = 挠曲系数（一阶标定：山脉负载 35% 由邻域挠曲支撑，其余均衡支撑）。</summary>
        public void ApplyFlexure()
        {
            int n = GlobalGrid.VertexCount;
            var neighbors = GlobalGrid.Neighbors;
            var load = new float[n];
            var flex = new float[n];
            for (int i = 0; i < n; i++)
            {
                load[i] = Displacement[i] > 0f ? Displacement[i] : 0f;   // 山体负载（正位移）
                flex[i] = load[i];
            }
            const int rounds = 4;   // ⚠️ 2026-08-18 8→4：少抹一层细节（挠曲是负载扩散不是地形平滑——山谷保留）
            var tmp = new float[n];
            for (int r = 0; r < rounds; r++)
            {
                for (int i = 0; i < n; i++)
                {
                    var nbs = neighbors[i];
                    float sum = flex[i];
                    for (int j = 0; j < nbs.Length; j++) sum += flex[nbs[j]];
                    tmp[i] = sum / (nbs.Length + 1);
                }
                var t = tmp; tmp = flex; flex = t;
            }
            const float flexK = 0.35f;
            for (int i = 0; i < n; i++)
                Displacement[i] -= flex[i] * flexK;
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
