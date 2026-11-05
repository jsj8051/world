using System;
using System.Collections.Generic;
using Godot;
using World.Data;
using World.Logic;

namespace World.Tests;

/// <summary>
/// 气候链的纯托管测试：合成球面网格（经纬格 + 4 邻接）喂给 <see cref="ClimateGrid"/>，
/// 不启引擎、不碰 H3 原生库。钉的是物理不变量（纬度趋势、季节相位、守恒恒等式），不是具体数值。
/// </summary>
static class ClimateTests
{
	const int Rows = 18;      // 纬度带数（−80°…+80°，避开极点退化）
	const int Cols = 36;      // 经度格数
	const float LandLatDeg = 45f;
	const float LandLonDeg = 70f;

	sealed class Planet
	{
		public ClimateGrid Grid;
		public Func<float, float, int> CellAt;
		public int EquatorCell, NorthMidCell, SouthMidCell, PoleCell;
	}

	static Planet Build(bool withLand = true)
	{
		var dirs = new List<Vector3>(Rows * Cols);
		for (int r = 0; r < Rows; r++)
		{
			float lat = (-80f + 160f * r / (Rows - 1)) * MathF.PI / 180f;
			for (int c = 0; c < Cols; c++)
			{
				float lon = (c * 360f / Cols - 180f) * MathF.PI / 180f;
				dirs.Add(new Vector3(
					MathF.Cos(lat) * MathF.Cos(lon),
					MathF.Sin(lat),
					MathF.Cos(lat) * MathF.Sin(lon)));
			}
		}

		int n = dirs.Count;
		var neighbors = new int[n][];
		var land = new bool[n];
		var height = new float[n];
		var planet = new Planet();

		for (int r = 0; r < Rows; r++)
		{
			// 经度 0 落在这一列；用它取样北/南中纬。
			for (int c = 0; c < Cols; c++)
			{
				int i = r * Cols + c;
				var list = new List<int> { r * Cols + (c + Cols - 1) % Cols, r * Cols + (c + 1) % Cols };
				if (r > 0) list.Add((r - 1) * Cols + c);
				if (r < Rows - 1) list.Add((r + 1) * Cols + c);
				neighbors[i] = list.ToArray();

				float latDeg = -80f + 160f * r / (Rows - 1);
				float lonDeg = c * 360f / Cols - 180f;
				land[i] = withLand && MathF.Abs(latDeg) <= LandLatDeg && MathF.Abs(lonDeg) <= LandLonDeg;
				height[i] = land[i] ? 100f : -300f;
			}
		}

		planet.Grid = ClimateGrid.FromArrays(dirs.ToArray(), neighbors, land, height, neighborKm: 1000.0);
		planet.CellAt = (latDeg, lonDeg) =>
			(int)MathF.Round((latDeg + 80f) * (Rows - 1) / 160f) * Cols
			+ (int)MathF.Round((lonDeg + 180f) * Cols / 360f);

		planet.EquatorCell = planet.CellAt(0f, 0f);
		planet.NorthMidCell = planet.CellAt(30f, 0f);
		planet.SouthMidCell = planet.CellAt(-30f, 0f);
		planet.PoleCell = planet.CellAt(75f, 0f);
		return planet;
	}

	/// <summary>无噪声、无洋流修幅、无湿润降温的"干净"配置：剩下的读数只由纬度/季节决定。</summary>
	static ClimateSpec CleanSpec() => new()
	{
		ContinentalityNoiseC = 0f,
		OceanCurrentWarmthC = 0f,
		WetCoolingCPerMm = 0f,
		PrecipNoiseAmount = 0f,
	};

	static MonthlyClimate RunMonthly(Planet planet, ClimateSpec spec, out float[] baseTempC)
	{
		var grid = planet.Grid;
		var baseTemp = new TemperatureModel();
		baseTemp.Compute(grid, spec, coastalWarmth: null, seed: 7);
		baseTempC = baseTemp.AnnualC;

		var monthly = new MonthlyClimate();
		monthly.Compute(grid, spec, baseTemp.AnnualC, coastalWarmth: null, seed: 7);
		return monthly;
	}

	static float Eastward(Planet planet, Vector3[] wind, float latDeg)
	{
		int i = planet.CellAt(latDeg, 0f);
		var r = planet.Grid.Dirs[i];
		var north = Vector3.Up - r * r.Dot(Vector3.Up);
		north = north.Normalized();
		var east = r.Cross(north).Normalized();
		return wind[i].Dot(east);
	}

	/// <summary>年基温必须随纬度单调下降：赤道 &gt; 中纬 &gt; 极地。</summary>
	public static void LatitudeCoolsTowardPoles()
	{
		var planet = Build();
		var spec = CleanSpec();
		RunMonthly(planet, spec, out var baseTemp);

		float equator = baseTemp[planet.EquatorCell];
		float mid = baseTemp[planet.NorthMidCell];
		float pole = baseTemp[planet.PoleCell];

		Program.True(equator > mid, $"赤道 {equator:F1} 应暖于 30°N {mid:F1}");
		Program.True(mid > pole, $"30°N {mid:F1} 应暖于 75°N {pole:F1}");
		Program.True(equator - pole > 20f, $"赤道–极地温差 {equator - pole:F1} 过小");
	}

	/// <summary>零倾角 ⇒ 没有季节：同一格 12 个月的温度必须几乎相同。</summary>
	public static void ZeroObliquityRemovesSeasons()
	{
		var planet = Build();
		var spec = CleanSpec();
		spec.ObliquityDeg = 0f;
		var monthly = RunMonthly(planet, spec, out _);

		float spread = Spread(monthly, planet.NorthMidCell);
		Program.True(spread < 0.5f, $"零倾角下 30°N 季节摆幅 {spread:F2}°C 应接近 0");
	}

	/// <summary>有倾角 ⇒ 南北半球季节反相：30°N 7 月最暖、30°S 7 月最冷。</summary>
	public static void ObliquityFlipsHemispheres()
	{
		var planet = Build();
		var spec = CleanSpec();
		var monthly = RunMonthly(planet, spec, out _);

		float northJuly = monthly.MonthTempC[6][planet.NorthMidCell];
		float northJan = monthly.MonthTempC[0][planet.NorthMidCell];
		float southJuly = monthly.MonthTempC[6][planet.SouthMidCell];
		float southJan = monthly.MonthTempC[0][planet.SouthMidCell];

		Program.True(northJuly > northJan + 5f, $"30°N 应夏暖冬冷：7月 {northJuly:F1} vs 1月 {northJan:F1}");
		Program.True(southJuly < southJan - 5f, $"30°S 应反相：7月 {southJuly:F1} vs 1月 {southJan:F1}");
	}

	/// <summary>季风强度来自季节反差：零倾角 ⇒ 恒 0。</summary>
	public static void MonsoonVanishesWithoutSeasons()
	{
		var planet = Build();
		var spec = CleanSpec();
		spec.ObliquityDeg = 0f;
		var monthly = RunMonthly(planet, spec, out _);

		float peak = 0f;
		foreach (float m in monthly.MonsoonStrength) peak = MathF.Max(peak, m);
		Program.True(peak < 0.05f, $"零倾角季风强度峰值 {peak:F3} 应为 0");
	}

	/// <summary>年降水 = Σ 12 月（月→年涌现的恒等式）。</summary>
	public static void AnnualPrecipIsMonthlySum()
	{
		var planet = Build();
		var monthly = RunMonthly(planet, CleanSpec(), out _);

		for (int i = 0; i < planet.Grid.Count; i++)
		{
			float sum = 0f;
			for (int m = 0; m < monthly.MonthCount; m++) sum += monthly.MonthPrecipMm[m][i];
			Program.Near(sum, monthly.AnnualPrecipMm[i], 1e-2, $"格 {i} 年降水应等于 Σ 月");
		}
	}

	/// <summary>ITCZ 随太阳北移：15°N 的海格 7 月降水应明显多于 1 月。</summary>
	public static void ItczFollowsTheSun()
	{
		var planet = Build();
		var monthly = RunMonthly(planet, CleanSpec(), out _);

		int row = (int)MathF.Round((15f + 80f) * (Rows - 1) / 160f);
		int cell = row * Cols + Cols / 2;   // 经度 0°，落在陆块内但 ITCZ 项与海陆无关

		float july = monthly.MonthPrecipMm[6][cell];
		float jan = monthly.MonthPrecipMm[0][cell];
		Program.True(july > jan * 1.2f, $"15°N 7月 {july:F0} mm 应明显多于 1月 {jan:F0} mm");
	}

	/// <summary>同一纬度上，内陆的季节摆幅必须大于海岸（大陆性放大年较差）。</summary>
	public static void InteriorHasLargerSeasonalSwing()
	{
		var planet = Build();
		var monthly = RunMonthly(planet, CleanSpec(), out _);

		int coast = planet.CellAt(20f, 70f);      // 陆块东缘：离海 1 跳
		int interior = planet.CellAt(20f, 0f);    // 同纬度内陆：离海 3 跳

		Program.True(planet.Grid.Land[coast] && planet.Grid.Land[interior], "取样失败：取到的不是陆地");
		Program.True(planet.Grid.DistToCoast[interior] > planet.Grid.DistToCoast[coast],
			$"取样失败：内陆跳数 {planet.Grid.DistToCoast[interior]} 未大于海岸 {planet.Grid.DistToCoast[coast]}");

		float coastSwing = Spread(monthly, coast);
		float interiorSwing = Spread(monthly, interior);
		Program.True(interiorSwing > coastSwing * 1.08f,
			$"内陆摆幅 {interiorSwing:F1} 应明显大于海岸 {coastSwing:F1}");
	}

	/// <summary>风场是切向的、有界的：方向 ⊥ 径向，长度 ∈ [0,1]。</summary>
	public static void WindIsTangentAndBounded()
	{
		var planet = Build();
		var monthly = RunMonthly(planet, CleanSpec(), out _);

		for (int m = 0; m < monthly.MonthCount; m++)
			for (int i = 0; i < planet.Grid.Count; i++)
			{
				var wind = monthly.Wind.Surface.Monthly[m][i];
				Program.True(wind.Length() <= 1f + 1e-4f, $"风速 {wind.Length():F2} 超过 1");
				if (wind.LengthSquared() > 1e-6f)
					Program.Near(wind.Dot(planet.Grid.Dirs[i]), 0.0, 1e-3, "风必须位于切平面");
			}
	}

	/// <summary>洋流：海格才有流、方向切向、强度落在 [0.3,1]；陆地恒零。</summary>
	public static void OceanCurrentsStayInTheOcean()
	{
		var planet = Build();
		var spec = CleanSpec();
		var grid = planet.Grid;

		var ocean = new OceanCurrentModel();
		var wind = PrevailingWind.BuildAll(grid.Dirs, spec.RotationSpeed, spec.Prograde);
		ocean.Compute(grid, spec, wind, oceanTempC: null);

		int flowing = 0;
		for (int i = 0; i < grid.Count; i++)
		{
			if (grid.Land[i])
			{
				Program.Near(ocean.Strength[i], 0.0, 1e-6, $"陆地 {i} 不应有洋流");
				continue;
			}

			var direction = ocean.Direction[i];
			if (direction.LengthSquared() <= 1e-9f) continue;
			flowing++;
			Program.Near(direction.Dot(grid.Dirs[i]), 0.0, 1e-3, "洋流方向必须切向");
			Program.True(ocean.Strength[i] >= 0.3f - 1e-4f && ocean.Strength[i] <= 1f + 1e-4f,
				$"洋流强度 {ocean.Strength[i]:F2} 越界");
		}
		Program.True(flowing > grid.Count / 4, $"只有 {flowing} 个海格有洋流，环流未成形");
	}

	/// <summary>湿润降温：降水越多温度越低，且水量与降温严格成比例。</summary>
	public static void WetCoolingLowersTemperature()
	{
		var spec = new ClimateSpec { WetCoolingCPerMm = 0.004f, WetCoolingFeedback = 0.3f };
		var temperature = new[] { 20f };
		var precip = new[] { 1000f };
		TemperatureModel.ApplyWetCooling(temperature, precip, spec);

		float expected = 20f - 0.004f * 1000f / 1.3f;
		Program.Near(temperature[0], expected, 1e-4, "湿润降温应为 k·P/(1+g)");
	}

	/// <summary>降水估计：越往内陆越干（大陆性衰减单调）。</summary>
	public static void CoastFactorDecaysInland()
	{
		const double kmPerHop = 50.0;
		float coast = PrecipitationModel.CoastFactor(1, kmPerHop, 45f);
		float shallow = PrecipitationModel.CoastFactor(3, kmPerHop, 45f);
		float deep = PrecipitationModel.CoastFactor(40, kmPerHop, 45f);

		Program.True(coast > shallow, $"海岸 {coast:F2} 应湿于近岸 {shallow:F2}");
		Program.True(shallow > deep, $"近岸 {shallow:F2} 应湿于深内陆 {deep:F2}");
		Program.Near(deep, PrecipitationModel.InteriorRatio(45f), 0.03, "深内陆应收敛到渐近下限");
	}

	/// <summary>纬度剖面不能有台阶：中高纬向极的逐行降温幅度必须彼此接近。
	/// 旧实现的 `|纬度| &gt; 60° → 冰反照率` 硬阈值在 60° 画出一个人造圈（实测跨圈 6 °C 跳变，
	/// 相当于相邻两行降温幅度差了一倍）。</summary>
	public static void NoLatitudeRingsInTemperature()
	{
		var planet = Build();
		var monthly = RunMonthly(planet, CleanSpec(), out _);

		var jumps = new List<float>();
		for (int r = 0; r + 1 < Rows; r++)
		{
			float lat = -80f + 160f * r / (Rows - 1);
			if (lat < 40f) continue;   // 只在中高纬比较（赤道附近本来就近平）
			float here = monthly.AnnualTempC[planet.CellAt(lat, 150f)];
			float next = monthly.AnnualTempC[planet.CellAt(-80f + 160f * (r + 1) / (Rows - 1), 150f)];
			jumps.Add(here - next);    // 向极降温（正数）
		}

		Program.True(jumps.Count >= 4, "取样失败：中高纬行数不足");
		jumps.Sort();
		float median = jumps[jumps.Count / 2];
		float worst = jumps[^1];
		Program.True(median > 0f, $"中高纬应持续向极降温，实测中位跳变 {median:F2}");
		Program.True(worst < 1.4f * median,
			$"纬度剖面出现台阶：最大同行降温 {worst:F2} °C，中位 {median:F2} °C（硬阈值会画出纬度圈）");
	}

	/// <summary>赤道不能有横贯断裂：跨赤道的相邻两行海温必须连续。
	/// 旧实现把"向极"直接取符号（北半球向北 / 南半球向南），同一条洋流在赤道两侧被判成
	/// 一暖一冷 ⇒ 海温沿赤道整圈 ±5 °C 台阶（2026-10-23 实机截图暴露）。</summary>
	public static void NoEquatorialStepInTemperature()
	{
		var planet = Build();
		var spec = CleanSpec();
		spec.OceanCurrentWarmthC = 5f;      // 只开洋流冷暖项：噪声与湿润降温都关掉，剩下的台阶只可能来自冷暖
		var grid = planet.Grid;

		var ocean = new OceanCurrentModel();
		ocean.Compute(grid, spec, PrevailingWind.BuildAll(grid.Dirs, spec.RotationSpeed, spec.Prograde), oceanTempC: null);
		var warmth = ocean.BuildCoastalWarmth(grid);

		var temperature = new TemperatureModel();
		temperature.Compute(grid, spec, warmth, seed: 7);

		int southRow = 8, northRow = 9;      // 合成网格的 ±4.7° 两行（正好跨赤道）
		float worst = 0f;
		int samples = 0;
		for (int c = 0; c < Cols; c++)
		{
			int south = southRow * Cols + c;
			int north = northRow * Cols + c;
			if (grid.Land[south] || grid.Land[north]) continue;

			samples++;
			worst = MathF.Max(worst, MathF.Abs(temperature.AnnualC[north] - temperature.AnnualC[south]));
		}

		Program.True(samples >= Cols / 2, $"取样失败：跨赤道海格仅 {samples} 列");
		Program.True(worst < 1.5f,
			$"赤道出现温度台阶：跨赤道最大温差 {worst:F1} °C（冷暖的经向符号在赤道翻转会到 5 °C 量级）");
	}

	/// <summary>季节反转：20–40°N 上，1 月陆地为高压、7 月陆地为低压（同纬海洋是参照面）。</summary>
	public static void PressureFlipsLandSeaSign()
	{
		var planet = Build();
		var monthly = RunMonthly(planet, CleanSpec(), out _);

		float jan = LandMinusOceanPressure(monthly, planet, 0);
		float jul = LandMinusOceanPressure(monthly, planet, 6);

		Program.True(jan > 1f, $"1 月陆压应高于同纬海洋：差 {jan:F1} hPa");
		Program.True(jul < -1f, $"7 月陆压应低于同纬海洋：差 {jul:F1} hPa");
	}

	/// <summary>7 月热低压的中心必须在大陆内部，不是贴海岸的一条带。</summary>
	public static void PressureCenterSitsInland()
	{
		var planet = Build();
		var monthly = RunMonthly(planet, CleanSpec(), out _);
		var grid = planet.Grid;

		int best = -1;
		float min = float.MaxValue;
		for (int i = 0; i < grid.Count; i++)
		{
			if (!grid.Land[i]) continue;
			float lat = grid.LatDeg(i);
			if (lat < 5f || lat > 40f) continue;
			float p = monthly.MonthPressureHpa[6][i];
			if (p < min) { min = p; best = i; }
		}

		Program.True(best >= 0, "取样失败：7 月陆地上没有格");
		Program.True(grid.DistToCoast[best] >= 2,
			$"7 月热低压中心贴海岸（离海 {grid.DistToCoast[best]} 跳）：中心应落在大陆内部");
	}

	/// <summary>p_z 廓线用 PCHIP：段内不得越过两端节点（普通样条的过冲会闭合出假中心）。</summary>
	public static void PressureProfileHasNoOvershoot()
	{
		float[] xs = { -90f, -75f, -60f, -45f, -30f, -15f, 0f, 15f, 30f, 45f, 60f, 75f, 90f };
		float[] ys = { 1014f, 1002f, 990f, 1002f, 1018f, 1014f, 1009f, 1014f, 1019f, 1008f, 996f, 1000f, 1012f };
		var slopes = MonotoneCubic.Slopes(xs, ys);

		for (int k = 0; k < xs.Length - 1; k++)
		{
			float lo = MathF.Min(ys[k], ys[k + 1]) - 1e-3f;
			float hi = MathF.Max(ys[k], ys[k + 1]) + 1e-3f;
			for (int s = 0; s <= 20; s++)
			{
				float x = xs[k] + (xs[k + 1] - xs[k]) * (s / 20f);
				float y = MonotoneCubic.Evaluate(xs, ys, slopes, x);
				Program.True(y >= lo && y <= hi, $"气压廓线在 {x:F1}° 过冲：{y:F2} 不在 [{lo:F2}, {hi:F2}]");
			}
		}
	}

	/// <summary>f→0：风顺气压梯度直吹（没有 1/f 奇点），且纬向分量很小。</summary>
	public static void FrictionSolutionFollowsPressureAtEquator()
	{
		var planet = Build(withLand: false);
		var grid = planet.Grid;
		var gradient = NorthwardGradient(grid, 50f);
		var (wind, speed) = SolveSurfaceWind(planet, CleanSpec(), gradient);

		int eq = planet.EquatorCell;
		var north = TangentField.NorthTangent(grid.Dirs[eq]);
		var east = TangentField.EastTangent(grid.Dirs[eq], north);
		float u = wind[eq].Dot(east);
		float v = wind[eq].Dot(north);

		Program.True(v < -0.1f, $"赤道应顺梯度向南（v<0）：v={v:F3}");
		Program.True(MathF.Abs(u) < 0.45f * MathF.Abs(v),
			$"赤道附近纬向分量应远小于经向：u={u:F3} v={v:F3}");
		Program.True(speed[eq] > 0f && speed[eq] <= 1f, $"风速越界：{speed[eq]:F3}");
	}

	/// <summary>北半球风相对梯度右偏；摩擦越大越偏向低压（与梯度的夹角越小）。</summary>
	public static void FrictionTurnsWithLatitude()
	{
		var planet = Build(withLand: false);
		var grid = planet.Grid;
		var gradient = NorthwardGradient(grid, 50f);

		var (light, _) = SolveSurfaceWind(planet, new ClimateSpec { FrictionSurface = 1e-5f }, gradient);
		var (heavy, _) = SolveSurfaceWind(planet, new ClimateSpec { FrictionSurface = 8e-5f }, gradient);

		int mid = planet.CellAt(40f, 0f);
		float lightAngle = AngleToDownGradient(light[mid], gradient[mid]);
		float heavyAngle = AngleToDownGradient(heavy[mid], gradient[mid]);

		Program.True(lightAngle > 1.0f, $"小摩擦应接近地转（偏转角大）：{lightAngle:F2} rad");
		Program.True(heavyAngle < lightAngle - 0.2f,
			$"摩擦增大应更偏向低压：{heavyAngle:F2} rad 未明显小于 {lightAngle:F2} rad");
	}

	/// <summary>风带从 p_z 的梯度里自然出现：20° 东风、40° 西风（无海陆、纯纬向带）。</summary>
	public static void WindBandsEmergeFromPressure()
	{
		var planet = Build(withLand: false);
		var spec = CleanSpec();
		var grid = planet.Grid;
		var temp = new float[grid.Count];
		for (int i = 0; i < grid.Count; i++) temp[i] = 30f - 0.5f * MathF.Abs(grid.LatDeg(i));

		var model = PressureFieldModel.Create(grid, spec, CoarseLayer.Identity(grid), temp);
		var pressure = new float[grid.Count];
		model.BuildMonth(temp, declinationRad: 0f, pressure);

		var gradient = new Vector3[grid.Count];
		TangentField.GradientField(grid, pressure, gradient);
		var (wind, _) = SolveSurfaceWind(planet, spec, gradient);

		float trade = Eastward(planet, wind, 20f);
		float west = Eastward(planet, wind, 40f);
		Program.True(trade < -0.02f, $"20° 应为东风，实得 {trade:F3}");
		Program.True(west > 0.02f, $"40° 应为西风，实得 {west:F3}");

		for (int i = 0; i < grid.Count; i++)
		{
			Program.True(wind[i].Length() <= 1f + 1e-4f, $"风速 {wind[i].Length():F2} 超过 1");
			if (wind[i].LengthSquared() > 1e-6f)
				Program.Near(wind[i].Dot(grid.Dirs[i]), 0.0, 1e-3, "风必须位于切平面");
		}
	}

	/// <summary>三套风同源、按摩擦单调：地面 ≤ 低空 ≤ 自由。</summary>
	public static void ThreeWindSetsFollowFrictionOrder()
	{
		var planet = Build(withLand: false);
		var spec = CleanSpec();
		var grid = planet.Grid;
		var temp = new float[grid.Count];
		for (int i = 0; i < grid.Count; i++) temp[i] = 30f - 0.5f * MathF.Abs(grid.LatDeg(i));

		var model = PressureFieldModel.Create(grid, spec, CoarseLayer.Identity(grid), temp);
		var pressure = new float[grid.Count];
		model.BuildMonth(temp, 0f, pressure);
		var gradient = new Vector3[grid.Count];
		TangentField.GradientField(grid, pressure, gradient);

		var surface = new Vector3[grid.Count];
		var low = new Vector3[grid.Count];
		var free = new Vector3[grid.Count];
		WindSolver.Create(grid, spec).SolveAll(gradient, surface, low, free);

		int mid = planet.CellAt(40f, 0f);
		Program.True(free[mid].Length() > 0.05f, $"自由风太弱（{free[mid].Length():F3}），比较无意义");
		Program.True(surface[mid].Length() <= low[mid].Length() + 1e-4f,
			$"地面风 {surface[mid].Length():F3} 应不快于低空 {low[mid].Length():F3}");
		Program.True(low[mid].Length() <= free[mid].Length() + 1e-4f,
			$"低空风 {low[mid].Length():F3} 应不快于自由 {free[mid].Length():F3}");
	}

	/// <summary>年均标量风速 = 12 个月 |v| 的算术均值；且 |年合成矢量| ≤ 它（季风区会互相抵消）。</summary>
	public static void AnnualSpeedIsScalarMean()
	{
		var planet = Build();
		var monthly = RunMonthly(planet, CleanSpec(), out _);
		var set = monthly.Wind.Surface;

		for (int i = 0; i < planet.Grid.Count; i++)
		{
			float sum = 0f;
			for (int m = 0; m < monthly.MonthCount; m++) sum += set.Monthly[m][i].Length();
			Program.Near(set.AnnualSpeed[i], sum / monthly.MonthCount, 1e-5, $"格 {i} 年均标量风速");
			Program.True(set.Annual[i].Length() <= set.AnnualSpeed[i] + 1e-4f,
				$"格 {i} 年合成矢量 {set.Annual[i].Length():F3} 不该大于标量均值 {set.AnnualSpeed[i]:F3}");
		}
	}

	/// <summary>季风指数：Δp 必须反号才算；恒定海陆差（不反号）恒 0。</summary>
	public static void MonsoonIndexNeedsReversal()
	{
		var planet = Build();
		var grid = planet.Grid;
		int n = grid.Count;
		const int months = 12;

		var steady = new float[months][];
		for (int m = 0; m < months; m++)
		{
			steady[m] = new float[n];
			for (int i = 0; i < n; i++) steady[m][i] = 1000f + (grid.Land[i] ? 5f : 0f);
		}
		var none = MonsoonIndex.Compute(grid, new ClimateSpec(), steady);
		for (int i = 0; i < n; i++)
			Program.Near(none[i], 0.0, 1e-6, $"格 {i} 不反号时季风强度应为 0");

		var flip = new float[months][];
		for (int m = 0; m < months; m++)
		{
			flip[m] = new float[n];
			for (int i = 0; i < n; i++)
				flip[m][i] = 1000f + (grid.Land[i] ? (m < 6 ? 5f : -5f) : 0f);
		}
		var some = MonsoonIndex.Compute(grid, new ClimateSpec(), flip);

		int landChecked = 0;
		for (int i = 0; i < n; i++)
		{
			if (!grid.Land[i]) { Program.Near(some[i], 0.0, 1e-6, $"海格 {i} 季风强度应为 0"); continue; }
			Program.Near(some[i], 10f / 30f, 1e-4, $"格 {i} 反号 10 hPa 应记 1/3");
			landChecked++;
		}
		Program.True(landChecked > 100, $"取样失败：只有 {landChecked} 个陆格");
	}

	/// <summary>真实气压链：大部分陆地季风强度 > 0.2，海格恒 0，且全部落在 [0,1]。</summary>
	public static void MonsoonStrengthFollowsPressure()
	{
		var planet = Build();
		var monthly = RunMonthly(planet, CleanSpec(), out _);
		var grid = planet.Grid;

		int landStrong = 0;
		for (int i = 0; i < grid.Count; i++)
		{
			float s = monthly.MonsoonStrength[i];
			Program.True(s >= 0f && s <= 1f, $"格 {i} 季风强度 {s:F3} 越界");
			if (!grid.Land[i]) { Program.Near(s, 0.0, 1e-6, $"海格 {i} 季风强度应为 0"); continue; }
			if (s > 0.2f) landStrong++;
		}
		Program.True(landStrong > grid.Count / 20, $"季风陆格只有 {landStrong} 个，气压口径没有生效");
	}

	/// <summary>向北的均匀气压梯度（hPa/rad），给求解器单测用。</summary>
	static Vector3[] NorthwardGradient(ClimateGrid grid, float magnitudeHpaPerRad)
	{
		var gradient = new Vector3[grid.Count];
		for (int i = 0; i < grid.Count; i++)
			gradient[i] = TangentField.NorthTangent(grid.Dirs[i]) * magnitudeHpaPerRad;
		return gradient;
	}

	static (Vector3[] wind, float[] speed) SolveSurfaceWind(Planet planet, ClimateSpec spec, Vector3[] gradient)
	{
		var grid = planet.Grid;
		var wind = new Vector3[grid.Count];
		var speed = new float[grid.Count];
		WindSolver.Create(grid, spec).SolveSurface(gradient, wind, speed);
		return (wind, speed);
	}

	/// <summary>风与"顺梯度方向"的夹角（0 = 直吹低压，π/2 = 沿等压线）。</summary>
	static float AngleToDownGradient(Vector3 wind, Vector3 gradient)
	{
		if (wind.LengthSquared() < 1e-12f) return 0f;
		return MathF.Acos(Math.Clamp(wind.Normalized().Dot(-gradient.Normalized()), -1f, 1f));
	}

	/// <summary>20–40°N 的陆压减海压（hPa；正 = 陆地高压）。</summary>
	static float LandMinusOceanPressure(MonthlyClimate monthly, Planet planet, int month)
	{
		var grid = planet.Grid;
		double landSum = 0, oceanSum = 0;
		int landN = 0, oceanN = 0;
		for (int i = 0; i < grid.Count; i++)
		{
			float lat = grid.LatDeg(i);
			if (lat < 20f || lat > 40f) continue;
			float p = monthly.MonthPressureHpa[month][i];
			if (grid.Land[i]) { landSum += p; landN++; }
			else { oceanSum += p; oceanN++; }
		}
		Program.True(landN > 0 && oceanN > 0, "取样失败：20–40°N 缺陆地或海洋");
		return (float)(landSum / landN - oceanSum / oceanN);
	}

	/// <summary>季节摆幅（同格 12 个月极差）。</summary>
	static float Spread(MonthlyClimate monthly, int cell)
	{
		float min = float.MaxValue, max = float.MinValue;
		for (int m = 0; m < monthly.MonthCount; m++)
		{
			float t = monthly.MonthTempC[m][cell];
			if (t < min) min = t;
			if (t > max) max = t;
		}
		return max - min;
	}
}
