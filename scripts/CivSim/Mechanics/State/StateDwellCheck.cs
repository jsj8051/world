using World.CivSim;
using World.CivSim.Entities;

namespace World.CivSim.Mechanics.State;

/// <summary>
/// 国家涌现 ④ 存续判定（规范积木，纯函数）。
/// 条件：都城实体存续时长 Tick − BornTick ≥ StateDwellTicks——场所比人长寿（BornTick 单调 = 天然弱滞回）。
/// </summary>
public static class StateDwellCheck
{
    /// <summary>④ 存续判定：都城聚落存在且存续达线。</summary>
    public static bool Check(CivSimContext ctx, Habitation capital) =>
        capital != null && ctx.Tick - capital.BornTick >= CivSimContext.StateDwellTicks;
}
