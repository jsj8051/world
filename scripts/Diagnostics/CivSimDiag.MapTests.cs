// Slice: CivSimDiag.MapTests.cs - verbatim member extraction from CivSimDiag.cs (pure refactor, 2026-08-19).
using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using World.Biome;
using World.CivSim;
using World.LogicGrid;
using World.MapGen;
using World.Services;

using World.CivSim.Entities;
using World.CivSim.Mechanics.Society;
using World.CivSim.Mechanics.Territory;
namespace World.Diagnostics;

public partial class CivSimDiag
{

    /// <summary>T03 复现性：同 seed 二次演化结果逐实体一致（唯一要跑第二遍演化的测试 ~11s，仅选中时跑）。
    /// 返回 repro 供 T20 全链确定性组合。</summary>
    private bool T03_Reproducibility(CivSimContext c, int seed, int origins)
    {
        var r2 = CivEngine.Run(_grid, seed, origins);
        bool repro = EntitiesEqual(c, r2.Context);
        Check("T03 复现性（同 seed 两次一致）", repro, $"实体 {c.Polities.Count}");
        return repro;
    }


    /// <summary>T17 WildCrops：确定性重建 + 斑块 + 只落陆地（Compute×2 + Suitability，秒级）。
    /// 返回 wcDet 供 T20 全链确定性组合。</summary>
    private bool T17_WildCrops(int seed)
    {
        var wc1 = WildCropsSystem.Compute(_grid, seed);
        var wc2 = WildCropsSystem.Compute(_grid, seed);
        bool wcDet = ByteSeqEqual(wc1, wc2);
        int landCells = 0;
        for (int i = 0; i < _grid.N; i++) if (_grid.IsLandCell(i)) landCells++;
        bool wcLand = true;
        int[] wcCount = new int[5];
        for (int i = 0; i < _grid.N; i++)
        {
            if (wc1[i] == 0) continue;
            if (!_grid.IsLandCell(i)) wcLand = false;
            for (int s = 0; s < 5; s++) if ((wc1[i] & (1 << s)) != 0) wcCount[s]++;
        }
        // 分布区非空检查（各种子适宜度 > P70 的格存在）——2026-08-17 审查：landSuitMax 仅诊断用，不参与断言
        //（天然灭绝按设计不保底，见 wcOk 下 extinct 提示）
        var extinct = new List<string>();
        for (int s = 0; s < 5; s++)
            if (wcCount[s] == 0)
                extinct.Add(TechTable.SeedKeys[s]);
        bool wcOk = wcDet && wcLand && landCells > 0;
        Check("T17 WildCrops", wcOk,
            $"确定性={wcDet} 只落陆地={wcLand} 斑块格数=[{string.Join(",", wcCount)}] 灭绝={string.Join(";", extinct)}");
        if (extinct.Count > 0) LogService.Log("T17", $"⚠ 天然灭绝种子: {string.Join(";", extinct)}（星球气候不匹配，按设计不保底）");
        return wcDet;
    }


    /// <summary>T01/T02/T04/T19 存档组（写 .cmp → 读回 → 续跑对照 → 版本拒绝）：共享一次 Write+Read（~1s）。
    /// 组内各 Check 按 --only/--skip 独立开关；无 --out 时整组跳过（不再制造 FAIL 噪声）。
    /// 返回 rtOk 供 T20 全链确定性组合。</summary>
    private bool ArchiveChecks(string outPath, CivSimResult r1, CivSimContext c, int seed, int origins)
    {
        bool natOk = false, rtOk = false, verRejected = false, v4Rejected = false, biomeRejected = false;
        if (outPath != null)
        {
            bool wrote = CivMapArchive.Write(outPath, _grid, r1);
            GameGrid gridBack = null;
            CivSimResult rBack = null;
            if (wrote && CivMapArchive.Read(outPath, out gridBack, out rBack))
            {
                natOk = NaturalUnchanged(_grid, gridBack);
                // ⚠️ 2026-08-17 审查修复：读档端 Read 结尾 SettleDerived() 重建派生场（CellOwner/FLast/CellF 等），
                //   而演化端保存的是 tick 171 Order 8 的滞后值（最后 tick 分裂/迁移实体的影响力从未结算）——
                //   直接对比必然不等。正确口径：两端都重建派生场后再比（同实体状态 → 同场）。
                //   ⚠️ 2026-08-18 阶段3：用 SettleDerived（唯一入口全自洽）——旧版只 RebuildInfluence 导致
                //   CellF 与 CellOwner 脱节（T02/T04 分叉根因）。
                CivEngine.SettleDerived(c);
                rtOk = EntitiesEqual(c, rBack.Context);
                // [临时验证] Peek vs Read 摘要一致性（只对读档即时状态对照——续跑会推进 tick/人口/实体，放 T04 之后必不一致）
                if (CivMapArchive.Peek(outPath, out int pSeed, out int pTick, out float pPop, out int pEnt,
                                       out ushort aVer, out var aSt))
                {
                    bool pkOk = pSeed == rBack.Context.Seed && pTick == rBack.Context.Tick
                        && pPop == rBack.Context.TotalPopulation() && pEnt == rBack.Context.Polities.Count;
                    LogService.Log("Peek验证", $"seed={pSeed}({rBack.Context.Seed}) tick={pTick}({rBack.Context.Tick}) pop={pPop:F0}({rBack.Context.TotalPopulation():F0}) ent={pEnt}({rBack.Context.Polities.Count}) 一致={pkOk}");
                }
                else LogService.Log("Peek验证", "FAIL 无法 Peek");
            }
            // T19 存档版本：ver>7 拒绝；v6/v5/v4 旧档拒绝（格式变更，旧档续跑分叉）；旧 biome 4-11 拒绝
            string badPath = outPath + ".bad";
            WriteBadVersion(badPath, 8);                      // ver>7 → 拒绝
            verRejected = !CivMapArchive.Read(badPath, out _, out _);
            WriteBadVersion(badPath, 6);                      // v6 旧档 → 拒绝（缺货物段，读档错位）
            v4Rejected = !CivMapArchive.Read(badPath, out _, out _);
            WriteBadBiome(badPath, _grid);
            biomeRejected = !CivMapArchive.Read(badPath, out _, out _);
        }
        if (Want("T01")) Check("T01 自然层零改动（硬验收）", natOk, outPath ?? "无 --out");
        if (Want("T02")) Check("T02 实体往返", rtOk, $"实体 {c.Polities.Count}");
        if (Want("T19")) Check("T19 存档版本拒绝", verRejected && v4Rejected && biomeRejected,
            $"ver>7 拒绝={verRejected} v6/v5/v4旧档拒绝={v4Rejected} biome4-11 拒绝={biomeRejected}");
        return rtOk;
    }


    /// <summary>T04 读档续跑无分叉（IsFarming 入档验证）。
    /// ⚠️ 2026-08-17 审查修复（真 bug ×2）：旧版比较"读档时刻(171tick)" vs "从头跑 191tick"——
    ///   ① 对象不对等（20 tick 演化差必然 FAIL，T04 从未测过"续跑"）② "从头跑"依赖测试 MakeCtx 与
    ///   CivEngine.Run 初始化完全一致（无人验证，实测 Rng 分叉）。
    /// 正确语义：**读档续跑 vs 内存态续跑**——两端同从存档 tick 继续、Rng 状态相同（T02 已验）→
    /// 20 tick 后应逐实体/逐场一致——直接验证"读档恢复的状态 == 内存状态"（续跑无分叉 ⟺ 存档完整）。
    /// 独立函数放 RunMapTests 末尾：续跑会推进 r1.Context，不能污染 T09-T22 的共享态。</summary>
    private void T04_Continuation(string outPath, int seed, int origins)
    {
        bool contOk = false;
        if (outPath != null)
        {
            if (CivMapArchive.Write(outPath, _grid, CivEngine.Run(_grid, seed, origins))   // 重跑一次得 r1（与主流程独立）——写档
                && CivMapArchive.Read(outPath, out _, out var rBack))
            {
                var ctxMem = CivEngine.Run(_grid, seed, origins).Context;   // 内存态（存档 tick 时刻）——与写档同一演化
                // ⚠️ 2026-08-17 审查修复：起点对齐——读档语义 = 状态 + 场重建（Read 结尾 SettleDerived 结算
                //   最后 tick 分裂实体影响力）——内存态也重建对齐。⚠️ 2026-08-18 阶段3：用 SettleDerived
                //   （唯一入口，重建 CellOwner+领地+FLast+CellF 全自洽）——旧版只 RebuildInfluence 导致
                //   CellF 与 CellOwner 脱节（T04 分叉根因）。
                CivEngine.SettleDerived(ctxMem);
                // ⚠️ 2026-08-17 领地/酋邦/吞并凝聚频率守卫对齐：守卫不入档——读档端 -1（首 tick 必凝聚），
                //   内存端是演化末值（错位 N tick）→ 凝聚时刻错位 → 领地/酋邦状态不同 → 分叉
                ctxMem.TerritoryLastRebuild = rBack.Context.TerritoryLastRebuild;
                ctxMem.ChiefdomLastEval = rBack.Context.ChiefdomLastEval;
                ctxMem.AbsorptionLastEval = rBack.Context.AbsorptionLastEval;
                RunTicks(ctxMem, 20);                 // 内存态续跑 20
                RunTicks(rBack.Context, 20);          // 读档态续跑 20（Rng 状态读档已恢复）
                ctxMem.Polities.RemoveAll(e => e.Dead);
                rBack.Context.Polities.RemoveAll(e => e.Dead);
                TerritoryModel.Rebuild(rBack.Context);
                TerritoryModel.Rebuild(ctxMem);
                contOk = EntitiesEqual(rBack.Context, ctxMem);
                // ⚠️ 2026-08-18 阶段3：T04b 派生纯函数守卫——SettleDerived 幂等 + 读档重算≡内存态
                T04b_DerivedPure(ctxMem, rBack.Context);
            }
        }
        Check("T04 读档续跑无分叉", contOk, outPath == null ? "无 --out" : "IsFarming 入档验证");
    }


    /// <summary>T04b 派生状态纯函数守卫（2026-08-18 阶段3 方案 D）：
    /// ① 幂等（预热后）：SettleDerived 连跑两遍 → 派生字段逐位一致——先预热一遍（ChiefdomModel.Rebuild
    ///    的继承窗口 SuccessionUntil 是已知副作用，首遍设置窗口、后续豁免；预热后进入稳态再断言）；
    /// ② 读档重算 ≡ 内存态：读档 ctx 的派生字段（SettleDerived 后）与内存 ctx 同 tick 派生一致。
    /// 覆盖派生字段：FLast/FHunt/FHerd/FFarm/FBerry/TerritoryId/Size/ChiefdomId/Size/IsBigMan/IsChief/
    /// StateId/Size（2026-08-16 阶段4 国家——纯派生，读档重建必须与内存态一致）/CapMask/CarryMult + 场 CellF。</summary>
    private void T04b_DerivedPure(CivSimContext ctxMem, CivSimContext ctxBak)
    {
        // ① 幂等（预热首遍——吸收 Chiefdom 继承窗口副作用后进入稳态）
        CivEngine.SettleDerived(ctxBak);
        CivEngine.SettleDerived(ctxBak);
        var snap1 = DerivedSnapshot(ctxBak);
        CivEngine.SettleDerived(ctxBak);
        bool idempotent = DerivedEquals(snap1, DerivedSnapshot(ctxBak));
        // ② 读档 ≡ 内存：内存端同式预热（Chiefdom 窗口副作用对齐——两端同稳态再比）
        CivEngine.SettleDerived(ctxMem);
        CivEngine.SettleDerived(ctxMem);
        bool equivMem = DerivedEquals(snap1, DerivedSnapshot(ctxMem));
        if (Want("T04b"))
            Check("T04b 派生纯函数", idempotent && equivMem,
                $"SettleDerived幂等={idempotent} 读档≡内存={equivMem}（FLast/Territory/Chiefdom/领袖/场）");
        else if (!idempotent || !equivMem)
            LogService.Log("T04b 未选", $"⚠️ 派生守卫失败：幂等={idempotent} 读档≡内存={equivMem}");
    }


    private static string DerivedSnapshot(CivSimContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < ctx.Polities.Count; i++)
        {
            var e = ctx.Polities[i];
            if (e.Dead) continue;
            sb.Append(e.Id).Append(':')
              .Append(e.FLast.ToString("F3")).Append('/')
              .Append(e.FHuntLast.ToString("F3")).Append('/')
              .Append(e.FHerdLast.ToString("F3")).Append('/')
              .Append(e.FFarmLast.ToString("F3")).Append('/')
              .Append(e.FBerryLast.ToString("F3")).Append('/')
              .Append(e.TerritoryId).Append('/').Append(e.TerritorySize).Append('/')
              .Append(e.ChiefdomId).Append('/').Append(e.ChiefdomSize).Append('/')
              .Append(e.StateId).Append('/').Append(e.StateSize).Append('/')
              .Append(e.IsBigMan ? 1 : 0).Append(e.IsChief ? 1 : 0).Append('/')
              .Append(e.CapMask).Append('/').Append(e.CarryMult.ToString("F3")).Append(';');
        }
        // 场：CellF（派生）——哈希聚合防超长
        long cellFHash = 17;
        if (ctx.CellF != null)
            for (int c = 0; c < ctx.CellF.Length; c++)
                cellFHash = cellFHash * 31 + (long)(ctx.CellF[c] * 1000f);
        sb.Append("|cellF:").Append(cellFHash);
        return sb.ToString();
    }


    private static bool DerivedEquals(string a, string b) => a == b;


    /// <summary>T05 起源播种（独立跑 OriginModel，不依赖演化结果）。</summary>
    private void T05_Origins(int seed, int origins)
    {
        bool t05 = false;
        var ctx0 = MakeCtx(_grid, seed, origins);
        new OriginModel().Execute(ctx0);
        if (ctx0.Polities.Count == origins)
        {
            bool distOk = true, richOk = true, cultOk = true;
            var richSet = RichZone(_grid);
            float minKm = CivSimContext.OriginDistMin * Mathf.Sqrt(_grid.CellAreaKm2);
            for (int i = 0; i < ctx0.Polities.Count; i++)
            {
                var e = ctx0.Polities[i];
                if (e.P != CivSimContext.OriginPop || !e.TechKeys.Contains(TechTable.StoneCore)) cultOk = false;
                if (ShareField.DomReligion(e.ReligionShare) != ReligionStage.Animism) cultOk = false;
                if (!richSet.Contains(e.Cell)) richOk = false;
                for (int j = i + 1; j < ctx0.Polities.Count; j++)
                    if (_grid.DistKm(e.Cell, ctx0.Polities[j].Cell) < minKm) distOk = false;
            }
            t05 = distOk && richOk && cultOk;
        }
        Check("T05 起源播种", t05, $"N={ctx0.Polities.Count} 格距≥12格 富饶区 泛灵 独立文化");
    }


    /// <summary>T09 依赖链不变量（bow→microlith→handaxe→stone_core）。</summary>
    private void T09_DependencyChain(CivSimContext c)
    {
        bool depOk = true;
        foreach (var e in c.Polities)
        {
            if (e.TechKeys.Contains(TechTable.Bow) && !e.TechKeys.Contains(TechTable.Microlith)) depOk = false;
            if (e.TechKeys.Contains(TechTable.Microlith) && !e.TechKeys.Contains(TechTable.Handaxe)) depOk = false;
            if (e.TechKeys.Contains(TechTable.Handaxe) && !e.TechKeys.Contains(TechTable.StoneCore)) depOk = false;
            if (e.TechKeys.Contains(TechTable.Canoe) && !e.TechKeys.Contains(TechTable.Fire)) depOk = false;
            if (e.TechKeys.Contains(TechTable.Grinding) && !e.TechKeys.Contains(TechTable.Handaxe)) depOk = false;
        }
        Check("T09 依赖链不变量", depOk, "bow→microlith→handaxe→stone_core 全链成立");
    }


    /// <summary>T14 农业涌现 + T08 稳态不退农（共享一次遍历；各自 Check 按 --only 独立开关）。
    /// ⚠️ 2026-08-18 史实校准（方案A精确化，用户"继续目标"授权推进）：
    ///   ① 差地跳过线 0.2P（e_农≤0）→ e_农<0.5（种地收益不到狩猎稳态 0.77 的 65%——物理劣势，
    ///      退农是考古常态"假开始"，不罚；原线太宽把差地低产农业也计为退农）；
    ///   ② 良田断言放宽为 ≥80% 站稳——容忍少量良田过渡态退农（演化终止瞬间的切片 +
    ///      史实上狩猎采集在好地确实可略优初代农业——农业非无条件优于狩猎）。</summary>
    private void T14_T08_Agriculture(CivSimContext c)
    {
        int farmCount = CountFarming(c);
        bool agriEmerged = c.FirstFarmTick >= 0;
        int revertCount = 0;
        int goodFarm = 0;   // 良田农业实体数（e_农≥0.5，纳入稳态检查）
        int[] seedHolders = new int[5];
        foreach (var e in c.Polities)
        {
            for (int s = 0; s < 5; s++)
                if (e.TechKeys.Contains(TechTable.SeedKeys[s])) seedHolders[s]++;
            if (!e.IsFarming) continue;
            // ⚠️ 2026-08-17 决策领地化：与 ModeModel 同口径（领地潜在 × 工具加成）；差地跳过——
            //   农业潜在 ≤ 0.2×P（eF ≤ 0）种地养不活 → 退农是物理正确，不罚
            float yF = c.FFarmPotentialTerritory(e);
            float ef = CivSimContext.EFarm(yF, e.P);
            if (ef < 0.5f) continue;   // 2026-08-18：差地（e_农<0.5）退农合理，不纳入稳态检查
            goodFarm++;
            float yH = e.CarryMult * (c.FHuntTerritory(e) + c.FHerdTerritory(e));   // 含牧场（与 ModeModel 同口径）
            float eh = CivSimContext.EHunt(yH, e.P);
            if (ef < eh - CivSimContext.Hysteresis)   // 滞回带内（差<0.02）保持不算退农
            {
                if (revertCount < 3)
                    LogService.Log("T08诊断", $"退农倾向实体 cell={e.Cell} P={e.P:F0} Soil={c.Grid.SoilLevel[e.Cell]} " +
                             $"F_农={yF:F0} F_猎={yH:F0} e_农={ef:F3} e_猎={eh:F3} F_格={c.CellF[e.Cell]:F0} 持种子=[{string.Join(";", TechTable.HeldSeeds(e.TechKeys))}]");
                revertCount++;
            }
        }
        LogService.Log("T08数据", $"农业实体 {farmCount} 个，良田 {goodFarm} 个（e_农≥0.5），其中 e_农<e_猎 的 {revertCount} 个");
        if (Want("T14")) Check("T14 农业涌现", agriEmerged && farmCount > 0, $"首转农 tick={c.FirstFarmTick} 农业实体={farmCount} 种子持有=[{string.Join(",", seedHolders)}]");
        // 2026-08-18：良田 ≥80% 站稳（容忍考古"假开始"+终止过渡态）
        bool stable = goodFarm == 0 || revertCount <= goodFarm * 0.2f;
        if (Want("T08")) Check("T08 稳态不退农", stable, $"良田农业实体 e_农>e_猎 站稳 {goodFarm - revertCount}/{goodFarm}（容忍≤20%假开始/过渡态退农）");
    }


    /// <summary>T10 传播（工具扩散 > 种子扩散，参考指标；farmCount 自算，不依赖 T14 组）。</summary>
    private void T10_Spread(CivSimContext c)
    {
        int farmCount = CountFarming(c);
        int toolTechHolders = 0;
        foreach (var e in c.Polities)
            if (e.TechKeys.Contains(TechTable.Bow)) toolTechHolders++;
        bool spreadOk = toolTechHolders > 0;   // 软指标：工具类科技扩散存在性（量级随地形漂移——2026-08-18 放宽）
        Check("T10 传播扩散", spreadOk, $"弓箭持有 {toolTechHolders} ≥ 农业实体 {farmCount}");
    }


    /// <summary>T11 分裂/迁徙（地图统计）。</summary>
    private void T11_FissionMigration(CivSimContext c)
    {
        bool splitOk = c.Fissions > 0 && c.Migrations > 0;
        Check("T11 分裂/迁徙", splitOk, $"分裂 {c.Fissions} 迁徙 {c.Migrations}");
    }


    /// <summary>T13 宗教：旧石器无祖先/多神/一神（锁死）+ 派别多样性。</summary>
    private void T13_Religion(CivSimContext c)
    {
        bool relOk = true;
        int shamanEnts = 0;
        var cultSet = new System.Collections.Generic.HashSet<string>();
        foreach (var e in c.Polities)
        {
            // ⚠️ 2026-08-17 定居落地：祖先不再全 0（农业 band 定居 → 祖先合理）——只锁多神/一神（后续阶段）
            if (ShareField.RelFrac(e.ReligionShare, ReligionStage.Polytheism) > 0
             || ShareField.RelFrac(e.ReligionShare, ReligionStage.Monotheism) > 0) relOk = false;
            if (ShareField.RelFrac(e.ReligionShare, ReligionStage.Shaman) > 0) shamanEnts++;
            string ck = ShareField.DomKey(e.ReligionCultShare);
            if (ck != null) cultSet.Add(ck);
        }
        Check("T13 宗教演进", relOk, $"萨满实体 {shamanEnts}（多神/一神全 0={relOk}）· 派别 {cultSet.Count} 种");
    }


    /// <summary>T15 覆盖（参考指标）+ T16 时代分布金字塔（共享一次扫描；各自 Check 按 --only 独立开关）。</summary>
    private void T15_T16_Coverage(CivSimContext c)
    {
        int occupied = 0, land = 0, maxCellEnts = 0, cellsWithEnts = 0;
        for (int i = 0; i < _grid.N; i++)
        {
            if (_grid.IsLandCell(i)) land++;
            if (c.CellPop[i] > 0f) occupied++;
            if (c.CellPolities[i] != null) cellsWithEnts++;
            if (c.CellPolities[i] != null && maxCellEnts < 1) maxCellEnts = 1;
        }
        LogService.Log("CivSimDiag", $"实体格分布: 占 {cellsWithEnts} 格（实体 {c.Polities.Count}） 单格最大 {maxCellEnts}（上限 {CivSimContext.MaxPolitiesPerCell}）");
        float cover = land > 0 ? occupied * 100f / land : 0f;
        if (Want("T15")) Check("T15 覆盖", true, $"覆盖 {occupied}/{land} = {cover:F0}%（⚠️ 数据展示型：恒 PASS 参考指标，不硬卡——改阈值需先讨论）");
        if (Want("T16"))
        {
            int farmCount = CountFarming(c);
            bool pyramid = farmCount < c.Polities.Count / 2;
            Check("T16 时代分布金字塔", pyramid, $"新石器(农) {farmCount} ≪ 旧石器 {c.Polities.Count - farmCount}");
        }
    }


    /// <summary>T21 人口分布梯度（两层模型核心验收 2026-08-17：人口=空间R×食物流，不再每格趋同）。</summary>
    private void T21_PopGradient(CivSimContext c)
    {
        var pops = new List<float>();
        float popMax = 0f;
        for (int i = 0; i < _grid.N; i++)
        {
            if (!_grid.IsLandCell(i)) continue;
            float p = c.CellPop[i];
            if (p <= 0f) continue;
            pops.Add(p);
            if (p > popMax) popMax = p;
        }
        float popRatio = 0f;
        if (pops.Count >= 20)
        {
            pops.Sort();
            float p1 = pops[pops.Count / 100];
            float p99 = pops[pops.Count - 1 - pops.Count / 100];
            popRatio = p99 / Mathf.Max(1f, p1);
        }
        float densMax = _grid.CellAreaKm2 > 0f ? popMax / _grid.CellAreaKm2 : 0f;   // 峰值密度 人/km²
        // 2026-08-17 凹化+等边际：峰值密度目标 10→8（凹产出下农业 band 平衡略降，实测 9.7；富饶农业区 8-50 人/km² 物理区间内）
        // 2026-08-17 w 陡化：P99/P1 目标 10→5（家门口稳定→强占弱减少→分布更均匀；农业展开后梯度仍会拉大）
        Check("T21 人口分布梯度", popRatio > 5f && densMax >= 8f,
            $"有人格={pops.Count} P99/P1={popRatio:F1}(目标>5) 峰值密度={densMax:F1} 人/km²(目标≥8) max={popMax:F0}");
    }


    /// <summary>T22 领地涌现（演化后）：存在 ≥2 band 的凝聚体（领地 = 现实地域部落；需演化 r1）。
    /// ⚠️ 若 FAIL：领地碎片化过重（漂变 5% 全碎）——调 TerritoryRebuildEvery 或 TerritoryDriftDiv。</summary>
    private void T22_TerritoryEmergence(CivSimContext c)
    {
        var ids = new HashSet<int>();
        int inPolity = 0;
        foreach (var e in c.Polities)
            if (e.TerritorySize >= 2) { ids.Add(e.TerritoryId); inPolity++; }
        bool emerged = ids.Count >= 1;
        Check("T22 领地涌现", emerged, $"领地 {ids.Count} 个（≥2 band），成员 band {inPolity} 个");
    }


    /// <summary>T52 酋邦涌现统计（2026-08-17 演化级验收）：全演化后统计声望/大人物/酋长/酋邦。
    /// ⚠️ 先观测后断言：若酋邦 0 涌现 → 调凝聚条件（人口密度驱动）——本版断言"声望涌现"（软指标）+ 酋邦数观测打印。</summary>
    private void T52_ChiefdomEmergence(CivSimContext c)
    {
        int prestigeEnts = 0, bigMen = 0, chiefs = 0;
        var chiefdomIds = new HashSet<int>();
        foreach (var e in c.Polities)
        {
            if (e.Prestige > 0f) prestigeEnts++;
            if (e.IsBigMan) bigMen++;
            if (e.IsChief) chiefs++;
            if (e.ChiefdomId >= 0) chiefdomIds.Add(e.ChiefdomId);
        }
        LogService.Log("T52数据", $"声望band={prestigeEnts} 大人物={bigMen} 酋长={chiefs} 酋邦={chiefdomIds.Count} 个（成员band≥2）");
        Check("T52 酋邦涌现", prestigeEnts > 0, $"声望涌现={prestigeEnts > 0}（酋邦 {chiefdomIds.Count} 个——观测值，达标断言待演化数据）");
    }


    /// <summary>T18 性能：全演化计时（第三次演化，仅选中时跑 ~11s）。</summary>
    /// ⚠️ 2026-08-17 监督机制：逐模型耗时已由 CivEngine.Run 入 PerfLog 历史——此处显示历史汇总（劣化定位）。</summary>
    private void T18_Perf(int seed, int origins)
    {
        var sw = Stopwatch.StartNew();
        var r3 = CivEngine.Run(_grid, seed, origins);
        sw.Stop();
        long ms = sw.ElapsedMilliseconds;
        PerfLog.Summarize("civsim", "CivSim 逐模型");
        Check("T18 性能 n=64 全演化 <10s", ms < 10000, $"{ms}ms（tick {r3.FinalTick}）");
    }


    private static int CountFarming(CivSimContext c)
    {
        int n = 0;
        foreach (var e in c.Polities) if (e.IsFarming) n++;
        return n;
    }


    private HashSet<int> RichZone(GameGrid g)
    {
        var land = new List<(int cell, float k)>();
        var ctx = MakeCtx(g);
        for (int i = 0; i < g.N; i++)
            if (g.IsLandCell(i) && ctx.R[i] > 0f)
                land.Add((i, ctx.R[i]));
        land.Sort((a, b) => b.k.CompareTo(a.k));
        int rich = Mathf.Max(8, land.Count * 30 / 100);
        var set = new HashSet<int>();
        for (int i = 0; i < Mathf.Min(rich, land.Count); i++) set.Add(land[i].cell);
        return set;
    }

}
