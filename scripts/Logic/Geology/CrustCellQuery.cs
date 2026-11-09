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
		EvolutionFrame frame = facts.Evolution;
		CrustProjection projection = frame?.Projection;
		int plate = projection?.SurfacePlateOfCell[cell] ?? facts.PlateOfCell[cell];
		CrustType type = projection?.SurfaceCrustType[cell] ?? facts.Crust.Type[cell];
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
		int dominant = field.DominantNormalEdge(boundaries, cell);
		if (dominant < 0)
		{
			double strongest = -1;
			foreach (int edge in adjacent)
			{
				if (field.EdgeKind[edge] != BoundaryKind.Transform) continue;
				double shear = Math.Abs(field.Evolution?.TangentSpeedKmPerMa[edge] ?? 0);
				if (shear > strongest || (shear == strongest && edge < dominant))
					(dominant, strongest) = (edge, shear);
			}
		}

		int index = 0;
		if (dominant >= 0) details[index++] = ReadBoundary(dominant);
		foreach (int edge in adjacent)
			if (edge != dominant) details[index++] = ReadBoundary(edge);

		float? materialHeight = projection?.MaterialHeightM[cell];
		return new CrustCellDetails(plate, type, frame?.Clock.TimeMa,
			projection?.OceanicCoverage[cell], projection?.OceanAgeMa[cell], heading,
			frame != null ? tangent.Length() * frame.Definition.RadiusKm : null,
			materialHeight, materialHeight.HasValue ? facts.HeightM[cell] - (double)materialHeight.Value : null, details);

		CrustCellBoundary ReadBoundary(int i)
		{
			PlateBoundaryEdge edge = boundaries.Edges[i];
			bool isA = edge.CellA == cell;
			int other = isA ? edge.CellB : edge.CellA;
			BoundaryKind motion = field.EdgeKind[i];
			ConvergenceKind convergence = field.Evolution?.ConvergenceKinds[i]
				?? (motion != BoundaryKind.Convergent ? ConvergenceKind.None
					: type == CrustType.Continental && facts.Crust.Type[other] == CrustType.Continental
						? ConvergenceKind.Collision : ConvergenceKind.Subduction);
			BoundarySideRole role = convergence == ConvergenceKind.Subduction
				? (isA ? field.EdgeRoleA[i] : field.EdgeRoleB[i]) : BoundarySideRole.None;
			return new CrustCellBoundary(isA ? edge.PlateB : edge.PlateA, motion, convergence, role,
				field.Evolution?.NormalSpeedKmPerMa[i], field.Evolution?.TangentSpeedKmPerMa[i]);
		}
	}

	internal static Vector3 PlateTangent(WorldFacts facts, int plate, Vector3 direction)
	{
		if (facts.PlateMotions == null) return facts.Plates[plate].Omega.Cross(direction);
		var omega = facts.PlateMotions[plate].OmegaRadPerMa;
		return new Vector3((float)omega.X, (float)omega.Y, (float)omega.Z).Cross(direction);
	}
}
