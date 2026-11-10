using System;
using Godot;
using World.Data;
using World.Utils;

namespace World.Logic;

/// <summary>从已发布的快照查询选中格及其直接相邻边界。</summary>
internal static class CrustCellQuery
{
	public static CrustCellDetails Read(WorldFacts facts, int cell)
	{
		var boundaries = facts.PlateBoundaries;
		var field = facts.BoundaryFields;
		var surface = facts.Surface;
		ProjectedSurfaceFacts projected = surface.Projected;
		int plate = facts.PlateOfCell[cell];
		CrustType type = surface.SurfaceCrustType[cell];
		Vector3 direction = facts.Ball.CellDirs[cell];
		Vector3 tangent = PlateTangent(facts, plate, direction);
		double? heading = null;
		if (tangent.LengthSquared() >= 1e-16f)
		{
			var point = SpherePoint.At(direction);
			double angle = Math.Atan2(tangent.Dot(point.East), -tangent.Dot(point.South)) * (180 / Math.PI);
			heading = (angle + 360) % 360;
		}

		var adjacent = boundaries.EdgesOfCell(cell);
		var details = adjacent.IsEmpty ? Array.Empty<CrustCellBoundary>() : new CrustCellBoundary[adjacent.Length];
		int dominant = DominantNormalEdge(facts, cell);
		if (dominant < 0)
		{
			double strongest = -1;
			foreach (int edge in adjacent)
			{
				if (field.EdgeKind[edge] != BoundaryKind.Transform) continue;
				double shear = Math.Abs(surface.Boundaries.ShearKmPerMa[edge] ?? 0);
				if (shear > strongest || (shear == strongest && edge < dominant))
					(dominant, strongest) = (edge, shear);
			}
		}

		int index = 0;
		if (dominant >= 0) details[index++] = ReadBoundary(dominant);
		foreach (int edge in adjacent)
			if (edge != dominant) details[index++] = ReadBoundary(edge);

		float? materialHeight = projected?.MaterialHeightM[cell];
		return new CrustCellDetails(plate, type, surface.SourceTimeMa,
			projected?.OceanicCoverage[cell], projected?.OceanAgeMa[cell], heading,
			tangent.Length() * SphericalFbmNoise.WorldRadiusKm,
			materialHeight, materialHeight.HasValue ? facts.HeightM[cell] - materialHeight.Value : null, details);

		CrustCellBoundary ReadBoundary(int i)
		{
			PlateBoundaryEdge edge = boundaries.Edges[i];
			bool isA = edge.CellA == cell;
			int other = isA ? edge.CellB : edge.CellA;
			BoundaryKind motion = field.EdgeKind[i];
			ConvergenceKind convergence = surface.Boundaries.Convergence[i];
			BoundarySideRole role = convergence == ConvergenceKind.Subduction
				? (isA ? field.EdgeRoleA[i] : field.EdgeRoleB[i]) : BoundarySideRole.None;
			return new CrustCellBoundary(isA ? edge.PlateB : edge.PlateA, motion, convergence, role,
				surface.Boundaries.NormalKmPerMa[i], surface.Boundaries.ShearKmPerMa[i]);
		}
	}

	/// <summary>主导法向边界：两侧壳型来自最终表层，速度缺失时退回归一化运动量。</summary>
	internal static int DominantNormalEdge(WorldFacts facts, int cell)
	{
		var boundaries = facts.PlateBoundaries;
		var field = facts.BoundaryFields;
		int dominant = -1;
		double strength = 0;
		foreach (int edge in boundaries.EdgesOfCell(cell))
		{
			if (field.EdgeKind[edge] is not (BoundaryKind.Convergent or BoundaryKind.Divergent)) continue;
			double candidate = Math.Abs(facts.Surface.Boundaries.NormalKmPerMa[edge] ?? field.EdgeMotion[edge]);
			if (dominant < 0 || candidate > strength || (candidate == strength && edge < dominant))
				(dominant, strength) = (edge, candidate);
		}
		return dominant;
	}

	internal static Vector3 PlateTangent(WorldFacts facts, int plate, Vector3 direction)
		=> facts.Surface.MotionOmega[plate].Cross(direction);
}