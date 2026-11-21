using System;
using Godot;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>从板块参考网格重建高程。壳型和海陆符号限定拟合邻域。</summary>
internal sealed class SculptingElevationSampler(Ball ball, PlateElevationState elevations, float seaLevelM)
{
	internal bool TrySample(int plate, CrustType crust, Vector3 direction, int preferredCell, out float height)
	{
		int sample = preferredCell >= 0 ? preferredCell : BallGeoIndex.CellAt(ball, direction);
		if (elevations.TryGet(plate, sample, crust, out height))
		{
			height = Reconstruct(plate, sample, crust, direction, height);
			return true;
		}

		int nearest = -1;
		float distance = float.PositiveInfinity;
		float nearestHeight = 0;
		foreach (int neighbor in ball.CellNeighbors[sample])
		{
			if (!elevations.TryGet(plate, neighbor, crust, out float candidate)) continue;
			float offset = direction.DistanceSquaredTo(ball.CellDirs[neighbor]);
			if (offset >= distance) continue;
			nearest = neighbor;
			distance = offset;
			nearestHeight = candidate;
		}
		if (nearest < 0) return false;
		height = Reconstruct(plate, nearest, crust, direction, nearestHeight);
		return true;
	}

	float Reconstruct(int plate, int sample, CrustType crust, Vector3 direction, float height)
	{
		Vector3 center = ball.CellDirs[sample];
		Vector3 offset = direction - center;
		float distanceSquared = offset.LengthSquared();
		if (distanceSquared <= 1e-12f) return height;

		Vector3 tangent = (MathF.Abs(center.Y) < .9f ? Vector3.Up : Vector3.Right).Cross(center);
		Vector3 bitangent = center.Cross(tangent);
		bool land = height > seaLevelM;
		float lowest = height, highest = height, reachSquared = 0;
		double xx = 0, xy = 0, yy = 0, xHeight = 0, yHeight = 0;
		double xRadius = 0, yRadius = 0, radiusNorm = 0, radiusHeight = 0;
		foreach (int neighbor in ball.CellNeighbors[sample])
		{
			if (!elevations.TryGet(plate, neighbor, crust, out float other) || (other > seaLevelM) != land) continue;

			Vector3 chord = ball.CellDirs[neighbor] - center;
			double x = chord.Dot(tangent), y = chord.Dot(bitangent);
			double radial = x * x + y * y;
			double difference = (double)other - height;
			xx += x * x;
			xy += x * y;
			yy += y * y;
			xHeight += x * difference;
			yHeight += y * difference;
			xRadius += x * radial;
			yRadius += y * radial;
			radiusNorm += radial * radial;
			radiusHeight += radial * difference;
			reachSquared = Math.Max(reachSquared, chord.LengthSquared());
			lowest = Math.Min(lowest, other);
			highest = Math.Max(highest, other);
		}

		// Distant shore samples and nearly collinear neighborhoods keep the source height.
		double determinant = xx * yy - xy * xy;
		double trace = xx + yy;
		const double relativeFitTolerance = 1e-5;
		if (distanceSquared > reachSquared || determinant <= relativeFitTolerance * trace * trace || lowest == highest)
			return height;

		double slopeX = (yy * xHeight - xy * yHeight) / determinant;
		double slopeY = (xx * yHeight - xy * xHeight) / determinant;
		double targetX = offset.Dot(tangent), targetY = offset.Dot(bitangent);
		double reconstructed = height + slopeX * targetX + slopeY * targetY;

		// Fit an isotropic curvature after projecting out the linear terms.
		double radiusX = (yy * xRadius - xy * yRadius) / determinant;
		double radiusY = (xx * yRadius - xy * xRadius) / determinant;
		double remainingNorm = radiusNorm - radiusX * xRadius - radiusY * yRadius;
		if (remainingNorm > relativeFitTolerance * radiusNorm)
		{
			double curvature = (radiusHeight - slopeX * xRadius - slopeY * yRadius) / remainingNorm;
			reconstructed += curvature * (targetX * (targetX - radiusX) + targetY * (targetY - radiusY));
		}
		return (float)Math.Clamp(reconstructed, lowest, highest);
	}
}
