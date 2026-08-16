// 职责：①b 影响力场（Order 8）——每格归属 = argmax(P×CarryMult×w(d))，粘性：非 owner 需超现 owner×1.15。
namespace World.CivSim;

// ══════════════════════════════════════════════════════════════════
// ①b 影响力场（Order 8）：每格归属 = argmax(P×CarryMult×w(d))；粘性：非 owner 需超现 owner×1.15。
//     领地 = 归属格集合（Voronoi 胞自动涌现，无主动宣示/竞争操作——竞争即场对比）。
// ══════════════════════════════════════════════════════════════════
public sealed class InfluenceModel : CivModelBase
{
    public override string Name => "影响力归属";
    public override int Order => 8;

    public override void Execute(CivSimContext ctx)
    {
        ctx.RebuildInfluence();
    }
}
