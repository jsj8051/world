using System;
using Godot;

namespace World.Logic;

/// <summary>
/// 切平面场运算：梯度（全工程唯一实现）+ 局部北/东基。
/// 气压梯度与风求解必须共用同一份梯度定义——两处各写一份是"同一概念两处实现"的典型，
/// 一旦其中一处改了投影或归一化，风就会悄悄偏离它声称要跟随的气压场。
///
/// @example
/// <code>
/// var grad = TangentField.Gradient(grid, pressureMonth, i);   // hPa/rad
/// TangentField.GradientField(grid, pressureMonth, destination);
/// </code>
/// </summary>
public static class TangentField
{
	/// <summary>切平面梯度（邻域和口径）：Σ (f_j − f_i)·(r_j − r_i)/|r_j − r_i|，再投影回切平面。
	/// 各向同性网格上 ≈ (邻居数/2)·d·∇f；换算成物理梯度要除以 ½Σ|r_j−r_i|（见 <see cref="WindSolver"/>）。</summary>
	public static Vector3 Gradient(ClimateGrid grid, float[] field, int i)
	{
		var g = Vector3.Zero;
		var r = grid.Dirs[i];
		foreach (int nb in grid.Neighbors[i])
		{
			var edge = grid.Dirs[nb] - r;
			float len = edge.Length();
			if (len < 1e-9f) continue;
			g += (field[nb] - field[i]) * (edge / len);
		}
		g -= r * r.Dot(g);
		return g;
	}

	/// <summary>整场梯度（目标数组由调用方持有，逐月复用同一条缓冲）。</summary>
	public static void GradientField(ClimateGrid grid, float[] field, Vector3[] destination)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (field == null) throw new ArgumentNullException(nameof(field));
		if (destination == null) throw new ArgumentNullException(nameof(destination));
		if (field.Length != grid.Count) throw new ArgumentException("场与格数不一致", nameof(field));
		if (destination.Length != grid.Count) throw new ArgumentException("目标场与格数不一致", nameof(destination));

		for (int i = 0; i < grid.Count; i++) destination[i] = Gradient(grid, field, i);
	}

	/// <summary>该点的北向切向量（极点退化时取任意切向）。</summary>
	public static Vector3 NorthTangent(Vector3 r)
	{
		var up = Vector3.Up;
		var north = up - r * r.Dot(up);
		if (north.LengthSquared() < 1e-9f)
		{
			var axis = MathF.Abs(r.X) < 0.9f ? Vector3.Right : Vector3.Forward;
			north = axis - r * r.Dot(axis);
		}
		return north.Normalized();
	}

	/// <summary>该点的东向切向量（由北向基旋转 90°）。</summary>
	public static Vector3 EastTangent(Vector3 r, Vector3 north) => r.Cross(north).Normalized();
}
