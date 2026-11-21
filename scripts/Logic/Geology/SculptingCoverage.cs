using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using World.Data;
using World.H3Grid;
using World.Utils;
using World.Utils.H3;
using Vector3 = Godot.Vector3;

namespace World.Logic;

/// <summary>Pulls the rotated plate footprints back onto common samples of the destination sphere.</summary>
internal sealed class SculptingCoverage
{
	internal readonly record struct Opponent(int Plate, CrustType Crust);
	internal sealed record Collision(int Winner, CrustType Crust, Opponent[] Opponents, float Strength);
	internal readonly record struct Opening(int First, int Second, int FirstDistance, int SecondDistance, float Width);
	readonly record struct Footprint(uint Mask, uint Continental, int Center, int Land, int Ocean);
	readonly record struct Front(int Cell, int Reference, int Distance);

	internal int[] Reference { get; }
	internal byte[] BareSamples { get; }
	internal Collision[] Collisions { get; }
	internal Opening?[] Openings { get; }
	internal int[] IdealCell { get; }
	internal Vector3[] IdealDirection { get; }
	internal float Spacing { get; }

	SculptingCoverage(Ball ball, PlateMovementResult movement, CancellationToken cancellation)
	{
		var surface = movement.Source.Surface;
		int size = surface.CellCount;
		int plateCount = movement.Plates.Count;
		Spacing = (float)(SpatialScale.Of(ball).NeighborCenterKm / SphericalFbmNoise.WorldRadiusKm);
		Reference = new int[size];
		BareSamples = new byte[size];
		Collisions = new Collision[size];
		Openings = new Opening?[size];
		IdealCell = new int[size];
		IdealDirection = new Vector3[size];
		var depths = new int[size];
		var sameCrust = new bool[size];
		var sizes = new int[plateCount];
		var centers = new Vector3[plateCount];
		var caps = new float[plateCount];
		var frontier = new Queue<int>();
		for (int cell = 0; cell < size; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			int plate = surface.PlateOfCell[cell];
			sizes[plate]++;
			centers[plate] += ball.CellDirs[cell];
			depths[cell] = -1;
			sameCrust[cell] = true;
			foreach (int neighbor in ball.CellNeighbors[cell])
			{
				if (surface.CrustTypes[neighbor] != surface.CrustTypes[cell]) sameCrust[cell] = false;
				if (surface.PlateOfCell[neighbor] == plate || depths[cell] == 0) continue;
				depths[cell] = 0;
				frontier.Enqueue(cell);
			}
		}
		while (frontier.TryDequeue(out int cell))
			foreach (int neighbor in ball.CellNeighbors[cell])
			{
				if (depths[neighbor] >= 0) continue;
				depths[neighbor] = depths[cell] + 1;
				frontier.Enqueue(neighbor);
			}

		for (int plate = 0; plate < plateCount; plate++) centers[plate] = centers[plate].Normalized();
		for (int cell = 0; cell < size; cell++)
		{
			int plate = surface.PlateOfCell[cell];
			caps[plate] = Math.Max(caps[plate], ball.CellDirs[cell].DistanceTo(centers[plate]));
		}
		var inverse = movement.InverseSurfaceMoves;
		var forward = new RotationD[plateCount];
		for (int plate = 0; plate < plateCount; plate++)
		{
			forward[plate] = RotationOps.Inverse(inverse[plate]);
			centers[plate] = Rotate(forward[plate], centers[plate]);
			caps[plate] += 2.5f * Spacing;
			caps[plate] *= caps[plate];
		}
		var options = new ParallelOptions { CancellationToken = cancellation };
		Parallel.For(0, size, options, cell =>
		{
			Vector3 center = ball.CellDirs[cell];
			IdealDirection[cell] = Rotate(forward[surface.PlateOfCell[cell]], center);
			IdealCell[cell] = CellAt(ball, IdealDirection[cell]);
			Span<Vector3> probes = stackalloc Vector3[7];
			probes[0] = center;
			int sampleCount = 1;
			foreach (int neighbor in ball.CellNeighbors[cell])
				probes[sampleCount++] = center.Lerp(ball.CellDirs[neighbor], .36f).Normalized();
			uint all = (1u << sampleCount) - 1;
			Span<Footprint> hits = stackalloc Footprint[plateCount];
			hits.Clear();
			uint covered = 0;
			int best = -1, bestScore = 0;
			for (int plate = 0; plate < plateCount; plate++)
			{
				if (sizes[plate] == 0 || center.DistanceSquaredTo(centers[plate]) > caps[plate]) continue;
				int back = CellAt(ball, Rotate(inverse[plate], center));
				bool centerInside = surface.PlateOfCell[back] == plate;
				Footprint hit;
				if (centerInside && depths[back] != 0 && sameCrust[back])
				{
					bool land = surface.CrustTypes[back] == CrustType.Continental;
					hit = new Footprint(all, land ? all : 0, back, land ? back : -1, land ? -1 : back);
				}
				else
				{
					bool near = centerInside;
					foreach (int neighbor in ball.CellNeighbors[back]) near |= surface.PlateOfCell[neighbor] == plate;
					if (!near) continue;
					uint mask = 0, continental = 0;
					int reference = -1, land = -1, ocean = -1;
					for (int index = 0; index < sampleCount; index++)
					{
						int old = index == 0 ? back : CellAt(ball, Rotate(inverse[plate], probes[index]));
						if (surface.PlateOfCell[old] != plate) continue;
						uint bit = 1u << index;
						mask |= bit;
						if (reference < 0) reference = old;
						if (surface.CrustTypes[old] == CrustType.Continental)
						{
							continental |= bit;
							if (land < 0) land = old;
						}
						else if (ocean < 0) ocean = old;
					}
					hit = new Footprint(mask, continental, reference, land, ocean);
				}
				hits[plate] = hit;
				covered |= hit.Mask;
				int score = 2 * BitOperations.PopCount(hit.Mask) + (int)(hit.Mask & 1);
				if (score > bestScore) (best, bestScore) = (hit.Center, score);
			}
			Reference[cell] = best;
			BareSamples[cell] = (byte)BitOperations.PopCount(all & ~covered);
			if (best < 0) return;

			int winner = -1;
			float winnerScore = float.NegativeInfinity;
			bool winnerContinental = false;
			for (int plate = 0; plate < plateCount; plate++)
			{
				var hit = hits[plate];
				if (hit.Mask == 0) continue;
				uint shared = 0;
				for (int other = 0; other < plateCount; other++)
					if (other != plate) shared |= Shared(hit, hits[other]);
				if (shared == 0) continue;
				bool continental = (hit.Continental & shared) != 0;
				int core = Math.Max(0, depths[hit.Center]);
				float score = core + .24f * BitOperations.PopCount(hit.Mask) + .35f * MathF.Log(1 + sizes[plate]);
				if (winner < 0 || continental && !winnerContinental || continental == winnerContinental && score > winnerScore)
					(winner, winnerScore, winnerContinental) = (plate, score, continental);
			}
			if (winner < 0) return;
			var winning = hits[winner];
			var opponents = new List<Opponent>(2);
			float strength = 0;
			for (int plate = 0; plate < plateCount; plate++)
			{
				if (plate == winner) continue;
				var hit = hits[plate];
				uint shared = Shared(winning, hit);
				if (shared == 0) continue;
				bool land = (shared & hit.Continental) != 0;
				opponents.Add(new Opponent(plate, land ? CrustType.Continental : CrustType.Oceanic));
				int winnerSource = winnerContinental ? winning.Land : winning.Ocean;
				int loserSource = land ? hit.Land : hit.Ocean;
				strength = Math.Max(strength, ball.CellDirs[winnerSource].DistanceTo(ball.CellDirs[loserSource]) / Spacing);
			}
			if (opponents.Count == 0) return;
			Reference[cell] = winnerContinental ? winning.Land : winning.Ocean;
			Collisions[cell] = new Collision(winner, winnerContinental ? CrustType.Continental : CrustType.Oceanic,
				opponents.ToArray(), strength);
		});
		FindOpenings(ball, movement, cancellation);
	}

	internal static SculptingCoverage Build(Ball ball, PlateMovementResult movement, CancellationToken cancellation)
		=> new(ball, movement, cancellation);

	static uint Shared(Footprint first, Footprint second)
	{
		uint mask = first.Mask & second.Mask;
		return (mask & 1) != 0 || BitOperations.PopCount(mask) >= 2 ? mask : 0;
	}

	void FindOpenings(Ball ball, PlateMovementResult movement, CancellationToken cancellation)
	{
		var owners = movement.Source.Surface.PlateOfCell;
		var first = new Front[Reference.Length];
		var second = new Front[Reference.Length];
		var queue = new Queue<Front>();
		for (int cell = 0; cell < Reference.Length; cell++)
		{
			if (BareSamples[cell] == 0) continue;
			// Covered samples in a partially empty cell also count as shore.
			if (Reference[cell] >= 0) Visit(cell, Reference[cell], 1);
			foreach (int neighbor in ball.CellNeighbors[cell])
				if (Reference[neighbor] >= 0) Visit(cell, Reference[neighbor], 1);
		}
		while (queue.TryDequeue(out Front marker))
		{
			if ((marker.Cell & 255) == 0) cancellation.ThrowIfCancellationRequested();
			foreach (int neighbor in ball.CellNeighbors[marker.Cell])
				if (BareSamples[neighbor] > 0) Visit(neighbor, marker.Reference, marker.Distance + 1);
		}
		for (int cell = 0; cell < Reference.Length; cell++)
		{
			Front a = first[cell], b = second[cell];
			if (a.Distance == 0 || Collisions[cell] != null) continue;
			bool uncovered = Reference[cell] < 0;
			if (b.Distance == 0)
			{
				if (!uncovered) continue;
				// A narrow rift may expose the former plate even when its moved front is engulfed elsewhere.
				var other = Rotate(movement.InverseSurfaceMoves[owners[a.Reference]], ball.CellDirs[cell]);
				var vacated = Rotate(movement.InverseSurfaceMoves[owners[cell]], ball.CellDirs[cell]);
				float separation = Math.Max(a.Distance, other.DistanceTo(vacated) / Spacing);
				Openings[cell] = new Opening(a.Reference, cell, a.Distance, a.Distance + 1, separation);
				continue;
			}
			int left = IdealCell[a.Reference], right = IdealCell[b.Reference];
			float opening = left == right ? 0 : SculptingBoundaryMotion.NormalMotion(ball, left, right, owners[a.Reference],
				owners[b.Reference], movement.InverseSurfaceMoves);
			if (!uncovered && opening <= .12f) continue;
			float width = Math.Max(uncovered ? Math.Min(a.Distance, b.Distance) : 0,
				opening * IdealDirection[a.Reference].DistanceTo(IdealDirection[b.Reference]) / Spacing);
			Openings[cell] = new Opening(a.Reference, b.Reference, a.Distance, b.Distance, width);
		}

		void Visit(int cell, int reference, int distance)
		{
			var marker = new Front(cell, reference, distance);
			if (first[cell].Distance == 0) first[cell] = marker;
			else if (second[cell].Distance == 0 && owners[first[cell].Reference] != owners[reference]) second[cell] = marker;
			else return;
			queue.Enqueue(marker);
		}
	}

	static int CellAt(Ball ball, Vector3 point)
		=> ball.CellIndexOf(H3.LatLngToCell(new LatLng(Math.Atan2(point.Y, Math.Sqrt(point.X * point.X + point.Z * point.Z)),
			Math.Atan2(point.Z, point.X)), ball.Res));

	static Vector3 Rotate(RotationD rotation, Vector3 point)
	{
		var (x, y, z) = RotationOps.Apply(rotation, point.X, point.Y, point.Z);
		return new Vector3((float)x, (float)y, (float)z);
	}
}
