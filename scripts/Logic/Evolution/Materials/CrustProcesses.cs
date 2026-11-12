using System;
using System.Collections.Generic;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>把输运后的占地需求结算为固定面积。只有活动边界能触发物质交换。</summary>
internal sealed class CrustProcesses
{
	internal readonly record struct Flows(double Subducted, double NewOceanic, double Volcanic, double AreaCorrection);
	readonly record struct Contact(double AreaKm2, int Plate, int OtherPlate, CrustType Type,
		int Under, bool ContinentalRift)
	{
		public bool IsSubduction => Under >= 0;
		public int Upper => Under == Plate ? OtherPlate : Plate;
	}
	readonly record struct SubductionRequest(int Under, int Upper, double AreaKm2);
	readonly record struct CellFlow(double Subducted, double NewOceanic, double AreaCorrection,
		bool InvalidCoverage = false, double OceanVolcanic = 0, double LandVolcanic = 0, bool Overflow = false,
		List<SubductionRequest> PendingSubduction = null);
	readonly EvolutionGrid _grid;
	readonly EvolutionSpec _spec;
	readonly double _timeMa, _deltaMa;
	readonly double[] _volcanicRates;
	readonly List<Contact>[] _contacts;
	readonly (int Plate, double Rate, bool Arc)[] _volcanoes;
	readonly CellFlow[] _cellFlows;
	readonly ContinentalDeformation _deformation;

	public CrustProcesses(EvolutionGrid grid, EvolutionSpec spec, CrustProjection before,
		IReadOnlyList<PlateMotion> motions, double timeMa, double deltaMa, double[] volcanicRates,
		ContinentalDeformation deformation = null)
	{
		_grid = grid;
		_spec = spec;
		_timeMa = timeMa;
		_deltaMa = deltaMa;
		_volcanicRates = volcanicRates;
		_deformation = deformation;
		_contacts = new List<Contact>[grid.CellCount];
		_volcanoes = new (int Plate, double Rate, bool Arc)[grid.CellCount];
		_cellFlows = new CellFlow[grid.CellCount];
		for (int i = 0; i < before.PlateBoundaries.Edges.Length; i++)
		{
			if (before.Boundary.ConvergenceKinds[i] == ConvergenceKind.Collision) continue;
			PlateBoundaryEdge edge = before.PlateBoundaries.Edges[i];
			var kind = before.Boundary.MotionKinds[i];
			if (kind is not (EvolutionMotionKind.Convergent or EvolutionMotionKind.Divergent)) continue;
			double speed = before.Boundary.NormalSpeedKmPerMa[i];
			var (rateA, rateB, _) = grid.NormalAreaRates(edge.CellA, edge.CellB,
				motions[edge.PlateA].OmegaRadPerMa, motions[edge.PlateB].OmegaRadPerMa);
			var (areaA, areaB) = SplitBoundaryArea(rateA, rateB, deltaMa);
			var (typeA, typeB) = before.Boundary.SideTypes(i, before.Crust.Type[edge.CellA], before.Crust.Type[edge.CellB]);
			int under = before.Boundary.UnderthrustingPlateIds[i];
			bool continentalRift = typeA == CrustType.Continental && typeB == CrustType.Continental;
			AddContact(edge.CellA, new Contact(areaA, edge.PlateA, edge.PlateB, typeA, under, continentalRift));
			AddContact(edge.CellB, new Contact(areaB, edge.PlateB, edge.PlateA, typeB, under, continentalRift));
			if (under >= 0)
			{
				int over = under == edge.PlateA ? edge.CellB : edge.CellA;
				double footprint = spec.ArcReachKm / (spec.ArcReachKm + grid.CellRadiusRad[over] * grid.RadiusKm);
				AssignVolcanism(over, spec.ArcVolcanicRateKmPerMa * footprint, arc: true);
			}
			else if (kind == EvolutionMotionKind.Divergent)
			{
				if (typeA == CrustType.Oceanic) AssignVolcanism(edge.CellA, spec.RidgeVolcanicRateKmPerMa, arc: false);
				if (typeB == CrustType.Oceanic) AssignVolcanism(edge.CellB, spec.RidgeVolcanicRateKmPerMa, arc: false);
			}

			void AssignVolcanism(int cell, double rate, bool arc)
			{
				rate *= Math.Min(1, Math.Abs(speed) / (4 * spec.ActiveSpeedKmPerMa));
				if (rate > _volcanoes[cell].Rate) _volcanoes[cell] = (before.PlateOfCell[cell], rate, arc);
			}
		}
	}

	void AddContact(int cell, Contact contact)
	{
		// 零份额的一侧也保留接触关系，供同一俯冲带查询下盘材料。
		(_contacts[cell] ??= new()).Add(contact);
	}

	internal static (double A, double B) SplitBoundaryArea(double rateA, double rateB, double deltaMa)
	{
		// 同向时差额落在下游格。两格相加等于整条边的相对法向通量。
		return ((Math.Max(-rateB, 0) - Math.Max(-rateA, 0)) * deltaMa,
			(Math.Max(rateA, 0) - Math.Max(rateB, 0)) * deltaMa);
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
			double removed = 0;

			// 只沿原接触的俯冲带补结算该格尚未用掉的通量。
			if (flow.PendingSubduction != null)
			{
				foreach (var request in flow.PendingSubduction)
				{
					int slabKey = 2 * request.Under + (int)CrustType.Oceanic;
					double pending = Math.Min(excess, request.AreaKm2);
					foreach (int neighbor in _grid.Neighbors[cell])
					{
						if (pending <= tolerance) break;
						if (!SharesSubduction(neighbor, request)) continue;
						double available = changed.TryGetValue(neighbor, out var row)
							? row.Slots[slabKey].Area : rows.Get(neighbor, slabKey).Area;
						double upperArea = PlateArea(source, request.Upper);
						double take = Math.Min(Math.Min(pending, available), upperArea);
						if (take <= tolerance) continue;
						var target = Edit(neighbor);
						ref CrustSlot slab = ref target.Slots[slabKey];
						removed += take * slab.Thickness;
						slab.Remove(take);
						double share = take / upperArea;
						TransferShare(source, target, 2 * request.Upper + (int)CrustType.Continental, share);
						TransferShare(source, target, 2 * request.Upper + (int)CrustType.Oceanic, share);
						excess -= take;
						pending -= take;
					}
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
							TransferContinental(source, target, take);
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
				AreaCorrection = correction, Overflow = false, PendingSubduction = null };
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

	bool SharesSubduction(int cell, SubductionRequest request)
	{
		if (_contacts[cell] is not { } contacts) return false;
		foreach (var contact in contacts)
			if (contact.Under == request.Under && contact.Upper == request.Upper) return true;
		return false;
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

	static double PlateArea(CrustRowBuffer row, int plate) => row.Slots[2 * plate + (int)CrustType.Oceanic].Area
		+ row.Slots[2 * plate + (int)CrustType.Continental].Area;

	static void TransferContinental(CrustRowBuffer source, CrustRowBuffer target, double area)
	{
		double share = area / Area(source, CrustType.Continental);
		foreach (int key in source.WrittenKeys)
			if (key % 2 == (int)CrustType.Continental) TransferShare(source, target, key, share);
	}

	static void TransferShare(CrustRowBuffer source, CrustRowBuffer target, int key, double share)
	{
		ref CrustSlot slot = ref source.Slots[key];
		if (slot.Area == 0) return;
		target.Add(key, slot, share);
		slot.Remove(slot.Area * share);
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
		double area = Area(buffer);
		double capacity = _grid.AreasKm2[cell];
		double tolerance = capacity * Math.Min(_spec.CoverageRelativeTolerance, 1e-12);
		double excess = area - capacity;
		if (Math.Abs(excess) <= tolerance) return default;
		if (!(area > 0)) return new CellFlow(0, 0, 0, InvalidCoverage: true);

		CellFlow flow = ApplyBoundaryFluxes(cell, buffer, capacity, excess, tolerance);
		if (flow.Overflow) return flow;
		ApplyDeformation(cell, buffer, capacity, Area(buffer) - capacity, tolerance);
		area = Area(buffer);
		excess = area - capacity;
		if (Math.Abs(excess) <= tolerance) return flow;

		double correction = capacity / area;
		if (!double.IsFinite(correction)) return flow with { InvalidCoverage = true };
		flow = flow with { AreaCorrection = Math.Abs(excess) };
		// 非构造修正优先使用洋壳占地，避免在混合格按比例反复挤掉陆壳。
		// 洋壳占地足够时就地结算，否则把超额的陆壳搬到有空间的格。
		double ocean = Area(buffer, CrustType.Oceanic);
		double continental = area - ocean;
		if (continental > 0 && ocean > capacity * _spec.CoverageRelativeTolerance)
		{
			if (excess > ocean / 2) return flow with { Overflow = true };
			if (excess >= -ocean)
			{
				ScaleOcean(buffer, (ocean - excess) / ocean);
				return flow;
			}
		}
		foreach (int i in buffer.WrittenKeys)
		{
			ref CrustSlot slot = ref buffer.Slots[i];
			slot.ScaleArea(correction);
			slot.ReferenceThicknessArea += slot.Volume * (1 - correction);
		}
		return flow;
	}

	void ApplyDeformation(int cell, CrustRowBuffer buffer, double capacity, double excess, double tolerance)
	{
		if (_deformation == null || !_deformation.Cells.TryGetValue(cell, out var motion)) return;
		double requested = motion.ConvergenceKm2PerMa * _deltaMa;
		if (Math.Abs(excess) <= tolerance || Math.Abs(requested) <= tolerance || Math.Sign(excess) != Math.Sign(requested))
			return;
		ref CrustSlot land = ref buffer.Slots[2 * motion.Plate + (int)CrustType.Continental];
		if (land.Area <= tolerance) return;
		double amount = Math.Min(Math.Abs(excess), Math.Abs(requested));
		if (requested > 0)
		{
			double compressed = Math.Min(amount, land.Area * (1 - 1e-12));
			Compress(ref land, compressed / land.Area, compressed / Math.Sqrt(capacity));
		}
		else land.ScaleArea(1 + amount / land.Area);
	}

	CellFlow ApplyBoundaryFluxes(int cell, CrustRowBuffer buffer, double capacity, double excess, double tolerance)
	{
		if (_contacts[cell] is not { } contacts) return default;
		bool closing = excess > 0;
		double requested = 0;
		foreach (var contact in contacts)
			requested += Math.Max(0, AvailableFlux(contact));
		if (requested <= tolerance) return default;

		// 一个格可以同时邻接多条边。各边按通量分摊可结算的覆盖余量。
		double share = Math.Min(1, Math.Abs(excess) / requested);
		double subducted = 0, newOceanic = 0;
		List<SubductionRequest> pending = null;
		foreach (var contact in contacts)
		{
			double area = AvailableFlux(contact) * share;
			if (area <= tolerance) continue;
			ref CrustSlot land = ref buffer.Slots[2 * contact.Plate + (int)CrustType.Continental];
			if (closing)
			{
				area = Math.Min(area, PlateArea(buffer, contact.Upper));
				if (area <= tolerance) continue;
				ref CrustSlot slab = ref buffer.Slots[2 * contact.Under + (int)CrustType.Oceanic];
				double removed = Math.Min(area, slab.Area);
				if (removed > 0)
				{
					subducted += removed * slab.Thickness;
					slab.Remove(removed);
				}
				if (area - removed > tolerance)
					(pending ??= new()).Add(new SubductionRequest(contact.Under, contact.Upper, area - removed));
			}
			else if (contact.Type == CrustType.Oceanic || land.Strain >= _spec.RiftBreakupStrain)
			{
				var newborn = CrustSlot.Create(area, _spec.NewOceanicThicknessKm, _spec.NewOceanElevationM, _timeMa, -1);
				buffer.Add(2 * contact.Plate + (int)CrustType.Oceanic, newborn, 1);
				newOceanic += newborn.Volume;
			}
			else if (land.Area > 0)
			{
				double strain = land.Strain + area / capacity;
				land.ScaleArea((land.Area + area) / land.Area);
				land.StrainArea = strain * land.Area;
			}
		}
		return new CellFlow(subducted, newOceanic, 0, Overflow: pending != null, PendingSubduction: pending);

		double AvailableFlux(Contact contact) => closing ? (contact.IsSubduction ? contact.AreaKm2 : 0)
			: contact.Type == CrustType.Oceanic || contact.ContinentalRift ? -contact.AreaKm2 : 0;
	}

	static void Compress(ref CrustSlot slot, double fraction, double shorteningKm)
	{
		slot.CollisionVolume += Math.Max(0, slot.ReferenceThicknessArea * fraction);
		slot.ShorteningArea += shorteningKm * slot.Area;
		slot.ScaleArea(1 - fraction);
	}
}
