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
        _models.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach (var m in _models)
            m.Execute(ctx);
    }

    public static CivModelRegistry StoneAge()
    {
        return new CivModelRegistry()
            .Register(new OriginModel())
            .Register(new EnergyModel())
            .Register(new GrowthModel())
            .Register(new ModeModel())
            .Register(new InventionModel())
            .Register(new SpreadModel())
            .Register(new CultureModel())
            .Register(new ReligionModel())
            .Register(new SplitMigrateModel());
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

        // ── 富饶区：陆地 ∩ BaseK>0，按 BaseK 降序前 30% ──
        var land = new List<int>();
        for (int i = 0; i < n; i++)
            if (grid.IsLandCell(i) && ctx.BaseK[i] > 0f)
                land.Add(i);
        if (land.Count == 0) return;
        land.Sort((a, b) => ctx.BaseK[b].CompareTo(ctx.BaseK[a]));
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
                Id = ctx.Entities.Count,
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
            float y = ctx.Yield(e);
            e.EPerCap = y / Mathf.Max(0.001f, e.P);
            e.Surplus = e.EPerCap - 1f;
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ③ 人口增长（Order 20）：P *= exp(r_eff·(1 − P_格/K_格))，r_eff=0.5/tick ★
//    P 为部落人口；K 与压力因子用格级（格内实体共享 → 比例不变）。
//    P_格 > K → 负增长 = 饿死人（用户拍板 2026-08-06）。
// ══════════════════════════════════════════════════════════════════
public sealed class GrowthModel : CivModelBase
{
    public override string Name => "人口增长";
    public override int Order => 20;

    public override void Execute(CivSimContext ctx)
    {
        float r = ctx.TickFactor;   // 0.5/tick
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            if (list.Count == 0) continue;
            float P = ctx.CellPop[i];
            float K = ctx.CellK[i];
            if (P <= 0f || K <= 0f) continue;
            float factor = Mathf.Exp(r * (1f - P / K));
            for (int k = 0; k < list.Count; k++)
            {
                var e = list[k];
                if (e.Dead) continue;
                e.P *= factor;
                if (e.P < 1f) { e.P = 0f; e.Dead = true; }   // 饿死灭绝
            }
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
            bool hasSeed = TechTable.HeldSeeds(e.TechKeys).Count > 0;
            if (!hasSeed) { e.IsFarming = false; continue; }
            float yH = ctx.YHunter(e);
            float yF = ctx.YFarm(e);
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
        CivEngine.RefreshCellState(ctx);   // 生产方式已更新（Order 30）→ 刷新 CellK 供压力判定

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
            float pressure = ctx.CellK[e.Cell] > 0f ? ctx.CellPop[e.Cell] / ctx.CellK[e.Cell] : 0f;
            bool pressureOk = pressure > CivSimContext.SeedPressure;
            bool soilOk = ctx.Grid.SoilLevel[e.Cell] >= 3;
            bool grindOk = e.TechKeys.Contains(TechTable.Grinding);
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
                SpreadTech(ctx, repA, repB);
                SpreadTech(ctx, repB, repA);
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

    /// <summary>技术传播 from → to（to 缺 from 的技术且依赖满足 → 按概率获得）。</summary>
    private void SpreadTech(CivSimContext ctx, CivEntity from, CivEntity to)
    {
        foreach (var t in TechTable.All)
        {
            if (t.IsAgricultureConcept) continue;
            if (to.TechKeys.Contains(t.Key)) continue;
            if (!from.TechKeys.Contains(t.Key)) continue;
            if (!HasAll(to.TechKeys, t.Requires)) continue;   // 依赖硬门槛
            float p = t.SpreadBase;
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
            if (e.Surplus > 0f && e.TechKeys.Contains(TechTable.Microlith))
            {
                int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionUpgradeRate);
                ShareField.RelTransfer(e.ReligionShare, ReligionStage.Animism, ReligionStage.Shaman, amt);
            }
            // 萨满 → 祖先：农业（种子）+ 定居（后续阶段能力）→ 旧石器无定居科技，天然锁死
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
                SpreadReligion(ctx, repA, repB);
                SpreadReligion(ctx, repB, repA);
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
    private static void SpreadReligion(CivSimContext ctx, CivEntity from, CivEntity to)
    {
        string domFrom = ShareField.DomReligion(from.ReligionShare);
        string domTo = ShareField.DomReligion(to.ReligionShare);
        int fi = ShareField.ReligionIndex(domFrom);
        int ti = ShareField.ReligionIndex(domTo);
        if (fi <= ti) return;   // 只向更高阶段（不回头污染）
        int amt = (int)MathF.Round(ShareField.Unit * CivSimContext.ReligionSpreadRate);
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
    public override string Name => "分裂迁徙";
    public override int Order => 80;

    public override void Execute(CivSimContext ctx)
    {
        // ── 分裂（快照遍历，新实体下 tick 再判，防同 tick 连锁）──
        var snapshot = ctx.Entities.ToArray();
        foreach (var t in snapshot)
        {
            if (t.Dead) continue;
            if (t.P <= CivSimContext.SplitPop) continue;
            if (ctx.CellTribes[t.Cell].Count >= CivSimContext.MaxTribesPerCell) continue;   // 社会密度上限
            float newPop = t.P * CivSimContext.SplitShare;
            t.P -= newPop;
            var nt = new CivEntity
            {
                Id = ctx.Entities.Count,
                Cell = t.Cell,
                P = newPop,
                IsFarming = t.IsFarming,
                TechKeys = new HashSet<string>(t.TechKeys),     // 分裂瞬间技术相同，此后各自发明/学习
                CultureShare = ShareField.CloneShare(t.CultureShare),          // 份额等比例继承
                CultureGroupShare = ShareField.CloneShare(t.CultureGroupShare),
                ReligionShare = (ShareEntry[])t.ReligionShare.Clone(),
                ReligionCultShare = ShareField.CloneShare(t.ReligionCultShare),   // 派别随人口走
                OriginCell = t.Cell,
                BornTick = ctx.Tick,
            };
            // 文化群分化：0.5% 新 key（方言→语言群漂变），作用于新实体主导份额（份额值不变）
            if (ctx.CultureKeyCount < 250 && ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance)
            {
                nt.CultureGroupShare[0] = new ShareEntry { Key = ctx.NextCultureKey(), Frac = nt.CultureGroupShare[0].Frac };
            }
            // 宗教派别分化：0.5% 新 key（图腾漂变），与文化群分化独立判定
            if (ctx.ReligionKeyCount < 250 && ctx.Rng.NextDouble() < CivSimContext.CultureDriftChance)
            {
                nt.ReligionCultShare[0] = new ShareEntry { Key = ctx.NextReligionKey(), Frac = nt.ReligionCultShare[0].Frac };
            }
            ctx.Entities.Add(nt);
            ctx.CellTribes[t.Cell].Add(nt);
            ctx.Fissions++;
        }

        // ── 饱和迁徙：格 P > 0.75K → 最大实体分出 50% 迁相邻宜居格 ──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            if (list.Count == 0) continue;
            float P = ctx.CellPop[i];
            float K = ctx.CellK[i];
            if (K <= 0f || P < CivSimContext.MigrateThreshold * K) continue;
            var tmax = MaxPop(list);
            if (tmax == null) continue;
            int target = PickTarget(ctx, i, tmax);
            if (target < 0) continue;                          // 无处可迁 → 留原地（压力终达）
            float move = tmax.P * CivSimContext.MigrateShare;
            tmax.P -= move;
            ctx.CellPop[i] -= move;
            SpawnEntity(ctx, tmax, target, move);
            ctx.Migrations++;
        }

        // ── 探路迁徙：P>100 实体 2%/tick → 30% 迁 1-3 跳最高 K 无人格（跳跃扩散）──
        var snap2 = ctx.Entities.ToArray();
        foreach (var t in snap2)
        {
            if (t.Dead) continue;
            if (t.P < CivSimContext.ScoutMinPop) continue;
            if (ctx.Rng.NextDouble() >= CivSimContext.ScoutChance) continue;
            int target = PickScoutTarget(ctx, t.Cell);
            if (target < 0) continue;
            float move = t.P * CivSimContext.ScoutShare;
            t.P -= move;
            SpawnEntity(ctx, t, target, move);
            ctx.Migrations++;
        }
    }

    private static CivEntity MaxPop(List<CivEntity> list)
    {
        CivEntity best = null;
        foreach (var e in list)
            if (!e.Dead && (best == null || e.P > best.P)) best = e;
        return best;
    }

    /// <summary>饱和迁徙目标：陆地邻格无人最高优先；否则高 K/低密度；canoe 允许跨 1 格海。</summary>
    private static int PickTarget(CivSimContext ctx, int from, CivEntity mover)
    {
        int best = -1; float bestScore = -1f;
        bool canoe = mover.TechKeys.Contains(TechTable.Canoe);
        foreach (int nb in ctx.Grid.Neighbors[from])
        {
            if (ctx.Grid.IsLandCell(nb) && ctx.CellK[nb] > 0f)
            {
                if (ctx.CellPop[nb] <= 0f) return nb;   // 无人格最高优先
                float score = ctx.CellK[nb] / Mathf.Max(1f, ctx.CellPop[nb]);
                if (score > bestScore) { bestScore = score; best = nb; }
            }
            // 跨海：海邻格 → 其陆地邻格（1 格海连通）
            else if (canoe && !ctx.Grid.IsLandCell(nb))
            {
                foreach (int nb2 in ctx.Grid.Neighbors[nb])
                {
                    if (nb2 == from || !ctx.Grid.IsLandCell(nb2) || ctx.CellK[nb2] <= 0f) continue;
                    if (ctx.CellPop[nb2] <= 0f) return nb2;
                    float score = ctx.CellK[nb2] / Mathf.Max(1f, ctx.CellPop[nb2]);
                    if (score > bestScore) { bestScore = score; best = nb2; }
                }
            }
        }
        return best;
    }

    /// <summary>探路目标：1-3 跳 BFS 内最高 K 无人陆地格（时间戳标记，确定性）。</summary>
    private static int PickScoutTarget(CivSimContext ctx, int from)
    {
        var grid = ctx.Grid;
        int stamp = ++ctx.BfsStampValue;
        int best = -1; float bestK = -1f;
        foreach (int nb in grid.Neighbors[from])
        {
            if (!grid.IsLandCell(nb) || ctx.CellK[nb] <= 0f) continue;
            ctx.BfsStamp[nb] = stamp;
            if (ctx.CellPop[nb] <= 0f && ctx.CellK[nb] > bestK) { bestK = ctx.CellK[nb]; best = nb; }
        }
        foreach (int nb in grid.Neighbors[from])
        {
            if (ctx.BfsStamp[nb] != stamp) continue;
            foreach (int nb2 in grid.Neighbors[nb])
            {
                if (ctx.BfsStamp[nb2] == stamp) continue;
                ctx.BfsStamp[nb2] = stamp;
                if (grid.IsLandCell(nb2) && ctx.CellK[nb2] > 0f && ctx.CellPop[nb2] <= 0f && ctx.CellK[nb2] > bestK)
                { bestK = ctx.CellK[nb2]; best = nb2; }
            }
        }
        foreach (int nb in grid.Neighbors[from])
        {
            if (ctx.BfsStamp[nb] != stamp) continue;
            foreach (int nb2 in grid.Neighbors[nb])
            {
                if (ctx.BfsStamp[nb2] != stamp) continue;
                foreach (int nb3 in grid.Neighbors[nb2])
                {
                    if (ctx.BfsStamp[nb3] == stamp) continue;
                    ctx.BfsStamp[nb3] = stamp;
                    if (grid.IsLandCell(nb3) && ctx.CellK[nb3] > 0f && ctx.CellPop[nb3] <= 0f && ctx.CellK[nb3] > bestK)
                    { bestK = ctx.CellK[nb3]; best = nb3; }
                }
            }
        }
        return best;
    }

    private static void SpawnEntity(CivSimContext ctx, CivEntity from, int cell, float pop)
    {
        if (pop <= 0f) return;
        if (ctx.CellTribes[cell].Count >= CivSimContext.MaxTribesPerCell) return;   // 格内社会密度上限（防实体堆积）
        var nt = new CivEntity
        {
            Id = ctx.Entities.Count,
            Cell = cell,
            P = pop,
            IsFarming = from.IsFarming,
            TechKeys = new HashSet<string>(from.TechKeys),   // 迁徙带走技术
            CultureShare = ShareField.CloneShare(from.CultureShare),          // 份额随人口走
            CultureGroupShare = ShareField.CloneShare(from.CultureGroupShare),
            ReligionShare = (ShareEntry[])from.ReligionShare.Clone(),
            ReligionCultShare = ShareField.CloneShare(from.ReligionCultShare),
            OriginCell = cell,
            BornTick = ctx.Tick,
        };
        ctx.Entities.Add(nt);
        ctx.CellTribes[cell].Add(nt);
        ctx.CellPop[cell] += pop;   // 守恒：立即更新目标格
    }
}
