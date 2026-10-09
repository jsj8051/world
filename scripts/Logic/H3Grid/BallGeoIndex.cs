using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils.H3;        // H3.CellToLatLng / H3.LatLngToCell

namespace World.H3Grid;

// H3 球面网格数据层 · 逐格经纬度只读索引。
// ★不是新事实源：唯一数据源仍是 `Ball.CellDirs`，本类只缓存其纯函数值
//   （lat = asin(dir.Y)、lng = atan2(dir.Z, dir.X)），故不会与 Ball 失同步。
// 收口：① 逐格经纬度数组（原先多处各自内联 asin/atan2）；
//       ② `CellAt`（原先 FinalSpatialIndex 与 CellQuery 各一份相同实现）。
// ★2026-10-09 与 `Ball` 同批：ns `World.Spatial` → `World.H3Grid`，目录 → `scripts/Logic/H3Grid/`。
/// <summary>
/// 逐格经纬度只读索引（`Ball.CellDirs` 的纯函数缓存；与 <c>ball.CellIds</c> 逐位对齐）。
/// </summary>
public sealed class BallGeoIndex
{
	/// <summary>逐格纬度（**弧度**）。</summary>
	public float[] LatRad { get; }
	/// <summary>逐格经度（**弧度**）。</summary>
	public float[] LngRad { get; }
	/// <summary>逐格纬度（**度**；= LatRad × 180/π）。★勿与弧度混用。</summary>
	public float[] LatDeg { get; }
	/// <summary>逐格经度（**度**；= LngRad × 180/π）。★勿与弧度混用。</summary>
	public float[] LngDeg { get; }
	/// <summary>格数（= <c>ball.CellIds.Length</c>）。</summary>
	public int Count { get; }

	/// <summary>由 <see cref="Ball"/> 建立索引（构造期一次算好，之后只读）。</summary>
	public BallGeoIndex(Ball ball)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		var dirs = ball.CellDirs;
		Count = dirs.Length;
		LatRad = new float[Count];
		LngRad = new float[Count];
		LatDeg = new float[Count];
		LngDeg = new float[Count];
		const float radToDeg = 180f / MathF.PI;
		for (int i = 0; i < Count; i++)
		{
			// H3 官方球面约定（CoordUtil.LatLngToSphere 的逆）：Y = sin(lat)，atan2(Z, X) = lng。
			float lat = MathF.Asin(Math.Clamp(dirs[i].Y, -1f, 1f));
			float lng = MathF.Atan2(dirs[i].Z, dirs[i].X);
			LatRad[i] = lat;
			LngRad[i] = lng;
			LatDeg[i] = lat * radToDeg;
			LngDeg[i] = lng * radToDeg;
		}
	}

	/// <summary>格下标 i 的 |纬度|（度）。</summary>
	public float LatAbsDeg(int i) => MathF.Abs(LatDeg[i]);

	/// <summary>球面单位方向 → 本 res 格下标（拾取/任意点查询的格粒度换算）。</summary>
	public static int CellAt(Ball ball, Vector3 unitDir)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		float lat = MathF.Asin(Math.Clamp(unitDir.Y, -1f, 1f));
		float lng = MathF.Atan2(unitDir.Z, unitDir.X);
		return ball.CellIndexOf(H3.LatLngToCell(new LatLng(lat, lng), ball.Res));
	}
}
