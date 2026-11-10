using System;
using System.IO;

namespace World.Data.Maps;

internal enum MapProbeStatus { HeaderReadable, Unsupported, Invalid }

internal sealed class MapProbeResult
{
	public MapProbeStatus Status { get; }
	public Guid? MapId { get; }
	public string Title { get; }
	public string Format { get; }
	public int? SchemaVersion { get; }
	public MapIssue Issue { get; }

	public MapProbeResult(MapProbeStatus status, Guid? mapId, string title, string format,
		int? schemaVersion, MapIssue issue)
	{
		MapStructure.Require(Enum.IsDefined(status), "probe.status", "The probe status is unknown.");
		if (status == MapProbeStatus.HeaderReadable)
		{
			MapStructure.Require(mapId.HasValue && mapId.Value != Guid.Empty, "probe.mapId", "A readable header requires a map ID.");
			MapStructure.Require(title != null && title.Trim().Length is >= 1 and <= 120, "probe.title", "A readable header requires a valid title.");
			MapStructure.Require(format == GeographyManifest.CurrentFormat, "probe.format", "The readable header format is invalid.");
			MapStructure.Require(schemaVersion == GeographyManifest.CurrentSchemaVersion, "probe.schemaVersion", "The readable header version is invalid.");
			MapStructure.Require(issue == null, "probe.issue", "A readable header cannot contain an issue.");
		}
		else
			MapStructure.Present(issue, "probe.issue");
		(Status, MapId, Title, Format, SchemaVersion, Issue) = (status, mapId, title, format, schemaVersion, issue);
	}
}

internal sealed class MapLibraryEntry
{
	public string FilePath { get; }
	public string DisplayName { get; }
	public long FileSizeBytes { get; }
	public DateTimeOffset LastWriteUtc { get; }
	public MapProbeResult Probe { get; }

	public MapLibraryEntry(string filePath, string displayName, long fileSizeBytes,
		DateTimeOffset lastWriteUtc, MapProbeResult probe)
	{
		MapStructure.Require(!string.IsNullOrWhiteSpace(filePath) && Path.IsPathFullyQualified(filePath),
			"library.filePath", "An absolute file path is required.");
		string normalized = Path.GetFullPath(filePath);
		MapStructure.Require(string.Equals(filePath, normalized, StringComparison.Ordinal),
			"library.filePath", "The file path must already be normalized.");
		MapStructure.Require(!string.IsNullOrWhiteSpace(displayName), "library.displayName", "A display name is required.");
		MapStructure.Require(fileSizeBytes >= 0 && fileSizeBytes <= GeographyManifest.MaxFileBytes,
			"library.fileSizeBytes", "The file size is outside the supported bound.", MapIssueCode.SizeLimit);
		MapStructure.Require(lastWriteUtc.Offset == TimeSpan.Zero, "library.lastWriteUtc", "The timestamp must be UTC.");
		MapStructure.Present(probe, "library.probe");
		(FilePath, DisplayName, FileSizeBytes, LastWriteUtc, Probe) =
			(normalized, displayName, fileSizeBytes, lastWriteUtc, probe);
	}
}
