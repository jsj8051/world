using System;
using System.Collections.Generic;

namespace World.Data.Maps;

internal readonly record struct Float3Snapshot(float X, float Y, float Z)
{
	internal void Validate(string location, long index)
	{
		MapStructure.Finite(X, location + ".x", index);
		MapStructure.Finite(Y, location + ".y", index);
		MapStructure.Finite(Z, location + ".z", index);
	}
}

internal readonly record struct Double3Snapshot(double X, double Y, double Z)
{
	internal void Validate(string location, long index)
	{
		MapStructure.Finite(X, location + ".x", index);
		MapStructure.Finite(Y, location + ".y", index);
		MapStructure.Finite(Z, location + ".z", index);
	}
}

internal sealed class NullableFloat64Snapshot
{
	public SnapshotArray<byte> Validity { get; }
	public SnapshotArray<double> Values { get; }
	public int Count => Values.Count;
	public double? this[int index] => Validity[index] == 1 ? Values[index] : null;

	public NullableFloat64Snapshot(IEnumerable<byte> validity, IEnumerable<double> values)
	{
		Validity = new(MapStructure.Present(validity, "nullable.validity"));
		Values = new(MapStructure.Present(values, "nullable.values"));
	}

	internal void Validate(int count, string location, bool nonnegative = false)
	{
		MapStructure.Length(Validity.Count, count, location + ".validity");
		MapStructure.Length(Values.Count, count, location + ".values");
		for (int i = 0; i < count; i++)
		{
			MapStructure.Require(Validity[i] <= 1, location + ".validity", "A validity mask must be 0 or 1.", index: i);
			// A masked slot is null, not an age/speed of zero; do not interpret its payload.
			if (Validity[i] == 0) continue;
			MapStructure.Finite(Values[i], location, i);
			if (nonnegative)
				MapStructure.Require(Values[i] >= 0, location, "A nonnegative value is required.", index: i);
		}
	}
}

internal sealed class SurfaceSnapshot
{
	public SnapshotArray<CrustType> SurfaceCrustType { get; }
	public SnapshotArray<Float3Snapshot> MotionOmega { get; }
	public ProjectedSurfaceSnapshot Projected { get; }

	public SurfaceSnapshot(IEnumerable<CrustType> surfaceCrustType, IEnumerable<Float3Snapshot> motionOmega,
		ProjectedSurfaceSnapshot projected)
	{
		SurfaceCrustType = new(MapStructure.Present(surfaceCrustType, "surface.surfaceCrustType"));
		MotionOmega = new(MapStructure.Present(motionOmega, "surface.motionOmega"));
		Projected = projected;
	}

	public void Validate(int cellCount, int plateCount)
	{
		MapStructure.Require(cellCount >= 0 && plateCount >= 0, "surface", "Counts must be nonnegative.");
		MapStructure.Length(SurfaceCrustType.Count, cellCount, "surface.surfaceCrustType");
		MapStructure.Length(MotionOmega.Count, plateCount, "surface.motionOmega");
		for (int i = 0; i < cellCount; i++) MapGeologyWire.ToCrustType(SurfaceCrustType[i]);
		for (int i = 0; i < plateCount; i++) MotionOmega[i].Validate("surface.motionOmega", i);
		if (Projected == null) return;
		Projected.Validate(cellCount, plateCount);
		for (int i = 0; i < plateCount; i++)
		{
			Double3Snapshot physical = Projected.PhysicalOmegaRadPerMa[i];
			MapStructure.Require(MotionOmega[i] == new Float3Snapshot((float)physical.X, (float)physical.Y, (float)physical.Z),
				"surface.motionOmega", "Display motion must match the physical omega converted to f32.", index: i);
		}
	}
}

internal sealed class ProjectedSurfaceSnapshot
{
	public SnapshotArray<float> EmergentFraction { get; }
	public SnapshotArray<float> LandHeightM { get; }
	public SnapshotArray<float> WaterHeightM { get; }
	public SnapshotArray<float> MaterialHeightM { get; }
	public SnapshotArray<double> OceanicCoverage { get; }
	public NullableFloat64Snapshot OceanAgeMa { get; }
	public SnapshotArray<Double3Snapshot> PhysicalOmegaRadPerMa { get; }
	public SnapshotArray<double> VolcanicRateKmPerMa { get; }
	public double EmergentLandFraction { get; }

	public ProjectedSurfaceSnapshot(IEnumerable<float> emergentFraction, IEnumerable<float> landHeightM,
		IEnumerable<float> waterHeightM, IEnumerable<float> materialHeightM, IEnumerable<double> oceanicCoverage,
		NullableFloat64Snapshot oceanAgeMa, IEnumerable<Double3Snapshot> physicalOmegaRadPerMa,
		IEnumerable<double> volcanicRateKmPerMa, double emergentLandFraction)
	{
		EmergentFraction = new(MapStructure.Present(emergentFraction, "surface.projected.emergentFraction"));
		LandHeightM = new(MapStructure.Present(landHeightM, "surface.projected.landHeightM"));
		WaterHeightM = new(MapStructure.Present(waterHeightM, "surface.projected.waterHeightM"));
		MaterialHeightM = new(MapStructure.Present(materialHeightM, "surface.projected.materialHeightM"));
		OceanicCoverage = new(MapStructure.Present(oceanicCoverage, "surface.projected.oceanicCoverage"));
		OceanAgeMa = MapStructure.Present(oceanAgeMa, "surface.projected.oceanAgeMa");
		PhysicalOmegaRadPerMa = new(MapStructure.Present(physicalOmegaRadPerMa, "surface.projected.physicalOmegaRadPerMa"));
		VolcanicRateKmPerMa = new(MapStructure.Present(volcanicRateKmPerMa, "surface.projected.volcanicRateKmPerMa"));
		EmergentLandFraction = emergentLandFraction;
	}

	internal void Validate(int cellCount, int plateCount)
	{
		MapStructure.Length(EmergentFraction.Count, cellCount, "surface.projected.emergentFraction");
		MapStructure.Length(LandHeightM.Count, cellCount, "surface.projected.landHeightM");
		MapStructure.Length(WaterHeightM.Count, cellCount, "surface.projected.waterHeightM");
		MapStructure.Length(MaterialHeightM.Count, cellCount, "surface.projected.materialHeightM");
		MapStructure.Length(OceanicCoverage.Count, cellCount, "surface.projected.oceanicCoverage");
		MapStructure.Length(VolcanicRateKmPerMa.Count, cellCount, "surface.projected.volcanicRateKmPerMa");
		MapStructure.Length(PhysicalOmegaRadPerMa.Count, plateCount, "surface.projected.physicalOmegaRadPerMa");
		OceanAgeMa.Validate(cellCount, "surface.projected.oceanAgeMa", nonnegative: true);
		MapStructure.Range(EmergentLandFraction, 0, 1, "surface.projected.emergentLandFraction");
		for (int i = 0; i < cellCount; i++)
		{
			MapStructure.Range(EmergentFraction[i], 0, 1, "surface.projected.emergentFraction", i);
			MapStructure.Finite(LandHeightM[i], "surface.projected.landHeightM", i);
			MapStructure.Finite(WaterHeightM[i], "surface.projected.waterHeightM", i);
			MapStructure.Finite(MaterialHeightM[i], "surface.projected.materialHeightM", i);
			MapStructure.Range(OceanicCoverage[i], 0, 1, "surface.projected.oceanicCoverage", i);
			MapStructure.Finite(VolcanicRateKmPerMa[i], "surface.projected.volcanicRateKmPerMa", i);
			MapStructure.Require(VolcanicRateKmPerMa[i] >= 0, "surface.projected.volcanicRateKmPerMa", "A nonnegative rate is required.", index: i);
		}
		for (int i = 0; i < plateCount; i++) PhysicalOmegaRadPerMa[i].Validate("surface.projected.physicalOmegaRadPerMa", i);
	}
}