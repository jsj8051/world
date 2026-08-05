using Godot;

namespace World.MapGen.Model;

/// <summary>具体场实现（2026-08-16 抽象框架迁移）：继承统一基类 ModelBase + 场角色接口 IFieldRole，
/// Compute() 实现真实计算（算法迁移自旧 StageClimate 编排）。依赖拓扑序由 ClimateModel.Run 驱动。</summary>

/// <summary>海拔场（板块位移 → 相对海平面海拔 + 归一化 + 采样器；全流水线最源头）。</summary>
public sealed class ElevationField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public ElevationField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "海拔";
    public string Domain => "全球";
    public override float Magnitude => 9000f;
    public string Stage => "板块";

    public void Compute()
    {
        var pipe = _pipe;
        int vn = pipe.Verts.Length;
        var disp = pipe.Sim.Displacement;
        float sea = pipe.Sim.SeaLevel;
        pipe.Elev = new float[vn];
        pipe.MinElev = float.MaxValue; pipe.MaxElev = float.MinValue;
        for (int i = 0; i < vn; i++)
        {
            pipe.Elev[i] = disp[i] - sea;   // 米，0=海平面
            if (pipe.Elev[i] < pipe.MinElev) pipe.MinElev = pipe.Elev[i];
            if (pipe.Elev[i] > pipe.MaxElev) pipe.MaxElev = pipe.Elev[i];
        }
        pipe.ElevSpan = Mathf.Max(-pipe.MinElev, pipe.MaxElev);
        pipe.ENorm = new float[vn];
        for (int i = 0; i < vn; i++) pipe.ENorm[i] = pipe.ElevSpan > 1e-6f ? pipe.Elev[i] / pipe.ElevSpan : 0f;
        // 盛行风降水回调：球面点 → 归一化海拔（最近顶点，桶查询）
        pipe.ElevSampler = point =>
        {
            Vector3 dir = point.Normalized();
            int id = pipe.Grid.NearestId(dir);
            return pipe.ElevSpan > 1e-6f ? pipe.Elev[id] / pipe.ElevSpan : 0f;
        };
    }

    public override bool Verify() => _pipe.Elev != null;
}

/// <summary>侵蚀堆积场（物质搬运通量，2026-08-16 纳入抽象框架）。
/// 诊断场：从最终海拔/降水算每格【净沉积趋势】（正=堆积/负=侵蚀）——
/// 与板块模拟内 Crust.ModelErosion 同一物理（坡度×降水×倍率，高格减/低格加，物质守恒），
/// 但不重跑演化。下游消费：海拔变化率、土壤（冲积）、矿藏（沉积型）。</summary>
public sealed class ErosionDepositionField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public ErosionDepositionField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "侵蚀堆积";
    public string Domain => "陆地";
    public override float Magnitude => 300f;   // m/演化期（净速率量级）
    public string Stage => "板块";
    public override string[] DependsOn() => new[] { "海拔", "年降水", "月温度", "柯本biome" };   // 风蚀消费年合成风场+biome 裸露

    public void Compute()
    {
        var pipe = _pipe;
        int n = pipe.Verts.Length;
        var e = pipe.ENorm;
        var net = new float[n];

        // ── 1. 水蚀项（坡面径流：坡度×降水×局地降水权重，相邻下坡搬运；高山侵蚀→低处堆积）──
        //    P0 优化：搬运 ∝ 源格降水（湿润区坡面侵蚀强、干旱区弱——与风蚀互补）
        var precipW = new float[n];
        for (int i = 0; i < n; i++)
            precipW[i] = Mathf.Clamp(pipe.Precip[i] / 1000f, 0.2f, 1.5f);
        var outbound = new float[n];
        for (int i = 0; i < n; i++)
        {
            float hi = e[i];
            float sum = 0f;
            foreach (var nb in pipe.Neighbors[i])
            {
                float diff = hi - e[nb];
                if (diff > 0f) sum += diff * precipW[i];   // 源格降水权重
            }
            outbound[i] = sum;
        }
        for (int i = 0; i < n; i++)
        {
            float recv = 0f;
            float hi = e[i];
            foreach (var nb in pipe.Neighbors[i])
            {
                float diff = e[nb] - hi;
                if (diff > 0f) recv += diff * precipW[nb];   // 邻居(源格)降水权重
            }
            net[i] = recv - outbound[i];
        }

        // ── 2. 风蚀项（干旱区风卷沙 → 沿风场搬运 → 沉积率∝(1−局部风强)风弱处落沙）──
        //    P0 优化：风蚀源 × biome 裸露系数（沙漠全裸露/草原 0.6/森林≈0/冰盖≈0——真实
        //    植被覆盖决定风蚀；黄土机制=植被稀疏区风蚀）
        //    P2 优化：next 表缓存（风场固定，每格沿风向的贪心下一步预计算一次，追踪 O(步数)）
        var windYear = pipe.WindYear;
        var next = new int[n];
        for (int i = 0; i < n; i++)
        {
            var w = windYear[i];
            float wMag = w.Length();
            if (wMag < 1e-6f) { next[i] = -1; continue; }
            var windDir = w / wMag;
            int bestN = -1; float bestProj = -0.5f;
            foreach (var nb in pipe.Neighbors[i])
            {
                var dirN = (pipe.Verts[nb] - pipe.Verts[i]).Normalized();
                float proj = windDir.Dot(dirN);
                if (proj > bestProj) { bestProj = proj; bestN = nb; }
            }
            next[i] = bestN;
        }
        const float KWind = 0.3f;      // 风蚀强度系数（相对水蚀，全球占比 ~10-20%）
        const float KSettle = 0.5f;    // 沉降系数：depositRate = KSettle×(1−wMag)（风弱沉积快）
        const int MaxSteps = 100;      // 兜底上限（防风场闭环死循环；正常由沉积率终止）
        var visited = new bool[n];     // 防环（next 表可能成环——风场环流）
        for (int i = 0; i < n; i++)
        {
            if (e[i] < 0.02f) continue;          // 只陆地风蚀（海洋无地表物质）
            var w = windYear[i];
            float wMag = w.Length();
            if (wMag < 1e-6f) continue;
            float arid = 1f - Mathf.Clamp(pipe.Precip[i] / 800f, 0f, 1f);   // 干旱度（降水<800mm 裸露）
            float src = wMag * arid * KWind * WindExposure(pipe.Biome[i]);   // ×biome 裸露
            if (src < 1e-5f) continue;
            net[i] -= src;                       // 风蚀（源格被卷走）
            float remain = src;
            int cur = i;
            System.Array.Clear(visited, 0, n);
            visited[i] = true;
            for (int s = 0; s < MaxSteps && remain > 1e-5f; s++)
            {
                // 沉积率 ∝ 风减弱程度（局部风强弱 → 落沙快）
                float wLocal = windYear[cur].Length();
                float depositRate = KSettle * (1f - Mathf.Min(wLocal, 1f));
                float deposit = remain * depositRate;
                net[cur] += deposit;             // 路径沉积（风弱处落沙）
                remain -= deposit;
                // P2：next 表跳转（O(1)），visited 防环
                int nxt = next[cur];
                if (nxt < 0 || visited[nxt]) break;
                visited[nxt] = true;
                cur = nxt;
            }
        }

        // 标定 → m/演化期量级（正=堆积、负=侵蚀）
        pipe.ErosionNet = new float[n];
        for (int i = 0; i < n; i++) pipe.ErosionNet[i] = net[i] * 300f;
    }

    /// <summary>biome 裸露系数（风蚀源权重）：沙漠全裸露 ~ 森林≈0（植被覆盖决定风蚀）。</summary>
    private static float WindExposure(byte b)
    {
        switch (b)
        {
            case (byte)World.Biome.BiomeType.HotDesert:
            case (byte)World.Biome.BiomeType.ColdDesertKoppen:
            case (byte)World.Biome.BiomeType.Desert:
                return 1f;                                  // 沙漠全裸露
            case (byte)World.Biome.BiomeType.HotSteppe:
            case (byte)World.Biome.BiomeType.ColdSteppe:
            case (byte)World.Biome.BiomeType.TropicalSavanna:
            case (byte)World.Biome.BiomeType.TemperateGrassland:
            case (byte)World.Biome.BiomeType.Savanna:
                return 0.6f;                                // 草原
            case (byte)World.Biome.BiomeType.TropicalRainforest:
            case (byte)World.Biome.BiomeType.TropicalMonsoon:
            case (byte)World.Biome.BiomeType.HumidSubtropical:
            case (byte)World.Biome.BiomeType.Oceanic:
            case (byte)World.Biome.BiomeType.MonsoonSubtropical:
            case (byte)World.Biome.BiomeType.TemperateForest:
            case (byte)World.Biome.BiomeType.TropicalForest:
            case (byte)World.Biome.BiomeType.TropicalDryForest:
                return 0.1f;                                // 森林（风蚀≈0）
            case (byte)World.Biome.BiomeType.IceCap:
            case (byte)World.Biome.BiomeType.Tundra:
                return 0.05f;                               // 冰/苔原（无沙源）
            default:
                return 0.4f;                                // 灌丛/高山/地中海等中等裸露
        }
    }

    public override bool Verify() => _pipe.ErosionNet != null && AnyNonZero(_pipe.ErosionNet);
    private static bool AnyNonZero(float[] a)
    {
        foreach (var v in a) if (v != 0f) return true;
        return false;
    }
}

/// <summary>气候基准温度场（2026-08-16 月→年改造：纬度+海拔+洋流+反照率+大陆性 的静态基准，
/// 无季节项——月温度公式的 base 项；年均温由月温度聚合涌现）。</summary>
public sealed class TemperatureField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public TemperatureField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "气候基准";
    public string Domain => "全球";
    public override float Magnitude => 40f;
    public string Stage => "Stage1";

    public void Compute()
    {
        var pipe = _pipe;
        int vn = pipe.Verts.Length;
        // 第一遍洋流（解析 WindField 风驱；第二遍由 CurrentField 覆盖存档场）
        World.Biome.OceanCurrent.Compute(pipe.Verts, pipe.Neighbors, pipe.ENorm,
            out pipe.CurrentDirs, out pipe.CurrentWarmth, out pipe.CurrentStrength);
        // 沿岸采样：球面点 → 最近海洋顶点的冷暖+强度（陆地点查邻居，距离衰减）
        var grid = pipe.Grid;
        var warmthSampler = new System.Func<Vector3, (float warm, float str)>(point =>
        {
            Vector3 dir = point.Normalized();
            int id = grid.NearestId(dir);
            if (pipe.CurrentWarmth[id] != 0f)
                return (pipe.CurrentWarmth[id], pipe.CurrentStrength != null ? pipe.CurrentStrength[id] : 1f);
            float best = 0f, bestD = 1e9f, bestStr = 1f;
            foreach (var nb in grid.Neighbors[id])
            {
                if (pipe.CurrentWarmth[nb] != 0f)
                {
                    float d = Mathf.Acos(Mathf.Clamp(pipe.Verts[id].Dot(pipe.Verts[nb]), -1f, 1f));
                    if (d < bestD) { bestD = d; best = pipe.CurrentWarmth[nb]; bestStr = pipe.CurrentStrength != null ? pipe.CurrentStrength[nb] : 1f; }
                }
            }
            float decay = Mathf.Exp(-bestD / 0.08f);   // 距岸衰减（0.08rad ≈ 500km）
            return (best * decay, bestStr);
        });
        pipe.Climate.SetOceanCurrent(warmthSampler);
        // 气候基准（静态，无季节项）
        System.Threading.Tasks.Parallel.For(0, vn, i =>
        {
            pipe.TempBase[i] = pipe.Climate.ComputeTemperature(pipe.Verts[i] * pipe.P.RadiusKm, pipe.ENorm[i]);
        });
    }

    public override bool Verify() => _pipe.TempBase != null && AnyNonZero(_pipe.TempBase);
    internal static bool AnyNonZero(float[] a)
    {
        foreach (var v in a) if (v != 0f) return true;
        return false;
    }
}

/// <summary>年均温场（2026-08-16 月→年：月温度聚合 = mean(12 月)，涌现而非独立推导）。</summary>
public sealed class AnnualTempField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public AnnualTempField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "年均温";
    public string Domain => "全球";
    public override float Magnitude => 40f;
    public string Stage => "Stage1";
    public override string[] DependsOn() => new[] { "月温度" };

    public void Compute()
    {
        var pipe = _pipe;
        int vn = pipe.Verts.Length;
        if (pipe.Temp == null) pipe.Temp = new float[vn];
        for (int i = 0; i < vn; i++)
        {
            float sum = 0f;
            for (int m = 0; m < 12; m++) sum += pipe.MonthTemp[m][i];
            pipe.Temp[i] = sum / 12f;   // 年均温 = mean(月温度)
        }
    }

    public override bool Verify() => _pipe.Temp != null && AnyNonZero(_pipe.Temp);
    internal static bool AnyNonZero(float[] a)
    {
        foreach (var v in a) if (v != 0f) return true;
        return false;
    }
}

/// <summary>年降水场（纬度带 + 盛行风湿润度 + 雨影 + 洋流修正）。</summary>
public sealed class PrecipField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public PrecipField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "年降水";
    public string Domain => "陆地";
    public override float Magnitude => 2000f;
    public string Stage => "Stage1";
    public override string[] DependsOn() => new[] { "气候基准" };   // 洋流修正（第一遍在基准场内部完成）

    public void Compute()
    {
        var pipe = _pipe;
        int vn = pipe.Verts.Length;
        System.Threading.Tasks.Parallel.For(0, vn, i =>
        {
            pipe.Precip[i] = pipe.Climate.ComputePrecipitation(
                pipe.Verts[i] * pipe.P.RadiusKm, pipe.ENorm[i], pipe.ElevSampler);
        });
    }

    public override bool Verify() => _pipe.Precip != null && TemperatureField.AnyNonZero(_pipe.Precip);
}

/// <summary>月温度场（Plan C：年均温 + 季节摆动×(1+Kc×大陆性×月辐射) − 反照率）。
/// Compute = MonsoonSystem 主入口——一并产出月温度/统一风场/月降水/季风强度/柯本月数据。</summary>
public sealed class MonthTempField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public MonthTempField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "月温度";
    public string Domain => "全球";
    public override float Magnitude => 40f;
    public string Stage => "Stage1";
    public override string[] DependsOn() => new[] { "气候基准", "年降水", "温度→风→降水→温度" };   // 湿润降温修正基准后在 MonsoonSystem 前

    public void Compute()
    {
        var pipe = _pipe;
        World.Biome.MonsoonSystem.Compute(pipe.Verts, pipe.Neighbors, pipe.ENorm, pipe.Elev,
            pipe.TempBase, pipe.Precip, pipe.P.AxialTilt, pipe.P.RotationSpeed, pipe.Climate,
            out var monsoon, out var tHotM, out var tColdM, out var dryP, out var dryIdx, out var monthP,
            out var monthWind, out var monthTemp, out var precipAnnAbs);
        pipe.MonsoonStrength = monsoon;
        pipe.MonthPrecip = monthP;
        pipe.MonthTemp = monthTemp;
        pipe.MonthWind = monthWind;
        pipe.Precip = precipAnnAbs;   // 年降水 = Σ 12 月（月→年涌现，覆盖估算）
        // 柯本分类消费的月数据中间量
        pipe.HotM = tHotM; pipe.ColdM = tColdM; pipe.DryP = dryP; pipe.DryIdx = dryIdx;
        // 年合成风场（P1 优化：洋流第二遍 + 侵蚀堆积风蚀项共享，避免两场各算一遍）
        pipe.WindYear = new Vector3[pipe.Verts.Length];
        for (int m = 0; m < 12; m++)
            for (int i = 0; i < pipe.Verts.Length; i++)
                pipe.WindYear[i] += pipe.MonthWind[m][i] / 12f;
    }

    public override bool Verify() => _pipe.MonthTemp != null && AnyNonZero(_pipe.MonthTemp);
    private static bool AnyNonZero(float[][] a)
    {
        foreach (var arr in a)
            foreach (var v in arr)
                if (v != 0f) return true;
        return false;
    }
}

/// <summary>统一风场（热压场 → 邻居压差 + 科里奥利；含信风/西风/季风）。
/// 数据由 MonthTempField 的 MonsoonSystem 主入口一并产出（no-op，依赖序保证在其后）。</summary>
public sealed class UnifiedWindField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public UnifiedWindField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "统一风场";
    public string Domain => "全球";
    public override float Magnitude => 1f;
    public string Stage => "Stage1";
    public override string[] DependsOn() => new[] { "月温度" };
    public void Compute() { }   // ⚠️ 已由 MonthTempField 的 MonsoonSystem 一并产出
    public override bool Verify() => _pipe.MonthWind != null;
}

/// <summary>月降水场（年降水 × 月分配权重：ITCZ + 季风水汽 + 地形雨影 V·∇h）。
/// 数据由 MonsoonSystem 一并产出（no-op）。</summary>
public sealed class MonthPrecipField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public MonthPrecipField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "月降水";
    public string Domain => "陆地";
    public override float Magnitude => 2000f;
    public string Stage => "Stage1";
    public override string[] DependsOn() => new[] { "月温度", "统一风场" };
    public void Compute() { }   // ⚠️ 已由 MonthTempField 一并产出
    public override bool Verify() => _pipe.MonthPrecip != null && AnyNonZero(_pipe.MonthPrecip);
    private static bool AnyNonZero(float[][] a)
    {
        foreach (var arr in a)
            foreach (var v in arr)
                if (v != 0f) return true;
        return false;
    }
}

/// <summary>洋流场（多因素第二遍：统一风场年合成 + 科里奥利β + 热成风 + 海岸边界 + 西边界强化）。
/// 第一遍（WindField，供温度修正）在 TemperatureField 内部；这里是最终存档场。</summary>
public sealed class CurrentField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public CurrentField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "洋流";
    public string Domain => "海洋";
    public override float Magnitude => 1f;
    public string Stage => "Stage1";
    public override string[] DependsOn() => new[] { "统一风场", "年均温" };

    public void Compute()
    {
        var pipe = _pipe;
        int vn = pipe.Verts.Length;
        // 年合成风场（P1：MonthTempField 已产出 pipe.WindYear，共享不再重算）
        var windYear = pipe.WindYear;
        World.Biome.OceanCurrent.Compute(pipe.Verts, pipe.Neighbors, pipe.ENorm,
            out pipe.CurrentDirs, out pipe.CurrentWarmth, out pipe.CurrentStrength,
            windYear, pipe.Temp, betaScale: 1f);
    }

    public override bool Verify() => _pipe.CurrentDirs != null && _pipe.CurrentWarmth != null;
}

/// <summary>季风强度结论（冬夏水汽×辐射反差，tilt=0 → 0）。数据由 MonsoonSystem 一并产出（no-op）。</summary>
public sealed class MonsoonField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public MonsoonField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "季风强度";
    public string Domain => "陆地";
    public override float Magnitude => 1f;
    public string Stage => "Stage1";
    public override string[] DependsOn() => new[] { "月温度", "统一风场" };
    public void Compute() { }   // ⚠️ 已由 MonthTempField 一并产出
    public override bool Verify() => _pipe.MonsoonStrength != null;
}

/// <summary>柯本生物群系结论（场的导出量：真实月数据分类）。</summary>
public sealed class BiomeField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public BiomeField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "柯本biome";
    public string Domain => "陆地";
    public override float Magnitude => 32f;
    public string Stage => "Stage1";
    public override string[] DependsOn() => new[] { "年均温", "年降水", "月温度", "月降水" };   // ⚠️ 月降水:最湿月比例换算 mm

    public void Compute()
    {
        var pipe = _pipe;
        int vn = pipe.Verts.Length;
        System.Threading.Tasks.Parallel.For(0, vn, i =>
        {
            float latDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(pipe.Verts[i].Y, -1f, 1f)));
            // ⚠️ 2026-08-06 修复：DryP 是月降水比例(Σ=1)，柯本判据需 mm——
            //   比例 0-1 恒 <30 → D 带全判 Dwa、Af 永不出现（单位错配 bug）
            float dryMm = pipe.DryP[i] * pipe.Precip[i];
            float wetMm = 120f;   // 最湿月（Kottek w/s 判据用；月降水比例 → mm）
            if (pipe.MonthPrecip != null && pipe.MonthPrecip.Length == 12)
            {
                float wetP = 0f;
                for (int m = 0; m < 12; m++)
                    if (pipe.MonthPrecip[m][i] > wetP) wetP = pipe.MonthPrecip[m][i];
                wetMm = wetP * pipe.Precip[i];
            }
            pipe.Biome[i] = (byte)World.Biome.BiomeClassifier.Classify(pipe.ENorm[i], pipe.Temp[i], pipe.Precip[i],
                pipe.HotM[i], pipe.ColdM[i], dryMm, pipe.DryIdx[i], latDeg, wetMm);
        });
    }

    public override bool Verify() => _pipe.Biome != null && AnyNonZero(_pipe.Biome);
    private static bool AnyNonZero(byte[] a)
    {
        foreach (var v in a) if (v != 0) return true;
        return false;
    }
}
