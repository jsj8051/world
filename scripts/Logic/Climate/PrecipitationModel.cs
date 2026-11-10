using System;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 降水事实：逐格年降水（mm/年，绝对量纲）与逐月降水（mm/月）。
///
/// 年降水 = **Σ 12 个月**（月值由 <see cref="MonthlyClimate"/> 产出，年值涌现，不做"年值分配"）。
/// 另有一条**纬向估算**（纬向带 × 海洋性衰减），只用于温度闭环的湿润降温——
/// 因为那个闭环需要"温度之前就有降水"，不能用降水自己的下游产物。
///
/// 口径：海格也有降水（真实海面降水不为零），所以 <see cref="MeanMm"/> 是全格均值，
/// <c>RiverNetwork.RunoffThreshold</c> 直接用它是同一把尺子。
///
/// @example
/// <code>
/// precipitation.Adopt(monthly);
/// float annual = precipitation.AnnualMm[i];
/// float july   = precipitation.MonthlyMm[6][i];
/// </code>
/// </summary>
public sealed class PrecipitationModel
{
	/// <summary>逐格年降水（mm/年；与 <c>grid.Dirs</c> 逐位对齐）。</summary>
	public float[] AnnualMm { get; private set; } = Array.Empty<float>();

	/// <summary>逐月降水（mm/月）：<c>[month][cell]</c>。</summary>
	public float[][] MonthlyMm { get; private set; } = Array.Empty<float[]>();

	/// <summary>全球均值（mm/年）。H3 等积格 ⇒ 算术平均即全球均值。</summary>
	public float MeanMm { get; private set; }

	/// <summary>采纳月循环的产物作为降水事实（年 = Σ 月）。</summary>
	public void Adopt(MonthlyClimate climate)
	{
		if (climate == null) throw new ArgumentNullException(nameof(climate));
		MonthlyMm = climate.MonthPrecipMm;
		AnnualMm = climate.AnnualPrecipMm;

		float sum = 0f;
		foreach (float v in AnnualMm) sum += v;
		MeanMm = AnnualMm.Length > 0 ? sum / AnnualMm.Length : 0f;
	}

	/// <summary>从已保存的降水列装配：月数由行数推断，各月行须与年值等长；年/月值逐位复制，不重算。
	/// <see cref="MeanMm"/> 由已保存年值求平均（与 <see cref="Adopt"/> 同口径）。
	/// 输入为 null、长度不一致或含非有限值时抛 <see cref="ArgumentException"/>。</summary>
	internal static PrecipitationModel FromSaved(float[] annualMm, float[][] monthlyMm)
	{
		if (annualMm == null) throw new ArgumentNullException(nameof(annualMm));
		if (monthlyMm == null) throw new ArgumentNullException(nameof(monthlyMm));
		if (monthlyMm.Length == 0) throw new ArgumentException("逐月降水至少需要一个月", nameof(monthlyMm));

		var model = new PrecipitationModel
		{
			AnnualMm = CopyFinite(annualMm, "年降水"),
			MonthlyMm = CopyMonthly(monthlyMm, annualMm.Length, "逐月降水"),
		};

		float sum = 0f;
		foreach (float value in model.AnnualMm) sum += value;
		model.MeanMm = model.AnnualMm.Length > 0 ? sum / model.AnnualMm.Length : 0f;
		return model;
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

	/// <summary>大陆性 e 折长度（km；几何尺度 B，不随 res 变）：离岸每增加此长度，
	/// 「海洋性 − <see cref="InteriorRatio"/>」衰减到 1/e。出厂值 452 km（地球量级的大陆性 e 折长度）；
	/// 改世界半径须按比例重算，否则大陆性分布走样。</summary>
	public const float CoastDecayKm = 452f;

	// 纬向年平均降水 LUT（|纬度|度 → mm/年；地球海陆合计量级）：ITCZ 峰（0~10°）→ 副热带干槽
	// （20~25°）→ 温带次峰（45~55°）→ 极地低值；面积加权全球均值 ≈ 1,200 mm/年（未计大陆性衰减）。
	static readonly (float latDeg, float mm)[] ZonalMmBands =
	{
		(0f, 2200f), (10f, 1800f), (20f, 1100f), (25f, 850f), (35f, 900f),
		(45f, 1050f), (55f, 1150f), (65f, 850f), (75f, 450f), (90f, 250f),
	};

	// 深内陆渐近比 LUT（|纬度|度 → 无量纲 ∈ (0,1)；深内陆降水 / 同纬海岸降水）：
	// 副热带下沉 ⇒ 内陆极干（撒哈拉 / 阿塔卡马 ~0.1），赤道对流 ⇒ 内陆仍湿（刚果 ~0.8）；
	// 中高纬大陆内部（西伯利亚 / 加拿大）介于其间；工程标定参数，不是物理常数。
	static readonly (float latDeg, float ratio)[] InteriorRatioBands =
	{
		(0f, 0.80f), (15f, 0.45f), (25f, 0.12f), (35f, 0.30f),
		(50f, 0.45f), (65f, 0.55f), (90f, 0.65f),
	};

	/// <summary>
	/// 年降水的**纬向估算**（湿润降温闭环的输入）：纬向带基准 × 海洋性衰减 × 沿岸洋流修正。
	/// 不含季节、季风、地形雨影——那些只能在月循环里算，而闭环必须早于月循环。
	/// </summary>
	public static float[] EstimateAnnualMm(ClimateGrid grid, float[] coastalWarmth)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));

		int n = grid.Count;
		var estimate = new float[n];
		double kmPerHop = grid.NeighborKm;

		for (int i = 0; i < n; i++)
		{
			float latAbsDeg = MathF.Abs(grid.LatDeg(i));
			float zonal = ZonalMm(latAbsDeg);
			float maritime = grid.Land[i] ? CoastFactor(grid.DistToCoast[i], kmPerHop, latAbsDeg) : 1f;
			float value = zonal * maritime;

			// 暖流沿岸蒸发强 → 增湿；寒流沿岸 → 减湿。用整条洋流场的冷暖，不做额外放大。
			if (coastalWarmth != null) value *= 1f + coastalWarmth[i] * 0.25f;

			estimate[i] = MathF.Max(0f, value);
		}
		return estimate;
	}

	/// <summary>纬向年平均降水（mm/年；分段线性插值，南北对称）。对海格即海面降水量，
	/// 对陆格是"若完全海洋性"的基准。</summary>
	public static float ZonalMm(float latAbsDeg) => InterpLut(ZonalMmBands, latAbsDeg);

	/// <summary>深内陆渐近比（无量纲 ∈ (0,1)）：同纬度"深内陆降水 / 海岸降水"。
	/// 副热带最低（干旱内陆），赤道最高（对流性降水）。</summary>
	public static float InteriorRatio(float latAbsDeg) => InterpLut(InteriorRatioBands, latAbsDeg);

	/// <summary>陆格的海洋性 / 大陆性加权系数 ∈ [<see cref="InteriorRatio"/>, 1]：临海 1.0 →
	/// 内陆按 e 折长度 <see cref="CoastDecayKm"/> 衰减 → 趋 <see cref="InteriorRatio"/>。</summary>
	/// <param name="distToCoastHops"><c>DistToCoast</c>（自海格 BFS；陆格 ≥ 1）。</param>
	/// <param name="kmPerHop">本 res 的相邻格中心距（km；随 res 变）。</param>
	/// <param name="latAbsDeg">|纬度|（度）——决定渐近下限（副热带内陆最干）。</param>
	/// <remarks>退化解：全陆临海时 <c>distToCoastHops ≡ 1</c> ⇒ 内陆深度 0 ⇒ 系数 ≡ 1.0，
	/// 即没有大陆性、全世界都是海岸的纯纬向场。</remarks>
	public static float CoastFactor(int distToCoastHops, double kmPerHop, float latAbsDeg)
	{
		if (kmPerHop <= 0) throw new ArgumentOutOfRangeException(nameof(kmPerHop), kmPerHop, "相邻格中心距须为正");
		float floor = InteriorRatio(latAbsDeg);
		return floor + (1f - floor) * CoastalPenetration(distToCoastHops, kmPerHop);
	}

	/// <summary>海洋性渗透系数 ∈ (0, 1]：海岸格 1.0，内陆按 e 折长度 <see cref="CoastDecayKm"/> 单调衰减。
	/// 与纬度无关（纬度只通过 <see cref="InteriorRatio"/> 决定渐近下限）⇒ 可独立钉住"衰减长度是 km 不是 hop"。</summary>
	/// <param name="distToCoastHops"><c>DistToCoast</c>（自海格 BFS；陆格 ≥ 1，海格 = 0）。</param>
	/// <param name="kmPerHop">本 res 的相邻格中心距（km；随 res 变）。</param>
	public static float CoastalPenetration(int distToCoastHops, double kmPerHop)
	{
		if (kmPerHop <= 0) throw new ArgumentOutOfRangeException(nameof(kmPerHop), kmPerHop, "相邻格中心距须为正");
		// 海岸格 DistToCoast = 1 ⇒ −1 归一化成"内陆深度从 0 起算"
		double inlandKm = Math.Max(0, distToCoastHops - 1) * kmPerHop;
		return (float)Math.Exp(-inlandKm / CoastDecayKm);
	}

	/// <summary>分段线性插值（LUT 须按 x 升序）；≤ 首键取首值，≥ 末键取末值。</summary>
	static float InterpLut((float x, float y)[] table, float x)
	{
		if (x <= table[0].x) return table[0].y;
		for (int k = 0; k < table.Length - 1; k++)
		{
			var (x0, y0) = table[k];
			var (x1, y1) = table[k + 1];
			if (x <= x1) return y0 + (y1 - y0) * (x - x0) / (x1 - x0);
		}
		return table[^1].y;
	}
}
