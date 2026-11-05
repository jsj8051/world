using System;
using Godot;

namespace World.Utils;

/// <summary>
/// 单位球面上的一点：位置向量 + 球面坐标 + 该点的局部切基。
/// 约定与 <see cref="CoordUtil"/> 一致：极轴 +Y、θ 从北极起算、φ = atan2(Z, X)。
/// <see cref="South"/> 沿经线向南（θ 增大），<see cref="East"/> 沿纬线向东（φ 增大）；
/// 两向量都与 <see cref="Dir"/> 正交。
/// </summary>
public readonly struct SpherePoint
{
	/// <summary>点的位置向量（单位向量）。</summary>
	public readonly Vector3 Dir;
	/// <summary>极角（从北极起算，0…π，弧度）。</summary>
	public readonly float Theta;
	/// <summary>方位角（0…2π，弧度）。</summary>
	public readonly float Phi;
	/// <summary>该点切平面内的“向南”单位向量（θ 增大方向）。</summary>
	public readonly Vector3 South;
	/// <summary>该点切平面内的“向东”单位向量（φ 增大方向）。</summary>
	public readonly Vector3 East;

	SpherePoint(Vector3 dir, float theta, float phi, Vector3 south, Vector3 east)
	{
		Dir = dir;
		Theta = theta;
		Phi = phi;
		South = south;
		East = east;
	}

	/// <summary>由单位方向建点（<paramref name="dir"/> 须是单位向量）。</summary>
	public static SpherePoint At(Vector3 dir)
	{
		var (theta, phi) = CoordUtil.ToEuler(dir);
		float cosT = MathF.Cos(theta), sinT = MathF.Sin(theta);
		float cosP = MathF.Cos(phi), sinP = MathF.Sin(phi);
		return new SpherePoint(
			dir, theta, phi,
			new Vector3(cosT * cosP, -sinT, cosT * sinP),
			new Vector3(-sinP, 0f, cosP));
	}

	/// <summary>切平面内、从南向起算方位 <paramref name="azimuthFromSouth"/> 的单位方向。</summary>
	public Vector3 Direction(float azimuthFromSouth) =>
		MathF.Cos(azimuthFromSouth) * South + MathF.Sin(azimuthFromSouth) * East;
}
