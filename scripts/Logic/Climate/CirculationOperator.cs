using System;
using System.Linq;
using System.Threading;
using Godot;
using World.Data;
using World.H3Grid;
using H3 = World.Utils.H3.H3;

namespace World.Logic;

internal sealed class CirculationOperator
{
	internal const double ResidualTolerance = 1e-7;

	readonly record struct Neighbor(int Cell, Vector3 GradientWeight);
	public readonly record struct Edge(int Left, int Right, int FirstVertex, int SecondVertex, double LengthM, Vector3 Normal);
	readonly record struct EdgeDynamics(double DistanceM, double Coriolis, double Drag, double TerrainMobility,
		float LeftEast, float LeftNorth, float RightEast, float RightNorth);
	readonly ClimateGrid _grid;
	readonly double[] _area;
	readonly double _totalArea;
	readonly Neighbor[][] _neighbors;
	readonly Edge[] _edges;
	readonly EdgeDynamics[] _dynamics;
	readonly (double Normal, double Rotation)[] _mobility;
	readonly Vector3[] _east;
	readonly Vector3[] _north;
	readonly (double East, double North)[] _gradient;
	readonly double[] _inverseDiagonal;
	readonly double[][] _work;
	readonly bool _synthetic;
	double[] _directFactor;
	int[] _directPivots;
	double _fluxScale;

	public double[] CellAreas => _area;
	public ClimateGrid Grid => _grid;
	public double FluxScale => _fluxScale;
	public Edge[] Edges => _edges;

	public void BuildFaceFlux(double[] potential, double[] flux)
	{
		BuildGradient(potential);
		for (int i = 0; i < _edges.Length; i++)
			flux[i] = _edges[i].LengthM * NormalVelocity(i, potential);
	}

	public CirculationOperator(Ball ball, ClimateGrid grid, ClimateSpec spec, CancellationToken cancellation = default)
	{
		_grid = grid;
		_synthetic = ball == null;
		_area = new double[grid.Count];
		double radiusM = grid.RadiusKm * 1000.0;
		if (ball == null) Array.Fill(_area, 4 * Math.PI * radiusM * radiusM / grid.Count);
		else
			for (int i = 0; i < grid.Count; i++)
			{
				if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
				_area[i] = H3.CellAreaKm2(ball.CellIds[i]) * 1e6;
			}
		_totalArea = _area.Sum();
		_east = new Vector3[grid.Count];
		_north = new Vector3[grid.Count];
		_gradient = new (double, double)[grid.Count];
		_neighbors = new Neighbor[grid.Count][];
		_inverseDiagonal = new double[grid.Count];
		_work = Enumerable.Range(0, 8).Select(_ => new double[grid.Count]).ToArray();
		var vertexIds = ball?.CellIds.Select(H3.CellToVertexes).ToArray();
		var edges = new System.Collections.Generic.List<Edge>();
		var dynamics = new System.Collections.Generic.List<EdgeDynamics>();

		for (int i = 0; i < grid.Count; i++)
		{
			if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
			var direction = grid.Dirs[i];
			_north[i] = TangentField.NorthTangent(direction);
			_east[i] = TangentField.EastTangent(direction, _north[i]);
			_neighbors[i] = BuildGradientWeights(grid, i, _east[i], _north[i], radiusM);
		}

		for (int i = 0; i < grid.Count; i++)
		{
			if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
			var direction = grid.Dirs[i];
			foreach (int j in grid.Neighbors[i])
			{
				if (j <= i) continue;
				var other = grid.Dirs[j];
				var midpoint = (direction + other).Normalized();
				var normal = (other - direction).Normalized();
				double distanceM = radiusM * Math.Atan2(direction.Cross(other).Length(), direction.Dot(other));
				int firstIndex = -1, secondIndex = -1;
				double lengthM = distanceM / Math.Sqrt(3);
				if (ball != null)
				{
					ulong firstId = 0, secondId = 0;
					foreach (ulong vertex in vertexIds[i])
					{
						if (Array.IndexOf(vertexIds[j], vertex) < 0) continue;
						if (firstId == 0) firstId = vertex;
						else secondId = vertex;
					}
					if (secondId == 0) throw new InvalidOperationException("An H3 edge must link two shared vertices.");
					firstIndex = ball.VertexIndexOf(firstId);
					secondIndex = ball.VertexIndexOf(secondId);
					var first = ball.Vertices.Positions[firstIndex];
					var second = ball.Vertices.Positions[secondIndex];
					lengthM = radiusM * Math.Atan2(first.Cross(second).Length(), first.Dot(second));
				}
				double f = 2 * 7.2921e-5 * spec.RotationSpeed * (spec.Prograde ? 1 : -1) * midpoint.Y;
				double landFraction = ((grid.Land[i] ? 1.0 : 0.0) + (grid.Land[j] ? 1.0 : 0.0)) / 2;
				double drag = spec.FrictionSurface * (1 + landFraction * (spec.FrictionLandFactor - 1));
				var tangent = midpoint.Cross(normal);
				double terrain = (TerrainMobility(grid.HeightM[i]) + TerrainMobility(grid.HeightM[j])) / 2;
				edges.Add(new Edge(i, j, firstIndex, secondIndex, lengthM, normal));
				dynamics.Add(new EdgeDynamics(distanceM, f, drag, terrain,
					_east[i].Dot(tangent), _north[i].Dot(tangent), _east[j].Dot(tangent), _north[j].Dot(tangent)));
			}
		}
		_edges = edges.ToArray();
		_dynamics = dynamics.ToArray();
		_mobility = new (double, double)[_edges.Length];
		ConfigureMobility(1, false);
	}

	static double TerrainMobility(float heightM)
	{
		double t = Math.Clamp(heightM / 2500d, 0, 1);
		return 1 - 0.45 * t * t * (3 - 2 * t);
	}

	public void ConfigureMobility(double frictionFactor, bool useTerrain)
	{
		for (int i = 0; i < _dynamics.Length; i++)
		{
			var edge = _dynamics[i];
			double drag = edge.Drag * frictionFactor;
			double mobility = (useTerrain ? edge.TerrainMobility : 1) / (drag * drag + edge.Coriolis * edge.Coriolis);
			_mobility[i] = (mobility * drag / edge.DistanceM, mobility * edge.Coriolis / 2);
		}
	}

	// Weighted least squares supplies the tangential gradient at the shared edge.
	static Neighbor[] BuildGradientWeights(ClimateGrid grid, int i, Vector3 east, Vector3 north, double radiusM)
	{
		var offsets = grid.Neighbors[i].Select(j =>
		{
			var chord = grid.Dirs[j] - grid.Dirs[i];
			double x = chord.Dot(east) * radiusM;
			double y = chord.Dot(north) * radiusM;
			return (Cell: j, X: x, Y: y, Weight: 1 / (x * x + y * y));
		}).ToArray();
		double xx = offsets.Sum(offset => offset.Weight * offset.X * offset.X);
		double xy = offsets.Sum(offset => offset.Weight * offset.X * offset.Y);
		double yy = offsets.Sum(offset => offset.Weight * offset.Y * offset.Y);
		var (eastEast, eastNorth, northNorth) = InvertTangentFit(xx, xy, yy);
		return offsets.Select(offset => new Neighbor(offset.Cell, new Vector3(
			(float)(offset.Weight * (eastEast * offset.X + eastNorth * offset.Y)),
			(float)(offset.Weight * (eastNorth * offset.X + northNorth * offset.Y)), 0f))).ToArray();
	}

	internal static (double EastEast, double EastNorth, double NorthNorth) InvertTangentFit(double xx, double xy, double yy)
	{
		double trace = xx + yy;
		double determinant = xx * yy - xy * xy;
		if (trace == 0) return (0, 0, 0);
		if (determinant > 1e-5 * trace * trace) return (yy / determinant, -xy / determinant, xx / determinant);
		// A line of synthetic cells observes only one tangent direction.
		double rankOneScale = 1 / (trace * trace);
		return (xx * rankOneScale, xy * rankOneScale, yy * rankOneScale);
	}

	public void SetRelaxation(double fluxScale)
	{
		_fluxScale = fluxScale;
		_directFactor = null;
		Array.Fill(_inverseDiagonal, 1);
		for (int i = 0; i < _edges.Length; i++)
		{
			var edge = _edges[i];
			double diffusion = _fluxScale * edge.LengthM * _mobility[i].Normal;
			_inverseDiagonal[edge.Left] += diffusion / _area[edge.Left];
			_inverseDiagonal[edge.Right] += diffusion / _area[edge.Right];
		}
		for (int i = 0; i < _grid.Count; i++) _inverseDiagonal[i] = 1 / _inverseDiagonal[i];
	}

	void BuildGradient(double[] pressure)
	{
		for (int i = 0; i < _grid.Count; i++)
		{
			double east = 0, north = 0;
			foreach (var neighbor in _neighbors[i])
			{
				double difference = pressure[neighbor.Cell] - pressure[i];
				east += difference * neighbor.GradientWeight.X;
				north += difference * neighbor.GradientWeight.Y;
			}
			_gradient[i] = (east, north);
		}
	}

	double NormalVelocity(int index, double[] pressure)
	{
		ref readonly var edge = ref _edges[index];
		ref readonly var geometry = ref _dynamics[index];
		var (normal, rotation) = _mobility[index];
		var left = _gradient[edge.Left];
		var right = _gradient[edge.Right];
		return normal * (pressure[edge.Left] - pressure[edge.Right]) + rotation *
			(left.East * geometry.LeftEast + left.North * geometry.LeftNorth +
			 right.East * geometry.RightEast + right.North * geometry.RightNorth);
	}

	// Phi + tau*c^2*div(U(Phi)) = Phi_target. Each face flux is counted once with opposite signs.
	void Apply(double[] pressure, double[] output)
	{
		BuildGradient(pressure);
		Array.Copy(pressure, output, _grid.Count);
		for (int i = 0; i < _edges.Length; i++)
		{
			ref readonly var edge = ref _edges[i];
			double flux = _fluxScale * edge.LengthM * NormalVelocity(i, pressure);
			output[edge.Left] += flux / _area[edge.Left];
			output[edge.Right] -= flux / _area[edge.Right];
		}
	}

	// Jacobi-preconditioned BiCGSTAB. Latitude-dependent Coriolis makes the operator nonsymmetric.
	public int Solve(double[] target, double[] result, double[] initial = null, double tolerance = ResidualTolerance,
		int iterationLimit = 500, CancellationToken cancellation = default)
	{
		if (_synthetic && _grid.Count <= 128)
		{
			SolveSmallGrid(target, result, cancellation);
			return 1;
		}
		double[] residual = _work[0], shadow = _work[1], direction = _work[2], image = _work[3];
		double[] step = _work[4], secondImage = _work[5], scaledDirection = _work[6], scaledStep = _work[7];
		Array.Copy(initial ?? target, result, result.Length);
		Apply(result, image);
		for (int i = 0; i < result.Length; i++) shadow[i] = residual[i] = target[i] - image[i];
		double toleranceSquared = tolerance * tolerance * Dot(target, target);
		if (Dot(residual, residual) <= toleranceSquared) return 0;
		Array.Clear(direction);
		Array.Clear(image);
		double previousProjection = 1, alpha = 1, omega = 1;
		for (int iteration = 1; iteration <= iterationLimit; iteration++)
		{
			cancellation.ThrowIfCancellationRequested();
			double projection = Dot(shadow, residual);
			if (projection == 0 || !double.IsFinite(projection)) break;
			double beta = (projection / previousProjection) * (alpha / omega);
			for (int i = 0; i < result.Length; i++)
			{
				direction[i] = residual[i] + beta * (direction[i] - omega * image[i]);
				scaledDirection[i] = _inverseDiagonal[i] * direction[i];
			}
			Apply(scaledDirection, image);
			alpha = projection / Dot(shadow, image);
			for (int i = 0; i < result.Length; i++)
			{
				step[i] = residual[i] - alpha * image[i];
				result[i] += alpha * scaledDirection[i];
				scaledStep[i] = _inverseDiagonal[i] * step[i];
			}
			if (Dot(step, step) <= toleranceSquared) return iteration;
			Apply(scaledStep, secondImage);
			omega = Dot(secondImage, step) / Dot(secondImage, secondImage);
			if (omega == 0 || !double.IsFinite(omega)) break;
			for (int i = 0; i < result.Length; i++)
			{
				result[i] += omega * scaledStep[i];
				residual[i] = step[i] - omega * secondImage[i];
			}
			if (Dot(residual, residual) <= toleranceSquared) return iteration;
			previousProjection = projection;
		}
		return -1;
	}

	void SolveSmallGrid(double[] target, double[] result, CancellationToken cancellation)
	{
		int n = _grid.Count;
		if (_directFactor == null)
		{
			var matrix = new double[n * n];
			var basis = _work[0];
			var image = _work[1];
			Array.Clear(basis);
			for (int column = 0; column < n; column++)
			{
				cancellation.ThrowIfCancellationRequested();
				basis[column] = 1;
				Apply(basis, image);
				basis[column] = 0;
				for (int row = 0; row < n; row++) matrix[row * n + column] = image[row];
			}
			var pivots = new int[n];
			for (int pivot = 0; pivot < n; pivot++)
			{
				int largest = pivot;
				for (int row = pivot + 1; row < n; row++)
					if (Math.Abs(matrix[row * n + pivot]) > Math.Abs(matrix[largest * n + pivot])) largest = row;
				pivots[pivot] = largest;
				if (largest != pivot)
					for (int column = 0; column < n; column++)
						(matrix[pivot * n + column], matrix[largest * n + column]) =
							(matrix[largest * n + column], matrix[pivot * n + column]);
				for (int row = pivot + 1; row < n; row++)
				{
					double multiplier = matrix[row * n + pivot] /= matrix[pivot * n + pivot];
					for (int column = pivot + 1; column < n; column++)
						matrix[row * n + column] -= multiplier * matrix[pivot * n + column];
				}
			}
			_directFactor = matrix;
			_directPivots = pivots;
		}
		Array.Copy(target, result, n);
		for (int i = 0; i < n; i++) (result[i], result[_directPivots[i]]) = (result[_directPivots[i]], result[i]);
		for (int row = 0; row < n; row++)
			for (int column = 0; column < row; column++) result[row] -= _directFactor[row * n + column] * result[column];
		for (int row = n - 1; row >= 0; row--)
		{
			for (int column = row + 1; column < n; column++) result[row] -= _directFactor[row * n + column] * result[column];
			result[row] /= _directFactor[row * n + row];
		}
	}

	double Dot(double[] left, double[] right)
	{
		double sum = 0;
		for (int i = 0; i < left.Length; i++) sum += _area[i] * left[i] * right[i];
		return sum;
	}

	public double RelativeResidual(double[] pressure, double[] target)
	{
		var residual = _work[0];
		Apply(pressure, residual);
		for (int i = 0; i < residual.Length; i++) residual[i] -= target[i];
		double squaredError = Dot(residual, residual);
		return squaredError == 0 ? 0 : Math.Sqrt(squaredError / Dot(target, target));
	}

	public double AreaMean(double[] field)
	{
		double sum = 0;
		for (int i = 0; i < field.Length; i++) sum += _area[i] * field[i];
		return sum / _totalArea;
	}

	public double AreaRms(double[] field) => Math.Sqrt(Dot(field, field) / _totalArea);

}
