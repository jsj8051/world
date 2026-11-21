using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using World.H3Grid;
using H3 = World.Utils.H3.H3;

namespace World.Logic;

/// <summary>在板块主连通区的 H3 格边上寻找能切开地壳的低海拔路径。</summary>
internal sealed class PlateRiftSeam
{
	const int TrialDirections = 8;
	const double MinimumFragmentFraction = .25;
	const double ElevationScaleM = 1000;

	internal sealed record Division(HashSet<int> ChildCells, Vector3 Normal);
	readonly record struct CellEdge(int First, int Second);
	readonly record struct Link(int Vertex, CellEdge Cells, double Length, double Cost);
	sealed record Path(List<CellEdge> Edges, double Cost);

	readonly Ball _ball;
	readonly List<int> _component;
	readonly int[] _owners;
	readonly int _parent;
	readonly CancellationToken _cancellation;
	readonly List<Link>[] _links;
	readonly Vector3[] _positions;
	readonly int[] _boundary;
	readonly double _maxElevationWeight;
	readonly double[] _distance;
	readonly Link[] _incoming;
	readonly byte[] _sides;
	double _bestScore = double.PositiveInfinity;
	Division _best;

	internal PlateRiftSeam(Ball ball, List<int> component, int[] owners, float[] heights,
		CancellationToken cancellation)
	{
		_ball = ball;
		_component = component;
		_owners = owners;
		_parent = owners[component[0]];
		_cancellation = cancellation;

		float minimumHeight = component.Min(cell => heights[cell]);
		_maxElevationWeight = 1 + (component.Max(cell => heights[cell]) - (double)minimumHeight) / ElevationScaleM;
		_positions = ball.Vertices.Positions;
		_links = BuildLinks(heights, minimumHeight);
		// H3 的每个角点连着三格。恰有两格属于板块时，角点只有一条内部格边。
		_boundary = Enumerable.Range(0, _links.Length).Where(vertex => _links[vertex]?.Count == 1).ToArray();
		_distance = new double[_links.Length];
		_incoming = new Link[_links.Length];
		_sides = new byte[owners.Length];
	}

	internal Division Find(Vector3 center, Vector3 preferredNormal)
	{
		IReadOnlyList<int> loopEndpoints = _boundary.Length == 0 ? FindLoopEndpoints(center) : Array.Empty<int>();
		SearchCandidates(center, preferredNormal, loopEndpoints, guideWeight: 0);
		if (_best == null)
			SearchCandidates(center, preferredNormal, loopEndpoints, guideWeight: 12 * _maxElevationWeight);
		return _best;
	}

	List<Link>[] BuildLinks(float[] heights, float minimumHeight)
	{
		var corners = new ulong[_owners.Length][];
		var links = new List<Link>[_positions.Length];
		for (int i = 0; i < _component.Count; i++)
		{
			if ((i & 1023) == 0) _cancellation.ThrowIfCancellationRequested();
			int cell = _component[i];
			var cellCorners = corners[cell] ??= H3.CellToVertexes(_ball.CellIds[cell]);
			foreach (int neighbor in _ball.CellNeighbors[cell])
			{
				if (neighbor <= cell || _owners[neighbor] != _parent) continue;
				var neighborCorners = corners[neighbor] ??= H3.CellToVertexes(_ball.CellIds[neighbor]);
				int first = -1, second = -1;
				foreach (ulong corner in cellCorners)
				{
					if (Array.IndexOf(neighborCorners, corner) < 0) continue;
					if (first < 0) first = _ball.VertexIndexOf(corner);
					else second = _ball.VertexIndexOf(corner);
				}
				if (second < 0) throw new InvalidOperationException("H3 相邻格必须共享一条格边");
				var from = _positions[first];
				var to = _positions[second];
				double length = Math.Atan2(from.Cross(to).Length(), from.Dot(to));
				double elevation = ((double)heights[cell] + heights[neighbor]) / 2;
				double cost = length * (1 + (elevation - minimumHeight) / ElevationScaleM);
				var cells = new CellEdge(cell, neighbor);
				(links[first] ??= new List<Link>(3)).Add(new Link(second, cells, length, cost));
				(links[second] ??= new List<Link>(3)).Add(new Link(first, cells, length, cost));
			}
		}
		return links;
	}

	void SearchCandidates(Vector3 center, Vector3 preferredNormal, IReadOnlyList<int> loopEndpoints, double guideWeight)
	{
		for (int direction = 0; direction < TrialDirections; direction++)
		{
			_cancellation.ThrowIfCancellationRequested();
			var normal = preferredNormal.Rotated(center, direction * MathF.PI / TrialDirections);
			var along = center.Cross(normal);
			if (_boundary.Length == 0)
			{
				if (loopEndpoints.Count >= 2) SearchLoop(center, normal, along, loopEndpoints, guideWeight);
				continue;
			}

			float min = _boundary.Min(vertex => _positions[vertex].Dot(normal));
			float max = _boundary.Max(vertex => _positions[vertex].Dot(normal));
			float width = Math.Max(max - min, .001f * _ball.Radius);
			// 中部以及两侧各尝试一组端点，允许裂带在低地处偏离几何中线。
			foreach (float shift in new[] { 0f, -.18f, .18f })
			{
				float offset = (min + max) / 2 + width * shift;
				int start = BoundaryEndpoint(-along, normal, offset);
				int end = BoundaryEndpoint(along, normal, offset);
				if (start == end) continue;
				var path = Trace(start, end, normal, offset, width / 2, guideWeight, Vector3.Zero);
				if (path != null) Consider(path, normal);
			}
		}
	}

	int BoundaryEndpoint(Vector3 along, Vector3 normal, float offset)
		=> _boundary.MaxBy(vertex => (_positions[vertex].Dot(along)
			- 2 * MathF.Abs(_positions[vertex].Dot(normal) - offset), -vertex));

	List<int> FindLoopEndpoints(Vector3 center)
	{
		var endpoints = new List<int>();
		for (int vertex = 0; vertex < _links.Length; vertex++)
		{
			if ((vertex & 4095) == 0) _cancellation.ThrowIfCancellationRequested();
			if (_links[vertex] is not { } links) continue;
			bool forward = false, backward = false;
			foreach (var link in links)
			{
				float projection = _positions[link.Vertex].Dot(center);
				forward |= projection > 0;
				backward |= projection < 0;
			}
			if (forward && backward) endpoints.Add(vertex);
		}
		return endpoints;
	}

	void SearchLoop(Vector3 center, Vector3 normal, Vector3 along, IReadOnlyList<int> endpoints, double guideWeight)
	{
		// 全球板块没有边界。两条半球内的路径只在端点相接，形成一条闭合裂带。
		int start = endpoints.MinBy(vertex => _positions[vertex].Dot(along));
		int end = endpoints.MaxBy(vertex => _positions[vertex].Dot(along));
		if (start == end) return;
		var first = Trace(start, end, normal, 0, _ball.Radius, guideWeight, center);
		if (first == null) return;
		var second = Trace(start, end, normal, 0, _ball.Radius, guideWeight, -center);
		if (second == null) return;
		first.Edges.AddRange(second.Edges);
		Consider(new Path(first.Edges, first.Cost + second.Cost), normal);
	}

	Path Trace(int start, int end, Vector3 normal, float offset, float width, double guideWeight, Vector3 hemisphere)
	{
		Array.Fill(_distance, double.PositiveInfinity);
		var queue = new PriorityQueue<int, (double Cost, int Vertex)>();
		_distance[start] = 0;
		queue.Enqueue(start, (0, start));
		int visited = 0;
		while (queue.TryDequeue(out int vertex, out var priority))
		{
			if ((visited++ & 1023) == 0) _cancellation.ThrowIfCancellationRequested();
			if (priority.Cost != _distance[vertex]) continue;
			if (vertex == end) return ReadPath(start, end);
			foreach (var link in _links[vertex])
			{
				int next = link.Vertex;
				if (next != end && (_links[next].Count == 1
					|| hemisphere != Vector3.Zero && _positions[next].Dot(hemisphere) <= 0)) continue;
				double cost = priority.Cost + link.Cost;
				if (guideWeight > 0)
				{
					double deviation = ((_positions[vertex] + _positions[next]).Dot(normal) / 2 - offset) / width;
					cost += guideWeight * link.Length * deviation * deviation;
				}
				if (cost >= _distance[next]) continue;
				_distance[next] = cost;
				_incoming[next] = link with { Vertex = vertex };
				queue.Enqueue(next, (cost, next));
			}
		}
		return null;
	}

	Path ReadPath(int start, int end)
	{
		var edges = new List<CellEdge>();
		double cost = 0;
		for (int vertex = end; vertex != start; vertex = _incoming[vertex].Vertex)
		{
			var link = _incoming[vertex];
			edges.Add(link.Cells);
			cost += link.Cost;
		}
		return new Path(edges, cost);
	}

	void Consider(Path path, Vector3 normal)
	{
		if (path.Cost >= _bestScore) return;
		var blocked = new HashSet<CellEdge>(path.Edges);
		if (blocked.Count != path.Edges.Count) return;
		Array.Clear(_sides);
		var edge = path.Edges[0];
		int firstCount = Flood(edge.First, 1, blocked, out var firstCenter);
		int secondCount = _component.Count - firstCount;
		if (Math.Min(firstCount, secondCount) < MinimumFragmentFraction * _component.Count || _sides[edge.Second] != 0)
			return;
		if (Flood(edge.Second, 2, blocked, out var secondCenter) != secondCount) return;
		foreach (var segment in path.Edges)
			if (_sides[segment.First] == _sides[segment.Second]) return;

		// 让平坦地形倾向近似平分，同时允许显著低于周围地形的裂带偏离中线。
		double imbalance = (double)(firstCount - secondCount) / _component.Count;
		double score = path.Cost * (1 + 3 * imbalance * imbalance);
		if (score >= _bestScore) return;
		byte child = (firstCenter / firstCount).Dot(normal) > (secondCenter / secondCount).Dot(normal) ? (byte)1 : (byte)2;
		_bestScore = score;
		_best = new Division(_component.Where(cell => _sides[cell] == child).ToHashSet(), normal);
	}

	int Flood(int start, byte side, HashSet<CellEdge> blocked, out Vector3 center)
	{
		int count = 0;
		center = Vector3.Zero;
		var queue = new Queue<int>();
		queue.Enqueue(start);
		_sides[start] = side;
		while (queue.TryDequeue(out int cell))
		{
			if ((count++ & 1023) == 0) _cancellation.ThrowIfCancellationRequested();
			center += _ball.CellDirs[cell];
			foreach (int neighbor in _ball.CellNeighbors[cell])
			{
				if (_owners[neighbor] != _parent || _sides[neighbor] != 0
					|| blocked.Contains(new CellEdge(Math.Min(cell, neighbor), Math.Max(cell, neighbor)))) continue;
				_sides[neighbor] = side;
				queue.Enqueue(neighbor);
			}
		}
		return count;
	}
}
