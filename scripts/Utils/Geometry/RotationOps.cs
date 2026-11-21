using System;

namespace World.Utils;

/// <summary>双精度主动旋转；Compose(after, before) 先执行 before，再执行 after。</summary>
public static class RotationOps
{
	/// <summary>单位四元数的逆旋转。</summary>
	public static RotationD Inverse(RotationD rotation)
		=> new(-rotation.X, -rotation.Y, -rotation.Z, rotation.W);

	/// <summary>单位轴与任意有限弧度构造旋转，不引入时间或速度。</summary>
	/// <example><code>var turn = RotationOps.FromAxisAngle(new SpherePointD(0, 1, 0), Math.PI);</code></example>
	public static RotationD FromAxisAngle(SpherePointD axis, double angleRadians)
	{
		if (axis == null) throw new ArgumentNullException(nameof(axis));
		if (!double.IsFinite(angleRadians)) throw new ArgumentOutOfRangeException(nameof(angleRadians));
		double half = Math.IEEERemainder(angleRadians, 2 * Math.PI) / 2;
		double sin = Math.Sin(half);
		return Normalize(axis.X * sin, axis.Y * sin, axis.Z * sin, Math.Cos(half));
	}

	/// <summary>组合两个姿态并归一化，避免多轮乘法累积浮点长度误差。</summary>
	/// <example><code>var pose = RotationOps.Compose(turn, previous);</code></example>
	public static RotationD Compose(RotationD after, RotationD before)
	{
		if (after == null) throw new ArgumentNullException(nameof(after));
		if (before == null) throw new ArgumentNullException(nameof(before));
		return Normalize(
			after.W * before.X + after.X * before.W + after.Y * before.Z - after.Z * before.Y,
			after.W * before.Y - after.X * before.Z + after.Y * before.W + after.Z * before.X,
			after.W * before.Z + after.X * before.Y - after.Y * before.X + after.Z * before.W,
			after.W * before.W - after.X * before.X - after.Y * before.Y - after.Z * before.Z);
	}

	/// <summary>把单位方向从参考坐标旋转到当前坐标。</summary>
	/// <example><code>var direction = RotationOps.Apply(pose, referenceDirection);</code></example>
	public static SpherePointD Apply(RotationD rotation, SpherePointD direction)
	{
		if (rotation == null) throw new ArgumentNullException(nameof(rotation));
		if (direction == null) throw new ArgumentNullException(nameof(direction));
		var (x, y, z) = Apply(rotation, direction.X, direction.Y, direction.Z);
		double length = Math.Sqrt(x * x + y * y + z * z);
		return new SpherePointD(x / length, y / length, z / length);
	}

	/// <summary>旋转笛卡尔向量，保留长度。</summary>
	public static (double X, double Y, double Z) Apply(RotationD rotation, double x, double y, double z)
	{
		double tx = 2 * (rotation.Y * z - rotation.Z * y);
		double ty = 2 * (rotation.Z * x - rotation.X * z);
		double tz = 2 * (rotation.X * y - rotation.Y * x);
		return (x + rotation.W * tx + rotation.Y * tz - rotation.Z * ty,
			y + rotation.W * ty + rotation.Z * tx - rotation.X * tz,
			z + rotation.W * tz + rotation.X * ty - rotation.Y * tx);
	}

	static RotationD Normalize(double x, double y, double z, double w)
	{
		double length = Math.Sqrt(x * x + y * y + z * z + w * w);
		return new RotationD(x / length, y / length, z / length, w / length);
	}
}
