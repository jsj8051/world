using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>把过大的活动板块沿低海拔裂带分成两块，并给两个碎片相反的分离方向。</summary>
internal sealed class PlateRift(Ball ball)
{
	const double MaximumWorldFraction = .26;
	const float FragmentTurnRadians = .055f;
	internal sealed record Split(int ParentId, int ChildId, IReadOnlyList<SculptingPlateState> Plates);

	internal Split TrySplit(SculptingPlateState[] plates, IReadOnlyList<PlateRotation> rotations,
		int[] owners, float[] heights, CancellationToken cancellation)
	{
		var counts = new int[plates.Length];
		foreach (int owner in owners) counts[owner]++;
		int parent = -1;
		int largestSize = Math.Max(1, (int)(owners.Length * MaximumWorldFraction));
		for (int id = 0; id < plates.Length; id++)
		{
			if (plates[id].IsExtinct || counts[id] <= largestSize) continue;
			parent = id;
			largestSize = counts[id];
		}
		if (parent < 0) return null;

		var parcel = new List<int>(largestSize);
		for (int cell = 0; cell < owners.Length; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			if (owners[cell] != parent) continue;
			parcel.Add(cell);
		}
		var components = FindComponents(parcel, owners, parent, cancellation);
		var main = components.MaxBy(component => component.Count);
		if (main.Count < 2) return null;

		var plate = plates[parent];
		var motion = plate.RotationOverride ?? rotations[parent];
		var pose = plate.Orientation;
		var seed = ToVector(RotationOps.Apply(pose, plate.ReferenceSeed));
		var center = Vector3.Zero;
		foreach (int cell in main) center += ball.CellDirs[cell];
		center = (center.LengthSquared() > 1e-6f ? center : seed).Normalized();
		var turnVector = SculptingPlateTopology.TurnVector(motion);
		var normal = turnVector.Cross(center);
		if (normal.LengthSquared() < 1e-7f)
			normal = (MathF.Abs(center.Y) < .9f ? Vector3.Up : Vector3.Right).Cross(center);
		normal = normal.Normalized();
		var division = new PlateRiftSeam(ball, main, owners, heights, cancellation).Find(center, normal);
		if (division == null) return null;

		int child = plates.Length;
		var left = Vector3.Zero;
		var right = Vector3.Zero;
		foreach (int cell in main)
		{
			if (!division.ChildCells.Contains(cell)) left += ball.CellDirs[cell];
			else
			{
				right += ball.CellDirs[cell];
				owners[cell] = child;
			}
		}
		var leftDirection = left.Normalized();
		var rightDirection = right.Normalized();
		foreach (var component in components)
		{
			if (component == main) continue;
			var direction = Vector3.Zero;
			foreach (int cell in component) direction += ball.CellDirs[cell];
			if (direction.Dot(rightDirection - leftDirection) <= 0) continue;
			foreach (int cell in component) owners[cell] = child;
		}

		var separationAxis = leftDirection.Cross(rightDirection);
		if (separationAxis.LengthSquared() < 1e-6f) separationAxis = center.Cross(division.Normal);
		var separation = separationAxis.Normalized() * FragmentTurnRadians;
		var inverse = RotationOps.Inverse(pose);
		var parentSeed = ReferenceSeed(left, center - division.Normal * .25f, inverse);
		var childSeed = ReferenceSeed(right, center + division.Normal * .25f, inverse);
		var splitPlates = new SculptingPlateState[child + 1];
		Array.Copy(plates, splitPlates, child);
		splitPlates[parent] = plate.WithFragmentMotion(parentSeed, SculptingPlateTopology.Rotation(parent, turnVector - separation));
		splitPlates[child] = new SculptingPlateState(child, plate.Density, childSeed, pose)
			.WithFragmentMotion(childSeed, SculptingPlateTopology.Rotation(child, turnVector + separation));
		return new Split(parent, child, Array.AsReadOnly(splitPlates));
	}

	List<List<int>> FindComponents(List<int> parcel, int[] owners, int parent, CancellationToken cancellation)
	{
		var visited = new bool[owners.Length];
		var components = new List<List<int>>();
		var search = new Queue<int>();
		foreach (int start in parcel)
		{
			if (visited[start]) continue;
			var component = new List<int>();
			components.Add(component);
			visited[start] = true;
			search.Enqueue(start);
			while (search.TryDequeue(out int cell))
			{
				if ((component.Count & 1023) == 0) cancellation.ThrowIfCancellationRequested();
				component.Add(cell);
				foreach (int neighbor in ball.CellNeighbors[cell])
				{
					if (owners[neighbor] != parent || visited[neighbor]) continue;
					visited[neighbor] = true;
					search.Enqueue(neighbor);
				}
			}
		}
		return components;
	}

	static SpherePointD ReferenceSeed(Vector3 direction, Vector3 fallback, RotationD inverse)
	{
		if (direction.LengthSquared() < 1e-6f) direction = fallback;
		double norm = Length(direction);
		return RotationOps.Apply(inverse, new SpherePointD(direction.X / norm, direction.Y / norm, direction.Z / norm));
	}

	static double Length(Vector3 value)
		=> Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);

	static Vector3 ToVector(SpherePointD value) => new((float)value.X, (float)value.Y, (float)value.Z);
}
