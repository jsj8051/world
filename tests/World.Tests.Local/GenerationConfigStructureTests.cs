using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using World.Data;
using World.Data.Generation;

namespace World.Tests;

internal static class GenerationConfigStructureTests
{
	public static void DefaultsAndEnumSelectionsAreValid()
	{
		var config = new GenerationConfig();
		config.Validate();
		config.Layout = ContinentLayout.Archipelago;
		config.SeaLevel = SeaLevelPreset.High;
		config.Relief = TerrainReliefPreset.Rugged;
		config.Temperature = TemperaturePreset.Cold;
		config.Seasonality = SeasonalityPreset.Strong;
		config.Rainfall = RainfallPreset.Wet;
		config.Validate();
	}

	public static void PlateCountEnforcesApprovedBounds()
	{
		new GenerationConfig { PlateCount = GenerationConfig.MinimumPlateCount }.Validate();
		new GenerationConfig { PlateCount = GenerationConfig.MaximumPlateCount }.Validate();
		Program.Throws(() => new GenerationConfig { PlateCount = 3 }.Validate(), "Too few plates were accepted.");
		Program.Throws(() => new GenerationConfig { PlateCount = 25 }.Validate(), "Too many plates were accepted.");
	}

	public static void UnknownEnumValuesAreRejected()
	{
		Program.Throws(() => new GenerationConfig { Layout = (ContinentLayout)99 }.Validate(), "Unknown layout was accepted.");
		Program.Throws(() => new GenerationConfig { Rainfall = (RainfallPreset)99 }.Validate(), "Unknown rainfall preset was accepted.");
	}

	public static void ProfileRequiresIdentityNameAndValidConfig()
	{
		var now = DateTimeOffset.UtcNow;
		var profile = new GenerationProfile { Id = Guid.NewGuid(), Name = "Earth", CreatedUtc = now, UpdatedUtc = now };
		profile.Validate();
		Program.Throws(() => new GenerationProfile { Id = Guid.Empty, Name = "Earth", CreatedUtc = now, UpdatedUtc = now }.Validate(),
			"Empty profile identity was accepted.");
		Program.Throws(() => new GenerationProfile { Id = Guid.NewGuid(), Name = "  ", CreatedUtc = now, UpdatedUtc = now }.Validate(),
			"Blank profile name was accepted.");
		Program.Throws(() => new GenerationProfile { Id = Guid.NewGuid(), Name = new string('x', 41), CreatedUtc = now, UpdatedUtc = now }.Validate(),
			"Overlong profile name was accepted.");
		Program.Throws(() => new GenerationProfile { Id = Guid.NewGuid(), Name = "Earth", CreatedUtc = now, UpdatedUtc = now.AddSeconds(-1) }.Validate(),
			"Updated-before-created timestamps were accepted.");
	}

	public static void CatalogEnforcesVersionAndUniqueProfiles()
	{
		var now = DateTimeOffset.UtcNow;
		var first = new GenerationProfile { Id = Guid.NewGuid(), Name = "Earth", CreatedUtc = now, UpdatedUtc = now };
		var second = new GenerationProfile { Id = Guid.NewGuid(), Name = "Islands", CreatedUtc = now, UpdatedUtc = now };
		new GenerationProfileCatalog { Profiles = { first, second } }.Validate();
		Program.Throws(() => new GenerationProfileCatalog { SchemaVersion = 2 }.Validate(), "Unknown catalog version was accepted.");
		Program.Throws(() => new GenerationProfileCatalog { Profiles = { first, first } }.Validate(), "Duplicate profile ID was accepted.");
		Program.Throws(() => new GenerationProfileCatalog
		{
			Profiles =
			{
				first,
				new GenerationProfile { Id = Guid.NewGuid(), Name = " earth ", CreatedUtc = now, UpdatedUtc = now }
			}
		}.Validate(), "Case-insensitive duplicate profile name was accepted.");
	}

	public static void JsonRequiresCompleteKnownFields()
	{
		var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
		var now = DateTimeOffset.UtcNow;
		var catalog = new GenerationProfileCatalog
		{
			Profiles = { new GenerationProfile { Id = Guid.NewGuid(), Name = "Roundtrip", CreatedUtc = now, UpdatedUtc = now } }
		};
		string json = JsonSerializer.Serialize(catalog, options);
		var restored = JsonSerializer.Deserialize<GenerationProfileCatalog>(json, options);
		restored.Validate();
		Program.True(restored.Profiles[0].Name == "Roundtrip" && restored.Profiles[0].Config.Seed == 42,
			"Catalog JSON roundtrip changed identity or settings.");
		Program.Throws(() => JsonSerializer.Deserialize<GenerationConfig>("{}", options), "Missing settings were accepted.");
		Program.Throws(() => JsonSerializer.Deserialize<GenerationProfileCatalog>("{}", options), "Missing catalog fields were accepted.");
		Program.Throws(() => JsonSerializer.Deserialize<GenerationProfile>("{}", options), "Missing profile metadata was accepted.");
		Program.Throws(() => JsonSerializer.Deserialize<GenerationProfileCatalog>(
			json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"unknown\":true"), options),
			"Unknown catalog field was accepted.");
		Program.Throws(() => JsonSerializer.Deserialize<GenerationProfileCatalog>(
			json.Replace("\"seed\":42", "\"seed\":42,\"unknown\":true"), options),
			"Unknown setting field was accepted.");
		Program.Throws(() => JsonSerializer.Deserialize<GenerationProfileCatalog>(
			json.Replace("\"Auto\"", "\"Earth\""), options),
			"Unknown layout string was accepted.");
	}

	public static void AdditionalStructureBoundariesAreEnforced()
	{
		new GenerationConfig { Seed = int.MinValue }.Validate();
		new GenerationConfig { Seed = int.MaxValue }.Validate();
		Program.Throws(() => new GenerationConfig { SeaLevel = (SeaLevelPreset)99 }.Validate(), "Unknown sea level accepted.");
		Program.Throws(() => new GenerationConfig { Relief = (TerrainReliefPreset)99 }.Validate(), "Unknown relief accepted.");
		Program.Throws(() => new GenerationConfig { Temperature = (TemperaturePreset)99 }.Validate(), "Unknown temperature accepted.");
		Program.Throws(() => new GenerationConfig { Seasonality = (SeasonalityPreset)99 }.Validate(), "Unknown seasonality accepted.");
		var now = DateTimeOffset.UtcNow;
		new GenerationProfile { Name = new string('x', 40), CreatedUtc = now, UpdatedUtc = now }.Validate();
		Program.Throws(() => new GenerationProfile { Name = "Null", CreatedUtc = now, UpdatedUtc = now, Config = null }.Validate(), "Null config accepted.");
		Program.Throws(() => new GenerationProfile { Name = "Invalid", CreatedUtc = now, UpdatedUtc = now,
			Config = new GenerationConfig { PlateCount = 25 } }.Validate(), "Nested invalid config accepted.");
		Program.Throws(() => new GenerationProfile { Name = "Offset", CreatedUtc = now.ToOffset(TimeSpan.FromHours(8)), UpdatedUtc = now }.Validate(), "Non-UTC time accepted.");
		Program.Throws(() => new GenerationProfileCatalog { Profiles = null }.Validate(), "Null profile list accepted.");
		Program.Throws(() => new GenerationProfileCatalog { Profiles = { null } }.Validate(), "Null profile list item accepted.");
	}
}
