using System;
using System.IO;
using System.Threading;
using World.Data;
using World.Data.Maps;
using World.Infrastructure.Maps;
using World.Logic;
using World.Application.Loading;

namespace World.Tests;

internal static class MapRoundTripTests
{
	public static void GeneratedWorldRoundTripsThroughSnapshot()
	{
		var sim = new WorldGenSimulation(1, PlanetGeometry.ProductionRadius);
		sim.Run(new WorldSpec());
		WorldFacts facts = sim.Facts;
		GeographySnapshot snapshot = Capture(facts);
		Program.True(!snapshot.Manifest.HasProjectedSurface, "A static world must not claim a projected surface.");
		ImportedWorldFactsSource imported = ImportedWorldFactsFactory.Build(snapshot);
		Program.True(imported.Facts.Evolution == null, "Import must not synthesize an evolution frame.");
		Program.True(imported.Facts.SourceTimeMa == null, "A static map must not claim an evolution time.");
		Compare(facts, imported.Facts, snapshot.Manifest.CellCount);
	}

	public static void PackageWriteReadPreservesFacts()
	{
		var sim = new WorldGenSimulation(1, PlanetGeometry.ProductionRadius);
		sim.Run(new WorldSpec());
		WorldFacts facts = sim.Facts;
		GeographySnapshot snapshot = Capture(facts);
		string directory = Path.Combine(Environment.CurrentDirectory, ".tmp-map-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, "roundtrip.wmap");
		try
		{
			MapPackageWriter.Write(snapshot, path);
			MapProbeResult probe = MapPackageProbe.Probe(path);
			Program.True(probe.Status == MapProbeStatus.HeaderReadable,
				$"Probe failed: {probe.Status}/{probe.Issue?.Code}: {probe.Issue?.Message}");
			GeographySnapshot read = MapPackageReader.Read(path);
			ImportedWorldFactsSource first = ImportedWorldFactsFactory.Build(snapshot);
			ImportedWorldFactsSource second = ImportedWorldFactsFactory.Build(read);
			Compare(first.Facts, second.Facts, read.Manifest.CellCount);
			Program.True(read.Manifest.Title == snapshot.Manifest.Title
				&& read.Manifest.MapId == snapshot.Manifest.MapId, "Manifest identity changed across the package.");
		}
		finally
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		}
	}

	public static void ParallelReadReportsBlockProgress()
	{
		var sim = new WorldGenSimulation(1, PlanetGeometry.ProductionRadius);
		sim.Run(new WorldSpec());
		string directory = Path.Combine(Environment.CurrentDirectory, ".tmp-map-parallel-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, "parallel.wmap");
		try
		{
			MapPackageWriter.Write(Capture(sim.Facts), path);
			int calls = 0, last = 0, total = 0;
			GeographySnapshot read = MapPackageReader.Read(path,
				(done, all) =>
				{
					Interlocked.Increment(ref calls);
					Interlocked.Exchange(ref last, done);
					Interlocked.Exchange(ref total, all);
				}, CancellationToken.None);

			Program.True(Volatile.Read(ref calls) > 0, "并行读取没有上报进度");
			Program.True(Volatile.Read(ref last) == Volatile.Read(ref total)
				&& Volatile.Read(ref total) == read.Manifest.Blocks.Count,
				$"并行进度终值异常 calls={Volatile.Read(ref calls)} last={Volatile.Read(ref last)} " +
				$"total={Volatile.Read(ref total)} blocks={read.Manifest.Blocks.Count}");
		}
		finally
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		}
	}

	public static void BrowseJobReportsProgressAndBuildsFacts()
	{
		var sim = new WorldGenSimulation(1, PlanetGeometry.ProductionRadius);
		sim.Run(new WorldSpec());
		string directory = Path.Combine(Environment.CurrentDirectory, ".tmp-map-job-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, "job.wmap");
		try
		{
			MapPackageWriter.Write(Capture(sim.Facts), path);
			var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "加载地图", CancellationToken.None);
			var repository = new GameMapRepository(new MapPackageFileStorage(), directory);
			var job = new MapBrowseJob(repository);
			using var monitor = new LoadProgressMonitor(operation, LoadStagePlans.Browse, 0f, 0.75f);
			monitor.Attach(job);
			ImportedWorldFactsSource source = job.Run(path, CancellationToken.None);

			Program.True(operation.Progress >= 0.75f, $"浏览 Job 未推进到事实就绪：{operation.Progress}");
			Program.True(source.Facts.Ball.CellIds.Length > 0, "浏览 Job 没有装配出世界事实");

			operation.Complete();
			Program.True(operation.State == LoadState.Completed && operation.Progress == 1f, "浏览 Job 完成后状态错误");
		}
		finally
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		}
	}

	public static void PackageRejectsMissingAndCorruptFiles()
	{
		string directory = Path.Combine(Environment.CurrentDirectory, ".tmp-map-bad-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			string missing = Path.Combine(directory, "missing.wmap");
			Program.True(MapPackageProbe.Probe(missing).Status == MapProbeStatus.Invalid, "Missing file probed as valid.");
			Program.Throws(() => MapPackageReader.Read(missing), "Missing file was read.");

			string corrupt = Path.Combine(directory, "corrupt.wmap");
			File.WriteAllBytes(corrupt, new byte[] { 0x50, 0x4b, 0x03, 0x04, 0, 0, 0, 0 });
			Program.True(MapPackageProbe.Probe(corrupt).Status == MapProbeStatus.Invalid, "Truncated ZIP probed as valid.");
			Program.Throws(() => MapPackageReader.Read(corrupt), "Truncated ZIP was read.");
		}
		finally
		{
			if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
		}
	}

	static GeographySnapshot Capture(WorldFacts facts) => GeographySnapshotCapture.Capture(facts, Guid.NewGuid(),
		"roundtrip", string.Empty, DateTimeOffset.UtcNow, "tests");

	static void Compare(WorldFacts expected, WorldFacts actual, int n)
	{
		Program.True(actual.Ball.Res == expected.Ball.Res && actual.Ball.CellIds.Length == n, "Grid shape changed.");
		for (int i = 0; i < n; i++)
		{
			Program.True(expected.Ball.CellIds[i] == actual.Ball.CellIds[i], $"Cell order changed at {i}.");
			Program.True(BitConverter.SingleToInt32Bits(expected.HeightM[i])
				== BitConverter.SingleToInt32Bits(actual.HeightM[i]), $"Elevation changed at {i}.");
			Program.True(expected.FinalLand[i] == actual.FinalLand[i], $"Land mask changed at {i}.");
			Program.True(expected.Rivers.Downstream[i] == actual.Rivers.Downstream[i], $"Flow changed at {i}.");
			Program.True(BitConverter.SingleToInt32Bits(expected.TemperatureC[i])
				== BitConverter.SingleToInt32Bits(actual.TemperatureC[i]), $"Temperature changed at {i}.");
			Program.True(BitConverter.SingleToInt32Bits(expected.PrecipitationMm[i])
				== BitConverter.SingleToInt32Bits(actual.PrecipitationMm[i]), $"Precipitation changed at {i}.");
		}
		Program.True(expected.Basins.BasinId.Length == actual.Basins.BasinId.Length, "Basin table changed.");
		Program.True(expected.Lakes.LakeCount == actual.Lakes.LakeCount, "Lake count changed.");
		Program.True(expected.SpatialIndex.NearestRiverCell.Length == actual.SpatialIndex.NearestRiverCell.Length,
			"Query index shape changed.");
	}
}