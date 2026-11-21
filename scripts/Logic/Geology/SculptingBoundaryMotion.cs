using System.Collections.Generic;
using Godot;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

internal static class SculptingBoundaryMotion
{
	// 统一在边界中点比较两边的逆位移。负数为收拢，正数为张开，单位为格宽。
	internal static float NormalMotion(Ball ball, int cellA, int cellB, int plateA, int plateB,
		IReadOnlyList<RotationD> inverseMoves, Vector3? boundaryNormal = null)
	{
		Vector3 a = ball.CellDirs[cellA], b = ball.CellDirs[cellB];
		var mid = (a + b).Normalized();
		var normal = boundaryNormal ?? b - a;
		float widthSquared = normal.LengthSquared();
		if (widthSquared <= 1e-12f) return 0;
		var fromA = RotationOps.Apply(inverseMoves[plateA], mid.X, mid.Y, mid.Z);
		var fromB = RotationOps.Apply(inverseMoves[plateB], mid.X, mid.Y, mid.Z);
		return (float)(((fromA.X - fromB.X) * normal.X + (fromA.Y - fromB.Y) * normal.Y
			+ (fromA.Z - fromB.Z) * normal.Z) / widthSquared);
	}
}
