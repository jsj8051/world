using Godot;
using System;
using System.Collections.Generic;
using World.LogicGrid;
using World.MapGen;

using World.CivSim.Entities;
using World.CivSim.Mechanics.Territory;
using World.CivSim.Mechanics.Politics;
using World.CivSim.Mechanics.State;
namespace World.CivSim;

/// <summary>
/// 文明演化引擎（v4 纯实体模型）。输入 GameGrid（自然层只读），输出演化结果（实体表 + 每格状态）。
/// 确定性：同 seed 同网格 → 同结果；注册表按 Order 每 tick 执行（docs/石器时代设计.md §二）。
///
/// 终止条件（用户拍板 2026-08-06）：
///   首个实体转农 tick + 100 ticks 结束；兜底 500 ticks 无农 → 停止（天然灭绝星球，诊断警告）。
/// </summary>
public static class CivEngine
{
    /// <summary>运行一次完整演化。onProgress：后台线程调用（0..1，tick 级；调用方保证线程安全）。</summary>
    public static CivSimResult Run(GameGrid grid, int seed, int originCount = 3, Action<float> onProgress = null)
    {
        TechTable.Load();
        int n = grid.N;
        var ctx = new CivSimContext
        {
            Grid = grid,
            CellPolities = new Polity[n],
            Polities = new List<Polity>(),
            Seed = seed,
            OriginCount = originCount,
            Rng = new DeterministicRandom(seed),   // 可序列化状态：读档续跑无分叉
            R = new float[n],
            CellF = new float[n],
            CellPop = new float[n],
            CellFarmPop = new float[n],
            BfsStamp = new int[n],
            BfsStampValue = 1,
            WildCrops = grid.EnsureWildCrops(),
            Suit = WildCropsSystem.Suitability(grid),
            // 影响力场模型（2026-08-10）
            CellOwner = EnumerableRepeat(-1, n),
            CellBestOwner = EnumerableRepeat(-1, n),
            CellBestInf = new float[n],
            CellOwnerInf = new float[n],
            LockedUntil = EnumerableRepeat(0, n),   // 实控锁定（v8 冲突机制；0=无锁定）
            Cultivation = new float[n],              // 开垦率场（2026-08-17 土地挂钩；0=未开垦）
        };
        ctx.TerritoryCells = new List<int>[4096];
        ctx.TerritoryDists = new List<byte>[4096];
        for (int i = 0; i < ctx.TerritoryCells.Length; i++)
        {
            ctx.TerritoryCells[i] = new List<int>();
            ctx.TerritoryDists[i] = new List<byte>();
        }
        for (int i = 0; i < n; i++)
            ctx.CellPolities[i] = null;
        BuildLayer1(ctx);   // 层1 空间生产力 R（Miami NPP × 水因子，k 相对标定 → 陆地中位数 0.3 人/km²）
        // ⚠️ 2026-08-17：砍存量再生——无 InitStock；开垦率场构造时已建（全 0，随农田增长）

        var registry = CivModelRegistry.StoneAge();
        int maxTicks = CivSimContext.MaxTicksNoAgri + CivSimContext.TerminateAfterAgri;
        var modelMs = new Dictionary<string, long>();   // ⚠️ 2026-08-17 逐模型计时（监督劣化定位）
        var swRun = System.Diagnostics.Stopwatch.StartNew();
        for (ctx.Tick = 0; ctx.Tick < maxTicks; ctx.Tick++)
        {
            RefreshCellState(ctx);
            foreach (var m in registry.SortedModels())
            {
                var swm = System.Diagnostics.Stopwatch.StartNew();
                m.Execute(ctx);
                swm.Stop();
                modelMs[m.Name] = modelMs.TryGetValue(m.Name, out var prev) ? prev + swm.ElapsedMilliseconds : swm.ElapsedMilliseconds;
            }
            onProgress?.Invoke(Mathf.Min((ctx.Tick + 1f) / maxTicks, 1f));

            // 终止：首转农 +100 ticks；无农 500 ticks 兜底（天然灭绝星球）
            if (ctx.FirstFarmTick >= 0 && ctx.Tick - ctx.FirstFarmTick >= CivSimContext.TerminateAfterAgri)
            { ctx.Tick++; break; }
            if (ctx.FirstFarmTick < 0 && ctx.Tick >= CivSimContext.MaxTicksNoAgri - 1)
            { ctx.Tick++; break; }
        }
        swRun.Stop();

        ctx.Polities.RemoveAll(e => e.Dead);
        // ⚠️ 2026-08-18 阶段3 方案 D：边界态统一重建（唯一入口，与读档/Continue 同式）——
        //   FLast/CellF/领地/酋邦/领袖标记全部从末态持久字段重算，Run 返回态自洽 → 读档续跑无分叉。
        SettleDerived(ctx);
        // ⚠️ 2026-08-17 监督机制：CivSim 逐模型耗时入历史（对比/告警；--arch 全量测试时也自动记录）
        modelMs["总"] = swRun.ElapsedMilliseconds;
        var (hisAvg, _, hisCnt) = World.Diagnostics.PerfLog.Stats("civsim", "总");
        World.Diagnostics.PerfLog.Append("civsim", $"{ctx.Tick}t/{ctx.Polities.Count}e", modelMs);
        if (hisCnt > 0)
        {
            // 调用方可能后台线程（CivEvolveMenu Task.Run）：LogService 纪律禁止，保持 GD.Print 直调（ADR-0004 §决策4）
            if (swRun.ElapsedMilliseconds > hisAvg * 1.5)
                GD.Print($"[性能] ⚠️ CivSim 劣化告警：总={swRun.ElapsedMilliseconds}ms > 历史均值 {hisAvg:F0}ms ×1.5——检查近期模型改动");
            else
                GD.Print($"[性能] CivSim 本次总={swRun.ElapsedMilliseconds}ms（历史均值 {hisAvg:F0}ms / {hisCnt} 次 → 正常）");
        }
        return new CivSimResult { Context = ctx, FinalTick = ctx.Tick };
    }

    /// <summary>读档续跑：从已恢复的 ctx 继续 extraTicks（IsFarming 入档 → 无滞回分叉）。</summary>
    public static CivSimResult Continue(CivSimContext ctx, int extraTicks, Action<float> onProgress = null)
    {
        var registry = CivModelRegistry.StoneAge();
        for (int k = 0; k < extraTicks; k++, ctx.Tick++)
        {
            RefreshCellState(ctx);
            registry.ExecuteAll(ctx);
            onProgress?.Invoke((k + 1f) / Mathf.Max(1, extraTicks));
        }
        ctx.Polities.RemoveAll(e => e.Dead);
        SettleDerived(ctx);   // ⚠️ 2026-08-18 阶段3：与 Run 结尾同式（边界态统一重建）
        return new CivSimResult { Context = ctx, FinalTick = ctx.Tick };
    }

    /// <summary>层1 空间生产力 R：R = k × min(NPP_T, NPP_P) × 水因子（Miami 模型 Lieth 1975）。
    /// k 相对标定：陆地 R 中位数 → TargetMedianDensity=0.1 人/km²（2026-08 史实标定：狩猎采集密度）。
    /// 读档复用（同 grid 同结果，确定性；不存档）。</summary>
    public static void BuildLayer1(CivSimContext ctx)
    {
        var grid = ctx.Grid;
        int n = grid.N;
        var vals = new List<float>(n);
        float rMax = 0f;
        for (int i = 0; i < n; i++)
        {
            if (!grid.IsLandCell(i)) { ctx.R[i] = 0f; continue; }
            if (grid.Temp[i] <= -5.5f) { ctx.R[i] = 0f; continue; }   // ⚠️ 2026-08-18 冰盖无生产力（温度 ≤-5.5°C——余量避 byte 量化边界——T04 读档/内存同判）
            float raw = CivSimContext.MiamiNpp(grid.Temp[i], grid.Precip[i]) * (ctx.WaterRich(i) ? 1.5f : 1f);
            ctx.R[i] = raw;
            if (raw > rMax) rMax = raw;
            vals.Add(raw);
        }
        float k = 0f;
        if (vals.Count > 0)
        {
            vals.Sort();
            float median = vals[vals.Count / 2];
            k = median > 1f ? CivSimContext.TargetMedianDensity / median : 0f;
        }
        for (int i = 0; i < n; i++) ctx.R[i] *= k;
        ctx.RMax = Mathf.Max(1e-6f, rMax * k);   // 殖民落点分数归一化参考（2026-08-19 扩散项）
    }

    /// <summary>每 tick 开头的派生刷新（现状语义，保持不动）：CellPolities/CellPop/CellFarmPop/CarryMult/CapMask
    /// + 商品存储年步进（副作用）+ CellF 聚合（FLast 为上 tick 值——Harvest 在本 tick 才更新）。
    /// ⚠️ 2026-08-18 阶段3 拆分：内部 = RefreshCellStateCore(纯) + AccumulateStorage(副作用) + RefreshCellStateF(纯)。
    /// 副作用（商品存储消耗/衰变）只在演化每 tick 调用，绝不在 SettleDerived 边界重算里调（防双倍累积）。</summary>
    public static void RefreshCellState(CivSimContext ctx)
    {
        RefreshCellStateCore(ctx);
        AccumulateStorage(ctx);
        RefreshCellStateF(ctx);
    }

    /// <summary>纯派生①：CellPolities（一格一实体单引用）+ CellPop/CellFarmPop + CarryMult/CapMask。
    /// 幂等（可任意次数调用）；供 SettleDerived 边界重算用。</summary>
    public static void RefreshCellStateCore(CivSimContext ctx)
    {
        int n = ctx.Grid.N;
        // ⚠️ 2026-08-17 审查修复 + 2026-08 阶段2 一格一实体：每 tick 按实体列表顺序重建 CellPolities（单引用）。
        //   一格一实体：每格至多一个部落；重建=清空为 null 再按实体列表顺序写入（确定性，防读档续跑分叉）。
        for (int i = 0; i < n; i++) ctx.CellPolities[i] = null;
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead || e.Cell < 0 || e.Cell >= n) continue;
            ctx.CellPolities[e.Cell] = e;
        }
        Array.Clear(ctx.CellPop, 0, n);
        Array.Clear(ctx.CellFarmPop, 0, n);
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            e.CarryMult = TechTable.HuntingCarry(e.TechKeys);
            e.CapMask = CapabilityTable.MaskOf(ctx, e);
            ctx.CellPop[e.Cell] += e.P;
            if (e.IsFarming) ctx.CellFarmPop[e.Cell] += e.P;
        }
    }

    /// <summary>副作用累积：商品存储 tick 步进（2026-08-18 阶段3 存储/衰变机制；2026-08-19 聚落双池改造）。
    /// **每 tick 仅一次**（演化循环内），绝不在 SettleDerived 边界重算调用（否则双倍消耗 → 分叉）。
    /// 双池（用户拍板"存粮迁移到聚落"）：
    ///   · **随身池**（Polity.Stocks，v12 字段语义改）：衰变用**基础年率**（携带即自然损耗——存储科技不保护
    ///     随身物）；Material 流入的**溢余**；容量 CarryFoodCap/CarryMatCap×P（游群即随身）。
    ///   · **粮仓**（Habitation.Stocks，v13）：衰变用 techMult（storage/pottery/settle/grinding 分层保藏——
    ///     谷物耐储核心）；Material 流入**优先入仓**；容量 SettleFoodCap/SettleMatCap×P×(1+0.5×TownTier)（2026-08-23 功能定性：城镇级系数）。
    ///   Food 流入/消耗由 GrowthModel 管（缺口吃随身→粮仓，耐储者留底）；本方法只做衰变+流入+容量。
    /// 单位 Stocks = 人当量（与 FLast/P 同量纲）。衰变按年率折算 tick：
    ///   decayTick = 1 − (1 − BaseDecay×techMult)^TickYears（年衰变史实锚点 → 100 年聚合）。</summary>
    public static void AccumulateStorage(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead || e.P <= 0f) continue;
            if (e.Stocks == null || e.Stocks.Length != CommodityTable.Count) e.Stocks = CommodityTable.NewStocks();
            var s = ctx.HabitationOf(e);   // 粮仓（定居部落）；null = 游群
            if (s != null && (s.Stocks == null || s.Stocks.Length != CommodityTable.Count)) s.Stocks = CommodityTable.NewStocks();
            bool hasStorage = CapabilityTable.Has(ctx, e, CapabilityTable.Storage);
            bool hasPottery = CapabilityTable.Has(ctx, e, CapabilityTable.Pottery);
            bool hasSettle = CapabilityTable.Has(ctx, e, CapabilityTable.Settle);
            bool hasGrind = CapabilityTable.Has(ctx, e, CapabilityTable.Grinding);
            float techMult = !hasStorage ? 1f : (!hasPottery ? 0.6f : (!hasSettle ? 0.3f : 0.15f));
            if (hasGrind) techMult *= 0.7f;   // 加工态（磨盘去壳/鞣制）提升耐久
            // ── 衰变（随身基础年率 / 粮仓 ×techMult）──
            for (int k = 0; k < e.Stocks.Length; k++)
            {
                var def = CommodityTable.All[k];
                float carryDecay = 1f - Mathf.Pow(1f - def.BaseDecay, CivSimContext.TickYears);
                e.Stocks[k] *= (1f - carryDecay);
                if (s != null)
                {
                    float granDecay = 1f - Mathf.Pow(1f - def.BaseDecay * techMult, CivSimContext.TickYears);
                    s.Stocks[k] *= (1f - granDecay);
                }
            }
            // ── Material 流入（副产囤积：粮仓优先 → 随身溢余）──
            for (int k = 0; k < e.Stocks.Length; k++)
            {
                var def = CommodityTable.All[k];
                if (def.Kind != CommodityKind.Material) continue;
                float inflow = def.Produce(e);
                if (inflow <= 0f) continue;
                if (s != null)
                {
                    float granCap = CivSimContext.SettleMatCap * (1f + CivSimContext.SettlementStoragePerLevel * s.TownTier) * e.P;   // 城镇级系数（2026-08-23 功能定性）
                    float toGran = Mathf.Min(inflow, Mathf.Max(0f, granCap - s.Stocks[k]));
                    s.Stocks[k] += toGran;
                    inflow -= toGran;
                }
                e.Stocks[k] += Mathf.Min(inflow, Mathf.Max(0f, CivSimContext.CarryMatCap * e.P - e.Stocks[k]));
            }
            // ── 容量兜底（随身/粮仓统一 clamp——贸易接收可能超限，下 tick 归位）──
            for (int k = 0; k < e.Stocks.Length; k++)
            {
                var def = CommodityTable.All[k];
                float carryCap = def.Kind == CommodityKind.Food ? CivSimContext.CarryFoodCap * e.P : CivSimContext.CarryMatCap * e.P;
                if (e.Stocks[k] > carryCap) e.Stocks[k] = carryCap;
                if (e.Stocks[k] < 0f) e.Stocks[k] = 0f;
                if (s != null)
                {
                    float granCap = (def.Kind == CommodityKind.Food ? CivSimContext.SettleFoodCap : CivSimContext.SettleMatCap)
                                  * (1f + CivSimContext.SettlementStoragePerLevel * s.TownTier) * e.P;   // 城镇级系数（2026-08-23 功能定性）
                    if (s.Stocks[k] > granCap) s.Stocks[k] = granCap;
                    if (s.Stocks[k] < 0f) s.Stocks[k] = 0f;
                }
            }
        }
    }

    /// <summary>纯派生②：CellF 聚合（CellF[cell] = Σ 该格实体 FLast）。幂等。</summary>
    public static void RefreshCellStateF(CivSimContext ctx)
    {
        Array.Clear(ctx.CellF, 0, ctx.Grid.N);
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            ctx.CellF[e.Cell] += e.FLast;
        }
    }

    private static int[] EnumerableRepeat(int v, int n)
    {
        var a = new int[n];
        Array.Fill(a, v);
        return a;
    }

    /// <summary>末态派生产出重算（2026-08-18 T04 修复）。FLast/FHunt/FHerd/FFarm 派生不入档——
    /// Run 结尾（及读档补偿）用末态持久字段重算，保证派生态自洽：读档续跑无分叉。
    /// 与 HarvestModel 同式：AllocateAndProduce(e)→FHunt，FLast=Σ分量。</summary>
    public static void RecomputeProduction(CivSimContext ctx)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            // ⚠️ 2026-08-18 T04 修复：先归零分量——AllocateAndProduce 在领地为空时提前 return 0
            //   （不走到分量赋值），若不归零则陈旧 FFarm/FHerd 残留（无领地却挂产出的活 bug）。
            e.FFarmLast = 0f; e.FHerdLast = 0f; e.FBerryLast = 0f;
            e.FHuntLast = ctx.AllocateAndProduce(e);
            e.FLast = e.FHuntLast + e.FFarmLast + e.FHerdLast;
        }
    }

    /// <summary>领袖标记纯派生（IsBigMan/IsChief 从持久字段 Prestige + ReligionShare 确定性重算）。
    /// 2026-08-18 阶段3：提为共享函数——演化 PrestigeModel（Order 25，写后立即算）与
    /// SettleDerived 边界重算（读档/Run 结尾）用同一公式 → 无两套实现分叉。</summary>
    public static void DeriveLeadership(Polity e)
    {
        e.IsBigMan = e.Prestige >= CivSimContext.BigManPrestigeThreshold;
        // 酋长：BigMan + 祖先宗教（谱系合法性——祖先份额 > 0；祖先=settle 派生，旧石器无）
        e.IsChief = e.IsBigMan && ShareField.RelFrac(e.ReligionShare, ReligionStage.Ancestor) > 0;
    }

    /// <summary>边界态派生统一重建（2026-08-18 阶段3 方案 D：唯一重算入口）。
    /// 调用点：读档后（CivMapArchive.Read）、Run 结尾（演化终止）、Continue 开头——
    /// 三条路径同一函数 → 消除"重算路径各写一套"缺陷（T04 类分叉根治）。
    /// 纯派生（幂等，不含 Goods 副作用累积——AccumulateGoods 只在演化每 tick 循环调）。
    /// 依赖序（勿乱改，逐行注释依赖）：
    ///   ① Core：CellPolities/CellPop/CellFarmPop/CarryMult/CapMask（供②影响力和④领地并查集）
    ///   ② RebuildInfluence：CellOwner/TerritoryCells/Dists（供③产出；粘性基准用持久 CellOwner）
    ///   ③ RecomputeProduction：FLast/FHunt/FHerd/FFarm/FBerry（供④酋邦 DomOutput；供⑤CellF）
    ///   ③b DeriveLeadership：IsBigMan/IsChief（供④酋邦凝聚条件）
    ///   ④ TerritoryModel.Rebuild：TerritoryId/Size（供⑤酋邦聚合）
    ///   ⑤ ChiefdomModel.Rebuild：ChiefdomId/Size（读 TerritoryId + F 分量 + IsChief/IsBigMan）
    ///   ⑤b StateAssign.Rebuild：StateId/StateSize（读 ChiefdomCells + 聚落 + Contributed——纯派生不存档）
    ///   ⑥ RefreshCellStateF：CellF 聚合（供读档续跑首 tick 的 Invention 压力门）
    /// </summary>
    public static void SettleDerived(CivSimContext ctx)
    {
        RefreshCellStateCore(ctx);                       // ①
        ctx.RebuildInfluence();                          // ②（内部含 RebuildTerritory）
        RecomputeProduction(ctx);                        // ③
        for (int i = 0; i < ctx.Polities.Count; i++)       // ③b
            if (!ctx.Polities[i].Dead) DeriveLeadership(ctx.Polities[i]);
        TerritoryModel.Rebuild(ctx);                     // ④
        ChiefdomModel.Rebuild(ctx);                      // ⑤
        StateAssign.Rebuild(ctx);                         // ⑤b 国家（读 ⑤ 的 ChiefdomCells——须在其后）
        RefreshCellStateF(ctx);                          // ⑥
    }
}

/// <summary>演化结果（输出载体）。</summary>
public sealed class CivSimResult
{
    public CivSimContext Context;
    public int FinalTick;
}
