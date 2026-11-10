using System;
using System.Collections.Generic;
using System.Linq;

namespace World.Data.Maps;

// Wire shape for manifest.json. Kept separate from the validated carrier so a missing or
// mismatched field fails through GeographyManifest's constructor instead of serializer binding.
internal sealed class ManifestJson
{
	public string Format { get; set; }
	public int SchemaVersion { get; set; }
	public Guid MapId { get; set; }
	public string Title { get; set; }
	public string Description { get; set; }
	public DateTimeOffset CreatedUtc { get; set; }
	public string ProducerVersion { get; set; }
	public int H3Resolution { get; set; }
	public int CellCount { get; set; }
	public double PhysicalRadiusKm { get; set; }
	public float RenderRadius { get; set; }
	public float SeaLevelM { get; set; }
	public int MonthCount { get; set; }
	public double? SourceTimeMa { get; set; }
	public bool HasProjectedSurface { get; set; }
	public List<BlockJson> Blocks { get; set; }
}

internal sealed class BlockJson
{
	public string Id { get; set; }
	public string Path { get; set; }
	public MapBlockElementType ElementType { get; set; }
	public int[] Shape { get; set; }
	public long ByteLength { get; set; }
	public string Sha256 { get; set; }
}

internal static class ManifestJsonMapping
{
	public static GeographyManifest ToManifest(ManifestJson json)
	{
		if (json == null) throw new ArgumentNullException(nameof(json));
		IEnumerable<MapBlockDescriptor> blocks = (json.Blocks ?? new List<BlockJson>()).Select(block =>
			new MapBlockDescriptor(block.Id, block.Path, block.ElementType, block.Shape ?? Array.Empty<int>(),
				block.ByteLength, block.Sha256));
		return new GeographyManifest(json.Format, json.SchemaVersion, json.MapId, json.Title, json.Description,
			json.CreatedUtc, json.ProducerVersion, json.H3Resolution, json.CellCount, json.PhysicalRadiusKm,
			json.RenderRadius, json.SeaLevelM, json.MonthCount, json.SourceTimeMa, json.HasProjectedSurface, blocks);
	}
}