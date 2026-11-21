using System;
using System.Linq;
using System.Threading;
using Godot;
using World.Data;
using World.H3Grid;
using H3 = World.Utils.H3.H3;
using World.Utils;

namespace World.Logic;

/// <summary>Steady circulation for all three wind closures on one shared mesh.</summary>
internal sealed class ConservativeWindSolver
{
	internal const float ThermalGainMultiplier = 1.5f;
	const double AirDensity = 1.2;
	const double RelaxationSeconds = 0.5 * 86400;
	const double WaveSpeedMs = 40;
	const double FluxScale = RelaxationSeconds * WaveSpeedMs * WaveSpeedMs;
	const int RedistributionPasses = 3;

	readonly CirculationOperator _fine;
	readonly CirculationOperator _coarse;
	readonly ConservativeFaceTransport _transport;
	readonly int[] _parent;
	readonly double[] _parentArea;
	readonly CirculationInterpolation.InterpolationWeight[][] _interpolation;
	readonly CirculationOperator.PressureStencil _surfacePressure;
	readonly CirculationOperator.PressureStencil _lowPressure;
	readonly CirculationOperator.PressureStencil _freePressure;
	readonly float _referenceSpeed;
	readonly float _lowFriction;
	readonly float _freeFriction;

	public ConservativeWindSolver(Ball ball, ClimateGrid grid, ClimateSpec spec, CancellationToken cancellation)
	{
		cancellation.ThrowIfCancellationRequested();
		if (!(spec.FrictionSurface > 0) || !(spec.FrictionLandFactor > 0) || !(spec.WindSpeedRefMs > 0) ||
			!(spec.FrictionFreeFactor > 0 && spec.FrictionFreeFactor <= spec.FrictionLowFactor && spec.FrictionLowFactor <= 1))
			throw new ArgumentOutOfRangeException(nameof(spec), "风的摩擦参数和参考风速必须为正，且自由风摩擦不大于低空风。");
		_referenceSpeed = spec.WindSpeedRefMs;
		_lowFriction = spec.FrictionLowFactor;
		_freeFriction = spec.FrictionFreeFactor;
		long setup = WorldBuildProfile.Start();
		_fine = new CirculationOperator(ball, grid, spec, cancellation);
		WorldBuildProfile.Mark(ref setup, "wind-setup fine operator");

		// Keep the local flux correction bounded to at most two H3 levels.
		var coarseBall = ball == null || ball.Res <= 2 ? ball : new Ball(Math.Max(2, ball.Res - 2), 1f);
		if (ReferenceEquals(coarseBall, ball))
		{
			_coarse = _fine;
			_transport = new ConservativeFaceTransport(ball, grid, _fine, _fine, Enumerable.Range(0, grid.Count).ToArray(),
				prepareCurl: false, cancellation);
		}
		else
		{
			_parent = new int[grid.Count];
			_parentArea = new double[coarseBall.CellIds.Length];
			var landArea = new double[_parentArea.Length];
			var height = new double[_parentArea.Length];
			for (int i = 0; i < grid.Count; i++)
			{
				if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
				int parent = coarseBall.CellIndexOf(H3.CellToParent(ball.CellIds[i], coarseBall.Res));
				_parent[i] = parent;
				double area = _fine.CellAreas[i];
				_parentArea[parent] += area;
				if (grid.Land[i]) landArea[parent] += area;
				height[parent] += area * Math.Max(grid.HeightM[i], 0);
			}
			var coarseLand = landArea.Select((area, i) => area >= 0.5 * _parentArea[i]).ToArray();
			var coarseGrid = ClimateGrid.FromArrays(coarseBall.CellDirs, coarseBall.CellNeighbors, coarseLand,
				height.Select((value, i) => (float)(value / _parentArea[i])).ToArray(),
				grid.RadiusKm, SpatialScale.Of(coarseBall).NeighborCenterKm);
			_coarse = new CirculationOperator(coarseBall, coarseGrid, spec, cancellation);
			WorldBuildProfile.Mark(ref setup, "wind-setup coarse operator");
			_interpolation = CirculationInterpolation.BuildInterpolation(grid, coarseGrid, _parent, cancellation);
			WorldBuildProfile.Mark(ref setup, "wind-setup interpolation");
			_transport = new ConservativeFaceTransport(ball, grid, _fine, _coarse, _parent, prepareCurl: false, cancellation);
			WorldBuildProfile.Mark(ref setup, "wind-setup transport factors");
			if (_transport.UnmatchedCrossingCount != 0)
				throw new InvalidOperationException("守恒风网格包含没有对应粗格边的细格边。");
			_surfacePressure = BuildPressureStencil(1, true, cancellation);
			_lowPressure = BuildPressureStencil(_lowFriction, false, cancellation);
			_freePressure = BuildPressureStencil(_freeFriction, false, cancellation);
			WorldBuildProfile.Mark(ref setup, "wind-setup pressure stencils");
		}
	}

	CirculationOperator.PressureStencil BuildPressureStencil(double friction, bool terrain, CancellationToken cancellation)
	{
		_coarse.ConfigureMobility(friction, terrain);
		_coarse.SetRelaxation(FluxScale);
		return _coarse.BuildPressureStencil(cancellation);
	}

	public (Vector3[] Surface, Vector3[] Low, Vector3[] Free) SolveMonth(float[] targetPressureHpa,
		int[] upstream, float[] speed, CancellationToken cancellation)
	{
		var target = ToPotential(_fine, targetPressureHpa);
		var coarseTarget = _parent == null ? target : Aggregate(target);
		return (Solve(target, coarseTarget, 1, true, _surfacePressure, null, null, cancellation),
			Solve(target, coarseTarget, _lowFriction, false, _lowPressure, upstream, speed, cancellation),
			Solve(target, coarseTarget, _freeFriction, false, _freePressure, null, null, cancellation));
	}

	Vector3[] Solve(double[] target, double[] coarseTarget, double friction, bool terrain,
		CirculationOperator.PressureStencil stencil, int[] upstream, float[] speed, CancellationToken cancellation)
	{
		long timing = WorldBuildProfile.Start();
		cancellation.ThrowIfCancellationRequested();
		_fine.ConfigureMobility(friction, terrain);
		if (_parent != null) _coarse.ConfigureMobility(friction, terrain);
		WorldBuildProfile.Mark(ref timing, "wind-detail mobility");
		_coarse.SetRelaxation(FluxScale);
		var solution = new double[coarseTarget.Length];
		_coarse.Solve(coarseTarget, solution, cancellation: cancellation, stencil: stencil);
		double residual = _coarse.RelativeResidual(solution, coarseTarget);
		// Restart from the best finite iterate when the recursive residual loses accuracy.
		for (int retry = 0; retry < 3 && double.IsFinite(residual) && residual > CirculationOperator.ResidualTolerance; retry++)
		{
			_coarse.Solve(coarseTarget, solution, initial: solution, cancellation: cancellation);
			residual = _coarse.RelativeResidual(solution, coarseTarget);
		}
		if (!double.IsFinite(residual) || residual > CirculationOperator.ResidualTolerance)
			throw new InvalidOperationException($"守恒风求解未收敛（相对残差 {residual:E2}）。");

		var coarseFlux = new double[_coarse.Edges.Length];
		_coarse.BuildFaceFlux(solution, coarseFlux);
		WorldBuildProfile.Mark(ref timing, "wind-detail coarse solve");
		if (_parent == null) return _transport.BuildWind(coarseFlux, _referenceSpeed, upstream, speed);

		// Interpolate the coarse solution's departure from its target, shared by all children.
		for (int i = 0; i < solution.Length; i++) solution[i] -= coarseTarget[i];
		var reconstructed = new double[target.Length];
		var desired = new double[target.Length];
		for (int i = 0; i < target.Length; i++)
		{
			if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
			double adjustment = 0;
			foreach (var weight in _interpolation[i])
				adjustment += weight.Weight * solution[weight.Cell];
			reconstructed[i] = target[i] + adjustment;
			desired[i] = -adjustment * _fine.CellAreas[i] / FluxScale;
		}
		var flux = new double[_fine.Edges.Length];
		_fine.BuildFaceFlux(reconstructed, flux);
		WorldBuildProfile.Mark(ref timing, "wind-detail fine reconstruction");
		var budget = _transport.Correct(coarseFlux, flux, desired, RedistributionPasses, cancellation);
		WorldBuildProfile.Mark(ref timing, "wind-detail flux correction");
		if (!(Math.Max(budget.Fine, Math.Max(budget.Parent, budget.Crossing)) < 1e-9))
			throw new InvalidOperationException("守恒风的粗细网格通量收支未闭合。");
		var wind = _transport.BuildWind(flux, _referenceSpeed, upstream, speed);
		WorldBuildProfile.Mark(ref timing, "wind-detail vector reconstruction");
		return wind;
	}

	internal static double[] ToPotential(CirculationOperator model, float[] pressureHpa)
	{
		var potential = new double[pressureHpa.Length];
		for (int i = 0; i < potential.Length; i++) potential[i] = pressureHpa[i] * 100.0 / AirDensity;
		double mean = model.AreaMean(potential);
		for (int i = 0; i < potential.Length; i++) potential[i] -= mean;
		return potential;
	}

	double[] Aggregate(double[] target)
	{
		var coarse = new double[_parentArea.Length];
		for (int i = 0; i < target.Length; i++) coarse[_parent[i]] += target[i] * _fine.CellAreas[i];
		for (int i = 0; i < coarse.Length; i++) coarse[i] /= _parentArea[i];
		return coarse;
	}
}
