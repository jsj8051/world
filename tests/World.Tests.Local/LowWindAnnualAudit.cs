using System;
using System.Linq;
using Godot;
using World.Logic;

namespace World.Tests;

static partial class SeasonalCirculationExperiment
{
	readonly record struct ThermalCoast(CirculationOperator.Edge Edge, int Index, double Latitude, double InwardNorth, int Sign);
	readonly record struct AnnualWindMonth(double EquatorP95, double GlobalP95, double[] Zonal, double[] Coast);

	static bool ReportAnnualLowWind(string label, float[] latitude, Vector3[] east, double[] area, ThermalCoast[] coast,
		Func<int, ThermalWindMonth> solveMonth, int months)
	{
		var bandArea = new double[4];
		var bandByCell = new int[latitude.Length];
		for (int i = 0; i < latitude.Length; i++)
		{
			float lat = latitude[i];
			int band = lat switch
			{
				>= 10 and < 25 => 0,
				<= -10 and > -25 => 1,
				>= 30 and < 50 => 2,
				<= -30 and > -50 => 3,
				_ => -1
			};
			bandByCell[i] = band;
			if (band >= 0) bandArea[band] += area[i];
		}
		var regionalEdges = new[]
		{
			coast.Select((face, i) => (face, i)).Where(item => item.face.Latitude is >= 0 and < 50 && item.face.InwardNorth > 0.5).Select(item => item.i).ToArray(),
			coast.Select((face, i) => (face, i)).Where(item => item.face.Latitude is >= -50 and < 0 && item.face.InwardNorth < -0.5).Select(item => item.i).ToArray(),
			coast.Select((face, i) => (face, i)).Where(item => item.face.Latitude is >= 0 and < 25).Select(item => item.i).ToArray(),
			coast.Select((face, i) => (face, i)).Where(item => item.face.Latitude is >= -25 and < 0).Select(item => item.i).ToArray()
		};
		var coastLength = regionalEdges.Select(indices => indices.Sum(i => coast[i].Edge.LengthM)).ToArray();
		var summary = new AnnualWindMonth[months];
		var steps = new double[months];
		var strongFlips = new double[months];
		double totalLength = coast.Sum(face => face.Edge.LengthM);
		ThermalWindMonth first = default, previous = default;
		Console.WriteLine($"  {label}, 12 months at half-day relaxation. Physical wind in m/s, east/onshore positive.");
		Console.WriteLine("  month   equator/global P95     trades N/S       westerlies N/S     coast N-facing-south/S-facing-north    low coast N/S");
		for (int month = 0; month < months; month++)
		{
			var wind = solveMonth(month);
			var zonal = new double[4];
			for (int i = 0; i < area.Length; i++)
			{
				int band = bandByCell[i];
				if (band >= 0) zonal[band] += area[i] * wind.Velocity[i].Dot(east[i]);
			}
			for (int i = 0; i < zonal.Length; i++) zonal[i] /= bandArea[i];
			var coastal = regionalEdges.Select((indices, region) => coastLength[region] > 0
				? indices.Sum(i => coast[i].Edge.LengthM * wind.CoastalSpeed[i]) / coastLength[region] : double.NaN).ToArray();
			float equator = Percentile(wind.Velocity.Where((_, i) => Math.Abs(latitude[i]) <= 5).Select(value => value.Length()), 0.95);
			float global = Percentile(wind.Velocity.Select(value => value.Length()), 0.95);
			summary[month] = new AnnualWindMonth(equator, global, zonal, coastal);
			Console.WriteLine($"  {month + 1,5}    {equator,6:F2}/{global,-6:F2}     {zonal[0],6:F2}/{zonal[1],-6:F2}    " +
				$"{zonal[2],6:F2}/{zonal[3],-6:F2}               {coastal[0],6:F2}/{coastal[1],-6:F2}                {coastal[2],6:F2}/{coastal[3],-6:F2}");
			if (month == 0) first = wind;
			else RecordStep(month, previous, wind);
			previous = wind;
		}
		RecordStep(0, previous, first);
		int eqPeak = Array.IndexOf(summary, summary.MaxBy(value => value.EquatorP95));
		int globalPeak = Array.IndexOf(summary, summary.MaxBy(value => value.GlobalP95));
		int stepPeak = Array.IndexOf(steps, steps.Max());
		int coastFlipPeak = Array.IndexOf(strongFlips, strongFlips.Max());
		int reversedBands = summary.Sum(value =>
			(value.Zonal[0] >= 0 ? 1 : 0) + (value.Zonal[1] >= 0 ? 1 : 0) +
			(value.Zonal[2] <= 0 ? 1 : 0) + (value.Zonal[3] <= 0 ? 1 : 0));
		Console.WriteLine($"    Peak equator/global P95 {summary[eqPeak].EquatorP95:F2} (m{eqPeak + 1})/" +
			$"{summary[globalPeak].GlobalP95:F2} (m{globalPeak + 1}); wrong-sign band-months {reversedBands}/{months * 4}.");
		Console.WriteLine($"    Largest adjacent-month vector-change P95 {steps[stepPeak]:F2} ({Transition(stepPeak)}); " +
			$"largest strong coast reversal {strongFlips[coastFlipPeak]:P1} ({Transition(coastFlipPeak)}).");
		for (int region = 0; region < coastLength.Length; region++)
		{
			if (coastLength[region] == 0) continue;
			int jump = Enumerable.Range(0, months).MaxBy(month =>
				Math.Abs(summary[month].Coast[region] - summary[Previous(month)].Coast[region]));
			double change = summary[jump].Coast[region] - summary[Previous(jump)].Coast[region];
			Console.WriteLine($"    Coast region {region + 1}: largest adjacent-month mean change {change:+0.00;-0.00;0.00} m/s ({Transition(jump)}).");
		}
		return summary.All(value => double.IsFinite(value.EquatorP95) && double.IsFinite(value.GlobalP95)
			&& value.Zonal.All(double.IsFinite)) && steps.All(double.IsFinite) && strongFlips.All(double.IsFinite);

		int Previous(int month) => (month + months - 1) % months;
		string Transition(int month) => $"m{Previous(month) + 1}->m{month + 1}";

		void RecordStep(int month, ThermalWindMonth before, ThermalWindMonth after)
		{
			steps[month] = Percentile(after.Velocity.Select((wind, i) => (wind - before.Velocity[i]).Length()), 0.95);
			double reversalLength = 0;
			for (int i = 0; i < coast.Length; i++)
			{
				double oldSpeed = before.CoastalSpeed[i], newSpeed = after.CoastalSpeed[i];
				if ((oldSpeed < -0.5 && newSpeed > 0.5) || (oldSpeed > 0.5 && newSpeed < -0.5))
					reversalLength += coast[i].Edge.LengthM;
			}
			strongFlips[month] = reversalLength / totalLength;
		}
	}
}
