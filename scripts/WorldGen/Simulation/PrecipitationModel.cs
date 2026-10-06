using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Spatial;    // Ball（H3 球壳数据层）

namespace World.WorldGen;

// 世界生成空间 · 降水模型（River 2A，决策 08 v2 §River2A——World Simulation 的**输入/状态**，
//   不是地貌生成输入、不进 Placement 层、不参与 HeightComposer）：
//   降水 = **Final 世界事实的函数**（纬度带 × 海岸加权）——只消费 Final 层
//   （FinalDistToCoast），与气候模拟（将来按湿度/风向细化）不冲突：本模型是水文驱动的
//   第一版输入，气候上线后被其输出替换（口径：年降水 mm）。
// 纬度带（地球相对量级，对称）：赤道多雨（ITCZ）→ 副热带干燥（±25-35°）→ 温带次多
//   （±45-60°）→ 极地少。分段 LUT + 线性插值——直白可控，参数后续接气候输出。
// 海岸加权：临海多雨、内陆衰减（e 折长度 <see cref="CoastDecayKm"/>）。
// ★D-14（2026-10-04 修复，登记在 §9 债务表 / §13.2）——两处**独立**缺陷：
//   ① **字段接线反了**：原读 `FinalDistToLand`（BFS **from 陆格** ⇒ 陆格**恒 0**）
//      ⇒ coastFactor 恒 = 1.0，注释承诺的"临海多雨、内陆衰减"**从未生效**，
//      降水实为纯纬度带函数。陆格的离岸距离是 `FinalDistToCoast`（BFS **from 海格**）。
//   ② **尺度口径错了**：原写 `exp(-hops/10)`，把 **hop 当物理距离**用 ⇒ 同一个
//      "10 跳"在 res4 = 452 km、res1 = 8,364 km（**差 18×**），违反永久原则
//      "res 只决定一个格子多大，物理参数决定现实世界多大；禁止 hop × 固定常数当物理距离"。
//      衰减长度现按 **km**（几何尺度 B）冻结，hop → km 走 `SpatialScale`（随 res 变）。
//      取 450 km = 原"10 跳"在**生产档 res4** 的物理含义 ⇒ res4 行为逐点保持，
//      res1~res3 才第一次得到同样的物理含义。
/// <summary>
/// 降水模型：纬度带 × 海岸加权 → 逐格年降水（mm）。
/// </summary>
public sealed class PrecipitationModel
{
	/// <summary>逐格年降水（mm；相对量级 0~2400，气候模拟上线前为水文驱动输入）。</summary>
	public float[] AnnualMm { get; private set; } = Array.Empty<float>();
	/// <summary>全球均值（mm；河流径流阈值换算用）。</summary>
	public float MeanMm { get; private set; }

	public const float MaxMm = 2400f;

	/// <summary>
	/// ★大陆性衰减的 **e 折长度**（km；**几何尺度 B**，不随 res 变）。
	/// 标定依据：原实现写"10 跳尺度"，那是在**生产档 res4** 上标的（10 × 45.2 km/跳 ≈ 452 km）。
	///   冻结成 km 后 res4 保持既有量级，res1~res3 才得到同一物理含义。
	/// ⚠️ 这不是"现实世界的大陆性物理常数"，是**占位降水场的工程标定参数**——
	///   与 `RiverThresholdAreaKm2` 同类；气候模拟上线后由湿度/风向输送过程取代。
	/// </summary>
	public const float CoastDecayKm = 450f;

	/// <summary>内陆极深处相对海岸的降水系数下限（0.6 = 大陆性干燥倾向）。</summary>
	public const float ContinentalFloor = 0.6f;

	// 纬度带 LUT（|纬度|度 → 相对系数；地球 ITCZ/副热带/温带/极地 量级）
	private static readonly (float latDeg, float factor)[] LatBands =
	{
		(0f, 1.00f), (15f, 0.80f), (25f, 0.40f), (35f, 0.50f),
		(45f, 0.65f), (60f, 0.55f), (80f, 0.30f), (90f, 0.30f),
	};

	/// <summary>生成逐格年降水（只消费 Final 层：`FinalDistToCoast`）。</summary>
	public void Generate(Ball ball, FinalGeography final)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;
		AnnualMm = new float[n];

		// hop → km：正六边形蜂窝的**几何**关系（相邻格中心距 = √3 × 等积格边长），
		// 随 res 变——这正是"尺度换算"，不是被禁的 `hop × 固定常数`。
		// ⚠️ 它是**量级换算**（BFS 路径不沿大圆，逐跳离散 ±25%）；10 跳级衰减长度上
		//    误差约 ±8%，对一个平滑权重可接受。将来若需要精确的"离岸物理距离"，
		//    应在 Final 层加一个**真实距离场**（Dijkstra），而不是在这里提精度。
		double kmPerHop = Math.Sqrt(3.0) * SpatialScale.Of(ball).CellEdgeKm;

		float sum = 0f;
		for (int i = 0; i < n; i++)
		{
			float latDeg = MathF.Abs(MathF.Asin(Math.Clamp(dirs[i].Y, -1f, 1f)) * 180f / MathF.PI);
			float latFactor = LatFactor(latDeg);
			bool land = final.FinalLand[i];
			// ★D-14：读 **离岸距离** `FinalDistToCoast`（陆格原读的 `FinalDistToLand` 恒 0）
			float coastFactor = land ? CoastFactor(final.FinalDistToCoast[i], kmPerHop) : 1f;
			float v = MaxMm * latFactor * coastFactor;
			AnnualMm[i] = v;
			sum += v;
		}
		MeanMm = sum / n;
	}

	/// <summary>
	/// 陆格的**海岸/大陆性加权系数**：临海 1.0 → 内陆按 e 折长度
	/// <see cref="CoastDecayKm"/> 衰减 → 趋 <see cref="ContinentalFloor"/>。
	/// </summary>
	/// <param name="distToCoastHops">`FinalDistToCoast`（BFS from 海格；陆格 ≥ 1）。</param>
	/// <param name="kmPerHop">本 res 的相邻格中心距（km；随 res 变）。</param>
	/// <remarks>
	/// ★退化解（永久原则 4）：**全陆临海**时 `distToCoastHops ≡ 1` ⇒ 内陆深度 ≡ 0 km
	/// ⇒ 系数 ≡ 1.0 ⇒ **逐点退化到 D-14 修复前的行为**（那时因字段选错恰好恒为 1.0）。
	/// 这条退化解由 `PrecipitationModelTests` 钉死——它同时说明：修复前的"恒 1.0"
	/// 不是设计意图，而是"全世界都成了海岸"这个退化的巧合。
	/// </remarks>
	public static float CoastFactor(int distToCoastHops, double kmPerHop)
	{
		if (kmPerHop <= 0) throw new ArgumentOutOfRangeException(nameof(kmPerHop), kmPerHop, "相邻格中心距须为正");
		// 陆格最小 FinalDistToCoast = 1（紧邻海格）⇒ −1 归一化成"内陆深度从 0 起算"
		double inlandKm = Math.Max(0, distToCoastHops - 1) * kmPerHop;
		return ContinentalFloor + (1f - ContinentalFloor) * (float)Math.Exp(-inlandKm / CoastDecayKm);
	}

	/// <summary>纬度带系数（分段线性插值，对称）。</summary>
	public static float LatFactor(float latAbsDeg)
	{
		float x = Math.Clamp(latAbsDeg, 0f, 90f);
		for (int k = 0; k < LatBands.Length - 1; k++)
		{
			var (lat0, f0) = LatBands[k];
			var (lat1, f1) = LatBands[k + 1];
			if (x <= lat1)
				return f0 + (f1 - f0) * (x - lat0) / (lat1 - lat0);
		}
		return LatBands[^1].factor;
	}
}
