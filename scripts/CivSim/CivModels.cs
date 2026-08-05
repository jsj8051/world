using System;
using System.Collections.Generic;
using Godot;

namespace World.CivSim;

/// <summary>
/// 文明演化模型统一抽象基类（唯一基类 + 注册表）。每个机制 = 一个模型，按 Order 每 tick 执行。
/// v2 部落模型：部落=格内社会单元，分裂/迁徙/接触（传播/贸易/吞并/和平合并），部落级技术。
/// </summary>
public abstract class CivModelBase
{
    public abstract string Name { get; }
    public abstract int Order { get; }
    public abstract void Execute(CivSimContext ctx);
}

/// <summary>机制注册表（v2 石器时代：起源/增长/分裂/迁徙/接触/技术）。</summary>
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
            .Register(new GrowthModel())
            .Register(new FissionModel())
            .Register(new MigrationModel())
            .Register(new ContactModel())
            .Register(new TechModel());
    }
}

// ══════════════════════════════════════════════════════════════════
// ① 部落起源（播种）：seed 确定性选陆地格，每格 1 个部落（100 人，自带石核 T01）。
//    旧石器人类适应力强——不挑环境（极地/冰原 K≈0 除外）。
// ══════════════════════════════════════════════════════════════════
public sealed class OriginModel : CivModelBase
{
    public override string Name => "部落起源";
    public override int Order => 0;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.Tick > 0) return;
        var land = new List<int>();
        for (int i = 0; i < ctx.Grid.N; i++)
            if (ctx.Grid.IsLandCell(i) && ctx.BaseK[i] > 0f)
                land.Add(i);
        if (land.Count == 0) return;
        int count = Mathf.Min(ctx.OriginCount, land.Count);
        var used = new HashSet<int>();
        for (int k = 0; k < count; k++)
        {
            int pick;
            int guard = 0;
            do { pick = land[ctx.Rng.Next(land.Count)]; guard++; }
            while (used.Contains(pick) && guard < 64);
            used.Add(pick);
            var tribe = new Tribe
            {
                Id = ctx.Tribes.Count,
                Cell = pick,
                Population = 100f,
                Culture = (byte)k,          // 每摇篮独立文化标签
                TechFlags = TechTable.Set(0UL, 0),   // 自带石核 T01
                OriginCell = pick,
                BornTick = 0,
            };
            ctx.Tribes.Add(tribe);
            ctx.CellTribes[pick].Add(tribe);
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ② 人口增长（格级 logistic，按部落人口比例分配）：
//    格总人口 P 按 K 增长，dP 按各部落人口占比分配。超载（>1.3K）枯竭负增长。
// ══════════════════════════════════════════════════════════════════
public sealed class GrowthModel : CivModelBase
{
    public override string Name => "人口增长";
    public override int Order => 10;

    public override void Execute(CivSimContext ctx)
    {
        float f = ctx.TickFactor;
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            if (list.Count == 0) continue;
            float P = ctx.CellPop[i];
            float K = ctx.CellK[i];
            if (P <= 0f || K <= 0f) continue;
            float g = f * P * (1f - P / K);
            if (P > 1.3f * K) g -= f * P * 0.5f;   // 超载枯竭
            if (Mathf.Abs(g) < 1e-6f) continue;
            // 按人口比例分配增长
            for (int k = 0; k < list.Count; k++)
            {
                var t = list[k];
                if (t.Population > 0f)
                    t.Population = Mathf.Max(0f, t.Population + g * (t.Population / P));
            }
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ③ 部落分裂（segmentary lineage）：部落人口 > 150 → 同格裂变，
//    新部落带走 45%（继承技术/文化），此后各自独立发展（各有"酋长"）。
// ══════════════════════════════════════════════════════════════════
public sealed class FissionModel : CivModelBase
{
    public override string Name => "部落分裂";
    public override int Order => 20;

    public override void Execute(CivSimContext ctx)
    {
        // 快照遍历（新部落下 tick 再判分裂，避免同 tick 连锁）
        var snapshot = ctx.Tribes.ToArray();
        foreach (var t in snapshot)
        {
            if (t.Dead) continue;
            if (t.Population <= CivSimContext.SplitPop) continue;
            if (ctx.CellTribes[t.Cell].Count >= CivSimContext.MaxTribesPerCell) continue;   // 格内社会密度上限
            float newPop = t.Population * CivSimContext.SplitShare;
            t.Population -= newPop;
            var nt = new Tribe
            {
                Id = ctx.Tribes.Count,
                Cell = t.Cell,
                Population = newPop,
                Culture = t.Culture,
                TechFlags = t.TechFlags,     // 分裂瞬间技术相同，此后分道扬镳
                OriginCell = t.Cell,
                BornTick = ctx.Tick,
            };
            ctx.Tribes.Add(nt);
            ctx.CellTribes[t.Cell].Add(nt);
            ctx.Fissions++;
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ④ 迁徙：饱和迁徙（格人口 >90%K → 最大部落分出 50% 迁往相邻宜居格）
//    + 探路迁徙（人口 >100 的部落 2%/tick 向无人邻格迁出 30%，持续扩散——旧石器特性）。
//    目标格：陆地邻格（无船不跨海），优先无人/低密度、高 K。
// ══════════════════════════════════════════════════════════════════
public sealed class MigrationModel : CivModelBase
{
    public override string Name => "迁徙扩散";
    public override int Order => 30;

    public override void Execute(CivSimContext ctx)
    {
        // ── 饱和迁徙 ──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            if (list.Count == 0) continue;
            float P = ctx.CellPop[i];
            float K = ctx.CellK[i];
            if (K <= 0f || P < CivSimContext.MigrateThreshold * K) continue;
            // 最大部落
            Tribe tmax = list[0];
            for (int k = 1; k < list.Count; k++)
                if (list[k].Population > tmax.Population) tmax = list[k];
            int target = PickTarget(ctx, i, tmax, requireUnoccupied: false);
            if (target < 0) continue;
            float move = tmax.Population * CivSimContext.MigrateShare;
            tmax.Population -= move;
            SpawnTribe(ctx, tmax, target, move);
            ctx.Migrations++;
        }

        // ── 探路迁徙（持续扩散前沿）──
        var snapshot = ctx.Tribes.ToArray();
        foreach (var t in snapshot)
        {
            if (t.Dead) continue;
            if (t.Population < CivSimContext.ScoutMinPop) continue;
            if (ctx.Rng.NextDouble() >= CivSimContext.ScoutChance) continue;
            int target = PickTarget(ctx, t.Cell, t, requireUnoccupied: true);
            if (target < 0) continue;
            float move = t.Population * CivSimContext.ScoutShare;
            t.Population -= move;
            SpawnTribe(ctx, t, target, move);
            ctx.Migrations++;
        }
    }

    /// <summary>选迁徙目标：陆地邻格（无船不跨海），优先无人格、高 K、低密度。</summary>
    private static int PickTarget(CivSimContext ctx, int from, Tribe mover, bool requireUnoccupied)
    {
        int best = -1; float bestScore = -1f;
        foreach (int nb in ctx.Grid.Neighbors[from])
        {
            if (!ctx.Grid.IsLandCell(nb) || ctx.CellK[nb] <= 0f) continue;
            if (ctx.CellPop[nb] <= 0f) { if (requireUnoccupied) return nb; }
            else if (requireUnoccupied) continue;
            // 打分：K 高、人口低（宜居 + 空间）
            float score = ctx.CellK[nb] / Mathf.Max(1f, ctx.CellPop[nb]);
            if (score > bestScore) { bestScore = score; best = nb; }
        }
        return best;
    }

    private static void SpawnTribe(CivSimContext ctx, Tribe from, int cell, float pop)
    {
        if (pop <= 0f) return;
        var nt = new Tribe
        {
            Id = ctx.Tribes.Count,
            Cell = cell,
            Population = pop,
            Culture = from.Culture,
            TechFlags = from.TechFlags,     // 迁徙带走技术
            OriginCell = cell,
            BornTick = ctx.Tick,
        };
        ctx.Tribes.Add(nt);
        ctx.CellTribes[cell].Add(nt);
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑤ 部落接触（边界格对 + 同格）：四种后果
//    · 技术传播（接触即传播，贸易 ×2 加速）
//    · 贸易（物物交换，接触即贸易：统计 + 传播加速）
//    · 冲突吞并（同格人口 >3:1 → 强吞弱，技术并集）
//    · 和平合并（同格人口 0.5~2 且文化同 → 融合，技术并集）
//    · 文化同化（格内人口优势方同化弱方文化）
// ══════════════════════════════════════════════════════════════════
public sealed class ContactModel : CivModelBase
{
    public override string Name => "部落接触";
    public override int Order => 40;

    public override void Execute(CivSimContext ctx)
    {
        // ── 同格：主导部落 vs 其余（吞并/和平合并/文化/传播）──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var list = ctx.CellTribes[i];
            if (list.Count < 2) continue;
            list.Sort((a, b) => b.Population.CompareTo(a.Population));   // 主导在前
            var dom = list[0];
            for (int k = 1; k < list.Count; k++)
            {
                var t = list[k];
                if (t.Population <= 0f) { t.Dead = true; list.RemoveAt(k); k--; continue; }
                float ratio = dom.Population / t.Population;
                if (ratio > CivSimContext.AbsorbRatio)
                {
                    // 冲突吞并：强吞弱（消灭+吸收；死亡标记，Tribes 定期 compact 避免 O(n) Remove）
                    dom.Population += t.Population;
                    dom.TechFlags |= t.TechFlags;
                    t.Dead = true;
                    list.RemoveAt(k); ctx.Absorptions++; k--;
                }
                else if (ratio < CivSimContext.MergeRatioMax && dom.Culture == t.Culture
                         && ctx.Rng.NextDouble() < CivSimContext.MergeChance)
                {
                    // 和平合并：对等 + 同文化 → 融合
                    dom.Population += t.Population;
                    dom.TechFlags |= t.TechFlags;
                    t.Dead = true;
                    list.RemoveAt(k); ctx.Merges++; k--;
                }
                else
                {
                    // 维持接触：文化同化 + 技术互学 + 贸易
                    if (ctx.Rng.NextDouble() < CivSimContext.AssimilateChance)
                        t.Culture = dom.Culture;
                    SpreadTech(ctx, dom, t);
                    SpreadTech(ctx, t, dom);
                    ctx.TradeContacts++;
                }
            }
        }

        // ── 相邻格：接触传播 + 贸易（跨格不吞并——石器时代无领土概念）。
        //   每对格用代表部落（人口最大）互传——性能 O(边界格对)，格内技术差异由同格接触拉平 ──
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            var a = ctx.CellTribes[i];
            if (a.Count == 0) continue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (nb <= i) continue;   // 去重
                var b = ctx.CellTribes[nb];
                if (b.Count == 0) continue;
                var repA = a[0];
                for (int x = 1; x < a.Count; x++) if (a[x].Population > repA.Population) repA = a[x];
                var repB = b[0];
                for (int y = 1; y < b.Count; y++) if (b[y].Population > repB.Population) repB = b[y];
                SpreadTech(ctx, repA, repB);
                SpreadTech(ctx, repB, repA);
                ctx.TradeContacts++;
            }
        }
    }

    /// <summary>技术传播（from → to）：to 缺 from 的技术且前置满足 → 按传播概率获得。
    /// 接触即贸易 → 传播概率 × 贸易加速 ×2。旧石器技术易传，高级技术难传。</summary>
    private static void SpreadTech(CivSimContext ctx, Tribe from, Tribe to)
    {
        ulong missing = from.TechFlags & ~to.TechFlags;
        if (missing == 0) return;   // 对方全会 → 快速跳过
        var techs = TechTable.All;
        for (int i = 1; i < techs.Length; i++)   // 跳过石核(0)
        {
            if ((missing & (1UL << i)) == 0) continue;
            var def = techs[i];
            if (!TechTable.HasAll(to.TechFlags, def.Requires)) continue;
            float p = def.SpreadBase * CivSimContext.TradeSpreadBonus;
            if (ctx.Rng.NextDouble() < Mathf.Min(0.5f, p))
                to.TechFlags = TechTable.Set(to.TechFlags, i);
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑥ 技术发明（部落级）：前置满足 + 人口门槛（旧石器 30-120 低门槛；农业 -1=格 K 利用率
//    > 全局 P80 人口压力）+ 环境 + 随机概率 → 部落获得。
//    农业发明少数起源中心（高门槛+低概率），其余靠接触传播——部落节奏差异的来源。
// ══════════════════════════════════════════════════════════════════
public sealed class TechModel : CivModelBase
{
    public override string Name => "技术发明";
    public override int Order => 50;

    private float _agriPressureP80 = 0.6f;   // 农业人口压力阈值（缓存，每 tick 重算）

    public override void Execute(CivSimContext ctx)
    {
        // 农业特殊条件：格 K 利用率（人口压力）全局 P80——人口密度接近承载 = 觅食压力 → 种植
        ComputeAgriculturePressure(ctx);

        var snapshot = ctx.Tribes.ToArray();
        foreach (var t in snapshot)
        {
            if (t.Dead) continue;
            var techs = TechTable.All;
            // 随机起点轮转：每部落每 tick 只检查 TechInventRolls 个未获技术（性能上限；
            // 300 tick × 4 次 = 1200 次尝试，25 项技术覆盖充分）
            int start = ctx.Rng.Next(techs.Length - 1) + 1;
            for (int r = 0; r < CivSimContext.TechInventRolls; r++)
            {
                int i = start + r;
                if (i >= techs.Length) i = i % (techs.Length - 1) + 1;   // 环绕（跳过 0）
                if (TechTable.Has(t.TechFlags, i)) continue;
                var def = techs[i];
                if (!TechTable.HasAll(t.TechFlags, def.Requires)) continue;
                // 人口门槛（-1 = 农业压力判定）
                if (def.InvPop >= 0f)
                {
                    if (t.Population < def.InvPop) continue;
                }
                else
                {
                    float util = ctx.CellK[t.Cell] > 0f ? ctx.CellPop[t.Cell] / ctx.CellK[t.Cell] : 0f;
                    if (util < _agriPressureP80) continue;
                }
                // 环境
                if (!ctx.EnvMatches(t.Cell, def.InvEnv)) continue;
                // 随机
                if (ctx.Rng.NextDouble() < def.InvProb)
                    t.TechFlags = TechTable.Set(t.TechFlags, i);
            }
        }
    }

    private void ComputeAgriculturePressure(CivSimContext ctx)
    {
        var utils = new List<float>(ctx.Grid.N);
        for (int i = 0; i < ctx.Grid.N; i++)
        {
            if (ctx.CellPop[i] <= 0f || ctx.CellK[i] <= 0f) continue;
            utils.Add(ctx.CellPop[i] / ctx.CellK[i]);
        }
        if (utils.Count < 10) { _agriPressureP80 = 0.6f; return; }
        utils.Sort();
        _agriPressureP80 = utils[Mathf.Clamp((int)(utils.Count * 0.8f), 0, utils.Count - 1)];
    }
}
