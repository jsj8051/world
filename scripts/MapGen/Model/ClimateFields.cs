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
    public override string[] DependsOn() => new[] { "海拔", "年降水", "月温度" };   // 风蚀项消费年合成风场（MonthWind，月温度场产出）

    public void Compute()
    {
        var pipe = _pipe;
        int n = pipe.Verts.Length;
        var e = pipe.ENorm;
        var net = new float[n];

        // ── 1. 水蚀项（坡面径流：坡度×降水，相邻下坡搬运；高山侵蚀→低处堆积）──
        var outbound = new float[n];
        for (int i = 0; i < n; i++)
        {
            float hi = e[i];
            float sum = 0f;
            foreach (var nb in pipe.Neighbors[i])
            {
                float diff = hi - e[nb];
                if (diff > 0f) sum += diff;
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
                if (diff > 0f) recv += diff;
            }
            net[i] = recv - outbound[i];
        }

        // ── 2. 风蚀项（2026-08-16 用户拍板：风蚀沿风场逐步沉积，高山被侵蚀填到低处）──
        //    干旱区（降水少裸露）风卷沙 → 沿统一风场年合成方向搬运 → 沉积率由【局部风强】决定：
        //    depositRate ∝ (1 − wMag)——强风处颗粒悬浮不易沉（搬运远）、风弱处快速落沙
        //    （"风减弱处沉积"= 黄土/沙丘式沉积带的物理机制）。搬运距离由沉积率自然涌现，
        //    步数仅是防死循环兜底（非定死距离）。
        var windYear = new Vector3[n];
        for (int m = 0; m < 12; m++)
            for (int i = 0; i < n; i++) windYear[i] += pipe.MonthWind[m][i] / 12f;
        const float KWind = 0.3f;      // 风蚀强度系数（相对水蚀，全球占比 ~10-20%）
        const float KSettle = 0.5f;    // 沉降系数：depositRate = KSettle×(1−wMag)（风弱沉积快）
        const int MaxSteps = 100;      // 兜底上限（防风场闭环死循环；正常由沉积率终止）
        for (int i = 0; i < n; i++)
        {
            if (e[i] < 0.02f) continue;          // 只陆地风蚀（海洋无地表物质）
            var w = windYear[i];
            float wMag = w.Length();
            if (wMag < 1e-6f) continue;
            float arid = 1f - Mathf.Clamp(pipe.Precip[i] / 800f, 0f, 1f);   // 干旱度（降水<800mm 裸露）
            float src = wMag * arid * KWind;     // 风蚀源（相对量）
            if (src < 1e-5f) continue;
            net[i] -= src;                       // 风蚀（源格被卷走）
            float remain = src;
            int cur = i;
            var windDir = w / wMag;
            for (int s = 0; s < MaxSteps && remain > 1e-5f; s++)
            {
                // 沉积率 ∝ 风减弱程度（局部风强弱 → 落沙快）
                float wLocal = windYear[cur].Length();
                float depositRate = KSettle * (1f - Mathf.Min(wLocal, 1f));
                float deposit = remain * depositRate;
                net[cur] += deposit;             // 路径沉积（风弱处落沙）
                remain -= deposit;
                // 沿风向走：邻居中方向投影最大者（贪心爬，风场发散点停）
                int next = cur; float bestProj = -0.5f;
                foreach (var nb in pipe.Neighbors[cur])
                {
                    var dirN = (pipe.Verts[nb] - pipe.Verts[cur]).Normalized();
                    float proj = windDir.Dot(dirN);
                    if (proj > bestProj) { bestProj = proj; next = nb; }
                }
                if (next == cur) break;
                cur = next;
            }
        }

        // 标定 → m/演化期量级（正=堆积、负=侵蚀）
        pipe.ErosionNet = new float[n];
        for (int i = 0; i < n; i++) pipe.ErosionNet[i] = net[i] * 300f;
    }

    public override bool Verify() => _pipe.ErosionNet != null && AnyNonZero(_pipe.ErosionNet);
    private static bool AnyNonZero(float[] a)
    {
        foreach (var v in a) if (v != 0f) return true;
        return false;
    }
}

/// <summary>年均温场（纬度+海拔+洋流+反照率+大陆性，Plan C 源场）。
/// Compute = 第一遍洋流（WindField，供温度修正）+ 沿岸冷暖采样 + 年温计算。</summary>
public sealed class TemperatureField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public TemperatureField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "年均温";
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
        // 年温（Parallel 保留——纯函数逐格独立）
        System.Threading.Tasks.Parallel.For(0, vn, i =>
        {
            pipe.Temp[i] = pipe.Climate.ComputeTemperature(pipe.Verts[i] * pipe.P.RadiusKm, pipe.ENorm[i]);
        });
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
    public override string[] DependsOn() => new[] { "年均温" };   // 洋流修正（第一遍在温度场内部完成）

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
    public override string[] DependsOn() => new[] { "年均温", "年降水", "温度→风→降水→温度" };   // 湿润降温后在 MonsoonSystem 前

    public void Compute()
    {
        var pipe = _pipe;
        World.Biome.MonsoonSystem.Compute(pipe.Verts, pipe.Neighbors, pipe.ENorm, pipe.Elev,
            pipe.Temp, pipe.Precip, pipe.P.AxialTilt, pipe.P.RotationSpeed,
            out var monsoon, out var tHotM, out var tColdM, out var dryP, out var dryIdx, out var monthP,
            out var monthWind, out var monthTemp);
        pipe.MonsoonStrength = monsoon;
        pipe.MonthPrecip = monthP;
        pipe.MonthTemp = monthTemp;
        pipe.MonthWind = monthWind;
        // 柯本分类消费的月数据中间量
        pipe.HotM = tHotM; pipe.ColdM = tColdM; pipe.DryP = dryP; pipe.DryIdx = dryIdx;
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
        var windYear = new Vector3[vn];
        for (int m = 0; m < 12; m++)
            for (int i = 0; i < vn; i++) windYear[i] += pipe.MonthWind[m][i] / 12f;
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
    public override string[] DependsOn() => new[] { "年均温", "年降水", "月温度" };

    public void Compute()
    {
        var pipe = _pipe;
        int vn = pipe.Verts.Length;
        System.Threading.Tasks.Parallel.For(0, vn, i =>
        {
            float latDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(pipe.Verts[i].Y, -1f, 1f)));
            pipe.Biome[i] = (byte)World.Biome.BiomeClassifier.Classify(pipe.ENorm[i], pipe.Temp[i], pipe.Precip[i],
                pipe.HotM[i], pipe.ColdM[i], pipe.DryP[i], pipe.DryIdx[i], latDeg);
        });
    }

    public override bool Verify() => _pipe.Biome != null && AnyNonZero(_pipe.Biome);
    private static bool AnyNonZero(byte[] a)
    {
        foreach (var v in a) if (v != 0) return true;
        return false;
    }
}
