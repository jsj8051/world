using System;
using System.Collections.Generic;
using World.Data;

namespace World.Logic;

/// <summary>板块旋转给出的跨边面积比例和满足出流限制的输运子步。</summary>
internal sealed class CrustTransportFluxes
{
	readonly int _plateCount;
	readonly double[] _edgeFractions;
	readonly double[] _retainedFractions;
	readonly int[] _faceStart;
	readonly int[] _cellFaces;

	public EvolutionGrid Grid { get; }
	public int StepCount { get; }
	public double StepMa { get; }

	public CrustTransportFluxes(EvolutionGrid grid, IReadOnlyList<PlateMotion> motions, double deltaMa)
	{
		if (!(deltaMa > 0) || !double.IsFinite(deltaMa))
			throw new ArgumentOutOfRangeException(nameof(deltaMa));
		Grid = grid;
		_plateCount = motions.Count;
		_edgeFractions = new double[checked(grid.Faces.Length * _plateCount)];
		_retainedFractions = new double[checked(grid.CellCount * _plateCount)];
		for (int face = 0; face < grid.Faces.Length; face++)
		{
			EvolutionFace edge = grid.Faces[face];
			for (int plate = 0; plate < _plateCount; plate++)
			{
				double rate = edge.Rate(motions[plate].OmegaRadPerMa);
				_edgeFractions[face * _plateCount + plate] = rate;
				int source = rate >= 0 ? edge.A : edge.B;
				_retainedFractions[source * _plateCount + plate] += Math.Abs(rate);
			}
		}
		double maxFractionPerMa = 0;
		for (int cell = 0; cell < grid.CellCount; cell++)
			for (int plate = 0; plate < _plateCount; plate++)
			{
				int index = cell * _plateCount + plate;
				_retainedFractions[index] /= grid.AreasKm2[cell];
				maxFractionPerMa = Math.Max(maxFractionPerMa, _retainedFractions[index]);
			}
		if (maxFractionPerMa == 0) return;

		StepCount = checked((int)Math.Max(1, Math.Ceiling(deltaMa * maxFractionPerMa / 0.9)));
		StepMa = deltaMa / StepCount;
		for (int i = 0; i < _retainedFractions.Length; i++)
			_retainedFractions[i] = 1 - _retainedFractions[i] * StepMa;
		for (int face = 0; face < grid.Faces.Length; face++)
		{
			EvolutionFace edge = grid.Faces[face];
			for (int plate = 0; plate < _plateCount; plate++)
			{
				int index = face * _plateCount + plate;
				double rate = _edgeFractions[index];
				int source = rate >= 0 ? edge.A : edge.B;
				_edgeFractions[index] = Math.CopySign(Math.Abs(rate) * StepMa / grid.AreasKm2[source], rate);
			}
		}

		// 入流和修正都按全局边序汇总，保持可重演。
		_faceStart = new int[grid.CellCount + 1];
		foreach (ref readonly EvolutionFace edge in grid.Faces.AsSpan())
		{
			_faceStart[edge.A + 1]++;
			_faceStart[edge.B + 1]++;
		}
		for (int cell = 0; cell < grid.CellCount; cell++) _faceStart[cell + 1] += _faceStart[cell];
		_cellFaces = new int[_faceStart[grid.CellCount]];
		var cursor = (int[])_faceStart.Clone();
		for (int face = 0; face < grid.Faces.Length; face++)
		{
			ref readonly EvolutionFace edge = ref grid.Faces[face];
			_cellFaces[cursor[edge.A]++] = face;
			_cellFaces[cursor[edge.B]++] = face;
		}
	}

	public double RetainedFraction(int cell, int plate) => _retainedFractions[cell * _plateCount + plate];
	public double SignedFraction(int face, int plate) => _edgeFractions[face * _plateCount + plate];
	public ReadOnlySpan<int> FacesOfCell(int cell) => _cellFaces.AsSpan(_faceStart[cell], _faceStart[cell + 1] - _faceStart[cell]);
}
