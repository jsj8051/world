using System;
using System.Collections.Generic;
using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Mechanics.State;

/// <summary>
/// 国家涌现 ⑤ 写入/清空（规范积木——国家配方的执行端："State = AND(①②③④) → Assign"）。
/// 确定性重建 StateId/StateSize（纯派生不存档；读档入口 SettleDerived 同用——同一公式无分叉）。
/// ① 清空全部 StateId/StateSize；② 按酋邦（ChiefdomCells 成员表）判定涌现条件（4 个规范积木 AND）；
/// ③ 满足 → 全部成员 StateId = 酋长 Id、StateSize = 成员数。
/// ⚠️ 2026-08-16 性能（T18 暴露 309s 劣化）：每 tick 执行 → 必须 O(1) 索引——Id→Polity 数组
///   + PlaceId→Habitation 字典（线性扫描 × 成员数 × 酋邦数 = 每 tick 上千万比较）。
/// </summary>
public static class StateAssign
{
    public static void Rebuild(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            ctx.Polities[i].StateId = -1;
            ctx.Polities[i].StateSize = 1;
        }
        if (ctx.ChiefdomCells == null) return;
        // Id 索引（O(1) 取实体——每 tick 跑，线性扫描是性能杀手）
        int bufLen = Math.Max(ctx.NextPolityId, ctx.Polities.Count + 1);
        var byId = new Polity[bufLen];
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (!ctx.Polities[i].Dead && ctx.Polities[i].Id < bufLen) byId[ctx.Polities[i].Id] = ctx.Polities[i];
        // 聚落索引：Habitation.Id → Habitation（O(1) 查询）
        var settleById = new Dictionary<int, Habitation>();
        if (ctx.Habitations != null)
            foreach (var s in ctx.Habitations)
                settleById[s.Id] = s;
        // 按酋邦遍历（ChiefdomId = 酋长 Id——ChiefdomModel.Rebuild 已填成员表）
        for (int chiefId = 0; chiefId < ctx.ChiefdomCells.Length; chiefId++)
        {
            var members = ctx.ChiefdomCells[chiefId];
            if (members == null || members.Count < CivSimContext.ChiefdomMinPolities) continue;
            Polity chief = chiefId < bufLen ? byId[chiefId] : null;
            if (chief == null || chief.Dead || !chief.IsChief) continue;   // 无酋长 → 非国家（权力真空）
            if (!IsState(ctx, chief, members, byId, settleById)) continue;

            int size = members.Count;
            for (int k = 0; k < members.Count; k++)
            {
                int mid = members[k];
                if (mid >= bufLen) continue;
                Polity m = byId[mid];
                if (m == null || m.Dead) continue;
                m.StateId = chiefId;
                m.StateSize = size;
            }
        }
    }

    /// <summary>国家涌现判定 = 规范积木 AND 组合（纯函数——全部输入已入档/派生）。
    /// ⚠️ 2026-08-24 用户拍板：国家 = 三条件（都城 + 贡赋 + 存续）——"首都即可承担行政网络"，
    ///   城邦就是国家默认形态；次级中心（决策层级）从硬性条件移除（回归普通机制，非概念门槛）。</summary>
    private static bool IsState(CivSimContext ctx, Polity chief, List<int> members, Polity[] byId, Dictionary<int, Habitation> settleById)
    {
        Habitation capital = StateCapitalCheck.Of(chief, settleById);   // 解析都城（供 ④ 复用）
        if (!StateCapitalCheck.Check(ctx, chief, settleById)) return false;          // ① 都城（治理中心）
        if (!StateDwellCheck.Check(ctx, capital)) return false;                      // ④ 存续（弱滞回）
        return StateTributePoolCheck.Check(members, byId);                           // ③ 贡赋盈余
    }
}
