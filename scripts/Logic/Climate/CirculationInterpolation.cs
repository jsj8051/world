using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace World.Logic;

internal static class CirculationInterpolation
{
	internal readonly record struct InterpolationWeight(int Cell, double Weight);

	internal static InterpolationWeight[][] BuildInterpolation(ClimateGrid fine, ClimateGrid coarse, int[] parent,
		CancellationToken cancellation = default)
	{
		var candidates = coarse.Neighbors.Select((neighbors, i) => neighbors.Append(i)
			.Concat(neighbors.SelectMany(neighbor => coarse.Neighbors[neighbor])).Distinct().OrderBy(cell => cell).ToArray()).ToArray();
		float support = 1.35f * (float)coarse.NeighborKm;
		var result = new InterpolationWeight[fine.Count][];
		for (int i = 0; i < fine.Count; i++)
		{
			if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
			var row = new List<InterpolationWeight>();
			double total = 0;
			foreach (int cell in candidates[parent[i]])
			{
				float distance = CoarseLayer.GreatCircleKm(fine.Dirs[i], coarse.Dirs[cell], fine.RadiusKm);
				if (distance >= support) continue;
				double fraction = distance / support;
				double taper = 1 - fraction * fraction;
				double weight = taper * taper;
				row.Add(new InterpolationWeight(cell, weight));
				total += weight;
			}
			result[i] = total > 0
				? row.Select(entry => new InterpolationWeight(entry.Cell, entry.Weight / total)).ToArray()
				: new[] { new InterpolationWeight(parent[i], 1) };
		}
		return result;
	}
}
