using System;

namespace World.Tests;

/// <summary>
/// 本地测试跑器：`dotnet run --project tests/World.Tests.Local`，退出码 0 = 全绿。
/// 纯 C#（不启引擎、不碰 H3 原生库），pre-commit 与 CI 共用同一入口。
/// </summary>
static class Program
{
	static int _passed;
	static int _failed;

	static int Main(string[] args)
	{
		if (args.Length > 0 && args[0] == "--world")
		{
			WorldSmoke.Run(args.Length > 1 ? int.Parse(args[1]) : 3,
				args.Length > 2 && !args[2].StartsWith("--") ? int.Parse(args[2]) : 0,
				Array.IndexOf(args, "--csv") >= 0);
			return 0;
		}

		Console.WriteLine("World.Tests.Local");

		Run("年基温随纬度下降", ClimateTests.LatitudeCoolsTowardPoles);
		Run("零倾角无季节", ClimateTests.ZeroObliquityRemovesSeasons);
		Run("南北半球季节反相", ClimateTests.ObliquityFlipsHemispheres);
		Run("零倾角无季风", ClimateTests.MonsoonVanishesWithoutSeasons);
		Run("气压随季节海陆反转", ClimateTests.PressureFlipsLandSeaSign);
		Run("气压中心落在大陆内部", ClimateTests.PressureCenterSitsInland);
		Run("气压廓线不过冲", ClimateTests.PressureProfileHasNoOvershoot);
		Run("摩擦解在赤道顺梯度", ClimateTests.FrictionSolutionFollowsPressureAtEquator);
		Run("摩擦偏转角随纬度连续", ClimateTests.FrictionTurnsWithLatitude);
		Run("风带从气压梯度涌现", ClimateTests.WindBandsEmergeFromPressure);
		Run("三套风按摩擦单调", ClimateTests.ThreeWindSetsFollowFrictionOrder);
		Run("年均风速是标量均值", ClimateTests.AnnualSpeedIsScalarMean);
		Run("季风指数只在反号时成立", ClimateTests.MonsoonIndexNeedsReversal);
		Run("季风强度跟随气压", ClimateTests.MonsoonStrengthFollowsPressure);
		Run("年降水 = Σ 月", ClimateTests.AnnualPrecipIsMonthlySum);
		Run("ITCZ 随太阳北移", ClimateTests.ItczFollowsTheSun);
		Run("内陆季节摆幅大于海岸", ClimateTests.InteriorHasLargerSeasonalSwing);
		Run("纬度剖面没有台阶", ClimateTests.NoLatitudeRingsInTemperature);
		Run("赤道没有温度台阶", ClimateTests.NoEquatorialStepInTemperature);
		Run("风场切向且有界", ClimateTests.WindIsTangentAndBounded);
		Run("洋流只在海上且切向", ClimateTests.OceanCurrentsStayInTheOcean);
		Run("湿润降温", ClimateTests.WetCoolingLowersTemperature);
		Run("降水估计向内陆衰减", ClimateTests.CoastFactorDecaysInland);

		Console.WriteLine($"\n{_passed} passed, {_failed} failed");
		return _failed == 0 ? 0 : 1;
	}

	static void Run(string name, Action test)
	{
		try
		{
			test();
			_passed++;
			Console.WriteLine($"  ok   {name}");
		}
		catch (Exception e)
		{
			_failed++;
			Console.WriteLine($"  FAIL {name}: {e.Message}");
		}
	}

	/// <summary>条件不成立即失败。</summary>
	public static void True(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
	}

	/// <summary>数值落在容差内。</summary>
	public static void Near(double actual, double expected, double tolerance, string message)
	{
		if (Math.Abs(actual - expected) > tolerance)
			throw new Exception($"{message}: 实得 {actual:G6}，期望 {expected:G6} ± {tolerance:G3}");
	}

	/// <summary>动作必须抛异常。</summary>
	public static void Throws(Action action, string message)
	{
		try { action(); }
		catch { return; }
		throw new Exception($"{message}（没有抛异常）");
	}
}
