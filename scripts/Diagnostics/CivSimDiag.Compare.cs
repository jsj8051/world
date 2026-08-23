// Slice: CivSimDiag.Compare.cs - verbatim member extraction from CivSimDiag.cs (pure refactor, 2026-08-19).
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
namespace World.Diagnostics;

public partial class CivSimDiag
{

    private static bool EntitiesEqual(CivSimContext a, CivSimContext b, string tag = "")
    {
        if (a.Bands.Count != b.Bands.Count)
        {
            LogService.Log("往返诊断{tag}", $"实体数 {a.Bands.Count} vs {b.Bands.Count}");
            return false;
        }
        // ⚠️ 2026-08-17 审查修复：场层对比（Cultivation/CellOwner/LockedUntil/Rng 状态）——
        //   此前只对比实体层，v9 开垦率场往返错位/恢复错误零检测（T02 假 PASS 风险；
        //   T04 分叉定位也只差场层）。FirstFarmTick/Fissions 不入档 → 不对比（避免往返必 FAIL）。
        if (!FloatSeqEqual(a.Cultivation, b.Cultivation))
        {
            LogService.Log("往返诊断{tag}", $"Cultivation 场不一致（开垦率往返错位）");
            return false;
        }
        if (!IntSeqEqual(a.CellOwner, b.CellOwner))
        {
            LogService.Log("往返诊断{tag}", $"CellOwner 场不一致");
            return false;
        }
        if (!IntSeqEqual(a.LockedUntil, b.LockedUntil))
        {
            LogService.Log("往返诊断{tag}", $"LockedUntil 场不一致");
            return false;
        }
        if (RngStateOf(a) != RngStateOf(b))
        {
            LogService.Log("往返诊断{tag}", $"Rng 状态不一致 {RngStateOf(a)} vs {RngStateOf(b)}");
            return false;
        }
        for (int k = 0; k < a.Bands.Count; k++)
        {
            var x = a.Bands[k]; var y = b.Bands[k];
            if (x.Id != y.Id || x.Cell != y.Cell || x.P != y.P || x.IsFarming != y.IsFarming
                || x.OriginCell != y.OriginCell || x.BornTick != y.BornTick
                || x.TerritoryId != y.TerritoryId || x.TerritorySize != y.TerritorySize
                // ⚠️ 2026-08-17 酋邦层字段对比（v10 入档；T02/T04 验收）
                || x.Prestige != y.Prestige || x.IsBigMan != y.IsBigMan || x.IsChief != y.IsChief
                || x.ChiefdomId != y.ChiefdomId || x.Contributed != y.Contributed
                || x.SuccessionUntil != y.SuccessionUntil
                // ⚠️ 2026-08-16 阶段4 国家层字段对比（纯派生不存档——读档 SettleDerived 重建须 ≡ 内存态）
                || x.StateId != y.StateId || x.StateSize != y.StateSize
                // ⚠️ 2026-08-19 阶段5 军事征服字段对比（v14 入档：吞并效忠/参战冷却——读档须恢复）
                || x.ConqueredBy != y.ConqueredBy || x.LastWarTick != y.LastWarTick)
            {
                LogService.Log("往返诊断{tag}", $"实体{k}: id={x.Id}vs{y.Id} cell={x.Cell}vs{y.Cell} P={x.P:F1}vs{y.P:F1} farm={x.IsFarming}vs{y.IsFarming} origin={x.OriginCell}vs{y.OriginCell} born={x.BornTick}vs{y.BornTick}");
                return false;
            }
            if (!SetEqual(x.TechKeys, y.TechKeys))
            {
                LogService.Log("往返诊断{tag}", $"实体{k}: techKeys A=[{string.Join(";", x.TechKeys)}] B=[{string.Join(";", y.TechKeys)}]");
                return false;
            }
            if (!ShareEqual(x.CultureShare, y.CultureShare))
            {
                LogService.Log("往返诊断{tag}", $"实体{k}: CultureShare A=[{ShareStr(x.CultureShare)}] B=[{ShareStr(y.CultureShare)}]");
                return false;
            }
            if (!ShareEqual(x.CultureGroupShare, y.CultureGroupShare))
            {
                LogService.Log("往返诊断{tag}", $"实体{k}: CultureGroup A=[{ShareStr(x.CultureGroupShare)}] B=[{ShareStr(y.CultureGroupShare)}]");
                return false;
            }
            if (!ShareEqual(x.ReligionCultShare, y.ReligionCultShare))
            {
                LogService.Log("往返诊断{tag}", $"实体{k}: ReligionCult A=[{ShareStr(x.ReligionCultShare)}] B=[{ShareStr(y.ReligionCultShare)}]");
                return false;
            }
            if (!ShareEqual(x.ReligionShare, y.ReligionShare))
            {
                LogService.Log("往返诊断{tag}", $"实体{k}: Religion A=[{ShareStr(x.ReligionShare)}] B=[{ShareStr(y.ReligionShare)}]");
                return false;
            }
            // ⚠️ 2026-08-18 阶段3 贸易期：Stocks 是贸易的活状态（v12 入档）——往返/续跑必须逐位一致
            //   （贸易 → 下 tick 增长/交换 → 人口，分叉会逐 tick 放大；补进实体对比防线）
            if (!FloatSeqEqual(x.Stocks, y.Stocks))
            {
                LogService.Log("往返诊断{tag}", $"实体{k}: Stocks 不一致（贸易/存储状态分叉）");
                return false;
            }
        }
        // ⚠️ 2026-08-19 阶段3 聚落：聚落实体（v13 新段）是场所持久状态——往返/续跑必须一致
        if (!SettlementsEqual(a, b))
        {
            LogService.Log("往返诊断{tag}", $"Settlements 不一致（聚落状态分叉）");
            return false;
        }
        // ⚠️ 2026-08-19 阶段5 战争：War 段（v14 新段）是过程状态——往返/续跑必须逐位一致
        //   （战争进行中读档续跑 = 继续消耗同一战争状态，T04 防线）
        if (!WarsEqual(a, b))
        {
            LogService.Log("往返诊断{tag}", $"Wars 不一致（战争状态分叉）");
            return false;
        }
        return true;
    }


    private static bool WarsEqual(CivSimContext a, CivSimContext b)
    {
        var wa = a.Wars; var wb = b.Wars;
        if (wa == null || wb == null || wa.Count != wb.Count) return false;
        for (int i = 0; i < wa.Count; i++)
        {
            var x = wa[i]; var y = wb[i];
            if (x.StateIdA != y.StateIdA || x.StateIdB != y.StateIdB || x.Defender != y.Defender
                || x.StartTick != y.StartTick || x.WinsA != y.WinsA || x.WinsB != y.WinsB
                || x.LastBattleTick != y.LastBattleTick || x.TributeTo != y.TributeTo
                || x.TributeFrom != y.TributeFrom || x.TributesLeft != y.TributesLeft) return false;
        }
        return true;
    }


    private static bool SettlementsEqual(CivSimContext a, CivSimContext b)
    {
        var sa = a.Settlements; var sb = b.Settlements;
        if (sa == null || sb == null || sa.Count != sb.Count) return false;
        for (int i = 0; i < sa.Count; i++)
        {
            var x = sa[i]; var y = sb[i];
            if (x.Id != y.Id || x.Cell != y.Cell || x.BornTick != y.BornTick || x.Level != y.Level
                || x.LastLevelUpTick != y.LastLevelUpTick || x.DwellFrom != y.DwellFrom
                || x.OccupantId != y.OccupantId || x.RuinFrom != y.RuinFrom) return false;
            if (!FloatSeqEqual(x.Stocks, y.Stocks)) return false;
        }
        return true;
    }


    private static bool SetEqual(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var k in a) if (!b.Contains(k)) return false;
        return true;
    }


    private static bool IntSeqEqual(int[] a, int[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }


    private static ulong RngStateOf(CivSimContext ctx)
        => (ctx.Rng as DeterministicRandom)?.State ?? 0UL;


    private static bool ByteSeqEqual(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }


    private static bool ShareEqual(ShareEntry[] a, ShareEntry[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].Key != b[i].Key || a[i].Frac != b[i].Frac) return false;
        return true;
    }


    private static string ShareStr(ShareEntry[] s)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (int i = 0; i < s.Length; i++)
            parts.Add($"{s[i].Key ?? "-"}:{s[i].Frac}");
        return string.Join(",", parts);
    }


    /// <summary>自然层零改动：.cmp 读回 vs 源 grid 逐字段一致（NaN 视为相等；WildCrops 两端重建一致）。</summary>
    private static bool NaturalUnchanged(GameGrid a, GameGrid b)
    {
        if (a.N != b.N) return false;
        for (int i = 0; i < a.N; i++)
        {
            if (!FloatEq(a.Elev[i], b.Elev[i]) || !FloatEq(a.Temp[i], b.Temp[i]) || !FloatEq(a.Precip[i], b.Precip[i])) return false;
            if (a.Biome[i] != b.Biome[i] || a.RiverLevel[i] != b.RiverLevel[i] || a.LakeLevel[i] != b.LakeLevel[i]) return false;
            if (a.RiverFlow[i] != b.RiverFlow[i] || !FloatEq(a.RiverVolume[i], b.RiverVolume[i])) return false;
            if (a.MineralLevel[i] != b.MineralLevel[i] || a.SoilLevel[i] != b.SoilLevel[i]) return false;
            if (a.MonsoonLevel[i] != b.MonsoonLevel[i]) return false;
            if (!FloatEq(a.CurrentWarmth[i], b.CurrentWarmth[i]) || !FloatEq(a.CurrentStrength[i], b.CurrentStrength[i])) return false;
            if (a.CurrentDirs[i] != b.CurrentDirs[i]) return false;
            for (int m = 0; m < MonsoonSystem.MonthCount; m++)
            {
                if (a.MonthPrecip[m][i] != b.MonthPrecip[m][i]) return false;
                if (a.MonthTemp[m][i] != b.MonthTemp[m][i]) return false;
            }
        }
        if (!PsiEquivalent(a.Psi, b.Psi)) return false;
        var wa = a.EnsureWildCrops();
        var wb = b.EnsureWildCrops();
        return ByteSeqEqual(wa, wb);
    }


    /// <summary>Psi 对比：null 或全零视为空（WriteBody 补零写，源网格可能为 null）。</summary>
    private static bool PsiEquivalent(float[] x, float[] y)
    {
        bool xEmpty = x == null || AllZero(x);
        bool yEmpty = y == null || AllZero(y);
        if (xEmpty || yEmpty) return xEmpty && yEmpty;
        return FloatSeqEqual(x, y);
    }


    private static bool AllZero(float[] a)
    {
        for (int i = 0; i < a.Length; i++)
            if (a[i] != 0f) return false;
        return true;
    }


    private static bool FloatEq(float x, float y) => x == y || (float.IsNaN(x) && float.IsNaN(y));

    private static bool FloatSeqEqual(float[] x, float[] y)
    {
        if (x == null || y == null || x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++)
            if (!FloatEq(x[i], y[i])) return false;
        return true;
    }

}
