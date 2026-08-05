using Godot;
using System;
using System.Collections.Generic;
using World.CivSim;
using World.LogicGrid;
using World.MapGen;

namespace World.Diagnostics;

/// <summary>
/// 文明演化诊断（v2 部落模型）：读 .mpa → GameGrid（自然层只读）→ 演化（seed 确定性）→ 写 .cmp
/// → 读回校验（自然层零改动 + 部落表往返 + 同 seed 复现性）+ 演化统计（部落动态/技术分布/农业起源）。
///
/// 命令行：-- --arch=user://maps/xxx.mpa [--seed=N] [--origins=1..6] [--out=user://maps/xxx.cmp]
/// </summary>
public partial class CivSimDiag : Node
{
    public override void _Ready()
    {
        string arch = ArchiveDiag.ResolveArchPath();
        if (arch == null)
        {
            GD.PrintErr("[CivSimDiag] 需要 --arch=user://maps/xxx.mpa（文明演化是存档直读工具）");
            GetTree().Quit(1);
            return;
        }
        if (!ArchiveDiag.TryLoad(arch, out var ctx))
        {
            GetTree().Quit(1);
            return;
        }

        int seed = 42, origins = 3;
        string outPath = arch.GetBaseName() + ".cmp";
        var ua = OS.GetCmdlineUserArgs();
        for (int i = 0; i < ua.Length; i++)
        {
            string a = ua[i];
            string v = a.StartsWith("--") ? a.Substring(2) : a;
            if (v.StartsWith("seed=", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.Substring(5), out int s)) seed = s;
            else if (v.StartsWith("origins=", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.Substring(8), out int o)) origins = Mathf.Clamp(o, 1, 6);
            else if (v.StartsWith("out=", StringComparison.OrdinalIgnoreCase)) outPath = v.Substring(4);
        }

        var grid = GameGrid.FromMapData(ctx.Map);
        int n = grid.N;
        GD.Print($"[CivSimDiag] 读档 {arch} n={n} → 文明演化（seed={seed} 起源{origins}，部落=格内单元模型，自然层只读）");

        // ── 1. 演化 + 复现性 ──
        var r1 = CivEngine.Run(grid, seed, origins);
        var r2 = CivEngine.Run(grid, seed, origins);
        bool reproducible = TribesEqual(r1.Context, r2.Context);

        // ── 2. 写 .cmp + 读回 ──
        bool ok = CivMapArchive.Write(outPath, grid, r1);
        if (!ok) { GetTree().Quit(1); return; }
        if (!CivMapArchive.Read(outPath, out var gridBack, out var rBack))
        { GetTree().Quit(1); return; }

        // ── 3. 校验 ──
        bool natOk = NaturalUnchanged(grid, gridBack);
        bool rtOk = TribesEqual(r1.Context, rBack.Context);
        bool agriRepro = AgricultureCount(r1.Context) == AgricultureCount(r2.Context);

        // ── 4. 统计 ──
        var c = r1.Context;
        int occupied = 0, land = 0;
        var cultureCells = new Dictionary<byte, int>();
        var techEpochTribes = new int[5];
        int agriTribes = 0, maxTechTribes = 0;
        float popMax = 0f; int popMaxCell = -1;
        for (int i = 0; i < n; i++)
        {
            if (grid.IsLandCell(i)) land++;
            if (c.CellPop[i] > 0f)
            {
                occupied++;
                if (c.CellPop[i] > popMax) { popMax = c.CellPop[i]; popMaxCell = i; }
            }
        }
        foreach (var t in c.Tribes)
        {
            techEpochTribes[Mathf.Clamp(TechTable.MaxEpoch(t.TechFlags), 0, 4)]++;
            if (TechTable.Has(t.TechFlags, 7)) agriTribes++;
        }

        // 部落规模分布
        float popSum = 0f; int popMin = int.MaxValue, popMaxT = 0;
        foreach (var t in c.Tribes)
        {
            popSum += t.Population;
            popMin = Mathf.Min(popMin, (int)t.Population);
            popMaxT = Mathf.Max(popMaxT, (int)t.Population);
        }
        float meanPop = c.Tribes.Count > 0 ? popSum / c.Tribes.Count : 0f;

        // 技术分布明细（各技术持有部落数）
        var techDist = new int[TechTable.Count];
        foreach (var t in c.Tribes)
            for (int i = 0; i < TechTable.Count; i++)
                if (TechTable.Has(t.TechFlags, i)) techDist[i]++;
        var techSb = new System.Text.StringBuilder("[CivSimDiag] 技术分布: ");
        for (int i = 0; i < TechTable.Count; i++)
            if (techDist[i] > 0) techSb.Append($"{TechTable.All[i].Name}{techDist[i]} ");

        GD.Print($"[CivSimDiag] 演化: {r1.FinalTick} tick × {c.Epoch.TickYears} 年 = {r1.FinalTick * c.Epoch.TickYears} 年" +
                 $" | 总人口 {c.TotalPopulation():F0} | 部落 {c.Tribes.Count}（规模 {popMin}~{popMaxT} 均值 {meanPop:F0}）" +
                 $" | 覆盖 {occupied}/{land} 陆地格");
        GD.Print($"[CivSimDiag] 事件: 分裂 {c.Fissions} | 吞并 {c.Absorptions} | 和平合并 {c.Merges} | 迁徙 {c.Migrations} | 贸易接触 {c.TradeContacts}");
        GD.Print($"[CivSimDiag] 时代分布(部落): 石器{techEpochTribes[0]} 新石器{techEpochTribes[1]} 青铜{techEpochTribes[2]} 铁器{techEpochTribes[3]} 古典+{techEpochTribes[4]} | 农业部落 {agriTribes}");
        GD.Print(techSb.ToString());

        // 宗教分布 + 文化群
        var relNames = new[] { "万物有灵", "萨满图腾", "祖先崇拜", "多神教", "一神教" };
        var relDist = new int[5];
        var cultureGroups = new HashSet<byte>();
        foreach (var t in c.Tribes)
        {
            relDist[Mathf.Clamp(t.Religion, 0, 4)]++;
            cultureGroups.Add(t.CultureGroup);
        }
        GD.Print($"[CivSimDiag] 宗教分布: 万物有灵{relDist[0]} 萨满图腾{relDist[1]} 祖先崇拜{relDist[2]} 多神教{relDist[3]} 一神教{relDist[4]} | 文化群 {cultureGroups.Count} 个");
        GD.Print($"[CivSimDiag] 校验: 自然层零改动={(natOk ? "PASS" : "FAIL")} 部落表往返={(rtOk ? "PASS" : "FAIL")} " +
                 $"复现性={(reproducible ? "PASS" : "FAIL")} 农业复现={(agriRepro ? "PASS" : "FAIL")} → {(natOk && rtOk && reproducible && agriRepro ? "全部PASS" : "有失败!")}");
        GD.Print($"[CivSimDiag] 导出 {(ok ? "成功" : "失败")} → {outPath}");
        GetTree().Quit(natOk && rtOk && reproducible && agriRepro ? 0 : 1);
    }

    private static int AgricultureCount(CivSimContext c)
    {
        int a = 0;
        foreach (var t in c.Tribes) if (TechTable.Has(t.TechFlags, 7)) a++;
        return a;
    }

    /// <summary>部落表逐项一致（人口/格/文化/技术/起源）。</summary>
    private static bool TribesEqual(CivSimContext a, CivSimContext b)
    {
        if (a.Tribes.Count != b.Tribes.Count) return false;
        for (int k = 0; k < a.Tribes.Count; k++)
        {
            var x = a.Tribes[k]; var y = b.Tribes[k];
            if (x.Cell != y.Cell || x.Population != y.Population || x.Culture != y.Culture
                || x.TechFlags != y.TechFlags || x.OriginCell != y.OriginCell) return false;
        }
        return true;
    }

    /// <summary>自然层零改动：.cmp 读回的自然段与源 grid 逐字段一致（NaN 视为相等）。</summary>
    private static bool NaturalUnchanged(GameGrid a, GameGrid b)
    {
        if (a.N != b.N) return false;
        for (int i = 0; i < a.N; i++)
        {
            if (!FloatEq(a.Elev[i], b.Elev[i]) || !FloatEq(a.Temp[i], b.Temp[i]) || !FloatEq(a.Precip[i], b.Precip[i])) return false;
            if (a.Biome[i] != b.Biome[i] || a.RiverLevel[i] != b.RiverLevel[i] || a.LakeLevel[i] != b.LakeLevel[i]) return false;
            if (a.MineralLevel[i] != b.MineralLevel[i] || a.SoilLevel[i] != b.SoilLevel[i]) return false;
        }
        return true;
    }

    private static bool FloatEq(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));
}
