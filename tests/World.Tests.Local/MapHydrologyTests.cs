using System;
using System.Collections.Generic;
using World.Data.Maps;

namespace World.Tests;

internal static class MapHydrologyTests
{
	public static void MinimalHydrologyValidates() => Valid().Validate(3, 4);

	public static void RawDepressionsStaySeparateFromRoutedBasins()
	{
		Schema().Validate(3, 4);
		var wrong = Schema(basinIdOf: new[] { 2 }, lakeOfBasin: new[] { -1, 0 });
		Program.Throws(() => wrong.Validate(3, 4), "A routed basin ID was accepted as a raw depression ID.");
		var broken = Schema(lakeOfBasin: new[] { -1, -1 });
		Program.Throws(() => broken.Validate(3, 4), "An inconsistent lake inverse was accepted.");
	}

	public static void LakeLayersPreserveNoRimMarker()
	{
		var schema = Schema();
		Program.True(float.IsNaN(schema.Lakes.SpillElevationM[0]), "The no-rim NaN marker was not preserved.");
		var infinite = Schema(spill: new[] { float.PositiveInfinity });
		Program.Throws(() => infinite.Validate(3, 4), "An infinite spill elevation was accepted.");
		var negativeArea = Schema(areaKm2: new[] { -1d });
		Program.Throws(() => negativeArea.Validate(3, 4), "A negative lake area was accepted.");
	}

	public static void HydrologyRejectsWrongLengths()
	{
		Program.Throws(() => Rivers(flowAccum: new[] { 1, 2 }).Validate(3), "A short river array was accepted.");
		Program.Throws(() => new LakesSnapshot(1, new[] { -1, -1, -1 }, Array.Empty<int>(), Array.Empty<int>(),
			Array.Empty<float>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<float>(),
			Array.Empty<int>()).Validate(3), "A lake count without lake data was accepted.");
	}

	public static void HydrologyRejectsCycles()
	{
		Program.Throws(() => Rivers(downstream: new[] { 1, 0, -1 }).Validate(3), "A cyclic river flow was accepted.");
		Program.Throws(() => Topology(downstreamRiver: new[] { 1, 0, -1 }).Validate(3), "A cyclic river graph was accepted.");
		Program.Throws(() => new WaterSystemSnapshot(2, new[] { 0, 2 }, new[] { 0, -1 }, new[] { 1, 0 },
			new[] { -1, -1 }, new[] { -1, -1, -1 }, Array.Empty<int>(), 1, 1, 0, 0).Validate(3, 0),
			"A cyclic water system was accepted.");
	}

	public static void HydrologyRejectsMalformedLinesAndReferences()
	{
		Program.Throws(() => Lines(new[] { 0, 2 }).Validate(3, 4), "Line offsets were not checked against cells.");
		Program.Throws(() => Lines(new[] { 0, 1 }, points: new[] { 0f, 0f, 2f }).Validate(3, 4),
			"A non-unit line direction was accepted.");
		Program.Throws(() => Lines(new[] { 0, 1 }, lineOutlet: new[] { 7 }).Validate(3, 4),
			"An out-of-range line outlet was accepted.");
		Program.Throws(() => new WaterSystemSnapshot(1, new[] { 1 }, new[] { 4 }, new[] { -1 },
			new[] { -1 }, new[] { -1, -1, -1 }, Array.Empty<int>(), -1, 0, 1, 0).Validate(3, 0),
			"A lake node with an out-of-range lake reference was accepted.");
	}

	static HydrologySnapshot Valid() => new(Rivers(), Topology(), Lines(), Basins(),
		new LakesSnapshot(0, new[] { -1, -1, -1 }, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<float>(),
			Array.Empty<double>(), Array.Empty<double>(), Array.Empty<float>(), Array.Empty<int>()),
		new WaterSystemSnapshot(1, new[] { 2 }, new[] { -1 }, new[] { -1 }, new[] { -1 },
			new[] { -1, -1, -1 }, Array.Empty<int>(), 0, 0, 0, 0));

	static HydrologySnapshot Schema(IEnumerable<int> basinIdOf = null, IEnumerable<int> lakeOfBasin = null,
		IEnumerable<float> spill = null, IEnumerable<double> areaKm2 = null)
	{
		return new HydrologySnapshot(Rivers(), Topology(), Lines(), Basins(),
			new LakesSnapshot(1, new[] { 0, 0, -1 }, basinIdOf ?? new[] { 1 }, lakeOfBasin ?? new[] { -1, 0 },
				new[] { 5f }, areaKm2 ?? new[] { 1d }, new[] { 1d }, spill ?? new[] { float.NaN }, new[] { -1 }),
			new WaterSystemSnapshot(1, new[] { 2 }, new[] { -1 }, new[] { -1 }, new[] { -1 },
				new[] { -1, -1, -1 }, new[] { -1 }, 0, 0, 0, 0));
	}

	static RiversSnapshot Rivers(IEnumerable<int> downstream = null, IEnumerable<int> flowAccum = null) =>
		new(downstream ?? new[] { -1, 0, -1 }, flowAccum ?? new[] { 1, 2, 1 }, new[] { 0d, 0d, 0d },
			new[] { false, true, false }, 1, new[] { 0f, 0f, 0f }, new[] { 0d, 0d, 0d }, 0f, 0d,
			new HydrologyRoutingSnapshot(false, new[] { 0f, 1f, 2f }, new[] { 0f, 0f, 0f }, 0, 0d, 0));

	static RiverTopologySnapshot Topology(IEnumerable<int> downstreamRiver = null) =>
		new(downstreamRiver ?? new[] { -1, 0, -1 }, new[] { 0, 0, 0 }, new[] { -1, 0, -1 },
			new[] { 0f, 0f, 0f }, 1, 1, 0, 1, new[] { 1 }, new[] { 1 });

	static RiverLinesSnapshot Lines(IEnumerable<int> offsets = null, IEnumerable<float> points = null,
		IEnumerable<int> lineOutlet = null) =>
		new(4, offsets ?? new[] { 0, 1 }, new[] { 1 }, points ?? new[] { 0f, 0f, 1f }, lineOutlet ?? new[] { 1 });

	static BasinsSnapshot Basins() => new(new[] { 0, 0, 0 }, new[] { 0, 0, 0 }, new[] { true, true, true },
		1, new[] { 0 }, new[] { 0 }, new[] { 3 }, new[] { 3d }, new[] { 0f }, 1, 0);
}