using Godot;

namespace World.MapGen.Model;

/// <summary>全流水线场实现（2026-08-16 抽象框架迁移）：水文/生态/矿藏/土壤继承统一基类 ModelBase，
/// Compute() 实现真实计算（算法迁移自旧 StageHydrology/StageRiparian/StageMinerals/StageSoil）。</summary>

/// <summary>河流场（Stage2 水文：动态流向 + 输沙侵蚀沉积；末尾覆写河岸带 Riparian biome）。</summary>
public sealed class RiverField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public RiverField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "河流";
    public string Domain => "陆地";
    public override float Magnitude => 1f;
    public string Stage => "Stage2";
    public override string[] DependsOn() => new[] { "海拔", "年降水", "年均温" };

    public void Compute()
    {
        var pipe = _pipe;
        RiverSystem.ComputeIterative(pipe.Verts, pipe.Neighbors, pipe.ENorm, pipe.Elev,
            pipe.Precip, pipe.Temp, waterThreshold: 5000f, lakeThreshold: 200f,
            seaLevelM: 0f, elevSpan: pipe.ElevSpan, rounds: 8,   // ⚠️ 2026-08-18 4→8：河流下切增强（V 谷——山脉山谷）
            out pipe.RiverFlow, out pipe.RiverVolume, out pipe.RiverLevel, out pipe.LakeLevel, out _);
        // 侵蚀后更新范围（存档用；Elev 含河谷/三角洲）
        pipe.MinElev = float.MaxValue; pipe.MaxElev = float.MinValue;
        foreach (var e in pipe.Elev) { if (e < pipe.MinElev) pipe.MinElev = e; if (e > pipe.MaxElev) pipe.MaxElev = e; }
        // ── 河岸带（原 Stage3）：沿岸陆地格 → Riparian biome 覆写 ──
        pipe.RiparianCount = 0;
        for (int i = 0; i < pipe.Elev.Length; i++)
        {
            if (pipe.Elev[i] <= 0f) continue;                          // 海洋不算
            if (pipe.RiverLevel[i] > 0 || pipe.LakeLevel[i] > 0) continue; // 水格本身不算
            bool wet = false;
            foreach (var nb in pipe.Neighbors[i])
                if (pipe.RiverLevel[nb] > 0 || pipe.LakeLevel[nb] > 0) { wet = true; break; }
            if (wet) { pipe.Biome[i] = (byte)World.Biome.BiomeType.Riparian; pipe.RiparianCount++; }
        }
    }

    public override bool Verify() => _pipe.RiverFlow != null && AnyNonZero(_pipe.RiverFlow);
    private static bool AnyNonZero(int[] a)
    {
        foreach (var v in a) if (v != 0) return true;
        return false;
    }
}

/// <summary>湖泊场（Stage2 水文：蓄水格）。数据由 RiverField 一并产出（no-op）。</summary>
public sealed class LakeField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public LakeField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "湖泊";
    public string Domain => "陆地";
    public override float Magnitude => 1f;
    public string Stage => "Stage2";
    public override string[] DependsOn() => new[] { "河流" };
    public void Compute() { }   // ⚠️ 已由 RiverField 一并产出
    public override bool Verify() => _pipe.LakeLevel != null && AnyNonZero(_pipe.LakeLevel);
    private static bool AnyNonZero(byte[] a)
    {
        foreach (var v in a) if (v != 0) return true;
        return false;
    }
}

/// <summary>矿藏场（Stage4 资源：矿化事件在板块演化累积 → 分位数强度）。</summary>
public sealed class MineralField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public MineralField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "矿藏";
    public string Domain => "陆地";
    public override float Magnitude => 1f;
    public string Stage => "Stage4";
    public override string[] DependsOn() => new[] { "河流", "海拔", "年降水" };

    public void Compute()
    {
        var pipe = _pipe;
        MineralSystem.ComputeMinerals(pipe.Verts, pipe.Neighbors, pipe.RiverFlow, pipe.ENorm, pipe.Precip,
            pipe.Sim.WorldCrust?.Age, pipe.Sim.MineralHydro, pipe.Sim.MineralSed, pipe.Sim.MineralMeta,
            pipe.Sim.WorldCrust, pipe.P.Seed, out pipe.MineralLevel);
    }

    public override bool Verify() => _pipe.MineralLevel != null && AnyNonZero(_pipe.MineralLevel);
    private static bool AnyNonZero(byte[] a)
    {
        foreach (var v in a) if (v != 0) return true;
        return false;
    }
}

/// <summary>土壤场（Stage5：肥力 1-5，biome/气候消费）。</summary>
public sealed class SoilField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public SoilField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "土壤";
    public string Domain => "陆地";
    public override float Magnitude => 5f;
    public string Stage => "Stage5";
    public override string[] DependsOn() => new[] { "柯本biome", "海拔" };

    public void Compute()
    {
        var pipe = _pipe;
        SoilSystem.ComputeSoil(pipe.ENorm, pipe.Biome, pipe.Precip, pipe.Temp,
            pipe.Sim.WorldCrust?.MaficVolcanic, pipe.RiverFlow, out pipe.SoilLevel);
    }

    public override bool Verify() => _pipe.SoilLevel != null && AnyNonZero(_pipe.SoilLevel);
    private static bool AnyNonZero(byte[] a)
    {
        foreach (var v in a) if (v != 0) return true;
        return false;
    }
}

/// <summary>大陆架平台场（2026-08-18 用户拍板：沿海深海不科学——被动大陆边缘形态）。
/// 近岸 ≤4 跳海格 → -150m（大陆架平台——标准深度）；4~6 跳过渡（大陆坡——插值到真实深度）；
/// &gt;6 跳真实深海盆。陆地不变（Elev&gt;0 判定不变）——海陆比/模拟不变；浅海带变宽（显示）。
/// 注册在侵蚀后、温度前——浅海暖海岸（海温调节）物理链完整。</summary>
public sealed class ContinentalShelfField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public ContinentalShelfField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "大陆架";
    public string Domain => "海洋";
    public override float Magnitude => 1f;
    public string Stage => "Stage2";
    public override string[] DependsOn() => new[] { "侵蚀堆积", "海拔" };

    public void Compute()
    {
        var pipe = _pipe;
        int n = pipe.Elev.Length;
        // 海岸距离 BFS（从陆地展开——海格距海岸跳数；只走海格）
        var shoreDist = new int[n];
        System.Array.Fill(shoreDist, int.MaxValue);
        var q = new System.Collections.Generic.Queue<int>();
        for (int i = 0; i < n; i++)
            if (pipe.Elev[i] > 0f) { shoreDist[i] = 0; q.Enqueue(i); }
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            if (shoreDist[c] >= 6) continue;
            foreach (var nb in pipe.Neighbors[c])
                if (shoreDist[nb] == int.MaxValue && pipe.Elev[nb] <= 0f)   // 只走海格
                { shoreDist[nb] = shoreDist[c] + 1; q.Enqueue(nb); }
        }
        // 大陆架调整：≤4 跳 → -150m 平台；4~6 跳 → 大陆坡（-150 → 真实深度插值）
        // ⚠️ 2026-08-18 主动边缘（俯冲带）：跳过平台化——保持原始深度（智利/日本型海岸无大陆架）
        byte[] subd = pipe.Sim?.SubductionMask;
        const float shelfDepth = -150f;
        for (int i = 0; i < n; i++)
        {
            if (pipe.Elev[i] >= 0f || shoreDist[i] == int.MaxValue) continue;   // 陆地 / 深海盆（>6 跳）
            if (subd != null && subd[i] == 1 && shoreDist[i] <= 6) continue;    // 俯冲带（主动边缘——无大陆架）
            if (shoreDist[i] <= 4)
                pipe.Elev[i] = shelfDepth;
            else
                pipe.Elev[i] = Mathf.Lerp(shelfDepth, pipe.Elev[i], (shoreDist[i] - 4) / 2f);
        }
        // 更新范围（存档用）
        pipe.MinElev = float.MaxValue; pipe.MaxElev = float.MinValue;
        foreach (var e in pipe.Elev) { if (e < pipe.MinElev) pipe.MinElev = e; if (e > pipe.MaxElev) pipe.MaxElev = e; }
    }

    public override bool Verify() => _pipe.Elev != null;
}

/// <summary>冰盖场（2026-08-18 用户拍板：冰盖了就应该变成陆地——生成阶段）。
/// 温度 ≤-5°C（海水冰点）的海 → Elev=5m（冰盖陆地——基岩上冰）。
/// 模拟影响（读档 R 场重建）：冰盖格 Elev&gt;0 判陆地（影响圈 BFS 可穿越）+ R=0
/// （CivEngine.BuildLayer1 温度 ≤-5 强制无生产力——冰盖不能采集/驻扎）。
/// 依赖大陆架（冰盖覆盖大陆架——最后抬）。</summary>
public sealed class IceSheetField : ModelBase, IFieldRole
{
    private readonly PlanetPipeline _pipe;
    public IceSheetField(PlanetPipeline pipe) => _pipe = pipe;
    public override string Name => "冰盖";
    public string Domain => "极地";
    public override float Magnitude => 1f;
    public string Stage => "Stage2";
    public override string[] DependsOn() => new[] { "气候基准", "大陆架" };

    public void Compute()
    {
        var pipe = _pipe;
        int n = pipe.Elev.Length;
        for (int i = 0; i < n; i++)
        {
            if (pipe.Elev[i] > 0f) continue;      // 已是陆地
            if (pipe.Temp[i] > -5f) continue;     // 海冰阈值（海水冰点——海冰形成）
            pipe.Elev[i] = 5f;                    // 冰盖陆地（基岩上冰——抬到海平面以上）
        }
        pipe.MinElev = float.MaxValue; pipe.MaxElev = float.MinValue;
        foreach (var e in pipe.Elev) { if (e < pipe.MinElev) pipe.MinElev = e; if (e > pipe.MaxElev) pipe.MaxElev = e; }
    }

    public override bool Verify() => _pipe.Elev != null;
}
