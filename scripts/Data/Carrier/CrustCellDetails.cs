namespace World.Data;

internal sealed record CrustCellDetails(int PlateId, CrustType Type, double? TimeMa,
	double? OceanicCoverage, double? OceanAgeMa, double? HeadingDeg, double? SpeedKmPerMa,
	float? MaterialHeightM, double? ReliefM, CrustCellBoundary[] Boundaries);

internal readonly record struct CrustCellBoundary(int NeighborPlateId, BoundaryKind Motion,
	ConvergenceKind Convergence, BoundarySideRole Role, double? NormalKmPerMa, double? ShearKmPerMa);
