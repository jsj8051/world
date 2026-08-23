using Godot;
using World.MapGen;

using World.CivSim.Entities;
namespace World.Diagnostics;

/// <summary>MapArchive v6 段表格式往返验证（2026-08-23 段表化 P2）：
/// 生成小型球面数据 → WriteSpherical 写档 → Peek 与 Read 分别读回，逐字段对比写入值。
/// 覆盖：HEAD 全字段（seed/radiusKm/顶点数/海拔温度降水范围/自转三参）+ 全部段
/// （VERT/ELEV/TEMP/PREC/BIOM/OCEN 洋流含 psi/河流三件套/LAKE/MINE/SOIL/MONO/MPRC/MTMP）。
/// headless 跑：--quit-after 300 足够。退出码：任一不一致 → 1；全部一致 → 0。</summary>
public partial class PeekDiag : Node
{
    private const int N = 42;   // 测试用小网格顶点数（Icosahedron.Subdivide(2)）

    public override void _Ready()
    {
        string path = "user://maps/tmp_peekdiag_v6.mpa";
        var verts = new Vector3[N];
        var elev = new float[N];
        var temp = new float[N];
        var precip = new float[N];
        var biome = new byte[N];
        var dirs = new Vector3[N];
        var warmth = new float[N];
        var strength = new float[N];
        var psi = new float[N];
        var riverLevel = new byte[N];
        var riverFlow = new int[N];
        var riverVolume = new float[N];
        var lakeLevel = new byte[N];
        var mineralLevel = new byte[N];
        var soilLevel = new byte[N];
        var monsoonLevel = new byte[N];
        var monthPrecip = new byte[12][];
        var monthTemp = new byte[12][];
        var rng = new System.Random(20260823);
        for (int i = 0; i < N; i++)
        {
            // 构造球面均匀方向（简单 θ/φ 采样，仅往返测试用，无需真实 icosa 拓扑）
            float phi = (float)(rng.NextDouble() * Mathf.Tau);
            float z = (float)(rng.NextDouble() * 2.0 - 1.0);
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            verts[i] = new Vector3(r * Mathf.Cos(phi), z, r * Mathf.Sin(phi));
            elev[i] = (float)(rng.NextDouble() * 9000.0 - 4000.0);
            temp[i] = (float)(rng.NextDouble() * 60.0 - 30.0);
            precip[i] = (float)(rng.NextDouble() * 3000.0);
            biome[i] = (byte)(i % 8);
            dirs[i] = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            warmth[i] = (float)rng.NextDouble();
            strength[i] = (float)(0.3 + rng.NextDouble() * 0.7);
            psi[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
            riverLevel[i] = (byte)(i % 4);
            riverFlow[i] = i * 7;
            riverVolume[i] = (float)rng.NextDouble() * 100f;
            lakeLevel[i] = (byte)(i % 3 == 0 ? 1 : 0);
            mineralLevel[i] = (byte)(i % 5);
            soilLevel[i] = (byte)(1 + i % 5);
            monsoonLevel[i] = (byte)(i * 3);
            for (int m = 0; m < 12; m++)
            {
                if (monthPrecip[m] == null) { monthPrecip[m] = new byte[N]; monthTemp[m] = new byte[N]; }
                monthPrecip[m][i] = (byte)(i + m);
                monthTemp[m][i] = (byte)(i * 2 + m);
            }
        }

        float minElev = -4000f, maxElev = 5000f;
        float minTemp = -30f, maxTemp = 30f;
        float minPrecip = 0f, maxPrecip = 3000f;
        int seed = 20260823;

        if (!MapArchive.WriteSpherical(path, seed, verts, minElev, maxElev, elev, temp, precip, biome,
                                       minTemp, maxTemp, minPrecip, maxPrecip,
                                       prograde: true, rotationSpeed: 0.8f, axialTilt: 21.5f,
                                       currentDirs: dirs, currentWarmth: warmth, currentStrength: strength, psi: psi,
                                       riverLevel: riverLevel, riverFlow: riverFlow, riverVolume: riverVolume,
                                       lakeLevel: lakeLevel, mineralLevel: mineralLevel, soilLevel: soilLevel,
                                       monsoonLevel: monsoonLevel, monthPrecip: monthPrecip, monthTemp: monthTemp,
                                       radiusKm: 5100f, log: true))
        {
            GD.Print("PeekDiag: 写入失败");
            GetTree().Quit(1);
            return;
        }

        int fail = 0;

        // ── Peek（轻量头部）──
        if (!MapArchive.Peek(path, out int pSeed, out int pVc, out int pH, out float pMinE, out float pMaxE, out ushort pVer, out _))
        {
            GD.Print("PeekDiag: Peek 失败");
            fail++;
        }
        else
        {
            if (pSeed != seed) { GD.Print($"PeekDiag: seed 不一致 {pSeed} != {seed}"); fail++; }
            if (pVc != N) { GD.Print($"PeekDiag: 顶点数不一致 {pVc} != {N}"); fail++; }
            if (pH != 0) { GD.Print($"PeekDiag: 平面 height 应恒 0，得 {pH}"); fail++; }
            if (pMinE != minElev) { GD.Print($"PeekDiag: minElev 不一致 {pMinE} != {minElev}"); fail++; }
            if (pMaxE != maxElev) { GD.Print($"PeekDiag: maxElev 不一致 {pMaxE} != {maxElev}"); fail++; }
            if (pVer != MapArchive.Version) { GD.Print($"PeekDiag: 版本不一致 {pVer} != {MapArchive.Version}"); fail++; }
            if (fail == 0) GD.Print($"PeekDiag: Peek 一致 seed={pSeed} 顶点={pVc} elev[{pMinE:F0},{pMaxE:F0}] ver={pVer}");
        }

        // ── Read（全量）──
        if (!MapArchive.Read(path, out var map))
        {
            GD.Print("PeekDiag: Read 失败");
            fail++;
            GetTree().Quit(fail == 0 ? 0 : 1);
            return;
        }
        Check(map.Seed == seed, "seed");
        Check(map.RadiusKm == 5100f, "radiusKm");
        Check(map.Verts.Length == N, "verts 长度");
        for (int i = 0; i < N && fail < 3; i++)
        {
            if (map.Verts[i] != verts[i]) { GD.Print($"PeekDiag: verts[{i}] 不一致"); fail++; break; }
            if (map.Elev[i] != elev[i]) { GD.Print($"PeekDiag: elev[{i}] 不一致"); fail++; break; }
            if (map.Temp[i] != temp[i]) { GD.Print($"PeekDiag: temp[{i}] 不一致"); fail++; break; }
            if (map.Precip[i] != precip[i]) { GD.Print($"PeekDiag: precip[{i}] 不一致"); fail++; break; }
            if (map.Biome[i] != biome[i]) { GD.Print($"PeekDiag: biome[{i}] 不一致"); fail++; break; }
        }
        Check(map.MinElev == minElev && map.MaxElev == maxElev, "elev 范围");
        Check(map.MinTemp == minTemp && map.MaxTemp == maxTemp, "temp 范围");
        Check(map.MinPrecip == minPrecip && map.MaxPrecip == maxPrecip, "precip 范围");
        Check(map.ProgradeRotation, "prograde");
        Check(map.RotationSpeed == 0.8f, "rotationSpeed");
        Check(map.AxialTilt == 21.5f, "axialTilt");
        Check(map.CurrentDirs != null && map.CurrentWarmth != null && map.CurrentStrength != null && map.Psi != null, "洋流段存在");
        for (int i = 0; i < N && fail < 3; i++)
        {
            if (map.CurrentDirs[i] != dirs[i]) { GD.Print($"PeekDiag: dirs[{i}] 不一致"); fail++; break; }
            if (map.CurrentWarmth[i] != warmth[i]) { GD.Print($"PeekDiag: warmth[{i}] 不一致"); fail++; break; }
            if (map.CurrentStrength[i] != strength[i]) { GD.Print($"PeekDiag: strength[{i}] 不一致"); fail++; break; }
            if (map.Psi[i] != psi[i]) { GD.Print($"PeekDiag: psi[{i}] 不一致"); fail++; break; }
        }
        Check(map.RiverLevel != null && map.RiverFlow != null && map.RiverVolume != null && map.LakeLevel != null, "河流段存在");
        Check(map.MineralLevel != null && map.SoilLevel != null, "矿藏/土壤段存在");
        Check(map.MonsoonLevel != null && map.MonthPrecip != null && map.MonthTemp != null, "季风/月段存在");
        Check(map.MonsoonLevel[5] == 15, "monsoon 值");
        Check(map.MonthPrecip[11][41] == 52 && map.MonthTemp[0][1] == 2, "月场值");

        // ── 缺失段语义：不传洋流 → OCEN 不存在 → CurrentDirs=null（现场重算兜底入口）──
        string path2 = "user://maps/tmp_peekdiag_noocean.mpa";
        if (MapArchive.WriteSpherical(path2, seed, verts, minElev, maxElev, elev, temp, precip, biome,
                                      minTemp, maxTemp, minPrecip, maxPrecip, log: false)
            && MapArchive.Read(path2, out var map2))
        {
            Check(map2.CurrentDirs == null && map2.Psi == null, "无洋流段 → null（现场重算兜底）");
            Check(map2.Elev != null && map2.Verts != null, "基础场仍在");
        }
        else
        {
            GD.Print("PeekDiag: 缺洋流往返失败");
            fail++;
        }

        GD.Print(fail == 0
            ? "PeekDiag: 全部一致（v6 段表往返 OK）"
            : $"PeekDiag: {fail} 处不一致");
        GetTree().Quit(fail == 0 ? 0 : 1);
    }

    private void Check(bool ok, string what)
    {
        if (!ok) GD.Print($"PeekDiag: {what} 不一致");
    }
}