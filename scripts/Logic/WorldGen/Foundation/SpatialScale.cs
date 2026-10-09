using System;
using World.H3Grid;    // Ball（H3 球壳数据层）
using World.Utils;           // SphericalFbmNoise
using World.Utils.H3;        // H3（LatLng 单位是弧度）

namespace World.WorldGen;

// 世界生成空间 · **空间尺度 / 单位层**（收口 §07 D-10）。
//
// ── 为什么需要这一层 ────────────────────────────────────────────────────────
// res 只是**采样/离散化精度**，决定"一个格子多大"，**不应改变任何物理量的意义**。
// （历史上曾有三套互不一致的格面积口径，已由本类统一收口。）
//
// ── 三类参数（见到任何参数先问它属于哪一类）───────────────────────────────
//   **A 拓扑尺度**  BFS 距离 / H3 hop / 邻居数 / 图深度  → 用 cell / hop（**与 res 强相关**）
//   **B 几何尺度** 面积 / 距离 / 山脉宽度 / 河长 / 湖面  → 用 km / km² / km³
//   **C 物理尺度** 降水 / 蒸发 / 径流 / 水量 / 侵蚀     → 用 mm / mm·a⁻¹ / km³
// ★**res 只决定"一个格子有多大"；物理参数决定"现实世界有多大"；两者不得混用。**
//
// ── ★验收门槛（永久原则 5）────────────────────────────────────────────────
//   **本类任何新增成员都必须配一条"与独立实现逐点对照"的测试，而不是只看公式自洽。**
//   依据：`DistanceKm` 初版弧度乘了两次 `d2r` ⇒ 距离缩小 57 倍，静态看公式完全正常，
//   是"逐邻居实测"照出来的。对照口径见 `SpatialScaleTests`（面积×4/距离/跳数/守恒）。
//
// ── 面积口径的分工（为什么不止一个）──────────────────────────────────────
//   · `CellAreaKm2`（**等积口径** `4πR²/n`）= 默认。保证 `Σ = TotalAreaKm2` **精确守恒**，
//     零 native 调用、无硬编码表。
//   · `ExactCellAreaKm2(i)`（**H3 精确**）= 最终判读 / 渲染 / 精度敏感场合。
//   · 不要用 `hops × 固定常数` 跨 res 换算距离：H3 边长随 res 变（res4 ≈26.1 km、res1 ≈482.9 km）；
//     `HopDistanceKm` 只在**同一 res 内**有效，且是**有偏近似**。
public sealed class SpatialScale
{
	/// <summary>分辨率（**本项目编号**，0..8）。</summary>
	public int Res { get; }
	/// <summary>该分辨率下的总格数（球壳铺满）。</summary>
	public int CellCount { get; }
	/// <summary>地球总表面积（km²；= 4πR²，R = <see cref="SphericalFbmNoise.EarthRadiusKm"/>）。</summary>
	public double TotalAreaKm2 { get; }
	/// <summary>
	/// **等积口径**的单格平均面积（km²）= <see cref="TotalAreaKm2"/> / <see cref="CellCount"/>。
	/// 这是所有"面积 ⇄ 格数"换算的**唯一默认口径**——选它不是因为它最精确，
	/// 而是因为它让 `Σ(格面积) = 总面积` 严格成立（用 H3 逐格精确值做守恒断言会引入 pentagon 残差）。
	/// </summary>
	public double CellAreaKm2 { get; }
	/// <summary>等积口径的单格等效边长（km；六边形 s = √(2A / 3√3)）。</summary>
	public double CellEdgeKm { get; }
	/// <summary>等积口径的单格等效直径（km；六边形对角距 = 2s）。</summary>
	public double CellDiameterKm { get; }
	/// <summary>等积口径的单格等效半径（km；内切圆 = s·√3/2）。</summary>
	public double CellInradiusKm { get; }

	SpatialScale(int res, int cellCount)
	{
		Res = res;
		CellCount = cellCount;
		TotalAreaKm2 = 4.0 * Math.PI * SphericalFbmNoise.EarthRadiusKm * SphericalFbmNoise.EarthRadiusKm;
		CellAreaKm2 = TotalAreaKm2 / cellCount;
		CellEdgeKm = Math.Sqrt(2.0 * CellAreaKm2 / (3.0 * Math.Sqrt(3.0)));
		CellDiameterKm = 2.0 * CellEdgeKm;
		CellInradiusKm = CellEdgeKm * Math.Sqrt(3.0) / 2.0;
	}

	/// <summary>由 <see cref="Ball"/> 建立尺度口径。</summary>
	public static SpatialScale Of(Ball ball)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		return new SpatialScale(ball.Res, ball.CellIds.Length);
	}

	/// <summary>由分辨率 + 格数建立（无需 Ball；纯标量换算场景，如面板预估）。</summary>
	public static SpatialScale ForRes(int res, int cellCount)
	{
		if (cellCount <= 0) throw new ArgumentOutOfRangeException(nameof(cellCount), cellCount, "格数须为正");
		return new SpatialScale(res, cellCount);
	}

	/// <summary>面积 → 格数（向上取整 ⇒ 覆盖至少该面积）。</summary>
	public int CellsForArea(double areaKm2)
	{
		if (areaKm2 <= 0) return 0;
		return (int)Math.Ceiling(areaKm2 / CellAreaKm2);
	}

	/// <summary>格数 → 面积（等积口径）。</summary>
	public double AreaOfCells(double cellCount) => cellCount * CellAreaKm2;

	/// <summary>
	/// **H3 精确**单格面积（km²）。逐格调用 H3 native；量级为 O(n)，
	/// 大规模循环（如 res5 的 200 万格）请优先用 <see cref="CellAreaKm2"/> 均值口径。
	/// </summary>
	public static double ExactCellAreaKm2(ulong cell) => H3.CellAreaKm2(cell);

	/// <summary>
	/// H3 **官方**"平均六边形面积"（km²；`getHexagonAreaAvgKm2`）——**对照口径**，非默认。
	/// ⚠️ 两个坑：① **编号偏移**：本项目最低层叫 res0（122 格），H3 官方 res0 是 2 格——
	/// 查官方表格必须 **+1**（项目 res1 = 官方 res2）。
	/// ② 与等积口径差 ~0.4%（五边形胞偏大抬高算术平均）。
	/// 默认一律用 <see cref="CellAreaKm2"/>（守恒可证），本方法只用于**交叉校验与判读**。
	/// </summary>
	public static double OfficialHexagonAreaAvgKm2(int res) => H3.GetHexagonAreaAvgKm2(res);

	/// <summary>
	/// 面积恒等式自检：<c>CellAreaKm2 × CellCount == TotalAreaKm2</c>（等积口径的定义即如此）。
	/// 任何"格面积"读数只要不满足它，就说明用了别的口径——测试与判读都应先过这一关。
	/// </summary>
	public bool AreaConserving => Math.Abs(CellAreaKm2 * CellCount - TotalAreaKm2) < 1e-6;

	/// <summary>
	/// 两格之间的**真实大圆距离**（km；H3 `CellToLatLng` + haversine）。
	/// 这是**物理距离口径**——与 hop 无关，跨 res 语义一致。
	/// ⚠️ `H3.LatLng` 的 `Lat`/`Lng` 字段名没有单位后缀，但 H3 C API 返回的是**弧度**。
	/// </summary>
	public static double DistanceKm(ulong cellA, ulong cellB)
	{
		var a = H3.CellToLatLng(cellA);
		var b = H3.CellToLatLng(cellB);
		return GreatCircleKm(a.Lat, a.Lng, b.Lat, b.Lng);
	}

	/// <summary>由两格的球面单位方向算大圆距离（km）——与 <see cref="DistanceKm"/> 同口径，
	/// 但不需要 cell id（本项目多数格子方向已在 <c>Ball.CellDirs</c> 里）。</summary>
	public static double DistanceKm(Godot.Vector3 unitA, Godot.Vector3 unitB)
	{
		(double lat1, double lon1) = ToLatLngRad(unitA);
		(double lat2, double lon2) = ToLatLngRad(unitB);
		return GreatCircleKm(lat1, lon1, lat2, lon2);
	}

	/// <summary>球面单位方向 → (纬度弧度, 经度弧度)。</summary>
	public static (double latRad, double lngRad) ToLatLngRad(Godot.Vector3 unit)
		=> (Math.Asin(Math.Clamp(unit.Y, -1.0, 1.0)),
			Math.Atan2(unit.Z, unit.X));

	/// <summary>
	/// 拓扑跳数 → **有偏近似**的物理距离（km = hops × <see cref="CellEdgeKm"/>）。
	/// ⚠️ 三重不可靠，**不得用于任何需要真实距离的模拟**：
	///   ① 只在**同一 <see cref="Res"/> 内**自洽（换 res 系数就变）；
	///   ② H3 邻接路径不在大圆上（会沿格心连线走，高纬弯折）；
	///   ③ 12 个五边形胞的邻接畸变。
	/// 实测标定（2026-10-04，`--probe`）：真实邻格中心距 / 等积格边长 =
	///   res1 1.78（最短 1.32）/ res2 1.77（1.23）/ res3 1.78（1.41）——
	///   即"典型值 ≈ √3，但离散度达 ±25%"。
	/// 需要真实距离时用 <see cref="DistanceKm"/>（逐对 haversine，误差 &lt;1%）。
	/// </summary>
	public double HopDistanceKm(int hops) => hops * CellEdgeKm;

	/// <summary>
	/// 拓扑跳数 → 距离的**实测包络**（km）。系数由 `--probe` 实测标定（1.2 ~ 2.25），
	/// **不是理论保证**——H3 变长胞 + 五边形畸变使"每跳距离"没有严格上下界。
	/// 用途仅限"这个跳数大致对应多大物理尺度"的**诚实表述**（如 UI 提示、量级排序）。
	/// </summary>
	public (double minKm, double maxKm) HopDistanceKmRange(int hops)
		=> (hops * 1.2 * CellEdgeKm, hops * 2.25 * CellEdgeKm);

	/// <summary>大圆距离（km）。入参一律**弧度**（本项目 H3 与球面方向都是弧度口径）。</summary>
	static double GreatCircleKm(double lat1Rad, double lon1Rad, double lat2Rad, double lon2Rad)
	{
		double dLat = lat2Rad - lat1Rad;
		double dLon = lon2Rad - lon1Rad;
		double s1 = Math.Sin(dLat / 2), s2 = Math.Sin(dLon / 2);
		double a = s1 * s1 + Math.Cos(lat1Rad) * Math.Cos(lat2Rad) * s2 * s2;
		return 2.0 * SphericalFbmNoise.EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
	}
}
