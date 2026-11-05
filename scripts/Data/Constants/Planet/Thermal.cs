namespace World.Data;

/// <summary>行星热力学常数（温度模型共用的物理量）。</summary>
public static class Thermal
{
    /// <summary>大气温度递减率（°C/km；地球对流层实测 ~6.5）。</summary>
    public const float LapseRateCPerKm = 6.5f;
}
