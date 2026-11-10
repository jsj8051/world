using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.Data;
using World.Data.Maps;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 显示所需的最终表层事实：不区分“生成中的演化帧”和“导入的地图结果”，
/// 地图模式、拾取与信息卡都只读这里。
/// </summary>
internal sealed class SurfaceFacts
{
	public CrustType[] SurfaceCrustType { get; }
	public Vector3[] MotionOmega { get; }
	public ProjectedSurfaceFacts Projected { get; }
	public FinalBoundaryFacts Boundaries { get; }
	public double? SourceTimeMa { get; }
	/// <summary>物理欧拉矢量的逐板块三元组（f64）；静态结果没有来源角速度时为 null。</summary>
	public double[] PhysicalOmegaFlat { get; }

	SurfaceFacts(CrustType[] surfaceCrustType, Vector3[] motionOmega, ProjectedSurfaceFacts projected,
		FinalBoundaryFacts boundaries, double? sourceTimeMa, double[] physicalOmegaFlat)
	{
		SurfaceCrustType = surfaceCrustType;
		MotionOmega = motionOmega;
		Projected = projected;
		Boundaries = boundaries;
		SourceTimeMa = sourceTimeMa;
		PhysicalOmegaFlat = physicalOmegaFlat;
	}

	internal static SurfaceFacts FromEvolution(Ball ball, CrustField crust, Plate[] plates,
		EvolutionFrame frame, PlateBoundaryFields boundaryFields, PlateBoundarySet boundaries)
	{
		CrustProjection projection = frame?.Projection;
		var cells = projection?.SurfaceCells;
		ProjectedSurfaceFacts projected = null;
		if (cells != null)
		{
			IReadOnlyList<double?> ages = projection.OceanAgeMa;
			projected = new ProjectedSurfaceFacts(
				cells.Select(cell => cell.EmergentFraction).ToArray(),
				cells.Select(cell => cell.LandHeightM).ToArray(),
				cells.Select(cell => cell.WaterHeightM).ToArray(),
				projection.MaterialHeightM.ToArray(),
				projection.OceanicCoverage.ToArray(),
				ages.ToArray(),
				projection.VolcanicRateKmPerMa.ToArray(),
				projection.EmergentLandFraction);
		}

		CrustType[] surfaceTypes = projected != null
			? projection.SurfaceCrustType.ToArray()
			: crust.Type;
		Vector3[] motion = frame?.Motions != null
			? frame.Motions.Select(m => new Vector3((float)m.OmegaRadPerMa.X, (float)m.OmegaRadPerMa.Y,
				(float)m.OmegaRadPerMa.Z)).ToArray()
			: plates.Select(plate => plate.Omega).ToArray();
		double[] physical = frame?.Motions == null
			? null
			: frame.Motions.SelectMany(m => new[] { m.OmegaRadPerMa.X, m.OmegaRadPerMa.Y, m.OmegaRadPerMa.Z }).ToArray();
		return new SurfaceFacts(surfaceTypes, motion, projected,
			FinalBoundaryFacts.FromEvolution(crust, boundaryFields, boundaries), frame?.Clock.TimeMa, physical);
	}

	internal static SurfaceFacts FromSnapshot(GeographySnapshot snapshot)
	{
		var surface = snapshot.Surface;
		ProjectedSurfaceFacts projected = null;
		if (surface.Projected != null)
		{
			var source = surface.Projected;
			projected = new ProjectedSurfaceFacts(
				source.EmergentFraction.ToArray(), source.LandHeightM.ToArray(),
				source.WaterHeightM.ToArray(), source.MaterialHeightM.ToArray(),
				source.OceanicCoverage.ToArray(),
				Enumerable.Range(0, source.OceanAgeMa.Count).Select(i => source.OceanAgeMa[i]).ToArray(),
				source.VolcanicRateKmPerMa.ToArray(), source.EmergentLandFraction);
		}
		Vector3[] motion = surface.MotionOmega.Select(value => new Vector3(value.X, value.Y, value.Z)).ToArray();
		FinalBoundarySnapshot final = snapshot.Tectonics.FinalBoundary;
		var boundaries = new FinalBoundaryFacts(
			final.SideTypeA.ToArray(), final.SideTypeB.ToArray(), final.ConvergenceKind.ToArray(),
			Enumerable.Range(0, final.NormalKmPerMa.Count).Select(i => final.NormalKmPerMa[i]).ToArray(),
			Enumerable.Range(0, final.ShearKmPerMa.Count).Select(i => final.ShearKmPerMa[i]).ToArray());
		double[] physical = surface.Projected == null
			? null
			: surface.Projected.PhysicalOmegaRadPerMa.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray();
		return new SurfaceFacts(surface.SurfaceCrustType.ToArray(), motion, projected, boundaries,
			snapshot.Manifest.SourceTimeMa, physical);
	}
}

internal sealed class ProjectedSurfaceFacts
{
	public float[] EmergentFraction { get; }
	public float[] LandHeightM { get; }
	public float[] WaterHeightM { get; }
	public float[] MaterialHeightM { get; }
	public double[] OceanicCoverage { get; }
	public double?[] OceanAgeMa { get; }
	public double[] VolcanicRateKmPerMa { get; }
	public double EmergentLandFraction { get; }

	public ProjectedSurfaceFacts(float[] emergentFraction, float[] landHeightM, float[] waterHeightM,
		float[] materialHeightM, double[] oceanicCoverage, double?[] oceanAgeMa, double[] volcanicRateKmPerMa,
		double emergentLandFraction)
	{
		EmergentFraction = emergentFraction;
		LandHeightM = landHeightM;
		WaterHeightM = waterHeightM;
		MaterialHeightM = materialHeightM;
		OceanicCoverage = oceanicCoverage;
		OceanAgeMa = oceanAgeMa;
		VolcanicRateKmPerMa = volcanicRateKmPerMa;
		EmergentLandFraction = emergentLandFraction;
	}
}

internal sealed class FinalBoundaryFacts
{
	public CrustType[] SideTypeA { get; }
	public CrustType[] SideTypeB { get; }
	public ConvergenceKind[] Convergence { get; }
	public double?[] NormalKmPerMa { get; }
	public double?[] ShearKmPerMa { get; }

	public FinalBoundaryFacts(CrustType[] sideTypeA, CrustType[] sideTypeB, ConvergenceKind[] convergence,
		double?[] normalKmPerMa, double?[] shearKmPerMa)
	{
		SideTypeA = sideTypeA;
		SideTypeB = sideTypeB;
		Convergence = convergence;
		NormalKmPerMa = normalKmPerMa;
		ShearKmPerMa = shearKmPerMa;
	}

	internal static FinalBoundaryFacts FromEvolution(CrustField crust, PlateBoundaryFields fields,
		PlateBoundarySet boundaries)
	{
		int count = boundaries.Edges.Length;
		var sideA = new CrustType[count];
		var sideB = new CrustType[count];
		var convergence = new ConvergenceKind[count];
		var normal = new double?[count];
		var shear = new double?[count];
		EvolutionBoundaryField evolution = fields.Evolution;
		for (int i = 0; i < count; i++)
		{
			PlateBoundaryEdge edge = boundaries.Edges[i];
			CrustType typeA = crust.Type[edge.CellA];
			CrustType typeB = crust.Type[edge.CellB];
			if (evolution != null)
			{
				(typeA, typeB) = evolution.SideTypes(i, typeA, typeB);
				convergence[i] = evolution.ConvergenceKinds[i];
				normal[i] = evolution.NormalSpeedKmPerMa[i];
				shear[i] = evolution.TangentSpeedKmPerMa[i];
			}
			else if (fields.EdgeKind[i] == BoundaryKind.Convergent)
				convergence[i] = typeA == CrustType.Continental && typeB == CrustType.Continental
					? ConvergenceKind.Collision : ConvergenceKind.Subduction;
			sideA[i] = typeA;
			sideB[i] = typeB;
		}
		return new FinalBoundaryFacts(sideA, sideB, convergence, normal, shear);
	}

	public (CrustType A, CrustType B) SideTypes(int edge) => (SideTypeA[edge], SideTypeB[edge]);
}