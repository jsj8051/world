using System;
using System.Collections.Generic;
using World.Data.Maps;

namespace World.Tests;

internal static class MapManifestTests
{
	public static void ManifestAcceptsHeaderWithoutDirectory()
	{
		var manifest = Create(blocks: Array.Empty<MapBlockDescriptor>());
		Program.True(manifest.Blocks.Count == 0, "Header metadata should not imply a validated layout.");
	}

	public static void ManifestRejectsUnknownFormat()
	{
		var exception = Capture(() => Create(format: "world.other"));
		Program.True(exception.Issue.Code == MapIssueCode.UnknownFormat, "Unknown format should have a typed issue.");
	}

	public static void ManifestRejectsUnsupportedVersion()
	{
		var exception = Capture(() => Create(version: 2));
		Program.True(exception.Issue.Code == MapIssueCode.UnsupportedVersion, "Version mismatch should have a typed issue.");
	}

	public static void DescriptorCopiesShapeAndChecksByteLength()
	{
		int[] source = { 2, 3 };
		var descriptor = new MapBlockDescriptor("climate/wind", "data/climate/wind.bin",
			MapBlockElementType.F32, source, 24, new string('a', 64));
		source[0] = 99;
		Program.True(descriptor.Shape[0] == 2, "Shape must be an immutable copy.");
		Program.Throws(() => new MapBlockDescriptor("climate/wind", "data/climate/wind.bin",
			MapBlockElementType.F32, new[] { 2, 3 }, 23, new string('a', 64)), "Incorrect byte length must be rejected.");
	}

	public static void ManifestLayoutRejectsMissingRequiredBlocks()
	{
		var exception = Capture(() => Create(blocks: Array.Empty<MapBlockDescriptor>()).ValidateLayout());
		Program.True(exception.Issue.Code == MapIssueCode.MissingBlock, "A complete layout requires schema blocks.");
	}

	public static void ManifestRejectsDuplicateDescriptors()
	{
		var block = new MapBlockDescriptor("grid/cell_ids", "data/grid/cell_ids.bin",
			MapBlockElementType.U64, new[] { 122 }, 976, new string('b', 64));
		var exception = Capture(() => Create(blocks: new[] { block, block }));
		Program.True(exception.Issue.Code == MapIssueCode.DuplicateEntry, "Duplicate directory rows must be rejected.");
	}

	public static void ProbeRequiresIssueForUnsupportedResults()
	{
		Program.Throws(() => new MapProbeResult(MapProbeStatus.Unsupported, null, null,
			GeographyManifest.CurrentFormat, 2, null), "Unsupported results require an issue.");
	}

	static GeographyManifest Create(string format = GeographyManifest.CurrentFormat, int version = GeographyManifest.CurrentSchemaVersion,
		IEnumerable<MapBlockDescriptor> blocks = null)
		=> new(format, version, Guid.Parse("11111111-1111-4111-8111-111111111111"), "Header fixture", "",
			new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "test", 0, 122,
			6371, 2, 0, 12, null, false, blocks ?? Array.Empty<MapBlockDescriptor>());

	static MapStructureException Capture(Action action)
	{
		try { action(); }
		catch (MapStructureException exception) { return exception; }
		throw new Exception("Expected a MapStructureException.");
	}
}
