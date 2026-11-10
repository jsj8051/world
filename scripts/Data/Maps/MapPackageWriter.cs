using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace World.Data.Maps;

/// <summary>
/// 写出 <c>.wmap</c> 地图包：先算完整块目录并以新清单复核结构，再写临时文件、成功后原子替换。
/// </summary>
internal static class MapPackageWriter
{
	internal static void Write(GeographySnapshot snapshot, string destinationPath)
	{
		if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
		if (string.IsNullOrWhiteSpace(destinationPath))
			throw MapPackageFormat.Failure(MapIssueCode.InvalidValue, "package.path", "A destination path is required.");
		if (File.Exists(destinationPath))
			throw MapPackageFormat.Failure(MapIssueCode.InvalidValue, "package.path", "The destination already exists.");

		var blocks = new BlockSet();
		AddAll(snapshot, blocks);
		GeographyManifest source = snapshot.Manifest;
		var manifest = new GeographyManifest(source.Format, source.SchemaVersion, source.MapId, source.Title,
			source.Description, source.CreatedUtc, source.ProducerVersion, source.H3Resolution, source.CellCount,
			source.PhysicalRadiusKm, source.RenderRadius, source.SeaLevelM, source.MonthCount, source.SourceTimeMa,
			source.HasProjectedSurface, blocks.Descriptors);
		// Re-run the full structural checks with the real block directory before touching disk.
		_ = new GeographySnapshot(manifest, snapshot.Grid, snapshot.Terrain, snapshot.Surface, snapshot.Tectonics,
			snapshot.Climate, snapshot.Hydrology, snapshot.QueryIndex);

		byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, MapPackageFormat.JsonOptions);
		if (json.Length > GeographyManifest.MaxManifestBytes)
			throw MapPackageFormat.Failure(MapIssueCode.SizeLimit, MapPackageFormat.ManifestEntry, "The manifest is too large.");

		string directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
		if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
		string temp = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
		try
		{
			using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
			{
				MapPackageFormat.WriteEntry(archive, MapPackageFormat.ManifestEntry, json);
				foreach (var entry in blocks.Entries)
					MapPackageFormat.WriteEntry(archive, entry.Path, entry.Data);
			}
			if (File.Exists(destinationPath))
				throw MapPackageFormat.Failure(MapIssueCode.InvalidValue, "package.path", "The destination appeared while writing.");
			File.Move(temp, destinationPath);
		}
		catch
		{
			if (File.Exists(temp)) File.Delete(temp);
			throw;
		}
	}

	static void AddAll(GeographySnapshot snapshot, BlockSet blocks)
	{
		int n = snapshot.Manifest.CellCount;
		int p = snapshot.Tectonics.Plates.Count;
		int q = snapshot.Tectonics.Crust.Provinces.Count;
		int e = snapshot.Tectonics.PlateBoundaries.Edges.Count;
		int l = snapshot.Hydrology.Lakes.SurfaceElevationM.Count;
		int r = snapshot.Hydrology.RiverLines.LineOutlet.Count;
		int k = snapshot.Hydrology.RiverLines.Cells.Count;

		blocks.Add("grid/cell_ids", MapBlockElementType.U64, new[] { n }, snapshot.Grid.CellIds.ToArray());
		blocks.Add("terrain/height_m", MapBlockElementType.F32, new[] { n }, snapshot.Terrain.HeightM.ToArray());
		blocks.Add("terrain/final_land", MapBlockElementType.U8, new[] { n }, snapshot.Terrain.FinalLand.ToArray());
		blocks.Add("terrain/final_land_fraction", MapBlockElementType.F32, new[] { n }, snapshot.Terrain.FinalLandFraction.ToArray());
		blocks.Add("terrain/final_landmass_id", MapBlockElementType.I32, new[] { n }, snapshot.Terrain.FinalLandmassId.ToArray());
		blocks.Add("terrain/final_landmass_count", MapBlockElementType.I32, new[] { 1 }, new[] { snapshot.Terrain.FinalLandmassCount });
		blocks.Add("terrain/final_dist_to_coast", MapBlockElementType.I32, new[] { n }, snapshot.Terrain.FinalDistToCoast.ToArray());
		blocks.Add("terrain/final_dist_to_land", MapBlockElementType.I32, new[] { n }, snapshot.Terrain.FinalDistToLand.ToArray());

		blocks.Add("surface/surface_crust_type", MapBlockElementType.U8, new[] { n },
			snapshot.Surface.SurfaceCrustType.Select(MapGeologyWire.ToCrustType).ToArray());
		blocks.Add("surface/motion_omega", MapBlockElementType.F32, new[] { p, 3 }, Flat(snapshot.Surface.MotionOmega));
		if (snapshot.Surface.Projected != null)
		{
			var projected = snapshot.Surface.Projected;
			blocks.Add("surface/projected/emergent_fraction", MapBlockElementType.F32, new[] { n }, projected.EmergentFraction.ToArray());
			blocks.Add("surface/projected/land_height_m", MapBlockElementType.F32, new[] { n }, projected.LandHeightM.ToArray());
			blocks.Add("surface/projected/water_height_m", MapBlockElementType.F32, new[] { n }, projected.WaterHeightM.ToArray());
			blocks.Add("surface/projected/material_height_m", MapBlockElementType.F32, new[] { n }, projected.MaterialHeightM.ToArray());
			blocks.Add("surface/projected/oceanic_coverage", MapBlockElementType.F64, new[] { n }, projected.OceanicCoverage.ToArray());
			blocks.AddNullable("surface/projected/ocean_age_ma", new[] { n },
				Enumerable.Range(0, projected.OceanAgeMa.Count).Select(i => projected.OceanAgeMa[i]).ToArray());
			blocks.Add("surface/projected/physical_omega_rad_per_ma", MapBlockElementType.F64, new[] { p, 3 },
				projected.PhysicalOmegaRadPerMa.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray());
			blocks.Add("surface/projected/volcanic_rate_km_per_ma", MapBlockElementType.F64, new[] { n },
				projected.VolcanicRateKmPerMa.ToArray());
			blocks.Add("surface/projected/emergent_land_fraction", MapBlockElementType.F64, new[] { 1 },
				new[] { projected.EmergentLandFraction });
		}

		var tectonics = snapshot.Tectonics;
		blocks.Add("tectonics/plates/plate_id", MapBlockElementType.I32, new[] { p },
			tectonics.Plates.Select(v => v.PlateId).ToArray());
		blocks.Add("tectonics/plates/seed_theta", MapBlockElementType.F32, new[] { p },
			tectonics.Plates.Select(v => v.SeedTheta).ToArray());
		blocks.Add("tectonics/plates/seed_phi", MapBlockElementType.F32, new[] { p },
			tectonics.Plates.Select(v => v.SeedPhi).ToArray());
		blocks.Add("tectonics/plates/density", MapBlockElementType.F32, new[] { p },
			tectonics.Plates.Select(v => v.Density).ToArray());
		blocks.Add("tectonics/plates/omega", MapBlockElementType.F32, new[] { p, 3 },
			tectonics.Plates.SelectMany(v => new[] { v.Omega.X, v.Omega.Y, v.Omega.Z }).ToArray());
		blocks.Add("tectonics/crust/type", MapBlockElementType.U8, new[] { n },
			tectonics.Crust.Type.Select(MapGeologyWire.ToCrustType).ToArray());
		blocks.Add("tectonics/crust/province_id", MapBlockElementType.I32, new[] { n },
			tectonics.Crust.ProvinceId.ToArray());
		blocks.Add("tectonics/crust/provinces/id", MapBlockElementType.I32, new[] { q },
			tectonics.Crust.Provinces.Select(v => v.Id).ToArray());
		blocks.Add("tectonics/crust/provinces/type", MapBlockElementType.U8, new[] { q },
			tectonics.Crust.Provinces.Select(v => MapGeologyWire.ToCrustType(v.Type)).ToArray());
		blocks.Add("tectonics/crust/provinces/seed_cell", MapBlockElementType.I32, new[] { q },
			tectonics.Crust.Provinces.Select(v => v.SeedCell).ToArray());
		blocks.Add("tectonics/crust/provinces/seed_plate_id", MapBlockElementType.I32, new[] { q },
			tectonics.Crust.Provinces.Select(v => v.SeedPlateId).ToArray());
		blocks.Add("tectonics/crust/provinces/cell_count", MapBlockElementType.I32, new[] { q },
			tectonics.Crust.Provinces.Select(v => v.CellCount).ToArray());
		blocks.Add("tectonics/plate_of_cell", MapBlockElementType.I32, new[] { n }, tectonics.PlateOfCell.ToArray());

		var boundaries = tectonics.PlateBoundaries;
		blocks.Add("tectonics/plate_boundaries/plate_count", MapBlockElementType.I32, new[] { 1 }, new[] { boundaries.PlateCount });
		blocks.Add("tectonics/plate_boundaries/edges/cell_a", MapBlockElementType.I32, new[] { e },
			boundaries.Edges.Select(v => v.CellA).ToArray());
		blocks.Add("tectonics/plate_boundaries/edges/cell_b", MapBlockElementType.I32, new[] { e },
			boundaries.Edges.Select(v => v.CellB).ToArray());
		blocks.Add("tectonics/plate_boundaries/edges/plate_a", MapBlockElementType.I32, new[] { e },
			boundaries.Edges.Select(v => v.PlateA).ToArray());
		blocks.Add("tectonics/plate_boundaries/edges/plate_b", MapBlockElementType.I32, new[] { e },
			boundaries.Edges.Select(v => v.PlateB).ToArray());
		blocks.Add("tectonics/plate_boundaries/pairs/plate_a", MapBlockElementType.I32, new[] { p * p },
			boundaries.Pairs.Select(v => v.PlateA).ToArray());
		blocks.Add("tectonics/plate_boundaries/pairs/plate_b", MapBlockElementType.I32, new[] { p * p },
			boundaries.Pairs.Select(v => v.PlateB).ToArray());
		blocks.Add("tectonics/plate_boundaries/pairs/edge_start", MapBlockElementType.I32, new[] { p * p },
			boundaries.Pairs.Select(v => v.EdgeStart).ToArray());
		blocks.Add("tectonics/plate_boundaries/pairs/edge_count", MapBlockElementType.I32, new[] { p * p },
			boundaries.Pairs.Select(v => v.EdgeCount).ToArray());
		blocks.Add("tectonics/plate_boundaries/cell_edge_start", MapBlockElementType.I32, new[] { n + 1 },
			boundaries.CellEdgeStart.ToArray());
		blocks.Add("tectonics/plate_boundaries/cell_edge_index", MapBlockElementType.I32, new[] { 2 * e },
			boundaries.CellEdgeIndex.ToArray());

		var fields = tectonics.BoundaryFields;
		blocks.Add("tectonics/boundary_fields/edge_motion", MapBlockElementType.F32, new[] { e }, fields.EdgeMotion.ToArray());
		blocks.Add("tectonics/boundary_fields/edge_kind", MapBlockElementType.U8, new[] { e },
			fields.EdgeKind.Select(MapGeologyWire.ToBoundaryKind).ToArray());
		blocks.Add("tectonics/boundary_fields/edge_role_a", MapBlockElementType.U8, new[] { e },
			fields.EdgeRoleA.Select(MapGeologyWire.ToBoundarySideRole).ToArray());
		blocks.Add("tectonics/boundary_fields/edge_role_b", MapBlockElementType.U8, new[] { e },
			fields.EdgeRoleB.Select(MapGeologyWire.ToBoundarySideRole).ToArray());

		var final = tectonics.FinalBoundary;
		blocks.Add("tectonics/final_boundary/side_type_a", MapBlockElementType.U8, new[] { e },
			final.SideTypeA.Select(MapGeologyWire.ToCrustType).ToArray());
		blocks.Add("tectonics/final_boundary/side_type_b", MapBlockElementType.U8, new[] { e },
			final.SideTypeB.Select(MapGeologyWire.ToCrustType).ToArray());
		blocks.Add("tectonics/final_boundary/convergence_kind", MapBlockElementType.U8, new[] { e },
			final.ConvergenceKind.Select(MapGeologyWire.ToConvergenceKind).ToArray());
		blocks.AddNullable("tectonics/final_boundary/normal_km_per_ma", new[] { e },
			Enumerable.Range(0, e).Select(i => final.NormalKmPerMa[i]).ToArray());
		blocks.AddNullable("tectonics/final_boundary/shear_km_per_ma", new[] { e },
			Enumerable.Range(0, e).Select(i => final.ShearKmPerMa[i]).ToArray());

		for (int channel = 0; channel < MapPackageFormat.ChannelNames.Length; channel++)
		{
			var influence = tectonics.Channels[channel];
			string prefix = "tectonics/tectonic_channel/" + MapPackageFormat.ChannelNames[channel] + "/";
			int s = influence.Sources.Count;
			int c = influence.DistanceKm.Count;
			blocks.Add(prefix + "sources/edge_index", MapBlockElementType.I32, new[] { s },
				influence.Sources.Select(v => v.EdgeIndex).ToArray());
			blocks.Add(prefix + "sources/side_cell", MapBlockElementType.I32, new[] { s },
				influence.Sources.Select(v => v.SideCell).ToArray());
			blocks.Add(prefix + "sources/channel", MapBlockElementType.U8, new[] { s },
				influence.Sources.Select(v => MapGeologyWire.ToTectonicChannel(v.Channel)).ToArray());
			blocks.Add(prefix + "distance_km", MapBlockElementType.F32, new[] { c }, influence.DistanceKm.ToArray());
			blocks.Add(prefix + "source_index", MapBlockElementType.I32, new[] { c }, influence.SourceIndex.ToArray());
		}

		var climate = snapshot.Climate;
		blocks.Add("climate/precipitation_mm", MapBlockElementType.F32, new[] { n }, climate.PrecipitationMm.ToArray());
		blocks.Add("climate/precipitation_monthly_mm", MapBlockElementType.F32, new[] { 12, n },
			climate.PrecipitationMonthlyMm.ToArray());
		blocks.Add("climate/temperature_c", MapBlockElementType.F32, new[] { n }, climate.TemperatureC.ToArray());
		blocks.Add("climate/temperature_monthly_c", MapBlockElementType.F32, new[] { 12, n },
			climate.TemperatureMonthlyC.ToArray());
		blocks.Add("climate/hot_month_c", MapBlockElementType.F32, new[] { n }, climate.HotMonthC.ToArray());
		blocks.Add("climate/cold_month_c", MapBlockElementType.F32, new[] { n }, climate.ColdMonthC.ToArray());
		blocks.Add("climate/pressure_annual_hpa", MapBlockElementType.F32, new[] { n }, climate.PressureAnnualHpa.ToArray());
		blocks.Add("climate/pressure_monthly_hpa", MapBlockElementType.F32, new[] { 12, n },
			climate.PressureMonthlyHpa.ToArray());
		blocks.Add("climate/dry_month_mm", MapBlockElementType.F32, new[] { n }, climate.DryMonthMm.ToArray());
		blocks.Add("climate/dry_month_index", MapBlockElementType.I32, new[] { n }, climate.DryMonthIndex.ToArray());
		blocks.Add("climate/monsoon_strength", MapBlockElementType.F32, new[] { n }, climate.MonsoonStrength.ToArray());
		blocks.Add("climate/ocean_current_direction", MapBlockElementType.F32, new[] { n, 3 },
			climate.OceanCurrentDirection.ToArray());
		blocks.Add("climate/ocean_current_warmth", MapBlockElementType.F32, new[] { n }, climate.OceanCurrentWarmth.ToArray());
		blocks.Add("climate/ocean_current_strength", MapBlockElementType.F32, new[] { n }, climate.OceanCurrentStrength.ToArray());
		AddWind(blocks, "surface", climate.SurfaceWind, n);
		AddWind(blocks, "low", climate.LowWind, n);
		AddWind(blocks, "free", climate.FreeWind, n);

		var rivers = snapshot.Hydrology.Rivers;
		blocks.Add("hydrology/rivers/downstream", MapBlockElementType.I32, new[] { n }, rivers.Downstream.ToArray());
		blocks.Add("hydrology/rivers/flow_accum", MapBlockElementType.I32, new[] { n }, rivers.FlowAccum.ToArray());
		blocks.Add("hydrology/rivers/flow_accum_km2", MapBlockElementType.F64, new[] { n }, rivers.FlowAccumKm2.ToArray());
		blocks.Add("hydrology/rivers/is_river", MapBlockElementType.U8, new[] { n }, rivers.IsRiver.ToArray());
		blocks.Add("hydrology/rivers/sink_count", MapBlockElementType.I32, new[] { 1 }, new[] { rivers.SinkCount });
		blocks.Add("hydrology/rivers/runoff_accum", MapBlockElementType.F32, new[] { n }, rivers.RunoffAccum.ToArray());
		blocks.Add("hydrology/rivers/runoff_accum_km3", MapBlockElementType.F64, new[] { n }, rivers.RunoffAccumKm3.ToArray());
		blocks.Add("hydrology/rivers/runoff_threshold", MapBlockElementType.F32, new[] { 1 }, new[] { rivers.RunoffThreshold });
		blocks.Add("hydrology/rivers/river_threshold_km2", MapBlockElementType.F64, new[] { 1 }, new[] { rivers.RiverThresholdKm2 });
		blocks.Add("hydrology/rivers/routing/used_fill", MapBlockElementType.U8, new[] { 1 },
			new[] { rivers.Routing.UsedFill ? (byte)1 : (byte)0 });
		blocks.Add("hydrology/rivers/routing/routing_height_m", MapBlockElementType.F32, new[] { n },
			rivers.Routing.RoutingHeightM.ToArray());
		blocks.Add("hydrology/rivers/routing/fill_depth_m", MapBlockElementType.F32, new[] { n },
			rivers.Routing.FillDepthM.ToArray());
		blocks.Add("hydrology/rivers/routing/depression_count", MapBlockElementType.I32, new[] { 1 },
			new[] { rivers.Routing.DepressionCount });
		blocks.Add("hydrology/rivers/routing/filled_cell_count", MapBlockElementType.I32, new[] { 1 },
			new[] { rivers.Routing.FilledCellCount });
		blocks.Add("hydrology/rivers/routing/max_fill_depth_m", MapBlockElementType.F64, new[] { 1 },
			new[] { rivers.Routing.MaxFillDepthM });

		AddRiverTopology(blocks, snapshot.Hydrology.RiverTopology, n);
		blocks.Add("hydrology/river_lines/source_res", MapBlockElementType.I32, new[] { 1 }, new[] { snapshot.Hydrology.RiverLines.SourceRes });
		blocks.Add("hydrology/river_lines/line_cells/point_offsets", MapBlockElementType.I32, new[] { r + 1 },
			snapshot.Hydrology.RiverLines.PointOffsets.ToArray());
		blocks.Add("hydrology/river_lines/line_cells/cells", MapBlockElementType.I32, new[] { k },
			snapshot.Hydrology.RiverLines.Cells.ToArray());
		blocks.Add("hydrology/river_lines/lines/points", MapBlockElementType.F32, new[] { k, 3 },
			snapshot.Hydrology.RiverLines.Points.ToArray());
		blocks.Add("hydrology/river_lines/line_outlet", MapBlockElementType.I32, new[] { r },
			snapshot.Hydrology.RiverLines.LineOutlet.ToArray());

		AddBasins(blocks, snapshot.Hydrology.Basins, n);
		AddLakes(blocks, snapshot.Hydrology.Lakes, n);
		AddWaterSystem(blocks, snapshot.Hydrology.WaterSystem, n, l);
		blocks.Add("query_index/nearest_landmass_id", MapBlockElementType.I32, new[] { n },
			snapshot.QueryIndex.NearestLandmassId.ToArray());
		blocks.Add("query_index/nearest_landmass_dist_hops", MapBlockElementType.I32, new[] { n },
			snapshot.QueryIndex.NearestLandmassDistHops.ToArray());
		blocks.Add("query_index/nearest_river_cell", MapBlockElementType.I32, new[] { n },
			snapshot.QueryIndex.NearestRiverCell.ToArray());
		blocks.Add("query_index/nearest_river_dist_hops", MapBlockElementType.I32, new[] { n },
			snapshot.QueryIndex.NearestRiverDistHops.ToArray());
	}

	static void AddWind(BlockSet blocks, string name, WindSetSnapshot wind, int n)
	{
		blocks.Add("climate/wind/" + name + "/monthly", MapBlockElementType.F32, new[] { 12, n, 3 }, wind.Monthly.ToArray());
		blocks.Add("climate/wind/" + name + "/annual", MapBlockElementType.F32, new[] { n, 3 }, wind.Annual.ToArray());
		blocks.Add("climate/wind/" + name + "/annual_speed", MapBlockElementType.F32, new[] { n }, wind.AnnualSpeed.ToArray());
	}

	static void AddRiverTopology(BlockSet blocks, RiverTopologySnapshot graph, int n)
	{
		blocks.Add("hydrology/river_topology/downstream_river", MapBlockElementType.I32, new[] { n }, graph.DownstreamRiver.ToArray());
		blocks.Add("hydrology/river_topology/upstream_count", MapBlockElementType.I32, new[] { n }, graph.UpstreamCount.ToArray());
		blocks.Add("hydrology/river_topology/node_kind", MapBlockElementType.I32, new[] { n }, graph.NodeKind.ToArray());
		blocks.Add("hydrology/river_topology/accum_mm", MapBlockElementType.F32, new[] { n }, graph.AccumMm.ToArray());
		blocks.Add("hydrology/river_topology/river_cell_count", MapBlockElementType.I32, new[] { 1 }, new[] { graph.RiverCellCount });
		blocks.Add("hydrology/river_topology/source_count", MapBlockElementType.I32, new[] { 1 }, new[] { graph.SourceCount });
		blocks.Add("hydrology/river_topology/confluence_count", MapBlockElementType.I32, new[] { 1 }, new[] { graph.ConfluenceCount });
		blocks.Add("hydrology/river_topology/outlet_count", MapBlockElementType.I32, new[] { 1 }, new[] { graph.OutletCount });
		blocks.Add("hydrology/river_topology/sources", MapBlockElementType.I32, new[] { graph.Sources.Count }, graph.Sources.ToArray());
		blocks.Add("hydrology/river_topology/outlets", MapBlockElementType.I32, new[] { graph.Outlets.Count }, graph.Outlets.ToArray());
	}

	static void AddBasins(BlockSet blocks, BasinsSnapshot basins, int n)
	{
		blocks.Add("hydrology/basins/basin_id", MapBlockElementType.I32, new[] { n }, basins.BasinId.ToArray());
		blocks.Add("hydrology/basins/terminal_cell", MapBlockElementType.I32, new[] { n }, basins.TerminalCell.ToArray());
		blocks.Add("hydrology/basins/terminates_at_ocean", MapBlockElementType.U8, new[] { n }, basins.TerminatesAtOcean.ToArray());
		blocks.Add("hydrology/basins/basin_count", MapBlockElementType.I32, new[] { 1 }, new[] { basins.BasinCount });
		blocks.Add("hydrology/basins/kind", MapBlockElementType.I32, new[] { basins.BasinCount }, basins.Kind.ToArray());
		blocks.Add("hydrology/basins/outlet", MapBlockElementType.I32, new[] { basins.BasinCount }, basins.Outlet.ToArray());
		blocks.Add("hydrology/basins/area", MapBlockElementType.I32, new[] { basins.BasinCount }, basins.Area.ToArray());
		blocks.Add("hydrology/basins/area_km2", MapBlockElementType.F64, new[] { basins.BasinCount }, basins.AreaKm2.ToArray());
		blocks.Add("hydrology/basins/accum_mm", MapBlockElementType.F32, new[] { basins.BasinCount }, basins.AccumMm.ToArray());
		blocks.Add("hydrology/basins/ocean_basin_count", MapBlockElementType.I32, new[] { 1 }, new[] { basins.OceanBasinCount });
		blocks.Add("hydrology/basins/endorheic_basin_count", MapBlockElementType.I32, new[] { 1 }, new[] { basins.EndorheicBasinCount });
	}

	static void AddLakes(BlockSet blocks, LakesSnapshot lakes, int n)
	{
		int count = lakes.LakeCount;
		int depressions = lakes.LakeOfBasin.Count;
		blocks.Add("hydrology/lakes/lake_count", MapBlockElementType.I32, new[] { 1 }, new[] { count });
		blocks.Add("hydrology/lakes/depression_id_of", MapBlockElementType.I32, new[] { n }, lakes.DepressionIdOf.ToArray());
		blocks.Add("hydrology/lakes/basin_id_of", MapBlockElementType.I32, new[] { count }, lakes.BasinIdOf.ToArray());
		blocks.Add("hydrology/lakes/lake_of_basin", MapBlockElementType.I32, new[] { depressions }, lakes.LakeOfBasin.ToArray());
		blocks.Add("hydrology/lakes/surface_elevation_m", MapBlockElementType.F32, new[] { count }, lakes.SurfaceElevationM.ToArray());
		blocks.Add("hydrology/lakes/area_km2", MapBlockElementType.F64, new[] { count }, lakes.AreaKm2.ToArray());
		blocks.Add("hydrology/lakes/volume_km3", MapBlockElementType.F64, new[] { count }, lakes.VolumeKm3.ToArray());
		blocks.Add("hydrology/lakes/spill_elevation_m", MapBlockElementType.F32, new[] { count }, lakes.SpillElevationM.ToArray());
		blocks.Add("hydrology/lakes/outlet_cell", MapBlockElementType.I32, new[] { count }, lakes.OutletCell.ToArray());
	}

	static void AddWaterSystem(BlockSet blocks, WaterSystemSnapshot water, int n, int lakeCount)
	{
		int count = water.NodeCount;
		blocks.Add("hydrology/water_system/node_count", MapBlockElementType.I32, new[] { 1 }, new[] { count });
		blocks.Add("hydrology/water_system/kind_of", MapBlockElementType.I32, new[] { count }, water.KindOf.ToArray());
		blocks.Add("hydrology/water_system/ref_of", MapBlockElementType.I32, new[] { count }, water.RefOf.ToArray());
		blocks.Add("hydrology/water_system/next", MapBlockElementType.I32, new[] { count }, water.Next.ToArray());
		blocks.Add("hydrology/water_system/via_cell", MapBlockElementType.I32, new[] { count }, water.ViaCell.ToArray());
		blocks.Add("hydrology/water_system/node_of_river_cell", MapBlockElementType.I32, new[] { n }, water.NodeOfRiverCell.ToArray());
		blocks.Add("hydrology/water_system/node_of_lake", MapBlockElementType.I32, new[] { lakeCount }, water.NodeOfLake.ToArray());
		blocks.Add("hydrology/water_system/ocean_node", MapBlockElementType.I32, new[] { 1 }, new[] { water.OceanNode });
		blocks.Add("hydrology/water_system/river_count", MapBlockElementType.I32, new[] { 1 }, new[] { water.RiverCount });
		blocks.Add("hydrology/water_system/lake_node_count", MapBlockElementType.I32, new[] { 1 }, new[] { water.LakeNodeCount });
		blocks.Add("hydrology/water_system/sink_count", MapBlockElementType.I32, new[] { 1 }, new[] { water.SinkCount });
	}

	static float[] Flat(IReadOnlyList<Float3Snapshot> values)
	{
		var flat = new float[values.Count * 3];
		for (int i = 0; i < values.Count; i++)
		{
			flat[i * 3] = values[i].X;
			flat[i * 3 + 1] = values[i].Y;
			flat[i * 3 + 2] = values[i].Z;
		}
		return flat;
	}

	sealed class BlockSet
	{
		public List<MapBlockDescriptor> Descriptors { get; } = new();
		public List<(string Path, byte[] Data)> Entries { get; } = new();

		public void Add<T>(string id, MapBlockElementType type, int[] shape, T[] values) where T : unmanaged
			=> AddRaw(id, type, shape, MapPackageFormat.Bytes(values));

		public void Add(string id, MapBlockElementType type, int[] shape, bool[] values)
			=> AddRaw(id, type, shape, MapPackageFormat.Bytes(values));

		public void AddNullable(string id, int[] shape, IReadOnlyList<double?> values)
		{
			var validity = new byte[values.Count];
			var actual = new double[values.Count];
			for (int i = 0; i < values.Count; i++)
			{
				if (!values[i].HasValue) continue;
				validity[i] = 1;
				actual[i] = values[i].Value;
			}
			Add(id + "_valid", MapBlockElementType.U8, shape, validity);
			Add(id, MapBlockElementType.F64, shape, actual);
		}

		void AddRaw(string id, MapBlockElementType type, int[] shape, byte[] data)
		{
			long expected = MapPackageFormat.CheckedLength(shape, type);
			if (expected != data.Length)
				throw MapPackageFormat.Failure(MapIssueCode.InvalidShape, "data/" + id + ".bin",
					$"Block bytes {data.Length} ≠ shape bytes {expected}.");
			string path = "data/" + id + ".bin";
			Descriptors.Add(new MapBlockDescriptor(id, path, type, shape, data.Length, MapPackageFormat.Sha256(data)));
			Entries.Add((path, data));
		}
	}
}