using System;
using World.Data.Maps;

namespace World.Tests;

static class MapClimateTests
{
	public static void RejectsWrongLengths()
	{
		ClimateSnapshot climate = Create(cellCount: 1, monthlyPrecipitation: Array.Empty<float>());
		Program.Throws(() => climate.Validate(1), "monthly precipitation length must match 12*N");
	}

	public static void RejectsOutOfRangeValues()
	{
		ClimateSnapshot climate = Create(cellCount: 1, monsoon: new[] { 1.01f });
		Program.Throws(() => climate.Validate(1), "monsoon strength must be in [0,1]");
	}

	public static void RejectsInvalidVectors()
	{
		ClimateSnapshot climate = Create(cellCount: 1, currentDirection: new[] { 2f, 0f, 0f });
		Program.Throws(() => climate.Validate(1), "current direction must be zero or unit length");
	}

	public static void ClonesBuffersAndPreservesFloatBits()
	{
		float payload = BitConverter.Int32BitsToSingle(unchecked((int)0x3f123456));
		float[] precipitation = { payload };
		ClimateSnapshot climate = Create(cellCount: 1, precipitation: precipitation);
		precipitation[0] = 99;
		Program.True(BitConverter.SingleToInt32Bits(climate.PrecipitationMm[0]) == unchecked((int)0x3f123456),
			"snapshot must clone buffers without changing float bits");
	}

	static ClimateSnapshot Create(int cellCount, float[] monthlyPrecipitation = null,
		float[] monsoon = null, float[] currentDirection = null, float[] precipitation = null)
	{
		int months = 12;
		return new ClimateSnapshot(
			precipitation ?? new float[cellCount], monthlyPrecipitation ?? new float[months * cellCount],
			new float[cellCount], new float[months * cellCount], new float[cellCount], new float[cellCount],
			Ones(cellCount), Ones(months * cellCount), new float[cellCount], new int[cellCount],
			monsoon ?? new float[cellCount], Wind(WindKind.Surface, cellCount, months),
			Wind(WindKind.Low, cellCount, months), Wind(WindKind.Free, cellCount, months),
			currentDirection ?? new float[cellCount * 3], new float[cellCount], new float[cellCount]);
	}

	static WindSetSnapshot Wind(WindKind kind, int cells, int months)
		=> new(kind, new float[months * cells * 3], new float[cells * 3], new float[cells]);

	static float[] Ones(int count)
	{
		var values = new float[count];
		Array.Fill(values, 1f);
		return values;
	}
}
