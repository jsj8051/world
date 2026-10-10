namespace World.Constants;

/// <summary>
/// **热力学常数**（温度场 / 能量平衡模型）。
///
/// ★已归一：输入/输出两侧的原始物理常数组合已各自**合并为一个系数**
///   （不再暴露 Q₀、α、ε、σ 各自的值；改系数即改整体行为）。
/// </summary>
public static class Thermal
{
    /// <summary>
    /// **输入系数**：赤道（cos φ = 1）吸收的太阳通量（W/m²）。
    /// 归一自 `Q₀·(1−α)` = 1361 × (1 − 0.30) ≈ 952.7。
    /// </summary>
    public const float AbsorbedSolarFluxWm2 = 952.7f;

    /// <summary>
    /// **输出系数**：辐射散热的倒数因子（m²·K⁴/W），用于 `T = (Q_in · 本值)^(1/4)`。
    /// 归一自 `1/(ε·σ)` = 1 / (1.0 × 5.670374419e-8) ≈ 1.7636e7。
    /// </summary>
    public const float RadiativeCoolingInverseWm2 = 1.7636e7f;

    /// <summary>大气温度递减率（°C/km；地球对流层实测 ~6.5）。</summary>
    public const float LapseRateCPerKm = 6.5f;

    /// <summary>绝对零度偏移（℃ = K − 273.15）。</summary>
    public const float KelvinToCelsius = 273.15f;
}
