using System;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>用月中日平均辐照与海陆热惯性求解周期性的逐月温度。</summary>
internal static class MonthlyTemperatureModel
{
	const double OceanThermalTimeMonths = 4.0;
	const double ReferenceInsolationAnomaly = 0.20;

	public static void Compute(ClimateGrid grid, ClimateSpec spec, float[] baseTempC,
		MonthlyInsolation[] insolation, float[][] temperatureC, CancellationToken cancellation)
	{
		int monthCount = insolation.Length;
		double monthLength = 12.0 / monthCount;
		double sensitivity = spec.SeasonalBaseC / ReferenceInsolationAnomaly;
		double inlandResponse = Math.Max(0.0, spec.ContinentalResponse);
		var dailyMean = new double[monthCount];

		for (int cell = 0; cell < grid.Count; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();

			double latitude = grid.LatRad(cell);
			double sinLatitude = Math.Sin(latitude);
			double cosLatitude = Math.Max(0.0, Math.Cos(latitude));
			double annualMean = 0.0;
			for (int month = 0; month < monthCount; month++)
			{
				dailyMean[month] = insolation[month].DailyMean(sinLatitude, cosLatitude);
				annualMean += dailyMean[month];
			}
			annualMean /= monthCount;

			double responseRate = (1.0 + inlandResponse * Continentality(grid, spec, cell)) / OceanThermalTimeMonths;
			double retention = Math.Exp(-monthLength * responseRate);
			double uptake = 1.0 - retention;
			double retainedInsolation = 0.0;
			for (int month = 0; month < monthCount; month++)
				retainedInsolation = retention * retainedInsolation + uptake * dailyMean[month];

			// 一年后 S_end = r^M · S_start + 本年输入。令两端相等，直接得到跨年周期解。
			retainedInsolation /= 1.0 - Math.Exp(-12.0 * responseRate);

			float groundAlbedo = grid.Land[cell] ? spec.AlbedoLand : spec.AlbedoOcean;
			if (grid.ElevNorm[cell] > spec.AlpineElevNorm) groundAlbedo = spec.AlbedoSnow;
			double groundCooling = groundAlbedo * spec.AlbedoCoolingC;
			double iceCooling = (spec.AlbedoIceCap - groundAlbedo) * spec.AlbedoCoolingC;

			for (int month = 0; month < monthCount; month++)
			{
				retainedInsolation = retention * retainedInsolation + uptake * dailyMean[month];
				double temperature = baseTempC[cell] + sensitivity * (retainedInsolation - annualMean)
					- groundCooling * retainedInsolation;
				double ice = IceFraction(temperature, spec.IceMeltC, spec.IceFullC);
				temperatureC[month][cell] = (float)(temperature - ice * iceCooling * dailyMean[month]);
			}
		}
	}

	static double Continentality(ClimateGrid grid, ClimateSpec spec, int cell)
	{
		if (!grid.Land[cell]) return 0.0;
		int distanceHops = grid.DistToCoast[cell];
		if (distanceHops <= 0) return 1.0; // 陆格无可达海格
		double distanceKm = distanceHops * grid.NeighborKm;
		return distanceKm / (distanceKm + Math.Max(0.0, spec.ContinentalityLengthKm));
	}

	/// <summary>冰比例在融冰温度与完全结冰温度之间平滑过渡。</summary>
	static double IceFraction(double temperatureC, float meltC, float fullC)
	{
		if (meltC <= fullC) return temperatureC <= fullC ? 1.0 : 0.0;
		double x = Math.Clamp((meltC - temperatureC) / (meltC - fullC), 0.0, 1.0);
		return x * x * (3.0 - 2.0 * x);
	}
}
