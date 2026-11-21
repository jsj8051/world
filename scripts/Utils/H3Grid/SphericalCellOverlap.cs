using System;
using System.Collections.Generic;
using System.Threading;
using World.H3Grid;
using World.Utils.H3;
using H3Api = World.Utils.H3.H3;

namespace World.Utils;

/// <summary>Overlaps a rotated H3 cell with the fixed H3 grid using great-circle edges.</summary>
internal sealed class SphericalCellOverlap
{
	internal readonly record struct Overlap(int Cell, double Area);

	readonly Ball _ball;
	readonly CellShape[] _shapes;
	readonly List<int> _frontier = new();

	// H3 cell boundaries have at most ten vertices, including face crossings.
	const int MaxVertices = 10;

	internal SphericalCellOverlap(Ball ball) : this(ball, new CellShape[ball.CellIds.Length]) { }

	SphericalCellOverlap(Ball ball, CellShape[] shapes)
	{
		_ball = ball;
		_shapes = shapes;
	}

	/// <summary>Share immutable cell geometry, with an independent overlap search frontier.</summary>
	internal SphericalCellOverlap CreateWorker() => new(_ball, _shapes);

	internal double CellArea(int cell) => Shape(cell).Area;

	internal void Project(int sourceCell, RotationD rotation, List<Overlap> overlaps)
	{
		overlaps.Clear();
		CellShape source = Shape(sourceCell);
		if (rotation.X == 0 && rotation.Y == 0 && rotation.Z == 0)
		{
			overlaps.Add(new Overlap(sourceCell, source.Area));
			return;
		}

		Span<Direction> rotated = stackalloc Direction[source.Ring.Length];
		for (int i = 0; i < rotated.Length; i++) rotated[i] = source.Ring[i].Rotate(rotation);
		Direction center = source.Center.Rotate(rotation);
		int origin = _ball.CellIndexOf(H3Api.LatLngToCell(center.LatLng, _ball.Res));
		_frontier.Clear();
		_frontier.Add(origin);
		double total = 0;
		for (int i = 0; i < _frontier.Count; i++)
		{
			int cell = _frontier[i];
			CellShape target = Shape(cell);
			// Each cell is enclosed by the spherical cap through its farthest vertex.
			// The triangle inequality for chord distance rules out disjoint pairs.
			double combinedRadius = source.ChordRadius + target.ChordRadius;
			if (center.DistanceSquared(target.Center) > combinedRadius * combinedRadius + 1e-14) continue;
			double area = target.IntersectionArea(rotated);
			if (area <= source.Area * 1e-10) continue;
			total += area;
			overlaps.Add(new Overlap(cell, area));
			foreach (int neighbor in _ball.CellNeighbors[cell])
				if (!_frontier.Contains(neighbor)) _frontier.Add(neighbor);
		}
		if (Math.Abs(total - source.Area) > 1e-6 * source.Area)
			throw new InvalidOperationException($"旋转格的重叠面积未覆盖来源格 {_ball.CellIds[sourceCell]:x}。");
		for (int i = 0; i < overlaps.Count; i++)
		{
			Overlap overlap = overlaps[i];
			overlaps[i] = overlap with { Area = overlap.Area * (source.Area / total) };
		}
	}

	CellShape Shape(int cell)
	{
		CellShape shape = Volatile.Read(ref _shapes[cell]);
		if (shape != null) return shape;
		ulong id = _ball.CellIds[cell];
		LatLng[] boundary = H3Api.CellToBoundary(id);
		var ring = new Direction[boundary.Length];
		for (int i = 0; i < ring.Length; i++) ring[i] = Direction.From(boundary[i]);
		shape = new CellShape(Direction.From(H3Api.CellToLatLng(id)), ring);
		return Interlocked.CompareExchange(ref _shapes[cell], shape, null) ?? shape;
	}

	sealed class CellShape
	{
		readonly Direction[] _edgeNormals;
		readonly bool _convex;
		internal Direction Center { get; }
		internal Direction[] Ring { get; }
		internal double Area { get; }
		internal double ChordRadius { get; }

		internal CellShape(Direction center, Direction[] ring)
		{
			Center = center;
			Ring = ring;
			Area = PolygonArea(ring);
			double farthest = 0;
			foreach (Direction vertex in ring) farthest = Math.Max(farthest, center.DistanceSquared(vertex));
			ChordRadius = Math.Sqrt(farthest);
			_edgeNormals = new Direction[ring.Length];
			_convex = true;
			for (int i = 0; i < ring.Length; i++)
			{
				Direction normal = Inward(ring[i], ring[(i + 1) % ring.Length], center);
				_edgeNormals[i] = normal;
				foreach (Direction vertex in ring)
					if (normal.Dot(vertex) < -1e-14) _convex = false;
			}
		}

		internal double IntersectionArea(ReadOnlySpan<Direction> polygon)
		{
			if (_convex) return ClipArea(polygon, _edgeNormals);

			// A boundary that crosses an icosahedron face can bend at the crossing.
			// The triangles around its H3 center still partition the cell.
			Span<Direction> triangle = stackalloc Direction[3];
			double area = 0;
			for (int i = 0; i < Ring.Length; i++)
			{
				Direction first = Ring[i], second = Ring[(i + 1) % Ring.Length];
				triangle[0] = _edgeNormals[i];
				triangle[1] = Inward(Center, first, second);
				triangle[2] = Inward(second, Center, first);
				area += ClipArea(polygon, triangle);
			}
			return area;
		}
	}

	static Direction Inward(Direction first, Direction second, Direction inside)
	{
		Direction normal = first.Cross(second);
		return normal.Dot(inside) < 0 ? normal.Scale(-1) : normal;
	}

	static double ClipArea(ReadOnlySpan<Direction> subject, ReadOnlySpan<Direction> normals)
	{
		Span<Direction> input = stackalloc Direction[4 * MaxVertices];
		Span<Direction> output = stackalloc Direction[4 * MaxVertices];
		subject.CopyTo(input);
		int count = subject.Length;
		foreach (Direction normal in normals)
		{
			int written = 0;
			Direction previous = input[count - 1];
			double previousSide = normal.Dot(previous);
			for (int i = 0; i < count; i++)
			{
				Direction current = input[i];
				double side = normal.Dot(current);
				if ((side > 0) != (previousSide > 0))
				{
					double fraction = previousSide / (previousSide - side);
					output[written++] = previous.Scale(1 - fraction).Add(current.Scale(fraction)).Normalized();
				}
				if (side > 0) output[written++] = current;
				previous = current;
				previousSide = side;
			}
			if (written < 3) return 0;
			Span<Direction> swap = input;
			input = output;
			output = swap;
			count = written;
		}
		return PolygonArea(input[..count]);
	}

	static double PolygonArea(ReadOnlySpan<Direction> ring)
	{
		double area = 0;
		Direction first = ring[0];
		for (int i = 1; i + 1 < ring.Length; i++)
		{
			Direction second = ring[i], third = ring[i + 1];
			area += 2 * Math.Atan2(first.Dot(second.Cross(third)),
				1 + first.Dot(second) + second.Dot(third) + third.Dot(first));
		}
		return Math.Abs(area);
	}

	readonly record struct Direction(double X, double Y, double Z)
	{
		internal static Direction From(LatLng point)
		{
			double horizontal = Math.Cos(point.Lat);
			return new Direction(horizontal * Math.Cos(point.Lng), Math.Sin(point.Lat), horizontal * Math.Sin(point.Lng));
		}

		internal LatLng LatLng => new(Math.Atan2(Y, Math.Sqrt(X * X + Z * Z)), Math.Atan2(Z, X));

		internal Direction Rotate(RotationD rotation)
		{
			var (x, y, z) = RotationOps.Apply(rotation, X, Y, Z);
			return new Direction(x, y, z);
		}

		internal double Dot(Direction other) => X * other.X + Y * other.Y + Z * other.Z;
		internal double DistanceSquared(Direction other)
		{
			double x = X - other.X, y = Y - other.Y, z = Z - other.Z;
			return x * x + y * y + z * z;
		}
		internal Direction Scale(double scale) => new(X * scale, Y * scale, Z * scale);
		internal Direction Add(Direction other) => new(X + other.X, Y + other.Y, Z + other.Z);
		internal Direction Cross(Direction other) => new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);
		internal Direction Normalized() => Scale(1 / Math.Sqrt(Dot(this)));
	}
}
