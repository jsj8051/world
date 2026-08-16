// 职责：①a 农田开垦（Order 6）——农业 band 每 tick 提高领地格开垦率。
using Godot;

namespace World.CivSim;

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
