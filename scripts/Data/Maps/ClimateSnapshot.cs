using System;
using System.Collections.Generic;

namespace World.Data.Maps;

internal enum WindKind
{
	Surface = 0,
	Low = 1,
	Free = 2,
}

internal sealed class WindSetSnapshot
{
	public WindKind Kind { get; }
	public SnapshotArray<float> Monthly { get; }
	public SnapshotArray<float> Annual { get; }
	public SnapshotArray<float> AnnualSpeed { get; }

	public WindSetSnapshot(WindKind kind, IEnumerable<float> monthly, IEnumerable<float> annual,
		IEnumerable<float> annualSpeed)
	{
		MapStructure.Require(Enum.IsDefined(kind), "climate/wind/kind", "Unknown wind kind.");
		Kind = kind;
		Monthly = new SnapshotArray<float>(monthly);
		Annual = new SnapshotArray<float>(annual);
		AnnualSpeed = new SnapshotArray<float>(annualSpeed);
	}

	internal void Validate(int cellCount, int monthCount, string name)
	{
		MapStructure.Length(Monthly.Count, checked(monthCount * cellCount * 3), $"climate/wind/{name}/monthly");
		MapStructure.Length(Annual.Count, checked(cellCount * 3), $"climate/wind/{name}/annual");
		MapStructure.Length(AnnualSpeed.Count, cellCount, $"climate/wind/{name}/annual_speed");
		ValidateFinite(Monthly, $"climate/wind/{name}/monthly");
		ValidateFinite(Annual, $"climate/wind/{name}/annual");
		for (int cell = 0; cell < cellCount; cell++)
		{
			MapStructure.Range(AnnualSpeed[cell], 0, 1, $"climate/wind/{name}/annual_speed", cell);
			ValidateBoundedVector(Annual, cell * 3, $"climate/wind/{name}/annual", cell);
		}
		for (int i = 0; i < monthCount * cellCount; i++)
			ValidateBoundedVector(Monthly, i * 3, $"climate/wind/{name}/monthly", i);
	}

	internal void ValidateDirections(IReadOnlyList<float> flattenedCellDirections, int cellCount, int monthCount, string name)
	{
		MapStructure.Length(flattenedCellDirections.Count, checked(cellCount * 3), "climate/cell_directions");
		for (int i = 0; i < cellCount; i++)
		{
			ValidateTangency(Annual, i * 3, flattenedCellDirections, i, $"climate/wind/{name}/annual", i);
			for (int month = 0; month < monthCount; month++)
				ValidateTangency(Monthly, (month * cellCount + i) * 3, flattenedCellDirections, i,
					$"climate/wind/{name}/monthly", (long)month * cellCount + i);
		}
	}

	static void ValidateBoundedVector(SnapshotArray<float> values, int offset, string location, int index)
	{
		double norm = Norm(values[offset], values[offset + 1], values[offset + 2]);
		MapStructure.Require(norm <= 1 + 1e-5, location, "Wind vector magnitude exceeds the normalized bound.", index: index);
	}

	static void ValidateTangency(SnapshotArray<float> values, int offset, IReadOnlyList<float> directions,
		int cell, string location, long index)
	{
		float x = directions[cell * 3], y = directions[cell * 3 + 1], z = directions[cell * 3 + 2];
		MapStructure.Finite(x, "climate/cell_directions", cell * 3);
		MapStructure.Finite(y, "climate/cell_directions", cell * 3 + 1);
		MapStructure.Finite(z, "climate/cell_directions", cell * 3 + 2);
		MapStructure.Require(Math.Abs(values[offset] * x + values[offset + 1] * y + values[offset + 2] * z) <= 1e-4,
			location, "Wind vector is not tangent to the supplied cell direction.", index: index);
	}

	static double Norm(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);

	static void ValidateFinite(SnapshotArray<float> values, string location)
	{
		for (int i = 0; i < values.Count; i++) MapStructure.Finite(values[i], location, i);
	}
}

internal sealed class ClimateSnapshot
{
	public SnapshotArray<float> PrecipitationMm { get; }
	public SnapshotArray<float> PrecipitationMonthlyMm { get; }
	public SnapshotArray<float> TemperatureC { get; }
	public SnapshotArray<float> TemperatureMonthlyC { get; }
	public SnapshotArray<float> HotMonthC { get; }
	public SnapshotArray<float> ColdMonthC { get; }
	public SnapshotArray<float> PressureAnnualHpa { get; }
	public SnapshotArray<float> PressureMonthlyHpa { get; }
	public SnapshotArray<float> DryMonthMm { get; }
	public SnapshotArray<int> DryMonthIndex { get; }
	public SnapshotArray<float> MonsoonStrength { get; }
	public WindSetSnapshot SurfaceWind { get; }
	public WindSetSnapshot LowWind { get; }
	public WindSetSnapshot FreeWind { get; }
	public SnapshotArray<float> OceanCurrentDirection { get; }
	public SnapshotArray<float> OceanCurrentWarmth { get; }
	public SnapshotArray<float> OceanCurrentStrength { get; }

	public ClimateSnapshot(IEnumerable<float> precipitationMm, IEnumerable<float> precipitationMonthlyMm,
		IEnumerable<float> temperatureC, IEnumerable<float> temperatureMonthlyC, IEnumerable<float> hotMonthC,
		IEnumerable<float> coldMonthC, IEnumerable<float> pressureAnnualHpa, IEnumerable<float> pressureMonthlyHpa,
		IEnumerable<float> dryMonthMm, IEnumerable<int> dryMonthIndex, IEnumerable<float> monsoonStrength,
		WindSetSnapshot surfaceWind, WindSetSnapshot lowWind, WindSetSnapshot freeWind,
		IEnumerable<float> oceanCurrentDirection, IEnumerable<float> oceanCurrentWarmth,
		IEnumerable<float> oceanCurrentStrength)
	{
		PrecipitationMm = new SnapshotArray<float>(precipitationMm);
		PrecipitationMonthlyMm = new SnapshotArray<float>(precipitationMonthlyMm);
		TemperatureC = new SnapshotArray<float>(temperatureC);
		TemperatureMonthlyC = new SnapshotArray<float>(temperatureMonthlyC);
		HotMonthC = new SnapshotArray<float>(hotMonthC);
		ColdMonthC = new SnapshotArray<float>(coldMonthC);
		PressureAnnualHpa = new SnapshotArray<float>(pressureAnnualHpa);
		PressureMonthlyHpa = new SnapshotArray<float>(pressureMonthlyHpa);
		DryMonthMm = new SnapshotArray<float>(dryMonthMm);
		DryMonthIndex = new SnapshotArray<int>(dryMonthIndex);
		MonsoonStrength = new SnapshotArray<float>(monsoonStrength);
		SurfaceWind = MapStructure.Present(surfaceWind, "climate/wind/surface");
		LowWind = MapStructure.Present(lowWind, "climate/wind/low");
		FreeWind = MapStructure.Present(freeWind, "climate/wind/free");
		OceanCurrentDirection = new SnapshotArray<float>(oceanCurrentDirection);
		OceanCurrentWarmth = new SnapshotArray<float>(oceanCurrentWarmth);
		OceanCurrentStrength = new SnapshotArray<float>(oceanCurrentStrength);
	}

	/// <summary>Validates the stored climate columns without deriving annual values from monthly data.</summary>
	internal void Validate(int cellCount, int monthCount = 12)
	{
		MapStructure.Require(cellCount >= 0 && monthCount > 0, "climate", "Cell and month counts must be valid.");
		ValidateLength(PrecipitationMm, cellCount, "precipitation_mm");
		ValidateLength(PrecipitationMonthlyMm, checked(cellCount * monthCount), "precipitation_monthly_mm");
		ValidateLength(TemperatureC, cellCount, "temperature_c");
		ValidateLength(TemperatureMonthlyC, checked(cellCount * monthCount), "temperature_monthly_c");
		ValidateLength(HotMonthC, cellCount, "hot_month_c");
		ValidateLength(ColdMonthC, cellCount, "cold_month_c");
		ValidateLength(PressureAnnualHpa, cellCount, "pressure_annual_hpa");
		ValidateLength(PressureMonthlyHpa, checked(cellCount * monthCount), "pressure_monthly_hpa");
		ValidateLength(DryMonthMm, cellCount, "dry_month_mm");
		MapStructure.Length(DryMonthIndex.Count, cellCount, "climate/dry_month_index");
		ValidateLength(MonsoonStrength, cellCount, "monsoon_strength");
		ValidateLength(OceanCurrentDirection, checked(cellCount * 3), "ocean_current_direction");
		ValidateLength(OceanCurrentWarmth, cellCount, "ocean_current_warmth");
		ValidateLength(OceanCurrentStrength, cellCount, "ocean_current_strength");

		ValidateNonnegative(PrecipitationMm, "precipitation_mm");
		ValidateNonnegative(PrecipitationMonthlyMm, "precipitation_monthly_mm");
		ValidateFinite(TemperatureC, "temperature_c");
		ValidateFinite(TemperatureMonthlyC, "temperature_monthly_c");
		ValidateFinite(HotMonthC, "hot_month_c");
		ValidateFinite(ColdMonthC, "cold_month_c");
		ValidatePositive(PressureAnnualHpa, "pressure_annual_hpa");
		ValidatePositive(PressureMonthlyHpa, "pressure_monthly_hpa");
		ValidateNonnegative(DryMonthMm, "dry_month_mm");
		ValidateRange(MonsoonStrength, 0, 1, "monsoon_strength");
		for (int i = 0; i < cellCount; i++) MapStructure.Range(DryMonthIndex[i], 0, monthCount - 1, "climate/dry_month_index", i);
		ValidateRange(OceanCurrentWarmth, -1, 1, "ocean_current_warmth");
		ValidateRange(OceanCurrentStrength, 0, 1, "ocean_current_strength");
		ValidateDirections(OceanCurrentDirection, cellCount);

		MapStructure.Require(SurfaceWind.Kind == WindKind.Surface, "climate/wind/surface", "Wind kind identity mismatch.");
		MapStructure.Require(LowWind.Kind == WindKind.Low, "climate/wind/low", "Wind kind identity mismatch.");
		MapStructure.Require(FreeWind.Kind == WindKind.Free, "climate/wind/free", "Wind kind identity mismatch.");
		SurfaceWind.Validate(cellCount, monthCount, "surface");
		LowWind.Validate(cellCount, monthCount, "low");
		FreeWind.Validate(cellCount, monthCount, "free");
	}

	internal void ValidateDirections(IReadOnlyList<float> flattenedCellDirections, int cellCount, int monthCount = 12)
	{
		MapStructure.Length(OceanCurrentDirection.Count, checked(cellCount * 3), "climate/ocean_current_direction");
		ValidateDirections(OceanCurrentDirection, cellCount);
		SurfaceWind.ValidateDirections(flattenedCellDirections, cellCount, monthCount, "surface");
		LowWind.ValidateDirections(flattenedCellDirections, cellCount, monthCount, "low");
		FreeWind.ValidateDirections(flattenedCellDirections, cellCount, monthCount, "free");
		for (int cell = 0; cell < cellCount; cell++)
			ValidateTangency(OceanCurrentDirection, cell * 3, flattenedCellDirections, cell, "climate/ocean_current_direction", cell);
	}

	static void ValidateDirections(SnapshotArray<float> values, int count)
	{
		for (int i = 0; i < count; i++)
		{
			int offset = i * 3;
			float x = values[offset], y = values[offset + 1], z = values[offset + 2];
			MapStructure.Finite(x, "climate/ocean_current_direction", offset);
			MapStructure.Finite(y, "climate/ocean_current_direction", offset + 1);
			MapStructure.Finite(z, "climate/ocean_current_direction", offset + 2);
			double norm = Math.Sqrt(x * x + y * y + z * z);
			MapStructure.Require(norm <= 1e-4 || Math.Abs(norm - 1) <= 1e-4,
				"climate/ocean_current_direction", "Current direction must be zero or unit length.", index: i);
		}
	}

	static void ValidateLength(SnapshotArray<float> values, int length, string name)
	{
		MapStructure.Length(values.Count, length, $"climate/{name}");
	}

	static void ValidateFinite(SnapshotArray<float> values, string name)
	{
		for (int i = 0; i < values.Count; i++) MapStructure.Finite(values[i], $"climate/{name}", i);
	}

	static void ValidateNonnegative(SnapshotArray<float> values, string name)
	{
		for (int i = 0; i < values.Count; i++) MapStructure.Range(values[i], 0, float.MaxValue, $"climate/{name}", i);
	}

	static void ValidatePositive(SnapshotArray<float> values, string name)
	{
		for (int i = 0; i < values.Count; i++)
			MapStructure.Require(float.IsFinite(values[i]) && values[i] > 0, $"climate/{name}", "Expected a positive finite value.", index: i);
	}

	static void ValidateRange(SnapshotArray<float> values, float minimum, float maximum, string name)
	{
		for (int i = 0; i < values.Count; i++) MapStructure.Range(values[i], minimum, maximum, $"climate/{name}", i);
	}

	static void ValidateTangency(SnapshotArray<float> values, int offset, IReadOnlyList<float> directions,
		int cell, string location, long index)
	{
		float x = directions[cell * 3], y = directions[cell * 3 + 1], z = directions[cell * 3 + 2];
		MapStructure.Finite(x, "climate/cell_directions", cell * 3);
		MapStructure.Finite(y, "climate/cell_directions", cell * 3 + 1);
		MapStructure.Finite(z, "climate/cell_directions", cell * 3 + 2);
		MapStructure.Require(Math.Abs(values[offset] * x + values[offset + 1] * y + values[offset + 2] * z) <= 1e-4,
			location, "Vector is not tangent to the supplied cell direction.", index: index);
	}
}
