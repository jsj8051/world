using System;
using System.Collections.Generic;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>将初始地图完全封闭的浅海还原为能排水的内陆低地。</summary>
internal static class InitialInlandSeaOps
{
	internal const float MaximumDepthM = 400f;

	internal static void Fill(Ball ball, float[] heights, IReadOnlyList<CrustType> crust, float seaLevelM)
	{
		var (componentOf, count) = LandMaskOps.Components(ball, cell => heights[cell] <= seaLevelM);
		if (count == 0) return;

		var oceanicCells = new int[count];
		var deepest = new float[count];
		var spill = new float[count];
		Array.Fill(spill, float.PositiveInfinity);
		int ocean = -1;
		for (int cell = 0; cell < heights.Length; cell++)
		{
			int component = componentOf[cell];
			if (component < 0) continue;
			if (crust[cell] == CrustType.Oceanic)
			{
				int oceanic = ++oceanicCells[component];
				if (ocean < 0 || oceanic > oceanicCells[ocean]) ocean = component;
			}
			deepest[component] = Math.Max(deepest[component], seaLevelM - heights[cell]);
			foreach (int neighbor in ball.CellNeighbors[cell])
				if (componentOf[neighbor] < 0)
					spill[component] = Math.Min(spill[component], heights[neighbor]);
		}
		for (int cell = 0; cell < heights.Length; cell++)
		{
			int component = componentOf[cell];
			if (component >= 0 && component != ocean && deepest[component] <= MaximumDepthM
				&& float.IsFinite(spill[component]))
				heights[cell] = spill[component];
		}
	}
}
