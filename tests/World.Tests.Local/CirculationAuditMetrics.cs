using System;
using System.Linq;
using Godot;
using World.Logic;

namespace World.Tests;

static class CirculationAuditMetrics
{
	public static (double Fine, double Parent) CellWindResidual(this CirculationOperator model, double[] pressure, double[] target, Vector3[] velocity,
		int[] parent, int parentCount)
	{
		var residual = new double[pressure.Length];
		for (int i = 0; i < residual.Length; i++) residual[i] = pressure[i] - target[i];
		foreach (var edge in model.Edges)
		{
			double speed = (velocity[edge.Left] + velocity[edge.Right]).Dot(edge.Normal) / 2;
			double flux = model.FluxScale * edge.LengthM * speed;
			residual[edge.Left] += flux / model.CellAreas[edge.Left];
			residual[edge.Right] -= flux / model.CellAreas[edge.Right];
		}
		var parentResidual = new double[parentCount];
		var parentTarget = new double[parentCount];
		var parentArea = new double[parentCount];
		for (int i = 0; i < residual.Length; i++)
		{
			int cell = parent[i];
			parentResidual[cell] += residual[i] * model.CellAreas[i];
			parentTarget[cell] += target[i] * model.CellAreas[i];
			parentArea[cell] += model.CellAreas[i];
		}
		double numerator = 0, denominator = 0;
		for (int i = 0; i < parentCount; i++)
		{
			numerator += parentResidual[i] * parentResidual[i] / parentArea[i];
			denominator += parentTarget[i] * parentTarget[i] / parentArea[i];
		}
		return (model.AreaRms(residual) / model.AreaRms(target), Math.Sqrt(numerator / denominator));
	}

	public static double[] CoastalVelocity(this CirculationOperator model, double[] pressure)
	{
		var flux = new double[model.Edges.Length];
		model.BuildFaceFlux(pressure, flux);
		return Coast(model).Select(face => flux[face.Index] / face.Edge.LengthM * (model.Grid.Land[face.Edge.Right] ? 1 : -1)).ToArray();
	}

	public static double[] CurrentCoastalVelocity(this CirculationOperator model, Vector3[] surface, double speedReference)
	{
		// Flat terrain lets us invert the production wind magnitude before averaging across each face.
		var velocity = surface.Select(wind =>
		{
			float speed = wind.Length();
			if (speed >= 1) throw new InvalidOperationException("The soft wind magnitude saturated.");
			return speed > 0 ? wind * (float)(-speedReference * Math.Log(1 - (double)speed) / speed) : wind;
		}).ToArray();
		return Coast(model).Select(face =>
		{
			var edge = face.Edge;
			var normal = edge.Normal;
			double outward = (velocity[edge.Left] + velocity[edge.Right]).Dot(normal) / 2;
			return outward * (model.Grid.Land[edge.Right] ? 1 : -1);
		}).ToArray();
	}

	public static double CoastalMean(this CirculationOperator model, double[] velocity)
	{
		var coast = Coast(model);
		return coast.Select((face, i) => face.Edge.LengthM * velocity[i]).Sum() / coast.Sum(face => face.Edge.LengthM);
	}

	public static double SeasonalReversalShare(this CirculationOperator model, double[][] velocity)
	{
		var coast = Coast(model);
		return coast.Where((_, i) => velocity[0][i] < 0 && velocity[1][i] > 0).Sum(face => face.Edge.LengthM)
			/ coast.Sum(face => face.Edge.LengthM);
	}

	public static int CoastCount(this CirculationOperator model) => Coast(model).Count();

	static System.Collections.Generic.IEnumerable<(CirculationOperator.Edge Edge, int Index)> Coast(CirculationOperator model) =>
		model.Edges.Select((edge, index) => (Edge: edge, Index: index))
			.Where(face => model.Grid.Land[face.Edge.Left] != model.Grid.Land[face.Edge.Right]);
}
