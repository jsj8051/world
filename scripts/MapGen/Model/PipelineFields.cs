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
            seaLevelM: 0f, elevSpan: pipe.ElevSpan, rounds: 4,
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
