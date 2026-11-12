using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.Intrinsics;
using World.Data;
using Tracers = System.Runtime.Intrinsics.Vector256<double>;

namespace World.Logic;

/// <summary>在同板块陆壳内部，用限幅通量修正恢复输运中的厚度和高程梯度。</summary>
internal sealed class ContinentalFluxCorrection
{
	const double InteriorCoverage = 0.99;
	const int ReferenceThickness = 0;
	const int ThicknessAnomaly = 1;
	const int CollisionThickness = 2;
	const int ReferenceElevation = 3;

	readonly CrustTransportFluxes _fluxes;
	readonly TangentBasis[] _bases;
	readonly Direction[] _faceMidpoints;
	readonly Rotation[] _backRotations;
	readonly IReadOnlyList<PlateMotion> _motions;

	public ContinentalFluxCorrection(CrustTransportFluxes fluxes, IReadOnlyList<PlateMotion> motions)
	{
		_fluxes = fluxes;
		_motions = motions;
		EvolutionGrid grid = fluxes.Grid;
		_bases = new TangentBasis[grid.CellCount];
		for (int cell = 0; cell < grid.CellCount; cell++)
			_bases[cell] = TangentBasis.At(Direction.From(grid.Centers[cell]));
		_faceMidpoints = new Direction[grid.Faces.Length];
		for (int face = 0; face < grid.Faces.Length; face++)
		{
			ref readonly var edge = ref grid.Faces[face];
			_faceMidpoints[face] = (Direction.From(edge.Start) + Direction.From(edge.End)).Unit();
		}
		_backRotations = new Rotation[motions.Count];
		for (int plate = 0; plate < motions.Count; plate++)
			_backRotations[plate] = Rotation.BackHalfStep(motions[plate].OmegaRadPerMa, fluxes.StepMa);
	}

	public Step CreateStep(CrustTransportStep transport) => new(this, transport);

	// 每次 Move 独占工作区。共用求解器的候选帧可以并发计算。
	internal sealed class Step : IDisposable
	{
		readonly ContinentalFluxCorrection _solver;
		readonly CrustTransportFluxes _fluxes;
		readonly CrustTransportStep _transport;
		readonly Rotation[] _backRotations;
		readonly EvolutionGrid _grid;
		readonly Sample[] _samples;
		readonly Gradient[] _gradients;
		readonly Tracers[] _faceCorrections;
		readonly Allowance[] _allowances;

		public Step(ContinentalFluxCorrection solver, CrustTransportStep transport)
		{
			_solver = solver;
			_fluxes = solver._fluxes;
			_transport = transport;
			_backRotations = solver._backRotations;
			if (_transport.StepMa != _fluxes.StepMa)
			{
				_backRotations = new Rotation[solver._motions.Count];
				for (int plate = 0; plate < _backRotations.Length; plate++)
					_backRotations[plate] = Rotation.BackHalfStep(solver._motions[plate].OmegaRadPerMa, transport.StepMa);
			}
			_grid = _fluxes.Grid;
			_samples = ArrayPool<Sample>.Shared.Rent(_grid.CellCount);
			_gradients = ArrayPool<Gradient>.Shared.Rent(_grid.CellCount);
			_faceCorrections = ArrayPool<Tracers>.Shared.Rent(_grid.Faces.Length);
			_allowances = ArrayPool<Allowance>.Shared.Rent(_grid.CellCount);
		}

		public void Prepare(CrustRows source)
		{
			EvolutionWork.ForCells(_grid.CellCount, (start, end) =>
			{
				for (int cell = start; cell < end; cell++)
				{
					_samples[cell] = new Sample(-1, 0, Tracers.Zero);
					double minimumArea = InteriorCoverage * _grid.AreasKm2[cell];
					foreach (ref readonly var entry in source.Row(cell))
					{
						if (entry.Key % 2 != (int)CrustType.Continental || entry.Slot.Area < minimumArea) continue;
						_samples[cell] = new Sample(entry.Key, entry.Slot.Area, Amounts(entry.Slot) * (1 / entry.Slot.Area));
						break;
					}
				}
			});
			EvolutionWork.ForCells(_grid.CellCount, (start, end) =>
			{
				for (int cell = start; cell < end; cell++)
					if (_samples[cell].Key >= 0) _gradients[cell] = FitGradient(cell);
			});
			EvolutionWork.ForCells(_grid.Faces.Length, (start, end) =>
			{
				for (int face = start; face < end; face++) _faceCorrections[face] = CandidateFlux(face);
			});
			EvolutionWork.ForCells(_grid.CellCount, (start, end) =>
			{
				for (int cell = start; cell < end; cell++)
					if (_samples[cell].Key >= 0) _allowances[cell] = MeasureAllowance(cell, source);
			});
		}

		Gradient FitGradient(int cell)
		{
			Sample center = _samples[cell];
			TangentBasis basis = _solver._bases[cell];
			double xx = 0, xy = 0, yy = 0;
			Tracers xValues = Tracers.Zero, yValues = Tracers.Zero;
			foreach (int neighbor in _grid.Neighbors[cell])
			{
				Sample sample = _samples[neighbor];
				if (sample.Key != center.Key) continue;
				var (x, y) = basis.Project(Direction.From(_grid.Centers[neighbor]));
				double weight = 1 / (x * x + y * y);
				if (!double.IsFinite(weight)) continue;
				Tracers difference = sample.Mean - center.Mean;
				// 常量场的除法舍入误差不应产生梯度或限制其他示踪量。
				Tracers roundoff = Vector256.Max(Vector256.Abs(sample.Mean), Vector256.Abs(center.Mean)) * 1e-13;
				difference = Vector256.ConditionalSelect(Vector256.GreaterThan(Vector256.Abs(difference), roundoff),
					difference, Tracers.Zero);
				xx += x * x * weight;
				xy += x * y * weight;
				yy += y * y * weight;
				xValues += difference * (x * weight);
				yValues += difference * (y * weight);
			}
			double determinant = xx * yy - xy * xy;
			if (determinant <= 1e-8 * xx * yy) return default;
			double inverse = 1 / determinant;
			return new Gradient((xValues * yy - yValues * xy) * inverse, (yValues * xx - xValues * xy) * inverse);
		}

		Tracers CandidateFlux(int face)
		{
			if (_transport.HasLocalVelocity(face)) return Tracers.Zero;
			ref readonly var edge = ref _grid.Faces[face];
			int key = _samples[edge.A].Key;
			if (key < 0 || _samples[edge.B].Key != key) return Tracers.Zero;
			int plate = key / 2;
			double fraction = _transport.SignedFraction(face, plate);
			int upstream = fraction >= 0 ? edge.A : edge.B;
			Gradient gradient = _gradients[upstream];
			if (fraction == 0 || (gradient.X == Tracers.Zero && gradient.Y == Tracers.Zero)) return Tracers.Zero;
			Direction departure = _backRotations[plate].Apply(_solver._faceMidpoints[face]);
			var (x, y) = _solver._bases[upstream].Project(departure);
			// 正值修正沿 A → B 记账。面积通量取自迎风输运本身。
			return (gradient.X * x + gradient.Y * y) * (fraction * _samples[upstream].Area);
		}

		Allowance MeasureAllowance(int cell, CrustRows source)
		{
			Sample sample = _samples[cell];
			int key = sample.Key, plate = key / 2;
			ref readonly CrustSlot slot = ref source.Get(cell, key);
			double retained = _transport.RetainedFraction(cell, plate);
			double lowArea = slot.Area * retained;
			double lowSpareVolume = (slot.Volume - slot.VolcanicVolume) * retained;
			Tracers lowAmounts = Amounts(slot) * retained;
			Tracers min = sample.Mean, max = sample.Mean;
			Tracers gains = Tracers.Zero, losses = Tracers.Zero;
			double volumeLoss = 0;
			foreach (int face in _fluxes.FacesOfCell(cell))
			{
				ref readonly var edge = ref _grid.Faces[face];
				bool isA = cell == edge.A;
				int neighbor = isA ? edge.B : edge.A;
				Tracers correction = isA ? -_faceCorrections[face] : _faceCorrections[face];
				gains += Vector256.Max(correction, Tracers.Zero);
				losses += Vector256.Max(-correction, Tracers.Zero);
				volumeLoss += Math.Max(0, -VolumeChange(correction));

				double incoming = _transport.SignedFraction(face, plate) * (isA ? -1 : 1);
				if (_samples[neighbor].Key == key) ExpandBounds(_samples[neighbor].Mean);
				if (incoming <= 0) continue;
				ref readonly CrustSlot other = ref source.Get(neighbor, key);
				if (other.Area <= 0) continue;
				Tracers amounts = Amounts(other);
				lowArea += other.Area * incoming;
				lowAmounts += amounts * incoming;
				lowSpareVolume += (other.Volume - other.VolcanicVolume) * incoming;
				// 混合格仍可提供一阶入流。其材料浓度也属于当前格的可达范围。
				if (_samples[neighbor].Key != key) ExpandBounds(amounts * (1 / other.Area));
			}
			return new Allowance(Ratios(max * lowArea - lowAmounts, gains), Ratios(lowAmounts - min * lowArea, losses),
				Ratio(lowSpareVolume, volumeLoss));

			void ExpandBounds(Tracers mean)
			{
				min = Vector256.Min(min, mean);
				max = Vector256.Max(max, mean);
			}
		}

		public void Apply(int cell, CrustRowBuffer row)
		{
			int key = _samples[cell].Key;
			if (key < 0) return;
			Tracers change = Tracers.Zero;
			foreach (int face in _fluxes.FacesOfCell(cell))
			{
				Tracers correction = _faceCorrections[face];
				if (correction == Tracers.Zero) continue;
				ref readonly var edge = ref _grid.Faces[face];
				// 同一系数用于两端和所有字段，逐边等量借记与贷记。
				double factor = Math.Min(_allowances[edge.A].For(-correction), _allowances[edge.B].For(correction));
				change += correction * (cell == edge.A ? -factor : factor);
			}
			if (change == Tracers.Zero) return;
			ref CrustSlot slot = ref row.Slots[key];
			slot.Volume += VolumeChange(change);
			slot.ReferenceThicknessArea += change.GetElement(ReferenceThickness);
			slot.ReferenceHeightArea += change.GetElement(ReferenceElevation);
			slot.CollisionVolume += change.GetElement(CollisionThickness);
			// 限幅后只允许舍入误差跨过零。
			slot.CollisionVolume = Math.Max(0, slot.CollisionVolume);
		}

		public void Dispose()
		{
			ArrayPool<Sample>.Shared.Return(_samples);
			ArrayPool<Gradient>.Shared.Return(_gradients);
			ArrayPool<Tracers>.Shared.Return(_faceCorrections);
			ArrayPool<Allowance>.Shared.Return(_allowances);
		}
	}

	static Tracers Amounts(in CrustSlot slot) => Vector256.Create(slot.ReferenceThicknessArea,
		slot.Volume - slot.ReferenceThicknessArea, slot.CollisionVolume, slot.ReferenceHeightArea);

	static double VolumeChange(Tracers change) => change.GetElement(ReferenceThickness) + change.GetElement(ThicknessAnomaly);

	static double Ratio(double room, double requested) => requested <= 0 ? 1
		: Math.Clamp(room / requested * (1 - 1e-12), 0, 1);

	static Tracers Ratios(Tracers room, Tracers requested) => Vector256.Create(
		Ratio(room.GetElement(0), requested.GetElement(0)), Ratio(room.GetElement(1), requested.GetElement(1)),
		Ratio(room.GetElement(2), requested.GetElement(2)), Ratio(room.GetElement(3), requested.GetElement(3)));

	readonly record struct Sample(int Key, double Area, Tracers Mean);
	readonly record struct Gradient(Tracers X, Tracers Y);
	readonly record struct Allowance(Tracers Gain, Tracers Loss, double VolumeLoss)
	{
		public double For(Tracers change)
		{
			Tracers factors = Vector256.ConditionalSelect(Vector256.GreaterThan(change, Tracers.Zero), Gain, Loss);
			factors = Vector256.ConditionalSelect(Vector256.Equals(change, Tracers.Zero), Tracers.One, factors);
			double factor = Math.Min(Math.Min(factors.GetElement(0), factors.GetElement(1)),
				Math.Min(factors.GetElement(2), factors.GetElement(3)));
			return VolumeChange(change) < 0 ? Math.Min(factor, VolumeLoss) : factor;
		}
	}

	readonly record struct TangentBasis(Direction East, Direction North)
	{
		public static TangentBasis At(Direction center)
		{
			Direction axis = Math.Abs(center.Z) < 0.9 ? new Direction(0, 0, 1) : new Direction(1, 0, 0);
			Direction east = axis.Cross(center).Unit();
			return new TangentBasis(east, center.Cross(east));
		}

		public (double X, double Y) Project(Direction direction) => (East.Dot(direction), North.Dot(direction));
	}

	readonly record struct Rotation(Direction Axis, double Sin, double Cos)
	{
		public static Rotation BackHalfStep(SphereVectorD omega, double stepMa)
		{
			var axis = new Direction(omega.X, omega.Y, omega.Z);
			double speed = Math.Sqrt(axis.Dot(axis));
			if (speed == 0) return new Rotation(default, 0, 1);
			var (sin, cos) = Math.SinCos(-0.5 * stepMa * speed);
			return new Rotation(axis * (1 / speed), sin, cos);
		}

		public Direction Apply(Direction point) => point * Cos + Axis.Cross(point) * Sin
			+ Axis * (Axis.Dot(point) * (1 - Cos));
	}

	readonly record struct Direction(double X, double Y, double Z)
	{
		public static Direction From(SpherePointD point) => new(point.X, point.Y, point.Z);
		public static Direction operator +(Direction a, Direction b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
		public static Direction operator *(Direction a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
		public Direction Cross(Direction b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
		public double Dot(Direction b) => X * b.X + Y * b.Y + Z * b.Z;
		public Direction Unit() => this * (1 / Math.Sqrt(Dot(this)));
	}
}
