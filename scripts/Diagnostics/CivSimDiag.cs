using Godot;
using System;
using World.CivSim;
using World.LogicGrid;
using World.MapGen;

namespace World.Diagnostics;

/// <summary>
/// 文明演化诊断：读 .mpa → GameGrid（自然层只读）→ 石器时代演化（seed 确定性）→ 写 .cmp
/// 游玩地图 → 读回校验（自然层零改动 + 文明层往返一致 + 同 seed 复现性）+ 演化统计。
///
/// 命令行：-- --arch=user://maps/xxx.mpa [--seed=N] [--origins=1..3] [--out=user://maps/xxx.cmp]
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
        GD.Print($"[CivSimDiag] 读档 {arch} n={n} → 石器时代演化（seed={seed} 起源{origins}，自然层只读）");

        // ── 1. 演化 + 复现性 ──
        var r1 = CivEngine.Run(grid, seed, origins);
        var r2 = CivEngine.Run(grid, seed, origins);   // 同 seed 复跑（确定性校验）
        bool reproducible = PopulationsEqual(r1.Context, r2.Context);

        // ── 2. 写 .cmp + 读回 ──
        bool ok = CivMapArchive.Write(outPath, grid, r1);
        if (!ok) { GetTree().Quit(1); return; }
        if (!CivMapArchive.Read(outPath, out var gridBack, out var rBack))
        { GetTree().Quit(1); return; }

        // ── 3. 校验：自然层零改动（.cmp 自然段 vs 源 grid）+ 文明层往返 ──
        bool natOk = NaturalUnchanged(grid, gridBack);
        bool civOk = CivRoundTrip(r1.Context, rBack.Context);

        // ── 4. 统计 ──
        int occupied = 0, land = 0;
        for (int i = 0; i < n; i++) { if (grid.IsLandCell(i)) land++; if (r1.Context.Cells[i].TribeId >= 0) occupied++; }

        // 可达性分析：从起源格 BFS 陆地连通分量（覆盖上限=起源大陆；孤立大陆无船不可达，物理正确）
        var visited = new bool[n];
        var queue = new System.Collections.Generic.Queue<int>();
        foreach (var t in r1.Context.Tribes) { if (!visited[t.OriginCell]) { visited[t.OriginCell] = true; queue.Enqueue(t.OriginCell); } }
        int reachable = 0;
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            reachable++;
            foreach (int nb in grid.Neighbors[cur])
            {
                if (!visited[nb] && grid.IsLandCell(nb)) { visited[nb] = true; queue.Enqueue(nb); }
            }
        }
        int unreachableLand = land - reachable;   // 孤立大陆（无船不可达）
        int cultureCount = 0;
        var culturePop = new System.Collections.Generic.Dictionary<byte, int>();
        int[] techDist = new int[4];
        float popMax = 0f; int popMaxCell = -1;
        for (int i = 0; i < n; i++)
        {
            var c = r1.Context.Cells[i];
            if (c.TribeId < 0) continue;
            if (!culturePop.ContainsKey(c.Culture)) { culturePop[c.Culture] = 0; cultureCount++; }
            culturePop[c.Culture]++;
            techDist[Mathf.Clamp(c.Tech, 0, 3)]++;
            if (c.Population > popMax) { popMax = c.Population; popMaxCell = i; }
        }
        // 起源格 → 覆盖（部落主格）
        var sb = new System.Text.StringBuilder("[CivSimDiag] 部落: ");
        for (int k = 0; k < Mathf.Min(r1.Context.Tribes.Count, 5); k++)
        {
            var t = r1.Context.Tribes[k];
            sb.Append($"#{t.Id}(起源{t.OriginCell}→主格{t.MainCell} 人口{t.Population:F0} 文化{t.Culture} 技术{t.Tech}) ");
        }
        if (r1.Context.Tribes.Count > 5) sb.Append($"…共{r1.Context.Tribes.Count}");

        GD.Print($"[CivSimDiag] 演化: {r1.FinalTick} tick × {r1.Context.Epoch.TickYears} 年 = {r1.FinalTick * r1.Context.Epoch.TickYears} 年" +
                 $" | 总人口 {r1.Context.TotalPopulation:F0} | 覆盖 {occupied}/{reachable} 可达陆地格 ({occupied * 100f / Math.Max(1, reachable):F0}%)" +
                 $" | 孤立大陆 {unreachableLand} 格（无船不可达）");
        GD.Print($"[CivSimDiag] 分布: 人口最高格 #{popMaxCell}={popMax:F0} | 文化区 {cultureCount} 个（最大 {MaxCulture(culturePop)} 格）" +
                 $" | 技术分布 石核{techDist[0]} 手斧{techDist[1]} 细石器{techDist[2]} 弓箭{techDist[3]}");
        GD.Print(sb.ToString());
        GD.Print($"[CivSimDiag] 校验: 自然层零改动={(natOk ? "PASS" : "FAIL")} 文明层往返={(civOk ? "PASS" : "FAIL")} " +
                 $"复现性(同seed两次一致)={(reproducible ? "PASS" : "FAIL")} → {(natOk && civOk && reproducible ? "全部PASS" : "有失败!")}");
        GD.Print($"[CivSimDiag] 导出 {(ok ? "成功" : "失败")} → {outPath}");
        GetTree().Quit(natOk && civOk && reproducible ? 0 : 1);
    }

    private static int MaxCulture(System.Collections.Generic.Dictionary<byte, int> d)
    {
        int m = 0;
        foreach (var kv in d) m = Math.Max(m, kv.Value);
        return m;
    }

    private static bool PopulationsEqual(CivSimContext a, CivSimContext b)
    {
        if (a.Cells.Length != b.Cells.Length) return false;
        for (int i = 0; i < a.Cells.Length; i++)
        {
            if (a.Cells[i].Population != b.Cells[i].Population) return false;
            if (a.Cells[i].Culture != b.Cells[i].Culture) return false;
            if (a.Cells[i].Tech != b.Cells[i].Tech) return false;
            if (a.Cells[i].TribeId != b.Cells[i].TribeId) return false;
        }
        return true;
    }

    /// <summary>自然层零改动：.cmp 读回的自然段与源 grid 逐字段一致（NaN 视为相等——往返位级一致）。</summary>
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

    /// <summary>文明层往返：写前 ctx 与读回 ctx 逐格一致。</summary>
    private static bool CivRoundTrip(CivSimContext a, CivSimContext b)
    {
        if (a.Cells.Length != b.Cells.Length) return false;
        for (int i = 0; i < a.Cells.Length; i++)
        {
            if (a.Cells[i].Population != b.Cells[i].Population) return false;
            if (a.Cells[i].Culture != b.Cells[i].Culture) return false;
            if (a.Cells[i].Tech != b.Cells[i].Tech) return false;
            if (a.Cells[i].TribeId != b.Cells[i].TribeId) return false;
        }
        if (a.Tribes.Count != b.Tribes.Count) return false;
        for (int k = 0; k < a.Tribes.Count; k++)
        {
            if (a.Tribes[k].Id != b.Tribes[k].Id) return false;
            if (a.Tribes[k].OriginCell != b.Tribes[k].OriginCell) return false;
            if (a.Tribes[k].MainCell != b.Tribes[k].MainCell) return false;
            if (a.Tribes[k].Culture != b.Tribes[k].Culture) return false;
            if (a.Tribes[k].Tech != b.Tribes[k].Tech) return false;
            if (a.Tribes[k].Population != b.Tribes[k].Population) return false;
        }
        return true;
    }
}
