using System;
using System.Collections.Generic;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>把输运后的占地需求结算为固定面积。只有活动边界能触发物质交换。</summary>
internal sealed class CrustProcesses
{
	internal readonly record struct Flows(double Subducted, double NewOceanic, double Volcanic, double AreaCorrection);
	readonly record struct Contact(double Speed, int Under, bool Collision, bool Rift, bool OceanOpening, int A, int B)
	{
		public bool IsSubduction => Speed < 0 && Under >= 0;
	}
	readonly record struct CellFlow(double Subducted, double NewOceanic, double AreaCorrection,
		bool InvalidCoverage = false, double OceanVolcanic = 0, double LandVolcanic = 0, bool Overflow = false);
	readonly EvolutionGrid _grid;
	readonly EvolutionSpec _spec;
	readonly CrustProjection _before;
	readonly double _timeMa, _deltaMa;
	readonly double[] _volcanicRates;
	readonly Contact[] _contacts;
	readonly (int Plate, double Rate, bool Arc)[] _volcanoes;
	readonly CellFlow[] _cellFlows;

	public CrustProcesses(EvolutionGrid grid, EvolutionSpec spec, CrustProjection before,
		double timeMa, double deltaMa, double[] volcanicRates)
	{
		_grid = grid;
		_spec = spec;
		_before = before;
		_timeMa = timeMa;
		_deltaMa = deltaMa;
		_volcanicRates = volcanicRates;
		_contacts = new Contact[grid.CellCount];
		_volcanoes = new (int Plate, double Rate, bool Arc)[grid.CellCount];
		_cellFlows = new CellFlow[grid.CellCount];
		for (int i = 0; i < before.PlateBoundaries.Edges.Length; i++)
		{
			PlateBoundaryEdge edge = before.PlateBoundaries.Edges[i];
			double speed = before.Boundary.NormalSpeedKmPerMa[i];
			switch (before.Boundary.MotionKinds[i])
			{
				case EvolutionMotionKind.Convergent:
					var closing = new Contact(speed, before.Boundary.UnderthrustingPlateIds[i],
						before.Boundary.ConvergenceKinds[i] == ConvergenceKind.Collision, false, false, edge.PlateA, edge.PlateB);
					Assign(_contacts, edge.CellA, closing);
					Assign(_contacts, edge.CellB, closing);
					if (closing.Under >= 0)
					{
						int over = closing.Under == edge.PlateA ? edge.CellB : edge.CellA;
						double footprint = spec.ArcReachKm / (spec.ArcReachKm + grid.CellRadiusRad[over] * grid.RadiusKm);
						AssignVolcanism(over, spec.ArcVolcanicRateKmPerMa * footprint, arc: true);
					}
					break;
				case EvolutionMotionKind.Divergent:
					var (a, b) = before.Boundary.SideTypes(i, before.Crust.Type[edge.CellA], before.Crust.Type[edge.CellB]);
					bool continentalRift = a == CrustType.Continental && b == CrustType.Continental;
					AssignOpening(edge.CellA, a == CrustType.Oceanic, continentalRift);
					AssignOpening(edge.CellB, b == CrustType.Oceanic, continentalRift);
					break;
			}

			void AssignOpening(int cell, bool ocean, bool rift)
			{
				Assign(_contacts, cell, new Contact(speed, -1, false, rift, ocean, edge.PlateA, edge.PlateB));
				if (ocean) AssignVolcanism(cell, spec.RidgeVolcanicRateKmPerMa, arc: false);
			}

			void AssignVolcanism(int cell, double rate, bool arc)
			{
				rate *= Math.Min(1, Math.Abs(speed) / (4 * spec.ActiveSpeedKmPerMa));
				if (rate > _volcanoes[cell].Rate) _volcanoes[cell] = (before.PlateOfCell[cell], rate, arc);
			}
		}

	}

	public void SettleCell(int cell, CrustRowBuffer row)
	{
		CellFlow flow = SettleCoverage(cell, row);
		(int plate, double rate, bool arc) = _volcanoes[cell];
		if (!flow.InvalidCoverage && rate != 0)
		{
			double ocean = Grow(ref row.Slots[2 * plate + (int)CrustType.Oceanic]);
			double land = Grow(ref row.Slots[2 * plate + (int)CrustType.Continental]);
			flow = flow with { OceanVolcanic = ocean, LandVolcanic = land };
		}
		_cellFlows[cell] = flow;

		double Grow(ref CrustSlot slot)
		{
			if (slot.Area == 0) return 0;
			double volume = rate * _deltaMa * slot.Area;
			// 增生加厚现存洋壳，出生时间保持原值。
			slot.BirthVolume *= 1 + volume / slot.Volume;
			slot.Volume += volume;
			slot.VolcanicVolume += volume;
			if (arc) slot.ArcVolcanicVolume += volume;
			_volcanicRates[cell] += rate * slot.Area / _grid.AreasKm2[cell];
			return volume;
		}
	}

	public Flows CollectFlows()
	{
		// 按格号和原壳型顺序汇总，保持浮点求和顺序。
		double subducted = 0, newOcean = 0, volcanic = 0, areaCorrection = 0;
		for (int cell = 0; cell < _cellFlows.Length; cell++)
		{
			CellFlow flow = _cellFlows[cell];
			if (flow.InvalidCoverage)
				throw new EvolutionStepException(EvolutionProblemCode.InvalidCoverage, $"格 {cell} 材料覆盖非法");
			subducted += flow.Subducted;
			newOcean += flow.NewOceanic;
			volcanic += flow.OceanVolcanic;
			volcanic += flow.LandVolcanic;
			areaCorrection += flow.AreaCorrection;
		}
		return new Flows(subducted, newOcean, volcanic, areaCorrection);
	}

	public CrustRows ResolveOverflow(CrustRows rows, CancellationToken cancellation)
	{
		Dictionary<int, CrustRowBuffer> changed = null;
		int[] visited = null;
		var queue = new Queue<int>();
		for (int cell = 0; cell < _cellFlows.Length; cell++)
		{
			CellFlow flow = _cellFlows[cell];
			if (!flow.Overflow) continue;
			cancellation.ThrowIfCancellationRequested();
			changed ??= new();
			var source = Edit(cell);
			double capacity = _grid.AreasKm2[cell];
			double excess = Area(source) - capacity;
			double tolerance = capacity * Math.Min(_spec.CoverageRelativeTolerance, 1e-12);
			Contact contact = _contacts[cell];
			double removed = 0;

			// 沿同一俯冲带，用相邻格的下盘洋壳腾出等量空间，并把重叠材料转过去。
			if (contact.IsSubduction)
			{
				int slabKey = 2 * contact.Under + (int)CrustType.Oceanic;
				foreach (int neighbor in _grid.Neighbors[cell])
				{
					if (excess <= tolerance) break;
					Contact adjacent = _contacts[neighbor];
					if (!adjacent.IsSubduction || adjacent.Under != contact.Under) continue;
					double available = changed.TryGetValue(neighbor, out var row)
						? row.Slots[slabKey].Area : rows.Get(neighbor, slabKey).Area;
					double take = Math.Min(excess, available);
					if (take <= tolerance) continue;
					var target = Edit(neighbor);
					ref CrustSlot slab = ref target.Slots[slabKey];
					removed += take * slab.Thickness;
					slab.Remove(take);
					Transfer(source, target, take, continentalOnly: false);
					excess -= take;
				}
			}

			// 若陆壳已经占满本格，先把溢出的陆壳移入最近有洋壳余地的格。
			// 接收格只调洋壳占地，体积及俯冲账本保持独立。
			double correction = 0;
			if (excess > tolerance)
			{
				double land = Area(source, CrustType.Continental);
				double ocean = Area(source, CrustType.Oceanic);
				double overflow = Math.Max(0, land + Math.Min(ocean, excess) - capacity);
				if (overflow > tolerance)
				{
					visited ??= new int[_grid.CellCount];
					queue.Clear();
					visited[cell] = cell + 1;
					queue.Enqueue(cell);
					while (overflow > tolerance && queue.TryDequeue(out int current))
					{
						cancellation.ThrowIfCancellationRequested();
						foreach (int neighbor in _grid.Neighbors[current])
						{
							if (visited[neighbor] == cell + 1) continue;
							visited[neighbor] = cell + 1;
							queue.Enqueue(neighbor);
							if (_cellFlows[neighbor].Overflow) continue;
							double room = changed.TryGetValue(neighbor, out var row)
								? Area(row, CrustType.Oceanic) : OceanArea(rows.Row(neighbor));
							// 留下正面积承载接收格原有的洋壳体积。
							double take = Math.Min(overflow, room / 2);
							if (take <= tolerance) continue;
							var target = Edit(neighbor);
							ScaleOcean(target, (room - take) / room);
							Transfer(source, target, take, continentalOnly: true);
							overflow -= take;
							correction += take;
							if (overflow <= tolerance) break;
						}
					}
					if (overflow > tolerance)
						throw new EvolutionStepException(EvolutionProblemCode.InvalidCoverage, $"格 {cell} 的覆盖余量无法重新分配");
				}

				land = Area(source, CrustType.Continental);
				ocean = Area(source, CrustType.Oceanic);
				if (ocean > 0 && land + ocean - capacity > tolerance)
				{
					ScaleOcean(source, (capacity - land) / ocean);
					correction += land + ocean - capacity;
				}
			}

			_cellFlows[cell] = flow with { Subducted = flow.Subducted + removed,
				AreaCorrection = correction, Overflow = false };
		}
		return changed == null ? rows : rows.ReplaceRows(changed);

		CrustRowBuffer Edit(int cell)
		{
			if (changed.TryGetValue(cell, out var row)) return row;
			row = new CrustRowBuffer(rows.SlotsPerCell);
			foreach (ref readonly CrustEntry entry in rows.Row(cell)) row.Set(entry.Key, entry.Slot);
			changed.Add(cell, row);
			return row;
		}
	}

	static double Area(CrustRowBuffer row, CrustType? type = null)
	{
		double area = 0;
		foreach (int key in row.WrittenKeys)
			if (!type.HasValue || key % 2 == (int)type.Value) area += row.Slots[key].Area;
		return area;
	}

	static double OceanArea(ReadOnlySpan<CrustEntry> row)
	{
		double area = 0;
		foreach (ref readonly CrustEntry entry in row)
			if (entry.Key % 2 == (int)CrustType.Oceanic) area += entry.Slot.Area;
		return area;
	}

	static void Transfer(CrustRowBuffer source, CrustRowBuffer target, double area, bool continentalOnly)
	{
		double share = area / Area(source, continentalOnly ? CrustType.Continental : null);
		foreach (int key in source.WrittenKeys)
		{
			ref CrustSlot slot = ref source.Slots[key];
			if (slot.Area == 0 || (continentalOnly && key % 2 != (int)CrustType.Continental)) continue;
			target.Add(key, slot, share);
			slot.Remove(slot.Area * share);
		}
	}

	static void ScaleOcean(CrustRowBuffer row, double factor)
	{
		foreach (int key in row.WrittenKeys)
		{
			if (key % 2 != (int)CrustType.Oceanic) continue;
			ref CrustSlot slot = ref row.Slots[key];
			slot.ScaleArea(factor);
			slot.ReferenceThicknessArea += slot.Volume * (1 - factor);
		}
	}

	CellFlow SettleCoverage(int cell, CrustRowBuffer buffer)
	{
		Span<CrustSlot> row = buffer.Slots;
		Contact contact = _contacts[cell];
		double area = 0;
		foreach (int key in buffer.WrittenKeys) area += row[key].Area;
		double capacity = _grid.AreasKm2[cell];
		double excess = area - capacity;
		if (Math.Abs(excess) <= capacity * Math.Min(_spec.CoverageRelativeTolerance, 1e-12)) return default;
		if (!(area > 0)) return new CellFlow(0, 0, 0, InvalidCoverage: true);

		double removedVolume = 0;
		int owner = _before.PlateOfCell[cell];
		ref CrustSlot land = ref row[owner * 2 + (int)CrustType.Continental];
		if (excess > 0 && contact.IsSubduction)
		{
			ref CrustSlot slab = ref row[contact.Under * 2 + (int)CrustType.Oceanic];
			double removed = Math.Min(excess, slab.Area);
			if (removed > 0)
			{
				removedVolume = removed * slab.Thickness;
				slab.Remove(removed);
				area -= removed;
			}
			if (area - capacity > capacity * Math.Min(_spec.CoverageRelativeTolerance, 1e-12))
				return new CellFlow(removedVolume, 0, 0, Overflow: true);
		}
		else if (excess < 0 && (contact.OceanOpening || (contact.Rift && land.Strain >= _spec.RiftBreakupStrain)))
		{
			double opened = -excess;
			var newborn = CrustSlot.Create(opened, _spec.NewOceanicThicknessKm, _spec.NewOceanElevationM, _timeMa, -1);
			buffer.Add(owner * 2 + (int)CrustType.Oceanic, newborn, 1);
			return new CellFlow(0, newborn.Volume, 0);
		}
		else if (excess < 0 && contact.Rift && land.Area > 0)
		{
			double strain = land.Strain + (-excess / capacity);
			land.ScaleArea((land.Area - excess) / land.Area);
			land.StrainArea = strain * land.Area;
			return default;
		}

		double correction = capacity / area;
		if (!double.IsFinite(correction)) return new CellFlow(0, 0, 0, InvalidCoverage: true);
		double correctedArea = excess <= 0 || !contact.Collision ? Math.Abs(capacity - area) : 0;
		// 非构造修正优先使用洋壳占地，避免在混合格按比例反复挤掉陆壳。
		// 洋壳占地足够时就地结算，否则把超额的陆壳搬到有空间的格。
		double remainder = area - capacity;
		if (correctedArea > capacity * Math.Min(_spec.CoverageRelativeTolerance, 1e-12))
		{
			double ocean = Area(buffer, CrustType.Oceanic);
			double continental = area - ocean;
			if (continental > 0 && ocean > capacity * _spec.CoverageRelativeTolerance)
			{
				if (remainder > ocean / 2) return new CellFlow(removedVolume, 0, 0, Overflow: true);
				if (ocean > 0 && remainder >= -ocean)
				{
					ScaleOcean(buffer, (ocean - remainder) / ocean);
					return new CellFlow(removedVolume, 0, correctedArea);
				}
			}
		}
		foreach (int i in buffer.WrittenKeys)
		{
			ref CrustSlot slot = ref row[i];
			bool physicalCompression = excess > 0 && contact.Collision && i % 2 == (int)CrustType.Continental
				&& (i / 2 == contact.A || i / 2 == contact.B);
			if (physicalCompression)
			{
				slot.CollisionVolume += Math.Max(0, slot.ReferenceThicknessArea * (1 - correction));
				slot.ShorteningArea += -contact.Speed * _deltaMa * slot.Area;
			}
			slot.ScaleArea(correction);
			// 走滑和迎风混合造成的覆盖差只修面积，不在非活动边上制造均衡抬升。
			if (!physicalCompression) slot.ReferenceThicknessArea += slot.Volume * (1 - correction);
		}
		return new CellFlow(removedVolume, 0, correctedArea);
	}

	static void Assign(Contact[] contacts, int cell, Contact candidate)
	{
		if (Math.Abs(candidate.Speed) > Math.Abs(contacts[cell].Speed)) contacts[cell] = candidate;
	}
}
