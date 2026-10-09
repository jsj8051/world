using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using World.Domain;
using World.CivSim;
using World.LogicGrid;
using World.Archive;
using World.Services;

using World.CivSim.Entities;
namespace World.Diagnostics;

/// <summary>
/// 文明演化诊断（v4 纯实体模型全测试规格，docs/石器时代设计.md §十二）：
///   S1-S7 构造式场景（内联构造，不依赖自然图）+ 构造风格 T 测试（T23-T77）。
///   每项输出 "T-xx 名称 PASS/FAIL 数据"；全 PASS 退出码 0，任一 FAIL 退出码 1。
///
/// ★2026-10-09 存档清退：原 T01-T22/T52/T90/T91「地图测试」强依赖 `--arch=<.mpa>`
///   读档经 `GameGrid.FromMapData` 构造网格；Legacy 存档编解码（MapArchive/CivMapArchive/
///   GameMapArchive/ArchiveChunk/ArchiveLayout/CivArchiveSchema）已删除、待新线（WorldGen）
///   重做 ⇒ 地图测试及其辅助（MapTests/Compare/FishPotential/OverHarvest）一并移除。
///   本诊断现只跑**无自然图依赖**的构造场景，**不需任何命令行参数**。
///
/// 命令行：-- [--only=] [--skip=]
/// </summary>
public partial class CivSimDiag : DiagSceneBase
{
    private int _pass, _fail;
    private HashSet<string> _only;   // --only= 白名单（空 = 全部）
    private HashSet<string> _skip;   // --skip= 黑名单

    /// <summary>代码/编辑器内调用入口：设置测试筛选（等价命令行 --only= / --skip=；须在节点入树前调用）。</summary>
    public void SetFilter(string only, string skip)
    {
        if (!string.IsNullOrEmpty(only)) _only = ParseSet(only);
        if (!string.IsNullOrEmpty(skip)) _skip = ParseSet(skip);
    }

    public override void _Ready()
    {
        var args = ParseUserArgs();
        if (args.TryGetValue("only", out var onlyArg)) _only = ParseSet(onlyArg);
        if (args.TryGetValue("skip", out var skipArg)) _skip = ParseSet(skipArg);
        if (_only != null || _skip != null)
            LogService.Log("CivSimDiag", $"筛选: --only=[{string.Join(",", _only ?? new HashSet<string>())}] --skip=[{string.Join(",", _skip ?? new HashSet<string>())}]");

        // ── 构造场景（无自然图依赖；2026-10-09 存档清退后 = 本诊断唯一路径）──
        RunScenarios();

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
        if (Want("T62")) T62_TownFunction();
        if (Want("T63")) T63_SettlementPersistence();
        if (Want("T64")) T64_StateEmergence();
        if (Want("T65")) T65_StateMechanisms();
        if (Want("T66")) T66_StateCollapse();
        if (Want("T67")) T67_SuccessionInstitutionalized();
        if (Want("T70")) T70_WarDeclareGate();      // 阶段5 军事征服（2026-08-19，docs/阶段5设计-军事征服.md）
        if (Want("T71")) T71_BattleChance();
        if (Want("T72")) T72_WarAnnex();
        if (Want("T73")) T73_WarTribute();
        if (Want("T74")) T74_WarTruce();
        if (Want("T75")) T75_WarDiplomacy();
        if (Want("T76")) T76_SeaColonization();  // 扩张修正（2026-08-19：跨海 unlock_sea 落地 + 殖民扩散项）
        if (Want("T77")) T77_ColonizeDiffusion();
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
        if (Want("T41")) T41_PerfHistory();    // ⚠️ 只读历史汇总，秒级——可进全量；默认不进（避免输出噪音）
        if (Want("T42")) T42_PrestigeAccumulation();
        if (Want("T43")) T43_BigManEmergence();
        if (Want("T44")) T44_ChiefInstitutionalize();
        if (Want("T45")) T45_ChiefdomCoalesce();
        if (Want("T46")) T46_PolityIndependence();
        if (Want("T47")) T47_TributeReciprocity();
        if (Want("T48")) T48_EliteSupport();
        if (Want("T49")) T49_AllianceStrength();
        if (Want("T50")) T50_SuccessionWindow();
        if (Want("T23")) T23_TerritoryMult();
    }

}

// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲
// Slices (2026-08-19 pure refactor: partial class, behavior unchanged):
//   CivSimDiag.Builders.cs  - scenario construction helpers (MakeGrid/MakeCtx/AddPolity/AddHabitation/SetupStateChiefdom)
//   CivSimDiag.Scenarios.cs - S1-S7 + construct-style T tests (T23-T77 series)
// 2026-10-09 存档清退：MapTests.cs / Compare.cs / FishPotential.cs / OverHarvest.cs 已删
//   （它们全部以 --arch 读档为唯一输入；待新线存档重做后按需重建）。
// 鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲鈺愨晲
