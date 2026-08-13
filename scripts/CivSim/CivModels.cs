using System;
using System.Collections.Generic;
using Godot;
using World.Biome;
using World.LogicGrid;

namespace World.CivSim;

/// <summary>
/// 文明演化模型统一抽象基类（唯一基类 + 注册表）。
/// v4 纯实体模型：每个机制 = 一个模型，按 Order 每 tick 执行（docs/石器时代设计.md §二）。
/// </summary>
public abstract class CivModelBase
{
    public abstract string Name { get; }
    public abstract int Order { get; }
    public abstract void Execute(CivSimContext ctx);
}

/// <summary>机制注册表（v4 石器时代：9 模型，Order 0-80）。</summary>
public sealed class CivModelRegistry
{
    private readonly List<CivModelBase> _models = new();

    public CivModelRegistry Register(CivModelBase m) { _models.Add(m); return this; }

    public void ExecuteAll(CivSimContext ctx)
    {
        foreach (var m in SortedModels())
            m.Execute(ctx);
    }

    /// <summary>按 Order 排序后的模型列表（诊断逐模型执行用；幂等排序）。</summary>
    public IReadOnlyList<CivModelBase> SortedModels()
    {
        _models.Sort((a, b) => a.Order.CompareTo(b.Order));
        return _models;
    }

    public static CivModelRegistry StoneAge()
    {
        return new CivModelRegistry()
            .Register(new OriginModel())
            .Register(new CultivateModel())     // 农田开垦（Order 6，2026-08-17 土地挂钩）
            .Register(new InfluenceModel())     // 归属 = argmax(P×M×w(d))，粘性 1.15
            .Register(new HarvestModel())       // 领地采集（静态丰度×土地×劳动力）→ FLast
            .Register(new EnergyModel())
            .Register(new GrowthModel())
            .Register(new PrestigeModel())      // 声望/酋长（Order 25，2026-08-17 酋邦层）
            .Register(new ModeModel())
            .Register(new InventionModel())
            .Register(new SpreadModel())
            .Register(new CultureModel())
            .Register(new ReligionModel())
            .Register(new TerritoryModel())     // 领地凝聚（Order 45，2026-08-17 注册修复：此前从未注册进演化——
                                                //   TerritoryId/Size 全 -1 → 科技传播领地加成失效 + 酋邦永不凝聚）
            .Register(new ChiefdomModel())      // 酋邦凝聚（Order 46，2026-08-17 酋邦层）
            .Register(new AbsorptionModel())    // 吞并（Order 47，2026-08-17 用户拍板：驻扎格被覆盖→并入/迁走）
            .Register(new ConflictModel())      // 边境冲突（Order 75，2026-08-10）：粘性僵局暴力出口
            .Register(new SplitMigrateModel());
    }
}

// ══════════════════════════════════════════════════════════════════
// ①a 农田开垦（Order 6）：农业 band 每 tick 提高**领地格**开垦率（2026-08-17 领地农业——
//    农田 = 开垦的领地格；采集产出 ×(1−开垦)、农业产出 ×开垦；土地竞争载体）。
// ══════════════════════════════════════════════════════════════════
public sealed class CultivateModel : CivModelBase
{
    public override string Name => "农田开垦";
    public override int Order => 6;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.Cultivation == null) return;
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead || !e.IsFarming) continue;
            var terr = ctx.TerritoryCells[e.Id];
            if (terr == null || terr.Count == 0) continue;
            foreach (int c in terr)
            {
                if (c < 0 || c >= ctx.Cultivation.Length) continue;
                float v = ctx.Cultivation[c] + CivSimContext.CultivateRate * (1f - ctx.Cultivation[c]);
                ctx.Cultivation[c] = Mathf.Min(1f, v);
            }
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ①b 影响力场（Order 8）：每格归属 = argmax(P×CarryMult×w(d))；粘性：非 owner 需超现 owner×1.15。
//     领地 = 归属格集合（Voronoi 胞自动涌现，无主动宣示/竞争操作——竞争即场对比）。
// ══════════════════════════════════════════════════════════════════
public sealed class InfluenceModel : CivModelBase
{
    public override string Name => "影响力归属";
    public override int Order => 8;

    public override void Execute(CivSimContext ctx)
    {
        ctx.RebuildInfluence();
    }
}

// ══════════════════════════════════════════════════════════════════
// ①c 采集收获（Order 9）：领地建筑分配产出（2026-08-17 凹化+等边际——
//     采集/农田每格建筑，等边际闭式分配劳动力；FBerryLast/FFarmLast 分量缓存）。
// ══════════════════════════════════════════════════════════════════
public sealed class HarvestModel : CivModelBase
{
    public override string Name => "采集收获";
    public override int Order => 9;

    public override void Execute(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead) continue;
            e.FHuntLast = ctx.AllocateAndProduce(e);   // 采集（猎+果）+ 牧场 + 农业（等边际分配后实际产出）
            e.FLast = e.FHuntLast + e.FFarmLast + e.FHerdLast;
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ① 起源播种（Order 0）：富饶区池内随机 + 格距 ≥12 + 不同大陆优先。
//    每摇篮独立文化/文化群（互不同源）；P=100，自带 stone_core。
// ══════════════════════════════════════════════════════════════════
public sealed class OriginModel : CivModelBase
{
    public override string Name => "起源播种";
    public override int Order => 0;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.Tick > 0) return;
        var grid = ctx.Grid;
        int n = grid.N;

        // ── 富饶区：陆地 ∩ R>0，按 R 降序前 30% ──
        var land = new List<int>();
        for (int i = 0; i < n; i++)
            if (grid.IsLandCell(i) && ctx.R[i] > 0f)
                land.Add(i);
        if (land.Count == 0) return;
        land.Sort((a, b) => ctx.R[b].CompareTo(ctx.R[a]));
        int rich = Mathf.Max(8, land.Count * 30 / 100);
        var pool = land.GetRange(0, Mathf.Min(rich, land.Count));

        // ── 大陆连通分量（BFS 陆地；-1=海洋）──
        int[] continent = ComputeContinents(grid, n);

        // ── 贪心选格：优先"已选起源数最少的大陆"，大陆内随机；格距 ≥ OriginDistMin ──
        float minDistKm = CivSimContext.OriginDistMin * Mathf.Sqrt(grid.CellAreaKm2);   // 12 格 × 平均格距
        int count = Mathf.Min(ctx.OriginCount, pool.Count);
        var chosen = new List<int>();
        var contCount = new Dictionary<int, int>();
        for (int k = 0; k < count; k++)
        {
            // 候选 = 池内且距已选 ≥ 阈值
            var cands = new List<int>();
            foreach (int c in pool)
            {
                bool ok = true;
                foreach (int p in chosen)
                    if (grid.DistKm(c, p) < minDistKm) { ok = false; break; }
                if (ok) cands.Add(c);
            }
            if (cands.Count == 0) break;
            // 优先未占大陆：取"大陆上已选起源数最少"的候选组（确定性分组，组内随机抽取；
            // ⚠️ 不能用随机 tie-break 排序——比较器必须一致，否则 ArraySortHelper 抛异常）
            int minCount = int.MaxValue;
            foreach (int c in cands)
            {
                int cc = contCount.TryGetValue(continent[c], out var vc) ? vc : 0;
                if (cc < minCount) minCount = cc;
            }
            var minCands = new List<int>();
            foreach (int c in cands)
            {
                int cc = contCount.TryGetValue(continent[c], out var vc) ? vc : 0;
                if (cc == minCount) minCands.Add(c);
            }
            int pick = minCands[ctx.Rng.Next(minCands.Count)];
            chosen.Add(pick);
            contCount[continent[pick]] = contCount.TryGetValue(continent[pick], out var v) ? v + 1 : 1;
        }

        foreach (int pick in chosen)
        {
            string key = ctx.NextCultureKey();   // 每摇篮独立文化/文化群 key（互不同源）
            string relKey = ctx.NextReligionKey();   // 每摇篮独立宗教派别（图腾体系互不同源）
            var e = new CivEntity
            {
                Id = ctx.NextEntityId++,   // 独立计数器（2026-08-10：Entities.Count 读档后分叉）
                Cell = pick,
                P = CivSimContext.OriginPop,
                OriginCell = pick,
                BornTick = 0,
                CultureShare = ShareField.NewCulture(key),
                CultureGroupShare = ShareField.NewCulture(key),
                ReligionShare = ShareField.NewReligion(ReligionStage.Animism),
                ReligionCultShare = ShareField.NewCulture(relKey),
            };
            e.TechKeys.Add(TechTable.StoneCore);
            ctx.Entities.Add(e);
            ctx.CellTribes[pick].Add(e);
        }
        ctx.FirstFarmTick = -1;
    }

    /// <summary>陆地连通分量（BFS，确定性：格序遍历）。</summary>
    private static int[] ComputeContinents(GameGrid grid, int n)
    {
        var cont = new int[n];
        for (int i = 0; i < n; i++) cont[i] = -1;
        int id = 0;
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (cont[i] != -1 || !grid.IsLandCell(i)) continue;
            cont[i] = id;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                foreach (int nb in grid.Neighbors[c])
                    if (cont[nb] == -1 && grid.IsLandCell(nb))
                    {
                        cont[nb] = id;
                        queue.Enqueue(nb);
                    }
            }
            id++;
        }
        return cont;
    }
}

// ══════════════════════════════════════════════════════════════════
// ② 能量核算（Order 10）：e = Y/P；s = e − 1。
//    e_猎(P)=Y_猎/(P+h) 仅用于生产方式选择（ModeModel）；此处用实际产量（§二 注）。
// ══════════════════════════════════════════════════════════════════
public sealed class EnergyModel : CivModelBase
{
    public override string Name => "能量核算";
    public override int Order => 10;

    public override void Execute(CivSimContext ctx)
    {
        // 刷新格人口（本 tick 起始快照：增长/压力共用）
        Array.Clear(ctx.CellPop, 0, ctx.CellPop.Length);
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead) continue;
            ctx.CellPop[e.Cell] += e.P;
        }
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead) continue;
            float f = e.FLast;   // 当 tick 实际产出（RefreshCellState 已算，含劳动因子/冷下限）
            e.EPerCap = f / Mathf.Max(0.001f, e.P);
            e.Surplus = e.EPerCap - 1f;
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ③ 人口增长（Order 20）：P_i ×= exp(r_eff·(1 − D_i/F_i))，r_eff=0.5/tick ★
//    D_i = P_i×c（c=1）；F_i = 部落当 tick 实际产出（两层模型 2026-08-17：按部落，不共享格因子）。
//    F_i < D_i → 负增长 = 饿死人（用户拍板 2026-08-06）；P<1 灭绝。
// ══════════════════════════════════════════════════════════════════
public sealed class GrowthModel : CivModelBase
{
    public override string Name => "人口增长";
    public override int Order => 20;

    public override void Execute(CivSimContext ctx)
    {
        float r = ctx.TickFactor;   // 0.5/tick
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead) continue;
            float f = e.FLast;   // 当 tick 实际产出（RefreshCellState 已算，农业含劳动因子；寒冷区含下限）
            if (f <= 0f) continue;
            // ⚠️ 2026-08-17 定居生育跃迁（史实：定居 → 生育间隔缩短/婴儿存活率↑，人口密度 10-50× 游群）
            float rEff = r;
            if (CapabilityTable.Has(ctx, e, "settle")) rEff *= CivSimContext.SettleGrowthMult;   // 1.5
            float factor = Mathf.Exp(rEff * (1f - e.P / f));
            // 存储缓冲（Testart 分水岭，2026-08-09；2026-08-17 分层强化）：
            //   storage（游群粮袋）缺口 ×0.6 → +pottery（陶器密封）×0.4 → +settle（定居粮仓）×0.3
            //   （无状态效果——不引盈余池入档，读档续跑无分叉；宏观等效饥荒缓冲）
            if (factor < 1f && CapabilityTable.Has(ctx, e, "storage"))
            {
                float relief = CivSimContext.StorageFamineRelief;
                if (CapabilityTable.Has(ctx, e, "pottery")) relief = CivSimContext.StorageReliefPottery;
                if (CapabilityTable.Has(ctx, e, "settle")) relief = CivSimContext.StorageReliefSettle;
                factor = 1f + (factor - 1f) * relief;
            }
            // 酋邦再分配互惠（2026-08-17：Halstead-O'Shea 1989 坏年景开仓——贡献过才受赈）：
            //   成员 band 曾交贡赋（Contributed>0）→ 灾年缺口 ×0.5（酋长开仓）；未贡献不受赈
            if (factor < 1f && e.ChiefdomId >= 0 && e.Contributed > 0f)
                factor = 1f + (factor - 1f) * CivSimContext.TributeRelief;
            e.P *= factor;
            if (e.P < 1f) { e.P = 0f; e.Dead = true; }   // 饿死灭绝
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ④ 生产方式选择（Order 30）：argmax(e_猎(P), e_农(P))，w 已含于 e_农（无双扣）。
//    滞回：|e_猎 − e_农| < 0.02 → 保持当前方式（防来回跳）。
//    稳态论证：农业稳态 e=0.8 > 狩猎稳态 0.77 → 站稳不退农（docs §4.4）。
// ══════════════════════════════════════════════════════════════════
public sealed class ModeModel : CivModelBase
{
    public override string Name => "生产方式选择";
    public override int Order => 30;

    public override void Execute(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead) continue;
            bool hasSeed = CapabilityTable.Has(ctx, e, "seed");
            if (!hasSeed) { e.IsFarming = false; continue; }
            // ⚠️ 2026-08-17 决策领地化：yH/yF 用 Σ 领地格潜在（与产出层同口径）——
            //   旧版单格判定导致"领地有良田但驻扎格差 → 永不转农"（科技地图"好几块地只有一处新石器"的根因）；
            //   2026-08-17 畜牧接入：草原牧场潜在并入 yH（草原游牧抬高狩猎收益 → 抑制转农，史实正确）
            float yH = e.CarryMult * (ctx.FHuntTerritory(e) + ctx.FHerdTerritory(e));   // 领地采集+牧场潜在 × 工具加成
            float yF = ctx.FFarmPotentialTerritory(e);                                   // 领地农业潜在（劳动因子=1，防小部落死锁）
            if (yF <= 0f) { e.IsFarming = false; continue; }
            float eH = CivSimContext.EHunt(yH, e.P);
            float eF = CivSimContext.EFarm(yF, e.P);
            float diff = eH - eF;   // e_猎 − e_农（农含 w 扣减）
            if (Mathf.Abs(diff) >= CivSimContext.Hysteresis)
                e.IsFarming = eF > eH;
            if (e.IsFarming && ctx.FirstFarmTick < 0)
                ctx.FirstFarmTick = ctx.Tick;   // 终止条件锚点
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑤ 科技发明（Order 40）：通用 Kremer + 种子压力触发（Boserup）。
//    通用：λ = k·(P_部落/P_ref)·(1+知识/16)·env_i（依赖硬门槛 → 环境 → 随机）
//    种子：WildCrops 位 ✓ + P_格/K_格>0.7 + Soil≥3 + grinding → invProb=0.005（仅起源区）
// ══════════════════════════════════════════════════════════════════
public sealed class InventionModel : CivModelBase
{
    public override string Name => "科技发明";
    public override int Order => 40;

    public override void Execute(CivSimContext ctx)
    {
        CivEngine.RefreshCellState(ctx);   // 生产方式已更新（Order 30）→ 刷新 F_格 供压力判定

        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead) continue;
            // ── 通用发明（Kremer）──
            foreach (var t in TechTable.All)
            {
                if (t.IsSeed || t.IsAgricultureConcept) continue;
                if (e.TechKeys.Contains(t.Key)) continue;
                if (!HasAll(e.TechKeys, t.Requires)) continue;
                float env = ctx.EnvFactor(e.Cell, t);
                if (env <= 0f) continue;
                float lambda = t.InvRate * (e.P / Mathf.Max(1f, t.PRef)) * (1f + TechTable.Knowledge(e.TechKeys) / 16f) * env;
                if (ctx.Rng.NextDouble() < lambda)
                    e.TechKeys.Add(t.Key);
            }
            // ── 种子（压力触发，Boserup 被逼出来的）──
            float pressure = ctx.CellF[e.Cell] > 0f ? ctx.CellPop[e.Cell] / ctx.CellF[e.Cell] : 0f;
            bool pressureOk = pressure > CivSimContext.SeedPressure;
            bool soilOk = ctx.Grid.SoilLevel[e.Cell] >= 3;
            bool grindOk = CapabilityTable.Has(ctx, e, "grinding");
            if (pressureOk && soilOk && grindOk)
            {
                byte wild = ctx.WildCrops[e.Cell];
                for (int s = 0; s < TechTable.SeedKeys.Length; s++)
                {
                    if ((wild & (1 << s)) == 0) continue;          // WildCrops 位（隐含气候+土壤）
                    if (e.TechKeys.Contains(TechTable.SeedKeys[s])) continue;
                    if (ctx.Rng.NextDouble() < CivSimContext.SeedInvProb)
                        e.TechKeys.Add(TechTable.SeedKeys[s]);
                }
            }
            TechTable.SyncAgriculture(e.TechKeys);
        }
    }

    private static bool HasAll(HashSet<string> keys, string[] req)
    {
        foreach (var r in req) if (!keys.Contains(r)) return false;
        return true;
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑤ 领地凝聚（Order 45）：band 凝聚体 = 连通分量（每 TerritoryRebuildEvery tick 重算）。
//    凝聚边 = 同格 band 对 或 邻格格代表对 + CultureGroupShare 主导 key 相同 + 双方存活。
//    分量标号 = 分量最小实体 Id（确定性：读档重建 → 续跑无分叉）。纯派生，不入档。
//    距离衰减 = 接触衰减：远格接触少 → 漂变分群 → 边断（零新常量，全部涌现）。
// ══════════════════════════════════════════════════════════════════
public sealed class TerritoryModel : CivModelBase
{
    public override string Name => "领地凝聚";
    public override int Order => 45;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.Tick - ctx.TerritoryLastRebuild < CivSimContext.TerritoryRebuildEvery) return;
        ctx.TerritoryLastRebuild = ctx.Tick;
        Rebuild(ctx);
    }

    /// <summary>重建全部实体领地（读档入口也调用——派生状态从存档确定性重算）。</summary>
    public static void Rebuild(CivSimContext ctx)
    {
        var parent = new Dictionary<int, int>();   // 实体 Id → 并查集父
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        foreach (var e in ctx.Entities)
            if (!e.Dead) parent[e.Id] = e.Id;
        // 同格凝聚边：格内 band 两两，同语言群 → 凝聚
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            for (int a = 0; a < list.Count; a++)
            {
                var ea = list[a];
                if (ea.Dead) continue;
                for (int b = a + 1; b < list.Count; b++)
                {
                    var eb = list[b];
                    if (eb.Dead) continue;
                    if (ShareField.DomKey(ea.CultureGroupShare) == ShareField.DomKey(eb.CultureGroupShare))
                        Union(ea.Id, eb.Id);
                }
            }
        }
        // 邻格凝聚边：格代表对（格内 P 最大）× 邻格代表，同语言群 → 凝聚（其余 band 经同格边挂靠）
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            if (list.Count == 0) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var nbList = ctx.CellTribes[nb];
                if (nbList.Count == 0) continue;
                var repA = MaxPop(list);
                var repB = MaxPop(nbList);
                if (repA == null || repB == null) continue;
                if (ShareField.DomKey(repA.CultureGroupShare) == ShareField.DomKey(repB.CultureGroupShare))
                    Union(repA.Id, repB.Id);
            }
        }
        // 填分量：标号 = 分量最小实体 Id（确定性）；size = 分量实体数
        var sizes = new Dictionary<int, int>();
        var mins = new Dictionary<int, int>();
        foreach (var e in ctx.Entities)
        {
            if (e.Dead) continue;
            int root = Find(e.Id);
            sizes[root] = sizes.TryGetValue(root, out var v) ? v + 1 : 1;
            if (!mins.TryGetValue(root, out var m) || e.Id < m) mins[root] = e.Id;
        }
        foreach (var e in ctx.Entities)
        {
            if (e.Dead) continue;
            int root = Find(e.Id);
            e.TerritoryId = mins[root];
            e.TerritorySize = sizes[root];
        }
    }

    private static CivEntity MaxPop(List<CivEntity> list)
    {
        CivEntity best = null;
        for (int k = 0; k < list.Count; k++)
            if (!list[k].Dead && (best == null || list[k].P > best.P)) best = list[k];
        return best;
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑥ 科技传播（Order 50）：同格实体对 + 邻格边界（代表实体对）。
//    p = SpreadBase × 种子修正（clamp(φ, 0.3, 1.0)）；依赖缺失不传；Rogers S 自然涌现。
// ══════════════════════════════════════════════════════════════════
public sealed class SpreadModel : CivModelBase
{
    public override string Name => "科技传播";
    public override int Order => 50;

    public override void Execute(CivSimContext ctx)
    {
        // ── 同格对 ──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            for (int a = 0; a < list.Count; a++)
            {
                if (list[a].Dead) continue;
                for (int b = a + 1; b < list.Count; b++)
                {
                    if (list[b].Dead) continue;
                    SpreadTech(ctx, list[a], list[b]);
                    SpreadTech(ctx, list[b], list[a]);
                }
            }
        }
        // ── 邻格边界（代表实体对，人口最大）──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellTribes[i];
            if (a.Count == 0) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var b = ctx.CellTribes[nb];
                if (b.Count == 0) continue;
                var repA = MaxPop(a);
                var repB = MaxPop(b);
                // 闭塞区域：跨格传播 ×= BorderCost（地形障碍 × 气候相似度；A→B 用 A 的科技判定障碍突破）
                float cost = ctx.BorderCost(i, nb, repA.TechKeys);
                if (cost <= 0f) continue;
                SpreadTech(ctx, repA, repB, cost);
                SpreadTech(ctx, repB, repA, cost);
            }
        }
    }

    private static CivEntity MaxPop(List<CivEntity> list)
    {
        var best = list[0];
        for (int k = 1; k < list.Count; k++)
            if (!list[k].Dead && list[k].P > best.P) best = list[k];
        return best;
    }

    /// <summary>领地传播乘数：同领地 ×1.5（整合加成）；至少一方是正式领地（≥2 band）→ ×0.5（跨边界软冲突）；散兵部落间 ×1（BorderCost 已有）。</summary>
    internal static float TerritoryMult(CivEntity a, CivEntity b)
    {
        if (a.TerritoryId >= 0 && a.TerritoryId == b.TerritoryId) return CivSimContext.TerritorySpreadMult;
        if (a.TerritorySize >= 2 || b.TerritorySize >= 2) return CivSimContext.CrossBorderSpreadMult;
        return 1f;
    }

    /// <summary>技术传播 from → to（to 缺 from 的技术且依赖满足 → 按概率获得）。
    /// ⚠️ 2026-08-10 确定性修复：HashSet 遍历顺序依赖构建历史（读档重建 Add 顺序 ≠ 演化布局）→
    ///    同 Rng 数对应不同 key → 读档续跑分叉。改为**排序遍历**（与布局无关，ctx 缓冲无分配）。</summary>
    private void SpreadTech(CivSimContext ctx, CivEntity from, CivEntity to, float border = 1f)
    {
        float terr = TerritoryMult(from, to);   // 领地乘数（同领地×1.5 / 跨领地×0.5 / 散兵×1）
        int nKeys = from.TechKeys.Count;
        if (nKeys == 0) return;
        var keys = ctx.KeyBuf;
        if (keys == null || keys.Length < nKeys) ctx.KeyBuf = keys = new string[Math.Max(16, nKeys * 2)];
        from.TechKeys.CopyTo(keys, 0);
        Array.Sort(keys, 0, nKeys, StringComparer.Ordinal);   // 确定性顺序（HashSet 布局无关）
        for (int ki = 0; ki < nKeys; ki++)
        {
            var key = keys[ki];
            if (to.TechKeys.Contains(key)) continue;
            var t = TechTable.Get(key);
            if (t == null || t.IsAgricultureConcept) continue;
            if (!HasAll(to.TechKeys, t.Requires)) continue;   // 依赖硬门槛
            float p = t.SpreadBase * border * terr;
            if (t.IsSeed)
                p *= Mathf.Clamp(ctx.Phi(to.Cell, t.SeedIndex), 0.3f, 1f);   // 种子传播修正
            if (ctx.Rng.NextDouble() < Mathf.Min(0.5f, p))
            {
                to.TechKeys.Add(t.Key);
                TechTable.SyncAgriculture(to.TechKeys);
            }
        }
    }

    private static bool HasAll(HashSet<string> keys, string[] req)
    {
        foreach (var r in req) if (!keys.Contains(r)) return false;
        return true;
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑨ 边境冲突（Order 75，2026-08-10 定稿 §十五）：归属两条途径——和平（场 argmax+粘性）/
//    武力（冲突强制易主+实控锁定）。军事实力 MilitMult 与影响力解耦（武器科技只进军事）。
//    触发：粘性僵持窗口（I_B < I_A ≤ I_B×1.15）+ 资源压力 + 低频概率。
//    结果：损耗（胜者小败者大）+ 掠夺存量 + 易主锁定（场不重算 N tick）+ 驱逐。
// ══════════════════════════════════════════════════════════════════
public sealed class ConflictModel : CivModelBase
{
    public override string Name => "边境冲突";
    public override int Order => 75;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.LockedUntil == null || ctx.Entities.Count < 2) return;
        int n = ctx.Grid.N;
        // ⚠️ 2026-08-17 审查修复（真 bug）：防护计数器误用总计数 ctx.Conflicts（演化累计到 3 后
        //   本模型永久 return——冲突机制实际只生效前 3 场）；且总计数不入档 → 读档端 0 vs 内存端累计
        //   值 → T04 续跑 Rng 分叉。改为 Execute 内独立的本 tick 计数（总计数保留统计）。
        int conflictsThisTick = 0;
        for (int c = 0; c < n; c++)
        {
            int owner = ctx.CellOwner[c];
            if (owner < 0) continue;
            if (ctx.LockedUntil[c] > ctx.Tick) continue;               // 锁定格不冲突（既成事实）
            int ch = ctx.CellBestOwner[c];
            if (ch < 0 || ch == owner) continue;
            float iCh = ctx.CellBestInf[c];
            float iOwn = ctx.CellOwnerInf[c];
            if (iCh <= iOwn || iCh > iOwn * CivSimContext.Stickiness) continue;   // 必须粘性僵持窗口
            var eo = FindEntity(ctx, owner);
            var ec = FindEntity(ctx, ch);
            if (eo == null || ec == null || eo.Dead || ec.Dead) continue;
            if (ec.LastConflictTick >= 0 && ctx.Tick - ec.LastConflictTick < CivSimContext.ConflictCooldown) continue;
            if (eo.LastConflictTick >= 0 && ctx.Tick - eo.LastConflictTick < CivSimContext.ConflictCooldown) continue;
            // 压力门控（低频：旧石器战争偶发——饿/超载才打）
            bool pressure = ctx.IsStarving(ec) || ctx.IsStarving(eo)
                || ec.P > CivSimContext.SplitPop || eo.P > CivSimContext.SplitPop;
            if (!pressure) continue;
            // ⚠️ 2026-08-17 酋邦军事整合（Kirch 1984）：
            //   ① 同酋邦冲突概率 ×0.5（酋长仲裁——非消除，pax 不存在）
            //   ② 继承窗口内 ×2（权力真空 → 继承战争，Polynesia 常态）
            float conflictChance = CivSimContext.ConflictChance;
            bool sameChiefdom = ec.ChiefdomId >= 0 && ec.ChiefdomId == eo.ChiefdomId;
            if (sameChiefdom) conflictChance *= CivSimContext.InternalConflictMult;
            bool succession = ec.SuccessionUntil > ctx.Tick || eo.SuccessionUntil > ctx.Tick;
            if (succession) conflictChance *= CivSimContext.SuccessionConflictMult;
            if (ctx.Rng.NextDouble() >= conflictChance) continue;
            ResolveConflict(ctx, ec, eo, c);
            if (++conflictsThisTick >= 3) return;   // 单 tick 最多 3 场（性能/爆炸防护）
        }
    }

    internal static void ResolveConflict(CivSimContext ctx, CivEntity challenger, CivEntity owner, int cell)
    {
        // 胜率：P×MilitMult 对比（武器科技加成；随机——弱 band 可爆冷）
        float pC = challenger.P * TechTable.MilitaryMult(challenger.TechKeys);
        float pO = owner.P * TechTable.MilitaryMult(owner.TechKeys);
        // ⚠️ 2026-08-17 联盟合力（Kirch：防御方是酋邦时，入侵者面对酋邦总力量——人多势众，非加成系数）
        if (owner.ChiefdomId >= 0)
        {
            for (int i = 0; i < ctx.Entities.Count; i++)
            {
                var m = ctx.Entities[i];
                if (m.Dead || m == owner || m.ChiefdomId != owner.ChiefdomId) continue;
                pO += m.P * TechTable.MilitaryMult(m.TechKeys);
            }
        }
        float winChance = pC / Mathf.Max(0.0001f, pC + pO);
        bool challengerWins = ctx.Rng.NextDouble() < winChance;
        var winner = challengerWins ? challenger : owner;
        var loser = challengerWins ? owner : challenger;
        // 损耗（胜者小、败者大；不直接灭——饿死兜底）
        winner.P *= (1f - CivSimContext.ConflictLossChallenger);
        loser.P *= (1f - CivSimContext.ConflictLossOwner);
        if (loser.P < 1f) loser.P = 1f;
        winner.LastConflictTick = ctx.Tick;
        loser.LastConflictTick = ctx.Tick;
        // ⚠️ 2026-08-17 掠夺改纯控制权（用户拍板）：砍存量后无货可抢——掠夺 = 武力夺取格子控制权
        //   （下方 CellOwner 强制易主 + 实控锁定；即时资源收益取消）
        if (challengerWins)
        {
            // 武力夺取：争议格 + 挑战者影响圈内败者格，全部强制易主 + 实控锁定
            ctx.CellOwner[cell] = challenger.Id;
            ctx.LockedUntil[cell] = ctx.Tick + CivSimContext.ConflictLockTicks;
            ctx.BfsRadius(cell, CivSimContext.InfluenceRadius, (c2, d) =>
            {
                if (ctx.CellOwner[c2] == owner.Id && ctx.LockedUntil[c2] <= ctx.Tick)
                {
                    ctx.CellOwner[c2] = challenger.Id;
                    ctx.LockedUntil[c2] = ctx.Tick + CivSimContext.ConflictLockTicks;
                }
            }, landOnly: true);
        }
        else
        {
            // 防御成功：挑战者退兵，争议格锁定给 owner（防御方巩固）
            ctx.LockedUntil[cell] = ctx.Tick + CivSimContext.ConflictLockTicks;
        }
        // 驱逐：败者损耗后饿 → 强制迁移（被赶出争议区）
        if (ctx.Rng.NextDouble() < CivSimContext.ConflictExpelChance && ctx.IsStarving(loser))
        {
            int target = SplitMigrateModel.PickMigrateTarget(ctx, loser);
            if (target >= 0)
            {
                ctx.CellTribes[loser.Cell].Remove(loser);
                loser.Cell = target;
                loser.LastMigrateTick = ctx.Tick;
                ctx.CellTribes[target].Add(loser);
                ctx.Migrations++;
            }
        }
        ctx.Conflicts++;
    }

    private static CivEntity FindEntity(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Entities.Count; i++)
            if (ctx.Entities[i].Id == id && !ctx.Entities[i].Dead) return ctx.Entities[i];
        return null;
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑦ 文化互动（Order 60）：格级聚合-演化-分摊（不分部落，用户拍板）+ 相邻格 Axelrod。
//    同化：主导 x' = x + 0.3(1−x)；文化群：Abrams-Strogatz 竞争（慢）。
// ══════════════════════════════════════════════════════════════════
public sealed class CultureModel : CivModelBase
{
    public override string Name => "文化互动";
    public override int Order => 60;

    public override void Execute(CivSimContext ctx)
    {
        // ── 格级：文化同化 + 文化群竞争（聚合 → 演化 → 分摊写回）──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            if (list.Count < 2) continue;

            // 文化标签同化（快）
            var cShare = ShareField.PopMerge(list, e => e.CultureShare);
            ShareField.Assimilate(cShare, CivSimContext.AssimilateRate);
            // 文化群竞争（慢，Abrams-Strogatz）
            var gShare = ShareField.PopMerge(list, e => e.CultureGroupShare);
            StepAbramsStrogatz(gShare);
            // 分摊写回（"不分部落"：格级统一份额）
            foreach (var e in list)
            {
                if (e.Dead) continue;
                e.CultureShare = ShareField.CloneShare(cShare);
                e.CultureGroupShare = ShareField.CloneShare(gShare);
            }
        }

        // ── 相邻格：Axelrod 相似度互动（代表实体对）──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellTribes[i];
            if (a.Count == 0) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var b = ctx.CellTribes[nb];
                if (b.Count == 0) continue;
                var repA = MaxPop(a);
                var repB = MaxPop(b);
                float sim = (ShareField.DomKey(repA.CultureShare) == ShareField.DomKey(repB.CultureShare) ? 0.5f : 0f)
                          + (ShareField.DomKey(repA.CultureGroupShare) == ShareField.DomKey(repB.CultureGroupShare) ? 0.5f : 0f);
                if (sim <= 0.5f) continue;   // Axelrod：不相似不互动（保持差异）
                float rate = sim - 0.5f;
                // 闭塞区域：跨格文化转移 ×= BorderCost（障碍区文化交流弱 → 边界处文化差异保持）
                float cost = ctx.BorderCost(i, nb, repA.TechKeys);
                if (cost <= 0f) continue;
                rate *= cost;
                var strong = repA.P >= repB.P ? repA : repB;
                var weak = strong == repA ? repB : repA;
                int amt = (int)MathF.Round(rate * 255f);
                ShareField.Shift(weak.CultureShare, ShareField.DomKey(weak.CultureShare), ShareField.DomKey(strong.CultureShare), amt);
            }
        }
    }

    /// <summary>Abrams-Strogatz 份额竞争一步（dx/dt = (1−x)s·x^a − x(1−s)(1−x)^a，a=1.31）。</summary>
    private static void StepAbramsStrogatz(ShareEntry[] g)
    {
        const float a = 1.31f;
        float x = ShareField.DomFrac01(g);          // 主导群份额
        float s = x;                                // 地位 = 人口占比 = 份额
        if (x <= 0f || x >= 1f) return;
        float dx = (1f - x) * s * Mathf.Pow(x, a) - x * (1f - s) * Mathf.Pow(1f - x, a);
        int d = (int)MathF.Round(dx * 255f);
        if (d == 0) return;
        int sec = g[1].Frac;
        if (d > 0)
        {
            int take = Mathf.Min(d, sec);
            g[0].Frac = (byte)Mathf.Min(255, g[0].Frac + take);
            g[1].Frac = (byte)Mathf.Max(0, g[1].Frac - take);
        }
        else
        {
            int take = Mathf.Min(-d, g[0].Frac);
            g[0].Frac = (byte)Mathf.Max(0, g[0].Frac - take);
            g[1].Frac = (byte)Mathf.Min(255, g[1].Frac + take);
            if (g[0].Frac == 0) (g[0], g[1]) = (g[1], g[0]);   // 主导被反超 → 交换
        }
        if (g[0].Frac == 255) g[1] = new ShareEntry();   // 全占 → 清第二位
    }

    private static CivEntity MaxPop(List<CivEntity> list)
    {
        var best = list[0];
        for (int k = 1; k < list.Count; k++)
            if (!list[k].Dead && list[k].P > best.P) best = list[k];
        return best;
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑧ 宗教演进（Order 70）：份额场升级/传播/同化（不读时代 ★）。
//    泛灵→萨满：盈余 s>0 + 细石器；萨满→祖先：农业+定居（旧石器天然锁死）。
//    无农业 → 宗教停在泛灵/萨满（旧石器晚期洞穴壁画 = 萨满图腾，史实吻合）。
// ══════════════════════════════════════════════════════════════════
public sealed class ReligionModel : CivModelBase
{
    public override string Name => "宗教演进";
    public override int Order => 70;

    public override void Execute(CivSimContext ctx)
    {
        // ── 升级（实体，份额转移 0.05/tick）──
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead) continue;
            // 泛灵 → 萨满：盈余 s>0 + 细石器
            if (e.Surplus > 0f && CapabilityTable.Has(ctx, e, "microlith"))
            {
                int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionUpgradeRate);
                ShareField.RelTransfer(e.ReligionShare, ReligionStage.Animism, ReligionStage.Shaman, amt);
            }
            // 萨满 → 祖先：定居（=农业派生能力，2026-08-17 落地"定居+存储"缺口——
            //   谷物农业守田定居 → 祖先崇拜；旧石器无农 → settle 能力天然锁死）
            if (e.Surplus > 0f && CapabilityTable.Has(ctx, e, "settle"))
            {
                int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionUpgradeRate);
                ShareField.RelTransfer(e.ReligionShare, ReligionStage.Shaman, ReligionStage.Ancestor, amt);
            }
            // 祖先 → 多神 / 多神 → 一神：后续阶段
        }

        // ── 传播（接触，0.02/tick 只向更高阶段）──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            for (int a = 0; a < list.Count; a++)
            {
                if (list[a].Dead) continue;
                for (int b = a + 1; b < list.Count; b++)
                {
                    if (list[b].Dead) continue;
                    SpreadReligion(ctx, list[a], list[b]);
                    SpreadReligion(ctx, list[b], list[a]);
                }
            }
        }
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellTribes[i];
            if (a.Count == 0) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var b = ctx.CellTribes[nb];
                if (b.Count == 0) continue;
                var repA = MaxPop(a);
                var repB = MaxPop(b);
                // 闭塞区域：跨格宗教传播 ×= BorderCost（障碍区传教弱）
                float cost = ctx.BorderCost(i, nb, repA.TechKeys);
                if (cost <= 0f) continue;
                SpreadReligion(ctx, repA, repB, cost);
                SpreadReligion(ctx, repB, repA, cost);
            }
        }

        // ── 同化（格级，0.3/tick；类型 + 具体派别平行）──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            if (list.Count < 2) continue;
            var rShare = ShareField.RelPopMerge(list);
            string dom = ShareField.DomReligion(rShare);
            int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.AssimilateRate);
            for (int k = 0; k < ReligionStage.Count; k++)
            {
                if (ReligionStage.All[k] == dom) continue;
                ShareField.RelTransfer(rShare, ReligionStage.All[k], dom, amt);
            }
            // 具体派别同化（强势图腾吞噬弱派别，与文化标签同化平行）
            var cShare = ShareField.PopMerge(list, e => e.ReligionCultShare);
            ShareField.Assimilate(cShare, CivSimContext.AssimilateRate);
            foreach (var e in list)
            {
                if (e.Dead) continue;
                e.ReligionShare = (ShareEntry[])rShare.Clone();
                e.ReligionCultShare = ShareField.CloneShare(cShare);
            }
        }
    }

    /// <summary>宗教传播：高阶实体主导宗教份额流向低阶实体（只向更高阶段）。</summary>
    private static void SpreadReligion(CivSimContext ctx, CivEntity from, CivEntity to, float border = 1f)
    {
        string domFrom = ShareField.DomReligion(from.ReligionShare);
        string domTo = ShareField.DomReligion(to.ReligionShare);
        int fi = ShareField.ReligionIndex(domFrom);
        int ti = ShareField.ReligionIndex(domTo);
        if (fi <= ti) return;   // 只向更高阶段（不回头污染）
        int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionSpreadRate * border);
        ShareField.RelTransfer(to.ReligionShare, domTo, domFrom, amt);
    }

    private static CivEntity MaxPop(List<CivEntity> list)
    {
        var best = list[0];
        for (int k = 1; k < list.Count; k++)
            if (!list[k].Dead && list[k].P > best.P) best = list[k];
        return best;
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑨ 分裂/迁徙（Order 80）：segmentary lineage + 饱和/探路迁徙。
//    分裂：P>400 → 45% 带走，份额等比例继承，TechKeys 完整，0.5% 新文化群；格上限 8。
//    迁徙：饱和 0.75K（50%）；探路 2%/tick（30%，1-3 跳最高 K 无人格）；跨海需 canoe；
//          目标全失败 → 留在原地（格人口终填满 → 压力必达，用户拍板）。
// ══════════════════════════════════════════════════════════════════
public sealed class SplitMigrateModel : CivModelBase
{
    public override string Name => "分裂迁移";
    public override int Order => 80;

    public override void Execute(CivSimContext ctx)
    {
        ctx.EnsureTerritory();
        // ── 分裂（2026-08-10 殖民式：快照遍历，新实体下 tick 再判，防同 tick 连锁）──
        //    母 band 人口超载（裂变压力）→ 45% 分群**殖民**影响圈外 1-3 跳最高富饶无主地；
        //    母领地完全不动（承载不变 → P 减半 → 盈余再长 → 周期分裂）；扩散=殖民推进。
        //    无目标（无主地耗尽）→ 不分裂（饱和态：P 继续涨 → 竞争/饿死路径）。
        var snapshot = ctx.Entities.ToArray();
        foreach (var t in snapshot)
        {
            if (t.Dead) continue;
            if (t.LastSplitTick >= 0 && ctx.Tick - t.LastSplitTick < CivSimContext.SplitCooldown) continue;
            // 裂变压力（2026-08-09 用户拍板：资源压力+内部张力涌现，替代纯 P>SplitPop）：
            float tension = Mathf.Clamp((t.P - CivSimContext.FissionTensionStart) / CivSimContext.FissionTensionSpan, 0f, 1f);
            float pEff = t.P * (1f + Mathf.Max(0f, 1f - t.FLast / t.P) + tension);
            if (pEff <= CivSimContext.SplitPop) continue;
            int target = PickMigrateTarget(ctx, t);   // 殖民目标：影响圈外 1-3 跳最高 R 无主陆地格
            if (target < 0) continue;                 // 无主地耗尽 → 不分裂
            float newPop = t.P * CivSimContext.SplitShare;
            t.P -= newPop;
            t.LastSplitTick = ctx.Tick;
            var nt = new CivEntity
            {
                Id = ctx.NextEntityId++,   // 独立计数器（2026-08-10）
                Cell = target,
                P = newPop,
                IsFarming = t.IsFarming,
                TechKeys = new HashSet<string>(t.TechKeys),     // 分裂瞬间技术相同，此后各自发明/学习
                CultureShare = ShareField.CloneShare(t.CultureShare),          // 份额等比例继承
                CultureGroupShare = ShareField.CloneShare(t.CultureGroupShare),
                ReligionShare = (ShareEntry[])t.ReligionShare.Clone(),
                ReligionCultShare = ShareField.CloneShare(t.ReligionCultShare),   // 派别随人口走
                OriginCell = t.Cell,
                BornTick = ctx.Tick,
                LastMigrateTick = ctx.Tick,
                LastSplitTick = ctx.Tick,
            };
            // 文化标签分化：5% 新 key（习俗漂变——方言分化伴习俗变，独立于文化群判定）
            if (ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance)
                nt.CultureShare[0] = new ShareEntry { Key = ctx.NextCultureKey(), Frac = nt.CultureShare[0].Frac };
            // 文化群分化：5% 新 key（方言→语言群漂变），独立计数
            if (ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance)
                nt.CultureGroupShare[0] = new ShareEntry { Key = ctx.NextCultureGroupKey(), Frac = nt.CultureGroupShare[0].Frac };
            // 宗教派别分化：2% 新 key（图腾漂变）
            if (ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance)
                nt.ReligionCultShare[0] = new ShareEntry { Key = ctx.NextReligionKey(), Frac = nt.ReligionCultShare[0].Frac };
            ctx.Entities.Add(nt);
            ctx.CellTribes[target].Add(nt);
            ctx.Fissions++;
        }

        // ── 饥饿迁移（2026-08-10 影响力场模型）：饿（F<D）→ 驻扎点搬家到 1-3 跳内最高富饶度无主格。
        //    落脚必须无主（CellOwner==-1——有主格禁入，冲突未实现）；旧领地格下 tick 场重算自动废弃。
        //    冷却 MigrateCooldown tick 防抖动（连续饿会再次触发——游走 band 觅食迁徙）。
        var snap2 = ctx.Entities.ToArray();
        foreach (var t in snap2)
        {
            if (t.Dead) continue;
            if (t.LastMigrateTick >= 0 && ctx.Tick - t.LastMigrateTick < CivSimContext.MigrateCooldown) continue;
            if (!ctx.IsStarving(t)) continue;
            int target = PickMigrateTarget(ctx, t);
            if (target < 0) continue;   // 无处可去（全被占）→ 饿死路径（GrowthModel）
            ctx.CellTribes[t.Cell].Remove(t);
            t.Cell = target;
            t.LastMigrateTick = ctx.Tick;
            ctx.CellTribes[target].Add(t);
            ctx.Migrations++;
        }
    }

    /// <summary>迁移目标：1-3 跳 BFS 内最高富饶度（R × 路径 BorderCost 乘积）的**无主格**（CellOwner==-1）。
    /// 确定性：时间戳标记 + 固定遍历顺序；无主 = 落脚不侵犯（冲突未实现）。</summary>
    internal static int PickMigrateTarget(CivSimContext ctx, CivEntity mover)
    {
        var grid = ctx.Grid;
        var keys = mover.TechKeys;
        int stamp = ++ctx.BfsStampValue;
        int best = -1; float bestScore = -1f;
        // 1 跳
        foreach (int nb in grid.Neighbors[mover.Cell])
        {
            if (!grid.IsLandCell(nb) || ctx.R[nb] <= 0f) continue;
            float c1 = ctx.BorderCost(mover.Cell, nb, keys);
            if (c1 <= 0f) continue;
            ctx.BfsStamp[nb] = stamp;
            if (ctx.CellOwner[nb] == -1)
            {
                float s = ctx.R[nb] * c1;
                if (s > bestScore) { bestScore = s; best = nb; }
            }
        }
        // 2 跳
        foreach (int nb in grid.Neighbors[mover.Cell])
        {
            if (ctx.BfsStamp[nb] != stamp) continue;
            float c1 = ctx.BorderCost(mover.Cell, nb, keys);
            if (c1 <= 0f) continue;
            foreach (int nb2 in grid.Neighbors[nb])
            {
                if (ctx.BfsStamp[nb2] == stamp) continue;
                float c2 = ctx.BorderCost(nb, nb2, keys);
                if (c2 <= 0f) continue;
                ctx.BfsStamp[nb2] = stamp;
                if (grid.IsLandCell(nb2) && ctx.R[nb2] > 0f && ctx.CellOwner[nb2] == -1)
                {
                    float s = ctx.R[nb2] * c1 * c2;
                    if (s > bestScore) { bestScore = s; best = nb2; }
                }
            }
        }
        // 3 跳
        foreach (int nb in grid.Neighbors[mover.Cell])
        {
            if (ctx.BfsStamp[nb] != stamp) continue;
            float c1 = ctx.BorderCost(mover.Cell, nb, keys);
            if (c1 <= 0f) continue;
            foreach (int nb2 in grid.Neighbors[nb])
            {
                if (ctx.BfsStamp[nb2] != stamp) continue;
                float c2 = ctx.BorderCost(nb, nb2, keys);
                if (c2 <= 0f) continue;
                foreach (int nb3 in grid.Neighbors[nb2])
                {
                    if (ctx.BfsStamp[nb3] == stamp) continue;
                    float c3 = ctx.BorderCost(nb2, nb3, keys);
                    if (c3 <= 0f) continue;
                    ctx.BfsStamp[nb3] = stamp;
                    if (grid.IsLandCell(nb3) && ctx.R[nb3] > 0f && ctx.CellOwner[nb3] == -1)
                    {
                        float s = ctx.R[nb3] * c1 * c2 * c3;
                        if (s > bestScore) { bestScore = s; best = nb3; }
                    }
                }
            }
        }
        return best;
    }
}

// ══════════════════════════════════════════════════════════════════
// ①i 吞并（Order 47，2026-08-17 用户拍板）：驻扎格被外部势力覆盖（CellOwner≠自己）→ 吞并。
//   消灭"无家 band"中间态（弱 band 的家被强邻影响力覆盖→要么并入要么迁走——
//   不存在"势力色块无人口点"）。条件：非同格共住（共享村合法）+ 覆盖者更强。
//   处置：迁走优先（领地内无主格可逃→保留身份流亡）；无可逃→并入（P×0.5 转移，
//   战斗损耗+同化——被征服部落，其余人口流失）。同频评估（10 tick，守卫不入档）。
// ══════════════════════════════════════════════════════════════════
public sealed class AbsorptionModel : CivModelBase
{
    public override string Name => "吞并";
    public override int Order => 47;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.Tick - ctx.AbsorptionLastEval < 10) return;
        ctx.AbsorptionLastEval = ctx.Tick;
        var snapshot = ctx.Entities.ToArray();
        foreach (var e in snapshot)
        {
            if (e.Dead || e.Cell < 0 || e.Cell >= ctx.Grid.N) continue;
            int overlordId = ctx.CellOwner != null ? ctx.CellOwner[e.Cell] : -1;
            if (overlordId == e.Id || overlordId < 0) continue;   // 家在自己手里
            var overlord = FindById(ctx, overlordId);
            if (overlord == null || overlord.Dead) continue;
            // 同领地/同酋邦成员互驻不吞（自家联盟内部——领地内互驻合法）
            if (e.ChiefdomId >= 0 && e.ChiefdomId == overlord.ChiefdomId) continue;
            if (e.TerritorySize >= 2 && e.TerritoryId == overlord.TerritoryId) continue;
            // 同格共住合法（分裂/驱逐瞬态——同村共享，CellOwner 是代表）→ 不吞
            bool cohabited = false;
            foreach (var m in ctx.Entities)
                if (!m.Dead && m != e && m.Cell == e.Cell) { cohabited = true; break; }
            if (cohabited) continue;
            // 覆盖者必须更强（w 陡化后覆盖已需 2.1×——防御性再确认）
            if (overlord.P < e.P) continue;
            // 处置：迁走优先（领地内无主格可逃——流亡保留身份）
            int exile = FindExileCell(ctx, e);
            if (exile >= 0)
            {
                e.Cell = exile;
                e.LastMigrateTick = ctx.Tick;
            }
            else
            {
                // 并入：P×0.5 转移（战斗损耗+同化），自身消亡
                overlord.P += e.P * 0.5f;
                e.P = 0f;
                e.Dead = true;
            }
        }
    }

    private static CivEntity FindById(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Entities.Count; i++)
            if (ctx.Entities[i].Id == id) return ctx.Entities[i];
        return null;
    }

    /// <summary>迁走目标：领地格内无主格（CellOwner=-1 且 R>0）最高富饶者——留在自己影响圈内。</summary>
    private static int FindExileCell(CivSimContext ctx, CivEntity e)
    {
        var terr = e.Id < (ctx.TerritoryCells?.Length ?? 0) ? ctx.TerritoryCells[e.Id] : null;
        if (terr == null) return -1;
        int best = -1;
        float bestR = 0f;
        foreach (var c in terr)
        {
            if (ctx.CellOwner[c] >= 0) continue;
            if (ctx.R == null || ctx.R[c] <= 0f) continue;
            if (ctx.R[c] > bestR) { bestR = ctx.R[c]; best = c; }
        }
        return best;
    }
}
//   盈余 → 宴席（feasting）→ 声望（Sahlins 1963 Big Man：慷慨积累欠人情网络，个人化、可逆）。
//   BigMan = 声望阈值；酋长 Chief = BigMan + 祖先宗教（Polynesia 谱系合法性——divine kingship，
//   祖先崇拜提供谱系，Kirch 1984）。
//   贡赋流入（Earle 1997 实物税）：酋邦成员盈余 → Contributed 累计（互惠记录——灾年开仓资格）。
//   精英供养（等级结构）：酋长 band 的非生产者比例（EliteFrac）由酋邦贡赋供养——
//   贡赋不足 → 精英饿死（P 降）——等级 = 结构性供养，非盈余自动（回应"盈余>0≠等级"）。
// ══════════════════════════════════════════════════════════════════
public sealed class PrestigeModel : CivModelBase
{
    public override string Name => "声望积累";
    public override int Order => 25;

    public override void Execute(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead) continue;
            float surplus = e.FLast - e.P;   // 实际盈余（人当量；FLast 由 Harvest/RefreshCellState 已算）
            if (surplus > 0f && e.P > 0f)
                e.Prestige += surplus * CivSimContext.PrestigeGainRate;   // **绝对盈余**×rate（宴席=绝对食物量，Sahlins）
            else
                e.Prestige = Mathf.Max(0f, e.Prestige - CivSimContext.PrestigeDecay);   // 可逆（个人化）
            e.IsBigMan = e.Prestige >= CivSimContext.BigManPrestigeThreshold;
            // 酋长：BigMan + 祖先宗教（谱系合法性——祖先份额 > 0；祖先=settle 派生，旧石器无）
            e.IsChief = e.IsBigMan && ShareField.RelFrac(e.ReligionShare, ReligionStage.Ancestor) > 0;
            // 贡赋流入（互惠记录——Earle 实物税）：成员盈余 → 酋邦贡赋累计
            if (e.ChiefdomId >= 0 && surplus > 0f)
                e.Contributed += surplus * CivSimContext.TributeRate;
            // 精英供养（等级结构）：酋长 band 非生产者（祭司/战士/亲信）由酋邦贡赋供养
            if (e.IsChief && e.P > 0f)
            {
                float elite = e.P * CivSimContext.EliteFrac;
                float pool = TributePool(ctx, e);
                if (pool >= elite)
                    ConsumeTribute(ctx, e, elite);
                else
                    e.P = Mathf.Max(1f, e.P - (elite - pool) * 0.5f);   // 贡赋不足 → 精英饿死
            }
        }
    }

    /// <summary>酋邦贡赋池 = Σ成员 Contributed（按 ChiefdomId；滞后酋邦状态可接受——派生同 Territory 模式）。</summary>
    private static float TributePool(CivSimContext ctx, CivEntity chief)
    {
        if (chief.ChiefdomId < 0) return 0f;
        float sum = 0f;
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var m = ctx.Entities[i];
            if (!m.Dead && m.ChiefdomId == chief.ChiefdomId) sum += m.Contributed;
        }
        return sum;
    }

    /// <summary>消耗贡赋（按成员贡献比例扣减——实物税从贡献者处收取）。</summary>
    private static void ConsumeTribute(CivSimContext ctx, CivEntity chief, float amount)
    {
        float remaining = amount;
        for (int i = 0; i < ctx.Entities.Count && remaining > 0f; i++)
        {
            var m = ctx.Entities[i];
            if (m.Dead || m.ChiefdomId != chief.ChiefdomId || m.Contributed <= 0f) continue;
            float take = Mathf.Min(remaining, m.Contributed);
            m.Contributed -= take;
            remaining -= take;
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ①h 酋邦凝聚（Order 46，2026-08-17 酋邦层①）：部落联盟第二层并查集（band→部落→酋邦）。
//   凝聚条件（AND）：① 部落领地边界接触 ② 至少一方有酋长（IsChief）③ 产出结构互补
//   （主导产出类型不同——Halstead-O'Shea 1989：产出不同步 → 再分配价值高）。
//   解散：成员部落 < 2；酋长死亡 → 继承窗口（SuccessionUntil——权力真空 → 继承竞争，
//   Kirch 1984：Polynesia 继承战争常态；窗口内内部冲突概率 ×2——见 ConflictModel）。
//   派生重建（读档入口同用）：确定性（部落对遍历按部落 Id 序，无 Rng）。
// ══════════════════════════════════════════════════════════════════
public sealed class ChiefdomModel : CivModelBase
{
    public override string Name => "酋邦凝聚";
    public override int Order => 46;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.Tick - ctx.ChiefdomLastEval < CivSimContext.ChiefdomEvalEvery) return;   // 频率守卫（与领地同频）
        ctx.ChiefdomLastEval = ctx.Tick;
        Rebuild(ctx);
    }

    /// <summary>确定性重建酋邦（凝聚/解散/继承窗口/成员表）。
    /// ⚠️ 2026-08-17 设计修正（T50 暴露）：全量重算下"酋长死亡→不凝聚→解散"——继承窗口永无机会。
    ///   修正：① 旧酋邦快照检测危机（无酋长且未在危机 → 给 Prestige 最高者设窗口）
    ///   ② 危机成员（SuccessionUntil > Tick）豁免凝聚/解散条件（联盟在酋长死亡后存续，
    ///   窗口过期后正常重算——继承战争窗口，Kirch）。</summary>
    public static void Rebuild(CivSimContext ctx)
    {
        // ── ① 继承危机检测（旧酋邦快照——不依赖本次凝聚）──
        var oldChiefdoms = new Dictionary<int, List<CivEntity>>();
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead || e.ChiefdomId < 0) continue;
            if (!oldChiefdoms.TryGetValue(e.ChiefdomId, out var l)) oldChiefdoms[e.ChiefdomId] = l = new List<CivEntity>();
            l.Add(e);
        }
        foreach (var kv in oldChiefdoms)
        {
            if (kv.Value.Count < CivSimContext.ChiefdomMinTribes) continue;   // 单成员不算酋邦
            bool hasChief = false, inCrisis = false;
            foreach (var m in kv.Value)
            {
                if (m.IsChief) hasChief = true;
                if (m.SuccessionUntil > ctx.Tick) inCrisis = true;
            }
            if (!hasChief && !inCrisis)
            {
                // 酋长死亡（且未在危机中）→ 继承窗口：Prestige 最高者成为继位竞争中心
                CivEntity top = null;
                foreach (var m in kv.Value) if (top == null || m.Prestige > top.Prestige) top = m;
                if (top != null) top.SuccessionUntil = ctx.Tick + CivSimContext.SuccessionWindowTicks;
            }
        }

        // ── 部落级聚合 ──
        var tribes = new Dictionary<int, TribeAgg>();   // TerritoryId → 聚合
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead || e.TerritoryId < 0) continue;
            if (!tribes.TryGetValue(e.TerritoryId, out var agg)) agg = tribes[e.TerritoryId] = new TribeAgg();
            agg.Members.Add(e);
        }
        // 产出主导类型（猎/农/牧——互补判定用）
        foreach (var kv in tribes)
        {
            float hunt = 0, farm = 0, herd = 0;
            foreach (var m in kv.Value.Members)
            {
                hunt += m.FHuntLast;
                farm += m.FFarmLast;
                herd += m.FHerdLast;
                if (m.IsChief) kv.Value.HasChief = true;
                if (m.SuccessionUntil > ctx.Tick) kv.Value.InCrisis = true;   // 继承危机豁免
                if (m.IsBigMan && m.Prestige > kv.Value.MaxPrestige) kv.Value.MaxPrestige = m.Prestige;
            }
            kv.Value.DomOutput = hunt >= farm && hunt >= herd ? 0 : (farm >= herd ? 1 : 2);
        }

        // ── 并查集（酋邦 = 分量内最小部落 id）──
        var parent = new Dictionary<int, int>();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
        foreach (var tid in tribes.Keys) parent[tid] = tid;
        var tribeIds = new List<int>(tribes.Keys);
        tribeIds.Sort();
        for (int a = 0; a < tribeIds.Count; a++)
            for (int b = a + 1; b < tribeIds.Count; b++)
            {
                int ta = tribeIds[a], tb = tribeIds[b];
                var aggA = tribes[ta]; var aggB = tribes[tb];
                if (!aggA.HasChief && !aggB.HasChief && !aggA.InCrisis && !aggB.InCrisis) continue;   // ② 需酋长（或继承危机中——联盟存续）
                if (aggA.DomOutput == aggB.DomOutput) continue;           // ③ 产出互补（主导类型不同）
                if (!TribesTouch(ctx, aggA, aggB)) continue;              // ① 领地边界接触
                Union(ta, tb);
            }

        // ── 填 ChiefdomId/Size + 解散判定（危机成员豁免解散）──
        var comps = new Dictionary<int, List<int>>();   // 根 → 成员部落
        foreach (var tid in tribeIds) { int r = Find(tid); if (!comps.TryGetValue(r, out var l)) comps[r] = l = new List<int>(); l.Add(tid); }
        foreach (var kv in comps)
        {
            var comp = kv.Value;
            bool crisis = false;
            foreach (var tid in comp)
                if (tribes[tid].InCrisis) { crisis = true; break; }
            if (comp.Count < CivSimContext.ChiefdomMinTribes && !crisis)
            {
                // 解散：单部落（且非危机）→ ChiefdomId=-1（清除窗口）
                foreach (var tid in comp)
                    foreach (var m in tribes[tid].Members) { m.ChiefdomId = -1; m.ChiefdomSize = 1; m.SuccessionUntil = -1; }
                continue;
            }
            int chiefdomId = comp[0];   // 分量内最小部落 id（确定性标号）
            foreach (var tid in comp)
                foreach (var m in tribes[tid].Members)
                {
                    m.ChiefdomId = chiefdomId;
                    m.ChiefdomSize = comp.Count;
                    if (m.SuccessionUntil > 0 && m.SuccessionUntil <= ctx.Tick) m.SuccessionUntil = -1;   // 窗口过期清除
                }
        }

        // ── ChiefdomCells 成员表（按酋邦 id；再分配/联盟/供养查询用）──
        if (ctx.ChiefdomCells == null || ctx.ChiefdomCells.Length < 4096) ctx.ChiefdomCells = new List<int>[4096];
        for (int i = 0; i < ctx.ChiefdomCells.Length; i++) { if (ctx.ChiefdomCells[i] == null) ctx.ChiefdomCells[i] = new List<int>(); else ctx.ChiefdomCells[i].Clear(); }
        for (int i = 0; i < ctx.Entities.Count; i++)
        {
            var e = ctx.Entities[i];
            if (e.Dead || e.ChiefdomId < 0) continue;
            if (e.ChiefdomId >= ctx.ChiefdomCells.Length) continue;
            ctx.ChiefdomCells[e.ChiefdomId].Add(e.Id);
        }
    }

    private sealed class TribeAgg
    {
        public readonly List<CivEntity> Members = new();
        public int DomOutput;      // 0=猎 1=农 2=牧（主导产出类型）
        public bool HasChief;
        public bool InCrisis;      // 继承危机中（SuccessionUntil > Tick——联盟存续豁免）
        public float MaxPrestige;
    }

    /// <summary>领地边界接触：A 的领地格有邻格属于 B 的领地格（格集从成员 TerritoryCells 并集）。</summary>
    private static bool TribesTouch(CivSimContext ctx, TribeAgg a, TribeAgg b)
    {
        var cellSetA = new HashSet<int>();
        foreach (var m in a.Members)
        {
            var terr = ctx.TerritoryCells[m.Id];
            if (terr == null) continue;
            foreach (var c in terr) cellSetA.Add(c);
        }
        var cellSetB = new HashSet<int>();
        foreach (var m in b.Members)
        {
            var terr = ctx.TerritoryCells[m.Id];
            if (terr == null) continue;
            foreach (var c in terr) cellSetB.Add(c);
        }
        foreach (var c in cellSetA)
            foreach (int nb in ctx.Grid.Neighbors[c])
                if (cellSetB.Contains(nb)) return true;
        return false;
    }
}
