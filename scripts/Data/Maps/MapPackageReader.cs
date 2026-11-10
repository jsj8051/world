using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace World.Data.Maps;

/// <summary>
/// 读取 <c>.wmap</c>：严格校验清单、块目录、长度、校验和与引用关系，全部通过才返回快照。
/// 数据块按独立 zip 流并行解压/校验/解码；块间互不依赖，结果按清单序归位。
/// </summary>
internal static class MapPackageReader
{
	/// <summary>并行读取的工作线程数上限（按块数截断）。</summary>
	const int ReadWorkers = 4;

	internal static GeographySnapshot Read(string path) => Read(path, null, CancellationToken.None);

	internal static GeographySnapshot Read(string path, Action<int, int> onBlock, CancellationToken cancellation)
	{
		try
		{
			return ReadArchive(path, onBlock, cancellation);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (MapStructureException)
		{
			throw;
		}
		catch (JsonException e)
		{
			throw MapPackageFormat.Failure(MapIssueCode.UnknownFormat, MapPackageFormat.ManifestEntry,
				"The manifest is not valid JSON: " + e.Message);
		}
		catch (InvalidDataException e)
		{
			throw MapPackageFormat.Failure(MapIssueCode.UnknownFormat, MapPackageFormat.ManifestEntry,
				"The file is not a readable ZIP package: " + e.Message);
		}
		catch (IOException e)
		{
			throw MapPackageFormat.Failure(MapIssueCode.IoFailure, "package", "Could not read the package: " + e.Message);
		}
		catch (UnauthorizedAccessException e)
		{
			throw MapPackageFormat.Failure(MapIssueCode.IoFailure, "package", "Could not open the package: " + e.Message);
		}
	}

	static GeographySnapshot ReadArchive(string path, Action<int, int> onBlock, CancellationToken cancellation)
	{
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
			throw MapPackageFormat.Failure(MapIssueCode.IoFailure, "package", "The map file does not exist.");
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (stream.Length > GeographyManifest.MaxFileBytes)
			throw MapPackageFormat.Failure(MapIssueCode.SizeLimit, "package", "The map file exceeds the size limit.");
		using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
		if (archive.Entries.Count > GeographyManifest.MaxEntryCount)
			throw MapPackageFormat.Failure(MapIssueCode.SizeLimit, "package", "The package has too many entries.");

		var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
		long total = 0;
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			if (!IsSafeEntryName(entry.FullName))
				throw MapPackageFormat.Failure(MapIssueCode.UnexpectedBlock, entry.FullName, "Unsafe or unexpected entry name.");
			if (!entries.TryAdd(entry.FullName, entry))
				throw MapPackageFormat.Failure(MapIssueCode.DuplicateEntry, entry.FullName, "A ZIP entry is duplicated.");
			total = checked(total + entry.Length);
			if (total > GeographyManifest.MaxTotalUncompressedBytes)
				throw MapPackageFormat.Failure(MapIssueCode.SizeLimit, "package", "Uncompressed contents exceed the size limit.");
		}
		if (!entries.TryGetValue(MapPackageFormat.ManifestEntry, out ZipArchiveEntry manifestEntry))
			throw MapPackageFormat.Failure(MapIssueCode.MissingBlock, MapPackageFormat.ManifestEntry,
				"The package has no manifest.");

		byte[] manifestBytes = MapPackageFormat.ReadEntry(manifestEntry, GeographyManifest.MaxManifestBytes);
		ManifestJson json = JsonSerializer.Deserialize<ManifestJson>(manifestBytes, MapPackageFormat.JsonOptions)
			?? throw MapPackageFormat.Failure(MapIssueCode.UnknownFormat, MapPackageFormat.ManifestEntry,
				"The manifest is empty.");
		GeographyManifest manifest = ManifestJsonMapping.ToManifest(json);
		manifest.ValidateLayout();

		var expected = new HashSet<string>(StringComparer.Ordinal) { MapPackageFormat.ManifestEntry };
		foreach (MapBlockDescriptor block in manifest.Blocks)
		{
			if (!expected.Add(block.Path) || !entries.ContainsKey(block.Path))
				throw MapPackageFormat.Failure(MapIssueCode.MissingBlock, block.Path, "A declared data block is missing.");
		}
		foreach (string name in entries.Keys)
			if (!expected.Contains(name))
				throw MapPackageFormat.Failure(MapIssueCode.UnexpectedBlock, name, "The entry is not declared by the manifest.");

		byte[][] raw = ReadBlocksInParallel(path, manifest, onBlock, cancellation);
		var blocks = new Dictionary<string, byte[]>(manifest.Blocks.Count, StringComparer.Ordinal);
		for (int i = 0; i < manifest.Blocks.Count; i++)
			blocks[manifest.Blocks[i].Id] = raw[i];

		return Build(manifest, blocks);
	}

	/// <summary>
	/// 多 worker 并行读取数据块：每个 worker 独立打开文件与 zip 流，按块号取模分片。
	/// 任何一个块失败即停止其余 worker；全部结束后只抛“最小块号”的那个错误，保证错误定位稳定。
	/// </summary>
	static byte[][] ReadBlocksInParallel(string path, GeographyManifest manifest,
		Action<int, int> onBlock, CancellationToken cancellation)
	{
		int count = manifest.Blocks.Count;
		var data = new byte[count][];
		if (count == 0) return data;

		int workers = Math.Min(ReadWorkers, count);
		int completed = 0;
		var gate = new object();
		ExceptionDispatchInfo first = null;
		int firstIndex = int.MaxValue;

		var tasks = new Task[workers];
		for (int w = 0; w < workers; w++)
		{
			int worker = w;
			tasks[w] = Task.Run(() =>
			{
				try
				{
					using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
					using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
					for (int i = worker; i < count; i += workers)
					{
						cancellation.ThrowIfCancellationRequested();
						try
						{
							MapBlockDescriptor block = manifest.Blocks[i];
							ZipArchiveEntry entry = archive.GetEntry(block.Path)
								?? throw MapPackageFormat.Failure(MapIssueCode.MissingBlock, block.Path,
									"The declared data block is missing.");
							byte[] bytes = MapPackageFormat.ReadEntry(entry, GeographyManifest.MaxEntryBytes);
							if (bytes.Length != block.ByteLength)
								throw MapPackageFormat.Failure(MapIssueCode.InvalidShape, block.Path,
									"The block length differs from the manifest.");
							if (MapPackageFormat.Sha256(bytes) != block.Sha256)
								throw MapPackageFormat.Failure(MapIssueCode.ChecksumMismatch, block.Path,
									"The block checksum does not match.");
							data[i] = bytes;
						}
						catch (Exception e) when (e is not OperationCanceledException)
						{
							lock (gate)
							{
								if (i < firstIndex)
								{
									firstIndex = i;
									first = ExceptionDispatchInfo.Capture(e);
								}
							}
							return;
						}
						onBlock?.Invoke(Interlocked.Increment(ref completed), count);
					}
				}
				catch (OperationCanceledException)
				{
					// 外部取消：由调用方统一抛。
				}
				catch (Exception e)
				{
					lock (gate)
					{
						if (first == null)
						{
							firstIndex = 0;
							first = ExceptionDispatchInfo.Capture(e);
						}
					}
				}
			});
		}

		Task.WaitAll(tasks);
		cancellation.ThrowIfCancellationRequested();
		if (first != null) first.Throw();
		if (!data.All(bytes => bytes != null))
			throw MapPackageFormat.Failure(MapIssueCode.IoFailure, "package", "A data block was not read.");
		return data;
	}

	static bool IsSafeEntryName(string name)
	{
		if (string.IsNullOrEmpty(name) || name.Contains('\\') || name.StartsWith('/')
			|| name.Contains("..", StringComparison.Ordinal)) return false;
		return name == MapPackageFormat.ManifestEntry || (name.StartsWith("data/", StringComparison.Ordinal)
			&& name.EndsWith(".bin", StringComparison.Ordinal));
	}

	static GeographySnapshot Build(GeographyManifest manifest, Dictionary<string, byte[]> blocks)
	{
		int n = manifest.CellCount;
		var decoder = new Decoder(manifest, blocks);
		int p = decoder.Shape("tectonics/plates/plate_id")[0];
		int q = decoder.Shape("tectonics/crust/provinces/id")[0];
		int e = decoder.Shape("tectonics/plate_boundaries/edges/cell_a")[0];


		var grid = new GridSnapshot(decoder.U64("grid/cell_ids"));

		SurfaceSnapshot surface = decoder.Surface(n, p);
		var plates = new PlateSnapshot[p];
		int[] plateIds = decoder.I32("tectonics/plates/plate_id");
		float[] seedTheta = decoder.F32("tectonics/plates/seed_theta");
		float[] seedPhi = decoder.F32("tectonics/plates/seed_phi");
		float[] density = decoder.F32("tectonics/plates/density");
		float[] omega = decoder.F32("tectonics/plates/omega");
		for (int i = 0; i < p; i++)
			plates[i] = new PlateSnapshot(plateIds[i], seedTheta[i], seedPhi[i],
				new Float3Snapshot(omega[i * 3], omega[i * 3 + 1], omega[i * 3 + 2]), density[i]);

		byte[] crustType = decoder.U8("tectonics/crust/type");
		byte[] provinceType = decoder.U8("tectonics/crust/provinces/type");
		var provinces = new CrustProvinceSnapshot[q];
		int[] provinceIds = decoder.I32("tectonics/crust/provinces/id");
		int[] seedCells = decoder.I32("tectonics/crust/provinces/seed_cell");
		int[] seedPlates = decoder.I32("tectonics/crust/provinces/seed_plate_id");
		int[] provinceCounts = decoder.I32("tectonics/crust/provinces/cell_count");
		for (int i = 0; i < q; i++)
			provinces[i] = new CrustProvinceSnapshot(provinceIds[i], MapGeologyWire.FromCrustType(provinceType[i]),
				seedCells[i], seedPlates[i], provinceCounts[i]);
		var crust = new CrustSnapshot(crustType.Select(MapGeologyWire.FromCrustType),
			decoder.I32("tectonics/crust/province_id"), provinces);

		var edges = new BoundaryEdgeSnapshot[e];
		int[] cellA = decoder.I32("tectonics/plate_boundaries/edges/cell_a");
		int[] cellB = decoder.I32("tectonics/plate_boundaries/edges/cell_b");
		int[] plateA = decoder.I32("tectonics/plate_boundaries/edges/plate_a");
		int[] plateB = decoder.I32("tectonics/plate_boundaries/edges/plate_b");
		for (int i = 0; i < e; i++) edges[i] = new BoundaryEdgeSnapshot(cellA[i], cellB[i], plateA[i], plateB[i]);
		var pairs = new BoundaryPairSnapshot[p * p];
		int[] pairA = decoder.I32("tectonics/plate_boundaries/pairs/plate_a");
		int[] pairB = decoder.I32("tectonics/plate_boundaries/pairs/plate_b");
		int[] pairStart = decoder.I32("tectonics/plate_boundaries/pairs/edge_start");
		int[] pairCount = decoder.I32("tectonics/plate_boundaries/pairs/edge_count");
		for (int i = 0; i < pairs.Length; i++)
			pairs[i] = new BoundaryPairSnapshot(pairA[i], pairB[i], pairStart[i], pairCount[i]);
		var boundaries = new PlateBoundariesSnapshot(decoder.I32("tectonics/plate_boundaries/plate_count")[0],
			edges, pairs, decoder.I32("tectonics/plate_boundaries/cell_edge_start"),
			decoder.I32("tectonics/plate_boundaries/cell_edge_index"));

		byte[] edgeKind = decoder.U8("tectonics/boundary_fields/edge_kind");
		byte[] roleA = decoder.U8("tectonics/boundary_fields/edge_role_a");
		byte[] roleB = decoder.U8("tectonics/boundary_fields/edge_role_b");
		var fields = new BoundaryFieldsSnapshot(decoder.F32("tectonics/boundary_fields/edge_motion"),
			edgeKind.Select(MapGeologyWire.FromBoundaryKind), roleA.Select(MapGeologyWire.FromBoundarySideRole),
			roleB.Select(MapGeologyWire.FromBoundarySideRole));

		byte[] sideA = decoder.U8("tectonics/final_boundary/side_type_a");
		byte[] sideB = decoder.U8("tectonics/final_boundary/side_type_b");
		byte[] convergence = decoder.U8("tectonics/final_boundary/convergence_kind");
		var finalBoundary = new FinalBoundarySnapshot(sideA.Select(MapGeologyWire.FromCrustType),
			sideB.Select(MapGeologyWire.FromCrustType), convergence.Select(MapGeologyWire.FromConvergenceKind),
			decoder.Nullable("tectonics/final_boundary/normal_km_per_ma", e),
			decoder.Nullable("tectonics/final_boundary/shear_km_per_ma", e));

		var channels = new List<TectonicInfluenceSnapshot>();
		for (int channel = 0; channel < MapPackageFormat.ChannelNames.Length; channel++)
		{
			string prefix = "tectonics/tectonic_channel/" + MapPackageFormat.ChannelNames[channel] + "/";
			int s = decoder.Shape(prefix + "sources/edge_index")[0];
			int[] sourceEdges = decoder.I32(prefix + "sources/edge_index");
			int[] sourceCells = decoder.I32(prefix + "sources/side_cell");
			byte[] sourceChannels = decoder.U8(prefix + "sources/channel");
			var sources = new TectonicSourceSnapshot[s];
			for (int i = 0; i < s; i++)
				sources[i] = new TectonicSourceSnapshot(sourceEdges[i], sourceCells[i],
					MapGeologyWire.FromTectonicChannel(sourceChannels[i]));
			channels.Add(new TectonicInfluenceSnapshot(MapGeologyWire.FromTectonicChannel((byte)channel), sources,
				decoder.F32(prefix + "distance_km"), decoder.I32(prefix + "source_index")));
		}

		var tectonics = new TectonicsSnapshot(plates, crust, decoder.I32("tectonics/plate_of_cell"), boundaries,
			fields, finalBoundary, channels);

		var climate = new ClimateSnapshot(
			decoder.F32("climate/precipitation_mm"), decoder.F32("climate/precipitation_monthly_mm"),
			decoder.F32("climate/temperature_c"), decoder.F32("climate/temperature_monthly_c"),
			decoder.F32("climate/hot_month_c"), decoder.F32("climate/cold_month_c"),
			decoder.F32("climate/pressure_annual_hpa"), decoder.F32("climate/pressure_monthly_hpa"),
			decoder.F32("climate/dry_month_mm"), decoder.I32("climate/dry_month_index"),
			decoder.F32("climate/monsoon_strength"),
			decoder.Wind("climate/wind/surface", WindKind.Surface),
			decoder.Wind("climate/wind/low", WindKind.Low),
			decoder.Wind("climate/wind/free", WindKind.Free),
			decoder.F32("climate/ocean_current_direction"), decoder.F32("climate/ocean_current_warmth"),
			decoder.F32("climate/ocean_current_strength"));

		var terrain = new TerrainSnapshot(decoder.F32("terrain/height_m"), decoder.Bool("terrain/final_land"),
			decoder.F32("terrain/final_land_fraction"), decoder.I32("terrain/final_landmass_id"),
			decoder.I32("terrain/final_landmass_count")[0], decoder.I32("terrain/final_dist_to_coast"),
			decoder.I32("terrain/final_dist_to_land"));

		var rivers = new RiversSnapshot(decoder.I32("hydrology/rivers/downstream"),
			decoder.I32("hydrology/rivers/flow_accum"), decoder.F64("hydrology/rivers/flow_accum_km2"),
			decoder.Bool("hydrology/rivers/is_river"), decoder.I32("hydrology/rivers/sink_count")[0],
			decoder.F32("hydrology/rivers/runoff_accum"), decoder.F64("hydrology/rivers/runoff_accum_km3"),
			decoder.F32("hydrology/rivers/runoff_threshold")[0],
			decoder.F64("hydrology/rivers/river_threshold_km2")[0],
			new HydrologyRoutingSnapshot(decoder.Bool("hydrology/rivers/routing/used_fill")[0],
				decoder.F32("hydrology/rivers/routing/routing_height_m"),
				decoder.F32("hydrology/rivers/routing/fill_depth_m"),
				decoder.I32("hydrology/rivers/routing/depression_count")[0],
				decoder.F64("hydrology/rivers/routing/max_fill_depth_m")[0],
				decoder.I32("hydrology/rivers/routing/filled_cell_count")[0]));

		var topology = new RiverTopologySnapshot(decoder.I32("hydrology/river_topology/downstream_river"),
			decoder.I32("hydrology/river_topology/upstream_count"),
			decoder.I32("hydrology/river_topology/node_kind"),
			decoder.F32("hydrology/river_topology/accum_mm"),
			decoder.I32("hydrology/river_topology/river_cell_count")[0],
			decoder.I32("hydrology/river_topology/source_count")[0],
			decoder.I32("hydrology/river_topology/confluence_count")[0],
			decoder.I32("hydrology/river_topology/outlet_count")[0],
			decoder.I32("hydrology/river_topology/sources"),
			decoder.I32("hydrology/river_topology/outlets"));

		var riverLines = new RiverLinesSnapshot(decoder.I32("hydrology/river_lines/source_res")[0],
			decoder.I32("hydrology/river_lines/line_cells/point_offsets"),
			decoder.I32("hydrology/river_lines/line_cells/cells"),
			decoder.F32("hydrology/river_lines/lines/points"),
			decoder.I32("hydrology/river_lines/line_outlet"));

		var basins = new BasinsSnapshot(decoder.I32("hydrology/basins/basin_id"),
			decoder.I32("hydrology/basins/terminal_cell"), decoder.Bool("hydrology/basins/terminates_at_ocean"),
			decoder.I32("hydrology/basins/basin_count")[0], decoder.I32("hydrology/basins/kind"),
			decoder.I32("hydrology/basins/outlet"), decoder.I32("hydrology/basins/area"),
			decoder.F64("hydrology/basins/area_km2"), decoder.F32("hydrology/basins/accum_mm"),
			decoder.I32("hydrology/basins/ocean_basin_count")[0],
			decoder.I32("hydrology/basins/endorheic_basin_count")[0]);

		var lakes = new LakesSnapshot(decoder.I32("hydrology/lakes/lake_count")[0],
			decoder.I32("hydrology/lakes/depression_id_of"), decoder.I32("hydrology/lakes/basin_id_of"),
			decoder.I32("hydrology/lakes/lake_of_basin"), decoder.F32("hydrology/lakes/surface_elevation_m"),
			decoder.F64("hydrology/lakes/area_km2"), decoder.F64("hydrology/lakes/volume_km3"),
			decoder.F32("hydrology/lakes/spill_elevation_m"), decoder.I32("hydrology/lakes/outlet_cell"));

		var water = new WaterSystemSnapshot(decoder.I32("hydrology/water_system/node_count")[0],
			decoder.I32("hydrology/water_system/kind_of"), decoder.I32("hydrology/water_system/ref_of"),
			decoder.I32("hydrology/water_system/next"), decoder.I32("hydrology/water_system/via_cell"),
			decoder.I32("hydrology/water_system/node_of_river_cell"),
			decoder.I32("hydrology/water_system/node_of_lake"),
			decoder.I32("hydrology/water_system/ocean_node")[0],
			decoder.I32("hydrology/water_system/river_count")[0],
			decoder.I32("hydrology/water_system/lake_node_count")[0],
			decoder.I32("hydrology/water_system/sink_count")[0]);

		var hydrology = new HydrologySnapshot(rivers, topology, riverLines, basins, lakes, water);
		var query = new QueryIndexSnapshot(decoder.I32("query_index/nearest_landmass_id"),
			decoder.I32("query_index/nearest_landmass_dist_hops"),
			decoder.I32("query_index/nearest_river_cell"),
			decoder.I32("query_index/nearest_river_dist_hops"));

		return new GeographySnapshot(manifest, grid, terrain, surface, tectonics, climate, hydrology, query);
	}

	sealed class Decoder
	{
		readonly Dictionary<string, MapBlockDescriptor> _descriptors;
		readonly Dictionary<string, byte[]> _blocks;

		public Decoder(GeographyManifest manifest, Dictionary<string, byte[]> blocks)
		{
			_descriptors = manifest.Blocks.ToDictionary(block => block.Id, StringComparer.Ordinal);
			_blocks = blocks;
		}

		public int[] Shape(string id) => _descriptors[id].Shape.ToArray();

		public byte[] U8(string id) => Array<byte>(id, MapBlockElementType.U8);
		public int[] I32(string id) => Array<int>(id, MapBlockElementType.I32);
		public float[] F32(string id) => Array<float>(id, MapBlockElementType.F32);
		public double[] F64(string id) => Array<double>(id, MapBlockElementType.F64);
		public ulong[] U64(string id) => Array<ulong>(id, MapBlockElementType.U64);
		public bool[] Bool(string id) => U8(id).Select(value => value != 0).ToArray();

		public NullableFloat64Snapshot Nullable(string id, int count)
			=> new(U8(id + "_valid"), F64(id));

		public WindSetSnapshot Wind(string prefix, WindKind kind)
			=> new(kind, F32(prefix + "/monthly"), F32(prefix + "/annual"), F32(prefix + "/annual_speed"));

		public SurfaceSnapshot Surface(int n, int p)
		{
			var types = U8("surface/surface_crust_type").Select(MapGeologyWire.FromCrustType);
			var omega = Float3("surface/motion_omega", p);
			if (!_descriptors.ContainsKey("surface/projected/emergent_fraction"))
				return new SurfaceSnapshot(types, omega, null);
			return new SurfaceSnapshot(types, omega,
				new ProjectedSurfaceSnapshot(F32("surface/projected/emergent_fraction"),
					F32("surface/projected/land_height_m"), F32("surface/projected/water_height_m"),
					F32("surface/projected/material_height_m"), F64("surface/projected/oceanic_coverage"),
					Nullable("surface/projected/ocean_age_ma", n), Double3("surface/projected/physical_omega_rad_per_ma", p),
					F64("surface/projected/volcanic_rate_km_per_ma"),
					F64("surface/projected/emergent_land_fraction")[0]));
		}

		Float3Snapshot[] Float3(string id, int count) => Array<float>(id, MapBlockElementType.F32)
			.Chunk(3).Select(v => new Float3Snapshot(v[0], v[1], v[2])).ToArray();
		Double3Snapshot[] Double3(string id, int count) => Array<double>(id, MapBlockElementType.F64)
			.Chunk(3).Select(v => new Double3Snapshot(v[0], v[1], v[2])).ToArray();

		T[] Array<T>(string id, MapBlockElementType type) where T : unmanaged
		{
			MapBlockDescriptor descriptor = _descriptors[id];
			if (descriptor.ElementType != type)
				throw MapPackageFormat.Failure(MapIssueCode.InvalidValue, descriptor.Path, "The block element type is unexpected.");
			return MapPackageFormat.Decode<T>(_blocks[id], type, descriptor.Shape.ToArray(), descriptor.Path);
		}
	}
}

/// <summary>只读文件头/清单探测：不声称完整校验，不构建世界。</summary>
internal static class MapPackageProbe
{
	internal static MapProbeResult Probe(string path)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return Invalid(MapIssueCode.IoFailure, "package", "The map file does not exist.");
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (stream.Length > GeographyManifest.MaxFileBytes)
				return Invalid(MapIssueCode.SizeLimit, "package", "The map file exceeds the size limit.");
			using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
			ZipArchiveEntry entry = archive.GetEntry(MapPackageFormat.ManifestEntry);
			if (entry == null) return Invalid(MapIssueCode.MissingBlock, MapPackageFormat.ManifestEntry,
				"The package has no manifest.");
			byte[] bytes = MapPackageFormat.ReadEntry(entry, GeographyManifest.MaxManifestBytes);
			ManifestJson json = JsonSerializer.Deserialize<ManifestJson>(bytes, MapPackageFormat.JsonOptions);
			if (json == null) return Invalid(MapIssueCode.UnknownFormat, MapPackageFormat.ManifestEntry,
				"The manifest is empty.");
			GeographyManifest manifest = ManifestJsonMapping.ToManifest(json);
			manifest.ValidateLayout();
			return new MapProbeResult(MapProbeStatus.HeaderReadable, manifest.MapId, manifest.Title,
				manifest.Format, manifest.SchemaVersion, null);
		}
		catch (MapStructureException e)
		{
			MapProbeStatus status = e.Issue.Code == MapIssueCode.UnsupportedVersion
				? MapProbeStatus.Unsupported : MapProbeStatus.Invalid;
			return new MapProbeResult(status, null, null, null, null, e.Issue);
		}
		catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
		{
			return Invalid(MapIssueCode.UnknownFormat, "package", "The file is not a readable map package.");
		}
	}

	static MapProbeResult Invalid(MapIssueCode code, string location, string message)
		=> new(MapProbeStatus.Invalid, null, null, null, null,
			new MapIssue(code, location, null, message));
}