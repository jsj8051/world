// Slice: CivSimDiag.OverHarvest.cs - T90 过采判据实测（2026-10-06 用户拍板：先证明 ResourceStock 有真实消费者，再引入状态）。
// 纯只读诊断：自驱 tick 演化 + 在 Harvest 边界采样重算每格产出。
// ★ 零改动 CivSim：不新增状态、不动等边际 water-filling、不碰存档格式、不消耗 Rng。
using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using World.Domain;
using World.CivSim;
using World.LogicGrid;
using World.Services;

using World.CivSim.Entities;
namespace World.Diagnostics;

public partial class CivSimDiag
{
    // ── 区域分组（互斥；优先级 河岸 > 湖泊 > 海岸 > 内陆）──
    private const int RegInland = 0, RegRiparian = 1, RegLake = 2, RegCoast = 3, RegCount = 4;
    private static readonly string[] RegNames = { "内陆", "河岸", "湖泊", "海岸" };
    private const int BioLow = 0, BioHigh = 1, BioCount = 2;
    private static readonly string[] BioNames = { "低生物量", "高生物量" };

    /// <summary>
    /// 分布（线性或对数分桶）。
    /// ⚠️ 教训（首版 bug）：Harvest/Regen 可达 ~50（农业把单格产出放大到 R 的数十倍），
    ///   首版把 ≥1 全部塞进"溢出桶"⇒ P50 被 Quantile 返回 Max（P50=P95=P99=max），分布信息全丢。
    ///   ⇒ 比值可能跨越数量级的必须用对数桶。
    /// </summary>
    private sealed class Dist
    {
        private readonly int _bins;
        private readonly float _lo, _hi;
        private readonly bool _log;
        private readonly int[] _h;
        private readonly float _invSpan;

        public Dist(float lo, float hi, bool log = false, int bins = 200)
        {
            _lo = lo; _hi = hi; _log = log; _bins = bins;
            _h = new int[bins + 1];
            _invSpan = log ? 1f / Mathf.Log(hi / lo) : 1f / (hi - lo);
        }

        public int Count;
        public double Sum;
        public float Min = float.MaxValue, Max;
        public int Ge095, Ge100;

        public void Add(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return;
            Count++; Sum += v;
            if (v < Min) Min = v;
            if (v > Max) Max = v;
            if (v >= 0.95f) Ge095++;
            if (v >= 1.0f) Ge100++;
            _h[Bucket(v)]++;
        }

        private int Bucket(float v)
        {
            if (v <= _lo) return 0;
            if (v >= _hi) return _bins;
            float t = _log ? Mathf.Log(v / _lo) * _invSpan : (v - _lo) * _invSpan;
            int b = (int)(t * _bins);
            return b < 0 ? 0 : b >= _bins ? _bins : b;
        }

        /// <summary>分位数 = 桶上界（溢出桶返回 Max）。</summary>
        public float Quantile(double q)
        {
            if (Count == 0) return 0f;
            int need = (int)Math.Ceiling(q * Count);
            int acc = 0;
            for (int i = 0; i <= _bins; i++)
            {
                acc += _h[i];
                if (acc < need) continue;
                if (i >= _bins) return Max;
                float t = (i + 1f) / _bins;
                return _log ? _lo * Mathf.Exp(t * Mathf.Log(_hi / _lo)) : _lo + t * (_hi - _lo);
            }
            return Max;
        }

        public string Fmt(string tag) => Count == 0
            ? $"{tag}: n=0"
            : $"{tag}: n={Count} P50={Quantile(0.50):F3} P95={Quantile(0.95):F3} P99={Quantile(0.99):F3} P99.9={Quantile(0.999):F3} max={Max:F3} 均值={Sum / Count:F3} ≥0.95={Ge095}";
    }

    /// <summary>采样累加器（一格一票）。</summary>
    private sealed class Tally
    {
        // Sat：自然区间 [0,1]，线性桶
        public readonly Dist SatAll = new(0f, 1f);
        // Regen / Pop：可跨数量级（农业放大 / 超载实体），对数桶
        public readonly Dist RegAll = new(0.01f, 100f, true);
        public readonly Dist RegForageAll = new(0.01f, 100f, true);   // 仅无开垦格（隔离农业放大）
        public readonly Dist PopAll = new(0.01f, 100f, true);
        public readonly Dist[] SatReg = N(RegCount), RegReg = N(RegCount), PopReg = N(RegCount);
        public readonly Dist[] SatBio = N(BioCount), RegBio = N(BioCount), PopBio = N(BioCount);
        public int Samples;
        public double MaxRelErr;
        public float MaxSatP999;
        private static Dist[] N(int k)
        {
            var a = new Dist[k];
            for (int i = 0; i < k; i++) a[i] = new Dist(0f, 1f);
            return a;
        }
    }

    /// <summary>单次采样结果。</summary>
    private struct Samp
    {
        public double Harvest, Carry, SPhys, UnownedPotential, CultSum, ForageLoss, MaxRelErr;
        public int OwnedCells, UnownedCells, FarmCells;
        public float SatP999;
        public readonly int[] RegLand;   // 全体可居陆地格按区域计数（含无主；区分"没有该区域"与"该区域无人占"）
        public readonly int[] RegOwned;  // 已归属格按区域计数
        public Samp(int regs) { RegLand = new int[regs]; RegOwned = new int[regs]; }
    }

    /// <summary>
    /// T90 过采判据实测。
    /// 三个分布：Harvest/Carry（模型内部饱和度）、Harvest/Regen、Population/Carry；
    /// Regen = R·A（该格生态系统可持续产出，人当量/年）——静态丰度模型下唯一自洽的再生代理。
    ///
    /// ⚠️ 结构事实（本测试的第一结论，先于任何数值）：
    ///   Harvest ≤ Carry 恒成立（凹化 F_i = p·n/(LF·p+n) 单格产出不可能超过其潜在）
    ///   ⇒「取用超过潜在」在现有模型里**不可表达**。故真正有信息量的是：
    ///   ① 未归属可居陆地占比（空间前沿是否还开着）② 开垦的不可逆采集损失 ③ 饱和度分布形状。
    ///
    /// ⚠️ 采样点必须在 **Harvest 边界**（Order ≤ 9 跑完、Order > 9 之前）：
    ///   Order 45 领地重算 / Order 80 分裂迁移会改变领地与实体集合——
    ///   在 tick 末尾采样会与 HarvestModel 当 tick 的输入不一致（首版实测相对误差 14.2%，
    ///   正是"用 tick 末尾状态重算 tick 中产出"造成的）。
    /// </summary>
    private void T90_OverHarvest(int seed, int origins)
    {
        const int HarvestOrder = 9;      // HarvestModel.Order
        const int SampleEvery = 5;
        int n = _grid.N;
        float A = _grid.CellAreaKm2;

        // ── 自校验①：自驱循环 ≡ CivEngine.Run（同 seed 两次推进必须同态）──
        var refRun = CivEngine.Run(_grid, seed, origins);
        int refTick = refRun.FinalTick, refEnt = refRun.Context.Polities.Count;
        float refPop = refRun.Context.TotalPopulation();

        var ctx = MakeCtx(_grid, seed, origins);
        int landCells = 0, riparianAll = 0, lakeAll = 0;
        for (int c = 0; c < n; c++)
        {
            if (ctx.R[c] > 0f) landCells++;
            if (ctx.Grid.Biome[c] == (byte)BiomeType.Riparian) riparianAll++;
            if (ctx.Grid.LakeLevel[c] > 0) lakeAll++;
        }
        float rMed = LandRMedian(ctx);
        LogService.Log("T90", $"── 过采判据实测 ── n={n} seed={seed} 起源={origins} 可居陆地格={landCells} 格面积={A:F2}km² 陆地R中位={rMed:F3} 人/km²");
        LogService.Log("T90", $"[地图水域口径] Riparian biome 格={riparianAll} LakeLevel>0 格={lakeAll}（WaterRich 只认 Riparian/Lake ⇒ Riparian=0 时河流不给灌溉加成）");

        var models = CivModelRegistry.StoneAge().SortedModels();
        var harvest = new float[n];
        var carry = new float[n];
        var popAt = new float[n];
        var pool = new Tally();     // 全演化采样池（每 SampleEvery tick）
        var last = new Tally();     // 末态单 tick（干净样本）
        var series = new List<string>();
        var lakeNb = new Dictionary<int, bool>();
        int maxTicks = CivSimContext.MaxTicksNoAgri + CivSimContext.TerminateAfterAgri;
        Samp fin = new Samp(RegCount);

        while (ctx.Tick < maxTicks)
        {
            int t = ctx.Tick;
            // ── 一段 tick：前半（Order ≤ 9：开垦6 → 影响力8 → 采集收获9）──
            CivEngine.RefreshCellState(ctx);
            for (int m = 0; m < models.Count; m++)
                if (models[m].Order <= HarvestOrder) models[m].Execute(ctx);

            // 廉价每 tick 聚合（直读实体缓存）
            int bands = 0; float pop = 0; double sumF = 0;
            for (int i = 0; i < ctx.Polities.Count; i++)
            {
                var e = ctx.Polities[i];
                if (e.Dead) continue;
                bands++; pop += e.P; sumF += e.FLast;
            }
            if (t % 25 == 0)
                series.Add($"t={t}({t * CivSimContext.TickYears / 1000}k年) 实体={bands} 人口={pop:F0} ΣF={sumF:F0}");
            if (t % SampleEvery == 0)
            {
                var s = Sample(ctx, n, A, rMed, harvest, carry, popAt, pool, lakeNb);
                pool.Samples++;
                if (s.SatP999 > pool.MaxSatP999) pool.MaxSatP999 = s.SatP999;
            }

            // ── 后半（Order > 9）──
            for (int m = 0; m < models.Count; m++)
                if (models[m].Order > HarvestOrder) models[m].Execute(ctx);
            ctx.Tick++;

            // 终止条件（镜像 CivEngine.Run：首转农 +100 / 无农 500 兜底）
            // ⚠️ 首版 bug：此处再 ctx.Tick++ = 双重自增（上面已自增一次）→ 自驱比引擎多 1 tick（T90a FAIL）。
            //   Run 的语义是 for 的 Tick++ 被 break 跳过、仅 if 内自增一次；等价形态即"循环尾部自增、if 不再自增"。
            if (ctx.FirstFarmTick >= 0 && t - ctx.FirstFarmTick >= CivSimContext.TerminateAfterAgri) break;
            if (ctx.FirstFarmTick < 0 && t >= CivSimContext.MaxTicksNoAgri - 1) break;
        }

        // ── 末态对齐（与 CivEngine.Run 收尾同式）+ 干净的单 tick 采样 ──
        ctx.Polities.RemoveAll(e => e.Dead);
        CivEngine.SettleDerived(ctx);   // 重建派生场（含 FLast）→ 末态自洽
        fin = Sample(ctx, n, A, rMed, harvest, carry, popAt, last, lakeNb);
        last.Samples = 1;
        if (fin.SatP999 > pool.MaxSatP999) pool.MaxSatP999 = fin.SatP999;

        // ── 自校验①：自驱循环 ≡ CivEngine.Run ──
        bool harnessOk = ctx.Tick == refTick && ctx.Polities.Count == refEnt
                         && Mathf.Abs(ctx.TotalPopulation() - refPop) <= Mathf.Max(1e-3f, refPop * 1e-5f);
        Check("T90a 自驱循环 ≡ CivEngine.Run（同 seed 同态）", harnessOk,
            $"自驱 tick={ctx.Tick} 实体{ctx.Polities.Count} 人口{ctx.TotalPopulation():F1} vs 引擎 tick={refTick} 实体{refEnt} 人口{refPop:F1}");

        // ── 自校验②：逐格重算 ΣF ≡ 实体缓存 ΣFLast（测量有效性）──
        double maxErr = Math.Max(pool.MaxRelErr, last.MaxRelErr);
        Check("T90b 逐格重算 ≡ 实体 FLast（相对误差 < 1e-3）", maxErr < 1e-3,
            $"池采样={pool.Samples} 末态=1 max相对误差={maxErr:E2}");

        // ── 报告 ──
        double unownedPct = landCells > 0 ? 100.0 * fin.UnownedCells / landCells : 0;
        LogService.Log("T90", "[结构事实] Harvest ≤ Carry 恒成立（凹化 + 潜在封顶）⇒「取用超过潜在」在现有模型里不可表达");
        LogService.Log("T90", $"[占用] 末态 归属格={fin.OwnedCells} 未归属可居陆地={fin.UnownedCells}/{landCells}（{unownedPct:F1}%）| 有开垦格={fin.FarmCells}");
        double ownedSPhys = fin.SPhys - fin.UnownedPotential;
        LogService.Log("T90", $"[潜力] 生态Σ(R·A)={fin.SPhys:F0} | 归属格Σ(R·A)={ownedSPhys:F0}（占 {R(ownedSPhys, fin.SPhys):P1}）未归属Σ(R·A)={fin.UnownedPotential:F0} | 已利用ΣCarry={fin.Carry:F0} 实收ΣHarvest={fin.Harvest:F0}");
        LogService.Log("T90", $"[总量] ΣHarvest/ΣCarry={R(fin.Harvest, fin.Carry):F3} ΣHarvest/ΣRegen(R·A)={R(fin.Harvest, fin.SPhys):F3} ΣPop/ΣCarry={R(ctx.TotalPopulation(), fin.Carry):F3}");
        LogService.Log("T90", $"[开垦] Σ开垦率={fin.CultSum:F1} 均值={(fin.OwnedCells > 0 ? fin.CultSum / fin.OwnedCells : 0):F4} 采集+牧场潜在不可逆损失={fin.ForageLoss:F0}（占已利用潜在 {R(fin.ForageLoss, fin.Carry + fin.ForageLoss):P1}）");

        var regLine = new StringBuilder();
        for (int r = 0; r < RegCount; r++)
            regLine.Append($"{RegNames[r]}: 陆地{fin.RegLand[r]}格 归属{fin.RegOwned[r]}格({(fin.RegLand[r] > 0 ? 100.0 * fin.RegOwned[r] / fin.RegLand[r] : 0):F0}%) ");
        LogService.Log("T90", $"[区域占用] {regLine}（n=0 的分布 = 该区域无归属格，非无该区域）");

        LogService.Log("T90", "── 分布 A：全演化采样池（每 5 tick，格一票）──");
        LogService.Log("T90", pool.SatAll.Fmt("  Harvest/Carry     "));
        LogService.Log("T90", pool.RegAll.Fmt("  Harvest/Regen     "));
        LogService.Log("T90", pool.RegForageAll.Fmt("  Harvest/Regen(无农田格)"));
        LogService.Log("T90", pool.PopAll.Fmt("  Population/Carry  "));
        LogService.Log("T90", "── 分布 B：末态单 tick（干净样本）──");
        LogService.Log("T90", last.SatAll.Fmt("  Harvest/Carry     "));
        LogService.Log("T90", last.RegAll.Fmt("  Harvest/Regen     "));
        LogService.Log("T90", last.RegForageAll.Fmt("  Harvest/Regen(无农田格)"));
        LogService.Log("T90", last.PopAll.Fmt("  Population/Carry  "));
        for (int r = 0; r < RegCount; r++)
        {
            LogService.Log("T90", last.SatReg[r].Fmt($"  区域[{RegNames[r]}] Harvest/Carry  "));
            LogService.Log("T90", last.RegReg[r].Fmt($"  区域[{RegNames[r]}] Harvest/Regen  "));
            LogService.Log("T90", last.PopReg[r].Fmt($"  区域[{RegNames[r]}] Pop/Carry      "));
        }
        for (int b = 0; b < BioCount; b++)
        {
            LogService.Log("T90", last.SatBio[b].Fmt($"  生物量[{BioNames[b]}] Harvest/Carry  "));
            LogService.Log("T90", last.RegBio[b].Fmt($"  生物量[{BioNames[b]}] Harvest/Regen  "));
            LogService.Log("T90", last.PopBio[b].Fmt($"  生物量[{BioNames[b]}] Pop/Carry      "));
        }
        LogService.Log("T90", $"[时间序列 每25tick 1tick=100年] {string.Join(" | ", series)}");

        // ── A/B/C 判据 ──
        // ⚠️ 判据不能建立在 Harvest/Carry 或 P/Carry 的"接近 1"上——那是凹化固定点的**结构产物**
        //   （F 凹且饱和于 Carry ⇒ 均衡 P* = F(P*) 必然落在 0.8~0.95·Carry，与稀缺无关）。
        //   决定性判据 = 未归属可居陆地占比（空间前沿）+ 开垦不可逆损失占比。
        double nearCapPct = fin.OwnedCells > 0 ? 100.0 * last.SatAll.Ge095 / fin.OwnedCells : 0;
        double lossPct = R(fin.ForageLoss, fin.Carry + fin.ForageLoss);
        string tier, why;
        if (unownedPct > 50.0)
        { tier = "A"; why = "过半可居陆地无人利用 + 模型结构上不可表达过采"; }
        else if (unownedPct > 20.0)
        { tier = "B"; why = "前沿收缩中，局部接近领地容量上限 ⇒ 潜在约束，继续观测"; }
        else
        { tier = "C"; why = "可居陆地近饱和 ⇒ 承载力约束真实，值得设计最小账本"; }
        LogService.Log("T90", $"[判据] 未归属可居陆地={unownedPct:F1}% 开垦不可逆损失={lossPct:P1} " +
                             $"接近领地容量上限格(≥0.95)={last.SatAll.Ge095}/{fin.OwnedCells}（{nearCapPct:F1}%）" +
                             $" 全程 P99.9 峰值={pool.MaxSatP999:F3} ⇒ 档位 {tier}（{why}）");
        LogService.Log("T90", "[读法提示] Harvest/Carry 与 Pop/Carry 的'接近 1'是凹化固定点 P*=F(P*) 的结构产物（F 饱和于 Carry），" +
                             "不构成稀缺证据；Harvest/Regen>1 在无农田格上恒 ≤ max(ProductionWeight)=3.0 ⇒ 值是领地距离权重约定，非生物量主张。");
        LogService.Log("T90", tier == "A"
            ? "[结论] ResourceStock 无真实消费者 ⇒ 维持冻结；下一步做 FishPotential（能力补全，零状态）"
            : "[结论] 先继续观测 + 补 FishPotential，再决定是否开 D[c] 最小账本（只加账不动 water-filling）");
    }

    private static double R(double a, double b) => b > 0 ? a / b : 0;

    /// <summary>可居陆地格 R 中位数（高/低生物量分界；确定性）。</summary>
    private static float LandRMedian(CivSimContext ctx)
    {
        var list = new List<float>();
        for (int c = 0; c < ctx.R.Length; c++)
            if (ctx.R[c] > 0f) list.Add(ctx.R[c]);
        if (list.Count == 0) return 0f;
        list.Sort();
        return list[list.Count / 2];
    }

    /// <summary>一次采样：重算每格产出并填入分布。返回 Σ重算 / ΣFLast 的相对误差。</summary>
    private Samp Sample(CivSimContext ctx, int n, float A, float rMed,
        float[] harvest, float[] carry, float[] popAt, Tally tal, Dictionary<int, bool> lakeNb)
    {
        Array.Clear(harvest, 0, n);
        Array.Clear(carry, 0, n);
        Array.Clear(popAt, 0, n);
        var s = new Samp(RegCount);
        lakeNb.Clear();

        double sumRecomputed = 0, sumFLast = 0;
        // ── ① 按 band 重算每格产出（镜像 CivSimContext.AllocateAndProduce，只读）──
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            sumFLast += e.FLast;
            var terr = ctx.TerritoryOf(e);
            var dists = ctx.TerritoryDistsOf(e);
            if (terr == null || terr.Count == 0) continue;
            bool canHerd = CapabilityTable.Has(ctx, e, CapabilityTable.Livestock);
            byte[] wild = canHerd ? ctx.Grid.EnsureWildLivestock() : null;
            bool isFarm = e.IsFarming;

            float sumPc = 0f, sumPh = 0f, sumPf = 0f;
            for (int k = 0; k < terr.Count; k++)
            {
                int c = terr[k];
                if (ctx.R[c] <= 0f) continue;
                float w = CivSimContext.ProductionWeight(dists[k]);
                if (w <= 0f) continue;
                float cult = ctx.Cultivation != null ? ctx.Cultivation[c] : 0f;
                ctx.ForageSharesAt(c, out float prey, out float berry, out float fish);
                float pc = ctx.R[c] * A * w * ((1f - CivSimContext.PreyHabitatLoss * cult) * prey + (1f - cult) * berry + fish);
                if (pc > 0f) sumPc += pc;
                if (canHerd && wild != null && wild[c] != 0) sumPh += ctx.R[c] * CivSimContext.HerdMult * A * w * (1f - cult);
                if (isFarm && cult > 0f) sumPf += FarmPotential(ctx, e, c, A, w, cult);
            }
            float sumCollect = sumPc + sumPh;
            float total = sumCollect + sumPf;
            if (total <= 0f) continue;

            float N = e.P;
            float sqrtMu;
            float sqrtMuA = Mathf.Sqrt(CivSimContext.LaborFrac) * sumCollect / (N + CivSimContext.LaborFrac * sumCollect);
            if (sqrtMuA >= Mathf.Sqrt(1f / CivSimContext.LaborFracFarm))
                sqrtMu = sqrtMuA;
            else
                sqrtMu = (Mathf.Sqrt(CivSimContext.LaborFrac) * sumCollect + Mathf.Sqrt(CivSimContext.LaborFracFarm) * sumPf)
                       / (N + CivSimContext.LaborFrac * sumCollect + CivSimContext.LaborFracFarm * sumPf);
            if (sqrtMu <= 0f) continue;

            float sumCarryBand = 0f;
            for (int k = 0; k < terr.Count; k++)
            {
                int c = terr[k];
                if (ctx.R[c] <= 0f) continue;
                float w = CivSimContext.ProductionWeight(dists[k]);
                if (w <= 0f) continue;
                float cult = ctx.Cultivation != null ? ctx.Cultivation[c] : 0f;
                ctx.ForageSharesAt(c, out float prey, out float berry, out float fish);
                float pc = ctx.R[c] * A * w * ((1f - CivSimContext.PreyHabitatLoss * cult) * prey + (1f - cult) * berry + fish);
                float f = 0f, cCarry = 0f;
                if (pc > 0f)
                {
                    float nn = Mathf.Max(0f, Mathf.Sqrt(CivSimContext.LaborFrac) * pc / sqrtMu - CivSimContext.LaborFrac * pc);
                    f += pc * nn / (CivSimContext.LaborFrac * pc + nn);
                    cCarry += pc;
                }
                if (canHerd && wild != null && wild[c] != 0)
                {
                    float ph = ctx.R[c] * CivSimContext.HerdMult * A * w * (1f - cult);
                    if (ph > 0f)
                    {
                        float nn = Mathf.Max(0f, Mathf.Sqrt(CivSimContext.LaborFrac) * ph / sqrtMu - CivSimContext.LaborFrac * ph);
                        f += ph * nn / (CivSimContext.LaborFrac * ph + nn);
                        cCarry += ph;
                    }
                }
                if (isFarm && cult > 0f)
                {
                    float pf = FarmPotential(ctx, e, c, A, w, cult);
                    if (pf > 0f)
                    {
                        float nn = Mathf.Max(0f, Mathf.Sqrt(CivSimContext.LaborFracFarm) * pf / sqrtMu - CivSimContext.LaborFracFarm * pf);
                        f += pf * nn / (CivSimContext.LaborFracFarm * pf + nn);
                        cCarry += pf;
                    }
                }
                harvest[c] = f;
                carry[c] = cCarry;
                sumCarryBand += cCarry;
                // 开垦的不可逆损失：采集 R·A·w·cult·(1−0.5·猎物−水产) + 牧场 R·HerdMult·A·w·cult
                //   （2026-10-06 ① FishPotential：水产对开垦免疫 ⇒ 不计入损失）
                s.CultSum += cult;
                s.ForageLoss += ctx.R[c] * A * w * cult * (1f - 0.5f * prey - fish);
                if (canHerd && wild != null && wild[c] != 0)
                    s.ForageLoss += ctx.R[c] * CivSimContext.HerdMult * A * w * cult;
                sumRecomputed += f;
            }
            if (sumCarryBand > 0f)
                for (int k = 0; k < terr.Count; k++)
                {
                    int c = terr[k];
                    if (carry[c] > 0f) popAt[c] += e.P * carry[c] / sumCarryBand;
                }
        }

        // ── ② 逐格统计（可居陆地格；按区域 + 生物量分组）──
        for (int c = 0; c < n; c++)
        {
            if (ctx.R[c] <= 0f) continue;
            float sp = ctx.R[c] * A;
            s.SPhys += sp;
            int reg = RegInland;
            if (ctx.Grid.Biome[c] == (byte)BiomeType.Riparian) reg = RegRiparian;
            else if (ctx.Grid.LakeLevel[c] > 0 || HasLakeNeighbor(ctx, c, lakeNb)) reg = RegLake;
            else if (ctx.Grid.IsCoast(c)) reg = RegCoast;
            s.RegLand[reg]++;
            if (carry[c] <= 0f || ctx.CellOwner == null || ctx.CellOwner[c] < 0)
            {
                s.UnownedCells++;
                s.UnownedPotential += sp;
                continue;
            }
            float h = harvest[c];
            float cu = carry[c];
            s.OwnedCells++;
            s.RegOwned[reg]++;
            s.Harvest += h;
            s.Carry += cu;
            float cult = ctx.Cultivation != null ? ctx.Cultivation[c] : 0f;
            if (cult > 0f) s.FarmCells++;

            float sat = h / cu;
            float rreg = sp > 0f ? h / sp : 0f;
            float pr = popAt[c] / cu;
            int bio = ctx.R[c] >= rMed ? BioHigh : BioLow;

            tal.SatAll.Add(sat); tal.RegAll.Add(rreg); tal.PopAll.Add(pr);
            if (cult <= 0f) tal.RegForageAll.Add(rreg);
            tal.SatReg[reg].Add(sat); tal.RegReg[reg].Add(rreg); tal.PopReg[reg].Add(pr);
            tal.SatBio[bio].Add(sat); tal.RegBio[bio].Add(rreg); tal.PopBio[bio].Add(pr);
        }

        s.SatP999 = tal.SatAll.Quantile(0.999);
        s.MaxRelErr = sumFLast > 0 ? Math.Abs(sumRecomputed - sumFLast) / sumFLast : 0;
        if (s.MaxRelErr > tal.MaxRelErr) tal.MaxRelErr = s.MaxRelErr;
        return s;
    }

    /// <summary>单格农业潜在（镜像 AllocateAndProduce 的农田项）。</summary>
    private static float FarmPotential(CivSimContext ctx, Polity e, int c, float A, float w, float cult)
    {
        float rAgri = ctx.R[c] * ctx.IrrigFactor(c) * CivSimContext.AlluvFactor(ctx.Grid.SoilLevel[c]);
        if (rAgri <= 0f) return 0f;
        float best = 0f;
        foreach (var sk in TechTable.SeedKeys)
        {
            if (!e.TechKeys.Contains(sk)) continue;
            var def = TechTable.Get(sk);
            if (def == null) continue;
            best = Mathf.Max(best, def.AgriBase * ctx.Phi(c, def.SeedIndex));
        }
        return best > 0f ? best * rAgri * A * cult * w : 0f;
    }

    private static bool HasLakeNeighbor(CivSimContext ctx, int c, Dictionary<int, bool> cache)
    {
        if (cache.TryGetValue(c, out bool v)) return v;
        v = false;
        foreach (int nb in ctx.Grid.Neighbors[c])
            if (ctx.Grid.LakeLevel[nb] > 0) { v = true; break; }
        cache[c] = v;
        return v;
    }
}
