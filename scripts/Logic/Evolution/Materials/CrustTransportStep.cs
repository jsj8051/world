using System;
using System.Buffers;
using System.Collections.Generic;

namespace World.Logic;

/// <summary>把本子步求得的局部面积通量叠加到刚体通量，并重新核对出流稳定性。</summary>
internal sealed class CrustTransportStep : IDisposable
{
	readonly record struct FaceFractions(int PlateA, int PlateB, double FractionA, double FractionB);
	readonly CrustTransportFluxes _rigid;
	readonly Dictionary<int, FaceFractions> _local;
	readonly Dictionary<long, double> _retained;
	readonly byte[] _affectedFaces;
	readonly byte[] _affectedCells;
	readonly double _scale = 1;

	public int Count { get; }
	public double StepMa => _rigid.StepMa * _scale;

	public CrustTransportStep(CrustTransportFluxes rigid, ContinentalDeformation deformation)
	{
		_rigid = rigid;
		Count = rigid.StepCount;
		if (deformation == null || deformation.Faces.Count == 0) return;
		var grid = rigid.Grid;
		_local = new Dictionary<int, FaceFractions>(deformation.Faces.Count);
		_retained = new Dictionary<long, double>();
		foreach (var (face, motion) in deformation.Faces)
		{
			var edge = grid.Faces[face];
			double a = Update(face, edge, motion.PlateA, motion.RateA);
			double b = motion.PlateB < 0 ? 0 : Update(face, edge, motion.PlateB, motion.RateB);
			_local.Add(face, new FaceFractions(motion.PlateA, motion.PlateB, a, b));
		}
		double maxOutgoing = 0;
		foreach (double retained in _retained.Values) maxOutgoing = Math.Max(maxOutgoing, 1 - retained);
		int additionalSteps = checked((int)Math.Max(1, Math.Ceiling(maxOutgoing / 0.9)));
		Count = checked(Count * additionalSteps);
		_scale = 1d / additionalSteps;
		_affectedFaces = ArrayPool<byte>.Shared.Rent(grid.Faces.Length);
		_affectedCells = ArrayPool<byte>.Shared.Rent(grid.CellCount);
		Array.Clear(_affectedFaces, 0, grid.Faces.Length);
		Array.Clear(_affectedCells, 0, grid.CellCount);
		foreach (int face in _local.Keys) _affectedFaces[face] = 1;
		foreach (long key in _retained.Keys) _affectedCells[(int)(key >> 32)] = 1;

		double Update(int face, EvolutionFace edge, int plate, double rate)
		{
			double original = rigid.SignedFraction(face, plate);
			int newSource = rate >= 0 ? edge.A : edge.B;
			double fraction = rate * rigid.StepMa / grid.AreasKm2[newSource];
			Adjust(original >= 0 ? edge.A : edge.B, plate, Math.Abs(original));
			Adjust(newSource, plate, -Math.Abs(fraction));
			return fraction;
		}

		void Adjust(int cell, int plate, double change)
		{
			long key = Key(cell, plate);
			if (!_retained.TryGetValue(key, out double value)) value = rigid.RetainedFraction(cell, plate);
			_retained[key] = value + change;
		}
	}

	public bool HasLocalVelocity(int face) => _affectedFaces != null && _affectedFaces[face] != 0;

	public double SignedFraction(int face, int plate)
	{
		if (HasLocalVelocity(face))
		{
			var motion = _local[face];
			if (plate == motion.PlateA) return motion.FractionA * _scale;
			if (plate == motion.PlateB) return motion.FractionB * _scale;
		}
		return _rigid.SignedFraction(face, plate) * _scale;
	}

	public double RetainedFraction(int cell, int plate)
	{
		if (_affectedCells == null) return _rigid.RetainedFraction(cell, plate);
		if (_affectedCells[cell] == 0 || !_retained.TryGetValue(Key(cell, plate), out double retained))
			retained = _rigid.RetainedFraction(cell, plate);
		return 1 - (1 - retained) * _scale;
	}

	public void Dispose()
	{
		if (_affectedFaces == null) return;
		ArrayPool<byte>.Shared.Return(_affectedFaces);
		ArrayPool<byte>.Shared.Return(_affectedCells);
	}

	static long Key(int cell, int plate) => ((long)cell << 32) | (uint)plate;
}
