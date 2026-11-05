using System;

namespace World.Logic;

/// <summary>
/// 温度事实：逐格年均温 + 逐月温度 + 月极值（柯本分类与物候的下游输入）。
///
/// 事实来源是月循环：年均温 = mean(12 月)，不是另一套年均公式算出来的——
/// 这样"年均"和"季节"永远是同一个场，不会出现两套模型只靠一个补热项粘在一起。
///
/// @example
/// <code>
/// temperature.Adopt(monthly);
/// float annual = temperature.AnnualC[i];
/// float jan    = temperature.MonthlyC[0][i];
/// </code>
/// </summary>
public sealed class Temperature
{
	/// <summary>逐格年均温（°C）。</summary>
	public float[] AnnualC { get; private set; } = Array.Empty<float>();

	/// <summary>逐月温度（°C）：<c>[month][cell]</c>。</summary>
	public float[][] MonthlyC { get; private set; } = Array.Empty<float[]>();

	/// <summary>月数。</summary>
	public int MonthCount => MonthlyC.Length;

	/// <summary>最热月均温（°C）。</summary>
	public float[] HotMonthC { get; private set; } = Array.Empty<float>();

	/// <summary>最冷月均温（°C）。</summary>
	public float[] ColdMonthC { get; private set; } = Array.Empty<float>();

	/// <summary>全球年均温（°C；等积格算术平均）。</summary>
	public float MeanC { get; private set; }

	/// <summary>采纳月循环的产物作为温度事实。</summary>
	public void Adopt(MonthlyClimate climate)
	{
		if (climate == null) throw new ArgumentNullException(nameof(climate));
		AnnualC = climate.AnnualTempC;
		MonthlyC = climate.MonthTempC;
		HotMonthC = climate.HotMonthC;
		ColdMonthC = climate.ColdMonthC;

		float sum = 0f;
		foreach (float t in AnnualC) sum += t;
		MeanC = AnnualC.Length > 0 ? sum / AnnualC.Length : 0f;
	}
}
