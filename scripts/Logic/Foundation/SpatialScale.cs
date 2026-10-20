using System;
using World.H3Grid;
using World.Utils;
using World.Utils.H3;

namespace World.Logic;

// 空间尺度 / 单位层：res 只决定「一格多大」，不改变任何物理量的意义。
// 参数先分三类、不得混用：A 拓扑（BFS 距离 / H3 hop / 邻居数 / 图深度 → cell / hop，与 res 强相关）；
// B 几何（面积 / 距离 / 山脉宽度 / 河长 / 湖面 → km / km² / km³）；
// C 物理（降水 / 蒸发 / 径流 / 水量 / 侵蚀 → mm / mm·a⁻¹ / km³）。
//
// 世界大小由「res4 每格 = 5 km²」定义，不从「地球半径」反推：288,122 格 → 总面积 1,440,610 km²
// → R = √(A/4π) ≈ 338.586 km（周长 ≈ 2,127 km）、单格等效边长 1.387 km、邻格中心距 2.469 km。
// 落点是 `SphericalFbmNoise.WorldRadiusKm`：噪声波长、面积、距离都经它换算。
// 推论：任何噪声场最细的倍频层都要 ≥ 3 倍邻格中心距（≈ 7.4 km），更细的层在六边形格上会混叠成条纹。
//
// 面积口径两份：`CellAreaKm2` 等积（4πR²/n，默认，Σ 精确守恒、零 native 调用）与 `ExactCellAreaKm2`
// （H3 逐格精确，判读 / 渲染 / 精度敏感场合）。`HopDistanceKm` 只在同一 res 内有效，且是有偏近似。

public sealed class SpatialScale
{
	/// <summary>分辨率（本项目编号，0..8；编号即 H3 res）。</summary>
	public int Res { get; }
	/// <summary>该分辨率下的总格数（球壳铺满）。</summary>
	public int CellCount { get; }
	/// <summary>世界总表面积（km²；= 4πR²，R = <see cref="SphericalFbmNoise.WorldRadiusKm"/>）。</summary>
	public double TotalAreaKm2 { get; }
	/// <summary>等积口径的单格平均面积（km²）= 总面积 / 格数。面积 ⇄ 格数的唯一默认口径：
	/// 选它是因为 Σ(格面积) = 总面积 严格成立（用 H3 逐格精确值做守恒断言会引入 pentagon 残差）。</summary>
	public double CellAreaKm2 { get; }
	/// <summary>等积口径的单格等效边长（km；六边形 s = √(2A / 3√3)）。</summary>
	public double CellEdgeKm { get; }
	/// <summary>等积口径的单格等效直径（km；六边形对角距 = 2s）。</summary>
	public double CellDiameterKm { get; }
	/// <summary>等积口径的单格等效半径（km；内切圆 = s·√3/2）。</summary>
	public double CellInradiusKm { get; }

	/// <summary>邻格中心距 / 等积格边长的实测典型比值（res1~res3 实测 1.77~1.78，离散度 ±25%、
	/// 最短 1.23 ⇒ 是典型值，不是严格常数）。</summary>
	public const double NeighborCenterRatio = 1.78;

	/// <summary>邻格中心距（km）= <see cref="CellEdgeKm"/> × <see cref="NeighborCenterRatio"/>。
	/// 「带宽 km → 跳数」的折算走它（直接用 `CellEdgeKm` 折算会把带宽低估约 1.8 倍）。</summary>
	public double NeighborCenterKm => CellEdgeKm * NeighborCenterRatio;

	SpatialScale(int res, int cellCount)
	{
		Res = res;
		CellCount = cellCount;
		TotalAreaKm2 = 4.0 * Math.PI * SphericalFbmNoise.WorldRadiusKm * SphericalFbmNoise.WorldRadiusKm;
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

	/// <summary>H3 精确的单格面积（km²）：逐格调 native，量级 O(n)。大规模循环（如 res4 的 28.8 万格）
	/// 请优先用 <see cref="CellAreaKm2"/> 均值口径。</summary>
	public static double ExactCellAreaKm2(ulong cell) => H3.CellAreaKm2(cell);

	/// <summary>
	/// H3 官方的平均六边形面积（km²；<c>getHexagonAreaAvgKm2</c>）——对照口径，非默认。
	/// 入参是 H3 res（res0 = 122 格、res4 = 288,122 格），可直接对官方表。
	/// 与等积口径差约 0.4%（五边形胞偏大抬高算术平均）；默认一律用 <see cref="CellAreaKm2"/>，本方法只做交叉校验与判读。
	/// </summary>
	public static double OfficialHexagonAreaAvgKm2(int res) => H3.GetHexagonAreaAvgKm2(res);

	/// <summary>面积恒等式自检：<c>CellAreaKm2 × CellCount == TotalAreaKm2</c>（等积口径的定义即如此）。
	/// 任何「格面积」读数不满足它，就说明用了别的口径。</summary>
	public bool AreaConserving => Math.Abs(CellAreaKm2 * CellCount - TotalAreaKm2) < 1e-6;

	/// <summary>两格之间的真实大圆距离（km；H3 <c>CellToLatLng</c> + haversine）。这是物理距离口径，
	/// 与 hop 无关、跨 res 语义一致。<c>H3.LatLng</c> 的 <c>Lat</c>/<c>Lng</c> 没有单位后缀，但 H3 C API 返回弧度。</summary>
	public static double DistanceKm(ulong cellA, ulong cellB)
	{
		var a = H3.CellToLatLng(cellA);
		var b = H3.CellToLatLng(cellB);
		return GreatCircleKm(a.Lat, a.Lng, b.Lat, b.Lng);
	}

	/// <summary>由两格的球面单位方向算大圆距离（km），与 <see cref="DistanceKm"/> 同口径但不需要 cell id
	/// （本项目多数格子方向已在 <c>Ball.CellDirs</c> 里）。</summary>
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

	/// <summary>拓扑跳数 → 有偏近似的物理距离（km = hops × <see cref="CellEdgeKm"/>）。
	/// 不得用于任何需要真实距离的模拟：只在同一 <see cref="Res"/> 内自洽，H3 邻接路径不在大圆上（高纬弯折），
	/// 12 个五边形胞的邻接也畸变。需要真实距离时用 <see cref="DistanceKm"/>（逐对 haversine，误差 &lt;1%）。</summary>
	public double HopDistanceKm(int hops) => hops * CellEdgeKm;

	/// <summary>拓扑跳数 → 距离的实测包络（km；系数 1.2 ~ 2.25 由实测标定，不是理论上下界——
	/// H3 变长胞 + 五边形畸变使「每跳距离」没有严格边界）。
	/// 用途仅限「这个跳数大致对应多大物理尺度」的诚实表述（如 UI 提示、量级排序）。</summary>
	public (double minKm, double maxKm) HopDistanceKmRange(int hops)
		=> (hops * 1.2 * CellEdgeKm, hops * 2.25 * CellEdgeKm);

	/// <summary>大圆距离（km）。入参一律弧度（本项目 H3 与球面方向都是弧度口径）。</summary>
	static double GreatCircleKm(double lat1Rad, double lon1Rad, double lat2Rad, double lon2Rad)
	{
		double dLat = lat2Rad - lat1Rad;
		double dLon = lon2Rad - lon1Rad;
		double s1 = Math.Sin(dLat / 2), s2 = Math.Sin(dLon / 2);
		double a = s1 * s1 + Math.Cos(lat1Rad) * Math.Cos(lat2Rad) * s2 * s2;
		return 2.0 * SphericalFbmNoise.WorldRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
	}
}
