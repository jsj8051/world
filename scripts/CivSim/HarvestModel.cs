// Responsibility: Harvest (Order 9) - extracted from CivModels.cs verbatim (pure refactor).
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

namespace World.CivSim;


// ══════════════════════════════════════════════════════════════════
// ①c 采集收获（Order 9）：领地建筑分配产出（2026-08-17 凹化+等边际——
//     采集/农田每格建筑，等边际闭式分配劳动力；FBerryLast/FFarmLast 分量缓存）。
// ══════════════════════════════════════════════════════════════════
public sealed class HarvestModel : CivModelBase
{
    public override string Name => "采集收获";
    public override int Order => 9;

    public override void Execute(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead) continue;
            // ⚠️ 2026-08-18 T04 修复（与 RecomputeProduction 同式）：先归零分量——AllocateAndProduce
            //   领地为空时提前 return 0 不赋值分量，不归零则陈旧 FFarm/FHerd 残留（无领地挂产出）。
            e.FFarmLast = 0f; e.FHerdLast = 0f; e.FBerryLast = 0f;
            e.FHuntLast = ctx.AllocateAndProduce(e);   // 采集（猎+果）+ 牧场 + 农业（等边际分配后实际产出）
            e.FLast = e.FHuntLast + e.FFarmLast + e.FHerdLast;
        }
    }
}
