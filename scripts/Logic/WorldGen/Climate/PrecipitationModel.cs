using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.H3Grid;    // Ball（H3 球壳数据层）

namespace World.WorldGen;

// 世界生成空间 · **降水事实**（Phase 4 · P4-2）。P4-2 报告见 docs/（占位语义→绝对量纲的取证与动机）。
//   ★性质：**世界事实（World Fact）生产者**，不是 CivSim 输入适配器、不是 Legacy `PREC` 复制品。
//   ★事实 = 逐格**年降水总量**（mm/年，绝对量纲）：纬向基准 `ZonalMm(|lat|)`（地球实测量级）
//   × 海洋性/大陆性 `MaritimeFactor`（`InteriorRatio(lat)` 深内陆渐近比 + `exp(−内陆km/CoastDecayKm)` 衰减）。
//   副热带深内陆可达真实干旱量级（InteriorRatio 的意义——旧归一化场没有荒漠，勿回退到相对量纲）。
//   ★生产语义：只消费 Final 层（`FinalLand`/`FinalDistToCoast`）；不经过 res3、不接 `GameGrid`、
//   不读 CivSim / Legacy `.mpa`；生产粒度 = 传入 `Ball` 的 res，与 `ball.CellIds` 逐位对齐。
//   ★边界：**每格都有降水**（海格 = 海面降水量真值，无 0/null 缺失语义——Legacy 把海格填 0 是建模产物）；
//   海格不参与大陆性（`MaritimeFactor ≡ 1`）。
//   ★不表达（已钉清的边界，不是窟窿）：地形雨、季风、洋流、季节循环（P4-5 课题）。
//   Legacy `PREC` 与本事实同物理量但使用方式不同；是否 EQUIVALENT/需 Adapter 由 ④ provenance 对照取证决定。
/// <summary>
/// 降水事实：纬向基准（mm/年）× 海洋性/大陆性 → 逐格**年降水**（mm/年）。
/// </summary>
public sealed class PrecipitationModel
{
	/// <summary>逐格年降水（**mm/年，绝对量纲**；与 <c>ball.CellIds</c> 逐位对齐 ⇒ 生产档 = H3 res4 CellId）。</summary>
	public float[] AnnualMm { get; private set; } = Array.Empty<float>();

	/// <summary>
	/// 全球均值（mm/年）。★口径：H3 是**等积格** ⇒ 全球 cell 均值 = **算术平均**
	/// （再乘 `cos(lat)` 是把面积雅可比**二次**加权，会虚高）。
	/// 用途：`RiverNetwork` 的**径流阈值自锚定**（`RunoffThreshold = meanPrecip × 阈值格数`）
	/// 与 ④ provenance 对照的量级锚——**不是**新的生产输入。与 `TemperatureModel.MeanC` 同性质。
	/// </summary>
	public float MeanMm { get; private set; }

	/// <summary>
	/// **大陆性 e 折长度**（km；**几何尺度 B**，不随 res 变）。
	/// 语义：离岸距离每增加此长度，`(MaritimeFactor − InteriorRatio(lat))` 衰减到 `1/e`。
	/// 标定依据：原实现写"10 跳尺度"，那是在**生产档 res4** 上标的（10 × 45.2 km/跳 ≈ 452 km）。
	///   D-14 已把它从 hop 改成 km（否则同一个"10 跳"在 res4 = 452 km、res1 = 8,364 km）。
	/// ⚠️ **P4-2 不动这个值**：它是几何尺度 B 的既有冻结值，动它会连带移动水文（见 `新债务` 注释）。
	///   这是**占位气候事实的工程标定参数**，正式气候模拟上线后由湿度/环流输送过程取代。
	/// </summary>
	public const float CoastDecayKm = 450f;

	// 纬向年平均降水 LUT（|纬度|度 → **mm/年**；地球（海陆合计）量级）。
	// 形态：ITCZ 峰（0~10°）→ 副热带干槽（20~25°）→ 温带次峰（45~55°）→ 极地低值。
	// 面积加权全球均值 ≈ 1,000 mm/年（地球实测同量级）；由
	//   `scripts/Test/World.Tests/PrecipitationModelTests.GlobalMean_IsEarthLike_ArithmeticMean` 钉住。
	static readonly (float latDeg, float mm)[] ZonalMmBands =
	{
		(0f, 2200f), (10f, 1800f), (20f, 1100f), (25f, 850f), (35f, 900f),
		(45f, 1050f), (55f, 1150f), (65f, 850f), (75f, 450f), (90f, 250f),
	};

	// 深内陆渐近比 LUT（|纬度|度 → 无量纲 ∈ (0,1)；深内陆降水 / 同纬海岸降水）。
	// 物理：副热带下沉气流 ⇒ 内陆极干（撒哈拉/阿塔卡马 ~0.1）；赤道对流性降水 ⇒ 内陆仍湿（刚果 ~0.8）；
	//   中高纬大陆内部（西伯利亚/加拿大）介于其间。
	// ⚠️ 与 `CoastDecayKm` 同类：**工程标定参数**，不是物理常数。
	static readonly (float latDeg, float ratio)[] InteriorRatioBands =
	{
		(0f, 0.80f), (15f, 0.45f), (25f, 0.12f), (35f, 0.30f),
		(50f, 0.45f), (65f, 0.55f), (90f, 0.65f),
	};

	/// <summary>
	/// 生成逐格年降水（只消费 Final 层：`FinalLand` / `FinalDistToCoast`）。**绝不读取 CivSim / Legacy `.mpa`。**
	/// </summary>
	public void Generate(Ball ball, FinalGeography final)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		AnnualMm = new float[n];

		// hop → km：正六边形蜂窝的**几何**关系（相邻格中心距 = √3 × 等积格边长），
		// 随 res 变——这正是"尺度换算"，不是被禁的 `hop × 固定常数`（永久原则 2）。
		// ⚠️ 它是**量级换算**（BFS 路径不沿大圆，逐跳离散 ±25%）；450 km 级衰减长度上
		//    误差约 ±8%，对一个平滑权重可接受。将来若需要精确的"离岸物理距离"，
		//    应在 Final 层加一个**真实距离场**（Dijkstra），而不是在这里提精度。
		double kmPerHop = Math.Sqrt(3.0) * SpatialScale.Of(ball).CellEdgeKm;

		float sum = 0f;
		for (int i = 0; i < n; i++)
		{
			float latDeg = MathF.Abs(MathF.Asin(Math.Clamp(dirs[i].Y, -1f, 1f)) * 180f / MathF.PI);
			float zonalMm = ZonalMm(latDeg);
			bool land = final.FinalLand[i];
			// ★D-14：读 **离岸距离** `FinalDistToCoast`（陆格原读的 `FinalDistToLand` 恒 0）
			float maritime = land ? CoastFactor(final.FinalDistToCoast[i], kmPerHop, latDeg) : 1f;
			float v = zonalMm * maritime;
			AnnualMm[i] = v;
			sum += v;
		}
		MeanMm = sum / n;
	}

	/// <summary>
	/// 纬向年平均降水（**mm/年**；分段线性插值，南北对称）。对**海格**即海面降水量；
	/// 对**陆格**是"若完全海洋性"的基准。
	/// </summary>
	public static float ZonalMm(float latAbsDeg) => InterpLut(ZonalMmBands, latAbsDeg);

	/// <summary>
	/// 深内陆渐近比（无量纲 ∈ (0,1)）：同纬度上"深内陆降水 / 海岸降水"。
	/// 副热带最低（干旱内陆），赤道最高（对流性降水）。
	/// </summary>
	public static float InteriorRatio(float latAbsDeg) => InterpLut(InteriorRatioBands, latAbsDeg);

	/// <summary>
	/// 陆格的**海洋性/大陆性加权系数** ∈ [`InteriorRatio(lat)`, 1]：临海 1.0 → 内陆按 e 折长度
	/// <see cref="CoastDecayKm"/> 衰减 → 趋 <see cref="InteriorRatio"/>。
	/// </summary>
	/// <param name="distToCoastHops">`FinalDistToCoast`（BFS from 海格；陆格 ≥ 1）。</param>
	/// <param name="kmPerHop">本 res 的相邻格中心距（km；随 res 变）。</param>
	/// <param name="latAbsDeg">|纬度|（度）——决定深海性渐近比（副热带内陆最干）。</param>
	/// <remarks>
	/// ★退化解（永久原则 4）：**全陆临海**时 `distToCoastHops ≡ 1` ⇒ 内陆深度 ≡ 0 km
	/// ⇒ `exp(0) = 1` ⇒ 系数 ≡ 1.0 ⇒ **逐点退化到 D-14 修复前的行为**（那时因字段选错恰好恒为 1.0），
	/// 也等价于"没有大陆性、全世界都是海岸"的纯纬向场。由 `PrecipitationModelTests` 钉死。
	/// </remarks>
	public static float CoastFactor(int distToCoastHops, double kmPerHop, float latAbsDeg)
	{
		if (kmPerHop <= 0) throw new ArgumentOutOfRangeException(nameof(kmPerHop), kmPerHop, "相邻格中心距须为正");
		float floor = InteriorRatio(latAbsDeg);
		return floor + (1f - floor) * CoastalPenetration(distToCoastHops, kmPerHop);
	}

	/// <summary>
	/// **海洋性渗透系数** ∈ (0, 1]：海岸格 1.0，内陆按 e 折长度 <see cref="CoastDecayKm"/> 单调衰减。
	/// 与纬度**无关**（纬度只通过 <see cref="InteriorRatio"/> 决定渐近下限）⇒ 便于独立钉住"衰减长度是 km 不是 hop"。
	/// </summary>
	/// <param name="distToCoastHops">`FinalGeography.FinalDistToCoast`（BFS from 海格；陆格 ≥ 1，海格 = 0）。</param>
	/// <param name="kmPerHop">本 res 的相邻格中心距（km；随 res 变）。</param>
	public static float CoastalPenetration(int distToCoastHops, double kmPerHop)
	{
		if (kmPerHop <= 0) throw new ArgumentOutOfRangeException(nameof(kmPerHop), kmPerHop, "相邻格中心距须为正");
		// 海岸格 `FinalDistToCoast = 1` ⇒ −1 归一化成"内陆深度从 0 起算"
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
