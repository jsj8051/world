using System;
using System.Collections.Generic;
using World.Data;

namespace World.Logic;

/// <summary>把固定格材料变成供演化边界判读和世界生成使用的逐格事实。</summary>
internal static class CrustProjectionBuilder
{
	public static CrustProjection Build(EvolutionGrid grid, EvolutionReliefGeometry reliefGeometry,
		EvolutionSpec spec, long stepIndex, double timeMa,
		EvolutionCrust material, IReadOnlyList<PlateMotion> motions, Plate[] plates, CrustProvince[] templates,
		double[] volcanicRates, float seaLevelM)
	{
		int n = grid.CellCount;
		var plateOfCell = new int[n];
		var types = new CrustType[n];
		var provinceOfCell = new int[n];
		var heights = new float[n];
		var ages = new double?[n];
		var oceanCoverage = new double[n];
		var riftStrain = new double[n];
		var arcVolume = new double[n];

		EvolutionWork.ForCells(n, ProjectCells);

		CrustField crust = BuildCrust(types, provinceOfCell, templates);
		PlateBoundarySet boundaries = PlateBoundaryBuilder.Build(grid.Neighbors, plateOfCell, plates.Length, sortedNeighbors: true);
		EvolutionBoundaryField field = EvolutionBoundaryOps.Build(grid, boundaries, crust, motions, plates, spec, ages, material);
		return new CrustProjection(stepIndex, plates.Length, plateOfCell, crust, heights,
			ages, oceanCoverage, volcanicRates, boundaries, field, BuildSurface, takeOwnership: true);

		void ProjectCells(int start, int end)
		{
			for (int cell = start; cell < end; cell++)
				ProjectCell(cell, material.Rows.Row(cell));
		}

		void ProjectCell(int cell, ReadOnlySpan<CrustEntry> row)
		{
			double best = 0, provinceArea = 0, landArea = 0, oceanArea = 0;
			double heightArea = 0, oceanVolume = 0, birthVolume = 0, strainArea = 0;
			int province = -1;
			for (int offset = 0; offset < row.Length;)
			{
				int plate = row[offset].Key / 2;
				ref readonly CrustSlot ocean = ref CrustRows.EmptySlot;
				ref readonly CrustSlot land = ref CrustRows.EmptySlot;
				if (row[offset].Key % 2 == (int)CrustType.Oceanic)
					ocean = ref row[offset++].Slot;
				if (offset < row.Length && row[offset].Key == 2 * plate + (int)CrustType.Continental)
					land = ref row[offset++].Slot;
				double area = ocean.Area + land.Area;
				if (area > best)
				{
					plateOfCell[cell] = plate;
					arcVolume[cell] = ocean.ArcVolcanicVolume + land.ArcVolcanicVolume;
					best = area;
				}
				if (land.Area > provinceArea)
				{
					province = land.Province;
					provinceArea = land.Area;
				}
				landArea += land.Area;
				oceanArea += ocean.Area;
				oceanVolume += ocean.Volume;
				birthVolume += ocean.BirthVolume;
				strainArea += land.StrainArea;
				heightArea += HeightArea(land, spec, timeMa, false);
				heightArea += HeightArea(ocean, spec, timeMa, true);
			}
			bool continental = landArea >= oceanArea;
			types[cell] = continental ? CrustType.Continental : CrustType.Oceanic;
			provinceOfCell[cell] = continental ? province : -1;
			heights[cell] = (float)(heightArea / (landArea + oceanArea));
			oceanCoverage[cell] = Math.Clamp(oceanArea / grid.AreasKm2[cell], 0, 1);
			ages[cell] = oceanVolume > 0 ? Math.Max(0, timeMa - birthVolume / oceanVolume) : null;
			riftStrain[cell] = landArea > 0 ? strainArea / landArea : 0;
		}

		(float[], EvolutionRelief, CrustSurfaceCell[], double, CrustType[]) BuildSurface()
		{
			var surfaceTypes = new CrustType[n];
			EvolutionWork.ForCells(n, (start, end) =>
			{
				for (int cell = start; cell < end; cell++)
					surfaceTypes[cell] = material.DominantType(cell, plateOfCell[cell]);
			});
			var relief = new EvolutionRelief(grid, reliefGeometry, boundaries, field, surfaceTypes, plateOfCell, heights,
				material, riftStrain, arcVolume, spec, timeMa);
			var effects = relief.BuildCellRelief();
			var surface = new float[n];
			var cells = new CrustSurfaceCell[n];
			EvolutionWork.ForCells(n, (start, end) =>
			{
				for (int cell = start; cell < end; cell++)
				{
					double shared = effects.SharedDeltaM[cell];
					double area = 0, landArea = 0, otherOceanArea = 0;
					double landHeightArea = 0, otherOceanHeightArea = 0, trenchHeightArea = 0;
					var exposed = new SurfaceAreas();
					foreach (ref readonly CrustEntry entry in material.Rows.Row(cell))
					{
						bool ocean = entry.Key % 2 == (int)CrustType.Oceanic;
						double slotArea = entry.Slot.Area;
						area += slotArea;
						double heightArea = HeightArea(entry.Slot, spec, timeMa, ocean);
						if (!ocean)
						{
							landArea += slotArea;
							landHeightArea += heightArea;
							continue;
						}
						double trench = effects.TrenchDeltaAt(cell, entry.Key / 2);
						if (trench == 0)
						{
							otherOceanArea += slotArea;
							otherOceanHeightArea += heightArea;
							continue;
						}
						// 先按洋壳所属板块修正海拔，再结算是否露出水面。
						trenchHeightArea += trench * slotArea;
						exposed.Add(slotArea, heightArea / slotArea + shared + trench, seaLevelM);
					}
					surface[cell] = heights[cell] + (float)(shared + trenchHeightArea / area);
					if (landArea > 0) exposed.Add(landArea, landHeightArea / landArea + shared, seaLevelM);
					if (otherOceanArea > 0) exposed.Add(otherOceanArea, otherOceanHeightArea / otherOceanArea + shared, seaLevelM);
					cells[cell] = exposed.ToCell(seaLevelM);
				}
			});
			double exposedArea = 0, globeArea = 0;
			for (int cell = 0; cell < n; cell++)
			{
				globeArea += grid.AreasKm2[cell];
				exposedArea += grid.AreasKm2[cell] * cells[cell].EmergentFraction;
			}
			return (surface, relief, cells, exposedArea / globeArea, surfaceTypes);
		}
	}

	internal static CrustSurfaceCell ClassifySurface(double continentalArea, float continentalHeight,
		double oceanicArea, float oceanicHeight, float seaLevelM)
	{
		var areas = new SurfaceAreas();
		areas.Add(continentalArea, continentalHeight, seaLevelM);
		areas.Add(oceanicArea, oceanicHeight, seaLevelM);
		return areas.ToCell(seaLevelM);
	}

	struct SurfaceAreas
	{
		double _above, _below, _aboveHeightArea, _belowHeightArea;

		public void Add(double area, double height, float seaLevelM)
		{
			if (height > seaLevelM)
			{
				_above += area;
				_aboveHeightArea += area * height;
			}
			else
			{
				_below += area;
				_belowHeightArea += area * height;
			}
		}

		public readonly CrustSurfaceCell ToCell(float seaLevelM) => new((float)(_above / (_above + _below)),
			_above > 0 ? (float)(_aboveHeightArea / _above) : seaLevelM,
			_below > 0 ? (float)(_belowHeightArea / _below) : seaLevelM);
	}

	internal static CrustField BuildCrust(CrustType[] types, int[] provinceOfCell, CrustProvince[] templates)
	{
		var count = new int[templates.Length];
		foreach (int id in provinceOfCell)
			if (id >= 0) count[id]++;
		var provinces = new CrustProvince[count.Length];
		for (int i = 0; i < count.Length; i++)
		{
			CrustProvince p = templates[i];
			provinces[i] = new CrustProvince(i, p.Type, p.SeedCell, p.SeedPlateId, count[i]);
		}
		return new CrustField(types, provinceOfCell, provinces);
	}

	static double HeightArea(in CrustSlot slot, EvolutionSpec spec, double timeMa, bool ocean)
	{
		if (slot.Area == 0) return 0;
		double density = ocean ? spec.OceanicDensityKgM3 : spec.ContinentalDensityKgM3;
		double isostatic = (slot.Volume - slot.ReferenceThicknessArea) * 1000
			* (spec.MantleDensityKgM3 - density) / spec.MantleDensityKgM3;
		double cooling = 0;
		if (ocean)
		{
			double age = Math.Clamp(timeMa - slot.BirthVolume / slot.Volume, 0, spec.MaxCoolingAgeMa);
			cooling = spec.CoolingDepthMPerSqrtMa
				* (slot.CoolingReferenceArea - slot.Area * Math.Sqrt(age));
		}
		return slot.ReferenceHeightArea + isostatic + cooling + 1000 * slot.VolcanicVolume * spec.VolcanicReliefFraction;
	}
}
