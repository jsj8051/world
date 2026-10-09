using System;
using World.Spatial;       // Ball
using World.Constants;     // Thermal（热常数集中管理；2026-10-09 namespace World.Planet → World.Constants）

namespace World.WorldGen;

/// <summary>
/// 温度场：逐格温度事实（℃；与 <c>ball.CellIds</c> 逐位对齐）。
///
/// 模型（v1：**逐格能量收支**，输入与输出平衡得温度）：
///   Q_in(φ) = 输入系数 · cos φ         ← 每格从照射获取的能量（纬度决定入射角）
///   T_eq    = (Q_in · 输出系数)^(1/4)   ← 输出到外太空 = 输入时的平衡解
///   T       = T_eq − Γ·h               ← 大气温度递减（h 为海拔 km）
///
/// ★语义：**每格输入多少能量、输出多少能量，收支平衡即得温度**。
///   常数已归一（见 <see cref="Thermal"/>）：输入侧 Q₀(1−α)、输出侧 1/(εσ) 各合并为一个系数。
///   输入按纬度变化（cos φ：赤道最大、极地趋 0）⇒ 温度随纬度自然衰减。
///
/// ⚠️**已知模型边界（不加护栏，如实暴露）**：
///   `cos φ → 极地 = 0` ⇒ **T_eq → 0 K（−273 °C 奇异）**。真实地球极地靠**热输送**
///   维持——本模型无热输送，故极地必然奇冷。**这是模型缺失而非数值问题**，故意不加
///   下限：加了会把"缺热输送"伪装成正常结果，将来重做热输送时无从判断改善。
/// </summary>
public sealed class Temperature
{
    /// <summary>逐格温度（℃；与 <c>ball.CellIds</c> 逐位对齐）。</summary>
    public float[] CellTemperatureC { get; private set; } = Array.Empty<float>();

    /// <summary>全球平均温度（℃；等积格 ⇒ 算术平均）。判读/量级锚用，非生产输入。</summary>
    public float MeanC { get; private set; }

    // ── 系数：集中于 Constants.Thermal（已归一，不在此硬编码）──────────────
    const float InputFlux = Thermal.AbsorbedSolarFluxWm2;        // 输入侧（含 α）
    const float CoolingInv = Thermal.RadiativeCoolingInverseWm2; // 输出侧（含 εσ）
    const float LapseRateCPerKm = Thermal.LapseRateCPerKm;
    const float KelvinToCelsius = Thermal.KelvinToCelsius;

    public void Generate(Ball ball, HeightComposer composer, FinalGeography final)
    {
        if (ball == null) throw new ArgumentNullException(nameof(ball));
        if (composer == null) throw new ArgumentNullException(nameof(composer));
        if (final == null) throw new ArgumentNullException(nameof(final));
        var geo = ball.Geo;                   // 经纬度索引随 Ball 组合而来
        int n = ball.CellIds.Length;
        CellTemperatureC = new float[n];

        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float elevKm = composer.HeightM[i] * 1e-3f;          // 海拔 → km
            // 纬度弧度直接取自 BallGeoIndex（Cos 为偶函数 ⇒ 无需取 |纬度|）
            float qIn = InputFlux * MathF.Cos(geo.LatRad[i]);     // 纬度输入
            // ⚠️ 极地 qIn→0 ⇒ tEqC→−273.15（奇异，见类注释）；不夹不护，如实暴露。
            float tEqC = MathF.Pow(qIn * CoolingInv, 0.25f) - KelvinToCelsius;
            float t = tEqC - LapseRateCPerKm * elevKm;           // 海拔递减
            CellTemperatureC[i] = t;
            sum += t;
        }
        MeanC = sum / n;
    }
}
