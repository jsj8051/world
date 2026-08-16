// Responsibility: Invention (Order 40) - extracted from CivModels.cs verbatim (pure refactor).
using Godot;
using System;
using System.Collections.Generic;
using World.Biome;
using World.LogicGrid;

namespace World.CivSim;


// ══════════════════════════════════════════════════════════════════
// ⑤ 科技发明（Order 40）：通用 Kremer + 种子压力触发（Boserup）。
//    通用：λ = k·(P_部落/P_ref)·(1+知识/16)·env_i（依赖硬门槛 → 环境 → 随机）
//    种子：WildCrops 位 ✓ + P_格/K_格>0.7 + Soil≥3 + grinding → invProb=0.005（仅起源区）
// ══════════════════════════════════════════════════════════════════
public sealed class InventionModel : CivModelBase
{
    public override string Name => "科技发明";
    public override int Order => 40;

    public override void Execute(CivSimContext ctx)
    {
        CivEngine.RefreshCellState(ctx);   // 生产方式已更新（Order 30）→ 刷新 F_格 供压力判定

        for (int i = 0; i < ctx.Tribes.Count; i++)
        {
            var e = ctx.Tribes[i];
            if (e.Dead) continue;
            // ── 通用发明（Kremer）──
            foreach (var t in TechTable.All)
            {
                if (t.IsSeed || t.IsAgricultureConcept) continue;
                if (e.TechKeys.Contains(t.Key)) continue;
                if (!HasAll(e.TechKeys, t.Requires)) continue;
                float env = ctx.EnvFactor(e.Cell, t);
                if (env <= 0f) continue;
                float lambda = t.InvRate * (e.P / Mathf.Max(1f, t.PRef)) * (1f + TechTable.Knowledge(e.TechKeys) / 16f) * env;
                if (ctx.Rng.NextDouble() < lambda)
                    e.TechKeys.Add(t.Key);
            }
            // ── 种子（压力触发，Boserup 被逼出来的）──
            float pressure = ctx.CellF[e.Cell] > 0f ? ctx.CellPop[e.Cell] / ctx.CellF[e.Cell] : 0f;
            bool pressureOk = pressure > CivSimContext.SeedPressure;
            bool soilOk = ctx.Grid.SoilLevel[e.Cell] >= 3;
            bool grindOk = CapabilityTable.Has(ctx, e, "grinding");
            if (pressureOk && soilOk && grindOk)
            {
                byte wild = ctx.WildCrops[e.Cell];
                for (int s = 0; s < TechTable.SeedKeys.Length; s++)
                {
                    if ((wild & (1 << s)) == 0) continue;          // WildCrops 位（隐含气候+土壤）
                    if (e.TechKeys.Contains(TechTable.SeedKeys[s])) continue;
                    if (ctx.Rng.NextDouble() < CivSimContext.SeedInvProb)
                        e.TechKeys.Add(TechTable.SeedKeys[s]);
                }
            }
            TechTable.SyncAgriculture(e.TechKeys);
        }
    }

    private static bool HasAll(HashSet<string> keys, string[] req)
    {
        foreach (var r in req) if (!keys.Contains(r)) return false;
        return true;
    }
}
