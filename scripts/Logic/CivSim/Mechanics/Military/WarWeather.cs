using Godot;
using World.CivSim.Entities;

namespace World.CivSim.Mechanics.Military;

// ══════════════════════════════════════════════════════════════════
// 战争天气事件（2026-08-23 战争结算 v2 进程事件 B——用户拍板"天气要根据当地实际情况看"）：
//   不掷骰子凭空下雨——交战地气候本来怎样就怎样：高纬战场冬季严寒、季风区雨季泥泞、
//   旱区缺水。数据源 = 存档月场（GameGrid.MonthTemp[12][n] / MonthPrecip[12][n]，
//   v3.8 温度月度化；null = 旧档无月数据 → 天气中性）。
// 确定性：固定遍历序找第一对接触成员格（StatesTouch 同构）；无 Rng。
// ══════════════════════════════════════════════════════════════════
public static class WarWeather
{
    /// <summary>天气类型（一型互斥：严寒 > 雨季 > 干旱——每场会战只吃一种天气）。</summary>
    public enum Kind { None, Cold, Rainy, Dry }

    /// <summary>天气效果：进攻方（宣战方）军力乘数 + 双方附加损耗（人口比例）。</summary>
    public readonly struct Effect
    {
        public readonly Kind Kind;
        public readonly float AttackerMult;   // 进攻方军力 ×此值（1 = 无影响）
        public readonly float ExtraLoss;      // 双方损耗 +此值（人口比例；0 = 无）

        public Effect(Kind kind, float attackerMult, float extraLoss)
        {
            Kind = kind;
            AttackerMult = attackerMult;
            ExtraLoss = extraLoss;
        }
    }

    public static readonly Effect None = new(Kind.None, 1f, 0f);

    /// <summary>天气判定（纯函数——T70 式直接断言）。输入交战地气候特征，输出天气效果。</summary>
    public static Effect Classify(float coldestMonthTempC, float maxMonthPrecipFrac, float driestMonthPrecipMm, float annualTempC)
    {
        if (coldestMonthTempC < CivSimContext.WarColdMonthTemp)
            return new Effect(Kind.Cold, CivSimContext.WarColdAttackerMult, CivSimContext.WarColdLoss);
        if (maxMonthPrecipFrac > CivSimContext.WarRainyMonthFrac)
            return new Effect(Kind.Rainy, CivSimContext.WarRainyAttackerMult, 0f);
        if (driestMonthPrecipMm < CivSimContext.WarDryMonthPrecip && annualTempC > CivSimContext.WarDryTemp)
            return new Effect(Kind.Dry, 1f, CivSimContext.WarDryLoss);
        return None;
    }

    /// <summary>交战地天气提取：找第一对领地接触的成员格 → 取该格气候特征 → Classify。
    /// 月数据缺失（旧档）→ None（天气中性）。确定性：固定遍历序。</summary>
    public static Effect Of(CivSimContext ctx, War w)
    {
        var grid = ctx.Grid;
        if (grid == null || grid.MonthTemp == null || grid.MonthPrecip == null) return None;
        int cell = FindContactCell(ctx, w);
        if (cell < 0) cell = ChiefCell(ctx, w.StateIdA);   // 兜底：取挑战方酋长格
        if (cell < 0 || cell >= grid.N) return None;
        float coldest = float.MaxValue, maxFrac = 0f, minFrac = float.MaxValue;
        for (int m = 0; m < 12; m++)
        {
            if (grid.MonthTemp[m] == null || grid.MonthPrecip[m] == null) return None;
            float t = MonthTempC(grid.MonthTemp[m][cell]);      // byte −60~60 → °C
            float frac = grid.MonthPrecip[m][cell] / 255f;      // 月降水比例 0..1
            if (t < coldest) coldest = t;
            if (frac > maxFrac) maxFrac = frac;
            if (frac < minFrac) minFrac = frac;
        }
        if (coldest == float.MaxValue) return None;
        // 最干月降水 mm = 最小月比例 × 年降水（MonthPrecip 是比例场——×年降水=月降水，同 WildCropsSystem 口径）
        float driestMm = minFrac * grid.Precip[cell];
        return Classify(coldest, maxFrac, driestMm, grid.Temp[cell]);
    }

    // ──────────────────────────────────────────────
    // 私有辅助（确定性）
    // ──────────────────────────────────────────────

    /// <summary>月温度 byte 解码（编码：−60~60°C → 0-255，MapGenerator v3.8；逆运算）。</summary>
    private static float MonthTempC(byte b) => b / 255f * 120f - 60f;

    /// <summary>第一对领地接触成员格（StatesTouch 同构遍历：A 成员 × B 成员，接触即返回 A 侧成员格）。</summary>
    private static int FindContactCell(CivSimContext ctx, War w)
    {
        var grid = ctx.Grid;
        float reachKm = (2 * CivSimContext.InfluenceRadius + 1) * Mathf.Sqrt(grid.CellAreaKm2);
        var ma = MembersOf(ctx, w.StateIdA);
        var mb = MembersOf(ctx, w.StateIdB);
        for (int i = 0; i < ma.Count; i++)
        {
            var x = FindById(ctx, ma[i]);
            if (x == null || x.Dead) continue;
            for (int k = 0; k < mb.Count; k++)
            {
                var y = FindById(ctx, mb[k]);
                if (y == null || y.Dead) continue;
                if (grid.DistKm(x.Cell, y.Cell) > reachKm) continue;
                if (CivSimContext.TerritoryTouches(ctx, x, y)) return x.Cell;
            }
        }
        return -1;
    }

    /// <summary>国家至尊酋长格（兜底战场格）。</summary>
    private static int ChiefCell(CivSimContext ctx, int stateId)
    {
        var chief = FindById(ctx, stateId);
        return chief != null && !chief.Dead ? chief.Cell : -1;
    }

    private static System.Collections.Generic.List<int> MembersOf(CivSimContext ctx, int stateId)
    {
        if (ctx.ChiefdomCells == null || stateId < 0 || stateId >= ctx.ChiefdomCells.Length) return EmptyList;
        return ctx.ChiefdomCells[stateId];
    }

    private static readonly System.Collections.Generic.List<int> EmptyList = new();

    private static Polity FindById(CivSimContext ctx, int id)
    {
        for (int i = 0; i < ctx.Polities.Count; i++)
            if (ctx.Polities[i].Id == id && !ctx.Polities[i].Dead) return ctx.Polities[i];
        return null;
    }
}
