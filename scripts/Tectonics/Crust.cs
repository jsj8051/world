using Godot;
using System;

namespace World.Tectonics
{
    /// <summary>
    /// 地壳模型（tectonics.js Crust.js + RockColumn.js 的 C# 移植，2026-08-02）。
    ///
    /// 每格 8 个物质场（单位：km·kg/m³ = kg/m²，即"厚度×密度"的质量面密度）：
    ///   sediment(沉积物), sedimentary(沉积岩), metamorphic(变质岩),
    ///   felsic_plutonic(长英质深成), felsic_volcanic(长英质火山),
    ///   mafic_volcanic(镁铁质火山), mafic_plutonic(镁铁质深成), age(年龄)
    ///
    /// 守恒组（5 种 felsic 类）：总量守恒，用于质量守恒校验。
    /// 非守恒组：mafic 类（可俯冲消减/洋壳增生）、age。
    ///
    /// 派生场（JS 用 Memo 惰性缓存，C# 直接显式计算）：
    ///   thickness = Σ 各物质厚度（质量/密度）
    ///   total_mass = Σ 各物质质量
    ///   density = total_mass / thickness
    ///   displacement = thickness - thickness×density/mantle_density（均衡补偿，相对基准面）
    ///   buoyancy = -(density - mantle_density)×g，clamp ≤ 0（负=下沉驱动俯冲）
    ///
    /// 源码参考：docs/tectonics-ref/noncompiled/models/lithosphere/Crust.js
    /// </summary>
    public class Crust
    {
        public SphereGrid Grid;
        public float[] Sediment;
        public float[] Sedimentary;
        public float[] Metamorphic;
        public float[] FelsicPlutonic;
        public float[] FelsicVolcanic;
        public float[] MaficVolcanic;
        public float[] MaficPlutonic;
        public float[] Age;

        public Crust(SphereGrid grid)
        {
            Grid = grid;
            int n = grid.VertexCount;
            Sediment = new float[n];
            Sedimentary = new float[n];
            Metamorphic = new float[n];
            FelsicPlutonic = new float[n];
            FelsicVolcanic = new float[n];
            MaficVolcanic = new float[n];
            MaficPlutonic = new float[n];
            Age = new float[n];
        }

        /// <summary>全部 8 场（顺序固定，序列化用）。</summary>
        public float[][] AllPools()
        {
            return new[] { Sediment, Sedimentary, Metamorphic, FelsicPlutonic, FelsicVolcanic, MaficVolcanic, MaficPlutonic, Age };
        }

        /// <summary>质量场（7 个，不含 age）。</summary>
        public float[][] MassPools()
        {
            return new[] { Sediment, Sedimentary, Metamorphic, FelsicPlutonic, FelsicVolcanic, MaficVolcanic, MaficPlutonic };
        }

        /// <summary>守恒场（5 种 felsic 类）。</summary>
        public float[][] ConservedPools()
        {
            return new[] { Sediment, Sedimentary, Metamorphic, FelsicPlutonic, FelsicVolcanic };
        }

        /// <summary>全部置 0。</summary>
        public void Reset()
        {
            foreach (var pool in AllPools())
                Array.Clear(pool, 0, pool.Length);
        }

        /// <summary>crust += delta（8 场逐元素加）。对应 Crust.add_delta。</summary>
        public static void AddDelta(Crust crust, Crust delta)
        {
            var a = crust.AllPools();
            var b = delta.AllPools();
            for (int p = 0; p < 8; p++)
                for (int i = 0; i < a[p].Length; i++)
                    a[p][i] += b[p][i];
        }

        /// <summary>每格守恒质量（Σ 5 种 felsic）。对应 Crust.get_conserved_mass。</summary>
        public float[] GetConservedMass()
        {
            var mass = new float[Grid.VertexCount];
            foreach (var pool in ConservedPools())
                for (int i = 0; i < mass.Length; i++) mass[i] += pool[i];
            return mass;
        }

        /// <summary>每格总质量（Σ 7 种）。对应 Crust.get_total_mass。</summary>
        public float[] GetTotalMass()
        {
            var mass = new float[Grid.VertexCount];
            foreach (var pool in MassPools())
                for (int i = 0; i < mass.Length; i++) mass[i] += pool[i];
            return mass;
        }

        /// <summary>复用版（2026-08-02 性能：Merge 每 plate 调用，避免每步 3.4MB 分配）。</summary>
        public float[] GetTotalMass(float[] into)
        {
            System.Array.Clear(into, 0, into.Length);
            foreach (var pool in MassPools())
                for (int i = 0; i < into.Length; i++) into[i] += pool[i];
            return into;
        }

        /// <summary>每格总厚度（km）：质量/密度。对应 Crust.get_thickness。
        /// 密度：mafic 用随年龄插值（0→min, 250My→max），felsic 用固定密度。</summary>
        public float[] GetThickness(MaterialDensity materialDensity)
        {
            int n = Grid.VertexCount;
            var thickness = new float[n];
            for (int i = 0; i < n; i++)
            {
                // mafic 密度随年龄（洋壳冷却变密）
                float frac = FieldOps.Linearstep(0f, 250f * Units.MEGAYEAR, Age[i]);
                float maficDensity = materialDensity.MaficVolcanicMin
                    + (materialDensity.MaficVolcanicMax - materialDensity.MaficVolcanicMin) * frac;
                float t = 0;
                t += MaficPlutonic[i] / maficDensity;
                t += MaficVolcanic[i] / maficDensity;
                t += Sediment[i] / materialDensity.Sediment;
                t += Sedimentary[i] / materialDensity.Sedimentary;
                t += Metamorphic[i] / materialDensity.Metamorphic;
                t += FelsicPlutonic[i] / materialDensity.FelsicPlutonic;
                t += FelsicVolcanic[i] / materialDensity.FelsicVolcanic;
                thickness[i] = t;
            }
            return thickness;
        }

        /// <summary>复用版（2026-08-02 性能）。</summary>
        public float[] GetThickness(MaterialDensity materialDensity, float[] into)
        {
            int n = Grid.VertexCount;
            for (int i = 0; i < n; i++)
            {
                float frac = FieldOps.Linearstep(0f, 250f * Units.MEGAYEAR, Age[i]);
                float maficDensity = materialDensity.MaficVolcanicMin
                    + (materialDensity.MaficVolcanicMax - materialDensity.MaficVolcanicMin) * frac;
                float t = 0;
                t += MaficPlutonic[i] / maficDensity;
                t += MaficVolcanic[i] / maficDensity;
                t += Sediment[i] / materialDensity.Sediment;
                t += Sedimentary[i] / materialDensity.Sedimentary;
                t += Metamorphic[i] / materialDensity.Metamorphic;
                t += FelsicPlutonic[i] / materialDensity.FelsicPlutonic;
                t += FelsicVolcanic[i] / materialDensity.FelsicVolcanic;
                into[i] = t;
            }
            return into;
        }

        /// <summary>每格平均密度。对应 Crust.get_density。</summary>
        public float[] GetDensity(float[] totalMass, float[] thickness, float defaultDensity)
        {
            var density = new float[totalMass.Length];
            for (int i = 0; i < totalMass.Length; i++)
                density[i] = thickness[i] > 0 ? totalMass[i] / thickness[i] : defaultDensity;
            return density;
        }

        /// <summary>复用版（2026-08-02 性能）。</summary>
        public float[] GetDensity(float[] totalMass, float[] thickness, float defaultDensity, float[] into)
        {
            for (int i = 0; i < totalMass.Length; i++)
                into[i] = thickness[i] > 0 ? totalMass[i] / thickness[i] : defaultDensity;
            return into;
        }

        /// <summary>每格净浮力（N/m³，≤0）。对应 Crust.get_buoyancy。</summary>
        public float[] GetBuoyancy(float[] density, MaterialDensity materialDensity, float surfaceGravity)
        {
            var buoyancy = new float[density.Length];
            for (int i = 0; i < density.Length; i++)
            {
                float diff = density[i] - materialDensity.Mantle;
                buoyancy[i] = Mathf.Min(-diff * surfaceGravity, 0f);
            }
            return buoyancy;
        }

        /// <summary>每格均衡位移（相对基准面，m）。对应 FluidMechanics.get_isostatic_displacements。
        /// displacement = thickness - thickness×density/mantle_density。</summary>
        public float[] GetIsostaticDisplacement(float[] thickness, float[] density, MaterialDensity materialDensity)
        {
            var disp = new float[thickness.Length];
            float invMantle = 1f / materialDensity.Mantle;
            for (int i = 0; i < thickness.Length; i++)
                disp[i] = thickness[i] - thickness[i] * density[i] * invMantle;
            return disp;
        }

        // ── 地表过程（M3，Crust.js model_* 移植）──

        /// <summary>
        /// 侵蚀：沿网格边从高往低搬运 5 种 felsic 物质。
        /// 对应 JS Crust.model_erosion。
        ///
        /// 核心公式：outbound_transfer[from] += max(0, h[from]-h[to]) × precip × seconds × k × ρ
        /// 然后按各物质占比分配搬运量（fraction = pool[i]/total_transfer，clamp 到 [0,1]），
        /// 均分到各邻居。delta[from] -= transfer; delta[to] += transfer。
        ///
        /// precip = 1.05m/年（全球陆地平均降水），erosiveFactor = 1.8e-7，
        /// 搬运量单位 = kg（质量面密度）。
        /// </summary>
        public static void ModelErosion(
            SphereGrid grid, float[] surfaceHeight, float seconds,
            MaterialDensity materialDensity, Crust topCrust, Crust crustDelta,
            float erosionScale = 1f)   // 侵蚀强度倍率（2026-08-16 用户可调：0.5 温和 ~ 2 剧烈）
        {
            const float Precip = 1.05f / 365.25f / 24f / 3600f;  // m/s（1.05m/年）
            const float ErosiveFactor = 1.8e-7f;
            const float Rho = 2600f;   // material_density.felsic_plutonic

            int n = grid.VertexCount;
            crustDelta.Reset();

            // 1. 每顶点出站搬运总量（kg）
            var outbound = new float[n];
            var neighbors = grid.Neighbors;
            for (int i = 0; i < n; i++)
            {
                float hi = surfaceHeight[i];
                for (int k = 0; k < neighbors[i].Length; k++)
                {
                    int j = neighbors[i][k];
                    float diff = hi - surfaceHeight[j];
                    if (diff > 0)
                        outbound[i] += diff * Precip * seconds * ErosiveFactor * erosionScale * Rho;
                }
            }

            // 2. 各物质出站比例（按顶 crust 各池占比，clamp 到可用量）
            var frac = new float[5][];
            for (int p = 0; p < 5; p++) frac[p] = new float[n];
            var pools = topCrust.ConservedPools();   // 顺序固定：sediment, sedi, meta, felsicP, felsicV
            for (int i = 0; i < n; i++)
            {
                float remain = outbound[i];
                if (remain <= 0) continue;
                for (int p = 0; p < 5; p++)
                {
                    float pool = pools[p][i];
                    float f = remain > 1e-9f ? pool / remain : 0f;
                    f = Mathf.Clamp(f, 0f, 1f);
                    frac[p][i] = f / neighbors[i].Length;
                    remain *= 1f - f;
                }
            }

            // 3. 沿边搬运：from 减、to 加
            for (int i = 0; i < n; i++)
            {
                float hi = surfaceHeight[i];
                for (int k = 0; k < neighbors[i].Length; k++)
                {
                    int j = neighbors[i][k];
                    float diff = hi - surfaceHeight[j];
                    if (diff <= 0) continue;
                    float transfer = diff * Precip * seconds * ErosiveFactor * erosionScale * Rho;
                    for (int p = 0; p < 5; p++)
                    {
                        float t = transfer * frac[p][i];
                        if (t == 0) continue;
                        crustDelta.ConservedPools()[p][i] -= t;
                        crustDelta.ConservedPools()[p][j] += t;
                    }
                }
            }
        }

        /// <summary>
        /// 风化：岩石 → 沉积物（出露地表部分）。
        /// 对应 JS Crust.model_weathering。
        /// 简化：weathering = avg_diff × k × precip × seconds × ρ × bedrock_exposure
        /// 从各 felsic 池按比例抽，转成 sediment。
        /// </summary>
        public static void ModelWeathering(
            SphereGrid grid, float[] surfaceHeight, float seconds,
            MaterialDensity materialDensity, Crust topCrust, Crust crustDelta,
            float erosionScale = 1f)   // 侵蚀强度倍率（与 ModelErosion 联动，2026-08-16）
        {
            const float Precip = 1.05f / 365.25f / 24f / 3600f;  // m/s
            const float WeatheringFactor = 1.8e-7f;
            const float CriticalSedimentThickness = 1f;  // m

            int n = grid.VertexCount;
            var neighbors = grid.Neighbors;

            // 平均高度差（全图）
            double avgDiffSum = 0; int cnt = 0;
            for (int i = 0; i < n; i++)
                for (int k = 0; k < neighbors[i].Length; k++)
                {
                    avgDiffSum += Mathf.Abs(surfaceHeight[i] - surfaceHeight[neighbors[i][k]]);
                    cnt++;
                }
            float avgDiff = cnt > 0 ? (float)(avgDiffSum / cnt) : 0f;

            // 基岩暴露度：sediment 薄 → 暴露多
            for (int i = 0; i < n; i++)
            {
                float exposure = 1f - topCrust.Sediment[i]
                    / (CriticalSedimentThickness * materialDensity.Sediment);
                exposure = Mathf.Clamp(exposure, 0f, 1f);
                if (exposure <= 0) continue;

                float weathering = avgDiff * WeatheringFactor * erosionScale * Precip * seconds
                    * materialDensity.FelsicPlutonic
                    * (materialDensity.Mantle > 0 ? 1f : 1f)   // 重力修正占位（原版 surface_gravity/earth_g）
                    * exposure;
                if (weathering <= 0) continue;

                // 从各 felsic 池按比例抽（除 sediment 本身）
                float conserved = topCrust.Sedimentary[i] + topCrust.Metamorphic[i]
                                + topCrust.FelsicPlutonic[i] + topCrust.FelsicVolcanic[i];
                if (conserved <= 0) continue;
                weathering = Mathf.Min(weathering, conserved);
                float ratio = weathering / conserved;

                crustDelta.Sediment[i] += weathering;
                crustDelta.Sedimentary[i] -= topCrust.Sedimentary[i] * ratio;
                crustDelta.Metamorphic[i] -= topCrust.Metamorphic[i] * ratio;
                crustDelta.FelsicPlutonic[i] -= topCrust.FelsicPlutonic[i] * ratio;
                crustDelta.FelsicVolcanic[i] -= topCrust.FelsicVolcanic[i] * ratio;
            }
        }

        /// <summary>
        /// 成岩：沉积物 → 沉积岩（埋深超过 2.2MPa ≈ 500ft 沉积物）。
        /// 对应 JS Crust.model_lithification。
        /// </summary>
        public static void ModelLithification(
            float[] surfaceHeight, float seconds,
            MaterialDensity materialDensity, Crust topCrust, Crust crustDelta)
        {
            float gravity = 9.8f;
            for (int i = 0; i < topCrust.Sediment.Length; i++)
            {
                float overpressure = topCrust.Sediment[i] * gravity;   // kg/m² × m/s² = Pa
                float excess = overpressure - 2.2e6f;                  // 2.2MPa
                if (excess <= 0) continue;
                float lithified = excess / gravity;                    // kg
                lithified = Mathf.Min(lithified, topCrust.Sediment[i]);
                lithified = Mathf.Max(lithified, 0f);
                crustDelta.Sediment[i] -= lithified;
                crustDelta.Sedimentary[i] += lithified;
            }
        }

        /// <summary>
        /// 变质：沉积岩 → 变质岩（埋深超过 300MPa ≈ 11km 沉积岩）。
        /// 对应 JS Crust.model_metamorphosis。
        /// </summary>
        public static void ModelMetamorphosis(
            float[] surfaceHeight, float seconds,
            MaterialDensity materialDensity, Crust topCrust, Crust crustDelta)
        {
            float gravity = 9.8f;
            for (int i = 0; i < topCrust.Sedimentary.Length; i++)
            {
                float overpressure = (topCrust.Sediment[i] + topCrust.Sedimentary[i]) * gravity;
                float excess = overpressure - 300e6f;                  // 300MPa
                if (excess <= 0) continue;
                float metamorphosed = excess / gravity;
                metamorphosed = Mathf.Min(metamorphosed, topCrust.Sedimentary[i]);
                metamorphosed = Mathf.Max(metamorphosed, 0f);
                crustDelta.Sedimentary[i] -= metamorphosed;
                crustDelta.Metamorphic[i] += metamorphosed;
            }
        }
    }

    /// <summary>物质密度/粘度常量（tectonics.js World.js 移植）。单位 kg/m³。</summary>
    public class MaterialDensity
    {
        // 来源：docs/tectonics-ref/noncompiled/models/World.js material_density
        public float Mantle = 3075f;         // 经验标定（isostatic 模型反推）
        public float MaficVolcanicMin = 2890f;   // 年轻洋壳（Carlson & Raskin 1984）
        public float MaficVolcanicMax = 3300f;   // 老洋壳（冷却变密）
        public float Sediment = 1500f;
        public float Sedimentary = 2600f;
        public float Metamorphic = 2800f;
        public float FelsicPlutonic = 2600f;
        public float FelsicVolcanic = 2600f;
        public float Ocean = 1026f;
        // 地幔粘度（World.js material_viscosity，单位 m/s per Pascal）
        public float MantleViscosity = 1.57e20f;
    }

    /// <summary>时间单位（tectonics.js Units.js 移植，2026-08-02 修正）。
    /// ⚠️ MEGAYEAR 原版 = YEAR×1e6 = 3.156e13 秒，我曾误定义为 1e6 秒（差 31.5 倍）
    ///   → 侵蚀/age 时间全错 + 洋壳老化到密度上限后平坦。</summary>
    public static class Units
    {
        public const float YEAR = 365.256363004f * 24f * 3600f;   // 秒
        public const float MEGAYEAR = YEAR * 1e6f;                 // 秒（= 3.1558e13）
        public const float KM = 1000f;
    }
}
