// 职责：军事战争（Order 51）——阶段5 军事征服（docs/阶段5设计-军事征服.md；2026-08-23 概念=机制组合迁移至 Mechanics/Military/）。
// ⚠️ 2026-08-23 战争结算 v2（用户拍板"参数+随机直接结算"——docs 五·补2）：
//   宣战 = 可行性（冷却/相邻/池足）× 动机门（领土/压力/优势/世仇——WarAims 纯函数，双向评估）
//     × 关系/实力调节（WarAimPolicies 策略族）；去"单 tick 至多 1 场"限制。
//   会战 = 军力 × 士气骰子 × 天气事件（接交战地真实气候）；会战后瘟疫事件（进程随机事件）。
//   结算 = 硬路径（军力比 ≥ 碾压确认线不掷）→ 概率分档（中间地带三档权重掷）+ 声望影响。
//   旧"碾压→吞并 / 险胜→朝贡 / 超时→停战"确定性分档被 v2 取代；WarMaxTicks=60 兜底保留。
using Godot;
using System;
using System.Collections.Generic;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Policies;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Military;
namespace World.CivSim.Mechanics.Military;

// ══════════════════════════════════════════════════════════════════
// ①m 军事战争（Order 51，2026-08-19 阶段5；用户拍板 P1 仅国家 / P3 战争=外交状态 / P4 战果分档）：
//    战争 = 两个国家之间的**持续外交状态**（War 段入档 v14——过程状态不可派生重建），
//    不是瞬时事件。生命周期：宣战（领地相邻+池足+概率门控+冷却）→ 会战（WarBattleInterval
//    节奏，军力 = Σ 成员 P×MilitMult × 都城加成 × 城墙防御加成）→ 战果分档结算：
//      碾压（胜场 ≥ WarAnnexWins 且军力比 ≥ WarPowerRatio）→ 吞并（成员 ConqueredBy 强制
//      效忠征服者——ChiefdomModel 下 tick 重建生效；首领流放；战利品入池；原国家消失）；
//      险胜（胜场 ≥ WarTributeWins）→ 朝贡（TributeTo 模式：每 tick 转移贡赋 + 边境割地）；
//      超时（WarMaxTicks）→ 停战（无赔偿）。
//    接线：ConflictModel（交战国冲突 ×2）、TradeModel（交战国断交）、
//      ChiefdomModel（ConqueredBy 强制归属）、StateModel（吞并后原国自动消失）。
//    确定性：会战胜负走 Rng（种子确定性）；其余纯函数/固定遍历序——读档续跑无分叉（T04 覆盖）。
// ══════════════════════════════════════════════════════════════════
public sealed class WarModel : CivModelBase
{
    public override string Name => "军事战争";
    public override int Order => 51;

    protected override void Apply(CivSimContext ctx)
    {
        // ── ① 现有战争：朝贡转移 / 停战 / 会战 / 结算（倒序遍历——结算可能移除）──
        for (int i = ctx.Wars.Count - 1; i >= 0; i--)
        {
            var w = ctx.Wars[i];
            if (w.IsTribute) { ProcessTribute(ctx, w); continue; }
            if (ctx.Tick - w.StartTick >= CivSimContext.WarMaxTicks)
            {
                // ⚠️ 2026-08-23 v2：超时停战无赔偿，但净胜场差大者仍占声望优势（僵持中的相对胜利）
                if (w.WinsA != w.WinsB)
                {
                    var byId = IdIndex(ctx);
                    if (w.WinsA > w.WinsB) ApplyPrestige(ctx, w.StateIdA, w.StateIdB, w.WinsA - w.WinsB, byId);
                    else ApplyPrestige(ctx, w.StateIdB, w.StateIdA, w.WinsB - w.WinsA, byId);
                }
                ctx.Wars.RemoveAt(i);
                continue;
            }   // 停战
            if (ctx.Tick - w.LastBattleTick >= CivSimContext.WarBattleInterval)
                Battle(ctx, w);
            if (TryResolve(ctx, w)) ctx.Wars.RemoveAt(i);   // 吞并移除；朝贡转 Tribute 模式保留
        }
        // ── ② 宣战（低频门控——2026-08-23 v2：动机门 + 概率天然控制总量，去"单 tick 至多 1 场"）──
        DeclareWars(ctx);
    }

    /// <summary>该国家当前是否处于战争（交战或朝贡期——敌对关系，断交/冲突×2 依据）。</summary>
    public static bool IsAtWar(CivSimContext ctx, int stateA, int stateB)
    {
        if (stateA < 0 || stateB < 0 || stateA == stateB) return false;
        for (int i = 0; i < ctx.Wars.Count; i++)
        {
            var w = ctx.Wars[i];
            if (w.Involves(stateA) && w.Involves(stateB)) return true;
        }
        return false;
    }

    /// <summary>会战胜率（纯函数——T71 直接断言，免概率采样噪声；fA=挑战方军力，fB=守方军力）。</summary>
    internal static float BattleChanceOf(float fA, float fB) => fA / Mathf.Max(0.0001f, fA + fB);

    // ──────────────────────────────────────────────
    // 会战
    // ──────────────────────────────────────────────
    private static void Battle(CivSimContext ctx, War w)
    {
        w.LastBattleTick = ctx.Tick;
        var byId = IdIndex(ctx);
        var settleById = SettleIndex(ctx);
        // ⚠️ 2026-08-23 战争结算 v2：天气事件（接交战地真实气候 MonthTemp/MonthPrecip——
        //   当地本来就这样，非凭空随机；严寒/雨季阻进攻方，严寒/干旱双方附加损耗）
        var wth = WarWeather.Of(ctx, w);
        float fA = PowerOf(ctx, w.StateIdA, w, byId, settleById);
        float fB = PowerOf(ctx, w.StateIdB, w, byId, settleById);
        if (wth.AttackerMult != 1f) fA *= wth.AttackerMult;   // 进攻方（宣战方 A）受阻
        // 士气骰子（每场独立掷——战争迷雾：同参数每场波动）
        fA *= MoraleRoll(ctx);
        fB *= MoraleRoll(ctx);
        bool aWins = ctx.Rng.NextDouble() < BattleChanceOf(fA, fB);
        if (aWins)
        {
            w.WinsA++;
            ApplyLoss(ctx, w.StateIdB, byId);
            ApplyTreasuryLoss(ctx, w.StateIdA, byId);   // 胜方军费也烧（打仗烧钱）
        }
        else
        {
            w.WinsB++;
            ApplyLoss(ctx, w.StateIdA, byId);
            ApplyTreasuryLoss(ctx, w.StateIdB, byId);
        }
        if (wth.ExtraLoss > 0f)   // 天气附加损耗（严寒/干旱——双方都吃苦）
        {
            ApplyWeatherLoss(ctx, w.StateIdA, byId, wth.ExtraLoss);
            ApplyWeatherLoss(ctx, w.StateIdB, byId, wth.ExtraLoss);
        }
        Plague(ctx, w);   // 瘟疫事件（会战后营地病——打越久越易发）
    }

    /// <summary>士气骰子（每场会战独立掷——战争迷雾：同参数结果有波动）。</summary>
    private static float MoraleRoll(CivSimContext ctx) =>
        CivSimContext.WarMoraleMin
        + (float)ctx.Rng.NextDouble() * (CivSimContext.WarMoraleMax - CivSimContext.WarMoraleMin);

    /// <summary>会战胜方军费损耗（打仗烧钱——败方已有 ApplyLoss 的 WarLoss；胜方小损耗）。</summary>
    private static void ApplyTreasuryLoss(CivSimContext ctx, int stateId, Polity[] byId)
    {
        float keep = 1f - CivSimContext.WarTreasuryLoss;
        var members = MembersOf(ctx, stateId);
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            var m = byId[mid];
            if (m == null || m.Dead) continue;
            m.Contributed *= keep;
        }
    }

    /// <summary>天气附加损耗（严寒/干旱——双方人口按比例减，同会战损耗口径）。</summary>
    private static void ApplyWeatherLoss(CivSimContext ctx, int stateId, Polity[] byId, float loss)
    {
        float keep = 1f - loss;
        var members = MembersOf(ctx, stateId);
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            var m = byId[mid];
            if (m == null || m.Dead) continue;
            m.P *= keep;
        }
    }

    /// <summary>瘟疫事件（进程事件 A——营地病：斑疹伤寒/痢疾；围城/营地拥挤史实）。
    /// 触发概率随战争持续递增（打越久营越脏）；Rng 选遭灾方 + 减员幅度（5~10%）。</summary>
    private static void Plague(CivSimContext ctx, War w)
    {
        int elapsed = ctx.Tick - w.StartTick;
        float chance = CivSimContext.WarPlagueBase
            + CivSimContext.WarPlagueRamp * (elapsed / CivSimContext.WarPlagueRampTicks);
        if (ctx.Rng.NextDouble() >= chance) return;
        bool hitsA = ctx.Rng.NextDouble() < 0.5;
        float loss = CivSimContext.WarPlagueLossMin
            + (float)ctx.Rng.NextDouble() * (CivSimContext.WarPlagueLossMax - CivSimContext.WarPlagueLossMin);
        var byId = IdIndex(ctx);
        var members = MembersOf(ctx, hitsA ? w.StateIdA : w.StateIdB);
        float keep = 1f - loss;
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            var m = byId[mid];
            if (m == null || m.Dead) continue;
            m.P *= keep;
            m.Contributed *= keep;
        }
        ctx.WarsPlagued++;
    }

    /// <summary>国家军力 = Σ 成员 P×MilitMult × (1+WarCapitalBonus×都城城镇级（2026-08-23 功能定性）) × 防御加成（城墙，P6）。
    /// ⚠️ 每会战调 2 次（每 5 tick）——成员数百量级，O(成员) 可接受（T18 教训仅约束每 tick 路径）。</summary>
    private static float PowerOf(CivSimContext ctx, int stateId, War w, Polity[] byId, Dictionary<int, Habitation> settleById)
    {
        var members = MembersOf(ctx, stateId);
        float f = 0f;
        int capitalTier = 0, cities = 0;
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            var m = byId[mid];
            if (m == null || m.Dead) continue;
            f += m.P * TechTable.MilitaryMult(m.TechKeys);
            var st = m.PlaceId >= 0 && settleById.TryGetValue(m.PlaceId, out var s) ? s : null;
            if (st == null || st.OccupantId != m.Id) continue;
            if (m.Id == stateId) capitalTier = Math.Max(capitalTier, st.TownTier);   // 都城城镇级（村庄0/集镇1/城市2——2026-08-23 功能定性）
            if (st.IsCity) cities++;                                                        // 城市=要塞（P6——治理中心即城墙）
        }
        f *= 1f + CivSimContext.WarCapitalBonus * capitalTier;
        if (stateId == w.Defender)
            f *= 1f + CivSimContext.WarCityDefenseBonus * cities;   // 防御方城墙加成
        return f;
    }

    /// <summary>会战败方损耗：成员人口 ×(1-WarLoss) + 贡赋池 ×(1-WarLoss)（消耗战——损耗可累积瓦解）。</summary>
    private static void ApplyLoss(CivSimContext ctx, int loserState, Polity[] byId)
    {
        var members = MembersOf(ctx, loserState);
        float keep = 1f - CivSimContext.WarLoss;
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            var m = byId[mid];
            if (m == null || m.Dead) continue;
            m.P *= keep;
            m.Contributed *= keep;
        }
    }

    // ──────────────────────────────────────────────
    // 结算（战果分档，P4——未来改 AI 判定）
    // ──────────────────────────────────────────────
    /// <summary>战果结算（2026-08-23 战争结算 v2 概率分档——取代旧"达标即必然"）：
    ///   硬路径（军力比 ≥ 碾压确认线 → 不掷，必然吞并/朝贡——强弱悬殊无悬念）；
    ///   概率路径（中间地带达标 → 三档权重掷：吞并/朝贡/维持现状——拉锯战看运气）。
    ///   胜负方声望结算（胜者 Prestige↑ 败者↓）。返回 true = 战争已终结（移除）；false = 继续（或转朝贡保留）。</summary>
    private static bool TryResolve(CivSimContext ctx, War w)
    {
        var byId = IdIndex(ctx);
        var settleById = SettleIndex(ctx);
        float fA = PowerOf(ctx, w.StateIdA, w, byId, settleById);
        float fB = PowerOf(ctx, w.StateIdB, w, byId, settleById);
        float hard = CivSimContext.WarPowerRatio * CivSimContext.WarAnnexHardMult;   // 碾压确认线（不掷）
        // ── 硬路径：军力比 ≥ 碾压线 → 必然（强弱悬殊无悬念）──
        if (w.WinsA >= CivSimContext.WarAnnexWins && fA >= fB * hard)
        {
            Annex(ctx, w, winner: w.StateIdA, loser: w.StateIdB, byId);
            ApplyPrestige(ctx, w.StateIdA, w.StateIdB, w.WinsA - w.WinsB, byId);
            return true;
        }
        if (w.WinsB >= CivSimContext.WarAnnexWins && fB >= fA * hard)
        {
            Annex(ctx, w, winner: w.StateIdB, loser: w.StateIdA, byId);
            ApplyPrestige(ctx, w.StateIdB, w.StateIdA, w.WinsB - w.WinsA, byId);
            return true;
        }
        if (w.WinsA >= CivSimContext.WarTributeWins && fA >= fB * hard)
        {
            StartTribute(ctx, w, winner: w.StateIdA, loser: w.StateIdB, byId);
            ApplyPrestige(ctx, w.StateIdA, w.StateIdB, w.WinsA - w.WinsB, byId);
            return false;
        }
        if (w.WinsB >= CivSimContext.WarTributeWins && fB >= fA * hard)
        {
            StartTribute(ctx, w, winner: w.StateIdB, loser: w.StateIdA, byId);
            ApplyPrestige(ctx, w.StateIdB, w.StateIdA, w.WinsB - w.WinsA, byId);
            return false;
        }
        // ── 概率路径：中间地带（达标但未碾压）→ 三档权重掷（Rng 掷档——同样的两国，每次结果落在概率分布）──
        float ratioA = ClampedRatio(fA, fB);
        float ratioB = ClampedRatio(fB, fA);
        float wAnnexA = w.WinsA >= CivSimContext.WarAnnexWins && fA >= fB * CivSimContext.WarPowerRatio
            ? CivSimContext.WarAnnexWeightBase * ratioA * ratioA : 0f;   // ×军力比²——越碾压越可能
        float wAnnexB = w.WinsB >= CivSimContext.WarAnnexWins && fB >= fA * CivSimContext.WarPowerRatio
            ? CivSimContext.WarAnnexWeightBase * ratioB * ratioB : 0f;
        float wTribA = w.WinsA >= CivSimContext.WarTributeWins && fA > fB && wAnnexA <= 0f
            ? CivSimContext.WarTributeWeightBase * ratioA : 0f;   // 吞并优先（同侧不叠加）
        float wTribB = w.WinsB >= CivSimContext.WarTributeWins && fB > fA && wAnnexB <= 0f
            ? CivSimContext.WarTributeWeightBase * ratioB : 0f;
        float total = wAnnexA + wAnnexB + wTribA + wTribB + CivSimContext.WarStalemateWeight;
        if (total <= CivSimContext.WarStalemateWeight) return false;   // 无人达标 → 维持现状（继续打）
        double roll = ctx.Rng.NextDouble() * total;
        if (roll < wAnnexA)
        {
            Annex(ctx, w, w.StateIdA, w.StateIdB, byId);
            ApplyPrestige(ctx, w.StateIdA, w.StateIdB, w.WinsA - w.WinsB, byId);
            return true;
        }
        roll -= wAnnexA;
        if (roll < wAnnexB)
        {
            Annex(ctx, w, w.StateIdB, w.StateIdA, byId);
            ApplyPrestige(ctx, w.StateIdB, w.StateIdA, w.WinsB - w.WinsA, byId);
            return true;
        }
        roll -= wAnnexB;
        if (roll < wTribA)
        {
            StartTribute(ctx, w, w.StateIdA, w.StateIdB, byId);
            ApplyPrestige(ctx, w.StateIdA, w.StateIdB, w.WinsA - w.WinsB, byId);
            return false;
        }
        roll -= wTribA;
        if (roll < wTribB)
        {
            StartTribute(ctx, w, w.StateIdB, w.StateIdA, byId);
            ApplyPrestige(ctx, w.StateIdB, w.StateIdA, w.WinsB - w.WinsA, byId);
            return false;
        }
        return false;   // 维持现状（拉锯战常态）
    }

    /// <summary>军力比 clamp（概率路径安全：硬路径已处理 ≥ 碾压线，此处 < 碾压线——防溢出/NaN）。</summary>
    private static float ClampedRatio(float f, float other)
    {
        float r = f / Mathf.Max(0.0001f, other);
        return Mathf.Min(r, CivSimContext.WarPowerRatio * CivSimContext.WarAnnexHardMult);
    }

    /// <summary>战争声望结算：胜者 Prestige += 净胜场差×WarPrestigeGain，败者 − 同量（clamp 0——可逆，
    /// 接 Sahlins 声望体系：战争胜利 = 大人物慷慨资本的来源之一）。</summary>
    private static void ApplyPrestige(CivSimContext ctx, int winnerState, int loserState, int netWins, Polity[] byId)
    {
        if (netWins <= 0) return;
        float gain = CivSimContext.WarPrestigeGain * netWins;
        var wc = winnerState < byId.Length ? byId[winnerState] : null;
        var lc = loserState < byId.Length ? byId[loserState] : null;
        if (wc != null && !wc.Dead) wc.Prestige += gain;
        if (lc != null && !lc.Dead) lc.Prestige = Mathf.Max(0f, lc.Prestige - gain);
    }

    /// <summary>朝贡开始：转入 TributeTo 模式（每 tick 贡赋转移）+ 边境割地（战争保留——外交状态延续）。</summary>
    private static void StartTribute(CivSimContext ctx, War w, int winner, int loser, Polity[] byId)
    {
        w.TributeTo = winner;
        w.TributeFrom = loser;
        w.TributesLeft = CivSimContext.WarTributeTicks;
        CedeCells(ctx, w, loser, byId);
    }

    /// <summary>吞并：战败国成员 ConqueredBy 强制效忠战胜国酋长（ChiefdomModel 下 tick 重建生效——
    /// 无视庇护半径）；战败国酋长流放驱逐（其国随成员归属消散）；战利品入战胜国池。
    /// ⚠️ 持久痕迹：ConqueredBy 入档（v14）；征服者死亡 → ChiefdomModel 重建清空（效忠脱落）。
    /// ⚠️ StateId 不直接改——StateModel(49) 下 tick 从 ChiefdomId 重建，原国家自动消失（崩溃路径④）。</summary>
    private static void Annex(CivSimContext ctx, War w, int winner, int loser, Polity[] byId)
    {
        var members = MembersOf(ctx, loser);
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid >= byId.Length) continue;
            var m = byId[mid];
            if (m == null || m.Dead) continue;
            if (m.Id == loser) continue;                     // 败国首领不效忠——被流放
            m.ConqueredBy = winner;
        }
        // 首领流放：强制迁移（复用驱逐逻辑——败者饿死兜底由演化自然处理）
        var chief = loser < byId.Length ? byId[loser] : null;
        if (chief != null && !chief.Dead && chief.Cell >= 0 && chief.Cell < ctx.Grid.N)
        {
            int target = SplitMigrateModel.PickMigrateTarget(ctx, chief);
            if (target >= 0)
            {
                if (ctx.CellPolities[chief.Cell] == chief) ctx.CellPolities[chief.Cell] = null;
                chief.Cell = target;
                chief.LastMigrateTick = ctx.Tick;
                ctx.CellPolities[target] = chief;
                ctx.Migrations++;
            }
        }
        // 战利品：战败国贡赋池 × WarPlunderRate 入战胜国池（Tilly：战争养战争）
        var winnerChief = winner < byId.Length ? byId[winner] : null;
        if (winnerChief != null && !winnerChief.Dead)
        {
            float pool = 0f;
            for (int k = 0; k < members.Count; k++)
            {
                int mid = members[k];
                if (mid < byId.Length && byId[mid] != null && !byId[mid].Dead) pool += byId[mid].Contributed;
            }
            winnerChief.Contributed += pool * CivSimContext.WarPlunderRate;
        }
        ctx.WarsAnnexed++;
    }

    /// <summary>朝贡割地：战败国边境格（与战胜国领地相邻）取前 WarCedeCells 格 → CellOwner 归战胜国酋长。
    /// ⚠️ 领地重建（TerritoryModel 45）下 tick 自动整合——CellOwner 是持久字段，读档续跑无分叉。</summary>
    private static void CedeCells(CivSimContext ctx, War w, int loser, Polity[] byId)
    {
        var winnerChief = w.TributeTo < byId.Length ? byId[w.TributeTo] : null;
        if (winnerChief == null || winnerChief.Dead || ctx.CellOwner == null) return;
        int n = ctx.Grid.N;
        int ceded = 0;
        for (int c = 0; c < n && ceded < CivSimContext.WarCedeCells; c++)
        {
            int o = ctx.CellOwner[c];
            if (o < 0) continue;
            // 败国格：酋长自身格 或 成员格（成员 ChiefdomId == 败国 id——国家=酋邦，同 Id 语义）
            var oPolity = o < byId.Length ? byId[o] : null;
            bool isLoser = o == loser || (oPolity != null && !oPolity.Dead && oPolity.ChiefdomId == loser);
            if (!isLoser) continue;
            // 边境判定：任一邻居属战胜国
            bool bordersWinner = false;
            foreach (int nb in ctx.Grid.Neighbors[c])
            {
                int no = ctx.CellOwner[nb];
                if (no == winnerChief.Id) { bordersWinner = true; break; }
            }
            if (!bordersWinner) continue;
            ctx.CellOwner[c] = winnerChief.Id;
            ceded++;
        }
    }

    // ──────────────────────────────────────────────
    // 朝贡转移
    // ──────────────────────────────────────────────
    /// <summary>朝贡期：每 tick 转移 战败国总人口×WarTributeRate 入战胜国池（Earle 贡赋的对外延伸）。
    /// TributesLeft 递减，归零 → 战争终结（朝贡随战争存在，停战即止——不形成持久朝贡国）。</summary>
    private static void ProcessTribute(CivSimContext ctx, War w)
    {
        var byId = IdIndex(ctx);
        var payers = MembersOf(ctx, w.TributeFrom);
        float pop = 0f;
        for (int k = 0; k < payers.Count; k++)
        {
            int mid = payers[k];
            if (mid < byId.Length && byId[mid] != null && !byId[mid].Dead) pop += byId[mid].P;
        }
        float amount = pop * CivSimContext.WarTributeRate;
        // 战败国分摊（按人口比例——简化：均摊；确定性与成员表序无关）
        if (amount > 0f && payers.Count > 0)
        {
            float perCap = amount / pop;
            for (int k = 0; k < payers.Count; k++)
            {
                int mid = payers[k];
                var m = mid < byId.Length ? byId[mid] : null;
                if (m == null || m.Dead || m.P <= 0f) continue;
                m.Contributed = Mathf.Max(0f, m.Contributed - perCap * m.P);
            }
            var winnerChief = w.TributeTo < byId.Length ? byId[w.TributeTo] : null;
            if (winnerChief != null && !winnerChief.Dead) winnerChief.Contributed += amount;
        }
        if (--w.TributesLeft <= 0) ctx.Wars.Remove(w);
    }

    // ──────────────────────────────────────────────
    // 宣战
    // ──────────────────────────────────────────────
    private static void DeclareWars(CivSimContext ctx)
    {
        // 国家集合：至尊酋长（StateId == 自身 Id）且正式国家（Size ≥ 2）
        // ⚠️ 2026-08-23 概念 = 机制组合（Phase 1）：宣战资格走策略多态（WarPolicies.Of 查表——
        //   仅国家可宣战，阶段5 P1 拍板；未来村庄民兵/城防策略在此扩展）
        var states = new List<Polity>();
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            if (!WarPolicies.Of(e).CanDeclareWar(e)) continue;
            states.Add(e);
        }
        float reachKm = (2 * CivSimContext.InfluenceRadius + 1) * Mathf.Sqrt(ctx.Grid.CellAreaKm2);
        for (int i = 0; i < states.Count; i++)
        {
            var a = states[i];
            for (int j = i + 1; j < states.Count; j++)
            {
                var b = states[j];
                // ⚠️ 2026-08-23 战争结算 v2 动机门（用户拍板"该打的才打"——旧条件零动机 → 无差别开战）：
                //   **双向评估**（复查修正：初版只评估 Id 较小方——B 恨 A 而 A 无动机永不开战，方向性不对称；
                //   现任一方向有动机即可开战，挑战方 = 有动机方；双方都有动机时遍历序优先，确定性）
                //   （纯函数积木 WarAims + 政体差异策略族 WarAimPolicies.Of——查表工厂，零身份 if-else）
                float aimAB = WarAimPolicies.Of(a, b).AimMult(ctx, a, b);
                float aimBA = WarAimPolicies.Of(b, a).AimMult(ctx, b, a);
                var ch = a; var df = b; float aim = aimAB;
                if (aimAB <= 0f && aimBA > 0f) { ch = b; df = a; aim = aimBA; }
                if (aim <= 0f) continue;   // 双方都无动机 → 不打
                if (!CanDeclare(ctx, ch, df, reachKm)) continue;   // 条件判定（纯函数——T70 直接断言；池足只查挑战方）
                if (ctx.Rng.NextDouble() >= CivSimContext.WarDeclareChance * aim) continue;
                ctx.Wars.Add(new War
                {
                    StateIdA = ch.Id,
                    StateIdB = df.Id,
                    Defender = df.Id,
                    StartTick = ctx.Tick,
                    LastBattleTick = ctx.Tick,
                });
                ch.LastWarTick = df.LastWarTick = ctx.Tick;   // 参战冷却（双方）
                ctx.WarsDeclared++;
                // ⚠️ 2026-08-23 v2：去掉"单 tick 至多 1 场"限制（用户拍板——动机门修对后
                //   同 tick 多场是正常历史（大国多线开战）；总量由动机门 + 概率天然控制，无需护栏）
            }
        }
    }

    /// <summary>宣战条件判定（纯函数——T70 直接断言，免概率噪声）：
    /// 冷却过 + 未交战 + 领地相邻 + 宣战国贡赋池足（WarMinPoolPerCap——穷兵不打）。
    /// 概率门控（WarDeclareChance）在 DeclareWars 内部，不在本函数。</summary>
    internal static bool CanDeclare(CivSimContext ctx, Polity a, Polity b, float reachKm)
    {
        if (ctx.Tick - a.LastWarTick < CivSimContext.WarCooldownTicks) return false;
        if (ctx.Tick - b.LastWarTick < CivSimContext.WarCooldownTicks) return false;
        if (IsAtWar(ctx, a.Id, b.Id)) return false;
        if (ctx.Grid.DistKm(a.Cell, b.Cell) > reachKm) return false;   // 远隔无接触（预过滤）
        if (!StatesTouch(ctx, a, b)) return false;                     // 国家领地接触
        if (PoolOf(ctx, a.Id) < PopOf(ctx, a.Id) * CivSimContext.WarMinPoolPerCap) return false;   // 池足（穷兵不打）
        return true;
    }

    /// <summary>国家领地接触：任一成员对领地边界接触（TerritoryTouches——同酋邦凝聚判定）。
    /// ⚠️ 用 Id 索引（O(成员对) 一次成型——线性 Find 在成员数百时是亿级比较）。</summary>
    private static bool StatesTouch(CivSimContext ctx, Polity a, Polity b)
    {
        var byId = IdIndex(ctx);
        var ma = MembersOf(ctx, a.Id);
        var mb = MembersOf(ctx, b.Id);
        float reachKm = (2 * CivSimContext.InfluenceRadius + 1) * Mathf.Sqrt(ctx.Grid.CellAreaKm2);
        for (int i = 0; i < ma.Count; i++)
        {
            int xId = ma[i];
            var x = xId < byId.Length ? byId[xId] : null;
            if (x == null || x.Dead) continue;
            for (int k = 0; k < mb.Count; k++)
            {
                int yId = mb[k];
                var y = yId < byId.Length ? byId[yId] : null;
                if (y == null || y.Dead) continue;
                if (ctx.Grid.DistKm(x.Cell, y.Cell) > reachKm) continue;
                if (CivSimContext.TerritoryTouches(ctx, x, y)) return true;
            }
        }
        return false;
    }

    // ──────────────────────────────────────────────
    // 辅助（成员表 = 酋邦成员表——国家是酋邦的制度化；O(成员)）
    // ──────────────────────────────────────────────
    private static List<int> MembersOf(CivSimContext ctx, int stateId)
    {
        if (ctx.ChiefdomCells == null || stateId < 0 || stateId >= ctx.ChiefdomCells.Length) return EmptyList;
        return ctx.ChiefdomCells[stateId];
    }

    private static readonly List<int> EmptyList = new();

    private static float PopOf(CivSimContext ctx, int stateId)
    {
        float pop = 0f;
        var byId = IdIndex(ctx);
        var members = MembersOf(ctx, stateId);
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid < byId.Length && byId[mid] != null && !byId[mid].Dead) pop += byId[mid].P;
        }
        return pop;
    }

    private static float PoolOf(CivSimContext ctx, int stateId)
    {
        float pool = 0f;
        var byId = IdIndex(ctx);
        var members = MembersOf(ctx, stateId);
        for (int k = 0; k < members.Count; k++)
        {
            int mid = members[k];
            if (mid < byId.Length && byId[mid] != null && !byId[mid].Dead) pool += byId[mid].Contributed;
        }
        return pool;
    }

    private static Polity[] IdIndex(CivSimContext ctx)
    {
        int bufLen = Math.Max(ctx.NextPolityId, ctx.Polities.Count + 1);
        var byId = new Polity[bufLen];
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (!ctx.Polities[i].Dead && ctx.Polities[i].Id < bufLen) byId[ctx.Polities[i].Id] = ctx.Polities[i];
        return byId;
    }

    private static Dictionary<int, Habitation> SettleIndex(CivSimContext ctx)
    {
        var d = new Dictionary<int, Habitation>();
        if (ctx.Habitations != null)
            foreach (var s in ctx.Habitations)
                d[s.Id] = s;
        return d;
    }
}
