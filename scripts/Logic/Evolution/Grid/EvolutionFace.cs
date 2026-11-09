using System;
using System.Collections.Generic;
using World.Data;

namespace World.Logic;

/// <summary>一段共享大圆弧。折边由多段弧表示，流量只计算一次并由两格共用。</summary>
internal readonly record struct EvolutionFace(int A, int B, double X, double Y, double Z,
	SpherePointD Start, SpherePointD End, SpherePointD Outward, double LengthKm, double CosAngle)
{
	public double Rate(SphereVectorD omega) => X * omega.X + Y * omega.Y + Z * omega.Z;

	public static EvolutionFace[] Build(SpherePointD[][] cells, int[][] neighbors, double radiusKm, double tolerance)
	{
		var faces = new List<EvolutionFace>(3 * cells.Length);
		for (int a = 0; a < cells.Length; a++)
		{
			SpherePointD[] ring = cells[a];
			for (int i = 0; i < ring.Length; i++)
			{
				SpherePointD start = ring[i], end = ring[(i + 1) % ring.Length];
				int b = FindNeighbor(cells, neighbors[a], start, end, tolerance);
				if (b < 0 && neighbors[a].Length != 0)
					throw new ArgumentException($"格 {a} 的边 {i} 缺少共享邻格");
				if (b > a) faces.Add(Integrate(a, b, start, end, radiusKm));
			}
		}
		return faces.ToArray();
	}

	static int FindNeighbor(SpherePointD[][] cells, int[] neighbors, SpherePointD start, SpherePointD end, double tolerance)
	{
		foreach (int neighbor in neighbors)
		{
			SpherePointD[] other = cells[neighbor];
			for (int j = 0; j < other.Length; j++)
				if (Same(other[j], end, tolerance) && Same(other[(j + 1) % other.Length], start, tolerance))
					return neighbor;
		}
		return -1;
	}

	static bool Same(SpherePointD a, SpherePointD b, double tolerance) =>
		Math.Abs(a.X - b.X) < tolerance && Math.Abs(a.Y - b.Y) < tolerance && Math.Abs(a.Z - b.Z) < tolerance;

	static EvolutionFace Integrate(int a, int b, SpherePointD start, SpherePointD end, double radiusKm)
	{
		// 对逆时针边，外法向 = -(start × end)/sin(angle)。
		// 积分 (omega × r) · outward 的系数化简为 R²(end - start)。
		double areaScale = radiusKm * radiusKm;
		double nx = start.Z * end.Y - start.Y * end.Z;
		double ny = start.X * end.Z - start.Z * end.X;
		double nz = start.Y * end.X - start.X * end.Y;
		double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
		if (!(length > 1e-15)) throw new ArgumentException($"格 {a} 与 {b} 的共享边退化");
		double cosAngle = start.X * end.X + start.Y * end.Y + start.Z * end.Z;
		double angle = Math.Atan2(length, cosAngle);
		return new EvolutionFace(a, b, (end.X - start.X) * areaScale,
			(end.Y - start.Y) * areaScale, (end.Z - start.Z) * areaScale,
			start, end, new SpherePointD(nx / length, ny / length, nz / length), angle * radiusKm, cosAngle);
	}
}
