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
		var shortening = new double[n];
		var collisionVolume = new double[n];
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
				shortening[cell] += land.ShorteningArea;
				collisionVolume[cell] += Math.Min(land.CollisionVolume, Math.Max(0, land.Volume - land.ReferenceThicknessArea));
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
			shortening[cell] = landArea > 0 ? shortening[cell] / landArea : 0;
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
				riftStrain, shortening, collisionVolume, arcVolume, spec, timeMa);
			float[] surface = relief.AddToHeights();
			var cells = new CrustSurfaceCell[n];
			EvolutionWork.ForCells(n, (start, end) =>
			{
				for (int cell = start; cell < end; cell++)
				{
					double landArea = 0, oceanArea = 0, landHeightArea = 0, oceanHeightArea = 0;
					foreach (ref readonly CrustEntry entry in material.Rows.Row(cell))
					{
						bool ocean = entry.Key % 2 == (int)CrustType.Oceanic;
						double heightArea = HeightArea(entry.Slot, spec, timeMa, ocean);
						if (ocean) { oceanArea += entry.Slot.Area; oceanHeightArea += heightArea; }
						else { landArea += entry.Slot.Area; landHeightArea += heightArea; }
					}
					float delta = surface[cell] - heights[cell];
					float landHeight = landArea > 0 ? (float)(landHeightArea / landArea) + delta : surface[cell];
					float oceanHeight = oceanArea > 0 ? (float)(oceanHeightArea / oceanArea) + delta : surface[cell];
					cells[cell] = ClassifySurface(landArea, landHeight, oceanArea, oceanHeight, seaLevelM);
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
		bool landExposed = continentalHeight > seaLevelM;
		bool oceanExposed = oceanicHeight > seaLevelM;
		double above = (landExposed ? continentalArea : 0) + (oceanExposed ? oceanicArea : 0);
		double below = (landExposed ? 0 : continentalArea) + (oceanExposed ? 0 : oceanicArea);
		double aboveHeight = (landExposed ? continentalArea * continentalHeight : 0)
			+ (oceanExposed ? oceanicArea * oceanicHeight : 0);
		double belowHeight = (landExposed ? 0 : continentalArea * continentalHeight)
			+ (oceanExposed ? 0 : oceanicArea * oceanicHeight);
		return new CrustSurfaceCell((float)(above / (above + below)),
			above > 0 ? (float)(aboveHeight / above) : seaLevelM,
			below > 0 ? (float)(belowHeight / below) : seaLevelM);
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
