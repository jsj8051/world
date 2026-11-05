using System;

namespace World.Logic;

/// <summary>
/// 气压事实：逐月 + 年均海平面气压（hPa，绝对值，不是异常）。
/// 与温度/降水同源（逐格、由同一次月度解产出），气压图与季风口径都只读它。
///
/// @example
/// <code>
/// pressure.Adopt(monthly);
/// float july = pressure.MonthlyHpa[6][i];
/// </code>
/// </summary>
public sealed class PressureField
{
	/// <summary>逐月气压（hPa）：<c>[month][cell]</c>。</summary>
	public float[][] MonthlyHpa { get; private set; } = Array.Empty<float[]>();

	/// <summary>年均气压（hPa）= 12 个月算术均值（涌现，不是另算一套）。</summary>
	public float[] AnnualHpa { get; private set; } = Array.Empty<float>();

	/// <summary>月数。</summary>
	public int MonthCount => MonthlyHpa.Length;

	/// <summary>采纳月循环的产物作为气压事实。</summary>
	public void Adopt(MonthlyClimate climate)
	{
		if (climate == null) throw new ArgumentNullException(nameof(climate));
		MonthlyHpa = climate.MonthPressureHpa;
		AnnualHpa = climate.AnnualPressureHpa;
	}
}
