using System;
using System.Collections.Generic;
using System.Linq;

namespace World.Utils;

/// <summary>Exact sparse Cholesky solve of a weighted graph Laplacian with the last vertex grounded.</summary>
internal sealed class GroundedGraphLaplacian
{
	readonly record struct Entry(int Vertex, double Value);
	readonly record struct Column(int Vertex, double Diagonal, Entry[] Entries);
	readonly Column[] _columns;
	readonly int _vertexCount;
	internal int FactorEntryCount => _columns.Sum(column => 1 + column.Entries.Length);

	internal GroundedGraphLaplacian(int vertexCount, IEnumerable<(int Left, int Right, double Weight)> edges)
	{
		if (vertexCount < 1) throw new ArgumentOutOfRangeException(nameof(vertexCount));
		ArgumentNullException.ThrowIfNull(edges);
		_vertexCount = vertexCount;
		int size = vertexCount - 1;
		var rows = Enumerable.Range(0, size).Select(_ => new Dictionary<int, double>()).ToArray();
		var diagonal = new double[size];
		foreach (var (left, right, weight) in edges)
		{
			if (left < 0 || left >= vertexCount || right < 0 || right >= vertexCount || left == right
				|| !(weight > 0) || !double.IsFinite(weight)) throw new ArgumentException("Invalid weighted graph edge.", nameof(edges));
			if (left < size) diagonal[left] += weight;
			if (right < size) diagonal[right] += weight;
			if (left == size || right == size) continue;
			rows[left][right] = rows[left].GetValueOrDefault(right) - weight;
			rows[right][left] = rows[right].GetValueOrDefault(left) - weight;
		}
		_columns = new Column[size];
		var eliminated = new bool[size];
		for (int step = 0; step < size; step++)
		{
			int pivot = -1;
			for (int vertex = 0; vertex < size; vertex++)
				if (!eliminated[vertex] && (pivot < 0 || rows[vertex].Count < rows[pivot].Count)) pivot = vertex;
			double value = diagonal[pivot];
			if (!(value > 0) || !double.IsFinite(value)) throw new ArgumentException("Graph must be connected to its ground.", nameof(edges));
			double root = Math.Sqrt(value);
			var entries = rows[pivot].OrderBy(entry => entry.Key)
				.Select(entry => new Entry(entry.Key, entry.Value / root)).ToArray();
			_columns[step] = new Column(pivot, root, entries);
			eliminated[pivot] = true;
			// Eliminating a vertex creates a clique among its remaining neighbors (the Schur complement).
			for (int i = 0; i < entries.Length; i++)
			{
				var first = entries[i];
				diagonal[first.Vertex] -= first.Value * first.Value;
				rows[first.Vertex].Remove(pivot);
				for (int j = i + 1; j < entries.Length; j++)
				{
					var second = entries[j];
					double updated = rows[first.Vertex].GetValueOrDefault(second.Vertex) - first.Value * second.Value;
					rows[first.Vertex][second.Vertex] = updated;
					rows[second.Vertex][first.Vertex] = updated;
				}
			}
			rows[pivot].Clear();
		}
	}

	internal void Solve(ReadOnlySpan<double> rhs, Span<double> solution)
	{
		if (rhs.Length != _vertexCount || solution.Length != _vertexCount) throw new ArgumentException("Graph vector length mismatch.");
		rhs.CopyTo(solution);
		solution[_vertexCount - 1] = 0;
		foreach (var column in _columns)
		{
			double value = solution[column.Vertex] / column.Diagonal;
			solution[column.Vertex] = value;
			foreach (var entry in column.Entries) solution[entry.Vertex] -= entry.Value * value;
		}
		for (int i = _columns.Length - 1; i >= 0; i--)
		{
			var column = _columns[i];
			double value = solution[column.Vertex];
			foreach (var entry in column.Entries) value -= entry.Value * solution[entry.Vertex];
			solution[column.Vertex] = value / column.Diagonal;
		}
	}
}
