using System;
using System.Collections.Generic;
using System.Threading;

namespace World.Utils;

/// <summary>Integer cost-scaling flow. The caller supplies an initial feasible flow.</summary>
internal sealed class MinimumCostFlow
{
	struct Arc(int to, int next, int capacity, int cost)
	{
		internal readonly int To = to, Cost = cost;
		internal int Capacity = capacity, Next = next;
	}

	readonly int[] _heads;
	Arc[] _arcs;
	int _count;

	internal MinimumCostFlow(int vertices, int estimatedArcs)
	{
		_heads = new int[vertices];
		Array.Fill(_heads, -1);
		_arcs = new Arc[checked(2 * estimatedArcs)];
	}

	internal int Add(int from, int to, int capacity, int cost)
	{
		if (_arcs.Length - _count < 2) Array.Resize(ref _arcs, Math.Max(16, checked(_count * 3 / 2)));
		int index = _count;
		_arcs[_count++] = new Arc(to, _heads[from], capacity, cost);
		_arcs[_count++] = new Arc(from, _heads[to], 0, -cost);
		_heads[from] = index;
		_heads[to] = index + 1;
		return index;
	}

	internal int Flow(int arc) => _arcs[arc ^ 1].Capacity;

	internal int FirstArc(int vertex) => _heads[vertex];
	internal int NextArc(int arc) => _arcs[arc].Next;
	internal int End(int arc) => _arcs[arc].To;
	internal int Cost(int arc) => _arcs[arc].Cost;
	internal void Disable(int arc)
	{
		_arcs[arc].Capacity = 0;
		_arcs[arc ^ 1].Capacity = 0;
	}

	internal void UnlinkDisabled()
	{
		for (int vertex = 0; vertex < _heads.Length; vertex++)
		{
			int previous = -1;
			for (int arc = _heads[vertex]; arc >= 0; arc = _arcs[arc].Next)
			{
				if (_arcs[arc].Capacity != 0 || _arcs[arc ^ 1].Capacity != 0)
				{
					previous = arc;
					continue;
				}
				if (previous >= 0) _arcs[previous].Next = _arcs[arc].Next;
				else _heads[vertex] = _arcs[arc].Next;
			}
		}
	}

	internal bool[] Reachable(int source, CancellationToken cancellation)
	{
		var visited = new bool[_heads.Length];
		var queue = new Queue<int>();
		visited[source] = true;
		queue.Enqueue(source);
		int visits = 0;
		while (queue.TryDequeue(out int vertex))
		{
			if ((visits++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
			for (int edge = _heads[vertex]; edge >= 0; edge = _arcs[edge].Next)
			{
				ref Arc arc = ref _arcs[edge];
				if (arc.Capacity == 0 || visited[arc.To]) continue;
				visited[arc.To] = true;
				queue.Enqueue(arc.To);
			}
		}
		return visited;
	}

	internal int Maximize(int source, int sink, CancellationToken cancellation)
	{
		int size = _heads.Length;
		int unreachable = 2 * size;
		var height = new int[size];
		var excess = new int[size];
		var next = new int[size];
		var search = new int[size];
		var active = new Queue<int>();
		for (int edge = _heads[source]; edge >= 0; edge = _arcs[edge].Next)
			Push(source, edge, _arcs[edge].Capacity);
		RelabelGlobally();
		int work = 0;
		while (active.TryDequeue(out int vertex))
		{
			while (excess[vertex] > 0)
			{
				if ((work++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
				if (work >= 4 * _count)
				{
					RelabelGlobally();
					work = 0;
					break;
				}
				int edge = next[vertex];
				if (edge < 0)
				{
					int best = unreachable;
					for (int item = _heads[vertex]; item >= 0; item = _arcs[item].Next)
						if (_arcs[item].Capacity > 0) best = Math.Min(best, height[_arcs[item].To]);
					if (best == unreachable) throw new InvalidOperationException("Preflow could not return to its source.");
					height[vertex] = best + 1;
					next[vertex] = _heads[vertex];
					continue;
				}
				ref Arc arc = ref _arcs[edge];
				if (arc.Capacity == 0 || height[vertex] != height[arc.To] + 1)
				{
					next[vertex] = arc.Next;
					continue;
				}
				int other = arc.To;
				int previous = excess[other];
				Push(vertex, edge, Math.Min(excess[vertex], arc.Capacity));
				if (other != source && other != sink && previous == 0) active.Enqueue(other);
			}
		}
		return excess[sink];

		void RelabelGlobally()
		{
			Array.Fill(height, unreachable);
			height[source] = size;
			SearchReverse(sink, 0);
			SearchReverse(source, size);
			active.Clear();
			for (int vertex = 0; vertex < size; vertex++)
			{
				next[vertex] = _heads[vertex];
				if (vertex != source && vertex != sink && excess[vertex] > 0) active.Enqueue(vertex);
			}
		}

		void SearchReverse(int start, int firstHeight)
		{
			height[start] = firstHeight;
			search[0] = start;
			int length = 1;
			for (int i = 0; i < length; i++)
			{
				if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
				int vertex = search[i];
				for (int edge = _heads[vertex]; edge >= 0; edge = _arcs[edge].Next)
				{
					int other = _arcs[edge].To;
					if (_arcs[edge ^ 1].Capacity == 0 || height[other] != unreachable) continue;
					height[other] = height[vertex] + 1;
					search[length++] = other;
				}
			}
		}

		void Push(int from, int edge, int units)
		{
			_arcs[edge].Capacity -= units;
			_arcs[edge ^ 1].Capacity += units;
			excess[from] -= units;
			excess[_arcs[edge].To] += units;
		}
	}

	internal void Minimize(int maximumCost, int tolerance, CancellationToken cancellation)
	{
		int size = _heads.Length;
		// Sub-unit cost precision cannot affect the geometric resolution of the production graph.
		long scale = size < 4096 ? size + 1L : 1;
		long epsilon = maximumCost * scale;
		var price = new long[size];
		var excess = new int[size];
		var next = new int[size];
		var active = new Queue<int>(size);
		var distance = new long[size];
		var frontier = new PriorityQueue<int, long>();
		while (epsilon > tolerance)
		{
			epsilon = Math.Max(tolerance, (epsilon + 7) / 8);
			for (int vertex = 0; vertex < size; vertex++)
			{
				if ((vertex & 1023) == 0) cancellation.ThrowIfCancellationRequested();
				for (int edge = _heads[vertex]; edge >= 0; edge = _arcs[edge].Next)
				{
					ref Arc arc = ref _arcs[edge];
					if (arc.Capacity > 0 && ReducedCost(vertex, arc) < 0) Push(vertex, edge, arc.Capacity);
				}
			}
			for (int vertex = 0; vertex < size; vertex++)
			{
				next[vertex] = _heads[vertex];
				if (excess[vertex] > 0) active.Enqueue(vertex);
			}
			if (size >= 4096 && active.Count > 0) UpdatePrices();

			int iterations = 0, relabels = 0;
			while (active.TryDequeue(out int vertex))
			{
				while (excess[vertex] > 0)
				{
					if ((iterations++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
					int edge = next[vertex];
					if (edge < 0)
					{
						if (++relabels >= size && size >= 4096)
						{
							UpdatePrices();
							relabels = 0;
							continue;
						}
						long best = long.MaxValue;
						for (int item = _heads[vertex]; item >= 0; item = _arcs[item].Next)
							if (_arcs[item].Capacity > 0) best = Math.Min(best, ReducedCost(vertex, _arcs[item]));
						if (best == long.MaxValue) throw new InvalidOperationException("Flow has no feasible residual route.");
						price[vertex] -= best + epsilon;
						next[vertex] = _heads[vertex];
						continue;
					}
					ref Arc arc = ref _arcs[edge];
					if (arc.Capacity == 0 || ReducedCost(vertex, arc) >= 0)
					{
						next[vertex] = arc.Next;
						continue;
					}
					int previous = excess[arc.To];
					Push(vertex, edge, Math.Min(excess[vertex], arc.Capacity));
					if (previous <= 0 && excess[arc.To] > 0) active.Enqueue(arc.To);
				}
			}
		}

		long ReducedCost(int from, in Arc arc) => scale * arc.Cost + price[from] - price[arc.To];

		void UpdatePrices()
		{
			Array.Fill(distance, long.MaxValue);
			frontier.Clear();
			int unresolved = 0;
			for (int vertex = 0; vertex < size; vertex++)
			{
				if (excess[vertex] > 0) unresolved++;
				if (excess[vertex] >= 0) continue;
				distance[vertex] = 0;
				frontier.Enqueue(vertex, 0);
			}
			int visits = 0;
			long limit = 0;
			while (frontier.TryDequeue(out int vertex, out long current))
			{
				if (current != distance[vertex]) continue;
				if ((visits++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
				limit = current;
				if (excess[vertex] > 0 && --unresolved == 0) break;
				for (int edge = _heads[vertex]; edge >= 0; edge = _arcs[edge].Next)
				{
					ref Arc reverse = ref _arcs[edge ^ 1];
					if (reverse.Capacity <= 0) continue;
					int from = _arcs[edge].To;
					long reduced = ReducedCost(from, reverse);
					long weight = reduced < 0 ? 0 : reduced / epsilon + 1;
					long updated = current + weight;
					if (updated >= distance[from]) continue;
					distance[from] = updated;
					frontier.Enqueue(from, updated);
				}
			}
			for (int vertex = 0; vertex < size; vertex++)
			{
				price[vertex] -= Math.Min(distance[vertex], limit) * epsilon;
				next[vertex] = _heads[vertex];
			}
		}

		void Push(int from, int edge, int units)
		{
			_arcs[edge].Capacity -= units;
			_arcs[edge ^ 1].Capacity += units;
			excess[from] -= units;
			excess[_arcs[edge].To] += units;
		}
	}
}
