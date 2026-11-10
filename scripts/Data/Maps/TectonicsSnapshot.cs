using System;
using System.Collections.Generic;
using System.Linq;

namespace World.Data.Maps;

internal readonly record struct PlateSnapshot(int PlateId, float SeedTheta, float SeedPhi, Float3Snapshot Omega, float Density);
internal readonly record struct CrustProvinceSnapshot(int Id, CrustType Type, int SeedCell, int SeedPlateId, int CellCount);
internal readonly record struct BoundaryEdgeSnapshot(int CellA, int CellB, int PlateA, int PlateB);
internal readonly record struct BoundaryPairSnapshot(int PlateA, int PlateB, int EdgeStart, int EdgeCount);
internal readonly record struct TectonicSourceSnapshot(int EdgeIndex, int SideCell, TectonicChannel Channel);

internal sealed class CrustSnapshot
{
	public SnapshotArray<CrustType> Type { get; }
	public SnapshotArray<int> ProvinceId { get; }
	public SnapshotArray<CrustProvinceSnapshot> Provinces { get; }

	public CrustSnapshot(IEnumerable<CrustType> type, IEnumerable<int> provinceId, IEnumerable<CrustProvinceSnapshot> provinces)
	{
		Type = new(MapStructure.Present(type, "tectonics.crust.type"));
		ProvinceId = new(MapStructure.Present(provinceId, "tectonics.crust.provinceId"));
		Provinces = new(MapStructure.Present(provinces, "tectonics.crust.provinces"));
	}

	internal void Validate(int cellCount, int plateCount)
	{
		MapStructure.Length(Type.Count, cellCount, "tectonics.crust.type");
		MapStructure.Length(ProvinceId.Count, cellCount, "tectonics.crust.provinceId");
		var counts = new int[Provinces.Count];
		for (int cell = 0; cell < cellCount; cell++)
		{
			MapGeologyWire.ToCrustType(Type[cell]);
			int province = ProvinceId[cell];
			MapStructure.Index(province, Provinces.Count, "tectonics.crust.provinceId", allowMissing: true, index: cell);
			if (province == -1)
			{
				MapStructure.Require(Type[cell] == CrustType.Oceanic, "tectonics.crust.provinceId", "Only oceanic crust may have no province.", index: cell);
				continue;
			}
			MapStructure.Require(Type[cell] == Provinces[province].Type, "tectonics.crust.type", "Crust type must match its province.", index: cell);
			counts[province]++;
		}
		for (int i = 0; i < Provinces.Count; i++)
		{
			CrustProvinceSnapshot province = Provinces[i];
			MapStructure.Require(province.Id == i, "tectonics.crust.provinces.id", "A province ID must equal its row index.", index: i);
			MapGeologyWire.ToCrustType(province.Type);
			MapStructure.Index(province.SeedCell, cellCount, "tectonics.crust.provinces.seedCell", index: i);
			MapStructure.Index(province.SeedPlateId, plateCount, "tectonics.crust.provinces.seedPlateId", index: i);
			MapStructure.Require(province.CellCount == counts[i], "tectonics.crust.provinces.cellCount", "Province counts must match cell membership.", index: i);
		}
	}
}

internal sealed class PlateBoundariesSnapshot
{
	public int PlateCount { get; }
	public SnapshotArray<BoundaryEdgeSnapshot> Edges { get; }
	public SnapshotArray<BoundaryPairSnapshot> Pairs { get; }
	public SnapshotArray<int> CellEdgeStart { get; }
	public SnapshotArray<int> CellEdgeIndex { get; }

	public PlateBoundariesSnapshot(int plateCount, IEnumerable<BoundaryEdgeSnapshot> edges, IEnumerable<BoundaryPairSnapshot> pairs,
		IEnumerable<int> cellEdgeStart, IEnumerable<int> cellEdgeIndex)
	{
		PlateCount = plateCount;
		Edges = new(MapStructure.Present(edges, "tectonics.plateBoundaries.edges"));
		Pairs = new(MapStructure.Present(pairs, "tectonics.plateBoundaries.pairs"));
		CellEdgeStart = new(MapStructure.Present(cellEdgeStart, "tectonics.plateBoundaries.cellEdgeStart"));
		CellEdgeIndex = new(MapStructure.Present(cellEdgeIndex, "tectonics.plateBoundaries.cellEdgeIndex"));
	}

	internal void Validate(int cellCount, int plateCount, SnapshotArray<int> plateOfCell)
	{
		const string location = "tectonics.plateBoundaries";
		MapStructure.Require(PlateCount == plateCount, location + ".plateCount", "Boundary and plate counts must agree.");
		MapStructure.Require((long)plateCount * plateCount == Pairs.Count, location + ".pairs", "Expected P squared pair rows.", MapIssueCode.InvalidShape);
		MapStructure.Require((long)cellCount + 1 == CellEdgeStart.Count, location + ".cellEdgeStart", "Expected N plus one CSR offsets.", MapIssueCode.InvalidShape);
		MapStructure.Require(2L * Edges.Count == CellEdgeIndex.Count, location + ".cellEdgeIndex", "Expected two incidences per edge.", MapIssueCode.InvalidShape);
		var endpointPairs = new HashSet<long>();
		for (int i = 0; i < Edges.Count; i++)
		{
			BoundaryEdgeSnapshot edge = Edges[i];
			MapStructure.Index(edge.CellA, cellCount, location + ".edges.cellA", index: i);
			MapStructure.Index(edge.CellB, cellCount, location + ".edges.cellB", index: i);
			MapStructure.Require(edge.CellA < edge.CellB, location + ".edges", "Boundary endpoints must be ordered CellA < CellB.", index: i);
			MapStructure.Require(edge.PlateA != edge.PlateB && edge.PlateA == plateOfCell[edge.CellA] && edge.PlateB == plateOfCell[edge.CellB],
				location + ".edges", "Boundary plate ownership must match its two distinct endpoint plates.", MapIssueCode.InvalidReference, i);
			MapStructure.Require(endpointPairs.Add(((long)edge.CellA << 32) | (uint)edge.CellB), location + ".edges", "Duplicate boundary edge.", index: i);
		}
		var pairCoverage = new bool[Edges.Count];
		for (int key = 0; key < Pairs.Count; key++)
		{
			BoundaryPairSnapshot pair = Pairs[key];
			MapStructure.Require(pair.EdgeCount >= 0, location + ".pairs.edgeCount", "Edge counts must be nonnegative.", index: key);
			// Empty rows preserve the producer's unused fields, including default zero rows.
			if (pair.EdgeCount == 0) continue;
			MapStructure.Require(pair.PlateA == key / plateCount && pair.PlateB == key % plateCount && pair.PlateA < pair.PlateB,
				location + ".pairs", "A nonempty pair must match its upper-triangular table key.", MapIssueCode.InvalidReference, key);
			MapStructure.Require(pair.EdgeStart >= 0 && (long)pair.EdgeStart + pair.EdgeCount <= Edges.Count,
				location + ".pairs", "Pair edge range is out of bounds.", MapIssueCode.InvalidReference, key);
			for (int i = pair.EdgeStart; i < pair.EdgeStart + pair.EdgeCount; i++)
			{
				BoundaryEdgeSnapshot edge = Edges[i];
				MapStructure.Require(!pairCoverage[i] && Math.Min(edge.PlateA, edge.PlateB) == pair.PlateA && Math.Max(edge.PlateA, edge.PlateB) == pair.PlateB,
					location + ".pairs", "Each edge must belong to exactly one matching pair range.", MapIssueCode.InvalidReference, key);
				pairCoverage[i] = true;
			}
		}
		for (int i = 0; i < pairCoverage.Length; i++)
			MapStructure.Require(pairCoverage[i], location + ".pairs", "An edge is not covered by its pair range.", MapIssueCode.InvalidReference, i);

		MapStructure.Require(CellEdgeStart[0] == 0 && CellEdgeStart[cellCount] == CellEdgeIndex.Count,
			location + ".cellEdgeStart", "CSR offsets must start at zero and end at the incidence count.");
		var incidenceA = new bool[Edges.Count];
		var incidenceB = new bool[Edges.Count];
		for (int cell = 0; cell < cellCount; cell++)
		{
			int start = CellEdgeStart[cell], end = CellEdgeStart[cell + 1];
			MapStructure.Require(start >= 0 && end >= start && end <= CellEdgeIndex.Count,
				location + ".cellEdgeStart", "CSR offsets must be monotonic and in range.", MapIssueCode.InvalidReference, cell);
			// Never sort CSR incidences: their producer order affects downstream floating-point sums.
			for (int i = start; i < end; i++)
			{
				int edgeIndex = CellEdgeIndex[i];
				MapStructure.Index(edgeIndex, Edges.Count, location + ".cellEdgeIndex", index: i);
				BoundaryEdgeSnapshot edge = Edges[edgeIndex];
				MapStructure.Require(cell == edge.CellA || cell == edge.CellB, location + ".cellEdgeIndex", "The edge does not contain this cell.", MapIssueCode.InvalidReference, i);
				bool[] incidence = cell == edge.CellA ? incidenceA : incidenceB;
				MapStructure.Require(!incidence[edgeIndex], location + ".cellEdgeIndex", "Duplicate edge incidence for a cell.", MapIssueCode.InvalidReference, i);
				incidence[edgeIndex] = true;
			}
		}
		for (int i = 0; i < Edges.Count; i++)
			MapStructure.Require(incidenceA[i] && incidenceB[i], location + ".cellEdgeIndex", "Both edge endpoints require an incidence.", MapIssueCode.InvalidReference, i);
	}
}

internal sealed class BoundaryFieldsSnapshot
{
	public SnapshotArray<float> EdgeMotion { get; }
	public SnapshotArray<BoundaryKind> EdgeKind { get; }
	public SnapshotArray<BoundarySideRole> EdgeRoleA { get; }
	public SnapshotArray<BoundarySideRole> EdgeRoleB { get; }

	public BoundaryFieldsSnapshot(IEnumerable<float> edgeMotion, IEnumerable<BoundaryKind> edgeKind,
		IEnumerable<BoundarySideRole> edgeRoleA, IEnumerable<BoundarySideRole> edgeRoleB)
	{
		EdgeMotion = new(MapStructure.Present(edgeMotion, "tectonics.boundaryFields.edgeMotion"));
		EdgeKind = new(MapStructure.Present(edgeKind, "tectonics.boundaryFields.edgeKind"));
		EdgeRoleA = new(MapStructure.Present(edgeRoleA, "tectonics.boundaryFields.edgeRoleA"));
		EdgeRoleB = new(MapStructure.Present(edgeRoleB, "tectonics.boundaryFields.edgeRoleB"));
	}

	internal void Validate(int edgeCount)
	{
		MapStructure.Length(EdgeMotion.Count, edgeCount, "tectonics.boundaryFields.edgeMotion");
		MapStructure.Length(EdgeKind.Count, edgeCount, "tectonics.boundaryFields.edgeKind");
		MapStructure.Length(EdgeRoleA.Count, edgeCount, "tectonics.boundaryFields.edgeRoleA");
		MapStructure.Length(EdgeRoleB.Count, edgeCount, "tectonics.boundaryFields.edgeRoleB");
		for (int i = 0; i < edgeCount; i++)
		{
			MapStructure.Finite(EdgeMotion[i], "tectonics.boundaryFields.edgeMotion", i);
			MapGeologyWire.ToBoundaryKind(EdgeKind[i]);
			MapGeologyWire.ToBoundarySideRole(EdgeRoleA[i]);
			MapGeologyWire.ToBoundarySideRole(EdgeRoleB[i]);
		}
	}
}

internal sealed class FinalBoundarySnapshot
{
	public SnapshotArray<CrustType> SideTypeA { get; }
	public SnapshotArray<CrustType> SideTypeB { get; }
	public SnapshotArray<ConvergenceKind> ConvergenceKind { get; }
	public NullableFloat64Snapshot NormalKmPerMa { get; }
	public NullableFloat64Snapshot ShearKmPerMa { get; }

	public FinalBoundarySnapshot(IEnumerable<CrustType> sideTypeA, IEnumerable<CrustType> sideTypeB,
		IEnumerable<ConvergenceKind> convergenceKind, NullableFloat64Snapshot normalKmPerMa, NullableFloat64Snapshot shearKmPerMa)
	{
		SideTypeA = new(MapStructure.Present(sideTypeA, "tectonics.finalBoundary.sideTypeA"));
		SideTypeB = new(MapStructure.Present(sideTypeB, "tectonics.finalBoundary.sideTypeB"));
		ConvergenceKind = new(MapStructure.Present(convergenceKind, "tectonics.finalBoundary.convergenceKind"));
		NormalKmPerMa = MapStructure.Present(normalKmPerMa, "tectonics.finalBoundary.normalKmPerMa");
		ShearKmPerMa = MapStructure.Present(shearKmPerMa, "tectonics.finalBoundary.shearKmPerMa");
	}

	internal void Validate(int edgeCount)
	{
		MapStructure.Length(SideTypeA.Count, edgeCount, "tectonics.finalBoundary.sideTypeA");
		MapStructure.Length(SideTypeB.Count, edgeCount, "tectonics.finalBoundary.sideTypeB");
		MapStructure.Length(ConvergenceKind.Count, edgeCount, "tectonics.finalBoundary.convergenceKind");
		NormalKmPerMa.Validate(edgeCount, "tectonics.finalBoundary.normalKmPerMa");
		ShearKmPerMa.Validate(edgeCount, "tectonics.finalBoundary.shearKmPerMa");
		for (int i = 0; i < edgeCount; i++)
		{
			MapGeologyWire.ToCrustType(SideTypeA[i]);
			MapGeologyWire.ToCrustType(SideTypeB[i]);
			MapGeologyWire.ToConvergenceKind(ConvergenceKind[i]);
		}
	}
}

internal sealed class TectonicInfluenceSnapshot
{
	public TectonicChannel Channel { get; }
	public SnapshotArray<TectonicSourceSnapshot> Sources { get; }
	public SnapshotArray<float> DistanceKm { get; }
	public SnapshotArray<int> SourceIndex { get; }

	public TectonicInfluenceSnapshot(TectonicChannel channel, IEnumerable<TectonicSourceSnapshot> sources,
		IEnumerable<float> distanceKm, IEnumerable<int> sourceIndex)
	{
		Channel = channel;
		Sources = new(MapStructure.Present(sources, "tectonics.channels.sources"));
		DistanceKm = new(MapStructure.Present(distanceKm, "tectonics.channels.distanceKm"));
		SourceIndex = new(MapStructure.Present(sourceIndex, "tectonics.channels.sourceIndex"));
	}

	internal void Validate(int cellCount, SnapshotArray<BoundaryEdgeSnapshot> edges)
	{
		MapGeologyWire.ToTectonicChannel(Channel);
		string location = "tectonics.channels." + Channel;
		int expected = Sources.Count == 0 ? 0 : cellCount;
		MapStructure.Length(DistanceKm.Count, expected, location + ".distanceKm");
		MapStructure.Length(SourceIndex.Count, expected, location + ".sourceIndex");
		for (int i = 0; i < Sources.Count; i++)
		{
			TectonicSourceSnapshot source = Sources[i];
			MapGeologyWire.ToTectonicChannel(source.Channel);
			MapStructure.Require(source.Channel == Channel, location + ".sources.channel", "Source and field channels must agree.", index: i);
			MapStructure.Index(source.EdgeIndex, edges.Count, location + ".sources.edgeIndex", index: i);
			BoundaryEdgeSnapshot edge = edges[source.EdgeIndex];
			MapStructure.Require(source.SideCell == edge.CellA || source.SideCell == edge.CellB,
				location + ".sources.sideCell", "A source must reference one side of its edge.", MapIssueCode.InvalidReference, i);
		}
		for (int i = 0; i < expected; i++)
		{
			int sourceIndex = SourceIndex[i];
			MapStructure.Index(sourceIndex, Sources.Count, location + ".sourceIndex", allowMissing: true, index: i);
			float distance = DistanceKm[i];
			MapStructure.Require(!float.IsNaN(distance) && distance >= 0 && (sourceIndex == -1 || float.IsFinite(distance)),
				location + ".distanceKm", "Referenced distances must be finite and nonnegative; missing sources also allow positive infinity.", index: i);
		}
	}
}

internal sealed class TectonicsSnapshot
{
	public SnapshotArray<PlateSnapshot> Plates { get; }
	public CrustSnapshot Crust { get; }
	public SnapshotArray<int> PlateOfCell { get; }
	public PlateBoundariesSnapshot PlateBoundaries { get; }
	public BoundaryFieldsSnapshot BoundaryFields { get; }
	public FinalBoundarySnapshot FinalBoundary { get; }
	public IReadOnlyList<TectonicInfluenceSnapshot> Channels { get; }

	public TectonicsSnapshot(IEnumerable<PlateSnapshot> plates, CrustSnapshot crust, IEnumerable<int> plateOfCell,
		PlateBoundariesSnapshot plateBoundaries, BoundaryFieldsSnapshot boundaryFields, FinalBoundarySnapshot finalBoundary,
		IEnumerable<TectonicInfluenceSnapshot> channels)
	{
		Plates = new(MapStructure.Present(plates, "tectonics.plates"));
		Crust = MapStructure.Present(crust, "tectonics.crust");
		PlateOfCell = new(MapStructure.Present(plateOfCell, "tectonics.plateOfCell"));
		PlateBoundaries = MapStructure.Present(plateBoundaries, "tectonics.plateBoundaries");
		BoundaryFields = MapStructure.Present(boundaryFields, "tectonics.boundaryFields");
		FinalBoundary = MapStructure.Present(finalBoundary, "tectonics.finalBoundary");
		Channels = Array.AsReadOnly(MapStructure.Present(channels, "tectonics.channels").ToArray());
	}

	public void Validate(int cellCount)
	{
		MapStructure.Require(cellCount >= 0, "tectonics", "The cell count must be nonnegative.");
		MapStructure.Length(PlateOfCell.Count, cellCount, "tectonics.plateOfCell");
		for (int i = 0; i < Plates.Count; i++)
		{
			PlateSnapshot plate = Plates[i];
			MapStructure.Require(plate.PlateId == i, "tectonics.plates.plateId", "A plate ID must equal its row index.", index: i);
			MapStructure.Finite(plate.SeedTheta, "tectonics.plates.seedTheta", i);
			MapStructure.Finite(plate.SeedPhi, "tectonics.plates.seedPhi", i);
			plate.Omega.Validate("tectonics.plates.omega", i);
			MapStructure.Finite(plate.Density, "tectonics.plates.density", i);
		}
		for (int i = 0; i < cellCount; i++) MapStructure.Index(PlateOfCell[i], Plates.Count, "tectonics.plateOfCell", index: i);
		Crust.Validate(cellCount, Plates.Count);
		PlateBoundaries.Validate(cellCount, Plates.Count, PlateOfCell);
		BoundaryFields.Validate(PlateBoundaries.Edges.Count);
		FinalBoundary.Validate(PlateBoundaries.Edges.Count);
		MapStructure.Length(Channels.Count, 6, "tectonics.channels");
		var seenChannels = new HashSet<TectonicChannel>();
		for (int i = 0; i < Channels.Count; i++)
		{
			TectonicInfluenceSnapshot channel = MapStructure.Present(Channels[i], "tectonics.channels");
			channel.Validate(cellCount, PlateBoundaries.Edges);
			MapStructure.Require(seenChannels.Add(channel.Channel), "tectonics.channels", "Each of the six channels must occur once.", index: i);
		}
	}
}

// Protocol codes are semantic identities, not CLR enum ordinals (BoundaryKind differs today).
internal static class MapGeologyWire
{
	public static byte ToCrustType(CrustType value) => value switch
	{
		CrustType.Oceanic => 0, CrustType.Continental => 1,
		_ => throw Unknown("crustType"),
	};
	public static CrustType FromCrustType(byte value) => value switch
	{
		0 => CrustType.Oceanic, 1 => CrustType.Continental,
		_ => throw Unknown("crustType"),
	};
	public static byte ToBoundaryKind(BoundaryKind value) => value switch
	{
		BoundaryKind.Inactive => 0, BoundaryKind.Convergent => 1, BoundaryKind.Divergent => 2, BoundaryKind.Transform => 3,
		_ => throw Unknown("boundaryKind"),
	};
	public static BoundaryKind FromBoundaryKind(byte value) => value switch
	{
		0 => BoundaryKind.Inactive, 1 => BoundaryKind.Convergent, 2 => BoundaryKind.Divergent, 3 => BoundaryKind.Transform,
		_ => throw Unknown("boundaryKind"),
	};
	public static byte ToBoundarySideRole(BoundarySideRole value) => value switch
	{
		BoundarySideRole.None => 0, BoundarySideRole.Overriding => 1, BoundarySideRole.Underthrusting => 2,
		_ => throw Unknown("boundarySideRole"),
	};
	public static BoundarySideRole FromBoundarySideRole(byte value) => value switch
	{
		0 => BoundarySideRole.None, 1 => BoundarySideRole.Overriding, 2 => BoundarySideRole.Underthrusting,
		_ => throw Unknown("boundarySideRole"),
	};
	public static byte ToConvergenceKind(ConvergenceKind value) => value switch
	{
		ConvergenceKind.None => 0, ConvergenceKind.Collision => 1, ConvergenceKind.Subduction => 2,
		_ => throw Unknown("convergenceKind"),
	};
	public static ConvergenceKind FromConvergenceKind(byte value) => value switch
	{
		0 => ConvergenceKind.None, 1 => ConvergenceKind.Collision, 2 => ConvergenceKind.Subduction,
		_ => throw Unknown("convergenceKind"),
	};
	public static byte ToTectonicChannel(TectonicChannel value) => value switch
	{
		TectonicChannel.ContinentalCollision => 0, TectonicChannel.ContinentalArc => 1, TectonicChannel.OceanicArc => 2,
		TectonicChannel.Subduction => 3, TectonicChannel.ContinentalRift => 4, TectonicChannel.OceanicRidge => 5,
		_ => throw Unknown("tectonicChannel"),
	};
	public static TectonicChannel FromTectonicChannel(byte value) => value switch
	{
		0 => TectonicChannel.ContinentalCollision, 1 => TectonicChannel.ContinentalArc, 2 => TectonicChannel.OceanicArc,
		3 => TectonicChannel.Subduction, 4 => TectonicChannel.ContinentalRift, 5 => TectonicChannel.OceanicRidge,
		_ => throw Unknown("tectonicChannel"),
	};

	static MapStructureException Unknown(string location)
		=> new(new MapIssue(MapIssueCode.InvalidValue, "tectonics.enums." + location, null, "Unknown enum semantic value or wire code."));
}