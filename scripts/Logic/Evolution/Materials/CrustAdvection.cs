using System;
using System.Collections.Generic;
using World.Data;

namespace World.Logic;

/// <summary>用共享边通量搬运材料，再结算最后一个输运子步的构造过程。</summary>
internal sealed class CrustAdvection
{
	readonly CrustTransportFluxes _fluxes;
	readonly ContinentalFluxCorrection _continentalCorrection;

	public CrustAdvection(EvolutionGrid grid, IReadOnlyList<PlateMotion> motions, double deltaMa)
	{
		_fluxes = new CrustTransportFluxes(grid, motions, deltaMa);
		if (_fluxes.StepCount > 0)
			_continentalCorrection = new ContinentalFluxCorrection(_fluxes, motions);
	}

	public CrustRows Move(EvolutionCrust crust, Action<int, CrustRowBuffer> settle = null, ContinentalDeformation deformation = null)
	{
		CrustRows current = crust.Rows;
		EvolutionGrid grid = _fluxes.Grid;
		if (_fluxes.StepCount == 0)
		{
			if (settle == null) return current;
			return CrustRows.Build(grid.CellCount, current.SlotsPerCell, current, (cell, row) =>
			{
				foreach (ref readonly CrustEntry entry in current.Row(cell))
					row.Set(entry.Key, entry.Slot);
				settle(cell, row);
			});
		}

		using var transport = new CrustTransportStep(_fluxes, deformation);
		using var correction = _continentalCorrection.CreateStep(transport);
		for (int step = 0; step < transport.Count; step++)
		{
			bool last = step == transport.Count - 1;
			correction.Prepare(current);
			current = CrustRows.Build(grid.CellCount, current.SlotsPerCell, current, (cell, row) =>
			{
				foreach (ref readonly CrustEntry entry in current.Row(cell))
					if (entry.Slot.Area > 0)
						row.Add(entry.Key, entry.Slot, transport.RetainedFraction(cell, entry.Key / 2));

				foreach (int face in _fluxes.FacesOfCell(cell))
				{
					ref readonly EvolutionFace edge = ref grid.Faces[face];
					bool sourceIsA = cell == edge.B;
					int neighbor = sourceIsA ? edge.A : edge.B;
					foreach (ref readonly CrustEntry entry in current.Row(neighbor))
					{
						double fraction = transport.SignedFraction(face, entry.Key / 2);
						double incoming = sourceIsA ? fraction : -fraction;
						if (incoming > 0 && entry.Slot.Area > 0)
							row.Add(entry.Key, entry.Slot, incoming);
					}
				}

				correction.Apply(cell, row);
				if (last) settle?.Invoke(cell, row);
			});
		}
		return current;
	}
}
