using System;
using System.IO;
using System.Threading;
using World.Application.Generation;
using World.Application.Storage;
using World.Data;
using World.Data.Generation;
using World.Infrastructure.Generation;
using World.Params.Generation;

namespace World.Tests;

internal static class GenerationConfigLogicTests
{
	public static void DefaultsStartAppliedAndEditsRequireExplicitApply()
	{
		var store = new MemoryStore();
		var session = new GenerationConfigSession(store, "profiles.json");
		Program.True(session.Applied.Seed == 42 && !session.HasUnappliedChanges,
			"A fresh session must start with the defaults applied.");
		Program.True(session.GetAppliedForGeneration().Seed == 42 && store.Writes == 0,
			"Applied defaults could not start generation.");
		var edit = session.Draft; edit.Seed = 84; session.Edit(edit);
		Program.True(session.HasUnappliedChanges, "An edit did not mark the session dirty.");
		Program.Throws(() => session.GetAppliedForGeneration(), "Dirty generation accepted.");
		session.Save("Draft");
		Program.True(session.Applied.Seed == 42 && session.HasUnappliedChanges, "Save implicitly applied draft.");
		session.Apply();
		Program.True(session.GetAppliedForGeneration().Seed == 84 && store.Writes == 1, "Applied config incorrect.");
	}

	public static void ProfileNamingSelectionAndDeletionRemainSeparateFromApply()
	{
		var session = new GenerationConfigSession(new MemoryStore(), "profiles.json");
		session.Save(" First ");
		Guid first = session.SelectedProfileId.Value;
		Program.True(session.Profiles[0].Name == "First", "Name was not normalized.");
		session.Save("Second", true);
		Guid second = session.SelectedProfileId.Value;
		Program.True(first != second && session.Profiles.Count == 2, "Save as new reused identity.");
		session.Select(first); session.Rename(first, "Renamed");
		Program.True(session.SelectedProfileId == first && session.Profiles[0].Name == "Renamed", "Rename changed selection.");
		Program.Throws(() => session.Rename(second, " renamed "), "Duplicate name accepted.");
		session.Apply(); session.Delete(first);
		Program.True(session.SelectedProfileId == null && session.Profiles.Count == 1 && session.Applied.Seed == 42,
			"Delete changed applied config or retained selection.");
		Program.Throws(() => session.Select(first), "Deleted profile selected.");
		session.ResetDraft();
		Program.True(session.Draft.Seed == 42 && session.Applied.Seed == 42, "Reset erased applied config.");
	}

	public static void StoreFailuresDoNotPartiallyCommitSession()
	{
		var store = new MemoryStore();
		var session = new GenerationConfigSession(store, "profiles.json");
		session.Save("Original");
		Guid original = session.SelectedProfileId.Value;
		store.FailWrite = true;
		Program.Throws(() => session.Save("New", true), "Failed save succeeded.");
		Program.Throws(() => session.Rename(original, "Changed"), "Failed rename succeeded.");
		Program.Throws(() => session.Delete(original), "Failed delete succeeded.");
		Program.True(session.Profiles.Count == 1 && session.Profiles[0].Name == "Original" && session.SelectedProfileId == original,
			"Failed write partially changed catalog or selection.");
		Program.True(store.Catalog.Profiles.Count == 1 && store.Catalog.Profiles[0].Name == "Original", "Store catalog was mutated before write.");
		Program.Throws(() => session.Edit(new GenerationConfig { PlateCount = 25 }), "Invalid edit accepted.");
		Program.True(session.Draft.PlateCount == 8, "Failed edit mutated draft.");
	}

	public static void InvalidCatalogIsProtectedButTemporaryGenerationWorks()
	{
		var store = new MemoryStore { FailLoad = true };
		var session = new GenerationConfigSession(store, "profiles.json");
		Program.True(!string.IsNullOrEmpty(session.LoadProblem), "Load failure was hidden.");
		Program.Throws(() => session.Save("Overwrite"), "Broken catalog overwritten.");
		session.Apply();
		Program.True(session.GetAppliedForGeneration().Seed == 42 && store.Writes == 0, "Broken catalog blocks temporary generation.");
	}

	public static void SessionClonesAllInboundAndOutboundData()
	{
		var store = new MemoryStore();
		var session = new GenerationConfigSession(store, "profiles.json");
		var edit = new GenerationConfig { Seed = 7 }; session.Edit(edit); edit.Seed = 9;
		session.Apply(); session.Save("Clone");
		var draft = session.Draft; draft.Seed = 10;
		var applied = session.Applied; applied.Seed = 11;
		var generated = session.GetAppliedForGeneration(); generated.Seed = 12;
		var profiles = session.Profiles; profiles[0].Config.Seed = 13; profiles[0].Name = "Leaked";
		store.Catalog.Profiles[0].Config.Seed = 14;
		Program.True(session.Draft.Seed == 7 && session.Applied.Seed == 7 && session.Profiles[0].Config.Seed == 7
			&& session.Profiles[0].Name == "Clone", "Mutable state leaked.");
	}

	public static void MapperCopiesDefaultsAndMapsEveryPreset()
	{
		var defaults = new WorldSpec(); defaults.Terrain.LandBaseM = 33; defaults.Climate.Months = 12;
		var config = new GenerationConfig { Seed = 19, Layout = ContinentLayout.Pangea, PlateCount = 24,
			SeaLevel = SeaLevelPreset.Low, Relief = TerrainReliefPreset.Gentle, Temperature = TemperaturePreset.Cold,
			Seasonality = SeasonalityPreset.Weak, Rainfall = RainfallPreset.Dry };
		var low = GenerationConfigMapper.CreateWorldSpec(config, defaults);
		Program.True(low.Seed == 19 && low.Layout == ContinentLayout.Pangea && low.Plate.PlateCount == 24
			&& low.Terrain.SeaLevelM == -100 && low.Terrain.VerticalScale == 14 && low.Climate.Insolation == .9f
			&& low.Climate.SeasonalBaseC == 12.5f && low.Climate.PrecipBaseMmPerMonth == 20, "Low presets incorrect.");
		var standard = GenerationConfigMapper.CreateWorldSpec(new GenerationConfig(), defaults);
		Program.True(standard.Terrain.SeaLevelM == 0 && standard.Terrain.VerticalScale == 20 && standard.Climate.Insolation == 1
			&& standard.Climate.SeasonalBaseC == 25 && standard.Climate.PrecipBaseMmPerMonth == 30, "Standard presets incorrect.");
		config.SeaLevel = SeaLevelPreset.High; config.Relief = TerrainReliefPreset.Rugged; config.Temperature = TemperaturePreset.Hot;
		config.Seasonality = SeasonalityPreset.Strong; config.Rainfall = RainfallPreset.Wet;
		var high = GenerationConfigMapper.CreateWorldSpec(config, defaults);
		Program.True(high.Terrain.SeaLevelM == 100 && high.Terrain.VerticalScale == 26 && high.Climate.Insolation == 1.1f
			&& high.Climate.SeasonalBaseC == 37.5f && high.Climate.PrecipBaseMmPerMonth == 40, "High presets incorrect.");
		high.Terrain.LandBaseM = 99; high.Climate.Months = 4; high.Evolution.StepYears = 1; high.Plate.MaxSpeed = 99;
		Program.True(defaults.Terrain.LandBaseM == 33 && defaults.Climate.Months == 12 && defaults.Evolution.StepYears != 1
			&& defaults.Plate.MaxSpeed != 99 && low.Terrain.LandBaseM == 33, "Mapper mutated or shared defaults.");
	}

	public static void FileStoreRoundTripsAndFailedReplacePreservesOriginal()
	{
		string directory = Path.Combine(AppContext.BaseDirectory, "world-generation-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		try
		{
			string path = Path.Combine(directory, "profiles.json");
			var session = new GenerationConfigSession(new FileGenerationProfileStore(), path);
			session.Save("Disk");
			string original = File.ReadAllText(path);
			var loaded = new GenerationConfigSession(new FileGenerationProfileStore(), path);
			Program.True(loaded.Profiles.Count == 1 && loaded.Profiles[0].Name == "Disk", "Disk store roundtrip failed.");
			using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				if (OperatingSystem.IsWindows())
					Program.Throws(() => AtomicTextFile.Write(path, "replacement"), "Locked destination replaced.");
			}
			if (OperatingSystem.IsWindows()) Program.True(File.ReadAllText(path) == original, "Failed replacement changed original.");
			AtomicTextFile.Write(path, "broken");
			var broken = new GenerationConfigSession(new FileGenerationProfileStore(), path);
			Program.Throws(() => broken.Save("Bad"), "Corrupt file overwritten.");
			Program.True(File.ReadAllText(path) == "broken", "Corrupt original was lost.");
		}
		finally { Directory.Delete(directory, recursive: true); }
	}

	sealed class MemoryStore : IFileStorage<GenerationProfileCatalog>
	{
		public GenerationProfileCatalog Catalog = new();
		public int Writes;
		public bool FailWrite;
		public bool FailLoad;
		public GenerationProfileCatalog Import(string path, IFileImportObserver observer = null, CancellationToken cancellation = default)
		{
			if (FailLoad) throw new InvalidDataException("Broken catalog");
			return Catalog;
		}
		public void Export(string path, GenerationProfileCatalog catalog)
		{
			if (FailWrite) throw new IOException("Write refused");
			Writes++; Catalog = catalog;
		}
	}
}
