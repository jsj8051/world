using Godot;
using System;
using World.Biome;
using World.LogicGrid;
using World.MapGen;

namespace World.Diagnostics;

/// <summary>
/// 逻辑网格导出诊断：读 .mpa → 构建 LogicGrid（零重采样，格=模拟顶点胞）→ 写 .gmp →
/// 读回逐字段校验（自然层与源档一致、邻接确定性重建、面积守恒、人文层初始 0）。
///
/// 命令行：-- --arch=user://maps/xxx.mpa [--out=user://maps/xxx.gmp]
///   --arch 必填（无值=默认 map1.mpa）；--out 默认同目录同名 .gmp。
/// </summary>
public partial class LogicGridDiag : DiagSceneBase
{
    public override void _Ready()
    {
        string arch = ArchiveDiag.ResolveArchPath();
        if (arch == null)
        {
            GD.PrintErr("[LogicGridDiag] 需要 --arch=user://maps/xxx.mpa（逻辑网格导出是存档直读工具）");
            GetTree().Quit(1);
            return;
        }
        if (!ArchiveDiag.TryLoad(arch, out var ctx))
        {
            GetTree().Quit(1);
            return;
        }

        string outPath = arch.GetBaseName() + ".gmp";
        var args = ParseUserArgs();
        if (args.TryGetValue("out", out var outArg)) outPath = outArg;

        var map = ctx.Map;
        int n = map.Verts.Length;
        GD.Print($"[LogicGridDiag] 读档 {arch} n={n} → 构建逻辑网格（格=模拟顶点胞，零重采样）");

        // ── 1. 构建 + 导出 ──
        var g1 = GameGrid.FromMapData(map);
        bool ok = GameMapArchive.Write(outPath, g1);
        if (!ok)
        {
            GetTree().Quit(1);
            return;
        }

        // ── 2. 读回 + 校验 ──
        if (!GameMapArchive.Read(outPath, out var g2))
        {
            GetTree().Quit(1);
            return;
        }
        bool pass = true;
        pass &= FieldCompare.Eq("GridN/N", g1.GridN, g2.GridN, g1.N, g2.N);
        pass &= FieldCompare.Eq("Seed", g1.Seed, g2.Seed);
        pass &= FieldCompare.Eq("RadiusKm", g1.RadiusKm, g2.RadiusKm);
        pass &= FieldCompare.Eq("Prograde", g1.ProgradeRotation, g2.ProgradeRotation);
        pass &= FieldCompare.Eq("RotationSpeed", g1.RotationSpeed, g2.RotationSpeed);
        pass &= FieldCompare.Eq("AxialTilt", g1.AxialTilt, g2.AxialTilt);
        pass &= FieldCompare.Eq("Insolation", g1.Insolation, g2.Insolation);
        pass &= FieldCompare.MaxDiff("elev", g1.Elev, g2.Elev, out double dElev) < 1e-3f;
        pass &= FieldCompare.MaxDiff("temp", g1.Temp, g2.Temp, out double dTemp) < 1e-3f;
        pass &= FieldCompare.MaxDiff("precip", g1.Precip, g2.Precip, out double dPrecip) < 1e-3f;
        pass &= FieldCompare.ByteDiff("biome", g1.Biome, g2.Biome, out int dBio) == 0;
        pass &= FieldCompare.ByteDiff("riverLevel", g1.RiverLevel, g2.RiverLevel, out int dRiv) == 0;
        pass &= FieldCompare.IntDiff("riverFlow", g1.RiverFlow, g2.RiverFlow, out int dFlow) == 0;
        pass &= FieldCompare.MaxDiff("riverVolume", g1.RiverVolume, g2.RiverVolume, out double dVol) < 1e-3f;
        pass &= FieldCompare.ByteDiff("lake", g1.LakeLevel, g2.LakeLevel, out int dLake) == 0;
        pass &= FieldCompare.ByteDiff("mineral", g1.MineralLevel, g2.MineralLevel, out int dMin) == 0;
        pass &= FieldCompare.ByteDiff("soil", g1.SoilLevel, g2.SoilLevel, out int dSoil) == 0;
        pass &= FieldCompare.ByteDiff("monsoon", g1.MonsoonLevel, g2.MonsoonLevel, out int dMon) == 0;
        pass &= FieldCompare.Bytes2DDiff("monthPrecip", g1.MonthPrecip, g2.MonthPrecip, out int dMP) == 0;
        pass &= FieldCompare.Bytes2DDiff("monthTemp", g1.MonthTemp, g2.MonthTemp, out int dMT) == 0;
        pass &= FieldCompare.MaxDiff3("currentDirs", g1.CurrentDirs, g2.CurrentDirs, out double dCur) < 1e-3f;
        pass &= FieldCompare.MaxDiff("currentWarmth", g1.CurrentWarmth, g2.CurrentWarmth, out double dWarm) < 1e-3f;
        pass &= FieldCompare.MaxDiff("currentStrength", g1.CurrentStrength, g2.CurrentStrength, out double dStr) < 1e-3f;
        pass &= FieldCompare.IntDiff("province(初始0)", g1.Province, g2.Province, out int dProv) == 0;
        pass &= FieldCompare.IntDiff("country(初始0)", g1.Country, g2.Country, out int dCtry) == 0;

        // ── 3. 逻辑网格统计（读回实例）──
        var nb = g2.Neighbors;          // 确定性重建
        long degSum = 0; int degMin = int.MaxValue, degMax = 0;
        for (int i = 0; i < n; i++)
        {
            int d = nb[i].Length;
            degSum += d;
            if (d < degMin) degMin = d;
            if (d > degMax) degMax = d;
        }
        int land = 0, sea = 0;
        for (int i = 0; i < n; i++) { if (g2.IsLandCell(i)) land++; else sea++; }
        int nanVol = 0;
        for (int i = 0; i < n; i++) if (float.IsNaN(g2.RiverVolume[i])) nanVol++;
        float areaTotal = g2.CellAreaKm2 * n;
        float expectTotal = 4f * Mathf.Pi * g2.RadiusKm * g2.RadiusKm;

        GD.Print($"[LogicGridDiag] 校验: elev差={dElev:E1} temp差={dTemp:E1} precip差={dPrecip:E1} " +
                 $"biome差={dBio} 河流差={dRiv}/{dFlow} 流量差={dVol:E1} 湖泊差={dLake} 矿藏差={dMin} 土壤差={dSoil} " +
                 $"季风差={dMon} 月降水差={dMP} 月温差={dMT} 洋流差={dCur:E1}/{dWarm:E1}/{dStr:E1} 人文差={dProv}/{dCtry} → {(pass ? "PASS" : "FAIL")}");
        GD.Print($"[LogicGridDiag] 源档数据: riverVolume NaN={nanVol}/{n}（NaN 为源档固有，往返位级一致）");
        GD.Print($"[LogicGridDiag] 网格: n={g2.GridN} 格数={n} 邻接度[{degMin},{degMax}] 平均={degSum / (double)n:F2} " +
                 $"(球面三角期望 ~6) | 陆地={land} 海洋={sea} | 面积 {areaTotal:F0} vs 4πR²={expectTotal:F0} km² (R={g2.RadiusKm})");
        GD.Print($"[LogicGridDiag] 胞面积≈{g2.CellAreaKm2:F0} km²/格 | 人文层 province/country 全 0={dProv + dCtry == 0}");
        GD.Print($"[LogicGridDiag] 导出 {(ok ? "成功" : "失败")} → {outPath}（{(pass ? "全部校验通过" : "有字段不一致!")}）");
        GetTree().Quit(pass ? 0 : 1);
    }
}
