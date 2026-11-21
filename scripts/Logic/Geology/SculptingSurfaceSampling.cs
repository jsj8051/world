using System;
using System.Collections.Generic;
using System.Threading;
using World.Data;
using World.H3Grid;

namespace World.Logic;

internal static class SculptingSurfaceSampling
{
	internal static int[] Resolve(Ball ball, SculptingCoverage coverage, SculptingCrustContact[] contacts,
		CancellationToken cancellation)
	{
		var sources = (int[])coverage.Reference.Clone();
		var unclassifiedHoles = new List<int>();
		for (int cell = 0; cell < sources.Length; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			if (coverage.Collisions[cell] is { } impact)
			{
				contacts[cell] = impact.Crust == CrustType.Oceanic ? SculptingCrustContact.OceanicCollision
					: Array.Exists(impact.Opponents, opponent => opponent.Crust == CrustType.Continental)
						? SculptingCrustContact.ContinentalCollision : SculptingCrustContact.ContinentalMargin;
			}
			else if (sources[cell] < 0 && !coverage.Openings[cell].HasValue) unclassifiedHoles.Add(cell);
		}
		if (unclassifiedHoles.Count > 0)
		{
			var nearest = ExtendShore(ball, sources, cancellation);
			foreach (int cell in unclassifiedHoles) sources[cell] = nearest[cell];
		}
		return sources;
	}

	internal static int[] ExtendShore(Ball ball, IReadOnlyList<int> samples, CancellationToken cancellation)
	{
		var sources = new int[samples.Count];
		var frontier = new Queue<int>();
		for (int cell = 0; cell < sources.Length; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			sources[cell] = samples[cell];
			if (sources[cell] >= 0) frontier.Enqueue(cell);
		}
		if (frontier.Count == 0) throw new InvalidOperationException("Rotated surface has no sampled shore.");
		int visits = 0;
		while (frontier.TryDequeue(out int cell))
		{
			if ((visits++ & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			foreach (int neighbor in ball.CellNeighbors[cell])
			{
				if (sources[neighbor] >= 0) continue;
				sources[neighbor] = sources[cell];
				frontier.Enqueue(neighbor);
			}
		}
		return sources;
	}
}
