using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using World.CivSim;
using World.CivSim.Entities;
using World.LogicGrid;
using World.Services;

namespace World.Diagnostics;

/// <summary>承载分析探针（2026-08-24）：读 .cmp → 陆地/R 分布/总承载 + 每实体 P/F/领地/R 均值，
/// 定位"人口只填了承载 10%"的根因（可居区比例？领地连通？F≈P 温饱稳态？）。
/// 用：godot --headless --path . res://scenes/diag/PopTraceDiag.tscn -- --map=user://maps/xxx.cmp</summary>
public partial class PopTraceDiag : DiagSceneBase
{
    public override void _Ready()
    {
        string path = "user://maps/fix_probe4.cmp";
        var args = ParseUserArgs();
        if (args.TryGetValue("map", out var mapArg)) path = mapArg;
        if (!CivMapArchive.Read(path, out var grid, out var res))
        {
            LogService.LogErr("PopTraceDiag", $"读取失败 {path}");
            GetTree().Quit(1);
            return;
        }
        var c = res.Context;
        var sb = new StringBuilder();
        sb.AppendLine($"[PopTraceDiag] {path} n={grid.N} 实体={c.Polities.Count} tick={res.FinalTick} 人口={c.TotalPopulation():F0}");

        // ── R 分布与承载 ──
        int land = 0, rPos = 0, r005 = 0, r01 = 0, r02 = 0, r05 = 0;
        double capSum = 0, capPos = 0;
        for (int i = 0; i < grid.N; i++)
        {
            if (!grid.IsLandCell(i)) continue;
            land++;
            float r = c.R[i];
            if (r > 0f) { rPos++; capPos += r; }
            if (r > 0.05f) r005++;
            if (r > 0.1f) r01++;
            if (r > 0.2f) r02++;
            if (r > 0.5f) r05++;
            capSum += r;
        }
        double A = grid.CellAreaKm2;
        sb.AppendLine($"  陆地 {land} 格 × {A:F1}km² = {land * A:F0} km²");
        sb.AppendLine($"  R>0 可居 {rPos} 格（{100f*rPos/land:F1}%） | R>0.05 {r005} | R>0.1 {r01} | R>0.2 {r02} | R>0.5 {r05}");
        sb.AppendLine($"  总承载 ΣR×A = {capSum * A:F0} 人（R 均值 {capSum/land:F3}）| 可居承载 {capPos * A:F0} 人 | 人口填充率 {c.TotalPopulation() / Math.Max(1e-3, capPos * A) * 100:F1}%");

        // ── 每实体：P/F/领地格/领地 R 均值/自属 ──
        var pops = new List<float>(); var fs = new List<float>();
        foreach (var e in c.Polities)
        {
            if (e.Dead) continue;
            var terr = c.TerritoryOf(e);
            int tc = terr?.Count ?? 0;
            double terrSum = 0; int terrR = 0;
            if (terr != null) foreach (int g in terr) if (c.R[g] > 0f) { terrSum += c.R[g]; terrR++; }
            double terrAvg = terrR > 0 ? terrSum / terrR : 0;
            bool self = c.CellOwner != null && c.CellOwner[e.Cell] == e.Id;
            pops.Add(e.P); fs.Add(e.FLast);
            sb.AppendLine($"  e{e.Id} P={e.P:F1} F={e.FLast:F1} 领地={tc} 领地R均值={terrAvg:F3} 驻扎R={c.R[e.Cell]:F3} 自属={self} 农={e.IsFarming} 酋邦={e.ChiefdomId}");
        }
        pops.Sort(); fs.Sort();
        float Med(List<float> a) => a.Count == 0 ? 0f : a[a.Count / 2];
        sb.AppendLine($"  P 中位 {Med(pops):F1} 均 {c.TotalPopulation()/Math.Max(1,c.Polities.Count):F1} | FLast 中位 {Med(fs):F1}");
        int fLt = 0, fZero = 0;
        foreach (var e in c.Polities)
        {
            if (e.Dead) continue;
            if (e.FLast <= 0f) fZero++;
            else if (e.FLast < e.P) fLt++;
        }
        sb.AppendLine($"  F=0 冻结 {fZero} | F<P 挨饿 {fLt}");
        GD.Print(sb.ToString());
        GetTree().Quit(0);
    }
}