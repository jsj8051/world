using System;
using Godot;
using World.Data;
using World.Logic;

namespace World.Tests;

static class ConservativeFaceTransportTests
{
	internal static void RedistributionMatchesAnalyticMinimum()
	{
		var fineGrid = WindGrid();
		var coarseGrid = ClimateGrid.FromArrays(new[] { Direction(-.1f, 0), Direction(.1f, 0) },
			new[] { new[] { 1 }, new[] { 0 } }, new bool[2], new float[2]);
		var spec = new ClimateSpec();
		var fine = new CirculationOperator(null, fineGrid, spec);
		var coarse = new CirculationOperator(null, coarseGrid, spec);
		var transport = new ConservativeFaceTransport(null, fineGrid, fine, coarse, new[] { 0, 0, 1, 1 }, prepareCurl: false);

		const double crossingTotal = 1.4;
		double[] initial = { .3, -.7, .6, -.2 };
		double[] desired = { .2, -.4, .8, .1 };
		var actual = (double[])initial.Clone();
		var budget = transport.Correct(new[] { crossingTotal }, actual, desired, redistributionPasses: 1);

		// In face order (0→1, 0→2, 1→3, 2→3), all feasible fluxes lie on one line.
		// Minimize the squared displacement from the provisional flux along that line.
		double[] offset = { desired[0], 0, crossingTotal, desired[2] };
		double[] slope = { -1, 1, -1, 1 };
		double gradient = 0, curvature = 0;
		for (int i = 0; i < actual.Length; i++)
		{
			double weight = 1 / fine.Edges[i].LengthM;
			gradient += weight * (offset[i] - initial[i]) * slope[i];
			curvature += weight * slope[i] * slope[i];
		}
		for (int i = 0; i < actual.Length; i++)
			Program.True(Math.Abs(actual[i] - (offset[i] - gradient / curvature * slope[i])) < 1e-12,
				$"Face {i} must match the analytic constrained minimum.");
		Program.True(Math.Max(budget.Fine, Math.Max(budget.Parent, budget.Crossing)) < 1e-12,
			"Redistribution closes the fine, parent and crossing budgets.");
	}

	internal static void PressureStencilMatchesFaceDivergence()
	{
		var model = new CirculationOperator(null, WindGrid(), new ClimateSpec());
		double[] pressure = { 6, -9, .2, 4 };
		double[] constant = { 17, 17, 17, 17 };
		var target = new double[pressure.Length];
		foreach (var (friction, terrain) in new[] { (1d, true), (.35, false), (.1, false) })
		{
			model.ConfigureMobility(friction, terrain);
			model.SetRelaxation(7e7);
			var stencil = model.BuildPressureStencil();
			stencil.Apply(pressure, target);
			Program.True(model.RelativeResidual(pressure, target) < 1e-13,
				"The assembled stencil must reproduce the explicitly evaluated face divergence.");
			stencil.Apply(constant, target);
			Program.True(target.AsSpan().SequenceEqual(constant), "Constant pressure must give zero divergence.");
		}
	}

	static ClimateGrid WindGrid() => ClimateGrid.FromArrays(
		new[] { Direction(-.1f, .12f), Direction(-.17f, -.08f), Direction(.13f, .16f), Direction(.1f, -.1f) },
		new[] { new[] { 1, 2 }, new[] { 0, 3 }, new[] { 0, 3 }, new[] { 1, 2 } },
		new[] { true, false, true, false }, new[] { 850f, -230f, 1200f, 0f });

	static Vector3 Direction(float east, float north) => new Vector3(1, north, east).Normalized();
}
