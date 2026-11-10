using System;
using System.Linq;
using World.Data;
using World.Data.Maps;

namespace World.Tests;

internal static class MapGeologyTests
{
	public static void SurfaceFreezesInputAndReturnedCopies()
	{
		var types = new[] { CrustType.Oceanic, CrustType.Continental };
		var motion = new[] { new Float3Snapshot(1, 2, 3), new Float3Snapshot(4, 5, 6) };
		var surface = new SurfaceSnapshot(types, motion, null);
		types[0] = CrustType.Continental;
		motion[0] = default;
		CrustType[] copy = surface.SurfaceCrustType.ToArray();
		copy[0] = CrustType.Continental;
		surface.Validate(2, 2);
		Program.True(surface.SurfaceCrustType[0] == CrustType.Oceanic && surface.MotionOmega[0].X == 1,
			"Surface must own frozen copies of inputs and returned arrays.");
	}

	public static void ProjectedSurfacePreservesAllFactsAndNullAge()
	{
		SurfaceSnapshot surface = Surface(Projected());
		surface.Validate(2, 2);
		ProjectedSurfaceSnapshot projected = surface.Projected;
		Program.True(projected.OceanAgeMa[0] == null && projected.OceanAgeMa[1] == 0,
			"A masked age is null, distinct from a valid zero age.");
		Program.True(projected.MaterialHeightM[0] == 125 && projected.LandHeightM[0] == 300 && projected.WaterHeightM[0] == -20,
			"Mixed coast and material heights must remain separate facts.");
		Program.True(projected.OceanicCoverage[0] == 0.375 && projected.EmergentFraction[0] == 0.5f && projected.EmergentLandFraction == 0.234567890123,
			"Aggregate emergent fraction is preserved, not recomputed from cell values.");
		Program.True(projected.PhysicalOmegaRadPerMa[0].X == 1.000000000001 && projected.VolcanicRateKmPerMa[1] == 0.25,
			"Physical f64 motion and volcanic rates must retain their source precision.");
	}

	public static void SurfaceRejectsMissingGroupMembersShapesAndUnknownCrust()
	{
		Program.Throws(() => new ProjectedSurfaceSnapshot(new float[2], new float[2], new float[2], new float[2], new double[2],
			null, new Double3Snapshot[2], new double[2], 0), "A projected group cannot omit the age column.");
		Program.Throws(() => new SurfaceSnapshot(new[] { CrustType.Oceanic }, new Float3Snapshot[2], null).Validate(2, 2),
			"Surface cell shapes must match N.");
		Program.Throws(() => new SurfaceSnapshot(new CrustType[2], new Float3Snapshot[1], null).Validate(2, 2),
			"Surface motion shape must match P.");
		Program.Throws(() => new SurfaceSnapshot(new[] { (CrustType)255, CrustType.Oceanic }, new Float3Snapshot[2], null).Validate(2, 2),
			"Unknown final surface crust must be rejected.");
		Program.Throws(() => Surface(Projected(landHeight: new[] { float.NaN, 0f })).Validate(2, 2),
			"Projected height must be finite.");
	}

	public static void SurfaceRejectsOutOfRangeFactsAndMotionMismatch()
	{
		Program.Throws(() => Surface(Projected(emergent: new[] { 1.01f, 0f })).Validate(2, 2), "Emergent fraction must be in [0,1].");
		Program.Throws(() => Surface(Projected(coverage: new[] { 0d, double.PositiveInfinity })).Validate(2, 2), "Oceanic coverage must be finite in [0,1].");
		Program.Throws(() => Surface(Projected(volcanic: new[] { 0d, -0.1 })).Validate(2, 2), "Volcanic rate cannot be negative.");
		Program.Throws(() => Surface(Projected(aggregate: 1.01)).Validate(2, 2), "Aggregate emergent fraction must be in [0,1].");
		Program.Throws(() => new SurfaceSnapshot(new CrustType[2], new Float3Snapshot[2], Projected()).Validate(2, 2),
			"Display omega must exactly match conversion of physical f64 omega to f32.");
		Program.Throws(() => new SurfaceSnapshot(new CrustType[2], new[] { new Float3Snapshot(float.PositiveInfinity, 0, 0), default }, null).Validate(2, 2),
			"Effective display motion must be finite.");
	}

	public static void NullableGeologyColumnsValidateMasksAndActiveValues()
	{
		var masks = new byte[] { 0, 1 };
		var values = new[] { 0d, -2.5 };
		var nullable = new NullableFloat64Snapshot(masks, values);
		masks[0] = 1;
		values[1] = 99;
		nullable.Validate(2, "test.speed");
		Program.True(nullable[0] == null && nullable[1] == -2.5, "Nullable columns must freeze masks and preserve signed physical speed.");
		Program.Throws(() => new NullableFloat64Snapshot(new byte[] { 2 }, new[] { 0d }).Validate(1, "test.mask"), "Mask codes other than 0/1 must be rejected.");
		Program.Throws(() => new NullableFloat64Snapshot(new byte[] { 1 }, new[] { double.NaN }).Validate(1, "test.value"), "Active nullable values must be finite.");
		Program.Throws(() => new NullableFloat64Snapshot(new byte[] { 1 }, Array.Empty<double>()).Validate(1, "test.shape"), "Mask and payload shapes must agree.");
		Program.Throws(() => Surface(Projected(age: new NullableFloat64Snapshot(new byte[] { 0, 1 }, new[] { 0d, -1d }))).Validate(2, 2),
			"Valid ocean age cannot be negative.");
	}

	public static void TectonicsPreservesOrderingAndAllowsMovedProvinceSeed()
	{
		var edges = new[] { new BoundaryEdgeSnapshot(1, 2, 0, 1), new BoundaryEdgeSnapshot(0, 2, 0, 1) };
		var boundaries = new PlateBoundariesSnapshot(2, edges, Pairs(2), new[] { 0, 1, 2, 4 }, new[] { 1, 0, 1, 0 });
		var tectonics = Tectonics(cellCount: 3, boundaries: boundaries,
			crust: new CrustSnapshot(new[] { CrustType.Continental, CrustType.Oceanic, CrustType.Oceanic }, new[] { 0, -1, -1 },
				new[] { new CrustProvinceSnapshot(0, CrustType.Continental, 2, 1, 1) }));
		edges[0] = default;
		tectonics.Validate(3);
		Program.True(tectonics.PlateBoundaries.Edges[0].CellA == 1 && tectonics.PlateBoundaries.CellEdgeIndex[2] == 1,
			"Edge and CSR ordering must survive validation without sorting.");
		Program.True(tectonics.Crust.Provinces[0].SeedCell == 2, "A historical province seed need not still belong to its province.");
		Program.True(tectonics.FinalBoundary.NormalKmPerMa[0] == null, "Static physical speeds must retain null masks.");
	}

	public static void TectonicsRejectsPlateAndProvinceReferences()
	{
		Program.Throws(() => Tectonics(plates: new[] { new PlateSnapshot(1, 0, 0, default, 1), new PlateSnapshot(1, 0, 0, default, 1) }).Validate(2),
			"Plate IDs must equal row indices.");
		Program.Throws(() => Tectonics(plateOfCell: new[] { 0, 2 }).Validate(2), "PlateOfCell must reference the plate domain.");
		Program.Throws(() => Tectonics(crust: new CrustSnapshot(new[] { CrustType.Continental, CrustType.Oceanic }, new[] { -1, -1 }, Array.Empty<CrustProvinceSnapshot>())).Validate(2),
			"Only oceanic crust permits province -1.");
		Program.Throws(() => Tectonics(crust: new CrustSnapshot(new[] { CrustType.Oceanic, CrustType.Oceanic }, new[] { 0, -1 },
			new[] { new CrustProvinceSnapshot(0, CrustType.Continental, 0, 0, 1) })).Validate(2), "Cell and province crust types must agree.");
		Program.Throws(() => Tectonics(crust: new CrustSnapshot(new[] { CrustType.Oceanic, CrustType.Oceanic }, new[] { 0, -1 },
			new[] { new CrustProvinceSnapshot(0, CrustType.Oceanic, 0, 0, 2) })).Validate(2), "Province cell counts must match membership.");
		Program.Throws(() => Tectonics(crust: new CrustSnapshot(new[] { CrustType.Oceanic, CrustType.Oceanic }, new[] { 0, -1 },
			new[] { new CrustProvinceSnapshot(0, CrustType.Oceanic, 2, 0, 1) })).Validate(2), "Province seed cells must remain in the cell domain.");
	}

	public static void TectonicsRejectsMalformedBoundaryPairsAndCsr()
	{
		Program.Throws(() => Tectonics(boundaries: new PlateBoundariesSnapshot(2, new[] { new BoundaryEdgeSnapshot(1, 0, 1, 0) },
			Pairs(1), new[] { 0, 1, 2 }, new[] { 0, 0 })).Validate(2), "Boundary endpoints must be in original CellA < CellB convention.");
		Program.Throws(() => Tectonics(boundaries: new PlateBoundariesSnapshot(2, new[] { new BoundaryEdgeSnapshot(0, 1, 1, 0) },
			Pairs(1), new[] { 0, 1, 2 }, new[] { 0, 0 })).Validate(2), "Edge plate ownership must match the cell table.");
		Program.Throws(() => Tectonics(boundaries: new PlateBoundariesSnapshot(2, new[] { new BoundaryEdgeSnapshot(0, 1, 0, 1) },
			new BoundaryPairSnapshot[4], new[] { 0, 1, 2 }, new[] { 0, 0 })).Validate(2), "Every edge must be covered by a pair range.");
		Program.Throws(() => Tectonics(boundaries: new PlateBoundariesSnapshot(2, new[] { new BoundaryEdgeSnapshot(0, 1, 0, 1) },
			Pairs(1), new[] { 0, 2, 2 }, new[] { 0, 0 })).Validate(2), "A repeated incidence cannot replace the opposite endpoint incidence.");
		Program.Throws(() => Tectonics(boundaries: new PlateBoundariesSnapshot(2, new[] { new BoundaryEdgeSnapshot(0, 1, 0, 1) },
			Pairs(1), new[] { 1, 1, 2 }, new[] { 0, 0 })).Validate(2), "CSR must start at zero.");
		Program.Throws(() => Tectonics(boundaries: new PlateBoundariesSnapshot(2, new[] { new BoundaryEdgeSnapshot(0, 1, 0, 1) },
			Pairs(1), new[] { 0, 1, 2 }, new[] { 0, 1 })).Validate(2), "CSR edge references must be in range.");
	}

	public static void BoundaryFieldsRetainClassificationsAndSignedPhysicalSpeeds()
	{
		var final = new FinalBoundarySnapshot(new[] { CrustType.Continental }, new[] { CrustType.Oceanic }, new[] { ConvergenceKind.Subduction },
			new NullableFloat64Snapshot(new byte[] { 1 }, new[] { -12.75 }), new NullableFloat64Snapshot(new byte[] { 1 }, new[] { -3.25 }));
		var tectonics = Tectonics(finalBoundary: final);
		tectonics.Validate(2);
		Program.True(final.NormalKmPerMa[0] == -12.75 && final.ShearKmPerMa[0] == -3.25 && final.ConvergenceKind[0] == ConvergenceKind.Subduction,
			"Final side classification and signed physical speeds must not be normalized or recomputed.");
		Program.Throws(() => Tectonics(fields: new BoundaryFieldsSnapshot(new[] { float.NaN }, new[] { BoundaryKind.Inactive },
			new[] { BoundarySideRole.None }, new[] { BoundarySideRole.None })).Validate(2), "Normalized edge motion must remain finite.");
		Program.Throws(() => Tectonics(finalBoundary: new FinalBoundarySnapshot(new[] { CrustType.Oceanic }, new[] { CrustType.Oceanic },
			new[] { (ConvergenceKind)255 }, Nulls(1), Nulls(1))).Validate(2), "Unknown final convergence classification must be rejected.");
	}

	public static void InfluenceChannelsPreserveEmptyAndMissingSourceSemantics()
	{
		TectonicInfluenceSnapshot[] channels = EmptyChannels();
		channels[0] = new TectonicInfluenceSnapshot(TectonicChannel.ContinentalCollision,
			new[] { new TectonicSourceSnapshot(0, 0, TectonicChannel.ContinentalCollision) }, new[] { 0f, float.PositiveInfinity }, new[] { 0, -1 });
		var tectonics = Tectonics(channels: channels);
		channels[0] = null;
		tectonics.Validate(2);
		Program.True(tectonics.Channels[0].SourceIndex[1] == -1 && float.IsPositiveInfinity(tectonics.Channels[0].DistanceKm[1]),
			"Missing nearest source allows positive infinity while retaining -1.");
		Program.True(tectonics.Channels.Skip(1).All(c => c.Sources.Count == 0 && c.DistanceKm.Count == 0 && c.SourceIndex.Count == 0),
			"All six channels exist; source-free channels retain genuinely empty arrays.");
	}

	public static void InfluenceChannelsRejectIncompleteOrInvalidStructures()
	{
		Program.Throws(() => Tectonics(channels: EmptyChannels().Take(5).ToArray()).Validate(2), "All six channels are required.");
		TectonicInfluenceSnapshot[] duplicate = EmptyChannels();
		duplicate[5] = duplicate[0];
		Program.Throws(() => Tectonics(channels: duplicate).Validate(2), "Each channel must occur exactly once.");
		RejectInfluence(new TectonicInfluenceSnapshot(TectonicChannel.ContinentalCollision, Array.Empty<TectonicSourceSnapshot>(), new float[2], new[] { -1, -1 }));
		RejectInfluence(Influence(new[] { 0f, float.PositiveInfinity }, new[] { 0, 0 }));
		RejectInfluence(Influence(new[] { 0f, float.NaN }, new[] { 0, -1 }));
		RejectInfluence(Influence(new[] { 0f, float.NegativeInfinity }, new[] { 0, -1 }));
		RejectInfluence(Influence(new[] { 0f, 1f }, new[] { 0, 1 }));
		RejectInfluence(new TectonicInfluenceSnapshot(TectonicChannel.ContinentalCollision,
			new[] { new TectonicSourceSnapshot(0, 2, TectonicChannel.ContinentalCollision) }, new float[2], new int[2]));
		RejectInfluence(new TectonicInfluenceSnapshot(TectonicChannel.ContinentalCollision,
			new[] { new TectonicSourceSnapshot(0, 0, TectonicChannel.OceanicArc) }, new float[2], new int[2]));
	}

	public static void GeologyWireMappingsUseSemanticCodesAndRejectUnknowns()
	{
		Program.True(MapGeologyWire.ToBoundaryKind(BoundaryKind.Inactive) == 0 && MapGeologyWire.ToBoundaryKind(BoundaryKind.Transform) == 3,
			"Boundary wire codes deliberately differ from CLR ordinals.");
		foreach (CrustType value in Enum.GetValues<CrustType>()) Program.True(MapGeologyWire.FromCrustType(MapGeologyWire.ToCrustType(value)) == value, "Crust enum must round trip semantically.");
		foreach (BoundaryKind value in Enum.GetValues<BoundaryKind>()) Program.True(MapGeologyWire.FromBoundaryKind(MapGeologyWire.ToBoundaryKind(value)) == value, "Boundary enum must round trip semantically.");
		foreach (BoundarySideRole value in Enum.GetValues<BoundarySideRole>()) Program.True(MapGeologyWire.FromBoundarySideRole(MapGeologyWire.ToBoundarySideRole(value)) == value, "Side role must round trip semantically.");
		foreach (ConvergenceKind value in Enum.GetValues<ConvergenceKind>()) Program.True(MapGeologyWire.FromConvergenceKind(MapGeologyWire.ToConvergenceKind(value)) == value, "Convergence enum must round trip semantically.");
		foreach (TectonicChannel value in Enum.GetValues<TectonicChannel>()) Program.True(MapGeologyWire.FromTectonicChannel(MapGeologyWire.ToTectonicChannel(value)) == value, "Channel enum must round trip semantically.");
		Program.Throws(() => MapGeologyWire.FromCrustType(255), "Unknown crust wire codes must be rejected.");
		Program.Throws(() => MapGeologyWire.FromBoundaryKind(255), "Unknown boundary wire codes must be rejected.");
		Program.Throws(() => MapGeologyWire.FromBoundarySideRole(255), "Unknown side role wire codes must be rejected.");
		Program.Throws(() => MapGeologyWire.FromConvergenceKind(255), "Unknown convergence wire codes must be rejected.");
		Program.Throws(() => MapGeologyWire.FromTectonicChannel(255), "Unknown channel wire codes must be rejected.");
		Program.Throws(() => MapGeologyWire.ToCrustType((CrustType)255), "Unknown crust enum values must be rejected.");
		Program.Throws(() => MapGeologyWire.ToBoundaryKind((BoundaryKind)255), "Unknown boundary enum values must be rejected.");
		Program.Throws(() => MapGeologyWire.ToBoundarySideRole((BoundarySideRole)255), "Unknown role enum values must be rejected.");
		Program.Throws(() => MapGeologyWire.ToConvergenceKind((ConvergenceKind)255), "Unknown convergence enum values must be rejected.");
		Program.Throws(() => MapGeologyWire.ToTectonicChannel((TectonicChannel)255), "Unknown channel enum values must be rejected.");
	}

	static SurfaceSnapshot Surface(ProjectedSurfaceSnapshot projected) => new(new[] { CrustType.Continental, CrustType.Oceanic },
		new[] { new Float3Snapshot(1, 2, 3), new Float3Snapshot(4, 5, 6) }, projected);

	static ProjectedSurfaceSnapshot Projected(float[] emergent = null, float[] landHeight = null, double[] coverage = null,
		NullableFloat64Snapshot age = null, double[] volcanic = null, double aggregate = 0.234567890123)
		=> new(emergent ?? new[] { 0.5f, 0f }, landHeight ?? new[] { 300f, -100f }, new[] { -20f, -2000f }, new[] { 125f, -2500f },
			coverage ?? new[] { 0.375, 1d }, age ?? new NullableFloat64Snapshot(new byte[] { 0, 1 }, new[] { 0d, 0d }),
			new[] { new Double3Snapshot(1.000000000001, 2, 3), new Double3Snapshot(4, 5, 6) }, volcanic ?? new[] { 0d, 0.25 }, aggregate);

	static TectonicsSnapshot Tectonics(int cellCount = 2, PlateSnapshot[] plates = null, CrustSnapshot crust = null,
		int[] plateOfCell = null, PlateBoundariesSnapshot boundaries = null, BoundaryFieldsSnapshot fields = null,
		FinalBoundarySnapshot finalBoundary = null, TectonicInfluenceSnapshot[] channels = null)
	{
		boundaries ??= new PlateBoundariesSnapshot(2, new[] { new BoundaryEdgeSnapshot(0, 1, 0, 1) }, Pairs(1), new[] { 0, 1, 2 }, new[] { 0, 0 });
		int edgeCount = boundaries.Edges.Count;
		return new TectonicsSnapshot(plates ?? new[] { new PlateSnapshot(0, 0, 0, new Float3Snapshot(1, 2, 3), 1), new PlateSnapshot(1, 0, 0, new Float3Snapshot(4, 5, 6), 2) },
			crust ?? new CrustSnapshot(new CrustType[cellCount], Enumerable.Repeat(-1, cellCount), Array.Empty<CrustProvinceSnapshot>()),
			plateOfCell ?? Enumerable.Range(0, cellCount).Select(i => i == cellCount - 1 ? 1 : 0), boundaries,
			fields ?? new BoundaryFieldsSnapshot(new float[edgeCount], Enumerable.Repeat(BoundaryKind.Inactive, edgeCount), new BoundarySideRole[edgeCount], new BoundarySideRole[edgeCount]),
			finalBoundary ?? new FinalBoundarySnapshot(new CrustType[edgeCount], new CrustType[edgeCount], new ConvergenceKind[edgeCount], Nulls(edgeCount), Nulls(edgeCount)),
			channels ?? EmptyChannels());
	}

	static BoundaryPairSnapshot[] Pairs(int edgeCount) => new[] { default, new BoundaryPairSnapshot(0, 1, 0, edgeCount), default, default };
	static NullableFloat64Snapshot Nulls(int count) => new(new byte[count], new double[count]);
	static TectonicInfluenceSnapshot[] EmptyChannels() => Enum.GetValues<TectonicChannel>().Select(channel =>
		new TectonicInfluenceSnapshot(channel, Array.Empty<TectonicSourceSnapshot>(), Array.Empty<float>(), Array.Empty<int>())).ToArray();
	static TectonicInfluenceSnapshot Influence(float[] distance, int[] sourceIndex) => new(TectonicChannel.ContinentalCollision,
		new[] { new TectonicSourceSnapshot(0, 0, TectonicChannel.ContinentalCollision) }, distance, sourceIndex);
	static void RejectInfluence(TectonicInfluenceSnapshot influence)
	{
		TectonicInfluenceSnapshot[] channels = EmptyChannels();
		channels[0] = influence;
		Program.Throws(() => Tectonics(channels: channels).Validate(2), "Malformed influence references, shape or distances must be rejected.");
	}
}