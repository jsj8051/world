// 职责：Border conflict (Order 75)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Policies;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Military;
namespace World.CivSim.Mechanics.Military;


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

    protected override void Apply(CivSimContext ctx)
    {
        if (ctx.LockedUntil == null || ctx.Polities.Count < 2) return;
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
            var eo = FindPolity(ctx, owner);
            var ec = FindPolity(ctx, ch);
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
    /// 基础 ConflictChance × 政体整合倍率 × 继承窗口倍率——对象差异走策略多态（ConflictPolicies.Of 查表，2026-08-23）：
    ///   同国家：×0.25（内部秩序，Weber 强制力垄断）+ 继承窗口豁免（王朝制度化——同国不内战）；
    ///   同酋邦：×0.5（酋长仲裁）+ 继承窗口 ×2（权力真空 → 继承战争，Kirch）；
    ///   跨邦：×1 + 窗口 ×2。</summary>
    internal static float ConflictChanceOf(CivSimContext ctx, Polity a, Polity b)
    {
        float chance = CivSimContext.ConflictChance;
        var policy = ConflictPolicies.Of(a, b);   // 策略查表（按政治体关系——无身份 if-else，2026-08-23）
        chance *= policy.InternalMult;            // 内部秩序倍率（默认 1 = 无整合）
        // ⚠️ 2026-08-19 阶段5：交战国边境冲突 ×2（战争状态下的治安战更凶——外交断交的格级表现）
        if (a.StateId >= 0 && b.StateId >= 0 && WarModel.IsAtWar(ctx, a.StateId, b.StateId))
            chance *= CivSimContext.WarConflictMult;
        bool succession = a.SuccessionUntil > ctx.Tick || b.SuccessionUntil > ctx.Tick;
        if (succession && !policy.SuccessionExempt) chance *= CivSimContext.SuccessionConflictMult;   // 王朝豁免（策略提供）
        return chance;
    }

    internal static void ResolveConflict(CivSimContext ctx, Polity challenger, Polity owner, int cell)
    {
        // 胜率：P×MilitMult 对比（武器科技加成；随机——弱 band 可爆冷）
        float pC = challenger.P * TechTable.MilitaryMult(challenger.TechKeys);
        float pO = owner.P * TechTable.MilitaryMult(owner.TechKeys);
        // ⚠️ 2026-08-17 联盟合力（Kirch：防御方是酋邦时，入侵者面对酋邦总力量——人多势众，非加成系数）
        if (owner.ChiefdomId >= 0)
        {
            for (int i = 0; i < ctx.Polities.Count; i++)
            {
                var m = ctx.Polities[i];
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
                if (ctx.CellPolities[loser.Cell] == loser) ctx.CellPolities[loser.Cell] = null;   // 一格一实体
                loser.Cell = target;
                loser.LastMigrateTick = ctx.Tick;
                ctx.CellPolities[target] = loser;
                ctx.Migrations++;
            }
        }
        ctx.Conflicts++;
    }

    private static Polity FindPolity(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (ctx.Polities[i].Id == id && !ctx.Polities[i].Dead) return ctx.Polities[i];
        return null;
    }
}
