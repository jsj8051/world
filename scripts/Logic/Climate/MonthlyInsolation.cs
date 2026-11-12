using System;

namespace World.Logic;

/// <summary>按等长月份的月中采样，计算归一化的全天平均入射辐照。</summary>
internal readonly struct MonthlyInsolation
{
	// 月中采样从 1 月中开始，北半球夏至约在 6 月中之后 0.2 个月。
	const double JuneSolsticeYearFraction = 5.2 / 12.0;

	readonly double _sinDeclination;
	readonly double _cosDeclination;

	public float DeclinationRad { get; }

	MonthlyInsolation(double declinationRad)
	{
		DeclinationRad = (float)declinationRad;
		_sinDeclination = Math.Sin(declinationRad);
		_cosDeclination = Math.Cos(declinationRad);
	}

	public static MonthlyInsolation[] BuildYear(int monthCount, float obliquityDeg)
	{
		var year = new MonthlyInsolation[monthCount];
		double sinObliquity = Math.Sin(obliquityDeg * (Math.PI / 180.0));
		for (int month = 0; month < monthCount; month++)
		{
			double orbitAngle = Math.Tau * (month / (double)monthCount - JuneSolsticeYearFraction);
			double declination = Math.Asin(sinObliquity * Math.Cos(orbitAngle));
			year[month] = new MonthlyInsolation(declination);
		}
		return year;
	}

	public double DailyMean(double sinLatitude, double cosLatitude)
	{
		double offset = sinLatitude * _sinDeclination;
		double amplitude = cosLatitude * _cosDeclination;
		if (offset <= -amplitude) return 0.0; // 极夜
		if (offset >= amplitude) return offset; // 极昼，全天积分后余弦项相消

		double sunsetHourAngle = Math.Acos(-offset / amplitude);
		return Math.Max(0.0, (offset * sunsetHourAngle + amplitude * Math.Sin(sunsetHourAngle)) / Math.PI);
	}
}
