using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using World.Spatial;
using World.WorldGen;
using Godot;

namespace World.Tests;

/// <summary>
/// **O-O2 τ₀ 扫参取证**（⑱ P4-5e ②；用户拍板 2026-10-07：τ₀ = 1×/2×/3×/4×，**r/H 固定**，
/// 禁止事实归一化/后处理放大 Speed）。★`[Explicit]` = 只显式运行的取证 harness，**不是护栏**，
/// 不进常规 CI；结果供"速度 + 结构 + 输运"联合验收选档（**选最低合格档**，不追"最像地球"）。
/// 双口径统计（用户拍板）：`cur` = 全海格均值（生产诊断主口径）；p50/90/98/max = 流动格形状诊断。
/// 结构验收点：副热带 ψ 符号（NH>0/SH<0）、副极环分立、西边界集中度（max/p50）、δ 可分辨性。
/// 输运验收点（D3 降级裁决后）：`dTxm` = 洋流影响的月度调制最大幅度（随 Speed 单调，
/// 不再被 CFL 钉死）；`anchorMax/dToc` = 逐格年均锚自检（扣均值恒等式 ⇒ 应 ≈ 0）。
/// Gain=0 退化与 τ₀ 无关（求解器在 gain=0 时短路，环流场根本不进入）——由既有护栏
/// `InfluenceOff_IsBitwiseP45cBaseline` 钉死，扫参不重复。
/// </summary>
public class OceanTau0Scan
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(4, 1f));
	static Ball Ball => SharedBall.Value;

	static readonly Lazy<(FinalGeography f, TemperatureModel t, OceanRegions r)> Shared = new(() => Make());
	static (FinalGeography f, TemperatureModel t, OceanRegions r) Shared2 => Shared.Value;

	static (FinalGeography f, TemperatureModel t, OceanRegions r) Make()
	{
		var layout = new ContinentLayout(42, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = 42 });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var gr = new GeologicalRegions(42);
		gr.Generate(Ball, proj, 8_000_000f);
		var m = new MountainSkeleton(42, baseSigmaKm: 520f);
		m.Generate(Ball, gr, surface);
		var l = new RegionalLandforms(42);
		l.Generate(Ball, gr);
		var features = new List<FeatureField>
		{
			new(l, TerrainDomain.LandOnly),
			new(m, TerrainDomain.LandAndSea),
		};
		var c = new HeightComposer(42);
		c.Generate(Ball, surface, gr, features);
		var f = new FinalGeography();
		f.Generate(Ball, c, gr);
		var t = new TemperatureModel();
		t.Generate(Ball, f, c);
		var r = new OceanRegions();
		r.Generate(Ball, f);
		return (f, t, r);
	}

	[Test, Explicit]
	public void ScanTau0_1x_2x_3x_4x_OnRes4()
	{
		var (final, tempModel, regions) = Shared2;
		var annual = tempModel.AnnualMeanC;
		var closed = ThermalResponseSolver.Solve(Ball, final, annual);
		var dirs = Ball.CellDirs;
		const double Deg = Math.PI / 180.0;
		double betaAt45 = OceanCurrentModel.BetaParameter(45 * Deg);
		double deltaCells = (new OceanCurrentModel().FrictionRatePerSecond / betaAt45)
			/ (SpatialScale.Of(Ball).CellEdgeKm * 1000.0);

		Console.WriteLine($"[TAU0SCAN] res4 n={dirs.Length} land={100.0 * final.FinalLand.Count(BooleanRes => BooleanRes) / dirs.Length:F1}% " +
			$"delta=r/beta={deltaCells:F1} res4 格（可分辨性基准）");

		foreach (var mult in new[] { 1, 2, 3, 4 })
		{
			double baseTau0 = new OceanCurrentModel().WindStressAmplitudeNPerM2;
			var model = new OceanCurrentModel
			{
				WindStressAmplitudeNPerM2 = baseTau0 * mult,
			};
			var currents = model.Generate(Ball, final, regions);

			// ── 速度统计（双口径）──
			var flowing = new List<float>(currents.CellCount);
			double allSum = 0;
			for (int i = 0; i < currents.CellCount; i++)
			{
				allSum += currents.SpeedKmPerMonth[i];
				if (currents.SpeedKmPerMonth[i] > 0f) flowing.Add(currents.SpeedKmPerMonth[i]);
			}
			flowing.Sort();
			float P(double q) => flowing[(int)Math.Clamp((long)(q * (flowing.Count - 1)), 0, flowing.Count - 1)];
			double allMean = allSum / currents.CellCount;
			const double KmMonthToMs = 1.0 / 2629.8;

			// ── 结构（ψ 带均值符号；同 OceanCurrentModelTests 口径）──
			var psi = model.StreamFunctionDiagnostic;
			double BandMean(double latMin, double latMax)
			{
				double sum = 0; int cnt = 0;
				for (int i = 0; i < dirs.Length; i++)
				{
					if (final.FinalLand[i]) continue;
					double latDeg = Math.Asin(Math.Clamp(dirs[i].Y, -1f, 1f)) / Deg;
					if (latDeg < latMin || latDeg > latMax) continue;
					sum += psi[i]; cnt++;
				}
				return cnt > 0 ? sum / cnt : double.NaN;
			}
			double nhSub = BandMean(15, 35), shSub = BandMean(-35, -15), nhSubpolar = BandMean(55, 65);

			// ── 影响效应（同批 solve；gain=1；D3 降级版 = 月度调制 + 逐格年均锚自检）──
			var climate = CoupledClimateSolver.Solve(Ball, final, annual,
				currentInfluenceGain: ClimateParameters.OceanCurrentInfluenceGain, oceanCurrents: currents);
			float dTxm = 0f, anchorMax = 0f;
			for (int i = 0; i < dirs.Length; i++)
			{
				if (final.FinalLand[i]) continue;
				anchorMax = MathF.Max(anchorMax,
					MathF.Abs(climate.Temperature.AnnualMeanAt(i) - closed.AnnualMeanAt(i)));
				for (int m = 0; m < MonthlyField.Months; m++)
					dTxm = MathF.Max(dTxm, MathF.Abs(climate.Temperature.At(m, i) - closed.At(m, i)));
			}
			double dToc = climate.Temperature.MeanC - closed.MeanC;

			Console.WriteLine(
				$"[TAU0SCAN] tau0={mult}x " +
				$"cur={allMean:F1}({allMean * KmMonthToMs:F4}m/s) " +
				$"p50={P(0.50):F1}({P(0.50) * KmMonthToMs:F4}) p90={P(0.90):F1}({P(0.90) * KmMonthToMs:F4}) " +
				$"p98={P(0.98):F1}({P(0.98) * KmMonthToMs:F4}) max={flowing[^1]:F1}({flowing[^1] * KmMonthToMs:F3})m/s " +
				$"max/p50={(flowing[^1] / MathF.Max(1e-6f, P(0.50))):F0} " +
				$"psiNHsub={nhSub:E2} psiSHsub={shSub:E2} psiNHsubpolar={nhSubpolar:E2} " +
				$"converged={climate.Converged} years={climate.SpinupYearsUsed} " +
				$"dTxm={dTxm:F3}C anchorMax={anchorMax:E2}C dToc={dToc:+0.0000;-0.0000}C " +
				$"monAmpT={climate.Temperature.MeanAmplitudeC:F2}C");

			// 结构护栏（扫参中结构塌了要立刻红，不能等选档时才发现）
			Assert.That(nhSub, Is.GreaterThan(0), $"τ₀={mult}x：NH 副热带 ψ 须 > 0（顺时针环）");
			Assert.That(shSub, Is.LessThan(0), $"τ₀={mult}x：SH 副热带 ψ 须 < 0（逆时针环）");
			Assert.That(nhSub, Is.GreaterThan(nhSubpolar), $"τ₀={mult}x：副热带环与副极环须分立");
			Assert.That(climate.Converged, Is.True, $"τ₀={mult}x：耦合 Solver 须收敛");
			Assert.That(anchorMax, Is.LessThan(5e-3f), $"τ₀={mult}x：逐格年均锚须保持（条款 10 强形式）");
			Assert.That(Math.Abs(dToc), Is.LessThan(5e-3f), $"τ₀={mult}x：全球年均差须 ≈ 0（逐格锚的推论）");
		}
	}
}
