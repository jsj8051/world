// 职责：Mode selection (Order 30)（2026-08-19 拆分自 CivModels.cs 纯重构；2026-08-23 概念=机制组合迁移至 Mechanics/<域>/ 目录）
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

using World.CivSim;
using World.CivSim.Mechanics.Territory;
namespace World.CivSim.Mechanics.Territory;


// ══════════════════════════════════════════════════════════════════
// ④ 生产方式选择（Order 30）：argmax(e_猎(P), e_农(P))，w 已含于 e_农（无双扣）。
//    滞回：|e_猎 − e_农| < 0.02 → 保持当前方式（防来回跳）。
//    稳态论证：农业稳态 e=0.8 > 狩猎稳态 0.77 → 站稳不退农（docs §4.4）。
// ══════════════════════════════════════════════════════════════════
public sealed class ModeModel : CivModelBase
{
    public override string Name => "生产方式选择";
    public override int Order => 30;

    protected override void Apply(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            bool hasSeed = CapabilityTable.Has(ctx, e, CapabilityTable.Seed);
            if (!hasSeed) { e.IsFarming = false; continue; }
            // ⚠️ 2026-08-17 决策领地化：yH/yF 用 Σ 领地格潜在（与产出层同口径）——
            //   旧版单格判定导致"领地有良田但驻扎格差 → 永不转农"（科技地图"好几块地只有一处新石器"的根因）；
            //   2026-08-17 畜牧接入：草原牧场潜在并入 yH（草原游牧抬高狩猎收益 → 抑制转农，史实正确）
            float yH = e.CarryMult * (ctx.FHuntTerritory(e) + ctx.FHerdTerritory(e));   // 领地采集+牧场潜在 × 工具加成
            float yF = ctx.FFarmPotentialTerritory(e);                                   // 领地农业潜在（劳动因子=1，防小部落死锁）
            if (yF <= 0f) { e.IsFarming = false; continue; }
            float eH = CivSimContext.EHunt(yH, e.P);
            float eF = CivSimContext.EFarm(yF, e.P);
            float diff = eH - eF;   // e_猎 − e_农（农含 w 扣减）
            if (Mathf.Abs(diff) >= CivSimContext.Hysteresis)
                e.IsFarming = eF > eH;
            if (e.IsFarming && ctx.FirstFarmTick < 0)
                ctx.FirstFarmTick = ctx.Tick;   // 终止条件锚点
        }
    }
}
