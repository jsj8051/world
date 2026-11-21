using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>Commits plate identity changes after the transport ledger has been resolved.</summary>
internal static class SculptingPlateTopology
{
	internal static SculptingPlateState[] Update(Ball ball, PlateMovementResult movement, int[] owners,
		SculptingCoverage coverage, CancellationToken cancellation)
	{
		var plates = movement.Plates.ToArray();
		var sizes = new int[plates.Length];
		var oldSizes = new int[plates.Length];
		foreach (int owner in owners) sizes[owner]++;
		foreach (int owner in movement.Source.Surface.PlateOfCell) oldSizes[owner]++;
		for (int plate = 0; plate < sizes.Length; plate++)
			if (sizes[plate] == 0 && !plates[plate].IsExtinct) plates[plate] = plates[plate].Extinguish();
		var collided = new Dictionary<(int First, int Second), int>();
		if (coverage == null) return plates;
		for (int cell = 0; cell < owners.Length; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			if (coverage.Collisions[cell] is not { } impact) continue;
			foreach (var opponent in impact.Opponents)
			{
				var pair = Pair(impact.Winner, opponent.Plate);
				collided[pair] = collided.GetValueOrDefault(pair) + 1;
			}
		}
		if (collided.Count == 0) return plates;

		var turns = movement.Rotations.Select(TurnVector).ToArray();
		var contact = new Dictionary<(int First, int Second), int>();
		var perimeter = new int[plates.Length];
		for (int cell = 0; cell < owners.Length; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			int owner = owners[cell];
			foreach (int neighbor in ball.CellNeighbors[cell])
			{
				int other = owners[neighbor];
				if (cell >= neighbor || other == owner) continue;
				perimeter[owner]++;
				perimeter[other]++;
				var pair = Pair(owner, other);
				Vector3 direction = (ball.CellDirs[cell] + ball.CellDirs[neighbor]).Normalized();
				Vector3 first = turns[owner].Cross(direction), second = turns[other].Cross(direction);
				if ((first - second).Length() <= Math.Min(.032f, .025f + .3f * (first.Length() + second.Length())))
					contact[pair] = contact.GetValueOrDefault(pair) + 1;
			}
		}
		var receivers = Enumerable.Range(0, plates.Length).ToArray();
		foreach (var entry in collided.OrderByDescending(pair => pair.Value)
			.ThenBy(pair => pair.Key.First).ThenBy(pair => pair.Key.Second))
		{
			cancellation.ThrowIfCancellationRequested();
			var (first, second) = entry.Key;
			int receiver = sizes[first] >= sizes[second] ? first : second;
			int absorbed = receiver == first ? second : first;
			if (first == second || plates[first].IsExtinct || plates[second].IsExtinct
				|| receivers[absorbed] != absorbed || receivers[receiver] != receiver) continue;
			if (entry.Value < Math.Max(2, Math.Min(oldSizes[first], oldSizes[second]) * .025f)
				|| contact.GetValueOrDefault(entry.Key) < .45f * perimeter[absorbed]) continue;
			int total = sizes[receiver] + sizes[absorbed];
			turns[receiver] = (turns[receiver] * sizes[receiver] + turns[absorbed] * sizes[absorbed]) / total;
			plates[receiver] = plates[receiver].WithFragmentMotion(plates[receiver].ReferenceSeed, Rotation(receiver, turns[receiver]));
			plates[absorbed] = plates[absorbed].Extinguish();
			receivers[absorbed] = receiver;
			sizes[receiver] = total;
		}
		for (int cell = 0; cell < owners.Length; cell++)
		{
			int plate = owners[cell];
			while (receivers[plate] != plate) plate = receivers[plate];
			owners[cell] = plate;
		}
		return plates;
	}

	static (int First, int Second) Pair(int first, int second)
		=> first < second ? (first, second) : (second, first);

	internal static Vector3 TurnVector(PlateRotation rotation)
		=> new Vector3((float)rotation.Axis.X, (float)rotation.Axis.Y, (float)rotation.Axis.Z)
			* (float)Math.IEEERemainder(rotation.AngleRadians, Math.Tau);

	internal static PlateRotation Rotation(int plate, Vector3 turn)
	{
		double length = Math.Sqrt((double)turn.X * turn.X + (double)turn.Y * turn.Y + (double)turn.Z * turn.Z);
		return length == 0 ? new PlateRotation(plate, new SpherePointD(0, 1, 0), 0)
			: new PlateRotation(plate, new SpherePointD(turn.X / length, turn.Y / length, turn.Z / length), length);
	}
}
