using System;
using System.Collections.Generic;
using Godot;

namespace World.CivSim;

/// <summary>
/// 文明演化模型统一抽象基类（延续项目抽象建模定案：唯一基类 + 注册表，不各自建基类）。
/// 每个机制 = 一个模型，按 Order 顺序在每 tick 执行；Verify 可查依赖完整性。
/// </summary>
public abstract class CivModelBase
{
    public abstract string Name { get; }    // 机制名（诊断/日志）
    public abstract int Order { get; }      // 执行顺序（依赖序：增长→技术→迁徙→文化→竞争）
    public abstract void Execute(CivSimContext ctx);
    public virtual bool Verify(CivSimContext ctx) => true;
}

/// <summary>机制注册表（纪元 → 模型集合；v1 石器时代 6 个 tick 模型 + 起源）。</summary>
public sealed class CivModelRegistry
{
    private readonly List<CivModelBase> _models = new();

    public CivModelRegistry Register(CivModelBase m) { _models.Add(m); return this; }

    /// <summary>按 Order 顺序执行全部模型（每 tick）。</summary>
    public void ExecuteAll(CivSimContext ctx)
    {
        _models.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach (var m in _models)
            m.Execute(ctx);
    }

    /// <summary>石器时代注册表（v1 全启用）。</summary>
    public static CivModelRegistry StoneAge()
    {
        return new CivModelRegistry()
            .Register(new OriginModel())
            .Register(new GrowthModel())
            .Register(new TechModel())
            .Register(new MigrationModel())
            .Register(new CultureModel())
            .Register(new CompetitionModel());
    }
}

// ══════════════════════════════════════════════════════════════════
// ① 部落起源（播种）：seed 确定性选陆地格生成初始部落（模拟智人"摇篮"）。
//    旧石器人类适应力强——不挑条件，任何陆地都能起步（极地/冰原 K≈0 除外）。
// ══════════════════════════════════════════════════════════════════
public sealed class OriginModel : CivModelBase
{
    public override string Name => "部落起源";
    public override int Order => 0;

    public override void Execute(CivSimContext ctx)
    {
        if (ctx.Tick > 0) return;   // 只在 tick 0 播种
        var land = new List<int>();
        for (int i = 0; i < ctx.Grid.N; i++)
            if (ctx.Grid.IsLandCell(i) && ctx.BaseK[i] > 0f)
                land.Add(i);
        if (land.Count == 0) return;
        int count = Mathf.Min(ctx.OriginCount, land.Count);
        for (int k = 0; k < count; k++)
        {
            int pick = land[ctx.Rng.Next(land.Count)];
            var tribe = new Tribe
            {
                Id = ctx.Tribes.Count,
                OriginCell = pick,
                Culture = (byte)ctx.Tribes.Count,   // 每摇篮独立文化标签
                Tech = 0,
            };
            ctx.Tribes.Add(tribe);
            ctx.Cells[pick].Population = 100f;      // 起始人口 100
            ctx.Cells[pick].Culture = tribe.Culture;
            ctx.Cells[pick].Tech = 0;
            ctx.Cells[pick].TribeId = tribe.Id;
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ② 人口增长（logistic，承载力 K=环境×技术×水源）：
//    dP = r·Δt·P·(1−P/K)；超载（>1.3K）触发资源枯竭负增长。
//    自然增长率 r=0.5%/年（前工业 ~0.05%/年，游戏压缩 10×，否则旧石器 10 万年太久）。
// ══════════════════════════════════════════════════════════════════
public sealed class GrowthModel : CivModelBase
{
    public override string Name => "人口增长";
    public override int Order => 10;

    public override void Execute(CivSimContext ctx)
    {
        float f = ctx.TickFactor;
        float total = 0f;
        var cells = ctx.Cells;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].TribeId < 0) continue;
            float K = ctx.CellK[i];
            float p = cells[i].Population;
            if (K <= 0f) { cells[i].Population = 0f; continue; }
            float g = f * p * (1f - p / K);
            if (p > CivSimContext.OvercrowdLimit * K)
                g -= f * p * 0.5f;                       // 超载：资源枯竭衰减
            cells[i].Population = Mathf.Max(0f, p + g);
            total += cells[i].Population;
        }
        ctx.TotalPopulation = total;
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑤ 技术演进（史前序列：石核→手斧(火)→细石器→弓箭）。
//    按史实是泛大陆传播（阿舍利手斧横跨非欧亚）→ 全局解锁：
//    tick≥60 且全球人口>5万 → 手斧+火；tick≥100 → 细石器；tick≥150 → 弓箭。
//    人口不足则技术停滞（真实：塔斯马尼亚孤岛技术退化）。
//    技术提升承载力 ×1.4/级；火（技术≥1）解锁极寒区（苔原/冰原 K×3）。
// ══════════════════════════════════════════════════════════════════
public sealed class TechModel : CivModelBase
{
    public override string Name => "技术演进";
    public override int Order => 20;

    public override void Execute(CivSimContext ctx)
    {
        byte newTech = 0;
        if (ctx.Tick >= 60 && ctx.TotalPopulation > 50_000f) newTech = 1;   // 手斧/火（旧石器中期）
        if (ctx.Tick >= 100 && ctx.TotalPopulation > 200_000f) newTech = 2; // 细石器（旧石器晚期）
        if (ctx.Tick >= 150 && ctx.TotalPopulation > 500_000f) newTech = 3; // 弓箭（旧石器末期）
        if (newTech == 0) return;

        var cells = ctx.Cells;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].TribeId < 0) continue;
            if (cells[i].Tech < newTech) cells[i].Tech = newTech;
            // 承载力更新：基础 × 1.4^tech；火解锁极寒（苔原/冰原 ×3）
            float k = ctx.BaseK[i] * Mathf.Pow(CivSimContext.TechKFactor, cells[i].Tech);
            if (cells[i].Tech >= 1 && ctx.BaseK[i] <= 0.05f * ctx.Grid.CellAreaKm2)
                k = Mathf.Max(k, 0.05f * ctx.Grid.CellAreaKm2 * 3f);
            ctx.CellK[i] = k;
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ③ 迁徙扩散（人口压力驱动）：格人口 ≥90%K → 溢出 15% 人口到相邻宜居格。
//    选格：最宜居（K 最高）候选 + 小随机；无人格=开辟新地（谱系继承来源部落），
//    有人格=并入（人口相加）。无候选（全饱和）→ 强制压入最弱相邻格（冲突损耗 30%）。
//    速度自洽：n=64 每格 ~110km，1 格/100 年 ≈ 1.1km/年 = 真实智人扩散速率。
// ══════════════════════════════════════════════════════════════════
public sealed class MigrationModel : CivModelBase
{
    public override string Name => "迁徙扩散";
    public override int Order => 30;

    public override void Execute(CivSimContext ctx)
    {
        var cells = ctx.Cells;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].TribeId < 0) continue;
            float K = ctx.CellK[i];
            float p = cells[i].Population;
            if (K <= 0f) continue;

            // ── 探路扩散（持续探索，不依赖饱和）：有无人宜居邻格 → 恒播 5% 开拓新地。
            //    真实旧石器扩散前沿 ~1km/年 持续推进，不依赖人口密度（即使密度低也迁徙）。──
            if (p > 300f)
            {
                foreach (int nb in ctx.Grid.Neighbors[i])
                {
                    if (cells[nb].TribeId >= 0 || !ctx.Grid.IsLandCell(nb) || ctx.CellK[nb] <= 0f) continue;
                    MoveTo(ctx, i, nb, p * 0.05f);   // 新格种子 ~15+ 人，2-3 tick 内再探
                    break;                           // 每 tick 每格最多探 1 个新格（前沿稳定推进）
                }
            }

            // ── 饱和扩散（人口压力）：≥75%K → 主候选 25% + 其余候选各 8% ──
            if (p < CivSimContext.MigrateThreshold * K) continue;

            // 候选 = 无人或低密度（<0.8K）宜居邻格；weakest = 最弱饱和邻格（冲突压入目标）
            int best = -1; float bestK = -1f;
            var candidates = new System.Collections.Generic.List<int>();
            int weakest = -1; float weakestP = float.MaxValue;
            foreach (int nb in ctx.Grid.Neighbors[i])
            {
                if (!ctx.Grid.IsLandCell(nb) || ctx.CellK[nb] <= 0f) continue;
                float np = cells[nb].Population;
                float nk = ctx.CellK[nb];
                if (cells[nb].TribeId < 0 || np < 0.8f * nk)
                {
                    candidates.Add(nb);
                    if (nk > bestK) { bestK = nk; best = nb; }
                }
                else if (np < weakestP) { weakestP = np; weakest = nb; }
            }
            if (candidates.Count == 0 && weakest < 0) continue;

            // 主候选：最优宜居格 25%（10% 随机偏差选次优，避免全涌同一格）
            if (candidates.Count > 0)
            {
                int target = best;
                if (ctx.Rng.NextDouble() < 0.1 && candidates.Count > 1)
                {
                    for (int k = 0; k < candidates.Count; k++)
                        if (candidates[k] != best) { target = candidates[k]; break; }
                }
                MoveTo(ctx, i, target, p * CivSimContext.MigrateShare);
                // 其余候选：各播种 8%（多路扩散 → 前沿成面铺开，而非单线推进）
                foreach (int c in candidates)
                    if (c != target)
                        MoveTo(ctx, i, c, p * CivSimContext.MigrateShareSecondary);
            }
            else if (weakest >= 0)
            {
                // 全饱和 → 冲突压入最弱邻格：尝试转移 15%，30% 冲突损耗（资源争夺死亡）
                float push = p * 0.15f;
                cells[i].Population -= push;
                cells[weakest].Population += push * 0.7f;
            }
        }
    }

    private static void MoveTo(CivSimContext ctx, int from, int to, float amount)
    {
        var cells = ctx.Cells;
        if (amount <= 0f) return;
        cells[from].Population -= amount;   // 源格泄压（总人口守恒：迁移不产生/消灭人口）
        if (cells[to].TribeId < 0)
        {
            // 开辟新地：谱系继承来源部落（同起源扩散）
            cells[to].TribeId = cells[from].TribeId;
            cells[to].Culture = cells[from].Culture;
            cells[to].Tech = cells[from].Tech;
        }
        cells[to].Population += amount;
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑥ 文化传播（相邻同化）：相邻格人口比 >3:1 → 强文化以 50%/tick 概率同化弱格
//    （语言/习俗随人口优势扩散；真实：农业人口扩张携带文化替代狩猎采集者）。
// ══════════════════════════════════════════════════════════════════
public sealed class CultureModel : CivModelBase
{
    public override string Name => "文化传播";
    public override int Order => 40;

    public override void Execute(CivSimContext ctx)
    {
        var cells = ctx.Cells;
        var nb = ctx.Grid.Neighbors;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].TribeId < 0) continue;
            foreach (int j in nb[i])
            {
                if (j <= i || cells[j].TribeId < 0) continue;   // 只处理 i<j 避免双向重复
                float pi = cells[i].Population, pj = cells[j].Population;
                if (pi > CivSimContext.AssimilateRatio * pj && ctx.Rng.NextDouble() < CivSimContext.AssimilateChance)
                    cells[j].Culture = cells[i].Culture;
                else if (pj > CivSimContext.AssimilateRatio * pi && ctx.Rng.NextDouble() < CivSimContext.AssimilateChance)
                    cells[i].Culture = cells[j].Culture;
            }
        }
    }
}

// ══════════════════════════════════════════════════════════════════
// ⑦ 部落竞争（轻度）：超载格资源枯竭衰减（>1.3K 每 tick −5%）+ 迁徙冲突损耗。
//    石器时代无"战争"，只有密度压力下的消耗/替换（生态学竞争模型）。
// ══════════════════════════════════════════════════════════════════
public sealed class CompetitionModel : CivModelBase
{
    public override string Name => "部落竞争";
    public override int Order => 50;

    public override void Execute(CivSimContext ctx)
    {
        var cells = ctx.Cells;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].TribeId < 0) continue;
            float K = ctx.CellK[i];
            if (K > 0f && cells[i].Population > CivSimContext.OvercrowdLimit * K)
                cells[i].Population *= 0.95f;   // 超载枯竭（缓慢消耗，给迁徙留时间）
        }
    }
}
