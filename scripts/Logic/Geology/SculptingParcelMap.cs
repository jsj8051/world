using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>One world-wide assignment of old parcels, collision losses and newborn surface.</summary>
internal sealed class SculptingParcelMap
{
	internal const int Consumed = -1;
	const int Unassigned = -2;
	internal readonly record struct CollisionLoss(int Source, int CollisionCell);
	readonly record struct LossSlot(int Cell, SculptingCoverage.Opponent Opponent);

	internal IReadOnlyList<int> SourceOfCell { get; }
	internal IReadOnlyList<int> TargetOfSource { get; }
	internal IReadOnlyList<CollisionLoss> Losses { get; }

	SculptingParcelMap(int[] sources, int[] targets, CollisionLoss[] losses)
	{
		SourceOfCell = Array.AsReadOnly(sources);
		TargetOfSource = Array.AsReadOnly(targets);
		Losses = Array.AsReadOnly(losses);
	}

	internal static SculptingParcelMap Identity(int count)
	{
		int[] identity = Enumerable.Range(0, count).ToArray();
		return new SculptingParcelMap(identity, identity, Array.Empty<CollisionLoss>());
	}

	internal static SculptingParcelMap Build(Ball ball, SculptingSurfaceState surface, SculptingCoverage coverage,
		SculptingCrustContact[] contacts, CancellationToken cancellation)
	{
		int size = surface.CellCount;
		long profile = WorldBuildProfile.Start();
		int pool = 2 * size;
		int firstLoss = pool + 1;
		var starts = new int[size + 1];
		var slots = new List<LossSlot>();
		for (int cell = 0; cell < size; cell++)
		{
			starts[cell] = slots.Count;
			if (coverage.Collisions[cell] is { } collision)
				foreach (var opponent in collision.Opponents) slots.Add(new LossSlot(cell, opponent));
		}
		starts[size] = slots.Count;

		int entrance = firstLoss + slots.Count, exit = entrance + 1;
		int initialRadius = ball.Res >= 4 ? 2 : 1;
		var flow = new MinimumCostFlow(exit + 1, size * (initialRadius == 2 ? 22 : 10) + slots.Count * 4);
		var input = new int[size];
		var output = new int[size];
		const int collisionCredit = 180;
		for (int cell = 0; cell < size; cell++)
		{
			input[cell] = flow.Add(entrance, cell, 1, 0);
			output[cell] = flow.Add(size + cell, exit, 1, 0);
			if (coverage.Openings[cell].HasValue)
				flow.Add(pool, size + cell, 1, 14 * (7 - coverage.BareSamples[cell]));
		}
		for (int index = 0; index < slots.Count; index++)
			flow.Add(firstLoss + index, pool, 1, -collisionCredit);

		const int maximumRadius = 16;
		var seen = new int[size];
		var candidates = new int[1 + 3 * maximumRadius * (maximumRadius + 1)];
		var costs = new int[candidates.Length];
		float spacingSquared = coverage.Spacing * coverage.Spacing;
		int maximumCost = collisionCredit;
		int assigned = 0;
		bool[] cut = null;
		for (int radius = initialRadius; radius <= maximumRadius; radius++)
		{
			int startRing = radius == initialRadius ? 0 : radius;
			AddCandidates(startRing, radius, cut);
			assigned += flow.Maximize(entrance, exit, cancellation);
			if (profile != 0) WorldBuildProfile.Mark(ref profile, $"sculpting parcels radius {radius} ({size - assigned} pending)");
			if (assigned == size) break;
			// Wider alternatives only need to cross the residual cut blocking the world-wide assignment.
			if (radius >= 3) cut = flow.Reachable(entrance, cancellation);
		}
		if (assigned != size)
		{
			int blocked = Array.FindIndex(input, arc => flow.Flow(arc) == 0);
			throw new InvalidOperationException($"全图板块搬运有 {size - assigned} 个旧格缺少合法去向，首个旧格为 {ball.CellIds[blocked]:x}。");
		}
		foreach (int arc in input) flow.Disable(arc);
		foreach (int arc in output) flow.Disable(arc);
		if (ball.Res >= 4) RestrictRefinement();
		flow.UnlinkDisabled();
		// Production keeps the feasible flow directly; lower resolutions refine its travel costs.
		if (ball.Res < 4) flow.Minimize(maximumCost, 1, cancellation);
		WorldBuildProfile.Mark(ref profile, "sculpting parcels cost refinement");

		var sources = new int[size];
		var targets = new int[size];
		Array.Fill(sources, Unassigned);
		Array.Fill(targets, Unassigned);
		var losses = new List<CollisionLoss>();
		for (int source = 0; source < size; source++)
		for (int arc = flow.FirstArc(source); arc >= 0; arc = flow.NextArc(arc))
		{
			if (flow.Flow(arc) == 0) continue;
			int end = flow.End(arc);
			if (end >= firstLoss)
			{
				targets[source] = Consumed;
				losses.Add(new CollisionLoss(source, slots[end - firstLoss].Cell));
			}
			else
			{
				int cell = end - size;
				if (sources[cell] != Unassigned) throw new InvalidOperationException("目标地表重复分配。");
				targets[source] = cell;
				sources[cell] = source;
			}
			break;
		}
		int gaps = 0;
		for (int cell = 0; cell < size; cell++)
		{
			if (targets[cell] == Unassigned) throw new InvalidOperationException("旧地表没有合法去向。");
			if (sources[cell] >= 0) continue;
			if (!coverage.Openings[cell].HasValue) throw new InvalidOperationException("新生地表缺少张裂依据。");
			gaps++;
		}
		if (gaps != losses.Count || losses.Any(loss => sources[loss.CollisionCell] < 0))
			throw new InvalidOperationException("板块搬运的消散、碰撞及新生格数不守恒。");

		foreach (var loss in losses)
		{
			var winner = surface.CrustTypes[sources[loss.CollisionCell]];
			var loser = surface.CrustTypes[loss.Source];
			var contact = winner != loser ? SculptingCrustContact.ContinentalMargin
				: winner == CrustType.Continental ? SculptingCrustContact.ContinentalCollision
					: SculptingCrustContact.OceanicCollision;
			if (contacts[loss.CollisionCell] != SculptingCrustContact.ContinentalCollision)
				contacts[loss.CollisionCell] = contact;
		}
		return new SculptingParcelMap(sources, targets, losses.ToArray());

		void RestrictRefinement()
		{
			// Keep every feasible assignment and the cheapest alternatives for a sparse cost refinement.
			var alternatives = new int[candidates.Length];
			for (int source = 0; source < size; source++)
			{
				if ((source & 1023) == 0) cancellation.ThrowIfCancellationRequested();
				int count = 0;
				for (int arc = flow.FirstArc(source); arc >= 0; arc = flow.NextArc(arc))
				{
					int end = flow.End(arc);
					if (end < size || end >= pool || flow.Flow(arc) > 0) continue;
					alternatives[count] = arc;
					costs[count++] = flow.Cost(arc);
				}
				const int refineChoices = 2;
				if (count <= refineChoices) continue;
				Array.Sort(costs, alternatives, 0, count);
				for (int i = refineChoices; i < count; i++) flow.Disable(alternatives[i]);
			}
		}

		void AddCandidates(int minimumRing, int radius, bool[] reachable)
		{
			Array.Clear(seen);
			for (int source = 0; source < size; source++)
			{
				if ((source & 1023) == 0) cancellation.ThrowIfCancellationRequested();
				if (reachable != null && !reachable[source]) continue;
				int stamp = source + 1;
				var (ideal, idealDirection) = coverage.ProjectSource(source);
				candidates[0] = ideal;
				seen[ideal] = stamp;
				int length = 1, from = 0, first = 0;
				for (int ring = 1; ring <= radius; ring++)
				{
					int until = length;
					if (ring == minimumRing) first = length;
					for (; from < until; from++)
					foreach (int neighbor in ball.CellNeighbors[candidates[from]])
					{
						if (seen[neighbor] == stamp) continue;
						seen[neighbor] = stamp;
						candidates[length++] = neighbor;
					}
				}

				int owner = surface.PlateOfCell[source];
				var crust = surface.CrustTypes[source];
				int options = first;
				for (int i = first; i < length; i++)
				{
					int cell = candidates[i];
					float distance = idealDirection.DistanceSquaredTo(ball.CellDirs[cell]) / spacingSquared;
					int travel = (int)MathF.Round(50 * distance);
					var collision = coverage.Collisions[cell];
					if (distance <= 2.25f || distance <= 6.25f && collision != null
						&& coverage.Collisions[ideal] is { } idealCollision && idealCollision.Winner == collision.Winner
						&& Array.Exists(idealCollision.Opponents, opponent => opponent.Plate == owner && opponent.Crust == crust))
						for (int index = starts[cell]; index < starts[cell + 1]; index++)
						{
							var opponent = slots[index].Opponent;
							if (opponent.Plate == owner && opponent.Crust == crust)
								flow.Add(source, firstLoss + index, 1, travel);
						}
					if (reachable != null && reachable[size + cell]
						|| collision != null && (collision.Winner != owner || collision.Crust != crust)) continue;
					candidates[options] = cell;
					costs[options++] = travel + SurfaceCost(source, cell);
				}
				Array.Sort(costs, candidates, first, options - first);
				int limit = reachable == null ? options : Math.Min(options, first + 4);
				for (int i = limit - 1; i >= first; i--)
				{
					flow.Add(source, size + candidates[i], 1, costs[i]);
					maximumCost = Math.Max(maximumCost, costs[i]);
				}
			}
		}

		int SurfaceCost(int source, int cell)
		{
			int reference = coverage.Reference[cell];
			if (reference < 0) return 140;
			int cost = surface.PlateOfCell[source] != surface.PlateOfCell[reference] ? 125 : 0;
			if (surface.CrustTypes[source] != surface.CrustTypes[reference]) cost += 90;
			float expectedHeight = surface.HeightM[reference];
			float height = surface.HeightM[source];
			if ((height > surface.SeaLevelM) != (expectedHeight > surface.SeaLevelM)) cost += 90;
			return cost + (int)MathF.Min(110, MathF.Abs(height - expectedHeight) / 32);
		}
	}
}
