using System;

namespace World.Data;

/// <summary>固定球面格的双精度有向面积。格边为大圆弧，顶点须位于同一半球。</summary>
static class SpherePolygonOps
{
	const double MinCosine = 0.08715574274765817; // cos(85°)
	static double Dot(SpherePointD a, SpherePointD b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

	/// <summary>有向球面面积（球面度）：逆时针（从球外看）为正；多边形须简单且位于半球内。</summary>
	public static double SignedAreaSteradians(SpherePointD[] ring)
	{
		SpherePointD center = Center(ring);
		foreach (SpherePointD p in ring)
			if (Dot(center, p) < MinCosine)
				throw new NotSupportedException("球面多边形超出当前面积核支持的范围");

		double excess = 0;
		for (int i = 1; i + 1 < ring.Length; i++)
			excess += TriangleExcess(ring[0], ring[i], ring[i + 1]);
		return excess;
	}

	/// <summary>面积（km²）；负绕序面积没有物理意义，这里取绝对值。</summary>
	public static double AreaKm2(SpherePointD[] ring, double radiusKm)
	{
		RequirePositive(radiusKm, nameof(radiusKm));
		return Math.Abs(SignedAreaSteradians(ring)) * radiusKm * radiusKm;
	}

	/// <summary>环的球面质心方向（顶点求和后归一化；近对径时抛）。</summary>
	static SpherePointD Center(SpherePointD[] ring)
	{
		RequireRing(ring);
		double x = 0, y = 0, z = 0;
		foreach (SpherePointD p in ring) { x += p.X; y += p.Y; z += p.Z; }
		double length = Math.Sqrt(x * x + y * y + z * z);
		if (!(length > 1e-12)) throw new NotSupportedException("多边形顶点近对径，无法建立投影中心。");
		return new SpherePointD(x / length, y / length, z / length);
	}

	static double TriangleExcess(SpherePointD a, SpherePointD b, SpherePointD c)
	{
		double triple = a.X * (b.Y * c.Z - b.Z * c.Y)
			+ a.Y * (b.Z * c.X - b.X * c.Z)
			+ a.Z * (b.X * c.Y - b.Y * c.X);
		double denominator = 1 + a.X * b.X + a.Y * b.Y + a.Z * b.Z
			+ b.X * c.X + b.Y * c.Y + b.Z * c.Z
			+ c.X * a.X + c.Y * a.Y + c.Z * a.Z;
		return 2 * Math.Atan2(triple, denominator);
	}

	static void RequireRing(SpherePointD[] ring)
	{
		if (ring == null) throw new ArgumentNullException(nameof(ring));
		if (ring.Length < 3) throw new ArgumentException($"球面多边形至少 3 个顶点，实得 {ring.Length}", nameof(ring));
		foreach (SpherePointD p in ring)
		{
			if (p == null) throw new ArgumentException("球面多边形含空顶点", nameof(ring));
			p.Validate();
		}
	}

	static void RequirePositive(double value, string name)
	{
		if (!(value > 0) || !double.IsFinite(value))
			throw new ArgumentOutOfRangeException(name, $"{name} 必须是有限正数");
	}
}
