using System;
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
		// cos is decreasing on [0, pi]. Reject candidates outside the angular support before acos.
		// Leave a small floating-point margin so the original great-circle test decides ties.
		float minimumDot = MathF.Cos(MathF.Min(MathF.PI, support / fine.RadiusKm)) - 1e-6f;
		var result = new InterpolationWeight[fine.Count][];
		for (int i = 0; i < fine.Count; i++)
		{
			if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
			var row = new List<InterpolationWeight>();
			double total = 0;
			foreach (int cell in candidates[parent[i]])
			{
				float dot = fine.Dirs[i].Dot(coarse.Dirs[cell]);
				if (dot < minimumDot) continue;
				float distance = MathF.Acos(Math.Clamp(dot, -1f, 1f)) * fine.RadiusKm;
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
