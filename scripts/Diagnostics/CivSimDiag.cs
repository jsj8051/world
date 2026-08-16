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

namespace World.Diagnostics;

/// <summary>
/// 文明演化诊断（v4 纯实体模型全测试规格，docs/石器时代设计.md §十二）：
///   S1-S6 构造式场景（内联构造，不依赖自然图）+ T01-T20 地图测试。
///   每项输出 "T-xx 名称 PASS/FAIL 数据"；全 PASS 退出码 0，任一 FAIL 退出码 1。
///
/// 命令行：-- --arch=user://maps/xxx.mpa [--seed=N] [--origins=1..6] [--out=user://maps/xxx.cmp]
/// </summary>
public partial class CivSimDiag : DiagSceneBase
{
    private int _pass, _fail;
    private GameGrid _grid;
    private HashSet<string> _only;   // --only= 白名单（空 = 全部）
    private HashSet<string> _skip;   // --skip= 黑名单
    private string _forcedArch;      // SetArch() 注入（编辑器/代码内调用等价 --arch=）

    /// <summary>代码/编辑器内调用入口：设置测试筛选（等价命令行 --only= / --skip=；须在节点入树前调用）。</summary>
    public void SetFilter(string only, string skip)
    {
        if (!string.IsNullOrEmpty(only)) _only = ParseSet(only);
        if (!string.IsNullOrEmpty(skip)) _skip = ParseSet(skip);
    }

    /// <summary>代码/编辑器内调用入口：指定地图路径（等价 --arch=；须在节点入树前调用）。</summary>
    public void SetArch(string path) => _forcedArch = path;

    public override void _Ready()
    {
        string arch = _forcedArch ?? ArchiveDiag.ResolveArchPath();
        int seed = 42, origins = 3;
        string outPath = arch != null ? arch.GetBaseName() + ".cmp" : null;
        var args = ParseUserArgs();
        if (args.TryGetValue("seed", out var seedArg) && int.TryParse(seedArg, out int s)) seed = s;
        if (args.TryGetValue("origins", out var oArg) && int.TryParse(oArg, out int o)) origins = Mathf.Clamp(o, 1, 6);
        if (args.TryGetValue("out", out var outArg)) outPath = outArg;
        if (args.TryGetValue("only", out var onlyArg)) _only = ParseSet(onlyArg);
        if (args.TryGetValue("skip", out var skipArg)) _skip = ParseSet(skipArg);
        if (_only != null || _skip != null)
            LogService.Log("CivSimDiag", $"筛选: --only=[{string.Join(",", _only ?? new HashSet<string>())}] --skip=[{string.Join(",", _skip ?? new HashSet<string>())}]");

        // ── S 构造场景（无地图依赖，先跑）──
        RunScenarios();

        // ── T 地图测试 ──
        if (arch == null)
        {
            LogService.Log("CivSimDiag", $"无 --arch：仅跑构造场景（S1-S6）→ 总 {_pass}P/{_fail}F");
            GetTree().Quit(_fail == 0 ? 0 : 1);
            return;
        }
        if (!ArchiveDiag.TryLoad(arch, out var mctx))
        {
            GetTree().Quit(1);
            return;
        }
        _grid = GameGrid.FromMapData(mctx.Map);
        LogService.Log("CivSimDiag", $"读档 {arch} n={_grid.N} → 全测试（seed={seed} 起源{origins}，自然层只读）");
        bool hasFossil = false;
        for (int i = 0; i < _grid.N; i++)
            if (_grid.Biome[i] >= 4 && _grid.Biome[i] <= 11) { hasFossil = true; break; }
        LogService.Log("CivSimDiag", $"地图 biome 化石值(4-11)存在={hasFossil}（旧档放弃策略：含化石 → 存档/演化拒绝）");
        RunMapTests(seed, origins, outPath);

        LogService.Log("CivSimDiag", $"汇总：{_pass} PASS / {_fail} FAIL → {(_fail == 0 ? "全部PASS" : "有失败!")}");
        GetTree().Quit(_fail == 0 ? 0 : 1);
    }

    private void Check(string name, bool ok, string data = "")
    {
        if (ok) _pass++; else _fail++;
        // 断言输出：保持 GD.Print 直调（`^  FAIL` 前缀被 verify.sh/CI 解析，ADR-0004 §决策3）
        GD.Print($"  {(ok ? "PASS" : "FAIL")} {name}{(data.Length > 0 ? " | " + data : "")}");
    }

    /// <summary>测试筛选：--only（白名单，空=全部）与 --skip（黑名单）取交集语义。
    /// 支持前缀：S=全部构造场景，T=全部地图测试（含存档组）；"存档"=T01/T02/T04/T19 组。
    /// 共享计算的组（T14/T08、T15/T16）由组 gate 触发一次，组内各 Check 再按 Want 各自开关。</summary>
    private bool Want(string id)
    {
        if (_skip != null && _skip.Contains(id)) return false;
        if (_only == null || _only.Count == 0) return true;
        if (_only.Contains(id)) return true;
        if (_only.Contains("S") && id.StartsWith("S", StringComparison.Ordinal)) return true;
        if (_only.Contains("T") && (id.StartsWith("T", StringComparison.Ordinal) || id == "存档")) return true;
        return false;
    }

    /// <summary>显式 gate：仅当 --only 明确选中（含前缀 T/S）才跑。
    /// 2026-08-18：T40 性能基线是"显式跑的回归防线"（贵 ~30-40s，且 n16 pipeline 机器抖动可达 3×，
    /// 无筛选全量跑会噪声误报）——恢复其"不进全量默认"的设计意图。</summary>
    private bool WantExplicit(string id)
    {
        if (_skip != null && _skip.Contains(id)) return false;
        if (_only == null || _only.Count == 0) return false;
        if (_only.Contains(id)) return true;
        if (_only.Contains("S") && id.StartsWith("S", StringComparison.Ordinal)) return true;
        if (_only.Contains("T") && id.StartsWith("T", StringComparison.Ordinal)) return true;
        return false;
    }

    private bool WantAny(params string[] ids)
    {
        foreach (var id in ids) if (Want(id)) return true;
        return false;
    }

    private static HashSet<string> ParseSet(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in s.Split(','))
        {
            string id = t.Trim();
            if (id.Length > 0) set.Add(id);
        }
        return set;
    }

    // ═══════════════════ S 构造场景 ═══════════════════

    private void RunScenarios()
    {
        LogService.Log("CivSimDiag", "── S 构造场景 ──");
        if (Want("S1")) S1_GrowthAndEnergy();
        if (Want("S2")) S2_ModeMatrix();
        if (Want("S3")) S3_ShareConservation();
        if (Want("S4")) S4_FissionInherit();
        if (Want("S5")) S5_SpreadDependency();
        if (Want("S6")) S6_ReligionLock();
        if (Want("S7")) S7_StateInvariants();   // 运行时不变量（2026-08-19）

        // 无地图依赖的 T 测试（构造场景风格，S 段注册）
        if (Want("T24")) T24_TerritoryCohesion();
        if (Want("T25")) T25_FissionPressure();
        if (Want("T26")) T26_CapabilitySwitches();
        if (Want("T27")) T27_StorageBuffer();
        if (Want("T53")) T53_FamineFromStorage();
        if (Want("T54")) T54_GrindingPreserves();
        if (Want("T55")) T55_BarterExchange();
        if (Want("T56")) T56_TradeConvergence();
        if (Want("T57")) T57_CultureSpread();
        if (Want("T58")) T58_ReligionSectSpread();
        if (Want("T59")) T59_ChiefdomPatronage();
        if (Want("T60")) T60_TradeFlowStats();
        if (Want("T61")) T61_SettlementFormation();
        if (Want("T62")) T62_SettlementLevel();
        if (Want("T63")) T63_SettlementPersistence();
        if (Want("T64")) T64_StateEmergence();
        if (Want("T65")) T65_StateMechanisms();
        if (Want("T66")) T66_StateCollapse();
        if (Want("T67")) T67_SuccessionInstitutionalized();
        if (Want("T28")) T28_LivestockEmergence();
        if (Want("T29")) T29_GoodsAccumulation();
        if (Want("T30")) T30_WeightAllocation();
        if (Want("T31")) T31_DepletionMigrate();
        if (Want("T32")) T32_CompetitiveTakeover();
        if (Want("T33")) T33_ConflictBurst();
        if (Want("T34")) T34_WeaponAdvantage();
        if (Want("T35")) T35_LockHoldReclaim();
        if (Want("T36")) T36_LandCompetition();
        if (Want("T37")) T37_CultivationGrowth();
        if (Want("T38")) T38_EquiMarginal();
        if (Want("T39")) T39_SettleStorage();
        if (WantExplicit("T40")) T40_PerfSegments();   // ⚠️ n16 快生成 ~3-5s——仅显式 --only=T40 定期跑（不进全量默认）
        if (Want("T41")) T41_PerfHistory();    // ⚠️ 只读历史汇总，秒级——可进全量；默认不进（避免输出噪音）
        if (Want("T42")) T42_PrestigeAccumulation();
        if (Want("T43")) T43_BigManEmergence();
        if (Want("T44")) T44_ChiefInstitutionalize();
        if (Want("T45")) T45_ChiefdomCoalesce();
        if (Want("T46")) T46_TribeIndependence();
        if (Want("T47")) T47_TributeReciprocity();
        if (Want("T48")) T48_EliteSupport();
        if (Want("T49")) T49_AllianceStrength();
        if (Want("T50")) T50_SuccessionWindow();
        if (Want("T23")) T23_TerritoryMult();
    }

    // ═══════════════════ T 地图测试 ═══════════════════

    private void RunMapTests(int seed, int origins, string outPath)
    {
        LogService.Log("CivSimDiag", "── T 地图测试 ──");
        // 演化 gate：未选任何地图测试（如 --only=S1,S2）时跳过完整演化（最贵段 ~11s）
        bool needEvol = WantAny("T01", "T02", "T03", "T04", "T05", "T08", "T09", "T10", "T11", "T13", "T14", "T15", "T16", "T17", "T21", "T22", "T52", "存档");
        CivSimResult r1 = null;    // 演化结果（needEvol=false 时为 null，依赖它的测试已被筛掉）
        if (needEvol) r1 = EvolveAndDebug(seed, origins);
        else LogService.Log("CivSimDiag", "--only 未含地图测试：跳过演化");
        var c = r1?.Context;   // 演化 context（needEvol=false 时为 null）

        bool repro = false, wcDet = false, rtOk = false;
        if (Want("T03")) repro = T03_Reproducibility(c, seed, origins);   // 内部二次演化（~11s），仅选中时跑
        if (Want("T17")) wcDet = T17_WildCrops(seed);
        if (WantAny("T01", "T02", "T04", "T19", "存档")) rtOk = ArchiveChecks(outPath, r1, c, seed, origins);
        if (Want("T05")) T05_Origins(seed, origins);
        if (Want("T09")) T09_DependencyChain(c);
        if (WantAny("T14", "T08")) T14_T08_Agriculture(c);
        if (Want("T10")) T10_Spread(c);
        if (Want("T11")) T11_FissionMigration(c);
        if (Want("T13")) T13_Religion(c);
        if (WantAny("T15", "T16")) T15_T16_Coverage(c);
        if (Want("T21")) T21_PopGradient(c);
        if (Want("T22")) T22_TerritoryEmergence(c);
        if (Want("T52")) T52_ChiefdomEmergence(c);   // 演化级：酋邦涌现统计（先观测后断言）
        if (Want("T18")) T18_Perf(seed, origins);   // 第三次演化（~11s），仅选中时跑
        // ⚠️ 2026-08-17 审查修复：T04 拆独立函数放末尾——它续跑 r1.Context 会污染 T09-T22 的共享态；
        //   且语义改为"读档续跑 vs 内存态续跑"（见 T04_Continuation 注释）
        if (Want("T04")) T04_Continuation(outPath, seed, origins);
        // T20 全链确定性（复现×WildCrops×存档往返 组合指标）——需要 T03+T17+存档组同时被选才有意义
        if (Want("T20") && Want("T03") && Want("T17") && (outPath == null || WantAny("T01", "T02", "T04", "T19", "存档")))
            Check("T20 全链确定性", repro && wcDet && (outPath == null || rtOk), $"复现={repro} WildCrops={wcDet} 往返={rtOk}");
        else if (Want("T20"))
            GD.Print("  - T20 跳过：需同时选中 T03+T17+存档组（--only=T03,T17,存档,T20）");
    }

    /// <summary>完整演化一次 + [文化调试] 状态摘要打印（只被地图测试共享；演化后无条件打印摘要）。</summary>
    private CivSimResult EvolveAndDebug(int seed, int origins)
    {
        var sw = Stopwatch.StartNew();
        var r1 = CivEngine.Run(_grid, seed, origins);
        sw.Stop();
        var c = r1.Context;
        // [临时调试] 文化空间分布（起源 key + 各文化实体数）
        var cultCount = new Dictionary<string, int>();
        var originCults = new System.Text.StringBuilder();
        foreach (var e in r1.Context.Tribes)
        {
            string ck = World.CivSim.ShareField.DomKey(e.CultureShare);
            if (ck != null) cultCount[ck] = cultCount.TryGetValue(ck, out var v) ? v + 1 : 1;
            if (e.BornTick == 0) originCults.Append($"格{e.Cell}:{ck} ");
        }
        var cultTop = new List<KeyValuePair<string, int>>(cultCount);
        cultTop.Sort((a, b) => b.Value.CompareTo(a.Value));
        var cultStr = new System.Text.StringBuilder();
        for (int q = 0; q < Math.Min(6, cultTop.Count); q++)
            cultStr.Append($"{cultTop[q].Key}={cultTop[q].Value} ");
        LogService.Log("文化调试", $"起源={originCults} | 文化实体数: {cultStr}");
        // [调试] 起源格当前主导文化（是否被外文化吞并）——2026-08-17 审查修复：改动态起源格（旧硬编码 {7597,7106,58} 是特定 n128 图的残留）
        var originCells = new List<int>();
        foreach (var e in r1.Context.Tribes) if (e.BornTick == 0 && !originCells.Contains(e.Cell)) originCells.Add(e.Cell);
        originCells.Sort();
        var ocStr = new System.Text.StringBuilder();
        foreach (int oc in originCells)
        {
            if (oc < 0 || oc >= c.CellTribes.Length) continue;
            var d0 = c.CellTribes[oc];   // 一格一实体：单部落或 null
            string domKey = "无";
            if (d0 != null && !d0.Dead)
            {
                domKey = World.CivSim.ShareField.DomKey(d0.CultureShare) ?? "null";
                if (domKey == "null" || domKey == "无")
                    LogService.Log("文化调试", $"null格{oc} 实体:{d0.Id}(P{d0.P:F0})[{(World.CivSim.ShareField.DomKey(d0.CultureShare) ?? "n")}:{d0.CultureShare[0].Frac},{World.CivSim.ShareField.SecKey(d0.CultureShare) ?? "n"}:{d0.CultureShare[1].Frac}] ");
            }
            ocStr.Append($"格{oc}:{domKey}({(d0 != null && !d0.Dead ? 1 : 0)}实体) ");
        }
        LogService.Log("文化调试", $"起源格现状: {ocStr}");
        // [临时调试] 各文化格级覆盖（主导文化格数 + 平均实体人口）
        var cultGrid = new Dictionary<string, int>();
        var cultPop = new Dictionary<string, float>();
        for (int gi = 0; gi < c.CellTribes.Length; gi++)
        {
            var d0 = c.CellTribes[gi];
            if (d0 == null || d0.Dead) continue;
            string gk = World.CivSim.ShareField.DomKey(d0.CultureShare);
            if (gk == null) continue;
            cultGrid[gk] = cultGrid.TryGetValue(gk, out var gv) ? gv + 1 : 1;
            cultPop[gk] = cultPop.TryGetValue(gk, out var pv) ? pv + d0.P : d0.P;
        }
        var cgTop = new List<KeyValuePair<string, int>>(cultGrid);
        cgTop.Sort((a, b) => b.Value.CompareTo(a.Value));
        var cgStr = new System.Text.StringBuilder();
        for (int q = 0; q < cgTop.Count; q++)
            cgStr.Append($"{cgTop[q].Key}={cgTop[q].Value}格(均pop{cultPop[cgTop[q].Key] / cgTop[q].Value:F0}) ");
        LogService.Log("文化调试", $"格级覆盖: {cgStr}");
        // [临时调试] 文化群/宗教派别多样性（产生 vs 存活）
        var grpCount = new Dictionary<string, int>();
        var relCount = new Dictionary<string, int>();
        foreach (var e in r1.Context.Tribes)
        {
            string gk = World.CivSim.ShareField.DomKey(e.CultureGroupShare);
            if (gk != null) grpCount[gk] = grpCount.TryGetValue(gk, out var v) ? v + 1 : 1;
            string rk = World.CivSim.ShareField.DomKey(e.ReligionCultShare);
            if (rk != null) relCount[rk] = relCount.TryGetValue(rk, out var v2) ? v2 + 1 : 1;
        }
        LogService.Log("文化调试", $"文化群存活={grpCount.Count} 宗教派别存活={relCount.Count} (CultureKeyCount={r1.Context.CultureKeyCount} ReligionKeyCount={r1.Context.ReligionKeyCount})");
        LogService.Log("CivSimDiag", $"演化 {r1.FinalTick} tick（{r1.FinalTick * CivSimContext.TickYears} 年）| 实体 {r1.Context.Tribes.Count} | 人口 {r1.Context.TotalPopulation():F0} | 首转农 tick {r1.Context.FirstFarmTick} | 耗时 {sw.ElapsedMilliseconds}ms" +
                 $" | 贸易 {r1.Context.TradeEvents} 次/{r1.Context.TradeVolume:F0} 量");
        return r1;
    }
}

// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲
// Slices (2026-08-19 pure refactor: partial class, behavior unchanged):
//   CivSimDiag.Builders.cs  - scenario construction helpers (MakeGrid/MakeCtx/AddTribe/AddSettlement/SetupStateChiefdom/WriteBad*)
//   CivSimDiag.Scenarios.cs - S1-S6 + construct-style T tests (T23-T67 series)
//   CivSimDiag.MapTests.cs  - archive-driven map tests (T03/T04/T05/T09-T22/T52/T18 + helpers)
//   CivSimDiag.Compare.cs   - round-trip equality helpers (EntitiesEqual/ShareStr/NaturalUnchanged/...)
// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲
