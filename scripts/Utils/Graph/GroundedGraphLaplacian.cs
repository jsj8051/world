using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace World.Utils;

/// <summary>Sparse LDLᵀ factorization of a weighted graph Laplacian with the last vertex grounded.</summary>
internal sealed class GroundedGraphLaplacian
{
	readonly record struct Entry(int Vertex, double Value);
	readonly record struct Column(int Vertex, double Diagonal, Entry[] Entries);
	readonly Column[] _eliminated;
	readonly Column[] _interface;
	readonly int[] _reducedVertices;
	readonly int _vertexCount;
	internal int FactorEntryCount => _eliminated.Concat(_interface).Sum(column => 1 + column.Entries.Length);
	internal ReadOnlySpan<int> ReducedVertices => _reducedVertices;

	internal GroundedGraphLaplacian(int vertexCount, IEnumerable<(int Left, int Right, double Weight)> edges,
		bool[] retained = null)
	{
		if (vertexCount < 1) throw new ArgumentOutOfRangeException(nameof(vertexCount));
		ArgumentNullException.ThrowIfNull(edges);
		_vertexCount = vertexCount;
		int size = vertexCount - 1;
		if (retained != null && retained.Length != vertexCount) throw new ArgumentException("Graph vertex mask length mismatch.", nameof(retained));
		var columns = size <= 64 ? FactorSmall(size, edges, retained) : FactorSparse(size, edges, retained);
		if (retained == null)
		{
			_eliminated = columns;
			_interface = Array.Empty<Column>();
			_reducedVertices = Array.Empty<int>();
			return;
		}
		// A right-hand side on the interface can only reach its successors in the factor.
		// Keep that closure for the repeated solves, preserving the sparse elimination order.
		var reachable = (bool[])retained.Clone();
		foreach (var column in columns)
			if (reachable[column.Vertex])
				foreach (var entry in column.Entries) reachable[entry.Vertex] = true;
		_eliminated = columns.Where(column => !reachable[column.Vertex]).ToArray();
		_interface = columns.Where(column => reachable[column.Vertex]).ToArray();
		_reducedVertices = _interface.Select(column => column.Vertex).ToArray();
	}

	// A one-word adjacency set makes the minimum-degree selection and fill-in cheap on small blocks.
	static Column[] FactorSmall(int size, IEnumerable<(int Left, int Right, double Weight)> edges, bool[] retained)
	{
		Span<ulong> neighbors = stackalloc ulong[size];
		Span<double> matrix = stackalloc double[size * size];
		neighbors.Clear();
		matrix.Clear();
		foreach (var (left, right, weight) in edges)
		{
			ValidateEdge(size, left, right, weight);
			if (left < size) matrix[left * size + left] += weight;
			if (right < size) matrix[right * size + right] += weight;
			if (left == size || right == size) continue;
			matrix[left * size + right] -= weight;
			matrix[right * size + left] -= weight;
			neighbors[left] |= 1UL << right;
			neighbors[right] |= 1UL << left;
		}
		var columns = new Column[size];
		ulong remaining = size == 64 ? ulong.MaxValue : (1UL << size) - 1;
		for (int step = 0; step < size; step++)
		{
			int pivot = -1, degree = int.MaxValue;
			for (ulong vertices = remaining; vertices != 0; vertices &= vertices - 1)
			{
				int vertex = BitOperations.TrailingZeroCount(vertices);
				int candidateDegree = BitOperations.PopCount(neighbors[vertex]);
				if (!PreferPivot(vertex, candidateDegree, pivot, degree, retained)) continue;
				(pivot, degree) = (vertex, candidateDegree);
			}
			double diagonal = RequirePositivePivot(matrix[pivot * size + pivot]);
			ulong adjacent = neighbors[pivot];
			ulong pivotBit = 1UL << pivot;
			remaining &= ~pivotBit;
			var entries = new Entry[degree];
			int next = 0;
			for (ulong vertices = adjacent; vertices != 0; vertices &= vertices - 1)
			{
				int vertex = BitOperations.TrailingZeroCount(vertices);
				entries[next++] = new Entry(vertex, matrix[pivot * size + vertex] / diagonal);
				neighbors[vertex] = (neighbors[vertex] | adjacent) & ~(pivotBit | (1UL << vertex));
			}
			columns[step] = new Column(pivot, diagonal, entries);
			// Symmetric rank-one Schur update on the remaining neighbors of this pivot.
			for (int i = 0; i < entries.Length; i++)
			{
				int left = entries[i].Vertex;
				double pivotWeight = matrix[pivot * size + left];
				for (int j = i; j < entries.Length; j++)
				{
					int right = entries[j].Vertex;
					double updated = matrix[left * size + right] - pivotWeight * entries[j].Value;
					matrix[left * size + right] = matrix[right * size + left] = updated;
				}
			}
		}
		return columns;
	}

	static Column[] FactorSparse(int size, IEnumerable<(int Left, int Right, double Weight)> edges, bool[] retained)
	{
		var rows = Enumerable.Range(0, size).Select(_ => new Dictionary<int, double>()).ToArray();
		var diagonal = new double[size];
		foreach (var (left, right, weight) in edges)
		{
			ValidateEdge(size, left, right, weight);
			if (left < size) diagonal[left] += weight;
			if (right < size) diagonal[right] += weight;
			if (left == size || right == size) continue;
			rows[left][right] = rows[left].GetValueOrDefault(right) - weight;
			rows[right][left] = rows[right].GetValueOrDefault(left) - weight;
		}
		var columns = new Column[size];
		var eliminated = new bool[size];
		for (int step = 0; step < size; step++)
		{
			int pivot = -1, degree = int.MaxValue;
			for (int vertex = 0; vertex < size; vertex++)
				if (!eliminated[vertex] && PreferPivot(vertex, rows[vertex].Count, pivot, degree, retained))
					(pivot, degree) = (vertex, rows[vertex].Count);
			double value = RequirePositivePivot(diagonal[pivot]);
			var entries = rows[pivot].OrderBy(entry => entry.Key)
				.Select(entry => new Entry(entry.Key, entry.Value / value)).ToArray();
			columns[step] = new Column(pivot, value, entries);
			eliminated[pivot] = true;
			// Eliminating a vertex creates a clique among its remaining neighbors (the Schur complement).
			for (int i = 0; i < entries.Length; i++)
			{
				var first = entries[i];
				double firstWeighted = first.Value * value;
				diagonal[first.Vertex] -= firstWeighted * first.Value;
				rows[first.Vertex].Remove(pivot);
				for (int j = i + 1; j < entries.Length; j++)
				{
					var second = entries[j];
					double updated = rows[first.Vertex].GetValueOrDefault(second.Vertex) - firstWeighted * second.Value;
					rows[first.Vertex][second.Vertex] = updated;
					rows[second.Vertex][first.Vertex] = updated;
				}
			}
			rows[pivot].Clear();
		}
		return columns;
	}

	static bool PreferPivot(int vertex, int degree, int pivot, int bestDegree, bool[] retained)
		=> degree < bestDegree || degree == bestDegree && retained != null && !retained[vertex] && retained[pivot];

	static void ValidateEdge(int ground, int left, int right, double weight)
	{
		if ((uint)left > (uint)ground || (uint)right > (uint)ground || left == right
			|| !(weight > 0) || !double.IsFinite(weight)) throw new ArgumentException("Invalid weighted graph edge.", "edges");
	}

	static double RequirePositivePivot(double value) => value > 0 && double.IsFinite(value) ? value
		: throw new ArgumentException("Graph must be connected to its ground.", "edges");

	internal void Solve(ReadOnlySpan<double> rhs, Span<double> solution)
	{
		if (rhs.Length != _vertexCount || solution.Length != _vertexCount) throw new ArgumentException("Graph vector length mismatch.");
		rhs.CopyTo(solution);
		Reduce(solution);
		SolveReduced(solution);
		RecoverEliminated(solution);
	}

	// Keep the forward-substituted values at the eliminated vertices for back substitution.
	internal void Reduce(Span<double> state)
	{
		state[_vertexCount - 1] = 0;
		Forward(state, _eliminated);
	}

	// The trailing LDLᵀ factor solves the Schur complement on the retained vertices.
	internal void SolveReduced(Span<double> state)
	{
		state[_vertexCount - 1] = 0;
		Forward(state, _interface);
		Backward(state, _interface);
	}

	internal void RecoverEliminated(Span<double> state) => Backward(state, _eliminated);

	static void Forward(Span<double> state, Column[] columns)
	{
		foreach (var column in columns)
		{
			double value = state[column.Vertex];
			foreach (var entry in column.Entries) state[entry.Vertex] -= entry.Value * value;
		}
	}

	static void Backward(Span<double> state, Column[] columns)
	{
		for (int i = columns.Length - 1; i >= 0; i--)
		{
			var column = columns[i];
			double value = state[column.Vertex] / column.Diagonal;
			foreach (var entry in column.Entries) value -= entry.Value * state[entry.Vertex];
			state[column.Vertex] = value;
		}
	}
}
