// 职责：Chiefdom cohesion (Order 46)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Entities;
using World.CivSim.Policies;
using World.CivSim.Mechanics.Politics;
namespace World.CivSim.Mechanics.Politics;


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

    protected override bool CanApply(CivSimContext ctx) =>
        ctx.Tick - ctx.ChiefdomLastEval >= CivSimContext.ChiefdomEvalEvery;   // 频率守卫（与领地同频）

    protected override void Apply(CivSimContext ctx)
    {
        ctx.ChiefdomLastEval = ctx.Tick;   // 频率时间戳（副作用）
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
        var oldChiefdoms = new Dictionary<int, List<Band>>();
        for (int i = 0; i < ctx.Bands.Count; i++)
        {
            var e = ctx.Bands[i];
            if (e.Dead || e.ChiefdomId < 0) continue;
            if (!oldChiefdoms.TryGetValue(e.ChiefdomId, out var l)) oldChiefdoms[e.ChiefdomId] = l = new List<Band>();
            l.Add(e);
        }
        foreach (var kv in oldChiefdoms)
        {
            if (kv.Value.Count < CivSimContext.ChiefdomMinBands) continue;   // 单成员不算酋邦
            bool hasChief = false, inCrisis = false;
            foreach (var m in kv.Value)
            {
                if (m.IsChief) hasChief = true;
                if (m.SuccessionUntil > ctx.Tick) inCrisis = true;
            }
            if (!hasChief && !inCrisis)
            {
                // 酋长死亡（且未在危机中）→ 继承窗口：Prestige 最高者成为继位竞争中心
                Band top = null;
                foreach (var m in kv.Value) if (top == null || m.Prestige > top.Prestige) top = m;
                if (top != null) top.SuccessionUntil = ctx.Tick + CivSimContext.SuccessionWindowTicks;
            }
        }

        // ── ② 收集酋长（Prestige 降序 + Id 升序——确定性遍历序：先处理声望最高者）──
        var chiefs = new List<Band>();
        for (int i = 0; i < ctx.Bands.Count; i++)
        {
            var e = ctx.Bands[i];
            if (e.Dead || !e.IsChief || e.Cell < 0 || e.Cell >= ctx.Grid.N) continue;
            chiefs.Add(e);
        }
        chiefs.Sort((x, y) => y.Prestige != x.Prestige ? y.Prestige.CompareTo(x.Prestige) : x.Id.CompareTo(y.Id));

        // ── ③ 庇护 BFS：每酋长在 ChiefReach 内宣告庇护（band 只认声望更高的酋长）──
        //    Id 索引缓冲（Id 有空洞——NextBandId 分配，勿用列表索引）
        int bufLen = Math.Max(ctx.NextBandId, ctx.Bands.Count + 1);
        var bestPrestige = new float[bufLen];
        var bestChief = new int[bufLen];
        System.Array.Fill(bestChief, -1);
        foreach (var c in chiefs)
        {
            ctx.BfsRadius(c.Cell, CivSimContext.ChiefReach, (cell, _) =>
            {
                var e = ctx.CellBands[cell];
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
        //    Id 索引（ConqueredBy 强制归属查询——阶段5 吞并效忠，见 WarModel.Annex）
        var byId = new Band[bufLen];
        for (int i = 0; i < ctx.Bands.Count; i++)
        {
            var e = ctx.Bands[i];
            if (!e.Dead && e.Id < bufLen) byId[e.Id] = e;
        }
        var memberCount = new Dictionary<int, int>();   // chiefId → 成员数（含酋长自己）
        for (int i = 0; i < ctx.Bands.Count; i++)
        {
            var e = ctx.Bands[i];
            if (e.Dead) continue;
            if (e.SuccessionUntil > 0 && e.SuccessionUntil <= ctx.Tick) e.SuccessionUntil = -1;   // 窗口过期清除
            if (e.TerritoryId < 0) { e.ChiefdomId = -1; e.ChiefdomSize = 1; continue; }   // 无领地不入邦
            // ⚠️ 2026-08-23 概念 = 机制组合（Phase 1）：归属的对象差异（被征服效忠/酋长自中心/自由庇护人）
            //   走策略多态——MembershipPolicies.Of 查表（含效忠有效性校验：失效 → 脱落回退），
            //   Assign 多态执行，机制体内零身份 if-else 链。
            //   （Tilly 战争制造国家：被征服者的政治归属由武力决定，不由声望竞争决定——Conquered 策略）
            var policy = MembershipPolicies.Of(ctx, e, byId);
            int chiefId = policy.Assign(ctx, e, byId, bestChief);
            e.ChiefdomId = chiefId;
            if (chiefId < 0) { e.ChiefdomSize = 1; continue; }   // 半径内无酋长 → 独立
            memberCount[chiefId] = memberCount.TryGetValue(chiefId, out var n) ? n + 1 : 1;
        }

        // ── ⑤ 解散：< ChiefdomMinBands → -1（单人酋邦不成邦）──
        for (int i = 0; i < ctx.Bands.Count; i++)
        {
            var e = ctx.Bands[i];
            if (e.Dead || e.ChiefdomId < 0) continue;
            if (memberCount.TryGetValue(e.ChiefdomId, out var n) && n < CivSimContext.ChiefdomMinBands)
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
        for (int i = 0; i < ctx.Bands.Count; i++)
        {
            var e = ctx.Bands[i];
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
