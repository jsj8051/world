using System;
using Godot;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 降水事实：逐格年降水（mm/年，绝对量纲）= 纬向基准 ZonalMm(|lat|) × 海洋性系数
/// （<see cref="InteriorRatio"/> 为渐近下限，再按 exp(−内陆 km / <see cref="CoastDecayKm"/>) 衰减）。
/// 只消费 Final 层（FinalLand / FinalDistToCoast），不读 CivSim / .mpa，逐格与 ball.CellIds 对齐；
/// 海格取海面降水真值、不参与大陆性，每格都有值，没有 0/null 缺失语义。
/// 副热带深内陆可达真实干旱量级，勿回退到相对量纲。不表达地形雨、季风、洋流、季节循环。
/// </summary>
public sealed class PrecipitationModel
{
	/// <summary>逐格年降水（mm/年，绝对量纲；与 <c>ball.CellIds</c> 逐位对齐 ⇒ 生产档 = H3 res4 CellId）。</summary>
	public float[] AnnualMm { get; private set; } = Array.Empty<float>();

	/// <summary>全球均值（mm/年）。H3 等积格 ⇒ 全球 cell 均值 = 算术平均（再乘 cos(lat) 是二次面积加权，会虚高）。
	/// 用途：<c>RiverNetwork</c> 的径流阈值自锚定（<c>RunoffThreshold = meanPrecip × 阈值格数</c>）与
	/// ④ 对照的量级锚，不是新的生产输入。与 <c>TemperatureModel.MeanC</c> 同性质。</summary>
	public float MeanMm { get; private set; }

	/// <summary>大陆性 e 折长度（km；几何尺度 B，不随 res 变）：离岸每增加此长度，
	/// 「海洋性 − <see cref="InteriorRatio"/>」衰减到 1/e。出厂值 24 = 450 km × 338.586 / 6371；
	/// 改世界半径须按比例重算，否则大陆性分布走样。</summary>
	public const float CoastDecayKm = 24f;

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

	/// <summary>生成逐格年降水（只消费 Final 层：<c>FinalLand</c> / <c>FinalDistToCoast</c>）。
	/// 绝不读取 CivSim / <c>.mpa</c> 文件。</summary>
	public void Generate(Ball ball, FinalGeography final)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		AnnualMm = new float[n];

		// hop → km：正六边形蜂窝的几何关系（相邻格中心距 = √3 × 等积格边长），随 res 变。
		// 这是尺度换算，不是 hop × 固定常数。BFS 不沿大圆、逐跳离散 ±25%，24 km 级（约 10 跳）
		// 误差约 ±8%，对平滑权重可接受；要精确离岸距离应在 Final 层加真实距离场，不在这里提精度。
		double kmPerHop = Math.Sqrt(3.0) * SpatialScale.Of(ball).CellEdgeKm;

		float sum = 0f;
		for (int i = 0; i < n; i++)
		{
			float latDeg = MathF.Abs(MathF.Asin(Math.Clamp(dirs[i].Y, -1f, 1f)) * 180f / MathF.PI);
			float zonalMm = ZonalMm(latDeg);
			bool land = final.FinalLand[i];
			// 读离岸距离 FinalDistToCoast（陆格改用 FinalDistToLand 会恒 0）
			float maritime = land ? CoastFactor(final.FinalDistToCoast[i], kmPerHop, latDeg) : 1f;
			float v = zonalMm * maritime;
			AnnualMm[i] = v;
			sum += v;
		}
		MeanMm = sum / n;
	}

	/// <summary>纬向年平均降水（mm/年；分段线性插值，南北对称）。对海格即海面降水量，
	/// 对陆格是"若完全海洋性"的基准。</summary>
	public static float ZonalMm(float latAbsDeg) => InterpLut(ZonalMmBands, latAbsDeg);

	/// <summary>深内陆渐近比（无量纲 ∈ (0,1)）：同纬度"深内陆降水 / 海岸降水"。
	/// 副热带最低（干旱内陆），赤道最高（对流性降水）。</summary>
	public static float InteriorRatio(float latAbsDeg) => InterpLut(InteriorRatioBands, latAbsDeg);

	/// <summary>陆格的海洋性 / 大陆性加权系数 ∈ [<see cref="InteriorRatio"/>, 1]：临海 1.0 →
	/// 内陆按 e 折长度 <see cref="CoastDecayKm"/> 衰减 → 趋 <see cref="InteriorRatio"/>。</summary>
	/// <param name="distToCoastHops"><c>FinalDistToCoast</c>（自海格 BFS；陆格 ≥ 1）。</param>
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
	/// <param name="distToCoastHops"><c>FinalGeography.FinalDistToCoast</c>（自海格 BFS；陆格 ≥ 1，海格 = 0）。</param>
	/// <param name="kmPerHop">本 res 的相邻格中心距（km；随 res 变）。</param>
	public static float CoastalPenetration(int distToCoastHops, double kmPerHop)
	{
		if (kmPerHop <= 0) throw new ArgumentOutOfRangeException(nameof(kmPerHop), kmPerHop, "相邻格中心距须为正");
		// 海岸格 FinalDistToCoast = 1 ⇒ −1 归一化成"内陆深度从 0 起算"
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
