using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

// Face flux is signed from Left to Right and has units m²/s per unit layer depth.
internal sealed class ConservativeFaceTransport
{
	readonly record struct Link(int Index, int Sign);
	readonly record struct Crossing(Link[] Links, int Offset, double Length);
	readonly record struct Fit(double EastEast, double EastNorth, double NorthNorth);
	readonly record struct VertexFit(int First, int Second, int Third, float FirstWeight, float SecondWeight, float ThirdWeight);
	readonly CirculationOperator.Edge[] _faces;
	readonly CirculationOperator.Edge[] _coarseFaces;
	readonly Crossing[] _crossings;
	readonly int _crossingFaceCount;
	readonly Link[] _unmatchedCrossings;
	readonly int[] _parent;
	readonly double[] _area;
	readonly double[] _parentArea;
	readonly ParentCorrection[] _corrections;
	readonly int[] _interfaceCells;
	readonly int[] _reducedCells;
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
		var onInterface = new bool[parent.Length];
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
				if (lookup.TryGetValue(key, out int coarseIndex))
				{
					crossed[coarseIndex].Add(link);
					onInterface[face.Left] = onInterface[face.Right] = true;
				}
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
		_crossings = new Crossing[crossed.Length];
		int offset = 0;
		for (int i = 0; i < crossed.Length; i++)
		{
			var links = crossed[i].ToArray();
			if (links.Length == 0) throw new InvalidOperationException("A coarse face has no fine crossings.");
			_crossings[i] = new Crossing(links, offset, links.Sum(link => _faces[link.Index].LengthM));
			offset += links.Length;
		}
		_crossingFaceCount = offset;
		_unmatchedCrossings = unmatched.ToArray();
		_interfaceCells = Enumerable.Range(0, parent.Length).Where(cell => onInterface[cell]).ToArray();
		_corrections = children.Select((cells, i) =>
		{
			cancellation.ThrowIfCancellationRequested();
			return new ParentCorrection(cells, internalFaces[i], _faces, onInterface);
		}).ToArray();
		_reducedCells = _corrections.SelectMany(correction => correction.ReducedCells).ToArray();
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
		// Only crossing fluxes are updated during redistribution. Interior corrections stay in potential form.
		var initialCrossings = redistributionPasses > 0 ? new double[_crossingFaceCount] : null;
		var coarseNet = new double[_parentArea.Length];
		var provisionalParentNet = new double[_parentArea.Length];
		for (int i = 0; i < desiredNet.Length; i++) provisionalParentNet[_parent[i]] += desiredNet[i];
		for (int i = 0; i < _crossings.Length; i++)
		{
			var face = _coarseFaces[i];
			coarseNet[face.Left] += coarseFlux[i];
			coarseNet[face.Right] -= coarseFlux[i];
			var crossing = _crossings[i];
			double net = 0;
			foreach (var link in crossing.Links) net += link.Sign * flux[link.Index];
			double adjustment = (coarseFlux[i] - net) / crossing.Length;
			for (int j = 0; j < crossing.Links.Length; j++)
			{
				var link = crossing.Links[j];
				if (initialCrossings != null) initialCrossings[crossing.Offset + j] = flux[link.Index];
				flux[link.Index] += link.Sign * adjustment * _faces[link.Index].LengthM;
			}
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
		var potential = new double[desiredNet.Length];
		foreach (var correction in _corrections) correction.Prepare(deficit, potential);
		if (initialCrossings != null) Redistribute(initialCrossings, flux, potential, redistributionPasses, cancellation);
		foreach (var correction in _corrections) correction.AddFlux(potential, flux);

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
			foreach (var link in _crossings[i].Links) sum += link.Sign * flux[link.Index];
			double error = sum - coarseFlux[i];
			crossingError += error * error;
			crossingReference += coarseFlux[i] * coarseFlux[i];
		}
		return (Relative(fineError, fineReference), Relative(parentError, parentReference), Relative(crossingError, crossingReference));

		static double Relative(double error, double reference) => error == 0 ? 0 : Math.Sqrt(error / reference);
	}

	// Shift flux along each coarse crossing while preserving its sum. For a crossing step u,
	// an interior potential q solves Lq = -B_cross u, where B is the signed cell/face incidence.
	// Orthogonal projection makes the line search numerator the squared crossing norm.
	// Its curvature adds q^T Lq, which is a dot product on the interface because Lq vanishes
	// inside each parent. Recover the eliminated interior potentials after all line searches.
	void Redistribute(double[] initial, double[] flux, double[] potential, int passes, CancellationToken cancellation)
	{
		var direction = new double[_crossingFaceCount];
		var directionPotential = new double[potential.Length];
		var deficit = new double[potential.Length];
		for (int pass = 0; pass < passes; pass++)
		{
			cancellation.ThrowIfCancellationRequested();
			foreach (int cell in _interfaceCells) deficit[cell] = 0;
			double crossingEnergy = 0;
			foreach (var crossing in _crossings)
			{
				double sum = 0;
				for (int j = 0; j < crossing.Links.Length; j++)
				{
					var link = crossing.Links[j];
					int index = crossing.Offset + j;
					var face = _faces[link.Index];
					direction[index] = initial[index] - flux[link.Index]
						+ face.LengthM * (potential[face.Left] - potential[face.Right]);
					sum += link.Sign * direction[index];
				}
				double mean = sum / crossing.Length;
				for (int j = 0; j < crossing.Links.Length; j++)
				{
					var link = crossing.Links[j];
					int index = crossing.Offset + j;
					var face = _faces[link.Index];
					double step = direction[index] - link.Sign * face.LengthM * mean;
					direction[index] = step;
					deficit[face.Left] -= step;
					deficit[face.Right] += step;
					crossingEnergy += step * step / face.LengthM;
				}
			}
			if (!(crossingEnergy > 0)) break;
			foreach (var correction in _corrections) correction.SolveInterface(deficit, directionPotential);
			double interiorEnergy = 0;
			foreach (int cell in _interfaceCells)
				interiorEnergy += directionPotential[cell] * deficit[cell];
			double scale = crossingEnergy / (crossingEnergy + interiorEnergy);
			foreach (var crossing in _crossings)
				for (int j = 0; j < crossing.Links.Length; j++)
					flux[crossing.Links[j].Index] += scale * direction[crossing.Offset + j];
			foreach (int cell in _reducedCells) potential[cell] += scale * directionPotential[cell];
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
		readonly int[] _interface;
		readonly GroundedGraphLaplacian _factor;
		public int[] ReducedCells { get; }

		public ParentCorrection(List<int> cells, List<int> faces, CirculationOperator.Edge[] geometry, bool[] onInterface)
		{
			_cells = cells.ToArray();
			var index = cells.Select((cell, i) => (cell, i)).ToDictionary();
			_faces = faces.Select(face =>
			{
				var edge = geometry[face];
				return new LocalFace(face, index[edge.Left], index[edge.Right], edge.LengthM);
			}).ToArray();
			var retained = _cells.Select(cell => onInterface[cell]).ToArray();
			_interface = Enumerable.Range(0, _cells.Length).Where(cell => retained[cell]).ToArray();
			_factor = new GroundedGraphLaplacian(_cells.Length,
				_faces.Select(face => (face.Left, face.Right, face.Weight)), retained);
			ReducedCells = _factor.ReducedVertices.ToArray();
			for (int i = 0; i < ReducedCells.Length; i++) ReducedCells[i] = _cells[ReducedCells[i]];
		}

		public void Prepare(double[] deficit, double[] state)
		{
			Span<double> correction = stackalloc double[_cells.Length];
			for (int i = 0; i < _cells.Length; i++) correction[i] = deficit[_cells[i]];
			_factor.Reduce(correction);
			_factor.SolveReduced(correction);
			for (int i = 0; i < _cells.Length; i++) state[_cells[i]] = correction[i];
		}

		public void SolveInterface(double[] deficit, double[] potential)
		{
			Span<double> correction = stackalloc double[_cells.Length];
			correction.Clear();
			foreach (int cell in _interface) correction[cell] = deficit[_cells[cell]];
			_factor.SolveReduced(correction);
			foreach (int cell in _factor.ReducedVertices) potential[_cells[cell]] = correction[cell];
		}

		public void AddFlux(double[] state, double[] flux)
		{
			Span<double> correction = stackalloc double[_cells.Length];
			for (int i = 0; i < _cells.Length; i++) correction[i] = state[_cells[i]];
			_factor.RecoverEliminated(correction);
			foreach (var face in _faces)
				flux[face.Index] += face.Weight * (correction[face.Left] - correction[face.Right]);
		}
	}
}
