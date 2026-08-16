// Responsibility: State emergence (Order 49) - extracted from CivModels.cs verbatim (pure refactor).
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

namespace World.CivSim;


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
