using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.H3Grid;
using World.Utils;
using World.Utils.H3;

namespace World.Tests;

static class DiscreteRotationAudit
{
	readonly record struct RotationCase(string Name, SpherePointD Axis, double Angle);

	internal static void Run(int resolution)
	{
		var ball = new Ball(resolution, 1f);
		int count = ball.CellIds.Length;
		int[] pentagonDistance = DistancesFromPentagons(ball);
		int[] baseBoundaryDistance = DistancesFromBaseCellBoundaries(ball);
		var cases = new[]
		{
			new RotationCase("small-y", new SpherePointD(0, 1, 0), .01),
			new RotationCase("oblique-small", ObliqueAxis(), .08),
			new RotationCase("oblique-medium", ObliqueAxis(), .35),
			new RotationCase("oblique-large", ObliqueAxis(), 1.1),
		};

		Console.WriteLine($"Discrete rotation audit: res{resolution}, {count} cells, 12 pentagons");
		foreach (var rotationCase in cases)
		{
			var rotation = RotationOps.FromAxisAngle(rotationCase.Axis, rotationCase.Angle);
			var occupancy = new int[count];
			foreach (int source in Enumerable.Range(0, count))
			{
				Vector3 direction = ball.CellDirs[source];
				var moved = RotationOps.Apply(rotation, direction.X, direction.Y, direction.Z);
				var latLng = new LatLng(Math.Atan2(moved.Y, Math.Sqrt(moved.X * moved.X + moved.Z * moved.Z)),
					Math.Atan2(moved.Z, moved.X));
				int target = ball.CellIndexOf(H3.LatLngToCell(latLng, resolution));
				occupancy[target]++;
			}

			var defects = new bool[count];
			int excess = 0, gaps = 0, overloaded = 0;
			for (int cell = 0; cell < count; cell++)
			{
				defects[cell] = occupancy[cell] != 1;
				if (occupancy[cell] == 0) gaps++;
				if (occupancy[cell] > 1)
				{
					overloaded++;
					excess += occupancy[cell] - 1;
				}
			}

			var components = DefectComponents(ball, defects);
			int defectCount = defects.Count(value => value);
			Console.WriteLine($"  {rotationCase.Name}: gaps={gaps}, overloaded={overloaded}, excess={excess}, "
				+ $"defectCells={defectCount}, components={components.Count}, largest={components.DefaultIfEmpty().Max()}");
			foreach (int radius in new[] { 0, 1, 2, 3, 5, 10, 20 })
			{
				int nearPentagon = 0, nearBaseBoundary = 0;
				for (int cell = 0; cell < count; cell++)
				{
					if (!defects[cell]) continue;
					if (pentagonDistance[cell] <= radius) nearPentagon++;
					if (baseBoundaryDistance[cell] <= radius) nearBaseBoundary++;
				}
				Console.WriteLine($"    within pentagon distance {radius}: {nearPentagon}/{defectCount} "
					+ $"({(defectCount == 0 ? 0 : 100d * nearPentagon / defectCount):F1}%)");
				Console.WriteLine($"    within base-cell boundary distance {radius}: {nearBaseBoundary}/{defectCount} "
					+ $"({(defectCount == 0 ? 0 : 100d * nearBaseBoundary / defectCount):F1}%)");
			}
		}
	}

	static SpherePointD ObliqueAxis()
	{
		double length = Math.Sqrt(14);
		return new SpherePointD(1 / length, 2 / length, 3 / length);
	}
	static int[] DistancesFromPentagons(Ball ball)
	{
		var distances = new int[ball.CellIds.Length];
		Array.Fill(distances, -1);
		var queue = new Queue<int>();
		foreach (ulong pentagon in H3.GetPentagons(ball.Res))
		{
			int cell = ball.CellIndexOf(pentagon);
			distances[cell] = 0;
			queue.Enqueue(cell);
		}
		while (queue.TryDequeue(out int cell))
			foreach (int neighbor in ball.CellNeighbors[cell])
				if (distances[neighbor] < 0)
				{
					distances[neighbor] = distances[cell] + 1;
					queue.Enqueue(neighbor);
				}
		return distances;
	}

	static int[] DistancesFromBaseCellBoundaries(Ball ball)
	{
		int count = ball.CellIds.Length;
		var baseCells = ball.CellIds.Select(H3.GetBaseCellNumber).ToArray();
		var distances = new int[count];
		Array.Fill(distances, -1);
		var queue = new Queue<int>();
		for (int cell = 0; cell < count; cell++)
			foreach (int neighbor in ball.CellNeighbors[cell])
				if (baseCells[cell] != baseCells[neighbor])
				{
					distances[cell] = 0;
					queue.Enqueue(cell);
					break;
				}
		while (queue.TryDequeue(out int cell))
			foreach (int neighbor in ball.CellNeighbors[cell])
				if (distances[neighbor] < 0)
				{
					distances[neighbor] = distances[cell] + 1;
					queue.Enqueue(neighbor);
				}
		return distances;
	}

	static List<int> DefectComponents(Ball ball, bool[] defects)
	{
		var seen = new bool[defects.Length];
		var sizes = new List<int>();
		var queue = new Queue<int>();
		for (int seed = 0; seed < defects.Length; seed++)
		{
			if (!defects[seed] || seen[seed]) continue;
			seen[seed] = true;
			queue.Enqueue(seed);
			int size = 0;
			while (queue.TryDequeue(out int cell))
			{
				size++;
				foreach (int neighbor in ball.CellNeighbors[cell])
					if (defects[neighbor] && !seen[neighbor])
					{
						seen[neighbor] = true;
						queue.Enqueue(neighbor);
					}
			}
			sizes.Add(size);
		}
		return sizes;
	}
}
