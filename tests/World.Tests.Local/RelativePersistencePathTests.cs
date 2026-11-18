using System;
using System.IO;
using System.Linq;
using World.Application.Storage;
using World.Data;
using World.Data.Maps;
using World.Infrastructure;
using World.Infrastructure.Maps;
using World.Logic;
using World.Utils;

namespace World.Tests;

internal static class RelativePersistencePathTests
{
	internal static void ModelsUseNormalizedRelativePaths()
	{
		var probe = new MapProbeResult(MapProbeStatus.HeaderReadable, Guid.NewGuid(), "test",
			GeographyManifest.CurrentFormat, GeographyManifest.CurrentSchemaVersion, null);
		var candidate = new MapPackageCandidate("userdata\\saves/./test.wmap", probe);
		Program.True(candidate.FilePath == "userdata/saves/test.wmap", "Catalog candidates must normalize relative paths.");
		var entry = new MapLibraryEntry("userdata/saves/../saves/test.wmap", "test", 0, DateTimeOffset.UtcNow, probe);
		Program.True(entry.FilePath == candidate.FilePath, "Library models must use the same relative path convention.");
		foreach (string invalid in new[] { Path.Combine(AppContext.BaseDirectory, "test.wmap"), "../test.wmap", "/test.wmap", "\\test.wmap" })
		{
			Program.Throws(() => new MapPackageCandidate(invalid, probe), "Candidate accepted a non-relative path.");
			Program.Throws(() => new MapLibraryEntry(invalid, "test", 0, DateTimeOffset.UtcNow, probe), "Library entry accepted a non-relative path.");
		}
		Program.True(RootRelativePath.Normalize("a\\b/../file.dat") == "a/file.dat", "Separators and dot segments must normalize without cwd.");
	}

	internal static void CatalogPathsSurviveMovingGameRoot()
	{
		string temporary = Path.Combine(Environment.CurrentDirectory, ".tmp-relative-path-tests-" + Guid.NewGuid().ToString("N"));
		string firstRoot = Path.Combine(temporary, "first");
		string secondRoot = Path.Combine(temporary, "second");
		try
		{
			var sim = new WorldGenSimulation(1, PlanetGeometry.ProductionRadius);
			sim.Run(new WorldSpec());
			var snapshot = GeographySnapshotCapture.Capture(sim.Facts, Guid.NewGuid(), "relative", "", DateTimeOffset.UtcNow, "tests");
			const string relative = "userdata/saves/portable.wmap";
			var storage = new MapPackageFileStorage();
			Program.True(new FileSaveComponent<GeographySnapshot>(storage, firstRoot).Save(snapshot, relative) == relative,
				"Saving must return a relative path.");
			var firstCatalog = new GameMapCatalog(firstRoot, GamePaths.MapDirectory);
			var firstEntry = firstCatalog.List().Single();
			Program.True(firstCatalog.Directory == GamePaths.MapDirectory && firstEntry.FilePath == relative,
				"Catalog must not leak its physical root.");

			string secondPath = RootRelativePath.Resolve(secondRoot, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(secondPath));
			File.Copy(RootRelativePath.Resolve(firstRoot, relative), secondPath);
			var secondEntry = new GameMapCatalog(secondRoot, GamePaths.MapDirectory).List().Single();
			Program.True(secondEntry.FilePath == firstEntry.FilePath, "Moving the root must not change catalog paths.");
			var loaded = new FileLoadComponent<GeographySnapshot>(storage, secondRoot).Load(secondEntry.FilePath);
			Program.True(loaded.Manifest.MapId == snapshot.Manifest.MapId, "Relative catalog paths must load after moving the root.");
		}
		finally
		{
			if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
		}
	}

	internal static void FixedGameLocationsAreRelative()
	{
		foreach (string path in new[] { GamePaths.MapDirectory, GamePaths.ConfigDirectory,
			GamePaths.GenerationProfilesPath, GamePaths.CivilizationProfilesPath })
			Program.True(!Path.IsPathRooted(path) && RootRelativePath.Normalize(path) == path,
				"Fixed game locations must be normalized relative paths.");
	}
}
