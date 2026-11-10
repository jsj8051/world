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

	/// <summary>从已保存的温度列装配：年/月/最热/最冷逐位复制，不重算；月数由行数推断。
	/// <see cref="MeanC"/> 由已保存年值求平均（与 <see cref="Adopt"/> 同口径）。
	/// 输入为 null、长度不一致或含非有限值时抛 <see cref="ArgumentException"/>。</summary>
	internal static Temperature FromSaved(float[] annualC, float[][] monthlyC,
		float[] hotMonthC, float[] coldMonthC)
	{
		if (annualC == null) throw new ArgumentNullException(nameof(annualC));
		if (monthlyC == null) throw new ArgumentNullException(nameof(monthlyC));
		if (hotMonthC == null) throw new ArgumentNullException(nameof(hotMonthC));
		if (coldMonthC == null) throw new ArgumentNullException(nameof(coldMonthC));
		if (monthlyC.Length == 0) throw new ArgumentException("逐月温度至少需要一个月", nameof(monthlyC));

		int cells = annualC.Length;
		if (hotMonthC.Length != cells)
			throw new ArgumentException($"最热月均温长度 {hotMonthC.Length} ≠ 格数 {cells}", nameof(hotMonthC));
		if (coldMonthC.Length != cells)
			throw new ArgumentException($"最冷月均温长度 {coldMonthC.Length} ≠ 格数 {cells}", nameof(coldMonthC));

		var temperature = new Temperature
		{
			AnnualC = CopyFinite(annualC, "年均温"),
			MonthlyC = CopyMonthly(monthlyC, cells, "逐月温度"),
			HotMonthC = CopyFinite(hotMonthC, "最热月均温"),
			ColdMonthC = CopyFinite(coldMonthC, "最冷月均温"),
		};

		float sum = 0f;
		foreach (float value in temperature.AnnualC) sum += value;
		temperature.MeanC = cells > 0 ? sum / cells : 0f;
		return temperature;
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
