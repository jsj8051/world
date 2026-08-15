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
            .Register(new TradeModel())      // 物物交换（Order 55，2026-08-18 阶段3 贸易期——Spread 与 Culture 之间）
            .Register(new CultureModel())
            .Register(new ReligionModel())
            .Register(new TerritoryModel())     // 领地凝聚（Order 45，2026-08-17 注册修复：此前从未注册进演化——
                                                //   TerritoryId/Size 全 -1 → 科技传播领地加成失效 + 酋邦永不凝聚）
            .Register(new ChiefdomModel())      // 酋邦凝聚（Order 46，2026-08-17 酋邦层）
            .Register(new AbsorptionModel())    // 吞并（Order 47，2026-08-17 用户拍板：驻扎格被覆盖→并入/迁走）
            .Register(new SettlementModel())    // 聚落（Order 48，2026-08-19 阶段3 聚落设计——场所实体）
            .Register(new StateModel())         // 国家涌现（Order 49，2026-08-16 阶段4——酋邦制度化，docs/阶段4设计-国家涌现.md）
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
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead || !e.IsFarming) continue;
            var terr = ctx.TerritoryOf(e);
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
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead) continue;
            // ⚠️ 2026-08-18 T04 修复（与 RecomputeProduction 同式）：先归零分量——AllocateAndProduce
            //   领地为空时提前 return 0 不赋值分量，不归零则陈旧 FFarm/FHerd 残留（无领地挂产出）。
            e.FFarmLast = 0f; e.FHerdLast = 0f; e.FBerryLast = 0f;
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
            // 候选 = 池内、空格（一格一实体）、且距已选 ≥ 阈值
            var cands = new List<int>();
            foreach (int c in pool)
            {
                bool occupied = ctx.CellTribes != null && ctx.CellTribes[c] != null;
                if (occupied) continue;   // 一格一实体：起源只能选空格
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
            var e = new Tribe
            {
                Id = ctx.NextTribeId++,   // 独立计数器（2026-08-10：Tribes.Count 读档后分叉）
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
            ctx.Tribes.Add(e);
            ctx.CellTribes[pick] = e;   // 一格一实体：起源占据空格
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
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead) continue;
            ctx.CellPop[e.Cell] += e.P;
        }
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
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
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead) continue;
            float f = e.FLast;   // 当 tick 实际产出（RefreshCellState 已算，农业含劳动因子；寒冷区含下限）
            // ⚠️ 2026-08-18 阶段3 存储机制：有效粮食 = 当年产出 + Food 存储缓冲（AccumulateStorage 已做衰变/容量）。
            //   缺口（FLast<P）：从 Food 存储扣，**优先吃易腐（高衰变：浆果/肉），耐储者（谷物）留底**——
            //   这是"特定食物耐储"的机制意义（谷物是饥荒最后防线，新石器革命核心）。
            //   盈余（FLast>P）：按容量入仓（随身 0.06×P → 粮仓 0.5×P×等级倍率）。
            //   饥荒 = 连续歉年吃空存粮 → 缺口扩大 → 饿死（非硬标志）。
            //   ⚠️ 2026-08-19 聚落双池：缺口**先吃随身、再吃粮仓**（粮仓=耐储最后防线——人先耗行囊）；
            //   盈余**随身先满、粮仓后收**（正式存储归聚落，用户拍板）。
            var st = ctx.SettlementOf(e);   // 粮仓（定居部落；null=游群——随身即全部）
            if (e.Stocks != null && e.Stocks.Length == CommodityTable.Count)
            {
                if (f < e.P)
                {
                    float deficit = e.P - f;
                    var foodIdx = FoodIdxByDecayDesc();
                    foreach (int s in foodIdx)
                    {
                        if (deficit <= 0f) break;
                        float take = Mathf.Min(deficit, e.Stocks[s]);
                        e.Stocks[s] -= take;
                        deficit -= take;
                    }
                    if (st != null)
                    {
                        foreach (int s in foodIdx)
                        {
                            if (deficit <= 0f) break;
                            float take = Mathf.Min(deficit, st.Stocks[s]);
                            st.Stocks[s] -= take;
                            deficit -= take;
                        }
                    }
                    f += e.P - f - deficit;   // 存储补足缺口（不足则 f 仍 < P）
                }
                else if (f > e.P)
                {
                    // 盈余入仓：随身谷物（cap CarryFoodCap）→ 粮仓谷物（cap SettleFoodCap×等级倍率）
                    int gi = CommodityTable.Index(CommodityTable.Grain);
                    float surplus = f - e.P;
                    float carryRoom = Mathf.Max(0f, CivSimContext.CarryFoodCap * e.P - e.Stocks[gi]);
                    float toCarry = Mathf.Min(surplus, carryRoom);
                    e.Stocks[gi] += toCarry;
                    surplus -= toCarry;
                    if (st != null && surplus > 0f)
                    {
                        float granCap = CivSimContext.SettleFoodCap * (1f + CivSimContext.SettlementStoragePerLevel * st.Level) * e.P;
                        st.Stocks[gi] += Mathf.Min(surplus, Mathf.Max(0f, granCap - st.Stocks[gi]));
                    }
                }
            }
            if (f <= 0f) continue;
            // ⚠️ 2026-08-17 定居生育跃迁（史实：定居 → 生育间隔缩短/婴儿存活率↑，人口密度 10-50× 游群）
            float rEff = r;
            if (CapabilityTable.Has(ctx, e, "settle")) rEff *= CivSimContext.SettleGrowthMult;   // 1.5
            // ⚠️ 2026-08-19 聚落城市化集聚：占据高等级聚落 → 增长加成（城镇 ×1.25、城市 ×1.5——集聚收益）
            if (st != null && st.Level > 0)
                rEff *= 1f + CivSimContext.SettlementGrowthPerLevel * st.Level;
            float factor = Mathf.Exp(rEff * (1f - e.P / f));
            // 酋邦再分配互惠（2026-08-17：Halstead-O'Shea 1989 坏年景开仓——贡献过才受赈）：
            //   成员 band 曾交贡赋（Contributed>0）→ 灾年缺口 ×0.5（酋长开仓）；未贡献不受赈
            if (factor < 1f && e.ChiefdomId >= 0 && e.Contributed > 0f)
                factor = 1f + (factor - 1f) * CivSimContext.TributeRelief;
            e.P *= factor;
            if (e.P < 1f) { e.P = 0f; e.Dead = true; }   // 饿死灭绝
        }
    }

    /// <summary>Food 类商品索引，按衰变率**降序**（易腐先吃：浆果/肉 → 谷物留底）。
    /// 静态缓存（目录固定）；确定性（同目录同序）。</summary>
    private static int[] _foodIdxByDecay;
    private static int[] FoodIdxByDecayDesc()
    {
        if (_foodIdxByDecay != null) return _foodIdxByDecay;
        var list = new List<int>();
        for (int s = 0; s < CommodityTable.Count; s++)
            if (CommodityTable.All[s].Kind == CommodityKind.Food) list.Add(s);
        list.Sort((a, b) => CommodityTable.All[b].BaseDecay.CompareTo(CommodityTable.All[a].BaseDecay));
        _foodIdxByDecay = list.ToArray();
        return _foodIdxByDecay;
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
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
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

        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
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

        foreach (var e in ctx.Tribes)
            if (!e.Dead) parent[e.Id] = e.Id;
        // 邻格凝聚边（一格一实体：无同格对）：相邻占据格的部落，同语言群 → 凝聚
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var ea = ctx.CellTribes[i];
            if (ea == null || ea.Dead) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var eb = ctx.CellTribes[nb];
                if (eb == null || eb.Dead) continue;
                if (ShareField.DomKey(ea.CultureGroupShare) == ShareField.DomKey(eb.CultureGroupShare))
                    Union(ea.Id, eb.Id);
            }
        }
        // 填分量：标号 = 分量最小实体 Id（确定性）；size = 分量实体数
        var sizes = new Dictionary<int, int>();
        var mins = new Dictionary<int, int>();
        foreach (var e in ctx.Tribes)
        {
            if (e.Dead) continue;
            int root = Find(e.Id);
            sizes[root] = sizes.TryGetValue(root, out var v) ? v + 1 : 1;
            if (!mins.TryGetValue(root, out var m) || e.Id < m) mins[root] = e.Id;
        }
        foreach (var e in ctx.Tribes)
        {
            if (e.Dead) continue;
            int root = Find(e.Id);
            e.TerritoryId = mins[root];
            e.TerritorySize = sizes[root];
        }
    }

    private static Tribe MaxPop(List<Tribe> list)
    {
        Tribe best = null;
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
        // ── 一格一实体：传播只在"相邻有部落的格"之间（占据格彼此球面相邻 → 领地接触）。
        //   不跨空格传播（邻近不行），无同格对（一格一实体）。──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellTribes[i];
            if (a == null || a.Dead) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var b = ctx.CellTribes[nb];
                if (b == null || b.Dead) continue;
                // 闭塞区域：跨格传播 ×= BorderCost（地形障碍 × 气候相似度；A→B 用 A 的科技判定障碍突破）
                float cost = ctx.BorderCost(i, nb, a.TechKeys);
                if (cost <= 0f) continue;
                SpreadTech(ctx, a, b, cost);
                SpreadTech(ctx, b, a, cost);
            }
        }
    }

    private static Tribe MaxPop(List<Tribe> list)
    {
        var best = list[0];
        for (int k = 1; k < list.Count; k++)
            if (!list[k].Dead && list[k].P > best.P) best = list[k];
        return best;
    }

    /// <summary>领地传播乘数：同领地 ×1.5（整合加成）；至少一方是正式领地（≥2 band）→ ×0.5（跨边界软冲突）；散兵部落间 ×1（BorderCost 已有）。</summary>
    internal static float TerritoryMult(Tribe a, Tribe b)
    {
        if (a.TerritoryId >= 0 && a.TerritoryId == b.TerritoryId) return CivSimContext.TerritorySpreadMult;
        if (a.TerritorySize >= 2 || b.TerritorySize >= 2) return CivSimContext.CrossBorderSpreadMult;
        return 1f;
    }

    /// <summary>技术传播 from → to（to 缺 from 的技术且依赖满足 → 按概率获得）。
    /// ⚠️ 2026-08-10 确定性修复：HashSet 遍历顺序依赖构建历史（读档重建 Add 顺序 ≠ 演化布局）→
    ///    同 Rng 数对应不同 key → 读档续跑分叉。改为**排序遍历**（与布局无关，ctx 缓冲无分配）。</summary>
    private void SpreadTech(CivSimContext ctx, Tribe from, Tribe to, float border = 1f)
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
        if (ctx.LockedUntil == null || ctx.Tribes.Count < 2) return;
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
            var eo = FindTribe(ctx, owner);
            var ec = FindTribe(ctx, ch);
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
            // ⚠️ 2026-08-16 阶段4 国家（docs/阶段4设计-国家涌现.md §2.4）：
            //   ① 内部秩序：同国家冲突概率 ×0.25（StateInternalConflictMult——Weber 强制力垄断）
            //   ② 继承制度化：国家成员间继承窗口 ×2 豁免（王朝——制度化缓和继承战争，非消除；
            //      StateModel Order 49 在 Conflict 75 前已重建 StateId → 读当前值无分叉）
            float conflictChance = ConflictChanceOf(ctx, ec, eo);
            if (ctx.Rng.NextDouble() >= conflictChance) continue;
            ResolveConflict(ctx, ec, eo, c);
            if (++conflictsThisTick >= 3) return;   // 单 tick 最多 3 场（性能/爆炸防护）
        }
    }

    /// <summary>冲突触发概率（2026-08-16 提取为纯函数——T67 继承制度化直接断言，避免 0.01 概率采样噪声）。
    /// 基础 ConflictChance × 政体整合倍率 × 继承窗口倍率：
    ///   同国家：×0.25（内部秩序，Weber 强制力垄断）+ 继承窗口 ×2 豁免（王朝制度化——同国不内战）；
    ///   同酋邦：×0.5（酋长仲裁）+ 继承窗口 ×2（权力真空 → 继承战争，Kirch）；
    ///   跨邦：×1 + 窗口 ×2。</summary>
    internal static float ConflictChanceOf(CivSimContext ctx, Tribe a, Tribe b)
    {
        float chance = CivSimContext.ConflictChance;
        bool sameChiefdom = a.ChiefdomId >= 0 && a.ChiefdomId == b.ChiefdomId;
        if (sameChiefdom)
        {
            bool sameState = a.StateId >= 0 && a.StateId == b.StateId;
            chance *= sameState
                ? CivSimContext.StateInternalConflictMult
                : CivSimContext.InternalConflictMult;
        }
        bool succession = a.SuccessionUntil > ctx.Tick || b.SuccessionUntil > ctx.Tick;
        bool stateSuccessionExempt = a.StateId >= 0 && a.StateId == b.StateId;   // 同国家 → 王朝豁免 ×2
        if (succession && !stateSuccessionExempt) chance *= CivSimContext.SuccessionConflictMult;
        return chance;
    }

    internal static void ResolveConflict(CivSimContext ctx, Tribe challenger, Tribe owner, int cell)
    {
        // 胜率：P×MilitMult 对比（武器科技加成；随机——弱 band 可爆冷）
        float pC = challenger.P * TechTable.MilitaryMult(challenger.TechKeys);
        float pO = owner.P * TechTable.MilitaryMult(owner.TechKeys);
        // ⚠️ 2026-08-17 联盟合力（Kirch：防御方是酋邦时，入侵者面对酋邦总力量——人多势众，非加成系数）
        if (owner.ChiefdomId >= 0)
        {
            for (int i = 0; i < ctx.Tribes.Count; i++)
            {
                var m = ctx.Tribes[i];
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
                if (ctx.CellTribes[loser.Cell] == loser) ctx.CellTribes[loser.Cell] = null;   // 一格一实体
                loser.Cell = target;
                loser.LastMigrateTick = ctx.Tick;
                ctx.CellTribes[target] = loser;
                ctx.Migrations++;
            }
        }
        ctx.Conflicts++;
    }

    private static Tribe FindTribe(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Tribes.Count; i++)
            if (ctx.Tribes[i].Id == id && !ctx.Tribes[i].Dead) return ctx.Tribes[i];
        return null;
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑳ 物物交换（Order 55，2026-08-18 阶段3 贸易期；docs/阶段3设计-贸易机制.md）：
//    Material 商品的**出口**——互通有无，为专业化/文明整合铺路。
//    触发：领地边界接触（TerritoryTouches 共享判定，同酋邦凝聚——用户拍板"接触即互通"）。
//    商品流（比较优势）：逐商品比**人均库存**（Stocks[i]/P——相对丰缺）——
//      你多我少才换（无货币 → 无单向贸易，双重巧合需求）；交换量 = TradeRate×人均差×min(P)×距离折减。
//    距离折减：边界格距 d → ×(1/(1+0.5d))（接触对 d=1 → ×0.667；黑曜石随距衰减史实）。
//    食物保底：Food 出口后出口方人均 ≥ TradeFoodFloor×P（5 年存粮——饥荒最后防线）。
//    确定性：无 Rng、固定对序（部落表序 i<j）、顺序应用、纯 Stocks 转移（v12 已入档）——
//      读档续跑无分叉（T04 保证）；SettleDerived 不碰（副作用同 AccumulateStorage 层语义）。
// ══════════════════════════════════════════════════════════════════
public sealed class TradeModel : CivModelBase
{
    public override string Name => "物物交换";
    public override int Order => 55;

    public override void Execute(CivSimContext ctx)
    {
        // 空间预过滤：领地 = 驻扎点影响圈 R 内格——两领地可能接触仅当驻扎点距 ≤ 2R+1 格
        // （确定性：纯几何；把 O(对²×领地格) 降为 O(对²) 距离检查——全量演化性能防线）
        float reachKm = (2 * CivSimContext.InfluenceRadius + 1) * Mathf.Sqrt(ctx.Grid.CellAreaKm2);
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var a = ctx.Tribes[i];
            if (a.Dead || a.P <= 0f) continue;
            EnsureStocks(a);
            for (int j = i + 1; j < ctx.Tribes.Count; j++)
            {
                var b = ctx.Tribes[j];
                if (b.Dead || b.P <= 0f) continue;
                EnsureStocks(b);
                if (ctx.Grid.DistKm(a.Cell, b.Cell) > reachKm) continue;   // 远隔两地无接触可能
                if (!CivSimContext.TerritoryTouches(ctx, a, b)) continue;   // 领地边界接触（同酋邦判定）
                int d = CivSimContext.BoundaryDist(ctx, a, b);
                float mult = 1f / (1f + CivSimContext.TradeDistanceRate * d);   // 运输成本（接触对 d=1 → ×0.667）
                if (mult <= 0f) continue;
                Exchange(ctx, a, b, mult);
            }
        }
    }

    /// <summary>部落商品池 = 随身 + 占据聚落粮仓（2026-08-19 双池：正式存储归聚落——贸易互通含其仓）。</summary>
    private static float PoolOf(CivSimContext ctx, Tribe e, int s)
    {
        float v = e.Stocks != null && s < e.Stocks.Length ? e.Stocks[s] : 0f;
        var st = ctx.SettlementOf(e);
        if (st != null && st.Stocks != null && s < st.Stocks.Length) v += st.Stocks[s];
        return v;
    }

    /// <summary>逐商品等量交换（固定商品序 = 目录序，确定性；跨商品天然成对——A 出 X、B 出 Y 即双重巧合）。</summary>
    private static void Exchange(CivSimContext ctx, Tribe a, Tribe b, float mult)
    {
        for (int s = 0; s < CommodityTable.Count; s++)
        {
            float gap = PoolOf(ctx, a, s) / a.P - PoolOf(ctx, b, s) / b.P;   // A 人均 − B 人均（正 = A 盈余）
            if (Mathf.Abs(gap) < CivSimContext.TradeMinGap) continue;   // 需求匹配不足（无货币 → 无单向贸易）
            float amount = Mathf.Abs(gap) * CivSimContext.TradeRate * Mathf.Min(a.P, b.P) * mult;
            if (amount <= 0f) continue;
            if (gap > 0f) Transfer(ctx, a, b, s, amount);
            else Transfer(ctx, b, a, s, amount);
        }
    }

    /// <summary>单商品转移 from → to（等量守恒；食物出口保底——出口后总池人均不低于 TradeFoodFloor×P；
    /// 出方：粮仓先出（卖存粮）→ 随身后出；入方：粮仓先收（定居）→ 随身（游群）。
    /// 演化级统计：TradeEvents/TradeVolume 累计——2026-08-19 贸易量级观测）。</summary>
    private static void Transfer(CivSimContext ctx, Tribe from, Tribe to, int s, float amount)
    {
        var def = CommodityTable.All[s];
        if (def.Kind == CommodityKind.Food)
            amount = Mathf.Min(amount, Mathf.Max(0f, PoolOf(ctx, from, s) - CivSimContext.TradeFoodFloor * from.P));   // 保底（5 年存粮）
        amount = Mathf.Min(amount, PoolOf(ctx, from, s));
        if (amount <= 0f) return;
        // 出方：粮仓先出 → 随身后出
        float moved = 0f;
        var fs = ctx.SettlementOf(from);
        if (fs != null && fs.Stocks != null && s < fs.Stocks.Length && fs.Stocks[s] > 0f)
        {
            float take = Mathf.Min(amount, fs.Stocks[s]);
            fs.Stocks[s] -= take;
            moved += take;
        }
        if (amount - moved > 0f)
        {
            float take = Mathf.Min(amount - moved, from.Stocks[s]);
            from.Stocks[s] -= take;
            moved += take;
        }
        if (moved <= 0f) return;
        // 入方：粮仓先收（定居）→ 随身（游群；不查上限——AccumulateStorage 下 tick clamp）
        var ts = ctx.SettlementOf(to);
        if (ts != null && ts.Stocks != null && s < ts.Stocks.Length)
            ts.Stocks[s] += moved;
        else
            to.Stocks[s] += moved;
        ctx.TradeVolume += moved;
        ctx.TradeEvents++;
    }

    private static void EnsureStocks(Tribe e)
    {
        if (e.Stocks == null || e.Stocks.Length != CommodityTable.Count) e.Stocks = CommodityTable.NewStocks();
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
        // ── 相邻格：Axelrod 相似度互动（一格一实体：无同格聚合，只做邻格互动）──
        // ⚠️ 2026-08-19 修复（死代码）：旧版 sim = 同文化0.5+同群0.5，门槛 sim<=0.5 continue + rate=sim−0.5——
        //   唯一有传播意义的组合（同语言群、异文化）恰好 sim=0.5 被门槛挡死且 rate=0 → 文化永不混合
        //   （实测 60 tick 零传播）→ 单一 lineage 靠分裂无限扩张 → 地图大片单色。
        //   新语义（Axelrod）：**同语言群、异文化**的相邻部落互动（语言群=沟通能力——同群能交流才传文化，
        //   异群保持边界分界）；弱方（P 小）主导文化向强方主导文化转移（速率 CultureSpreadRate×BorderCost）。
        //   同文化对跳过（无转移语义——旧版同 key 自转移还瞬态污染份额场：次席重复 key）。
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellTribes[i];
            if (a == null || a.Dead) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var b = ctx.CellTribes[nb];
                if (b == null || b.Dead) continue;
                string domA = ShareField.DomKey(a.CultureShare);
                string domB = ShareField.DomKey(b.CultureShare);
                if (domA == null || domB == null || domA == domB) continue;   // 无文化/已同 → 无转移
                string grpA = ShareField.DomKey(a.CultureGroupShare);
                string grpB = ShareField.DomKey(b.CultureGroupShare);
                if (grpA == null || grpB == null || grpA != grpB) continue;   // 异语言群不传（边界文化分界）
                float cost = ctx.BorderCost(i, nb, a.TechKeys);
                if (cost <= 0f) continue;   // 闭塞区域：跨格文化转移 ×= BorderCost（障碍区交流弱 → 边界差异保持）
                var strong = a.P >= b.P ? a : b;
                var weak = strong == a ? b : a;
                string strongDom = ShareField.DomKey(strong.CultureShare);
                string weakDom = ShareField.DomKey(weak.CultureShare);
                int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.CultureSpreadRate * cost);
                if (amt <= 0) continue;
                ShareField.Shift(weak.CultureShare, weakDom, strongDom, amt);   // 弱方文化向强方文化转移
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

    private static Tribe MaxPop(List<Tribe> list)
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
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
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

        // ── 传播（接触，0.02/tick 只向更高阶段；一格一实体：仅相邻占据格之间）──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellTribes[i];
            if (a == null || a.Dead) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;
                var b = ctx.CellTribes[nb];
                if (b == null || b.Dead) continue;
                // 闭塞区域：跨格宗教传播 ×= BorderCost（障碍区传教弱）
                float cost = ctx.BorderCost(i, nb, a.TechKeys);
                if (cost <= 0f) continue;
                SpreadReligion(ctx, a, b, cost);
                SpreadReligion(ctx, b, a, cost);
                // ⚠️ 2026-08-19 修复：宗教图层显示 relig_N **派别**——旧版只传 5 段（泛灵→…），
                //   派别只靠分裂继承 → 不横向混合 → 大片单色（与文化传播同根因）。派别随接触转移
                //   （弱方派别向强方派别转移，速率同 5 段传播 ReligionSpreadRate×BorderCost）。
                SpreadSect(ctx, a, b, cost);
            }
        }

        // ── 一格一实体：格级宗教同化已无意义（单部落/格），删除——宗教仅靠实体级升级 + 邻格传播
    }

    /// <summary>宗教派别（relig_N）横向传播：相邻部落弱方（P 小）派别向强方派别转移。
    /// 无 Rng、固定遍历序 + P 比较 → 确定性（读档续跑无分叉）。</summary>
    private static void SpreadSect(CivSimContext ctx, Tribe a, Tribe b, float border)
    {
        var strong = a.P >= b.P ? a : b;
        var weak = strong == a ? b : a;
        string strongSect = ShareField.DomKey(strong.ReligionCultShare);
        string weakSect = ShareField.DomKey(weak.ReligionCultShare);
        if (strongSect == null || weakSect == null || strongSect == weakSect) return;   // 无派别/已同 → 无转移
        int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionSpreadRate * border);
        if (amt <= 0) return;
        ShareField.Shift(weak.ReligionCultShare, weakSect, strongSect, amt);
    }

    /// <summary>宗教传播：高阶实体主导宗教份额流向低阶实体（只向更高阶段）。</summary>
    private static void SpreadReligion(CivSimContext ctx, Tribe from, Tribe to, float border = 1f)
    {
        string domFrom = ShareField.DomReligion(from.ReligionShare);
        string domTo = ShareField.DomReligion(to.ReligionShare);
        int fi = ShareField.ReligionIndex(domFrom);
        int ti = ShareField.ReligionIndex(domTo);
        if (fi <= ti) return;   // 只向更高阶段（不回头污染）
        int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionSpreadRate * border);
        ShareField.RelTransfer(to.ReligionShare, domTo, domFrom, amt);
    }

    private static Tribe MaxPop(List<Tribe> list)
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
        var snapshot = ctx.Tribes.ToArray();
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
            var nt = new Tribe
            {
                Id = ctx.NextTribeId++,   // 独立计数器（2026-08-10）
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
            ctx.Tribes.Add(nt);
            ctx.CellTribes[target] = nt;   // 一格一实体：分裂殖民到空格
            ctx.Fissions++;
        }

        // ── 饥饿迁移（2026-08-10 影响力场模型）：饿（F<D）→ 驻扎点搬家到 1-3 跳内最高富饶度无主格。
        //    落脚必须无主（CellOwner==-1——有主格禁入，冲突未实现）；旧领地格下 tick 场重算自动废弃。
        //    冷却 MigrateCooldown tick 防抖动（连续饿会再次触发——游走 band 觅食迁徙）。
        var snap2 = ctx.Tribes.ToArray();
        foreach (var t in snap2)
        {
            if (t.Dead) continue;
            if (t.LastMigrateTick >= 0 && ctx.Tick - t.LastMigrateTick < CivSimContext.MigrateCooldown) continue;
            if (!ctx.IsStarving(t)) continue;
            int target = PickMigrateTarget(ctx, t);
            if (target < 0) continue;   // 无处可去（全被占）→ 饿死路径（GrowthModel）
            if (ctx.CellTribes[t.Cell] == t) ctx.CellTribes[t.Cell] = null;
            t.Cell = target;
            t.LastMigrateTick = ctx.Tick;
            ctx.CellTribes[target] = t;
            ctx.Migrations++;
        }
    }

    /// <summary>迁徙/殖民目标：起始格 BFS 至多 ColonizeRadius 跳；可穿过任意可穿越陆地（BorderCost>0）
    /// 寻找**未定居**目标（CellTribes==null，即格上无实体；CellOwner 影响力场会广泛圈地，不做定居判定）。
    /// 按 (R × 路径 BorderCost 衰减) 择优。确定性：时间戳标记 + 固定遍历顺序。
    /// 阶段2：原 1-3 跳手写展开致"想分裂却无目标" → 改 BFS 到 6 跳。</summary>
    internal static int PickMigrateTarget(CivSimContext ctx, Tribe mover)
    {
        var grid = ctx.Grid;
        var keys = mover.TechKeys;
        int best = -1; float bestScore = -1f;
        int maxLayer = CivSimContext.ColonizeRadius;
        // 复用 BfsStamp 作"本 tick 已访问"标记（stamp 值递增，区分 tick）
        int stamp = ++ctx.BfsStampValue;
        var q = new Queue<(int cell, int layer, float cost)>();
        foreach (int nb in grid.Neighbors[mover.Cell])
        {
            if (!grid.IsLandCell(nb) || ctx.R[nb] <= 0f) continue;
            float c1 = ctx.BorderCost(mover.Cell, nb, keys);
            if (c1 <= 0f) continue;
            ctx.BfsStamp[nb] = stamp;
            q.Enqueue((nb, 1, c1));
        }
        while (q.Count > 0)
        {
            var (c, layer, ccost) = q.Dequeue();
            // 目标 = 可穿越且未定居（CellTribes==null）；CellOwner 影响力圈地不算定居
            if (ctx.CellTribes[c] == null)
            {
                float s = ctx.R[c] * ccost;
                if (s > bestScore) { bestScore = s; best = c; }
            }
            if (layer >= maxLayer) continue;   // 达最大跳数不再扩展
            foreach (int nb in grid.Neighbors[c])
            {
                if (!grid.IsLandCell(nb) || ctx.R[nb] <= 0f) continue;
                if (ctx.BfsStamp[nb] == stamp) continue;
                float c2 = ctx.BorderCost(c, nb, keys);
                if (c2 <= 0f) continue;
                ctx.BfsStamp[nb] = stamp;
                q.Enqueue((nb, layer + 1, ccost * c2));
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
        var snapshot = ctx.Tribes.ToArray();
        foreach (var e in snapshot)
        {
            if (e.Dead || e.Cell < 0 || e.Cell >= ctx.Grid.N) continue;
            int overlordId = ctx.CellOwner != null ? ctx.CellOwner[e.Cell] : -1;
            if (overlordId == e.Id || overlordId < 0) continue;   // 家在自己手里
            var overlord = FindById(ctx, overlordId);
            if (overlord == null || overlord.Dead) continue;
            // ⚠️ 2026-08-17 v4 修正：恢复同领地/同酋邦豁免（v3 过度——领地内吞并导致部落
            //   聚合崩溃，T22 领地 30→2）。联盟内显示同色（PowerIdOf 同 TerritoryId/ChiefdomId
            //   → 同势力色）→ 弱成员驻扎格被覆盖也显示部落色（色块有强成员驻扎格=有人口）——
            //   不产生无人口势力。散兵（跨势力被覆盖，含同格共住）→ 吞并（用户拍板）。
            if (e.ChiefdomId >= 0 && e.ChiefdomId == overlord.ChiefdomId) continue;
            if (e.TerritorySize >= 2 && e.TerritoryId == overlord.TerritoryId) continue;
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

    private static Tribe FindById(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Tribes.Count; i++)
            if (ctx.Tribes[i].Id == id) return ctx.Tribes[i];
        return null;
    }

    /// <summary>迁走目标：领地格内无主格（CellOwner=-1 且 R>0）最高富饶者——留在自己影响圈内。</summary>
    private static int FindExileCell(CivSimContext ctx, Tribe e)
    {
        var terr = e.Id < (ctx.TerritoryCells?.Length ?? 0) ? ctx.TerritoryOf(e) : null;
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
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead) continue;
            float surplus = e.FLast - e.P;   // 实际盈余（人当量；FLast 由 Harvest/RefreshCellState 已算）
            if (surplus > 0f && e.P > 0f)
                e.Prestige += surplus * CivSimContext.PrestigeGainRate;   // **绝对盈余**×rate（宴席=绝对食物量，Sahlins）
            else
                e.Prestige = Mathf.Max(0f, e.Prestige - CivSimContext.PrestigeDecay);   // 可逆（个人化）
            // ⚠️ 2026-08-18 阶段3：领袖标记走共享派生函数（与 SettleDerived 同式）——无两套实现分叉
            CivEngine.DeriveLeadership(e);
            // 贡赋流入（互惠记录——Earle 实物税）：成员盈余 → 酋邦贡赋累计
            // ⚠️ 2026-08-16 阶段4 税制化：国家成员税率 ×2（StateTributeRate）——税 vs 互惠贡赋
            //   （滞后 1 tick 读 StateId：SettleDerived 重建值 ≡ 演化末值 → 读档续跑无分叉，T04）
            if (e.ChiefdomId >= 0 && surplus > 0f)
                e.Contributed += surplus * (e.StateId >= 0 ? CivSimContext.StateTributeRate : CivSimContext.TributeRate);
            // 精英供养（等级结构）：酋长 band 非生产者（祭司/战士/亲信）由酋邦贡赋供养
            // ⚠️ 2026-08-16 阶段4 官僚化：国家酋长精英比例 ×2.5（StateEliteFrac）——官僚体系更庞大
            if (e.IsChief && e.P > 0f)
            {
                float elite = e.P * (e.StateId >= 0 ? CivSimContext.StateEliteFrac : CivSimContext.EliteFrac);
                float pool = TributePool(ctx, e);
                if (pool >= elite)
                    ConsumeTribute(ctx, e, elite);
                else
                    e.P = Mathf.Max(1f, e.P - (elite - pool) * 0.5f);   // 贡赋不足 → 精英饿死
            }
        }
    }

    /// <summary>酋邦贡赋池 = Σ成员 Contributed（按 ChiefdomId；滞后酋邦状态可接受——派生同 Territory 模式）。</summary>
    private static float TributePool(CivSimContext ctx, Tribe chief)
    {
        if (chief.ChiefdomId < 0) return 0f;
        float sum = 0f;
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var m = ctx.Tribes[i];
            if (!m.Dead && m.ChiefdomId == chief.ChiefdomId) sum += m.Contributed;
        }
        return sum;
    }

    /// <summary>消耗贡赋（按成员贡献比例扣减——实物税从贡献者处收取）。</summary>
    private static void ConsumeTribute(CivSimContext ctx, Tribe chief, float amount)
    {
        float remaining = amount;
        for (int i = 0; i < ctx.Tribes.Count && remaining > 0f; i++)
        {
            var m = ctx.Tribes[i];
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

    /// <summary>确定性重建酋邦（庇护/解散/继承窗口/成员表）。
    /// ⚠️ 2026-08-19 重构（用户拍板"合理机制衬托"，反对硬上限）：**至尊酋长庇护（patronage）**——
    ///   旧版领地级并查集"任一方有酋长即合并"→ 语言领地内酋长遍地 → 3000+ band 超级酋邦
    ///   （n128 实测 3 个 350 万人口酋邦——史实不存在，酋邦上限数万）。
    ///   新机制：酋邦 = 至尊酋长的个人贡赋-再分配圈（Sahlins 个人化权力 / Earle 再分配半径 /
    ///   Kirch 继承分裂）——规模从 ChiefReach 半径涌现，无任何硬性规模上限。
    ///   ① 酋长 = 自己酋邦的中心（ChiefdomId = 自身 Id）；
    ///   ② 非酋长 band 选 ChiefReach 内 Prestige 最高的酋长为庇护人（平局 → 较小 Id）；
    ///   ③ 半径内无酋长 → 独立（-1）；同语言网络内多酋长 → 竞争的中小酋邦（语言族大 ≠ 政治统一，
    ///      Walker & Hamilton 2010 班图/南岛扩张：社会复杂性低而语言多样性高）。
    ///   ④ 继承窗口保留（酋长消亡 → 权力真空 → 继承竞争，Kirch；窗口内冲突 ×2——ConflictModel）。
    ///   确定性：酋长按（Prestige 降序, Id 升序）遍历 + BFS 固定序（无 Rng）。
    /// ⚠️ 2026-08-17 设计修正（T50 暴露）：全量重算下"酋长死亡→不凝聚→解散"——继承窗口永无机会。
    ///   修正：① 旧酋邦快照检测危机（无酋长且未在危机 → 给 Prestige 最高者设窗口）
    ///   ② 危机成员（SuccessionUntil > Tick）豁免凝聚/解散条件（联盟在酋长死亡后存续，
    ///   窗口过期后正常重算——继承战争窗口，Kirch）。</summary>
    public static void Rebuild(CivSimContext ctx)
    {
        // ── ① 继承危机检测（旧酋邦快照——不依赖本次凝聚）──
        var oldChiefdoms = new Dictionary<int, List<Tribe>>();
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead || e.ChiefdomId < 0) continue;
            if (!oldChiefdoms.TryGetValue(e.ChiefdomId, out var l)) oldChiefdoms[e.ChiefdomId] = l = new List<Tribe>();
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
                Tribe top = null;
                foreach (var m in kv.Value) if (top == null || m.Prestige > top.Prestige) top = m;
                if (top != null) top.SuccessionUntil = ctx.Tick + CivSimContext.SuccessionWindowTicks;
            }
        }

        // ── ② 收集酋长（Prestige 降序 + Id 升序——确定性遍历序：先处理声望最高者）──
        var chiefs = new List<Tribe>();
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead || !e.IsChief || e.Cell < 0 || e.Cell >= ctx.Grid.N) continue;
            chiefs.Add(e);
        }
        chiefs.Sort((x, y) => y.Prestige != x.Prestige ? y.Prestige.CompareTo(x.Prestige) : x.Id.CompareTo(y.Id));

        // ── ③ 庇护 BFS：每酋长在 ChiefReach 内宣告庇护（band 只认声望更高的酋长）──
        //    Id 索引缓冲（Id 有空洞——NextTribeId 分配，勿用列表索引）
        int bufLen = Math.Max(ctx.NextTribeId, ctx.Tribes.Count + 1);
        var bestPrestige = new float[bufLen];
        var bestChief = new int[bufLen];
        System.Array.Fill(bestChief, -1);
        foreach (var c in chiefs)
        {
            ctx.BfsRadius(c.Cell, CivSimContext.ChiefReach, (cell, _) =>
            {
                var e = ctx.CellTribes[cell];
                if (e == null || e.Dead || e.IsChief) return;   // 酋长不隶属（互相竞争）
                if (e.Id >= bestPrestige.Length) return;
                if (c.Prestige > bestPrestige[e.Id])   // 平局不覆盖（遍历序保证低 Id 先到）
                {
                    bestPrestige[e.Id] = c.Prestige;
                    bestChief[e.Id] = c.Id;
                }
            }, landOnly: true);   // 庇护沿可居土地（不跨海）
        }

        // ── ④ 分配 ChiefdomId/Size（酋长 = 自己中心；band = 最优庇护人）──
        var memberCount = new Dictionary<int, int>();   // chiefId → 成员数（含酋长自己）
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead) continue;
            if (e.SuccessionUntil > 0 && e.SuccessionUntil <= ctx.Tick) e.SuccessionUntil = -1;   // 窗口过期清除
            if (e.TerritoryId < 0) { e.ChiefdomId = -1; e.ChiefdomSize = 1; continue; }   // 无领地不入邦
            if (e.IsChief)
            {
                e.ChiefdomId = e.Id;   // 酋长 = 自己酋邦的中心
                memberCount[e.Id] = memberCount.TryGetValue(e.Id, out var n) ? n + 1 : 1;
                continue;
            }
            int pc = e.Id < bestChief.Length ? bestChief[e.Id] : -1;
            if (pc < 0) { e.ChiefdomId = -1; e.ChiefdomSize = 1; continue; }   // 半径内无酋长 → 独立
            e.ChiefdomId = pc;
            memberCount[pc] = memberCount.TryGetValue(pc, out var m) ? m + 1 : 1;
        }

        // ── ⑤ 解散：< ChiefdomMinTribes → -1（单人酋邦不成邦）──
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead || e.ChiefdomId < 0) continue;
            if (memberCount.TryGetValue(e.ChiefdomId, out var n) && n < CivSimContext.ChiefdomMinTribes)
            {
                e.ChiefdomId = -1;
                e.ChiefdomSize = 1;
            }
            else if (memberCount.TryGetValue(e.ChiefdomId, out var m))
            {
                e.ChiefdomSize = m;
            }
        }

        // ── ⑥ ChiefdomCells 成员表（按酋邦 id；再分配/联盟/供养查询用）──
        // ⚠️ 2026-08-17 索引体系修复：动态扩容（旧版固定 4096——ChiefdomId 超限直接 continue 丢成员）
        if (ctx.ChiefdomCells == null || ctx.ChiefdomCells.Length < 4096)
        {
            ctx.ChiefdomCells = new List<int>[4096];
            for (int i = 0; i < ctx.ChiefdomCells.Length; i++) ctx.ChiefdomCells[i] = new List<int>();
        }
        for (int i = 0; i < ctx.ChiefdomCells.Length; i++) ctx.ChiefdomCells[i].Clear();   // 重建前清空
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead || e.ChiefdomId < 0) continue;
            if (e.ChiefdomId >= ctx.ChiefdomCells.Length)
            {
                int newCap = e.ChiefdomId + 256;
                var grown = new List<int>[newCap];
                Array.Copy(ctx.ChiefdomCells, grown, ctx.ChiefdomCells.Length);
                for (int g = ctx.ChiefdomCells.Length; g < newCap; g++) grown[g] = new List<int>();
                ctx.ChiefdomCells = grown;
            }
            ctx.ChiefdomCells[e.ChiefdomId].Add(e.Id);
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ①j 聚落（Order 48，2026-08-19 阶段3 聚落设计；docs/阶段3设计-聚落实体.md）：
//    物理场所实体——农业部落（settle）的驻扎点固化；场所比人长寿。
//    形成：IsFarming 部落无聚落 → 所在格废墟接管（继承 Level）/新建（Level 0）；
//    存续：部落迁徙/灭绝 → 聚落 OccupantId=-1（废墟——实体保留）；
//    等级：Dwell（定居时长）× P 阈值纯函数（无 Rng，读档续跑无分叉）；都城（至尊酋长聚落）阈值减半；
//    收益：存储容量 ×(1+0.5×Level)（AccumulateStorage）、增长 ×(1+0.25×Level)（GrowthModel）。
// ══════════════════════════════════════════════════════════════════
public sealed class SettlementModel : CivModelBase
{
    public override string Name => "聚落";
    public override int Order => 48;

    public override void Execute(CivSimContext ctx)
    {
        // ① 占据同步：已死/迁走部落释放聚落（废墟——场所比人长寿）
        for (int i = 0; i < ctx.Settlements.Count; i++)
        {
            var s = ctx.Settlements[i];
            if (s.OccupantId < 0) continue;
            var occ = FindTribe(ctx, s.OccupantId);
            if (occ == null || occ.Dead || occ.Cell != s.Cell || occ.PlaceId != s.Id)
            {
                if (occ != null && !occ.Dead && occ.Cell != s.Cell)
                {
                    // 部落迁走：清其聚落关联（SettledSince 随迁徙重置——新址重新定居）
                    occ.PlaceId = -1;
                    occ.SettledSince = -1;
                }
                s.OccupantId = -1;
                if (s.RuinFrom < 0) s.RuinFrom = ctx.Tick;
            }
        }
        // ② 形成/接管：农业部落无聚落 → 建新村/接管废墟
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead || !e.IsFarming || e.PlaceId >= 0) continue;
            if (e.SettledSince < 0) e.SettledSince = ctx.Tick;   // 定居起点（转农/迁入当 tick）
            Settlement reclaim = null;
            for (int k = 0; k < ctx.Settlements.Count; k++)
                if (ctx.Settlements[k].Cell == e.Cell && ctx.Settlements[k].IsRuin) { reclaim = ctx.Settlements[k]; break; }
            if (reclaim != null)
            {
                // 接管废墟：继承 Level（场所比人长寿）；粮仓清空（新占据者从零开始）
                reclaim.OccupantId = e.Id;
                reclaim.DwellFrom = ctx.Tick;
                reclaim.RuinFrom = -1;
                System.Array.Clear(reclaim.Stocks, 0, reclaim.Stocks.Length);
                e.PlaceId = reclaim.Id;
            }
            else
            {
                var s = new Settlement
                {
                    Id = ctx.NextSettlementId++,
                    Cell = e.Cell,
                    BornTick = ctx.Tick,
                    Level = 0,
                    LastLevelUpTick = ctx.Tick,
                    DwellFrom = ctx.Tick,
                    OccupantId = e.Id,
                };
                ctx.Settlements.Add(s);
                e.PlaceId = s.Id;
            }
        }
        // ③ 等级演化（Dwell×P 阈值 + 冷却；都城 = 至尊酋长聚落，阈值减半）
        for (int i = 0; i < ctx.Settlements.Count; i++)
        {
            var s = ctx.Settlements[i];
            if (s.OccupantId < 0) continue;
            var occ = FindTribe(ctx, s.OccupantId);
            if (occ == null || occ.Dead) continue;
            if (ctx.Tick - s.LastLevelUpTick < CivSimContext.SettlementLevelCooldown) continue;
            int dwell = ctx.Tick - s.DwellFrom;
            bool capital = occ.IsChief && occ.ChiefdomId == occ.Id;   // 至尊酋长（自己酋邦中心）聚落 = 都城
            int target = s.Level;
            if (dwell >= CivSimContext.SettlementLevelTicks1 && occ.P >= (capital ? CivSimContext.SettlementPop1 / 2f : CivSimContext.SettlementPop1)) target = Math.Max(target, 1);
            if (dwell >= CivSimContext.SettlementLevelTicks2 && occ.P >= (capital ? CivSimContext.SettlementPop2 / 2f : CivSimContext.SettlementPop2)) target = Math.Max(target, 2);
            if (dwell >= CivSimContext.SettlementLevelTicks3 && occ.P >= (capital ? CivSimContext.SettlementPop3 / 2f : CivSimContext.SettlementPop3)) target = Math.Max(target, 3);
            if (target > s.Level) { s.Level = target; s.LastLevelUpTick = ctx.Tick; }
        }
    }

    private static Tribe FindTribe(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Tribes.Count; i++)
            if (ctx.Tribes[i].Id == id && !ctx.Tribes[i].Dead) return ctx.Tribes[i];
        return null;
    }
}

// ══════════════════════════════════════════════════════════════════
// ①k 国家涌现（Order 49，2026-08-16 阶段4；docs/阶段4设计-国家涌现.md；用户拍板 1A2A3A4A）：
//    酋邦 → 国家 = **制度化**（无规模阈值——性质跃迁非体积达标）。
//    涌现条件（AND，全部用已入档持久字段 → 纯派生不存档，读档续跑无分叉）：
//      ① 都城：至尊酋长（ChiefdomId==Id 且 IsChief）占据聚落，Level ≥ StateCapitalLevel(2=城镇+)
//      ② 决策层级：酋邦内 ≥2 个成员聚落，且存在 Level ≥ StateSubCenterLevel(1=村庄+) 的非都城聚落
//      ③ 贡赋盈余：贡赋池（Σ 成员 Contributed）≥ 酋邦总人口 × StateTributePerCap
//      ④ 存续：Tick − 都城.BornTick ≥ StateDwellTicks（都城实体存续——场所比人长寿）
//    判定为同一条件（无滞回字段——聚落等级单调 + 存续单调 = 天然弱滞回；4A 对称可逆）。
//    机制差异（接线点在 Prestige/Conflict——见设计文档 §2.4）：
//      税制化（贡赋率×2）、官僚供养↑（精英比例 0.25）、内部秩序（冲突 ×0.25）、
//      继承制度化（国家成员间继承窗口 ×2 豁免——ConflictModel 实现，晚于 StateModel 无分叉）。
//    执行位置：Order 49（Chiefdom 46 之后——读最新 ChiefdomId/成员表；Conflict 75 之前——冲突豁免生效）。
//    ⚠️ 滞后 1 tick：PrestigeModel(25) 读 StateId 是上一 tick 值——SettleDerived 重建值 ≡ 演化末写入值
//      （同输入同公式）→ 读档续跑无分叉（T04 验证）。
// ══════════════════════════════════════════════════════════════════
public sealed class StateModel : CivModelBase
{
    public override string Name => "国家涌现";
    public override int Order => 49;

    public override void Execute(CivSimContext ctx) => Rebuild(ctx);

    /// <summary>确定性重建国家（纯派生；读档入口 SettleDerived 同用——同一公式无分叉）。
    /// ① 清空全部 StateId/StateSize；② 按酋邦（ChiefdomCells 成员表）判定涌现条件；
    /// ③ 满足 → 全部成员 StateId = 酋长 Id、StateSize = 成员数。
    /// ⚠️ 2026-08-16 性能（T18 暴露 309s 劣化）：每 tick 执行 → 必须 O(1) 索引——
    ///   FindTribe 线性扫描 × 成员数 × 酋邦数 + SettlementOf 线性扫描 = 每 tick 上千万比较。
    ///   修复：Id→Tribe 数组（同 ChiefdomModel 缓冲）+ PlaceId→Settlement 字典。</summary>
    public static void Rebuild(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            ctx.Tribes[i].StateId = -1;
            ctx.Tribes[i].StateSize = 1;
        }
        if (ctx.ChiefdomCells == null) return;
        // Id 索引（O(1) 取实体——StateModel 每 tick 跑，线性扫描是性能杀手）
        int bufLen = Math.Max(ctx.NextTribeId, ctx.Tribes.Count + 1);
        var byId = new Tribe[bufLen];
        for (int i = 0; i < ctx.Tribes.Count; i++)
            if (!ctx.Tribes[i].Dead && ctx.Tribes[i].Id < bufLen) byId[ctx.Tribes[i].Id] = ctx.Tribes[i];
        // 聚落索引：Settlement.Id → Settlement（O(1) 查询——SettlementOf 线性扫描 O(S) 同样致命）
        var settleById = new Dictionary<int, Settlement>();
        if (ctx.Settlements != null)
            foreach (var s in ctx.Settlements)
                settleById[s.Id] = s;
        // 按酋邦遍历（ChiefdomId = 酋长 Id——ChiefdomModel.Rebuild ⑥ 已填成员表）
        for (int chiefId = 0; chiefId < ctx.ChiefdomCells.Length; chiefId++)
        {
            var members = ctx.ChiefdomCells[chiefId];
            if (members == null || members.Count < CivSimContext.ChiefdomMinTribes) continue;
            Tribe chief = chiefId < bufLen ? byId[chiefId] : null;
            if (chief == null || chief.Dead || !chief.IsChief) continue;   // 无酋长 → 非国家（权力真空）
            if (!IsState(ctx, chief, members, byId, settleById)) continue;

            int size = members.Count;
            for (int k = 0; k < members.Count; k++)
            {
                int mid = members[k];
                if (mid >= bufLen) continue;
                Tribe m = byId[mid];
                if (m == null || m.Dead) continue;
                m.StateId = chiefId;
                m.StateSize = size;
            }
        }
    }

    /// <summary>国家涌现判定（AND 四条件；纯函数——全部输入已入档/派生）。</summary>
    private static bool IsState(CivSimContext ctx, Tribe chief, List<int> members, Tribe[] byId, Dictionary<int, Settlement> settleById)
    {
        // ① 都城：酋长占据聚落（PlaceId → Settlement）
        Settlement capital = chief.PlaceId >= 0 && settleById.TryGetValue(chief.PlaceId, out var c) ? c : null;
        if (capital == null || capital.OccupantId != chief.Id) return false;
        if (capital.Level < CivSimContext.StateCapitalLevel) return false;
        // ④ 存续：都城实体存续时长（BornTick 单调——天然弱滞回）
        if (ctx.Tick - capital.BornTick < CivSimContext.StateDwellTicks) return false;
        // ② 决策层级：≥2 成员聚落 + 存在次级中心（非都城且 Level ≥ 阈值）
        int memberSettlements = 0;
        bool hasSubCenter = false;
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            Tribe m = byId[mid];
            if (m == null || m.Dead) continue;
            Settlement s = m.PlaceId >= 0 && settleById.TryGetValue(m.PlaceId, out var st) ? st : null;
            if (s == null || s.OccupantId != m.Id) continue;
            memberSettlements++;
            if (s.Id != capital.Id && s.Level >= CivSimContext.StateSubCenterLevel) hasSubCenter = true;
        }
        if (memberSettlements < 2 || !hasSubCenter) return false;
        // ③ 贡赋盈余：贡赋池 ≥ 酋邦总人口 × 线（剩余集中——Childe）
        float pop = 0f, pool = 0f;
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            Tribe m = byId[mid];
            if (m == null || m.Dead) continue;
            pop += m.P;
            pool += m.Contributed;
        }
        if (pop <= 0f) return false;
        return pool >= pop * CivSimContext.StateTributePerCap;
    }

    private static Tribe FindTribe(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Tribes.Count; i++)
            if (ctx.Tribes[i].Id == id && !ctx.Tribes[i].Dead) return ctx.Tribes[i];
        return null;
    }
}
