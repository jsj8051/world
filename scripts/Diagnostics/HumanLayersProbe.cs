using Godot;
using System;
using System.Collections.Generic;
using World.CivSim;
using World.LogicGrid;
using World.MapView;
using World.Services;

using World.CivSim.Entities;
namespace World.Diagnostics;

/// <summary>人文图层探针（2026-08-19 临时）：读 .cmp → 统计 文化/文化群/宗教派别/领地 分布
/// （实体数 / 归属格数 / 人口）+ 导出等距柱状 文化/宗教 图（归属格主导语义，同 MapViewer 显示）
/// + Axelrod 互动死代码实测（同文化对是否污染份额场 / 异文化对是否传播）。
/// 用：godot --headless --path . res://scenes/diag/HumanLayersProbe.tscn -- --map=user://maps/xxx.cmp
/// 日志重定向 logs\probes\。</summary>
public partial class HumanLayersProbe : DiagSceneBase
{
    private static readonly Color SeaColor = new(0.10f, 0.25f, 0.45f);

    public override void _Ready()
    {
        string path = "user://maps/map_seed42_n128.cmp";
        var args = ParseUserArgs();
        if (args.TryGetValue("map", out var mapArg)) path = mapArg;
        if (!CivMapArchive.Read(path, out var grid, out var res))
        {
            LogService.LogErr("HumanLayersProbe", $"读取失败 {path}");
            GetTree().Quit(1);
            return;
        }
        var ctx = res.Context;
        LogService.Log("HumanLayersProbe", $"{path} n={grid.N} 实体={ctx.Polities.Count} tick={res.FinalTick} 人口={ctx.TotalPopulation():F0}");

        var ownerCells = new Dictionary<int, int>();
        if (ctx.CellOwner != null)
            for (int c = 0; c < grid.N; c++)
            {
                int o = ctx.CellOwner[c];
                if (o >= 0) ownerCells[o] = ownerCells.TryGetValue(o, out var v) ? v + 1 : 1;
            }

        DumpLayer(ctx, ownerCells, "文化", e => ShareField.DomKey(e.CultureShare));
        DumpLayer(ctx, ownerCells, "文化群", e => ShareField.DomKey(e.CultureGroupShare));
        DumpLayer(ctx, ownerCells, "宗教派别", e => ShareField.DomKey(e.ReligionCultShare));
        DumpDisplayStats(ctx, grid.N);
        DumpEnclaveStats(ctx, grid.N);
        DumpPolitySizes(ctx);
        DumpModeStocks(ctx);
        DumpHabitations(ctx);
        DumpPowerColorCheck(ctx, grid.N);
        DumpTerritoryColorCheck(ctx, grid.N);
        DumpStateStats(ctx);
        DumpWarStats(ctx);
        DumpExpansionStats(ctx, grid);

        var terrPolities = new Dictionary<int, int>();
        foreach (var e in ctx.Polities)
        {
            if (e.Dead) continue;
            terrPolities[e.TerritoryId] = terrPolities.TryGetValue(e.TerritoryId, out var v) ? v + 1 : 1;
        }
        var terrList = new List<KeyValuePair<int, int>>(terrPolities);
        terrList.Sort((x, y) => y.Value.CompareTo(x.Value));
        var tSb = new System.Text.StringBuilder();
        foreach (var kv in terrList) tSb.Append($"{kv.Key}({kv.Value}b) ");
        LogService.Log("HumanLayersProbe", $"领地（{terrList.Count} 个）: {tSb}");

        ExportMap(grid, ctx, "culture", e => ShareField.KeyHash(ShareField.DomKey(e.CultureShare)));
        ExportMap(grid, ctx, "religion", e => ShareField.KeyHash(ShareField.DomKey(e.ReligionCultShare)));
        GetTree().Quit(0);
    }

    private sealed class Agg { public int Ent; public int Cells; public float Pop; }

    /// <summary>新显示语义统计（2026-08-19 终版：人文图层全部按归属者身份着色，定居格仅亮度强调）。
    /// 报告归属格分布（= 地图实际显示的文化分布）；定居/领地只差明度。</summary>
    private void DumpDisplayStats(CivSimContext ctx, int n)
    {
        var byId = new Dictionary<int, Polity>();
        foreach (var e in ctx.Polities) if (!e.Dead) byId[e.Id] = e;
        var cultCells = new Dictionary<string, int>();
        int owned = 0;
        for (int v = 0; v < n; v++)
        {
            if (ctx.R != null && ctx.R[v] <= 0f) continue;
            int owner = ctx.CellOwner != null && v < ctx.CellOwner.Length ? ctx.CellOwner[v] : -1;
            if (owner < 0 || !byId.TryGetValue(owner, out var o)) continue;
            owned++;
            string k = ShareField.DomKey(o.CultureShare) ?? "无";
            cultCells[k] = cultCells.TryGetValue(k, out var c) ? c + 1 : 1;
        }
        LogService.Log("HumanLayersProbe", $"显示统计（归属者统一）: 归属格={owned} | 文化数={cultCells.Count} | 分布: {JoinTop(cultCells)}");
    }

    private static string JoinTop(Dictionary<string, int> d)
    {
        var list = new List<KeyValuePair<string, int>>(d);
        list.Sort((x, y) => y.Value.CompareTo(x.Value));
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(8, list.Count); i++)
            sb.Append($"{list[i].Key}:{list[i].Value}格 ");
        return sb.ToString();
    }

    /// <summary>飞地诊断（2026-08-19 用户反馈"大量飞地"）：
    /// ① 定居格自属/他属（CellOwner == 定居部落？他属 = 显示上与领地底色可能冲突的点）；
    /// ② 他属定居格中 文化/势力 与归属者不同的数量（= 视觉飞地点）；
    /// ③ 文化图层最大连通同色区域（BFS，显示色 = 定居自身 or 归属者）——衡量地图碎成孤岛 vs 存在连贯区域。</summary>
    private void DumpEnclaveStats(CivSimContext ctx, int n)
    {
        var bestByCell = new Dictionary<int, Polity>();
        foreach (var e in ctx.Polities)
        {
            if (e.Dead || e.Cell < 0 || e.Cell >= n) continue;
            if (ctx.R != null && ctx.R[e.Cell] <= 0f) continue;
            if (!bestByCell.TryGetValue(e.Cell, out var cur) || e.P > cur.P) bestByCell[e.Cell] = e;
        }
        var byId = new Dictionary<int, Polity>();
        foreach (var e in ctx.Polities) if (!e.Dead) byId[e.Id] = e;
        int land = 0, owned = 0, selfOwned = 0, foreignOwned = 0, cultDiff = 0, powerDiff = 0;
        for (int v = 0; v < n; v++)
        {
            if (ctx.R != null && ctx.R[v] <= 0f) continue;
            land++;
            int owner = ctx.CellOwner != null && v < ctx.CellOwner.Length ? ctx.CellOwner[v] : -1;
            if (owner < 0) continue;
            owned++;
            if (!bestByCell.TryGetValue(v, out var se)) continue;
            if (owner == se.Id) { selfOwned++; continue; }
            foreignOwned++;
            if (byId.TryGetValue(owner, out var o))
            {
                if (ShareField.DomKey(o.CultureShare) != ShareField.DomKey(se.CultureShare)) cultDiff++;
                if (PowerKey(o) != PowerKey(se)) powerDiff++;
            }
        }
        LogService.Log("HumanLayersProbe", $"飞地诊断: 陆地格={land} 归属格={owned} | 定居自属={selfOwned} 他属={foreignOwned} | 他属中 文化异={cultDiff} 势力异={powerDiff}（他属+异 = 视觉飞地点）");
        // ③ 文化图层最大连通同色区域（显示色 = 定居自身 or 归属者；确定性 BFS）
        int[] colorOf = new int[n];
        for (int v = 0; v < n; v++)
        {
            if (ctx.R != null && ctx.R[v] <= 0f) continue;
            int owner = ctx.CellOwner != null && v < ctx.CellOwner.Length ? ctx.CellOwner[v] : -1;
            if (owner < 0 || !byId.TryGetValue(owner, out var o)) continue;
            colorOf[v] = bestByCell.TryGetValue(v, out var se)
                ? ShareField.KeyHash(ShareField.DomKey(se.CultureShare))
                : ShareField.KeyHash(ShareField.DomKey(o.CultureShare));
        }
        var compMax = new Dictionary<int, int>();   // 色 hash -> 最大连通块格数
        var compCount = new Dictionary<int, int>(); // 色 hash -> 连通块数
        var visited = new int[n];
        int stamp = 0;
        var q = new Queue<int>();
        for (int s = 0; s < n; s++)
        {
            if (colorOf[s] == 0 || visited[s] != 0) continue;
            stamp++;
            visited[s] = stamp;
            q.Enqueue(s);
            int size = 0, color = colorOf[s];
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                size++;
                foreach (int nb in ctx.Grid.Neighbors[c])
                {
                    if (colorOf[nb] != color || visited[nb] != 0) continue;
                    visited[nb] = stamp;
                    q.Enqueue(nb);
                }
            }
            compMax[color] = Math.Max(compMax.TryGetValue(color, out var m) ? m : 0, size);
            compCount[color] = compCount.TryGetValue(color, out var ct) ? ct + 1 : 1;
        }
        var compList = new List<KeyValuePair<int, int>>(compMax);
        compList.Sort((x, y) => y.Value.CompareTo(x.Value));
        var sb = new System.Text.StringBuilder();
        foreach (var kv in compList)
            sb.Append($"色{kv.Key}块{kv.Value}格({compCount[kv.Key]}块) ");
        LogService.Log("HumanLayersProbe", $"文化连通块: 色数={compMax.Count} | 前8最大块: {sb}");
        // ④ 势力层连通性（显示色 = 定居自身 PowerKey or 归属者 PowerKey）
        int[] powOf = new int[n];
        for (int v = 0; v < n; v++)
        {
            if (ctx.R != null && ctx.R[v] <= 0f) continue;
            int owner = ctx.CellOwner != null && v < ctx.CellOwner.Length ? ctx.CellOwner[v] : -1;
            if (owner < 0 || !byId.TryGetValue(owner, out var o)) continue;
            powOf[v] = bestByCell.TryGetValue(v, out var se) ? PowerKey(se).GetHashCode() : PowerKey(o).GetHashCode();
        }
        var (pMax, pCount) = BiggestComponents(powOf, n, ctx.Grid.Neighbors);
        LogService.Log("HumanLayersProbe", $"势力连通块: 色数={pMax.Count} | 前5最大块: {FormatComps(pMax, pCount)}");
        // ⑤ 语言群层连通性（归属者群）
        int[] grpOf = new int[n];
        for (int v = 0; v < n; v++)
        {
            if (ctx.R != null && ctx.R[v] <= 0f) continue;
            int owner = ctx.CellOwner != null && v < ctx.CellOwner.Length ? ctx.CellOwner[v] : -1;
            if (owner < 0 || !byId.TryGetValue(owner, out var o)) continue;
            grpOf[v] = ShareField.KeyHash(ShareField.DomKey(o.CultureGroupShare));
        }
        var (gMax, gCount) = BiggestComponents(grpOf, n, ctx.Grid.Neighbors);
        LogService.Log("HumanLayersProbe", $"语言群连通块: 色数={gMax.Count} | 前5最大块: {FormatComps(gMax, gCount)}");
    }

    private static string FormatComps(Dictionary<int, int> compMax, Dictionary<int, int> compCount)
    {
        var list = new List<KeyValuePair<int, int>>(compMax);
        list.Sort((x, y) => y.Value.CompareTo(x.Value));
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(5, list.Count); i++)
            sb.Append($"色{list[i].Key}块{list[i].Value}格({compCount[list[i].Key]}块) ");
        return sb.ToString();
    }

    private static (Dictionary<int, int>, Dictionary<int, int>) BiggestComponents(int[] colorOf, int n, int[][] neighbors)
    {
        var compMax = new Dictionary<int, int>();
        var compCount = new Dictionary<int, int>();
        var visited = new int[n];
        int stamp = 0;
        var q = new Queue<int>();
        for (int s = 0; s < n; s++)
        {
            if (colorOf[s] == 0 || visited[s] != 0) continue;
            stamp++;
            visited[s] = stamp;
            q.Enqueue(s);
            int size = 0, color = colorOf[s];
            while (q.Count > 0)
            {
                int c = q.Dequeue();
                size++;
                foreach (int nb in neighbors[c])
                {
                    if (colorOf[nb] != color || visited[nb] != 0) continue;
                    visited[nb] = stamp;
                    q.Enqueue(nb);
                }
            }
            compMax[color] = Math.Max(compMax.TryGetValue(color, out var m) ? m : 0, size);
            compCount[color] = compCount.TryGetValue(color, out var ct) ? ct + 1 : 1;
        }
        return (compMax, compCount);
    }

    private static string PowerKey(Polity e)
    {
        if (e.ChiefdomId >= 0) return "C" + e.ChiefdomId;
        if (e.TerritorySize >= 2) return "T" + e.TerritoryId;
        return "B" + e.Id;
    }

    private static int PowerIdOf(Polity e)
    {
        if (e.ChiefdomId >= 0) return unchecked((int)0x80000000) | (e.ChiefdomId & 0x3FFFFFFF);
        if (e.TerritorySize >= 2) return unchecked((int)0x40000000) | (e.TerritoryId & 0x3FFFFFFF);
        return unchecked((int)0x20000000) | (e.Id & 0x3FFFFFFF);
    }

    /// <summary>国家统计（2026-08-16 阶段4 国家涌现，docs/阶段4设计-国家涌现.md）：国家数/人口/成员/
    /// 都城等级分布——演化级观测（T64-T67 构造级验证机制，本探针看涌现画像）。
    /// ⚠️ 诊断模式：对每个 ≥2 成员酋邦打印未达标条件（都城/层级/贡赋/存续）——定位阈值校准。</summary>
    private void DumpStateStats(CivSimContext ctx)
    {
        var byState = new Dictionary<int, (int Polities, float Pop)>();
        var capitals = new Dictionary<int, int>();   // stateId → 都城 Level
        foreach (var e in ctx.Polities)
        {
            if (e.Dead || e.StateId < 0) continue;
            if (!byState.TryGetValue(e.StateId, out var agg)) agg = (0, 0f);
            agg.Polities++;
            agg.Pop += e.P;
            byState[e.StateId] = agg;
            if (e.IsChief && e.ChiefdomId == e.Id)
            {
                var st = ctx.HabitationOf(e);
                if (st != null) capitals[e.StateId] = st.Level;
            }
        }
        var list = new List<KeyValuePair<int, (int Polities, float Pop)>>(byState);
        list.Sort((x, y) => y.Value.Pop.CompareTo(x.Value.Pop));
        var sb = new System.Text.StringBuilder();
        int capSum = 0;
        foreach (var kv in list)
        {
            int capLvl = capitals.TryGetValue(kv.Key, out var l) ? l : -1;
            if (capLvl >= 0) capSum++;
            sb.Append($"国家{kv.Key}:{kv.Value.Polities}b/{kv.Value.Pop:F0}p/都城L{capLvl} ");
        }
        LogService.Log("HumanLayersProbe", $"国家统计: {list.Count} 个国家（{capSum} 有都城）| {sb}");
        // 诊断：按酋邦打印未达标条件（前 10 大酋邦）
        var diag = new List<(int Chief, int Polities, float Pop, int CapLvl, float Pool, float Need, int Settles, bool Sub, int Dwell)>();
        var byId = new Dictionary<int, Polity>();
        foreach (var e in ctx.Polities) if (!e.Dead) byId[e.Id] = e;
        var groups = new Dictionary<int, List<Polity>>();
        foreach (var e in ctx.Polities)
            if (!e.Dead && e.ChiefdomId >= 0)
            {
                if (!groups.TryGetValue(e.ChiefdomId, out var l)) groups[e.ChiefdomId] = l = new List<Polity>();
                l.Add(e);
            }
        foreach (var kv in groups)
        {
            var members = kv.Value;
            if (members.Count < 2) continue;
            Polity chief = null;
            foreach (var m in members) if (m.IsChief && m.ChiefdomId == m.Id) { chief = m; break; }
            if (chief == null) { diag.Add((kv.Key, members.Count, 0f, -1, 0f, 0f, 0, false, -1)); continue; }
            var cap = ctx.HabitationOf(chief);
            float pop = 0f, pool = 0f;
            int settles = 0; bool sub = false;
            foreach (var m in members)
            {
                pop += m.P; pool += m.Contributed;
                var st = ctx.HabitationOf(m);
                if (st != null && st.OccupantId == m.Id)
                {
                    settles++;
                    if (cap != null && st.Id != cap.Id && st.Level >= CivSimContext.StateSubCenterLevel) sub = true;
                }
            }
            int dwell = cap != null ? ctx.Tick - cap.BornTick : -1;
            diag.Add((kv.Key, members.Count, pop, cap != null ? cap.Level : -1, pool, pop * CivSimContext.StateTributePerCap, settles, sub, dwell));
        }
        diag.Sort((x, y) => y.Pop.CompareTo(x.Pop));
        var dSb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(10, diag.Count); i++)
        {
            var d = diag[i];
            dSb.Append($"邦{d.Chief}:{d.Polities}b/{d.Pop:F0}p 都城L{d.CapLvl} 池{d.Pool:F0}/需{d.Need:F0} 聚落{d.Settles} 次级{d.Sub} 存续{d.Dwell} | ");
        }
        LogService.Log("HumanLayersProbe", $"国家诊断(前10大酋邦): {dSb}");
    }

    /// <summary>战争统计（2026-08-19 阶段5）：宣战/吞并/朝贡场次 + 进行中的战争明细。</summary>
    private void DumpWarStats(CivSimContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var w in ctx.Wars)
        {
            if (w.IsTribute)
                sb.Append($"朝贡:{w.TributeFrom}→{w.TributeTo} 剩{w.TributesLeft}tick ");
            else
                sb.Append($"交战:{w.StateIdA}vs{w.StateIdB} 胜{w.WinsA}:{w.WinsB} 起{w.StartTick} ");
        }
        LogService.Log("HumanLayersProbe", $"战争统计: 进行中={ctx.Wars.Count}（累计宣战{ctx.WarsDeclared} 吞并{ctx.WarsAnnexed}）| {sb}");
    }

    /// <summary>扩张诊断（2026-08-19）：实体按格 R 分桶 + 领地格数分布 + 归属格 R 分桶——
    /// 验证"殖民扩散后贫瘠区能否活人"（领地大小/承载是否足够）。</summary>
    private void DumpExpansionStats(CivSimContext ctx, GameGrid grid)
    {
        int n = grid.N;
        var terrHist = new int[6];   // 领地格数：1 / 2-3 / 4-7 / 8-19 / 20-59 / 60+
        var popByR = new long[5];    // 实体人口按格 R 分桶：<0.02 / <0.05 / <0.1 / <0.2 / ≥0.2
        var entByR = new int[5];
        var ownedByR = new int[5];   // 归属格按 R 分桶
        float[] rBins = { 0.02f, 0.05f, 0.1f, 0.2f, float.MaxValue };
        foreach (var e in ctx.Polities)
        {
            if (e.Dead || e.Cell < 0 || e.Cell >= n) continue;
            int ri = BucketIndex(ctx.R[e.Cell], rBins);
            popByR[ri] += (long)e.P; entByR[ri]++;
            int tc = e.TerritoryId >= 0 && e.TerritoryId < ctx.TerritoryCells.Length ? ctx.TerritoryCells[e.TerritoryId].Count : 0;
            int ti = tc <= 1 ? 0 : tc <= 3 ? 1 : tc <= 7 ? 2 : tc <= 19 ? 3 : tc <= 59 ? 4 : 5;
            terrHist[ti]++;
        }
        for (int c = 0; c < n; c++)
        {
            if (ctx.CellOwner[c] >= 0)
                ownedByR[BucketIndex(ctx.R[c], rBins)]++;
        }
        string terrStr = $"领地: 1格={terrHist[0]} 2-3={terrHist[1]} 4-7={terrHist[2]} 8-19={terrHist[3]} 20-59={terrHist[4]} 60+={terrHist[5]}";
        string rStr = "格R分布: ";
        for (int i = 0; i < 5; i++)
            rStr += $"[{(i == 4 ? "≥0.2" : "<" + rBins[i])}]实体{entByR[i]}/人口{popByR[i]}/归属格{ownedByR[i]} ";
        LogService.Log("HumanLayersProbe", $"扩张诊断: {terrStr} | {rStr}");
    }

    private static int BucketIndex(float r, float[] bins)
    {
        for (int i = 0; i < bins.Length; i++)
            if (r < bins[i]) return i;
        return bins.Length - 1;
    }

    /// <summary>势力色碰撞检查（2026-08-16 用户"所有势力颜色都要不一样"验证）：直接用 **生产代码**
    /// PowerPalette.Build（最远点采样）构建调色板——统计不同势力 id 两两 RGB 距离最小值
    /// （应远大于 0.05，肉眼可区分；0=完全同色）。id 集与 MapViewer 一致：仅显示层实际使用的
    /// PowerIdOf（带域标记，CellOwner 中的活实体）——CellOwner 裸 id 不参与着色，混入会假撞色。</summary>
    private void DumpPowerColorCheck(CivSimContext ctx, int n)
    {
        var ids = new HashSet<int>();
        var byId = new Dictionary<int, Polity>();
        foreach (var e in ctx.Polities) if (!e.Dead) byId[e.Id] = e;
        if (ctx.CellOwner != null)
            for (int v = 0; v < n; v++)
                if (ctx.CellOwner[v] >= 0 && byId.TryGetValue(ctx.CellOwner[v], out var o))
                    ids.Add(PowerIdOf(o));
        // 与 MapViewer 同语义：调色板只收录显示层出现的势力（归属格主导——无格势力不上图不着色）
        var pal = PowerPalette.Build(ids);
        var list = new List<int>(ids);
        list.Sort();
        float minDist = float.MaxValue;
        string minPair = "";
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                float d = PowerPalette.Dist(pal[list[i]], pal[list[j]]);
                if (d < minDist) { minDist = d; minPair = $"{list[i]}vs{list[j]}"; }
            }
        LogService.Log("HumanLayersProbe", $"势力色碰撞检查: {list.Count} 个势力 最小色距={minDist:F3}({minPair})（应 >0.05 肉眼可区分；0=完全同色）");
    }

    /// <summary>势力范围色碰撞检查（2026-08-16 用户"势力范围全是白的"验证）：同 PowerPalette 最远点采样
    /// 构建领地调色板（id 集 = 显示层实际使用的语言群 key 哈希，同 MapViewer._tileTerritory 语义）——
    /// 统计两两最小色距（应 >0.05）；并报告是否有近白色（旧版明度 0.85 致全白）。</summary>
    private void DumpTerritoryColorCheck(CivSimContext ctx, int n)
    {
        var ids = new HashSet<int>();
        var byId = new Dictionary<int, Polity>();
        foreach (var e in ctx.Polities) if (!e.Dead) byId[e.Id] = e;
        if (ctx.CellOwner != null)
            for (int v = 0; v < n; v++)
                if (ctx.CellOwner[v] >= 0 && byId.TryGetValue(ctx.CellOwner[v], out var o))
                    ids.Add(ShareField.KeyHash(ShareField.DomKey(o.CultureGroupShare)));
        var pal = PowerPalette.Build(ids);
        var list = new List<int>(ids);
        list.Sort();
        float minDist = float.MaxValue;
        string minPair = "";
        int nearWhite = 0;   // RGB 三分量均 >0.7（旧版明度 0.85 的全白症状）
        for (int i = 0; i < list.Count; i++)
        {
            var c = pal[list[i]];
            if (c.R > 0.7f && c.G > 0.7f && c.B > 0.7f) nearWhite++;
            for (int j = i + 1; j < list.Count; j++)
            {
                float d = PowerPalette.Dist(pal[list[i]], pal[list[j]]);
                if (d < minDist) { minDist = d; minPair = $"{list[i]}vs{list[j]}"; }
            }
        }
        LogService.Log("HumanLayersProbe", $"势力范围色碰撞检查: {list.Count} 个领地 最小色距={minDist:F3}({minPair})（应 >0.05 肉眼可区分） 近白色={nearWhite}（应=0）");
    }

    /// <summary>聚落统计（2026-08-19 阶段3）：等级分布（新村/村庄/城镇/城市）+ 废墟数 + 都城（至尊酋长聚落）。</summary>
    private void DumpHabitations(CivSimContext ctx)
    {
        if (ctx.Habitations == null || ctx.Habitations.Count == 0)
        {
            LogService.Log("HumanLayersProbe", "聚落: 无（旧档/无农业定居——v12 旧档仅新演化生成）");
            return;
        }
        int[] levels = new int[4];
        int ruins = 0, capitals = 0;
        var byId = new Dictionary<int, Polity>();
        foreach (var e in ctx.Polities) if (!e.Dead) byId[e.Id] = e;
        foreach (var s in ctx.Habitations)
        {
            if (s.IsRuin) { ruins++; continue; }
            if (s.Level >= 0 && s.Level < 4) levels[s.Level]++;
            if (byId.TryGetValue(s.OccupantId, out var occ) && occ.IsChief && occ.ChiefdomId == occ.Id) capitals++;
        }
        LogService.Log("HumanLayersProbe", $"聚落: {ctx.Habitations.Count} 个 | 新村{levels[0]} 村庄{levels[1]} 城镇{levels[2]} 城市{levels[3]} 废墟{ruins} 都城{capitals}");
    }

    /// <summary>生产方式 × 人均库存画像（2026-08-19 贸易专业化观测）：农/牧/猎主导部落的库存分布——
    /// 预期农持谷物、牧持羊毛、猎持皮革（比较优势 → 各产所长 → 贸易互通）。</summary>
    private void DumpModeStocks(CivSimContext ctx)
    {
        var modes = new Dictionary<string, List<Polity>>();
        foreach (var e in ctx.Polities)
        {
            if (e.Dead) continue;
            string m;
            if (e.IsFarming && e.FFarmLast >= e.FHuntLast && e.FFarmLast >= e.FHerdLast) m = "农";
            else if (e.FHerdLast > e.FHuntLast) m = "牧";
            else m = "猎";
            if (!modes.TryGetValue(m, out var l)) modes[m] = l = new List<Polity>();
            l.Add(e);
        }
        foreach (var kv in modes)
        {
            var sb = new System.Text.StringBuilder();
            for (int s = 0; s < CommodityTable.Count; s++)
            {
                float sum = 0f;
                foreach (var e in kv.Value)
                {
                    float perCap = e.P > 0f ? e.Stocks[s] / e.P : 0f;
                    var st = ctx.HabitationOf(e);   // 粮仓（2026-08-19 双池：正式存储归聚落）
                    if (st != null) perCap += st.Stocks[s] / e.P;
                    sum += perCap;
                }
                sb.Append($"{CommodityTable.All[s].Id} {sum / kv.Value.Count:F2} ");
            }
            LogService.Log("HumanLayersProbe", $"生产方式[{kv.Key}] {kv.Value.Count} 部落 人均库存(随身+粮仓): {sb}");
        }
    }

    /// <summary>政体规模对照（史实参考：简单酋邦数千人、复杂酋邦上万；部落=语言群数百-数千人无中央）：
    /// 酋邦 band 数分布 + 大领地内部是否单酋邦（= 语言网络 vs 政治统一体）。</summary>
    private void DumpPolitySizes(CivSimContext ctx)
    {
        var chiefPolities = new Dictionary<int, int>();
        var chiefPop = new Dictionary<int, float>();
        var terrPolities = new Dictionary<int, int>();
        foreach (var e in ctx.Polities)
        {
            if (e.Dead) continue;
            if (e.ChiefdomId >= 0)
            {
                chiefPolities[e.ChiefdomId] = chiefPolities.TryGetValue(e.ChiefdomId, out var b) ? b + 1 : 1;
                chiefPop[e.ChiefdomId] = chiefPop.TryGetValue(e.ChiefdomId, out var p) ? p + e.P : e.P;
            }
            terrPolities[e.TerritoryId] = terrPolities.TryGetValue(e.TerritoryId, out var t) ? t + 1 : 1;
        }
        var cb = new List<KeyValuePair<int, int>>(chiefPolities);
        cb.Sort((x, y) => y.Value.CompareTo(x.Value));
        var sb = new System.Text.StringBuilder();
        foreach (var kv in cb)
            sb.Append($"酋邦{kv.Key}:{kv.Value}b({chiefPop[kv.Key]:F0}p) ");
        LogService.Log("HumanLayersProbe", $"酋邦 {cb.Count} 个 | 最大: {sb}");
        // 大领地（≥100 band）内酋邦数——语言网络 vs 政治统一
        var terrChiefs = new Dictionary<int, HashSet<int>>();
        foreach (var e in ctx.Polities)
        {
            if (e.Dead || e.ChiefdomId < 0) continue;
            if (!terrChiefs.TryGetValue(e.TerritoryId, out var s)) terrChiefs[e.TerritoryId] = s = new HashSet<int>();
            s.Add(e.ChiefdomId);
        }
        var big = new List<KeyValuePair<int, int>>(terrPolities);
        big.Sort((x, y) => y.Value.CompareTo(x.Value));
        var sb2 = new System.Text.StringBuilder();
        int shown = 0;
        foreach (var kv in big)
        {
            if (kv.Value < 100) break;
            sb2.Append($"领地{kv.Key}({kv.Value}b):{(terrChiefs.TryGetValue(kv.Key, out var s) ? s.Count : 0)}个酋邦 ");
            if (++shown >= 6) break;
        }
        LogService.Log("HumanLayersProbe", $"大领地内酋邦数（语言网络 vs 政治统一）: {sb2}");
    }

    private void DumpLayer(CivSimContext ctx, Dictionary<int, int> ownerCells, string name, Func<Polity, string> keyOf)
    {
        var agg = new Dictionary<string, Agg>();
        foreach (var e in ctx.Polities)
        {
            if (e.Dead) continue;
            string k = keyOf(e) ?? "无";
            if (!agg.TryGetValue(k, out var a)) agg[k] = a = new Agg();
            a.Ent++;
            a.Pop += e.P;
            if (ownerCells.TryGetValue(e.Id, out int cc)) a.Cells += cc;
        }
        var list = new List<KeyValuePair<string, Agg>>(agg);
        list.Sort((x, y) => y.Value.Cells.CompareTo(x.Value.Cells));
        var sb = new System.Text.StringBuilder();
        foreach (var kv in list)
            sb.Append($"{kv.Key}:{kv.Value.Ent}e/{kv.Value.Cells}格/{kv.Value.Pop:F0}p ");
        LogService.Log("HumanLayersProbe", $"{name}: {sb}");
    }

    /// <summary>等距柱状导出（归属格主导：每像素→最近逻辑顶点→CellOwner→该 band 的 key 哈希→色）。</summary>
    private void ExportMap(GameGrid grid, CivSimContext ctx, string tag, Func<Polity, int> keyOf)
    {
        const int w = 1024, h = 512;
        var verts = grid.Verts;
        const int BL = 64, BT = 32;
        var buckets = new List<int>[BT][];
        for (int t = 0; t < BT; t++)
        {
            buckets[t] = new List<int>[BL];
            for (int l = 0; l < BL; l++) buckets[t][l] = new List<int>();
        }
        for (int v = 0; v < verts.Length; v++)
        {
            var p = verts[v];
            int lat = (int)Mathf.Clamp((Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f)) / Mathf.Pi + 0.5f) * BT, 0, BT - 1);
            int lon = (int)Mathf.Clamp((Mathf.Atan2(p.Z, p.X) / Mathf.Tau + 0.5f) * BL, 0, BL - 1);
            buckets[lat][lon].Add(v);
        }
        var idMap = new Dictionary<int, Polity>();
        foreach (var e in ctx.Polities) if (!e.Dead) idMap[e.Id] = e;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        for (int y = 0; y < h; y++)
        {
            float lat = 90f - 180f * y / (h - 1);
            float sinLa = Mathf.Sin(Mathf.DegToRad(lat));
            float cosLa = Mathf.Cos(Mathf.DegToRad(lat));
            int latB = (int)Mathf.Clamp((lat / 180f + 0.5f) * BT, 0, BT - 1);
            for (int x = 0; x < w; x++)
            {
                float lon = -180f + 360f * x / (w - 1);
                float lo = Mathf.DegToRad(lon);
                var dir = new Vector3(cosLa * Mathf.Cos(lo), sinLa, cosLa * Mathf.Sin(lo));
                int lonB = (int)Mathf.Clamp((lon / 360f + 0.5f) * BL, 0, BL - 1);
                int best = -1;
                float bestD = float.MaxValue;
                for (int dt = -1; dt <= 1; dt++)
                    for (int dl = -1; dl <= 1; dl++)
                    {
                        var bl = buckets[(latB + dt + BT) % BT][(lonB + dl + BL) % BL];
                        foreach (int v in bl)
                        {
                            float d = dir.DistanceSquaredTo(verts[v]);
                            if (d < bestD) { bestD = d; best = v; }
                        }
                    }
                Color c;
                if (best < 0) c = new Color(0.2f, 0.2f, 0.2f);
                else if (ctx.CellOwner != null && best < ctx.CellOwner.Length && ctx.CellOwner[best] >= 0
                         && idMap.TryGetValue(ctx.CellOwner[best], out var owner))
                {
                    // 2026-08-19 定案：全部按归属者身份**统一着色**（无定居/领地深浅区分——用户"直接补齐"）
                    int k = keyOf(owner);
                    int g = ShareField.KeyHash(ShareField.DomKey(owner.CultureGroupShare));
                    c = k == 0 ? new Color(0.25f, 0.25f, 0.28f) : FamilyColor(g, k, 0.55f, 0.20f);
                }
                else c = SeaColor;
                img.SetPixel(x, y, c);
            }
        }
        string outPath = $"user://maps/human_{tag}_diag.png";
        img.SavePng(outPath);
        LogService.Log("HumanLayersProbe", $"saved {outPath}");
    }

    private static Color FamilyColor(int groupHash, int itemHash, float lightBase, float lightSpan)
    {
        float hue = HueOf(groupHash != 0 ? groupHash : itemHash);
        float shade = (itemHash & 0xFF) / 255f;
        return HslToRgb(hue, 0.55f, lightBase + lightSpan * shade);
    }

    private static float HueOf(int h)
    {
        // 黄金角散列（同 MapViewer 风格）
        uint u = (uint)h;
        return (u * 2654435761u % 100000u) / 100000f;
    }

    private static Color HslToRgb(float h, float s, float l)
    {
        float r, g, b;
        if (s <= 0f) { r = g = b = l; }
        else
        {
            float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
            float p = 2f * l - q;
            r = Hue2Rgb(p, q, h + 1f / 3f);
            g = Hue2Rgb(p, q, h);
            b = Hue2Rgb(p, q, h - 1f / 3f);
        }
        return new Color(r, g, b);
    }

    private static float Hue2Rgb(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}
