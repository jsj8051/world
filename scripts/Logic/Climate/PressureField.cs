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

	/// <summary>从已保存的气压列装配：月数由行数推断，各月行须与年均等长；逐位复制，不重算。
	/// 输入为 null、长度不一致或含非有限值时抛 <see cref="ArgumentException"/>。</summary>
	internal static PressureField FromSaved(float[] annualHpa, float[][] monthlyHpa)
	{
		if (annualHpa == null) throw new ArgumentNullException(nameof(annualHpa));
		if (monthlyHpa == null) throw new ArgumentNullException(nameof(monthlyHpa));
		if (monthlyHpa.Length == 0) throw new ArgumentException("逐月气压至少需要一个月", nameof(monthlyHpa));

		int cells = annualHpa.Length;
		return new PressureField
		{
			AnnualHpa = CopyFinite(annualHpa, "年均气压"),
			MonthlyHpa = CopyMonthly(monthlyHpa, cells, "逐月气压"),
		};
	}

	static float[] CopyFinite(float[] source, string label)
	{
		var copy = new float[source.Length];
		for (int i = 0; i < source.Length; i++)
		{
			if (!float.IsFinite(source[i])) throw new ArgumentException($"{label}[{i}] 不是有限数");
			copy[i] = source[i];
		}
		return copy;
	}

	static float[][] CopyMonthly(float[][] source, int cells, string label)
	{
		var copy = new float[source.Length][];
		for (int month = 0; month < source.Length; month++)
		{
			float[] row = source[month] ?? throw new ArgumentException($"{label}[{month}] 为 null");
			if (row.Length != cells)
				throw new ArgumentException($"{label}[{month}] 长度 {row.Length} ≠ 格数 {cells}");
			copy[month] = CopyFinite(row, $"{label}[{month}]");
		}
		return copy;
	}
}
