using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.Data;
using World.Data.Maps;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 从一次已发布的世界事实捕获独立地图结果（导出用）：只读复制，不跑任何生成阶段。
/// 产出清单只有头信息；完整数据块目录由 <c>MapPackageWriter</c> 生成后校验。
/// </summary>
internal static class GeographySnapshotCapture
{
	internal static GeographySnapshot Capture(WorldFacts facts, Guid mapId, string title, string description,
		DateTimeOffset createdUtc, string producerVersion)
	{
		if (facts == null) throw new ArgumentNullException(nameof(facts));
		var manifest = new GeographyManifest(
			GeographyManifest.CurrentFormat, GeographyManifest.CurrentSchemaVersion, mapId, title, description,
			createdUtc.ToUniversalTime(), producerVersion, facts.Ball.Res, facts.Ball.CellIds.Length,
			SphericalFbmNoise.WorldRadiusKm, facts.Ball.Radius, facts.SeaLevelM, 12, facts.SourceTimeMa,
			facts.Surface.Projected != null, Array.Empty<MapBlockDescriptor>());
		return new GeographySnapshot(manifest,
			new GridSnapshot(facts.Ball.CellIds),
			new TerrainSnapshot(facts.HeightM, facts.FinalLand, facts.FinalLandFraction, facts.FinalLandmassId,
				facts.FinalLandmassCount, facts.FinalDistToCoast, facts.FinalDistToLand),
			CaptureSurface(facts.Surface),
			CaptureTectonics(facts),
			CaptureClimate(facts),
			CaptureHydrology(facts),
			new QueryIndexSnapshot(facts.SpatialIndex.NearestLandmassId, facts.SpatialIndex.NearestLandmassDistHops,
				facts.SpatialIndex.NearestRiverCell, facts.SpatialIndex.NearestRiverDistHops));
	}

	static SurfaceSnapshot CaptureSurface(SurfaceFacts surface)
	{
		ProjectedSurfaceSnapshot projected = null;
		if (surface.Projected != null)
		{
			var source = surface.Projected;
			var physical = new Double3Snapshot[surface.MotionOmega.Length];
			for (int i = 0; i < physical.Length; i++)
			{
				// Evolution keeps the original f64 angular velocity; static results only have the f32 display value.
				if (surface.PhysicalOmegaFlat != null)
					physical[i] = new Double3Snapshot(surface.PhysicalOmegaFlat[i * 3],
						surface.PhysicalOmegaFlat[i * 3 + 1], surface.PhysicalOmegaFlat[i * 3 + 2]);
				else
				{
					Vector3 omega = surface.MotionOmega[i];
					physical[i] = new Double3Snapshot(omega.X, omega.Y, omega.Z);
				}
			}
			projected = new ProjectedSurfaceSnapshot(
				source.EmergentFraction, source.LandHeightM, source.WaterHeightM, source.MaterialHeightM,
				source.OceanicCoverage, SaveNullable(source.OceanAgeMa), physical,
				source.VolcanicRateKmPerMa, source.EmergentLandFraction);
		}
		return new SurfaceSnapshot(surface.SurfaceCrustType, ToFloat3(surface.MotionOmega), projected);
	}

	static TectonicsSnapshot CaptureTectonics(WorldFacts facts)
	{
		var plates = facts.Plates.Select(plate => new PlateSnapshot(plate.PlateId, plate.SeedTheta, plate.SeedPhi,
			new Float3Snapshot(plate.Omega.X, plate.Omega.Y, plate.Omega.Z), plate.Density)).ToArray();
		var provinces = facts.Crust.Provinces.Select(p =>
			new CrustProvinceSnapshot(p.Id, p.Type, p.SeedCell, p.SeedPlateId, p.CellCount)).ToArray();
		var crust = new CrustSnapshot(facts.Crust.Type, facts.Crust.ProvinceId, provinces);
		var boundaries = facts.PlateBoundaries;
		var edges = boundaries.Edges.Select(e => new BoundaryEdgeSnapshot(e.CellA, e.CellB, e.PlateA, e.PlateB)).ToArray();
		var pairs = boundaries.Pairs.Select(p =>
			new BoundaryPairSnapshot(p.PlateA, p.PlateB, p.EdgeStart, p.EdgeCount)).ToArray();
		var boundaryFields = facts.BoundaryFields;
		var fields = new BoundaryFieldsSnapshot(boundaryFields.EdgeMotion, boundaryFields.EdgeKind,
			boundaryFields.EdgeRoleA, boundaryFields.EdgeRoleB);
		FinalBoundaryFacts final = facts.Surface.Boundaries;
		var finalBoundary = new FinalBoundarySnapshot(final.SideTypeA, final.SideTypeB, final.Convergence,
			SaveNullable(final.NormalKmPerMa), SaveNullable(final.ShearKmPerMa));
		var channels = new List<TectonicInfluenceSnapshot>();
		foreach (TectonicChannel channel in Enum.GetValues<TectonicChannel>())
		{
			TectonicInfluenceField field = facts.Tectonic.Get(channel);
			var sources = field.Sources.Select(s =>
				new TectonicSourceSnapshot(s.EdgeIndex, s.SideCell, s.Channel)).ToArray();
			channels.Add(new TectonicInfluenceSnapshot(channel, sources, field.DistanceKm, field.SourceIndex));
		}
		return new TectonicsSnapshot(plates, crust, facts.PlateOfCell,
			new PlateBoundariesSnapshot(boundaries.PlateCount, edges, pairs,
				boundaries.CellEdgeStart, boundaries.CellEdgeIndex),
			fields, finalBoundary, channels);
	}

	static ClimateSnapshot CaptureClimate(WorldFacts facts)
	{
		return new ClimateSnapshot(
			facts.PrecipitationMm, FlattenMonthlyScalar(facts.PrecipitationMonthlyMm),
			facts.TemperatureC, FlattenMonthlyScalar(facts.TemperatureMonthlyC), facts.HotMonthC, facts.ColdMonthC,
			facts.PressureAnnualHpa, FlattenMonthlyScalar(facts.PressureMonthlyHpa),
			facts.DryMonthMm, facts.DryMonthIndex, facts.MonsoonStrength,
			CaptureWind(facts.Wind.Surface, World.Data.Maps.WindKind.Surface),
			CaptureWind(facts.Wind.Low, World.Data.Maps.WindKind.Low),
			CaptureWind(facts.Wind.Free, World.Data.Maps.WindKind.Free),
			ToFloat3Flat(facts.OceanCurrentDirection), facts.OceanCurrentWarmth, facts.OceanCurrentStrength);
	}

	static WindSetSnapshot CaptureWind(WindSet set, World.Data.Maps.WindKind kind)
		=> new(kind, FlattenMonthly(set.Monthly), FlattenAnnual(set.Annual), set.AnnualSpeed);

	static float[] FlattenMonthlyScalar(float[][] monthly)
	{
		int months = monthly.Length;
		int cells = months == 0 ? 0 : monthly[0].Length;
		var flattened = new float[checked(months * cells)];
		for (int month = 0; month < months; month++)
			for (int cell = 0; cell < cells; cell++)
				flattened[month * cells + cell] = monthly[month][cell];
		return flattened;
	}

	static float[] ToFloat3Flat(Vector3[] values)
	{
		var flattened = new float[checked(values.Length * 3)];
		for (int i = 0; i < values.Length; i++)
		{
			flattened[i * 3] = values[i].X;
			flattened[i * 3 + 1] = values[i].Y;
			flattened[i * 3 + 2] = values[i].Z;
		}
		return flattened;
	}

	static HydrologySnapshot CaptureHydrology(WorldFacts facts)
	{
		RiverNetwork rivers = facts.Rivers;
		HydrologyRoutingSurface routing = rivers.Routing;
		var routingSnapshot = new HydrologyRoutingSnapshot(routing.UsedFill, routing.RoutingHeightM,
			routing.FillDepthM, routing.DepressionCount, routing.MaxFillDepthM, routing.FilledCellCount);
		var riversSnapshot = new RiversSnapshot(rivers.Downstream, rivers.FlowAccum, rivers.FlowAccumKm2,
			rivers.IsRiver, rivers.SinkCount, rivers.RunoffAccum, rivers.RunoffAccumKm3, rivers.RunoffThreshold,
			rivers.RiverThresholdKm2, routingSnapshot);

		RiverGraph graph = facts.RiverTopology;
		var topology = new RiverTopologySnapshot(graph.DownstreamRiver, graph.UpstreamCount, graph.NodeKind,
			graph.AccumMm, graph.RiverCellCount, graph.SourceCount, graph.ConfluenceCount, graph.OutletCount,
			graph.Sources, graph.Outlets);

		RiverGeometry lines = facts.RiverLines;
		var offsets = new int[lines.LineCells.Count + 1];
		var cells = new List<int>();
		var points = new List<float>();
		for (int line = 0; line < lines.LineCells.Count; line++)
		{
			offsets[line] = cells.Count;
			cells.AddRange(lines.LineCells[line]);
			foreach (Vector3 point in lines.Lines[line])
			{
				points.Add(point.X);
				points.Add(point.Y);
				points.Add(point.Z);
			}
		}
		offsets[offsets.Length - 1] = cells.Count;
		var riverLines = new RiverLinesSnapshot(lines.SourceRes, offsets, cells, points, lines.LineOutlet);

		BasinGraph basins = facts.Basins;
		var basinSnapshot = new BasinsSnapshot(basins.BasinId, basins.TerminalCell, basins.TerminatesAtOcean,
			basins.BasinCount, basins.Kind, basins.Outlet, basins.Area, basins.AreaKm2, basins.AccumMm,
			basins.OceanBasinCount, basins.EndorheicBasinCount);

		LakeState lakes = facts.Lakes;
		var lakeSnapshot = new LakesSnapshot(lakes.LakeCount, lakes.DepressionIdOf, lakes.BasinIdOf,
			lakes.LakeOfBasin, lakes.SurfaceElevationM, lakes.AreaKm2, lakes.VolumeKm3,
			lakes.SpillElevationM, lakes.OutletCell);

		WaterTopology water = facts.WaterSystem;
		var waterSnapshot = new WaterSystemSnapshot(water.NodeCount, water.KindOf, water.RefOf, water.Next,
			water.ViaCell, water.NodeOfRiverCell, water.NodeOfLake, water.OceanNode,
			water.RiverCount, water.LakeNodeCount, water.SinkCount);

		return new HydrologySnapshot(riversSnapshot, topology, riverLines, basinSnapshot, lakeSnapshot, waterSnapshot);
	}

	static NullableFloat64Snapshot SaveNullable(IReadOnlyList<double?> values)
	{
		var validity = new byte[values.Count];
		var actual = new double[values.Count];
		for (int i = 0; i < values.Count; i++)
		{
			if (!values[i].HasValue) continue;
			validity[i] = 1;
			actual[i] = values[i].Value;
		}
		return new NullableFloat64Snapshot(validity, actual);
	}

	static Float3Snapshot[] ToFloat3(Vector3[] values)
		=> values.Select(value => new Float3Snapshot(value.X, value.Y, value.Z)).ToArray();

	static float[] FlattenMonthly(Vector3[][] monthly)
	{
		int months = monthly.Length;
		int cells = months == 0 ? 0 : monthly[0].Length;
		var flattened = new float[checked(months * cells * 3)];
		for (int month = 0; month < months; month++)
			for (int cell = 0; cell < cells; cell++)
			{
				Vector3 value = monthly[month][cell];
				int index = (month * cells + cell) * 3;
				flattened[index] = value.X;
				flattened[index + 1] = value.Y;
				flattened[index + 2] = value.Z;
			}
		return flattened;
	}

	static float[] FlattenAnnual(Vector3[] annual)
	{
		var flattened = new float[checked(annual.Length * 3)];
		for (int i = 0; i < annual.Length; i++)
		{
			flattened[i * 3] = annual[i].X;
			flattened[i * 3 + 1] = annual[i].Y;
			flattened[i * 3 + 2] = annual[i].Z;
		}
		return flattened;
	}
}