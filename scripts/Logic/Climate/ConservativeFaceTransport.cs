using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using World.H3Grid;

namespace World.Logic;

// Face flux is signed from Left to Right and has units m²/s per unit layer depth.
internal sealed class ConservativeFaceTransport
{
	readonly record struct Link(int Index, int Sign);
	readonly record struct Fit(double EastEast, double EastNorth, double NorthNorth);
	readonly record struct VertexFit(int First, int Second, int Third, float FirstWeight, float SecondWeight, float ThirdWeight);
	readonly CirculationOperator.Edge[] _faces;
	readonly CirculationOperator.Edge[] _coarseFaces;
	readonly Link[][] _crossings;
	readonly Link[] _unmatchedCrossings;
	readonly int[] _parent;
	readonly double[] _area;
	readonly double[] _parentArea;
	readonly ParentCorrection[] _corrections;
	readonly Fit[] _velocityFit;
	readonly Vector3[] _east;
	readonly Vector3[] _north;
	readonly Vector3[] _radial;
	readonly Vector3[] _boundaryChord;
	readonly VertexFit[] _vertexFit;

	public ConservativeFaceTransport(Ball ball, ClimateGrid grid, CirculationOperator fine, CirculationOperator coarse, int[] parent,
		bool prepareCurl = true, CancellationToken cancellation = default)
	{
		_faces = fine.Edges;
		_coarseFaces = coarse.Edges;
		_parent = parent;
		_area = fine.CellAreas;
		_radial = grid.Dirs;
		_parentArea = new double[coarse.CellAreas.Length];
		var children = Enumerable.Range(0, _parentArea.Length).Select(_ => new List<int>()).ToArray();
		for (int i = 0; i < parent.Length; i++)
		{
			children[parent[i]].Add(i);
			_parentArea[parent[i]] += _area[i];
		}
		var internalFaces = children.Select(_ => new List<int>()).ToArray();
		var crossed = _coarseFaces.Select(_ => new List<Link>()).ToArray();
		var lookup = _coarseFaces.Select((face, index) => ((face.Left, face.Right), index)).ToDictionary();
		var unmatched = new List<Link>();
		_north = grid.Dirs.Select(TangentField.NorthTangent).ToArray();
		_east = grid.Dirs.Select((dir, i) => TangentField.EastTangent(dir, _north[i])).ToArray();
		_boundaryChord = prepareCurl ? new Vector3[_faces.Length] : null;
		var vertexCells = prepareCurl ? new (int First, int Second, int Third)[ball.Vertices.Count] : null;
		if (vertexCells != null) Array.Fill(vertexCells, (-1, -1, -1));
		var xx = new double[parent.Length];
		var xy = new double[parent.Length];
		var yy = new double[parent.Length];
		for (int i = 0; i < _faces.Length; i++)
		{
			if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
			var face = _faces[i];
			int left = parent[face.Left], right = parent[face.Right];
			if (left == right) internalFaces[left].Add(i);
			else
			{
				var key = (Math.Min(left, right), Math.Max(left, right));
				var link = new Link(i, left < right ? 1 : -1);
				if (lookup.TryGetValue(key, out int coarseIndex)) crossed[coarseIndex].Add(link);
				else unmatched.Add(link);
			}
			if (prepareCurl)
			{
				var midpoint = (grid.Dirs[face.Left] + grid.Dirs[face.Right]).Normalized();
				// Positive boundary circulation is counterclockwise in the local east/north plane.
				var chord = (ball.Vertices.Positions[face.SecondVertex] - ball.Vertices.Positions[face.FirstVertex]) / ball.Radius;
				_boundaryChord[i] = chord.Dot(-midpoint.Cross(face.Normal)) >= 0 ? chord : -chord;
				AddVertexCell(face.FirstVertex, face.Left);
				AddVertexCell(face.FirstVertex, face.Right);
				AddVertexCell(face.SecondVertex, face.Left);
				AddVertexCell(face.SecondVertex, face.Right);
			}
			AddNormal(face.Left, face);
			AddNormal(face.Right, face);
		}
		_velocityFit = new Fit[parent.Length];
		for (int i = 0; i < parent.Length; i++)
		{
			var (eastEast, eastNorth, northNorth) = CirculationOperator.InvertTangentFit(xx[i], xy[i], yy[i]);
			_velocityFit[i] = new Fit(eastEast, eastNorth, northNorth);
		}
		_crossings = crossed.Select(list => list.ToArray()).ToArray();
		if (_crossings.Any(links => links.Length == 0)) throw new InvalidOperationException("A coarse face has no fine crossings.");
		_unmatchedCrossings = unmatched.ToArray();
		_corrections = children.Select((cells, i) =>
		{
			cancellation.ThrowIfCancellationRequested();
			return new ParentCorrection(cells, internalFaces[i], _faces);
		}).ToArray();
		_vertexFit = vertexCells?.Select((cells, i) =>
		{
			var vertex = ball.Vertices.Positions[i].Normalized();
			var first = grid.Dirs[cells.First];
			var second = grid.Dirs[cells.Second];
			var third = grid.Dirs[cells.Third];
			// Center the determinant at the vertex to keep its operands at grid scale.
			float a = vertex.Dot((second - vertex).Cross(third - vertex));
			float b = vertex.Dot((third - vertex).Cross(first - vertex));
			float c = vertex.Dot((first - vertex).Cross(second - vertex));
			float sum = a + b + c;
			return new VertexFit(cells.First, cells.Second, cells.Third, a / sum, b / sum, c / sum);
		}).ToArray();

		void AddNormal(int cell, CirculationOperator.Edge face)
		{
			double x = face.Normal.Dot(_east[cell]), y = face.Normal.Dot(_north[cell]);
			xx[cell] += face.LengthM * x * x;
			xy[cell] += face.LengthM * x * y;
			yy[cell] += face.LengthM * y * y;
		}

		void AddVertexCell(int vertex, int cell)
		{
			ref var cells = ref vertexCells[vertex];
			if (cell == cells.First || cell == cells.Second || cell == cells.Third) return;
			if (cells.First < 0) cells.First = cell;
			else if (cells.Second < 0) cells.Second = cell;
			else if (cells.Third < 0) cells.Third = cell;
			else throw new InvalidOperationException("An H3 vertex must meet three cells.");
		}
	}

	public int UnmatchedCrossingCount => _unmatchedCrossings.Length;

	public (double Fine, double Parent, double Crossing) Correct(double[] coarseFlux, double[] flux, double[] desiredNet,
		int redistributionPasses = 0, CancellationToken cancellation = default)
	{
		var initialFlux = redistributionPasses > 0 ? (double[])flux.Clone() : null;
		var coarseNet = new double[_parentArea.Length];
		var provisionalParentNet = new double[_parentArea.Length];
		for (int i = 0; i < desiredNet.Length; i++) provisionalParentNet[_parent[i]] += desiredNet[i];
		for (int i = 0; i < _crossings.Length; i++)
		{
			var face = _coarseFaces[i];
			coarseNet[face.Left] += coarseFlux[i];
			coarseNet[face.Right] -= coarseFlux[i];
			var links = _crossings[i];
			double net = 0, length = 0;
			foreach (var link in links)
			{
				net += link.Sign * flux[link.Index];
				length += _faces[link.Index].LengthM;
			}
			double adjustment = (coarseFlux[i] - net) / length;
			foreach (var link in links)
				flux[link.Index] += link.Sign * adjustment * _faces[link.Index].LengthM;
		}
		foreach (var link in _unmatchedCrossings) flux[link.Index] = 0;
		// Preserve the fine-scale divergence anomaly and give each parent its solved net flux.
		for (int i = 0; i < desiredNet.Length; i++)
		{
			int parent = _parent[i];
			desiredNet[i] += _area[i] * (coarseNet[parent] - provisionalParentNet[parent]) / _parentArea[parent];
		}
		var deficit = (double[])desiredNet.Clone();
		AccumulateNet(flux, deficit, -1);
		var potential = redistributionPasses > 0 ? new double[desiredNet.Length] : null;
		foreach (var correction in _corrections) correction.Apply(deficit, flux, potential);
		if (initialFlux != null) Redistribute(initialFlux, flux, potential, redistributionPasses, cancellation);

		var netAfter = new double[desiredNet.Length];
		AccumulateNet(flux, netAfter, 1);
		double fineError = 0, fineReference = 0;
		var parentAfter = new double[_parentArea.Length];
		for (int i = 0; i < desiredNet.Length; i++)
		{
			double error = netAfter[i] - desiredNet[i];
			fineError += error * error / _area[i];
			fineReference += desiredNet[i] * desiredNet[i] / _area[i];
			parentAfter[_parent[i]] += netAfter[i];
		}
		double parentError = 0, parentReference = 0, crossingError = 0, crossingReference = 0;
		for (int i = 0; i < coarseNet.Length; i++)
		{
			double error = parentAfter[i] - coarseNet[i];
			parentError += error * error / _parentArea[i];
			parentReference += coarseNet[i] * coarseNet[i] / _parentArea[i];
		}
		for (int i = 0; i < coarseFlux.Length; i++)
		{
			double sum = 0;
			foreach (var link in _crossings[i]) sum += link.Sign * flux[link.Index];
			double error = sum - coarseFlux[i];
			crossingError += error * error;
			crossingReference += coarseFlux[i] * coarseFlux[i];
		}
		return (Relative(fineError, fineReference), Relative(parentError, parentReference), Relative(crossingError, crossingReference));

		static double Relative(double error, double reference) => error == 0 ? 0 : Math.Sqrt(error / reference);
	}

	// Shift flux along each coarse crossing while preserving its sum, then rebalance its children.
	// An exact line search minimizes the length-weighted squared change in normal wind along this step.
	void Redistribute(double[] initial, double[] flux, double[] potential, int passes, CancellationToken cancellation)
	{
		var direction = new double[flux.Length];
		var directionPotential = new double[potential.Length];
		var deficit = new double[potential.Length];
		for (int pass = 0; pass < passes; pass++)
		{
			cancellation.ThrowIfCancellationRequested();
			Array.Clear(direction);
			Array.Clear(directionPotential);
			Array.Clear(deficit);
			foreach (var links in _crossings)
			{
				double sum = 0, length = 0;
				foreach (var link in links)
				{
					var face = _faces[link.Index];
					double gradient = (flux[link.Index] - initial[link.Index]) / face.LengthM
						- (potential[face.Left] - potential[face.Right]);
					direction[link.Index] = -face.LengthM * gradient;
					sum += link.Sign * direction[link.Index];
					length += face.LengthM;
				}
				foreach (var link in links)
					direction[link.Index] -= link.Sign * _faces[link.Index].LengthM * sum / length;
			}
			AccumulateNet(direction, deficit, -1);
			foreach (var correction in _corrections) correction.Apply(deficit, direction, directionPotential);
			double numerator = 0, denominator = 0;
			for (int i = 0; i < flux.Length; i++)
			{
				double weightedStep = direction[i] / _faces[i].LengthM;
				numerator -= (flux[i] - initial[i]) * weightedStep;
				denominator += direction[i] * weightedStep;
			}
			if (!(numerator > 0) || !(denominator > 0)) break;
			double scale = numerator / denominator;
			for (int i = 0; i < flux.Length; i++) flux[i] += scale * direction[i];
			for (int i = 0; i < potential.Length; i++) potential[i] += scale * directionPotential[i];
		}
	}

	void AccumulateNet(double[] flux, double[] net, int sign)
	{
		for (int i = 0; i < _faces.Length; i++)
		{
			double amount = sign * flux[i];
			net[_faces[i].Left] += amount;
			net[_faces[i].Right] -= amount;
		}
	}

	public Vector3[] BuildWind(double[] flux, float referenceSpeed, int[] upstream = null,
		float[] transportSpeed = null, bool physical = false)
	{
		if (upstream != null)
		{
			Array.Fill(upstream, -1);
			Array.Clear(transportSpeed);
		}
		var incoming = upstream == null ? null : new double[_area.Length];
		var fitEast = new double[_area.Length];
		var fitNorth = new double[_area.Length];
		for (int i = 0; i < flux.Length; i++)
		{
			var face = _faces[i];
			double amount = flux[i];
			int receiving = amount > 0 ? face.Right : face.Left;
			if (incoming != null && Math.Abs(amount) > incoming[receiving])
			{
				incoming[receiving] = Math.Abs(amount);
				upstream[receiving] = amount > 0 ? face.Left : face.Right;
				transportSpeed[receiving] = (float)(1 - Math.Exp(-Math.Abs(amount) / face.LengthM / referenceSpeed));
			}
			fitEast[face.Left] += amount * face.Normal.Dot(_east[face.Left]);
			fitEast[face.Right] += amount * face.Normal.Dot(_east[face.Right]);
			fitNorth[face.Left] += amount * face.Normal.Dot(_north[face.Left]);
			fitNorth[face.Right] += amount * face.Normal.Dot(_north[face.Right]);
		}
		var wind = new Vector3[_area.Length];
		for (int i = 0; i < wind.Length; i++)
		{
			var cell = _velocityFit[i];
			float east = (float)(cell.EastEast * fitEast[i] + cell.EastNorth * fitNorth[i]);
			float north = (float)(cell.EastNorth * fitEast[i] + cell.NorthNorth * fitNorth[i]);
			var velocity = _east[i] * east + _north[i] * north;
			velocity -= _radial[i] * velocity.Dot(_radial[i]);
			float speed = velocity.Length();
			if (physical) wind[i] = velocity;
			else if (speed > 0) wind[i] = velocity / speed * (1 - MathF.Exp(-speed / referenceSpeed));
		}
		return wind;
	}

	public float[] BuildWindCurl(Vector3[] annualWind)
	{
		var curl = new float[annualWind.Length];
		var vertexWind = new Vector3[_vertexFit.Length];
		for (int i = 0; i < vertexWind.Length; i++)
		{
			var fit = _vertexFit[i];
			vertexWind[i] = annualWind[fit.First] * fit.FirstWeight + annualWind[fit.Second] * fit.SecondWeight
				+ annualWind[fit.Third] * fit.ThirdWeight;
		}
		for (int i = 0; i < _faces.Length; i++)
		{
			var face = _faces[i];
			// Tangential stress is estimated from the flux-constrained vector fit.
			// For a regular hexagon the legacy neighbor-ring integral divided by six
			// equals half the circulation around its cell boundary in unit-sphere coordinates.
			float circulation = (vertexWind[face.FirstVertex] + vertexWind[face.SecondVertex]).Dot(_boundaryChord[i]) * 0.25f;
			curl[face.Left] += circulation;
			curl[face.Right] -= circulation;
		}
		return curl;
	}

	sealed class ParentCorrection
	{
		readonly record struct LocalFace(int Index, int Left, int Right, double Weight);
		readonly int[] _cells;
		readonly LocalFace[] _faces;
		readonly double[] _factor;

		public ParentCorrection(List<int> cells, List<int> faces, CirculationOperator.Edge[] geometry)
		{
			_cells = cells.ToArray();
			var index = cells.Select((cell, i) => (cell, i)).ToDictionary();
			_faces = faces.Select(face =>
			{
				var edge = geometry[face];
				return new LocalFace(face, index[edge.Left], index[edge.Right], edge.LengthM);
			}).ToArray();
			// One fixed potential removes the constant null space of the local graph Laplacian.
			int n = _cells.Length - 1;
			_factor = new double[n * n];
			foreach (var face in _faces)
			{
				int i = face.Left, j = face.Right;
				if (i < n) _factor[i * n + i] += face.Weight;
				if (j < n) _factor[j * n + j] += face.Weight;
				if (i < n && j < n) _factor[Math.Max(i, j) * n + Math.Min(i, j)] -= face.Weight;
			}
			for (int i = 0; i < n; i++)
			for (int j = 0; j <= i; j++)
			{
				double value = _factor[i * n + j];
				for (int k = 0; k < j; k++) value -= _factor[i * n + k] * _factor[j * n + k];
				if (i == j && !(value > 0)) throw new InvalidOperationException("A parent's fine graph is disconnected.");
				_factor[i * n + j] = i == j ? Math.Sqrt(value) : value / _factor[j * n + j];
			}
		}

		public void Apply(double[] deficit, double[] flux, double[] potential = null)
		{
			int n = _cells.Length - 1;
			Span<double> correction = stackalloc double[_cells.Length];
			correction[n] = 0;
			for (int i = 0; i < n; i++)
			{
				double value = deficit[_cells[i]];
				for (int j = 0; j < i; j++) value -= _factor[i * n + j] * correction[j];
				correction[i] = value / _factor[i * n + i];
			}
			for (int i = n - 1; i >= 0; i--)
			{
				double value = correction[i];
				for (int j = i + 1; j < n; j++) value -= _factor[j * n + i] * correction[j];
				correction[i] = value / _factor[i * n + i];
			}
			foreach (var face in _faces)
				flux[face.Index] += face.Weight * (correction[face.Left] - correction[face.Right]);
			if (potential != null)
				for (int i = 0; i < n; i++) potential[_cells[i]] += correction[i];
		}
	}
}
