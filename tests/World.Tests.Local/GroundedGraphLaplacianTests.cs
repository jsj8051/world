using System;
using System.Collections.Generic;
using System.Linq;
using World.Utils;

namespace World.Tests;

static class GroundedGraphLaplacianTests
{
	internal static void MatchesDenseReferenceAndPreservesResidual()
	{
		const int side = 7, count = side * side, n = count - 1;
		var edges = new List<(int Left, int Right, double Weight)>();
		for (int row = 0; row < side; row++)
		for (int col = 0; col < side; col++)
		{
			int cell = row * side + col;
			if (col + 1 < side) edges.Add((cell, cell + 1, 1 + .01 * cell));
			if (row + 1 < side) edges.Add((cell, cell + side, 2 + .02 * cell));
			if (row + 1 < side && col + 1 < side) edges.Add((cell, cell + side + 1, .7));
		}
		var solver = new GroundedGraphLaplacian(count, edges);
		Program.True(solver.FactorEntryCount < n * (n + 1) / 2 / 2, "Minimum-degree factor avoids most dense triangular entries");
		for (int trial = 0; trial < 8; trial++)
		{
			var rhs = Enumerable.Range(0, count).Select(i => Math.Sin(i * .71 + trial)).ToArray();
			var expected = DenseSolve(count, edges, rhs);
			var actual = (double[])rhs.Clone();
			solver.Solve(actual, actual);
			var residual = new double[count];
			foreach (var (left, right, weight) in edges)
			{
				double flux = weight * (actual[left] - actual[right]);
				residual[left] += flux;
				residual[right] -= flux;
			}
			Program.True(actual[count - 1] == 0 && Enumerable.Range(0, n).All(i =>
				Math.Abs(actual[i] - expected[i]) < 1e-10 * Math.Max(1, Math.Abs(expected[i]))
				&& Math.Abs(residual[i] - rhs[i]) < 1e-10), "Sparse solve matches dense reference and local budgets");
		}
		var single = new double[1];
		new GroundedGraphLaplacian(1, Array.Empty<(int, int, double)>()).Solve(new[] { 4d }, single);
		Program.True(single[0] == 0, "A single grounded node needs no factorization");
		bool disconnected = false;
		try { _ = new GroundedGraphLaplacian(3, new[] { (0, 1, 1d) }); }
		catch (ArgumentException) { disconnected = true; }
		Program.True(disconnected, "Disconnected graph is rejected");
	}

	internal static void RepeatedInterfaceSolveMatchesFullSolution()
	{
		foreach (int size in new[] { 9, 65, 70 })
		{
			var edges = Enumerable.Range(0, size - 1).Select(cell => (cell, cell + 1, 1 + .04 * cell)).ToList();
			var retained = new bool[size];
			retained[1] = retained[size / 2] = retained[^1] = true;
			var solver = new GroundedGraphLaplacian(size, edges, retained);
			var rhs = Enumerable.Range(0, size).Select(cell => Math.Cos(cell * .23)).ToArray();
			var state = (double[])rhs.Clone();
			solver.Reduce(state);
			solver.SolveReduced(state);
			for (int pass = 0; pass < 3; pass++)
			{
				var direction = new double[size];
				direction[1] = .3 * (pass + 1);
				direction[size / 2] = -.8;
				direction[^1] = .4;
				for (int cell = 0; cell < size; cell++) rhs[cell] += direction[cell];
				solver.SolveReduced(direction);
				foreach (int cell in solver.ReducedVertices) state[cell] += direction[cell];
			}
			solver.RecoverEliminated(state);
			var expected = DenseSolve(size, edges, rhs);
			for (int cell = 0; cell < size; cell++)
				Program.True(Math.Abs(state[cell] - expected[cell]) < 1e-9 * Math.Max(1, Math.Abs(expected[cell])),
					$"Recovered potential at cell {cell} must match the full solve on {size} vertices.");
		}
	}

	static double[] DenseSolve(int count, List<(int Left, int Right, double Weight)> edges, double[] rhs)
	{
		int n = count - 1;
		var matrix = new double[n, n];
		foreach (var (left, right, weight) in edges)
		{
			if (left < n) matrix[left, left] += weight;
			if (right < n) matrix[right, right] += weight;
			if (left < n && right < n) { matrix[left, right] -= weight; matrix[right, left] -= weight; }
		}
		for (int i = 0; i < n; i++)
		for (int j = 0; j <= i; j++)
		{
			double value = matrix[i, j];
			for (int k = 0; k < j; k++) value -= matrix[i, k] * matrix[j, k];
			matrix[i, j] = i == j ? Math.Sqrt(value) : value / matrix[j, j];
		}
		var solution = new double[count];
		for (int i = 0; i < n; i++)
		{
			double value = rhs[i];
			for (int j = 0; j < i; j++) value -= matrix[i, j] * solution[j];
			solution[i] = value / matrix[i, i];
		}
		for (int i = n - 1; i >= 0; i--)
		{
			double value = solution[i];
			for (int j = i + 1; j < n; j++) value -= matrix[j, i] * solution[j];
			solution[i] = value / matrix[i, i];
		}
		return solution;
	}
}
