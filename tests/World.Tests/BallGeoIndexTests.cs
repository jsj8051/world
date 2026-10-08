using System;
using NUnit.Framework;
using Godot;
using World.Spatial;
using World.Utils.H3;    // H3.CellToLatLng / H3.LatLngToCell（独立口径）

namespace World.Tests;

/// <summary>
/// 世界生成空间 · **逐格经纬度索引**护栏（`BallGeoIndex`，用户 2026-10-07 拍板全局收口）。
///
/// 本类钉三件事：
///   ① `BallGeoIndex` 的逐格经纬度 == `H3.CellToLatLng(ball.CellIds[i])`（H3 官方 native 值，
///      **独立实现对照**，永久原则 5）；
///   ② 弧度/度口径不串（项目曾因弧度当度用使距离缩小 57 倍）；
///   ③ `CellAt` 的 dir→cell 自反（该实现现由 `FinalSpatialIndex` / `CellQuery` 共用，
///      自反失败会同时击穿两处拾取/查询）。
/// 纪律（同 SpatialScaleTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class BallGeoIndexTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(2, 1f));   // res2：5,882 格，含五边形与高纬
	static Ball Ball => SharedBall.Value;

	[Test]
	public void Ball_ComposesGeo_AlignedWithCellDirs()
	{
		// ★组合关系钉：`Ball.Geo` 是 `Ball` 的一部分（构造期建立），必须与同一 `Ball` 的
		//   CellDirs/CellIds 逐位对齐——防止"Ball 换了但 Geo 没换"或独立构造导致的错位。
		var b = new Ball(2, 1f);
		Assert.That(b.Geo, Is.Not.Null, "Ball 必须组合 BallGeoIndex（Geo 随 Ball 同生共死）");
		Assert.That(b.Geo.Count, Is.EqualTo(b.CellIds.Length), "Geo 长度须与 CellIds 逐位对齐");
		// 抽查：Geo 的经纬度与同一 Ball 的 CellDirs 反算一致（自洽，非跨实例）
		for (int i = 0; i < b.Geo.Count; i += 97)
		{
			float lat = MathF.Asin(Math.Clamp(b.CellDirs[i].Y, -1f, 1f));
			Assert.That(b.Geo.LatRad[i], Is.EqualTo(lat).Within(1e-6f), $"格 {i} Geo 来自另一个 Ball？");
		}
	}

	[Test]
	public void CellLatLng_MatchesH3Native_Exactly()
	{
		// ★独立实现对照：本索引由 CellDirs 反算，H3.CellToLatLng 由 native 直出。
		// 球面约定（Y=北、x=cosφcosλ、z=cosφsinλ）若错位，这里会暴露为数百 km / 数十度。
		// 容差 1e-5 rad ≈ 0.0006° ≈ 64 m：CellDirs 是 float32（Godot Vector3），
		// asin/atan2 会放大末位误差——量级上仍与"口径错位（~0.1 rad）"差 4~5 个数量级。
		var geo = new BallGeoIndex(Ball);
		double worstLat = 0, worstLng = 0;
		for (int i = 0; i < geo.Count; i++)
		{
			var native = H3.CellToLatLng(Ball.CellIds[i]);
			worstLat = Math.Max(worstLat, Math.Abs(geo.LatRad[i] - native.Lat));
			worstLng = Math.Max(worstLng, Math.Abs(geo.LngRad[i] - native.Lng));
		}
		Assert.That(worstLat, Is.LessThan(1e-5),
			$"纬度弧度最大差 {worstLat:E2}（应 <1e-5 rad ≈ 64 m，仅 float32 精度）——球面反算口径与 H3 native 不一致");
		Assert.That(worstLng, Is.LessThan(1e-5),
			$"经度弧度最大差 {worstLng:E2}（应 <1e-5 rad）——atan2(Z,X) 口径与 H3 native 不一致");
	}

	[Test]
	public void DegAndRadCalibers_DoNotMix()
	{
		// ① 度 = 弧度 × 180/π（逐格恒等）
		// ② 纬度范围 [−90,90]、经度范围 (−180,180] 有界
		// ③ 抽样验证量纲：中纬度格度的绝对值应在 [0,90]，且不等于弧度值（除非恰 0）
		var geo = new BallGeoIndex(Ball);
		const float radToDeg = 180f / MathF.PI;
		int nonZero = 0;
		for (int i = 0; i < geo.Count; i++)
		{
			Assert.That(geo.LatDeg[i], Is.EqualTo(geo.LatRad[i] * radToDeg).Within(1e-4f),
				$"格 {i}：LatDeg 必须是 LatRad×180/π 的派生（防弧度/度混用）");
			Assert.That(geo.LatDeg[i], Is.InRange(-90f, 90f), $"格 {i} 纬度越界：{geo.LatDeg[i]}");
			Assert.That(geo.LngDeg[i], Is.InRange(-180.0001f, 180.0001f), $"格 {i} 经度越界：{geo.LngDeg[i]}");
			if (MathF.Abs(geo.LatDeg[i]) > 1f) nonZero++;
		}
		// 抽样量纲检查：|纬度| > 1° 的格，其度值必显著大于弧度值（×57.3），
		// 若有人误把弧度当度返回，这里会因 |度| < 0.02 而全数落在 [0,1°] 区间外不成立
		Assert.That(nonZero, Is.GreaterThan(geo.Count / 2),
			"应有超过半数格 |纬度| > 1°——若接近 0 说明返回的是弧度被当成了度（57 倍事故模式）");
	}

	[Test]
	public void CellAt_RoundTrips_EveryCell()
	{
		// 任意方向 → 格下标：用每格格心方向查回应是自身（退化解：恒等映射）。
		var geo = new BallGeoIndex(Ball);
		int mismatches = 0;
		for (int i = 0; i < geo.Count; i++)
			if (BallGeoIndex.CellAt(Ball, Ball.CellDirs[i]) != i) mismatches++;
		Assert.That(mismatches, Is.Zero,
			$"格心方向查格自反失败 {mismatches} 格——CellAt 的 dir→LatLng→cell 链与 Ball 格表不一致");
	}

	[Test]
	public void Alignment_WithCellIds_And_DegRangeSignature()
	{
		// 与 CellIds 逐位对齐 + 全球纬度分布的截面签名（赤道附近格密度最高 ⇒ 中位 |纬度| 应偏小，
		// 但不应为 0）——防止"数组长度对但内容全 0"这类静默失败。
		var geo = new BallGeoIndex(Ball);
		Assert.That(geo.Count, Is.EqualTo(Ball.CellIds.Length), "索引长度须与 CellIds 逐位对齐");
		var latAbs = new System.Collections.Generic.List<float>(geo.Count);
		for (int i = 0; i < geo.Count; i++) latAbs.Add(geo.LatAbsDeg(i));
		latAbs.Sort();
		float median = latAbs[latAbs.Count / 2];
		Assert.That(median, Is.GreaterThan(10f).And.LessThan(45f),
			$"全球 |纬度| 中位数 {median:F1}°（应在 10–45°——0 说明未计算，90 说明坐标系错）");
	}
}
