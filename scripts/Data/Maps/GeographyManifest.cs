using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace World.Data.Maps;

internal enum MapBlockElementType { U8, I32, I64, U64, F32, F64 }

internal sealed class MapBlockDescriptor
{
	[JsonPropertyName("id")] public string Id { get; }
	[JsonPropertyName("path")] public string Path { get; }
	[JsonPropertyName("elementType")] public MapBlockElementType ElementType { get; }
	[JsonPropertyName("shape")] public IReadOnlyList<int> Shape { get; }
	[JsonPropertyName("byteLength")] public long ByteLength { get; }
	[JsonPropertyName("sha256")] public string Sha256 { get; }

	public MapBlockDescriptor(string id, string path, MapBlockElementType elementType,
		IEnumerable<int> shape, long byteLength, string sha256)
	{
		MapStructure.Require(IsBlockId(id), "block.id", "A lowercase schema block name is required.");
		MapStructure.Require(path == "data/" + id + ".bin", "block.path", "The block path must match its ID.");
		MapStructure.Require(Enum.IsDefined(elementType), path, "The element type is unknown.");
		MapStructure.Present(shape, path + ".shape");
		var dimensions = new List<int>(3);
		foreach (int dimension in shape)
		{
			MapStructure.Require(dimensions.Count < 3 && dimension >= 0, path + ".shape",
				"A shape must contain one to three nonnegative dimensions.", MapIssueCode.InvalidShape);
			dimensions.Add(dimension);
		}
		MapStructure.Require(dimensions.Count > 0, path + ".shape", "A shape is required.", MapIssueCode.InvalidShape);
		long expectedLength;
		try
		{
			expectedLength = dimensions.Contains(0) ? 0 : ElementSize(elementType);
			foreach (int dimension in dimensions) expectedLength = checked(expectedLength * dimension);
		}
		catch (OverflowException)
		{
			throw new MapStructureException(new MapIssue(MapIssueCode.SizeLimit, path, null, "The block length overflows int64."));
		}
		MapStructure.Require(byteLength >= 0 && byteLength <= GeographyManifest.MaxEntryBytes
			&& expectedLength <= GeographyManifest.MaxEntryBytes, path, "The block exceeds the entry byte limit.", MapIssueCode.SizeLimit);
		MapStructure.Require(byteLength == expectedLength, path, "Byte length does not match shape and element size.", MapIssueCode.InvalidShape);
		MapStructure.Require(sha256 != null && sha256.Length == 64 && sha256.All(IsLowerHex), path + ".sha256",
			"A 64-character lowercase SHA-256 value is required.");
		(Id, Path, ElementType, Shape, ByteLength, Sha256) =
			(id, path, elementType, new ReadOnlyCollection<int>(dimensions), byteLength, sha256);
	}

	static bool IsLowerHex(char value) => value is >= '0' and <= '9' or >= 'a' and <= 'f';
	static bool IsBlockId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 256
		&& id.Split('/').All(part => part.Length > 0 && part[0] is >= 'a' and <= 'z'
			&& part.All(value => value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'));
	internal static int ElementSize(MapBlockElementType type) => type switch
	{
		MapBlockElementType.U8 => 1,
		MapBlockElementType.I32 or MapBlockElementType.F32 => 4,
		MapBlockElementType.I64 or MapBlockElementType.U64 or MapBlockElementType.F64 => 8,
		_ => throw new ArgumentOutOfRangeException(nameof(type)),
	};
}

internal sealed class GeographyManifest
{
	public const string CurrentFormat = "world.geography";
	public const int CurrentSchemaVersion = 1;
	public const int MonthsPerYear = 12;
	public const long MaxManifestBytes = 1024L * 1024;
	public const int MaxEntryCount = 1024;
	public const long MaxEntryBytes = 128L * 1024 * 1024;
	public const long MaxTotalUncompressedBytes = 512L * 1024 * 1024;
	public const long MaxFileBytes = 512L * 1024 * 1024;

	[JsonPropertyName("format")] public string Format { get; }
	[JsonPropertyName("schemaVersion")] public int SchemaVersion { get; }
	[JsonPropertyName("mapId")] public Guid MapId { get; }
	[JsonPropertyName("title")] public string Title { get; }
	[JsonPropertyName("description")] public string Description { get; }
	[JsonPropertyName("createdUtc")] public DateTimeOffset CreatedUtc { get; }
	[JsonPropertyName("producerVersion")] public string ProducerVersion { get; }
	[JsonPropertyName("h3Resolution")] public int H3Resolution { get; }
	[JsonPropertyName("cellCount")] public int CellCount { get; }
	[JsonPropertyName("physicalRadiusKm")] public double PhysicalRadiusKm { get; }
	[JsonPropertyName("renderRadius")] public float RenderRadius { get; }
	[JsonPropertyName("seaLevelM")] public float SeaLevelM { get; }
	[JsonPropertyName("monthCount")] public int MonthCount { get; }
	[JsonPropertyName("sourceTimeMa")] public double? SourceTimeMa { get; }
	[JsonPropertyName("hasProjectedSurface")] public bool HasProjectedSurface { get; }
	[JsonPropertyName("blocks")] public IReadOnlyList<MapBlockDescriptor> Blocks { get; }

	public GeographyManifest(string format, int schemaVersion, Guid mapId, string title, string description,
		DateTimeOffset createdUtc, string producerVersion, int h3Resolution, int cellCount,
		double physicalRadiusKm, float renderRadius, float seaLevelM, int monthCount,
		double? sourceTimeMa, bool hasProjectedSurface, IEnumerable<MapBlockDescriptor> blocks)
	{
		MapStructure.Require(format == CurrentFormat, "manifest.format", "The map format is not recognized.", MapIssueCode.UnknownFormat);
		MapStructure.Require(schemaVersion == CurrentSchemaVersion, "manifest.schemaVersion", "The schema version is not supported.", MapIssueCode.UnsupportedVersion);
		MapStructure.Require(mapId != Guid.Empty, "manifest.mapId", "A nonempty map ID is required.");
		MapStructure.Require(title != null && title.Trim().Length is >= 1 and <= 120, "manifest.title", "The trimmed title must contain 1 to 120 characters.");
		MapStructure.Require(description != null && description.Length <= 4096, "manifest.description", "A description of at most 4096 characters is required.");
		MapStructure.Require(createdUtc.Offset == TimeSpan.Zero, "manifest.createdUtc", "The creation time must be UTC.");
		MapStructure.Require(producerVersion != null && producerVersion.Length is >= 1 and <= 64, "manifest.producerVersion", "A producer version of 1 to 64 characters is required.");
		MapStructure.Require(h3Resolution is >= 0 and <= 4, "manifest.h3Resolution", "The H3 resolution is unsupported.", MapIssueCode.UnsupportedScale);
		int expectedCells = h3Resolution switch { 0 => 122, 1 => 842, 2 => 5882, 3 => 41162, _ => 288122 };
		MapStructure.Length(cellCount, expectedCells, "manifest.cellCount");
		MapStructure.Finite(physicalRadiusKm, "manifest.physicalRadiusKm");
		MapStructure.Require(physicalRadiusKm == 6371d, "manifest.physicalRadiusKm", "Only the current 6371 km physical scale is supported.", MapIssueCode.UnsupportedScale);
		MapStructure.Finite(renderRadius, "manifest.renderRadius");
		MapStructure.Require(renderRadius == 2f, "manifest.renderRadius", "Only render radius 2 is supported.", MapIssueCode.UnsupportedScale);
		MapStructure.Finite(seaLevelM, "manifest.seaLevelM");
		MapStructure.Require(monthCount == MonthsPerYear, "manifest.monthCount", "Exactly twelve months are required.");
		if (sourceTimeMa.HasValue) MapStructure.Range(sourceTimeMa.Value, 0, double.MaxValue, "manifest.sourceTimeMa");
		MapStructure.Present(blocks, "manifest.blocks");
		var copiedBlocks = new List<MapBlockDescriptor>();
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var paths = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
		long totalBytes = 0;
		foreach (var block in blocks)
		{
			MapStructure.Require(copiedBlocks.Count < MaxEntryCount - 1, "manifest.blocks", "The entry count exceeds the package limit.", MapIssueCode.SizeLimit);
			MapStructure.Present(block, "manifest.blocks");
			MapStructure.Require(ids.Add(block.Id) && paths.Add(block.Path), block.Path, "A block ID or entry path is duplicated.", MapIssueCode.DuplicateEntry);
			totalBytes = checked(totalBytes + block.ByteLength);
			MapStructure.Require(totalBytes <= MaxTotalUncompressedBytes - MaxManifestBytes, "manifest.blocks", "The directory exceeds the uncompressed byte limit.", MapIssueCode.SizeLimit);
			copiedBlocks.Add(block);
		}
		(Format, SchemaVersion, MapId, Title, Description, CreatedUtc, ProducerVersion) =
			(format, schemaVersion, mapId, title, description, createdUtc, producerVersion);
		(H3Resolution, CellCount, PhysicalRadiusKm, RenderRadius, SeaLevelM, MonthCount, SourceTimeMa, HasProjectedSurface) =
			(h3Resolution, cellCount, physicalRadiusKm, renderRadius, seaLevelM, monthCount, sourceTimeMa, hasProjectedSurface);
		Blocks = new ReadOnlyCollection<MapBlockDescriptor>(copiedBlocks);
	}

	public void ValidateLayout() => ValidateLayout(null);

	public void ValidateLayout(IReadOnlyDictionary<string, int> actualDimensions)
	{
		var schema = BuildSchema(HasProjectedSurface);
		var actual = Blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
		foreach (var block in Blocks)
			MapStructure.Require(schema.ContainsKey(block.Id), block.Path, "The block is not part of this schema.", MapIssueCode.UnexpectedBlock);
		foreach (string id in schema.Keys)
			MapStructure.Require(actual.ContainsKey(id), "data/" + id + ".bin", "A required schema block is missing.", MapIssueCode.MissingBlock);
		var dimensions = new Dictionary<string, int>(StringComparer.Ordinal) { ["N"] = CellCount };
		if (actualDimensions != null)
			foreach (var pair in actualDimensions)
			{
				MapStructure.Require(!string.IsNullOrWhiteSpace(pair.Key) && pair.Value >= 0,
					"manifest.blocks", "Actual layout dimensions must be named and nonnegative.", MapIssueCode.InvalidShape);
				dimensions[pair.Key] = pair.Value;
			}
		foreach (var block in Blocks)
		{
			var rule = schema[block.Id];
			MapStructure.Require(block.ElementType == rule.Type, block.Path, "The element type does not match the schema.", MapIssueCode.InvalidShape);
			MapStructure.Length(block.Shape.Count, rule.Shape.Length, block.Path);
			for (int axis = 0; axis < rule.Shape.Length; axis++)
			{
				string dimension = rule.Shape[axis];
				if (int.TryParse(dimension, out int fixedSize)) MapStructure.Length(block.Shape[axis], fixedSize, block.Path);
				else if (!dimension.Contains('*') && !dimension.EndsWith("+1", StringComparison.Ordinal))
				{
					if (dimensions.TryGetValue(dimension, out int size)) MapStructure.Length(block.Shape[axis], size, block.Path);
					else dimensions.Add(dimension, block.Shape[axis]);
				}
			}
		}
		foreach (var block in Blocks)
		{
			var rule = schema[block.Id];
			for (int axis = 0; axis < rule.Shape.Length; axis++)
			{
				string dimension = rule.Shape[axis];
				long expected = block.Shape[axis];
				if (dimension.EndsWith("+1", StringComparison.Ordinal)) expected = (long)dimensions[dimension[..^2]] + 1;
				else if (dimension == "P*P") expected = (long)dimensions["P"] * dimensions["P"];
				else if (dimension == "2*E") expected = 2L * dimensions["E"];
				MapStructure.Require(block.Shape[axis] == expected, block.Path, "A related table dimension does not match.", MapIssueCode.InvalidShape);
			}
		}
		for (int channel = 0; channel < ChannelNames.Length; channel++)
		{
			string prefix = "tectonics/tectonic_channel/" + ChannelNames[channel] + "/";
			int sources = actual[prefix + "sources/edge_index"].Shape[0];
			int count = actual[prefix + "distance_km"].Shape[0];
			MapStructure.Length(count, sources == 0 ? 0 : CellCount, "data/" + prefix + "distance_km.bin");
		}
	}

	sealed record BlockRule(MapBlockElementType Type, string[] Shape);
	static readonly string[] ChannelNames = { "continental_collision", "continental_arc", "oceanic_arc", "subduction", "continental_rift", "oceanic_ridge" };
	static Dictionary<string, BlockRule> BuildSchema(bool projected)
	{
		var schema = new Dictionary<string, BlockRule>(StringComparer.Ordinal);
		void Add(string prefix, MapBlockElementType type, string shape, string names)
		{
			foreach (string name in names.Split(' ')) schema.Add(prefix + name, new BlockRule(type, shape.Split(',')));
		}
		const MapBlockElementType u8 = MapBlockElementType.U8, i32 = MapBlockElementType.I32,
			f32 = MapBlockElementType.F32, f64 = MapBlockElementType.F64;
		Add("grid/", MapBlockElementType.U64, "N", "cell_ids");
		Add("terrain/", f32, "N", "height_m final_land_fraction");
		Add("terrain/", u8, "N", "final_land");
		Add("terrain/", i32, "N", "final_landmass_id final_dist_to_coast final_dist_to_land");
		Add("terrain/", i32, "1", "final_landmass_count");
		Add("surface/", u8, "N", "surface_crust_type");
		Add("surface/", f32, "P,3", "motion_omega");
		if (projected)
		{
			Add("surface/projected/", f32, "N", "emergent_fraction land_height_m water_height_m material_height_m");
			Add("surface/projected/", f64, "N", "oceanic_coverage ocean_age_ma volcanic_rate_km_per_ma");
			Add("surface/projected/", u8, "N", "ocean_age_ma_valid");
			Add("surface/projected/", f64, "P,3", "physical_omega_rad_per_ma");
			Add("surface/projected/", f64, "1", "emergent_land_fraction");
		}
		Add("tectonics/plates/", i32, "P", "plate_id");
		Add("tectonics/plates/", f32, "P", "seed_theta seed_phi density");
		Add("tectonics/plates/", f32, "P,3", "omega");
		Add("tectonics/crust/", u8, "N", "type");
		Add("tectonics/crust/", i32, "N", "province_id");
		Add("tectonics/crust/provinces/", i32, "Q", "id seed_cell seed_plate_id cell_count");
		Add("tectonics/crust/provinces/", u8, "Q", "type");
		Add("tectonics/", i32, "N", "plate_of_cell");
		Add("tectonics/plate_boundaries/", i32, "1", "plate_count");
		Add("tectonics/plate_boundaries/edges/", i32, "E", "cell_a cell_b plate_a plate_b");
		Add("tectonics/plate_boundaries/pairs/", i32, "P*P", "plate_a plate_b edge_start edge_count");
		Add("tectonics/plate_boundaries/", i32, "N+1", "cell_edge_start");
		Add("tectonics/plate_boundaries/", i32, "2*E", "cell_edge_index");
		Add("tectonics/boundary_fields/", f32, "E", "edge_motion");
		Add("tectonics/boundary_fields/", u8, "E", "edge_kind edge_role_a edge_role_b");
		Add("tectonics/final_boundary/", u8, "E", "side_type_a side_type_b convergence_kind normal_km_per_ma_valid shear_km_per_ma_valid");
		Add("tectonics/final_boundary/", f64, "E", "normal_km_per_ma shear_km_per_ma");
		foreach (string channel in ChannelNames)
		{
			string prefix = "tectonics/tectonic_channel/" + channel + "/";
			Add(prefix + "sources/", i32, "S_" + channel, "edge_index side_cell");
			Add(prefix + "sources/", u8, "S_" + channel, "channel");
			Add(prefix, f32, "C_" + channel, "distance_km");
			Add(prefix, i32, "C_" + channel, "source_index");
		}
		Add("climate/", f32, "N", "precipitation_mm temperature_c hot_month_c cold_month_c pressure_annual_hpa dry_month_mm monsoon_strength ocean_current_warmth ocean_current_strength");
		Add("climate/", f32, "12,N", "precipitation_monthly_mm temperature_monthly_c pressure_monthly_hpa");
		Add("climate/", i32, "N", "dry_month_index");
		Add("climate/", f32, "N,3", "ocean_current_direction");
		foreach (string wind in new[] { "surface", "low", "free" })
		{
			Add("climate/wind/" + wind + "/", f32, "12,N,3", "monthly");
			Add("climate/wind/" + wind + "/", f32, "N,3", "annual");
			Add("climate/wind/" + wind + "/", f32, "N", "annual_speed");
		}
		Add("hydrology/rivers/", i32, "N", "downstream flow_accum");
		Add("hydrology/rivers/", f64, "N", "flow_accum_km2 runoff_accum_km3");
		Add("hydrology/rivers/", u8, "N", "is_river");
		Add("hydrology/rivers/", f32, "N", "runoff_accum");
		Add("hydrology/rivers/", i32, "1", "sink_count");
		Add("hydrology/rivers/", f32, "1", "runoff_threshold");
		Add("hydrology/rivers/", f64, "1", "river_threshold_km2");
		Add("hydrology/rivers/routing/", u8, "1", "used_fill");
		Add("hydrology/rivers/routing/", f32, "N", "routing_height_m fill_depth_m");
		Add("hydrology/rivers/routing/", i32, "1", "depression_count filled_cell_count");
		Add("hydrology/rivers/routing/", f64, "1", "max_fill_depth_m");
		Add("hydrology/river_topology/", i32, "N", "downstream_river upstream_count node_kind");
		Add("hydrology/river_topology/", f32, "N", "accum_mm");
		Add("hydrology/river_topology/", i32, "1", "river_cell_count source_count confluence_count outlet_count");
		Add("hydrology/river_topology/", i32, "Sources", "sources");
		Add("hydrology/river_topology/", i32, "Outlets", "outlets");
		Add("hydrology/river_lines/", i32, "1", "source_res");
		Add("hydrology/river_lines/line_cells/", i32, "R+1", "point_offsets");
		Add("hydrology/river_lines/line_cells/", i32, "K", "cells");
		Add("hydrology/river_lines/lines/", f32, "K,3", "points");
		Add("hydrology/river_lines/", i32, "R", "line_outlet");
		Add("hydrology/basins/", i32, "N", "basin_id terminal_cell");
		Add("hydrology/basins/", u8, "N", "terminates_at_ocean");
		Add("hydrology/basins/", i32, "1", "basin_count ocean_basin_count endorheic_basin_count");
		Add("hydrology/basins/", i32, "B", "kind outlet area");
		Add("hydrology/basins/", f64, "B", "area_km2");
		Add("hydrology/basins/", f32, "B", "accum_mm");
		Add("hydrology/lakes/", i32, "1", "lake_count");
		Add("hydrology/lakes/", i32, "N", "depression_id_of");
		Add("hydrology/lakes/", i32, "L", "basin_id_of outlet_cell");
		Add("hydrology/lakes/", i32, "D", "lake_of_basin");
		Add("hydrology/lakes/", f32, "L", "surface_elevation_m spill_elevation_m");
		Add("hydrology/lakes/", f64, "L", "area_km2 volume_km3");
		Add("hydrology/water_system/", i32, "1", "node_count ocean_node river_count lake_node_count sink_count");
		Add("hydrology/water_system/", i32, "W", "kind_of ref_of next via_cell");
		Add("hydrology/water_system/", i32, "N", "node_of_river_cell");
		Add("hydrology/water_system/", i32, "L", "node_of_lake");
		Add("query_index/", i32, "N", "nearest_landmass_id nearest_landmass_dist_hops nearest_river_cell nearest_river_dist_hops");
		return schema;
	}
}
