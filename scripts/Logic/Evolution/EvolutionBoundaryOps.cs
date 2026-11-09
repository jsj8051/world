using System;
using System.Collections.Generic;
using World.Data;

namespace World.Logic;

/// <summary>
/// 逐边物理场：由板块物理角速度算边界法向/切向相对速率，再按阈值与两侧壳型分类。
/// 角速度单位为 rad/Ma，速率乘半径得 km/Ma。
/// </summary>
public static class EvolutionBoundaryOps
{
	/// <summary>建逐边场；与边界集逐边对齐。</summary>
	/// <exception cref="ArgumentException">输入不一致或含非法值。</exception>
	/// <example><code>var field = EvolutionBoundaryOps.Build(grid, boundaries, crust, motions, plates, spec);</code></example>
	public static EvolutionBoundaryField Build(EvolutionGrid grid, PlateBoundarySet boundaries,
		CrustField crust, IReadOnlyList<PlateMotion> motions, Plate[] plates, EvolutionSpec spec,
		IReadOnlyList<double?> oceanAges = null, EvolutionCrust material = null)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		return Build(grid.RadiusKm, cell => grid.Centers[cell], boundaries, crust, motions, plates, spec, oceanAges,
			grid.NormalSpeed, material);
	}

	internal static EvolutionBoundaryField Build(double radiusKm, Func<int, SpherePointD> direction,
		PlateBoundarySet boundaries, CrustField crust, IReadOnlyList<PlateMotion> motions,
		Plate[] plates, EvolutionSpec spec, IReadOnlyList<double?> oceanAges,
		Func<int, int, SphereVectorD, SphereVectorD, double> sharedEdgeNormal = null, EvolutionCrust material = null)
	{
		if (boundaries == null) throw new ArgumentNullException(nameof(boundaries));
		if (crust == null) throw new ArgumentNullException(nameof(crust));
		if (motions == null) throw new ArgumentNullException(nameof(motions));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		if (spec == null) throw new ArgumentNullException(nameof(spec));

		int edgeCount = boundaries.Edges.Length;
		var normal = new double[edgeCount];
		var tangent = new double[edgeCount];
		var kinds = new EvolutionMotionKind[edgeCount];
		var convergence = new ConvergenceKind[edgeCount];
		var under = new int[edgeCount];
		var sideTypes = new (CrustType A, CrustType B)[edgeCount];

		for (int k = 0; k < edgeCount; k++)
		{
			PlateBoundaryEdge edge = boundaries.Edges[k];
			sideTypes[k] = (crust.Type[edge.CellA], crust.Type[edge.CellB]);
			SpherePointD a = direction(edge.CellA);
			SpherePointD b = direction(edge.CellB);
			SpherePointD mid = Unit(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
			SpherePointD normalDir = Unit(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
			SpherePointD tangentDir = Cross(mid, normalDir);

			SphereVectorD velocityA = Velocity(radiusKm, motions, edge.PlateA, mid);
			SphereVectorD velocityB = Velocity(radiusKm, motions, edge.PlateB, mid);
			double dx = velocityB.X - velocityA.X;
			double dy = velocityB.Y - velocityA.Y;
			double dz = velocityB.Z - velocityA.Z;
			double nSpeed = sharedEdgeNormal != null
				? sharedEdgeNormal(edge.CellA, edge.CellB, motions[edge.PlateA].OmegaRadPerMa, motions[edge.PlateB].OmegaRadPerMa)
				: dx * normalDir.X + dy * normalDir.Y + dz * normalDir.Z;
			double tSpeed = dx * tangentDir.X + dy * tangentDir.Y + dz * tangentDir.Z;
			normal[k] = nSpeed;
			tangent[k] = tSpeed;

			double speed = Math.Abs(nSpeed);
			double shear = Math.Abs(tSpeed);
			if (speed <= spec.ActiveSpeedKmPerMa && shear <= spec.ActiveSpeedKmPerMa)
			{
				kinds[k] = EvolutionMotionKind.Inactive;
				convergence[k] = ConvergenceKind.None;
				under[k] = -1;
				continue;
			}
			if (nSpeed < -spec.ActiveSpeedKmPerMa)
			{
				kinds[k] = EvolutionMotionKind.Convergent;
				if (material != null) sideTypes[k] = (material.DominantType(edge.CellA, edge.PlateA),
					material.DominantType(edge.CellB, edge.PlateB));
				ClassifyConvergence(edge, sideTypes[k], plates, oceanAges, material, out convergence[k], out under[k]);
			}
			else if (nSpeed > spec.ActiveSpeedKmPerMa)
			{
				kinds[k] = EvolutionMotionKind.Divergent;
				if (material != null) sideTypes[k] = (material.DominantType(edge.CellA, edge.PlateA),
					material.DominantType(edge.CellB, edge.PlateB));
				convergence[k] = ConvergenceKind.None;
				under[k] = -1;
			}
			else
			{
				kinds[k] = EvolutionMotionKind.Transform;
				convergence[k] = ConvergenceKind.None;
				under[k] = -1;
			}
		}

		return new EvolutionBoundaryField(normal, tangent, kinds, convergence, under, sideTypes);

	}

	static void ClassifyConvergence(PlateBoundaryEdge edge, (CrustType A, CrustType B) types, Plate[] plates,
		IReadOnlyList<double?> oceanAges, EvolutionCrust material,
		out ConvergenceKind kind, out int underthrustingPlate)
	{
		var (a, b) = types;
		if (a == CrustType.Continental && b == CrustType.Continental)
		{
			kind = ConvergenceKind.Collision;
			underthrustingPlate = -1;
			return;
		}
		kind = ConvergenceKind.Subduction;
		if (a != b)
		{
			underthrustingPlate = a == CrustType.Oceanic ? edge.PlateA : edge.PlateB;
			return;
		}
		double? ageA = material != null ? material.OceanBirthTimeMa(edge.CellA, edge.PlateA) : -oceanAges?[edge.CellA];
		double? ageB = material != null ? material.OceanBirthTimeMa(edge.CellB, edge.PlateB) : -oceanAges?[edge.CellB];
		if (ageA.HasValue && ageB.HasValue
			&& Math.Abs(ageA.Value - ageB.Value) > 1e-8)
		{
			underthrustingPlate = ageA < ageB ? edge.PlateA : edge.PlateB;
			return;
		}
		double densityA = plates[edge.PlateA].Density;
		double densityB = plates[edge.PlateB].Density;
		if (densityA != densityB)
			underthrustingPlate = densityA > densityB ? edge.PlateA : edge.PlateB;
		else
			underthrustingPlate = edge.PlateA > edge.PlateB ? edge.PlateA : edge.PlateB;

	}

	static SphereVectorD Velocity(double radiusKm, IReadOnlyList<PlateMotion> motions, int plate,
		SpherePointD direction)
	{
		SphereVectorD omega = motions[plate].OmegaRadPerMa;
		double x = omega.Y * direction.Z - omega.Z * direction.Y;
		double y = omega.Z * direction.X - omega.X * direction.Z;
		double z = omega.X * direction.Y - omega.Y * direction.X;
		return new SphereVectorD(x * radiusKm, y * radiusKm, z * radiusKm);
	}

	static SpherePointD Cross(SpherePointD a, SpherePointD b)
		=> Unit(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

	static SpherePointD Unit(double x, double y, double z)
	{
		double length = Math.Sqrt(x * x + y * y + z * z);
		if (!(length > 1e-15)) throw new ArgumentException("边界方向退化");
		return new SpherePointD(x / length, y / length, z / length);
	}

}
