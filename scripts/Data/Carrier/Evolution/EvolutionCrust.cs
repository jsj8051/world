using System;
using System.Collections.Generic;
using World.Logic;

namespace World.Data;

/// <summary>按格、板块、壳型汇总的材料快照。面积固定在宿主网格上，体积和历史随材料输运。</summary>
public sealed class EvolutionCrust
{
	readonly IReadOnlyList<double> _cellAreas;
	internal CrustRows Rows { get; }
	public int CellCount => _cellAreas.Count;
	public int PlateCount { get; }
	public int OccupiedSlotCount { get; }
	public double VolumeKm3 { get; }

	// 合成夹具可传入密集槽，构造后只持有压缩行。
	internal EvolutionCrust(IReadOnlyList<double> cellAreas, int plateCount, CrustSlot[] slots,
		double timeMa, double coverageTolerance)
		: this(cellAreas, plateCount, FromDense(cellAreas.Count, plateCount, slots), timeMa, coverageTolerance)
	{
	}

	internal EvolutionCrust(IReadOnlyList<double> cellAreas, int plateCount, CrustRows rows,
		double timeMa, double coverageTolerance)
	{
		_cellAreas = cellAreas;
		PlateCount = plateCount;
		Rows = rows;
		if (rows.CellCount != cellAreas.Count || rows.SlotsPerCell != checked(plateCount * 2))
			throw new ArgumentException("材料槽与演化网格不匹配", nameof(rows));
		int occupied = 0;
		double volume = 0;
		for (int cell = 0; cell < cellAreas.Count; cell++)
		{
			double coverage = 0;
			foreach (ref readonly CrustEntry entry in rows.Row(cell))
			{
				ref readonly CrustSlot slot = ref entry.Slot;
				slot.Validate(entry.Key % 2 == (int)CrustType.Oceanic, timeMa);
				if (slot.Area > 0) occupied++;
				coverage += slot.Area;
				volume += slot.Volume;
			}
			if (Math.Abs(coverage - cellAreas[cell]) > cellAreas[cell] * coverageTolerance)
				throw new ArgumentException($"格 {cell} 覆盖面积 {coverage:G17} 与格面积 {cellAreas[cell]:G17} 不一致");
		}
		OccupiedSlotCount = occupied;
		VolumeKm3 = volume;
	}

	static CrustRows FromDense(int cells, int plateCount, CrustSlot[] slots)
	{
		int stride = checked(plateCount * 2);
		if (slots.Length != checked(cells * stride))
			throw new ArgumentException("材料槽与演化网格不匹配", nameof(slots));
		return CrustRows.Build(cells, stride, null, (cell, row) =>
		{
			for (int key = 0; key < stride; key++)
			{
				ref readonly CrustSlot slot = ref slots[cell * stride + key];
				if (!slot.IsDefault) row.Set(key, slot);
			}
		});
	}

	public double AreaKm2(int cell, int plate, CrustType type) => Get(cell, plate, type).Area;
	internal CrustType DominantType(int cell, int plate) =>
		AreaKm2(cell, plate, CrustType.Continental) >= AreaKm2(cell, plate, CrustType.Oceanic)
			? CrustType.Continental : CrustType.Oceanic;
	public double ThicknessKm(int cell, int plate, CrustType type) => Get(cell, plate, type).Thickness;
	public double ExtensionStrain(int cell, int plate) => Get(cell, plate, CrustType.Continental).Strain;
	public double? OceanBirthTimeMa(int cell, int plate)
	{
		ref readonly CrustSlot slot = ref Get(cell, plate, CrustType.Oceanic);
		return slot.Area > 0 ? slot.BirthVolume / slot.Volume : null;
	}

	internal ref readonly CrustSlot Get(int cell, int plate, CrustType type)
	{
		if ((uint)cell >= (uint)CellCount || (uint)plate >= (uint)PlateCount || (uint)type > 1)
			throw new ArgumentOutOfRangeException(nameof(cell), "材料索引越界");
		return ref Rows.Get(cell, 2 * plate + (int)type);
	}

	internal static int Index(int cell, int plate, CrustType type, int plateCount) =>
		(cell * plateCount + plate) * 2 + (int)type;
}

internal struct CrustSlot
{
	public double Area, Volume, BirthVolume, ReferenceHeightArea, ReferenceThicknessArea, CoolingReferenceArea;
	public double StrainArea, ShorteningArea, CollisionVolume, VolcanicVolume, ArcVolcanicVolume;
	public int Province;
	public double ProvinceWeight;
	public readonly double Thickness => Area > 0 ? Volume / Area : 0;
	public readonly double Strain => Area > 0 ? StrainArea / Area : 0;
	public readonly bool IsDefault => Area == 0 && Volume == 0 && BirthVolume == 0 && ReferenceHeightArea == 0
		&& ReferenceThicknessArea == 0 && CoolingReferenceArea == 0 && StrainArea == 0 && ShorteningArea == 0
		&& CollisionVolume == 0 && VolcanicVolume == 0 && ArcVolcanicVolume == 0 && Province == 0 && ProvinceWeight == 0;

	public static CrustSlot Create(double area, double thickness, double height, double? birth, int province) => new()
	{
		Area = area,
		Volume = area * thickness,
		BirthVolume = area * thickness * (birth ?? 0),
		ReferenceHeightArea = area * height,
		ReferenceThicknessArea = area * thickness,
		Province = province,
		ProvinceWeight = area,
	};

	public void Add(in CrustSlot source, double share)
	{
		double area = source.Area * share;
		double volume = source.Volume * share;
		// 反复跨格产生的极小尾量远低于账本精度。成组舍入，避免面积先下溢而留下孤立体积。
		const double traceCutoff = 1e-250;
		if (area < traceCutoff && volume < traceCutoff) return;
		Area += area;
		Volume += volume;
		BirthVolume += source.BirthVolume * share;
		ReferenceHeightArea += source.ReferenceHeightArea * share;
		ReferenceThicknessArea += source.ReferenceThicknessArea * share;
		CoolingReferenceArea += source.CoolingReferenceArea * share;
		StrainArea += source.StrainArea * share;
		ShorteningArea += source.ShorteningArea * share;
		CollisionVolume += source.CollisionVolume * share;
		VolcanicVolume += source.VolcanicVolume * share;
		ArcVolcanicVolume += source.ArcVolcanicVolume * share;
		double weight = source.ProvinceWeight * share;
		if (source.Province == Province) ProvinceWeight += weight;
		else if (weight > ProvinceWeight)
		{
			Province = source.Province;
			ProvinceWeight = weight;
		}
	}

	public void ScaleArea(double factor)
	{
		Area *= factor;
		ReferenceHeightArea *= factor;
		ReferenceThicknessArea *= factor;
		CoolingReferenceArea *= factor;
		StrainArea *= factor;
		ShorteningArea *= factor;
		ProvinceWeight *= factor;
	}

	public void Remove(double area)
	{
		double fraction = 1 - area / Area;
		if (fraction <= 0)
		{
			this = default;
			return;
		}
		ScaleArea(fraction);
		Volume *= fraction;
		BirthVolume *= fraction;
		CollisionVolume *= fraction;
		VolcanicVolume *= fraction;
		ArcVolcanicVolume *= fraction;
	}

	public readonly void Validate(bool oceanic, double timeMa)
	{
		if (!double.IsFinite(Area) || !double.IsFinite(Volume) || Area < 0 || Volume < 0
			|| (Area == 0) != (Volume == 0) || !double.IsFinite(BirthVolume)
			|| !double.IsFinite(ReferenceHeightArea) || !double.IsFinite(ReferenceThicknessArea)
			|| !double.IsFinite(CoolingReferenceArea) || !double.IsFinite(StrainArea)
			|| !double.IsFinite(ShorteningArea) || !double.IsFinite(CollisionVolume) || CollisionVolume < 0
			|| !double.IsFinite(VolcanicVolume) || VolcanicVolume < 0 || VolcanicVolume > Volume * (1 + 1e-12)
			|| !double.IsFinite(ArcVolcanicVolume) || ArcVolcanicVolume < 0 || ArcVolcanicVolume > VolcanicVolume
			|| (oceanic && BirthVolume > timeMa * Volume + 1e-8 * Volume))
			throw new ArgumentException($"演化材料含无效面积、体积或历史值，面积={Area:G17}，体积={Volume:G17}，" +
				$"出生时间积分={BirthVolume:G17}，火山体积={VolcanicVolume:G17}");
	}
}
