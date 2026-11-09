using System;
using System.Collections.Generic;
using World.Data;

namespace World.Logic;

/// <summary>一阶迎风输运。面积、体积及加权历史共用同一份跨边通量。</summary>
internal sealed class CrustAdvection
{
	readonly EvolutionGrid _grid;
	readonly int _plateCount;
	readonly double[] _edgeFraction;
	readonly double[] _remaining;
	readonly int[] _faceStart;
	readonly int[] _cellFaces;
	readonly int _substeps;

	public CrustAdvection(EvolutionGrid grid, IReadOnlyList<PlateMotion> motions, double deltaMa)
	{
		if (!(deltaMa > 0) || !double.IsFinite(deltaMa))
			throw new ArgumentOutOfRangeException(nameof(deltaMa));
		_grid = grid;
		_plateCount = motions.Count;
		_edgeFraction = new double[checked(grid.Faces.Length * _plateCount)];
		_remaining = new double[checked(grid.CellCount * _plateCount)];
		for (int face = 0; face < grid.Faces.Length; face++)
		{
			EvolutionFace edge = grid.Faces[face];
			for (int plate = 0; plate < _plateCount; plate++)
			{
				double rate = edge.Rate(motions[plate].OmegaRadPerMa);
				_edgeFraction[face * _plateCount + plate] = rate;
				int source = rate >= 0 ? edge.A : edge.B;
				_remaining[source * _plateCount + plate] += Math.Abs(rate);
			}
		}
		double maxFractionPerMa = 0;
		for (int cell = 0; cell < grid.CellCount; cell++)
			for (int plate = 0; plate < _plateCount; plate++)
			{
				int index = cell * _plateCount + plate;
				_remaining[index] /= grid.AreasKm2[cell];
				maxFractionPerMa = Math.Max(maxFractionPerMa, _remaining[index]);
			}
		if (maxFractionPerMa == 0) return;

		_substeps = checked((int)Math.Max(1, Math.Ceiling(deltaMa * maxFractionPerMa / 0.9)));
		double dt = deltaMa / _substeps;
		for (int i = 0; i < _remaining.Length; i++)
			_remaining[i] = 1 - _remaining[i] * dt;
		for (int face = 0; face < grid.Faces.Length; face++)
		{
			EvolutionFace edge = grid.Faces[face];
			for (int plate = 0; plate < _plateCount; plate++)
			{
				int index = face * _plateCount + plate;
				double rate = _edgeFraction[index];
				int source = rate >= 0 ? edge.A : edge.B;
				_edgeFraction[index] = Math.CopySign(Math.Abs(rate) * dt / grid.AreasKm2[source], rate);
			}
		}

		// 每格保留全局边序。先加本格余量，再按原顺序接收入流。
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

	public CrustRows Move(EvolutionCrust crust, Action<int, CrustRowBuffer> settle = null)
	{
		CrustRows current = crust.Rows;
		if (_substeps == 0)
		{
			if (settle == null) return current;
			return CrustRows.Build(_grid.CellCount, current.SlotsPerCell, current, (cell, row) =>
			{
				foreach (ref readonly CrustEntry entry in current.Row(cell))
					row.Set(entry.Key, entry.Slot);
				settle(cell, row);
			});
		}

		for (int step = 1; ; step++)
		{
			bool last = step == _substeps;
			current = CrustRows.Build(_grid.CellCount, current.SlotsPerCell, current, (cell, row) =>
			{
				int remainingOffset = cell * _plateCount;
				foreach (ref readonly CrustEntry entry in current.Row(cell))
					if (entry.Slot.Area > 0)
						row.Add(entry.Key, entry.Slot, _remaining[remainingOffset + entry.Key / 2]);

				for (int i = _faceStart[cell]; i < _faceStart[cell + 1]; i++)
				{
					int face = _cellFaces[i];
					ref readonly EvolutionFace edge = ref _grid.Faces[face];
					bool sourceIsA = cell == edge.B;
					int neighbor = sourceIsA ? edge.A : edge.B;
					int edgeOffset = face * _plateCount;
					foreach (ref readonly CrustEntry entry in current.Row(neighbor))
					{
						double fraction = _edgeFraction[edgeOffset + entry.Key / 2];
						double incoming = sourceIsA ? fraction : -fraction;
						if (incoming > 0 && entry.Slot.Area > 0)
							row.Add(entry.Key, entry.Slot, incoming);
					}
				}

				if (last) settle?.Invoke(cell, row);
			});
			if (last) return current;
		}
	}
}
