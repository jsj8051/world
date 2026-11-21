using System;
using System.Collections.Generic;
using System.Linq;
using World.H3Grid;
using World.Logic;

namespace World.Tests;

static class HydrologyRoutingTests
{
	internal static void AccumulationRetainsHeightOrderedSums()
	{
		var ball = new Ball(1, 1f);
		int count = ball.CellIds.Length;
		var random = new Random(381);
		var weight = Enumerable.Range(0, count).Select(i => i % 7 == 0 ? 1e8f : 1f + random.NextSingle()).ToArray();
		var elevation = Enumerable.Range(0, count).Select(_ => (float)random.Next(-4, 12)).ToArray();
		Compare(elevation, weight);
		Compare(elevation, null);

		// Equal-height tributaries with very different weights expose floating-point order changes.
		Array.Fill(elevation, 10f);
		elevation[0] = 1;
		foreach (int neighbor in ball.CellNeighbors[0]) elevation[neighbor] = 2;
		Compare(elevation, weight);
		elevation[0] = float.NaN;
		Compare(elevation, weight);

		void Compare(float[] height, float[] runoff)
		{
			var hydro = new H3Hydrology();
			hydro.Generate(ball, height, 0f, runoff);
			var expectedFlow = Enumerable.Repeat(1, count).ToArray();
			var expectedRunoff = runoff == null ? Enumerable.Repeat(1f, count).ToArray() : (float[])runoff.Clone();
			foreach (int cell in Enumerable.Range(0, count).OrderByDescending(i => height[i]).ThenBy(i => i))
			{
				int target = hydro.Downstream[cell];
				if (target < 0 || hydro.IsOcean[target]) continue;
				expectedFlow[target] += expectedFlow[cell];
				expectedRunoff[target] += expectedRunoff[cell];
			}
			Program.True(hydro.FlowAccum.AsSpan().SequenceEqual(expectedFlow), "Tributary counts must match the old height order.");
			for (int cell = 0; cell < count; cell++)
				Program.True(BitConverter.SingleToInt32Bits(expectedRunoff[cell]) == BitConverter.SingleToInt32Bits(hydro.WeightedAccum[cell]),
					$"Runoff must retain its original floating-point sum at cell {cell}.");
		}
	}

	internal static void CoastalFloodMatchesAllOceanSeeds()
	{
		var ball = new Ball(1, 1f);
		int count = ball.CellIds.Length;
		var random = new Random(793);
		var routing = new HydrologyRoutingSurface();
		for (int scenario = 0; scenario < 8; scenario++)
		{
			float seaLevel = scenario - 4;
			var elevation = Enumerable.Range(0, count).Select(_ => (float)random.Next(-3, 3)).ToArray();
			var expected = (float[])elevation.Clone();
			var visited = new bool[count];
			var queue = new PriorityQueue<int, float>();
			for (int cell = 0; cell < count; cell++)
				if (expected[cell] <= seaLevel) { visited[cell] = true; queue.Enqueue(cell, expected[cell]); }
			while (queue.TryDequeue(out int cell, out _))
				foreach (int neighbor in ball.CellNeighbors[cell])
				{
					if (visited[neighbor]) continue;
					visited[neighbor] = true;
					if (expected[neighbor] <= expected[cell])
						expected[neighbor] = expected[cell] + HydrologyRoutingSurface.DescentEpsilonM;
					queue.Enqueue(neighbor, expected[neighbor]);
				}
			routing.Generate(ball, elevation, seaLevel, useFill: true);
			Program.True(routing.RoutingHeightM.AsSpan().SequenceEqual(expected),
				$"Coastal flooding must reproduce all-ocean seeding at sea level {seaLevel}.");
		}
	}
}
