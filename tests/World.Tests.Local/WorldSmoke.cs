using System;
using System.Diagnostics;
using Godot;
using World.Data;
using World.Logic;
using World.Render;

namespace World.Tests;

/// <summary>
/// 全链冒烟：用真实 H3 网格跑一次 <see cref="WorldGenSimulation"/>，打印气候链读数与耗时。
/// 不进默认测试表（依赖 H3 原生库）；手动跑：
/// <code>dotnet run --project tests/World.Tests.Local -- --world 3</code>
/// </summary>
static class WorldSmoke
{
	public static void Run(int res, int oceanIterations = 0, bool printCsv = false)
	{
		var spec = new WorldSpec();
		if (oceanIterations > 0) spec.Climate.OceanIterations = oceanIterations;
		var sim = new WorldGenSimulation(res, 1f);
		var sw = Stopwatch.StartNew();
		sim.Run(spec);
		sw.Stop();

		// 单独再跑一遍气候阶段（球壳与地形产物不变，读数与全链内那次同输入）⇒ 气候耗时。
		var climateWatch = Stopwatch.StartNew();
		sim.Climate.Run(sim.Ball, sim.Terrain, spec.Terrain.SeaLevelM, spec.Climate, spec.Seed);
		climateWatch.Stop();

		var facts = sim.Facts;
		int n = facts.TemperatureC.Length;

		float tMin = float.MaxValue, tMax = float.MinValue, tSum = 0f;
		float pSum = 0f, pMax = 0f, monsoonPeak = 0f;
		int flowing = 0, monsoonLand = 0, monsoonStrong = 0;

		for (int i = 0; i < n; i++)
		{
			float t = facts.TemperatureC[i];
			tSum += t;
			tMin = MathF.Min(tMin, t);
			tMax = MathF.Max(tMax, t);
			pSum += facts.PrecipitationMm[i];
			pMax = MathF.Max(pMax, facts.PrecipitationMm[i]);
			monsoonPeak = MathF.Max(monsoonPeak, facts.MonsoonStrength[i]);
			if (facts.FinalLand[i])
			{
				monsoonLand++;
				if (facts.MonsoonStrength[i] > 0.2f) monsoonStrong++;
			}
			if (facts.OceanCurrentStrength[i] > 0.31f) flowing++;
		}

		Console.WriteLine($"cells={n} months={facts.TemperatureMonthlyC.Length} build={sw.ElapsedMilliseconds} ms climate={climateWatch.ElapsedMilliseconds} ms");
		Console.WriteLine($"temp  mean={tSum / n:F1}°C  min={tMin:F1}  max={tMax:F1}");
		Console.WriteLine($"precip mean={pSum / n:F0} mm/yr  max={pMax:F0}");
		Console.WriteLine($"wind mean|c| surface={MeanAnnualSpeed(facts, WindKind.Surface):F2}"
			+ $" low={MeanAnnualSpeed(facts, WindKind.Low):F2}"
			+ $" free={MeanAnnualSpeed(facts, WindKind.Free):F2}");
		Console.WriteLine($"monsoon land>0.2 = {monsoonStrong} / {monsoonLand}  peak={monsoonPeak:F2}");
		double windHash = 0;
		for (int i = 0; i < n; i++)
			windHash += (i + 1) * (facts.Wind.Surface.Annual[i].X
				+ 3.0 * facts.Wind.Surface.Annual[i].Y + 7.0 * facts.Wind.Surface.Annual[i].Z);
		Console.WriteLine($"wind hash={windHash:R}");
		Console.WriteLine($"ocean flowing={flowing} / {n}");
		Console.WriteLine($"ocean psi iters={sim.Climate.OceanCurrents.IterationsUsed} residual={sim.Climate.OceanCurrents.Residual:E2}");

		int riverCells = 0;
		foreach (int kind in facts.RiverTopology.NodeKind) if (kind >= 0) riverCells++;
		Console.WriteLine($"rivers cells={riverCells} outlets={facts.RiverTopology.Outlets.Count}");

		PrintLatitudeProfile(sim);
		PrintPressure(sim);
		PrintWind(sim);
		CheckWindModeVariants(sim);
		CheckParentSpeedRoughness(sim);
		CheckTectonicInfluence(sim);
		if (printCsv) PrintCsv(sim);

		Number("temp", tSum / n);
		Number("precip", pSum / n);

		if (!float.IsFinite(tSum) || !float.IsFinite(pSum))
			throw new Exception("气候场出现 NaN/Inf");
	}

	/// <summary>把逐格读数按 `CSV ` 前缀打到 stdout（lat, lon, 年均温, 1月, 7月, 陆格, 年降水）。
	/// DSH 沙箱下子进程可能不能写文件，所以由外部（shell）接管落盘。</summary>
	static void PrintCsv(WorldGenSimulation sim)
	{
		var facts = sim.Facts;
		Console.WriteLine("CSV lat,lon,t_ann,land,precip,cx,cy,cz,warmth,strength,wsurf,p_jan,p_jul,parent,w_jun");
		var parentIndex = new System.Collections.Generic.Dictionary<ulong, int>();
		var parentOf = new int[facts.Ball.CellIds.Length];
		if (facts.Ball.Res > 2)
		{
			for (int i = 0; i < parentOf.Length; i++)
			{
				ulong parent = World.Utils.H3.H3.CellToParent(facts.Ball.CellIds[i], facts.Ball.Res - 2);
				if (!parentIndex.TryGetValue(parent, out int k))
				{
					k = parentIndex.Count;
					parentIndex[parent] = k;
				}
				parentOf[i] = k;
			}
		}
		for (int i = 0; i < facts.TemperatureC.Length; i++)
		{
			var d = facts.Ball.CellDirs[i];
			float lat = MathF.Asin(Math.Clamp(d.Y, -1f, 1f)) * 180f / MathF.PI;
			float lon = MathF.Atan2(d.Z, d.X) * 180f / MathF.PI;
			var c = facts.OceanCurrentDirection[i];
			Console.WriteLine($"CSV {lat:F3},{lon:F3},{facts.TemperatureC[i]:F2},"
				+ $"{(facts.FinalLand[i] ? 1 : 0)},{facts.PrecipitationMm[i]:F0},"
				+ $"{c.X:F4},{c.Y:F4},{c.Z:F4},{facts.OceanCurrentWarmth[i]:F3},{facts.OceanCurrentStrength[i]:F3},"
				+ $"{facts.Wind.Surface.AnnualSpeed[i]:F4},{facts.PressureMonthlyHpa[0][i]:F2},"
				+ $"{facts.PressureMonthlyHpa[6][i]:F2},{parentOf[i]},"
				+ $"{facts.Wind.Surface.Monthly[5][i].Length():F4}");
		}
		Console.WriteLine("CSV end");
	}

	/// <summary>按 |纬度| 分箱的平均读数（海格 / 陆格分开）：查纬度圈上的跳变用。</summary>
	static void PrintLatitudeProfile(WorldGenSimulation sim)
	{
		var facts = sim.Facts;
		const int Bin = 5;
		int bins = 90 / Bin;
		var oceanT = new double[bins];
		var oceanN = new int[bins];
		var landT = new double[bins];
		var landN = new int[bins];

		for (int i = 0; i < facts.TemperatureC.Length; i++)
		{
			float lat = MathF.Abs(MathF.Asin(Math.Clamp(facts.Ball.CellDirs[i].Y, -1f, 1f))) * 180f / MathF.PI;
			int b = Math.Clamp((int)(lat / Bin), 0, bins - 1);
			if (facts.FinalLand[i]) { landT[b] += facts.TemperatureC[i]; landN[b]++; }
			else { oceanT[b] += facts.TemperatureC[i]; oceanN[b]++; }
		}

		Console.WriteLine("|lat|  ocean°C(n)      land°C(n)");
		for (int b = 0; b < bins; b++)
		{
			string ocean = oceanN[b] > 0 ? $"{oceanT[b] / oceanN[b],7:F1}({oceanN[b],5})" : "      -";
			string land = landN[b] > 0 ? $"{landT[b] / landN[b],7:F1}({landN[b],5})" : "      -";
			Console.WriteLine($"{b * Bin,3}–{b * Bin + Bin,2}  {ocean}   {land}");
		}
	}

	/// <summary>气压读数（季风重建的标定锚点）：20–60°N 海陆均值、极值位置、Δp 符号反转格数。</summary>
	static void PrintPressure(WorldGenSimulation sim)
	{
		var facts = sim.Facts;
		int n = facts.PressureAnnualHpa.Length;
		double annualSum = 0;
		for (int i = 0; i < n; i++) annualSum += facts.PressureAnnualHpa[i];
		Console.WriteLine($"pressure annual mean={annualSum / n:F1} hPa");

		var janOcean = BandOceanMean(facts, 0);
		var julOcean = BandOceanMean(facts, 6);
		PrintPressureMonth(facts, month: 0, label: "Jan");
		PrintPressureMonth(facts, month: 6, label: "Jul");
		PrintPressureBias(facts);

		int flips = 0, strong = 0;
		for (int i = 0; i < n; i++)
		{
			if (!facts.FinalLand[i]) continue;
			int b = BandOf(Lat(facts, i));
			float dJan = facts.PressureMonthlyHpa[0][i] - janOcean[b];
			float dJul = facts.PressureMonthlyHpa[6][i] - julOcean[b];
			if (float.IsNaN(dJan) || float.IsNaN(dJul)) continue;
			if (MathF.Abs(dJan) < 2f || MathF.Abs(dJul) < 2f) continue;
			strong++;
			if (MathF.Sign(dJan) != MathF.Sign(dJul)) flips++;
		}
		Console.WriteLine($"pressure Δp reversal={flips} / {strong} land cells (|Δp|>2 hPa)");

		Console.WriteLine("pressure band mean (all / ocean): Jan → Jul");
		for (int band = 0; band <= 80; band += 10)
		{
			float janAll = MeanPressure(facts, 0, band, band + 10);
			float julAll = MeanPressure(facts, 6, band, band + 10);
			float janSea = MeanPressure(facts, 0, band, band + 10, oceanOnly: true);
			float julSea = MeanPressure(facts, 6, band, band + 10, oceanOnly: true);
			Console.WriteLine($"  {band,2}–{band + 10,2}°  all {janAll:F1}→{julAll:F1}"
				+ $"  ocean {janSea:F1}→{julSea:F1}");
		}
	}

	/// <summary>某个月：20–60°N 的海陆均值，以及陆地最低/最高压的位置与离海跳数。</summary>
	static void PrintPressureMonth(WorldFacts facts, int month, string label)
	{
		int n = facts.PressureAnnualHpa.Length;
		double landSum = 0, oceanSum = 0;
		int landN = 0, oceanN = 0, lowCell = -1, highCell = -1;
		float low = float.MaxValue, high = float.MinValue;

		for (int i = 0; i < n; i++)
		{
			float lat = Lat(facts, i);
			if (lat < 20f || lat > 60f) continue;
			float p = facts.PressureMonthlyHpa[month][i];
			if (facts.FinalLand[i])
			{
				landSum += p; landN++;
				if (p < low) { low = p; lowCell = i; }
				if (p > high) { high = p; highCell = i; }
			}
			else { oceanSum += p; oceanN++; }
		}

		if (landN == 0 || oceanN == 0) { Console.WriteLine($"pressure {label} 取样失败"); return; }
		Console.WriteLine($"pressure {label} 20–60N land={landSum / landN:F1} ocean={oceanSum / oceanN:F1}"
			+ $"  Δp={(landSum / landN - oceanSum / oceanN):+0.0;-0.0} hPa");
		Console.WriteLine($"  land low={low:F1}@{Lat(facts, lowCell):F0},{Lon(facts, lowCell):F0}"
			+ $" dist={facts.FinalDistToCoast[lowCell]} h={facts.HeightM[lowCell]:F0}m"
			+ $" T={facts.TemperatureMonthlyC[month][lowCell]:F0}°C"
			+ $"  high={high:F1}@{Lat(facts, highCell):F0},{Lon(facts, highCell):F0}"
			+ $" dist={facts.FinalDistToCoast[highCell]} h={facts.HeightM[highCell]:F0}m"
			+ $" T={facts.TemperatureMonthlyC[month][highCell]:F0}°C");
	}

	/// <summary>年均海陆差（校验热力项的年均偏置已被扣掉）。</summary>
	static void PrintPressureBias(WorldFacts facts)
	{
		int n = facts.PressureAnnualHpa.Length;
		double landSum = 0, oceanSum = 0;
		int landN = 0, oceanN = 0;
		for (int i = 0; i < n; i++)
		{
			float lat = Lat(facts, i);
			if (lat < 20f || lat > 60f) continue;
			if (facts.FinalLand[i]) { landSum += facts.PressureAnnualHpa[i]; landN++; }
			else { oceanSum += facts.PressureAnnualHpa[i]; oceanN++; }
		}
		if (landN == 0 || oceanN == 0) return;
		Console.WriteLine($"pressure annual 20–60N land−ocean={landSum / landN - oceanSum / oceanN:+0.0;-0.0} hPa");
	}

	/// <summary>纬向风按 10° 带平均（东正）：风带符号从气压梯度里出来没有，一眼能看。</summary>
	static void PrintWind(WorldGenSimulation sim)
	{
		var facts = sim.Facts;
		Console.WriteLine("wind u (east+) by 10° band: Jan / Jul / annual");
		for (int band = 0; band <= 60; band += 10)
		{
			float jan = MeanEastward(facts, 0, band, band + 10);
			float jul = MeanEastward(facts, 6, band, band + 10);
			float annual = MeanEastward(facts, -1, band, band + 10);
			Console.WriteLine($"  {band,2}–{band + 10,2}°  Jan={jan:+0.000;-0.000}"
				+ $"  Jul={jul:+0.000;-0.000}  ann={annual:+0.000;-0.000}");
		}

		Console.WriteLine("wind u ocean-only by 10° band: Jan / Jul");
		for (int band = 0; band <= 60; band += 10)
		{
			float jan = MeanEastward(facts, 0, band, band + 10, oceanOnly: true);
			float jul = MeanEastward(facts, 6, band, band + 10, oceanOnly: true);
			Console.WriteLine($"  {band,2}–{band + 10,2}°  Jan={jan:+0.000;-0.000}  Jul={jul:+0.000;-0.000}");
		}

		int coastal = 0, reversed = 0, coastalLand = 0;
		for (int i = 0; i < facts.Wind.Surface.Annual.Length; i++)
		{
			if (!facts.FinalLand[i] || facts.FinalDistToCoast[i] != 1) continue;
			coastalLand++;
			var dir = facts.Ball.CellDirs[i];
			var north = Vector3.Up - dir * dir.Dot(Vector3.Up);
			if (north.LengthSquared() < 1e-9f) continue;
			north = north.Normalized();
			var east = dir.Cross(north).Normalized();

			bool eastCoast = false;
			foreach (int nb in facts.Ball.CellNeighbors[i])
			{
				if (facts.FinalLand[nb]) continue;
				var edge = facts.Ball.CellDirs[nb] - dir;
				float len = edge.Length();
				if (len > 1e-9f && edge.Dot(east) / len > 0.5f) { eastCoast = true; break; }
			}
			if (!eastCoast) continue;

			var jan = facts.Wind.Surface.Monthly[0][i];
			var jul = facts.Wind.Surface.Monthly[6][i];
			if (jan.LengthSquared() < 1e-6f || jul.LengthSquared() < 1e-6f) continue;

			coastal++;
			float cos = Math.Clamp(jan.Normalized().Dot(jul.Normalized()), -1f, 1f);
			if (MathF.Acos(cos) > 2f * MathF.PI / 3f) reversed++;
		}
		Console.WriteLine($"wind east-coast reversal ≥120°: {reversed} / {coastal} (coastal land={coastalLand})");
	}

	static float MeanPressure(WorldFacts facts, int month, float latMin, float latMax, bool oceanOnly = false)
	{
		double sum = 0;
		int count = 0;
		for (int i = 0; i < facts.PressureAnnualHpa.Length; i++)
		{
			if (oceanOnly && facts.FinalLand[i]) continue;
			float lat = Lat(facts, i);
			if (lat < latMin || lat >= latMax) continue;
			sum += facts.PressureMonthlyHpa[month][i];
			count++;
		}
		return count > 0 ? (float)(sum / count) : float.NaN;
	}

	/// <summary>某月（−1 = 年合成）在纬度带内的平均纬向风。</summary>
	static float MeanEastward(WorldFacts facts, int month, float latMin, float latMax, bool oceanOnly = false)
	{
		double sum = 0;
		int count = 0;
		for (int i = 0; i < facts.Wind.Surface.Annual.Length; i++)
		{
			if (oceanOnly && facts.FinalLand[i]) continue;
			float lat = Lat(facts, i);
			if (lat < latMin || lat >= latMax) continue;
			var dir = facts.Ball.CellDirs[i];
			var north = Vector3.Up - dir * dir.Dot(Vector3.Up);
			if (north.LengthSquared() < 1e-9f) continue;
			north = north.Normalized();
			var east = dir.Cross(north).Normalized();
			var wind = month < 0 ? facts.Wind.Surface.Annual[i] : facts.Wind.Surface.Monthly[month][i];
			sum += wind.Dot(east);
			count++;
		}
		return count > 0 ? (float)(sum / count) : 0f;
	}

	/// <summary>5° 纬度带的海格平均气压（NaN = 该带无海）。</summary>
	static float[] BandOceanMean(WorldFacts facts, int month)
	{
		var sums = new double[36];
		var counts = new int[36];
		for (int i = 0; i < facts.PressureAnnualHpa.Length; i++)
		{
			if (facts.FinalLand[i]) continue;
			int b = BandOf(Lat(facts, i));
			sums[b] += facts.PressureMonthlyHpa[month][i];
			counts[b]++;
		}
		var mean = new float[36];
		for (int b = 0; b < 36; b++) mean[b] = counts[b] > 0 ? (float)(sums[b] / counts[b]) : float.NaN;
		return mean;
	}

	/// <summary>地图变体轴：风场模式的三档都能取色（数据路径通，不只编译通过）。</summary>
	static void CheckWindModeVariants(WorldGenSimulation sim)
	{
		var mode = new WindMode(sim);
		mode.SetParameter(6);   // 7 月
		var colors = new Color[3];
		for (int k = 0; k < 3; k++)
		{
			mode.SetVariant(k);
			colors[k] = mode.CellColorAt(0);
		}
		if (mode.VariantOptions == null || mode.VariantOptions.Count != 3)
			throw new Exception("风场变体轴应有 3 档");
		Console.WriteLine($"wind variants: {string.Join(" / ", mode.VariantOptions)}"
			+ $"  selected={mode.SelectedKind}  colors={colors[0]} {colors[1]} {colors[2]}");
	}

	/// <summary>风速在 res2 父格边界的粗糙度（跨边界 / 父格内）。1 附近 = 梯度连续。</summary>
	static void CheckParentSpeedRoughness(WorldGenSimulation sim)
	{
		var facts = sim.Facts;
		var ball = facts.Ball;
		if (ball.Res <= 2) return;

		var parentIndex = new System.Collections.Generic.Dictionary<ulong, int>();
		var parentOf = new int[ball.CellIds.Length];
		for (int i = 0; i < parentOf.Length; i++)
		{
			ulong parent = World.Utils.H3.H3.CellToParent(ball.CellIds[i], ball.Res - 2);
			if (!parentIndex.TryGetValue(parent, out int k))
			{
				k = parentIndex.Count;
				parentIndex[parent] = k;
			}
			parentOf[i] = k;
		}

		var speed = facts.Wind.Surface.Monthly[5];
		double across = 0, within = 0;
		int nA = 0, nW = 0;
		for (int i = 0; i < speed.Length; i++)
		{
			foreach (int nb in ball.CellNeighbors[i])
			{
				if (nb < i) continue;
				float d = MathF.Abs(speed[i].Length() - speed[nb].Length());
				if (parentOf[i] != parentOf[nb]) { across += d; nA++; }
				else { within += d; nW++; }
			}
		}
		Console.WriteLine($"wind parent-boundary speed ratio={(across / nA) / (within / nW):F3}"
			+ $"  (across={across / nA:F5} within={within / nW:F5})");
	}

	/// <summary>生成一次世界并导出「地壳」地图（判读构造通道用）；不写默认测试表。</summary>
	public static void ExportCrust(int res)
	{
		var sim = new WorldGenSimulation(res, 1f);
		sim.Run(new WorldSpec());
		var mode = new CrustMode(sim);
		mode.BeginBake();

		var counts = new int[Enum.GetValues<TectonicChannel>().Length];
		foreach (TectonicChannel channel in Enum.GetValues<TectonicChannel>())
			if (mode.Influence.Has(channel)) counts[(int)channel] = mode.Influence.Get(channel).Sources.Length;

		int continental = 0;
		foreach (CrustType type in sim.Facts.Crust.Type)
			if (type == CrustType.Continental) continental++;

		Console.WriteLine($"crust cells={sim.Facts.PlateOfCell.Length} continental={continental}"
			+ $" oceanic={sim.Facts.PlateOfCell.Length - continental}"
			+ $" provinces={sim.Facts.Crust.Provinces.Length}");
		foreach (CrustProvince province in sim.Facts.Crust.Provinces)
			Console.WriteLine($"  province {province.Id} plate={province.SeedPlateId}"
				+ $" cells={province.CellCount} seed={province.SeedCell}");
		foreach (TectonicChannel channel in Enum.GetValues<TectonicChannel>())
			Console.WriteLine($"  {channel} sources={counts[(int)channel]}");

		int width = res >= 4 ? 4096 : 2048;
		int height = width / 2;
		mode.SetVariant(CrustMode.CrustOnlyVariant);
		Console.WriteLine($"crust types ppm={WritePpm(sim, mode, width, height, $"crust_res{res}_types.ppm")}");
		mode.SetVariant(CrustMode.ConstructionVariant);
		Console.WriteLine($"crust ppm={WritePpm(sim, mode, width, height, $"crust_res{res}.ppm")}");
	}

	/// <summary>导出地形图（验证地形是否跟着构造通道走）。</summary>
	public static void ExportTerrainMap(int res)
	{
		var sim = new WorldGenSimulation(res, 1f);
		sim.Run(new WorldSpec());
		int width = res >= 4 ? 4096 : 2048;
		Console.WriteLine($"terrain ppm={WritePpm(sim, new TerrainMode(sim), width, width / 2, $"terrain_res{res}.ppm")}");
	}

	static string WritePpm(WorldGenSimulation sim, MapMode mode, int width, int height, string name)
	{
		mode.BeginBake();
		byte[] rgba = MapExporter.RenderPixels(sim, mode, width, height);
		string dir = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "map_exports");
		System.IO.Directory.CreateDirectory(dir);
		string ppm = System.IO.Path.Combine(dir, name);
		using (var file = System.IO.File.Create(ppm))
		{
			file.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
			var rgb = new byte[width * height * 3];
			for (int i = 0, o = 0; i < rgba.Length; i += 4, o += 3)
			{
				rgb[o] = rgba[i];
				rgb[o + 1] = rgba[i + 1];
				rgb[o + 2] = rgba[i + 2];
			}
			file.Write(rgb);
		}
		return ppm;
	}

	/// <summary>构造影响场一致性：每个来源格自身可达，来源编号指回同通道来源，距离有限且非负。</summary>
	static void CheckTectonicInfluence(WorldGenSimulation sim)
	{
		const double capKm = 3000.0;
		TectonicInfluenceSet set = TectonicInfluenceOps.Build(sim.Ball, sim.Plate.Boundaries,
			sim.Plate.Plates, sim.Plate.Crust, sim.Plate.PlateOfCell,
			sim.Plate.Boundary.EdgeMotion, capKm);
		int n = sim.Ball.CellDirs.Length;
		int sourceCount = 0;
		var channels = 0;
		foreach (TectonicChannel channel in Enum.GetValues<TectonicChannel>())
		{
			TectonicInfluenceField field = set.Get(channel);
			if (!set.Has(channel)) continue;
			channels++;
			sourceCount += field.Sources.Length;
			if (field.DistanceKm.Length != n || field.SourceIndex.Length != n)
				throw new Exception($"构造通道 {channel} 的场长度 ≠ 格数");
			for (int i = 0; i < n; i++)
			{
				if (field.SourceIndex[i] < 0) continue;
				if (field.SourceIndex[i] >= field.Sources.Length)
					throw new Exception($"构造通道 {channel} 的来源下标越界");
				if (field.Sources[field.SourceIndex[i]].Channel != channel)
					throw new Exception($"构造通道 {channel} 的来源通道不符");
				if (!float.IsFinite(field.DistanceKm[i]) || field.DistanceKm[i] < 0f)
					throw new Exception($"构造通道 {channel} 的距离非法");
			}
			for (int k = 0; k < field.Sources.Length; k++)
			{
				int cell = field.Sources[k].SideCell;
				if (field.SourceIndex[cell] < 0)
					throw new Exception($"构造通道 {channel} 的来源格 {cell} 自身不可达");
			}
		}
		Console.WriteLine($"tectonic sources={sourceCount} channels={channels}");
	}

	/// <summary>某一档风的全球平均年均风速（0–1 代理量）。</summary>
	static float MeanAnnualSpeed(WorldFacts facts, WindKind kind)
	{
		var set = facts.Wind.Get(kind);
		double sum = 0;
		for (int i = 0; i < set.AnnualSpeed.Length; i++) sum += set.AnnualSpeed[i];
		return set.AnnualSpeed.Length > 0 ? (float)(sum / set.AnnualSpeed.Length) : 0f;
	}

	static float Lat(WorldFacts facts, int i) =>
		MathF.Asin(Math.Clamp(facts.Ball.CellDirs[i].Y, -1f, 1f)) * 180f / MathF.PI;

	static float Lon(WorldFacts facts, int i) =>
		MathF.Atan2(facts.Ball.CellDirs[i].Z, facts.Ball.CellDirs[i].X) * 180f / MathF.PI;

	static int BandOf(float lat) => Math.Clamp((int)((lat + 90f) / 5f), 0, 35);

	static void Number(string label, float value)
	{
		if (!float.IsFinite(value)) throw new Exception($"{label} 不是有限数");
	}
}
