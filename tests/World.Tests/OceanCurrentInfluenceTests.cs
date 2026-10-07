using System;
using System.Collections.Generic;
using NUnit.Framework;
using World.Spatial;
using World.WorldGen;
using Godot;

namespace World.Tests;

/// <summary>
/// **洋流温度影响护栏**（Phase 4 · P4-5d · **D3 降级版**，2026-10-07；
/// 裁决：docs/裁决-D3降级-洋流温度影响.md——方向性温度趋近 + 扣 12 月均值，
/// 取代原"边通量/CFL/零和守恒输运"机制，旧护栏随之退役）。
///
/// ★验收范围（裁决 §五 六钉）：
///   ① **退化**（原则 4 / F1 基线）：`currentInfluenceGain = 0` ⇒ 输出**逐位** = P4-5c 冻结基线；
///   ② **陆格不变**：海-陆热影响 = 0，陆格 12 月温度逐位 = 闭式解；
///   ③ **年均锚（条款 10 强形式）**：每个海格 AnnualMean 不变（Σ_m ΔT = 0 逐格恒等式）；
///   ④ **方向性**：合成三格链上游换位 ⇒ 月度响应换源（季节相位跟随上游客体）；
///   ⑤ **确定性**：同输入两次求解逐位同；
///   ⑥ **停滞格豁免**：Speed = 0 的海格温度逐位不动。
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService；res2 手工装配（同 P4-5c 测试）。
/// </summary>
public class OceanCurrentInfluenceTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(2, 1f));
	static Ball Ball => SharedBall.Value;

	static readonly Lazy<(FinalGeography f, TemperatureModel t, OceanCurrentField c)> Shared =
		new(() => Make(42));
	static (FinalGeography f, TemperatureModel t, OceanCurrentField c) Shared2 => Shared.Value;

	static (FinalGeography f, TemperatureModel t, OceanCurrentField c) Make(int seed = 42)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var gr = new GeologicalRegions(seed);
		gr.Generate(Ball, proj, 8_000_000f);
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);
		m.Generate(Ball, gr, surface);
		var l = new RegionalLandforms(seed);
		l.Generate(Ball, gr);
		var features = new List<FeatureField>
		{
			new(l, TerrainDomain.LandOnly),
			new(m, TerrainDomain.LandAndSea),
		};
		var c = new HeightComposer(seed);
		c.Generate(Ball, surface, gr, features);
		var f = new FinalGeography();
		f.Generate(Ball, c, gr);
		var t = new TemperatureModel();
		t.Generate(Ball, f, c);
		// ⑮ D2：海域划分 + 稳态环流（零 Climate 依赖）
		var regions = new OceanRegions();
		regions.Generate(Ball, f);
		var currents = new OceanCurrentModel().Generate(Ball, f, regions);
		return (f, t, currents);
	}

	static float[] AnnualMeanC => Shared2.t.AnnualMeanC;
	static FinalGeography Final => Shared2.f;
	static OceanCurrentField Currents => Shared2.c;

	static ClimateState Solve(float currentInfluenceGain, float evapGain = ClimateParameters.EvapGainMmPerMonthC)
		=> CoupledClimateSolver.Solve(Ball, Final, AnnualMeanC, evapGain,
			currentInfluenceGain, currentInfluenceGain > 0f ? Currents : null);

	// ── ① 退化（原则 4 / F1）────────────────────────────────────────────────────

	/// <summary>`currentInfluenceGain = 0` ⇒ 温度/水汽/降水**逐位**等于 P4-5c 冻结基线（影响链整体短路）。</summary>
	[Test]
	public void InfluenceOff_IsBitwiseP45cBaseline()
	{
		var off = Solve(0f);
		var p45c = CoupledClimateSolver.Solve(Ball, Final, AnnualMeanC);
		for (int k = 0; k < off.Temperature.C.Length; k++)
			Assert.That(off.Temperature.C[k], Is.EqualTo(p45c.Temperature.C[k]),
				"gain=0 温度分量须与 P4-5c 基线逐位相同");
		for (int k = 0; k < off.WaterVapor.V.Length; k++)
			Assert.That(off.WaterVapor.V[k], Is.EqualTo(p45c.WaterVapor.V[k]),
				"gain=0 水汽分量须与 P4-5c 基线逐位相同");
		for (int k = 0; k < off.Precipitation.V.Length; k++)
			Assert.That(off.Precipitation.V[k], Is.EqualTo(p45c.Precipitation.V[k]),
				"gain=0 降水分量须与 P4-5c 基线逐位相同");
	}

	// ── ② 陆格不变 ──────────────────────────────────────────────────────────────

	/// <summary>影响开启：陆格 12 月温度**逐位**不动（海-陆热影响 = 0；陆地无耦合）。</summary>
	[Test]
	public void InfluenceOn_LandBitwiseUntouched()
	{
		var closed = Solve(0f);
		var on = Solve(ClimateParameters.OceanCurrentInfluenceGain);
		for (int i = 0; i < Final.FinalLand.Length; i++)
		{
			if (!Final.FinalLand[i]) continue;
			for (int m = 0; m < MonthlyField.Months; m++)
				Assert.That(on.Temperature.At(m, i), Is.EqualTo(closed.Temperature.At(m, i)),
					$"陆格 {i} 月 {m}：温度被影响改动（海-陆热影响必须为 0）");
		}
	}

	// ── ③ 年均锚（条款 10 强形式：Σ_m ΔT = 0 逐格恒等式）────────────────────────

	/// <summary>
	/// 每个海格的年平均温度**逐格**保持（扣 12 月均值 ⇒ 恒等式；容差只吸收 float 求和舍入），
	/// 全球 MeanC 同样不动；同时影响须**真实存在**（海格月度场确实被调制，非均匀缩放）。
	/// </summary>
	[Test]
	public void InfluenceOn_AnnualAnchorPerCell_InfluenceReal()
	{
		var closed = Solve(0f);
		var on = Solve(ClimateParameters.OceanCurrentInfluenceGain);

		Assert.That(on.Converged, Is.True,
			$"影响开启须正常收敛（实际 {on.SpinupYearsUsed} 年）");

		float maxAnnualDrift = 0f;
		float maxMonthlyDelta = 0f;
		float minMonthlyDelta = float.MaxValue;
		int changed = 0;
		for (int i = 0; i < Final.FinalLand.Length; i++)
		{
			if (Final.FinalLand[i]) continue;
			float drift = MathF.Abs(on.Temperature.AnnualMeanAt(i) - closed.Temperature.AnnualMeanAt(i));
			if (drift > maxAnnualDrift) maxAnnualDrift = drift;
			for (int m = 0; m < MonthlyField.Months; m++)
			{
				float d = on.Temperature.At(m, i) - closed.Temperature.At(m, i);
				if (MathF.Abs(d) > 1e-4f) changed++;
				maxMonthlyDelta = MathF.Max(maxMonthlyDelta, d);
				minMonthlyDelta = MathF.Min(minMonthlyDelta, d);
			}
		}
		Assert.That(maxAnnualDrift, Is.LessThan(1e-3f),
			$"海格年均锚被破坏：最大漂移 {maxAnnualDrift:E2} °C（扣均值恒等式要求 ≈ 0，条款 10 强形式）");
		Assert.That(MathF.Abs(on.Temperature.MeanC - closed.Temperature.MeanC), Is.LessThan(1e-3f),
			"全球年均温度须不动（逐格锚的直接推论）");
		Assert.That(changed, Is.GreaterThan(100), "海格月度温度应有可观的被调制数（影响真实存在）");
		Assert.That(maxMonthlyDelta > 0f && minMonthlyDelta < 0f, Is.True,
			"月度调制须有正有负（只动月内结构：某些月被拉向上游、某些月被推离），非整体升降温");
	}

	// ── ④ 方向性（合成三格链：上游换位 ⇒ 月度响应换源）────────────────────────

	/// <summary>
	/// 合成实验：取海格 j（≥2 个海格邻居），令上游 i、背向侧 k。j 有流、i/k 静止。
	/// 基础月度场：T_i = 15 + 10·cos、T_j = 15、T_k = 15 + 10·sin（上游客体季节相位不同）。
	/// 正向流（i→j）：j 的月度响应 ∝ cos（跟随 i）；Direction 反转：响应 ∝ sin（上游换为 k）。
	/// 同时验证影响幅度公式 `inf = Speed/(Speed+v_half)` 与扣均值恒等式（年均锚 = 15）。
	/// </summary>
	[Test]
	public void UpstreamSwap_ReversesMonthlyResponse()
	{
		var final = Final;
		var land = final.FinalLand;
		int n = land.Length;
		var neighbors = Ball.CellNeighbors;
		var tans = Ball.CellNeighborDirs;

		// ── 选格：j = 有 ≥2 海格邻居的海格；i = 第一个海格邻居；
		//    k = 其余海格邻居中与 −t̂(j→i) 对齐最好者（反向流时的确定性上游）。
		int j = -1, i = -1, k = -1;
		float kAlign = 0f;
		for (int c = 0; c < n && j < 0; c++)
		{
			if (land[c]) continue;
			int first = -1;
			float bestBack = 0f;
			int bestBackIdx = -1;
			foreach (int nb in neighbors[c])
			{
				if (land[nb]) continue;
				if (first < 0) { first = nb; continue; }
				var t = TangentAt(tans, neighbors, c, nb);
				var ti = TangentAt(tans, neighbors, c, first);
				float back = -(ti.X * t.X + ti.Y * t.Y + ti.Z * t.Z);   // dot(−t̂(c→first), t̂(c→nb))
				if (back > bestBack) { bestBack = back; bestBackIdx = nb; }
			}
			if (first >= 0 && bestBackIdx >= 0 && bestBack > 0.5f)
			{
				j = c; i = first; k = bestBackIdx; kAlign = bestBack;
			}
		}
		Assert.That(j, Is.GreaterThanOrEqualTo(0), "res2 场须存在带两个海格邻居的格（fixture 前提）");

		// ── 基础月度场（闭式解替换为合成值；其余格任意——它们 Speed=0/陆 ⇒ 不被触碰）
		var temp = MonthlyTemperature.Create(n);
		for (int c = 0; c < n; c++)
			for (int m = 0; m < MonthlyField.Months; m++)
				temp.Set(m, c, 15f);
		for (int m = 0; m < MonthlyField.Months; m++)
		{
			float th = 2f * MathF.PI * m / MonthlyField.Months;
			temp.Set(m, i, 15f + 10f * MathF.Cos(th));
			temp.Set(m, k, 15f + 10f * MathF.Sin(th));
		}

		// ── 合成环流：只有 j 有流（50 km/月）；Direction 由上游语义手工设定
		const float speed = 50f;
		var dirs = new Vector3[n];
		var speeds = new float[n];
		var strength = new float[n];
		var tji = TangentAt(tans, neighbors, j, i);        // t̂(j→i)（单位切向）
		speeds[j] = speed;

		float InfluenceOf(float spd) => spd / (spd + ClimateParameters.CurrentInfluenceHalfSpeedKmPerMonth);
		float inf = InfluenceOf(speed);                    // gain = 1

		// 正向流 i→j：j 的去向 ≈ 离开 i ⇒ Direction_j = −t̂(j→i)；上游查找 = argmax dot(−Direction, t̂) ⇒ i
		var fieldFwd = new OceanCurrentField();
		dirs[j] = -tji;
		fieldFwd.Fill(dirs, speeds, strength, n, 1);
		var tFwd = MonthlyTemperature.Create(n);
		Array.Copy(temp.C, tFwd.C, temp.C.Length);
		CoupledClimateSolver.ApplyCurrentInfluence(Ball, final, fieldFwd, 1f, tFwd.C);

		// 反向流 j→i：Direction_j = +t̂(j→i)；上游 = argmax dot(−t̂(j→i), t̂) ⇒ k（对齐 = kAlign > 0.5）
		var fieldRev = new OceanCurrentField();
		dirs[j] = tji;
		fieldRev.Fill(dirs, speeds, strength, n, 1);
		var tRev = MonthlyTemperature.Create(n);
		Array.Copy(temp.C, tRev.C, temp.C.Length);
		CoupledClimateSolver.ApplyCurrentInfluence(Ball, final, fieldRev, 1f, tRev.C);

		// ── 断言：响应换源 + 幅度公式 + 扣均值恒等式
		for (int m = 0; m < MonthlyField.Months; m++)
		{
			float th = 2f * MathF.PI * m / MonthlyField.Months;
			Assert.That(tFwd.At(m, j), Is.EqualTo(15f + inf * 10f * MathF.Cos(th)).Within(1e-3f),
				$"正向流月 {m}：j 应跟随上游 i 的季节相位（∝ cos，幅度 = inf·10）");
			Assert.That(tRev.At(m, j), Is.EqualTo(15f + inf * 10f * MathF.Sin(th)).Within(1e-3f),
				$"反向流月 {m}：上游应换为 k（∝ sin）——方向反转 ⇒ 响应换源");
			// i / k 静止（Speed = 0）⇒ 逐位不动
			Assert.That(tFwd.At(m, i), Is.EqualTo(temp.At(m, i)), "静止源格 i 不得被修改");
			Assert.That(tFwd.At(m, k), Is.EqualTo(temp.At(m, k)), "静止格 k 不得被修改");
		}
		Assert.That(MathF.Abs(tFwd.AnnualMeanAt(j) - 15f), Is.LessThan(1e-3f), "正向：j 年均锚不动");
		Assert.That(MathF.Abs(tRev.AnnualMeanAt(j) - 15f), Is.LessThan(1e-3f), "反向：j 年均锚不动");
		Assert.That(kAlign, Is.GreaterThan(0.5f), "反向上游 k 的对齐须显著（否则上游选择不可靠）");
	}

	/// <summary>取格 `c` 指向邻居 `nb` 的切向单位向量（Ball.CellNeighborDirs 对齐口径）。</summary>
	static Vector3 TangentAt(Vector3[][] tans, int[][] neighbors, int c, int nb)
	{
		for (int idx = 0; idx < neighbors[c].Length; idx++)
			if (neighbors[c][idx] == nb) return tans[c][idx];
		throw new ArgumentException("nb 不是 c 的邻居");
	}

	// ── ⑤⑥ 确定性 + 停滞格豁免 ────────────────────────────────────────────────

	/// <summary>确定性：同输入两次求解逐位同（无随机项；承 P4-5c 纪律）。</summary>
	[Test]
	public void InfluenceOn_IsBitwiseReproducible()
	{
		var a = Solve(ClimateParameters.OceanCurrentInfluenceGain);
		var b = Solve(ClimateParameters.OceanCurrentInfluenceGain);
		for (int k = 0; k < a.Temperature.C.Length; k++)
			Assert.That(a.Temperature.C[k], Is.EqualTo(b.Temperature.C[k]), "温度须逐位同");
		for (int k = 0; k < a.WaterVapor.V.Length; k++)
			Assert.That(a.WaterVapor.V[k], Is.EqualTo(b.WaterVapor.V[k]), "水汽须逐位同");
	}

	/// <summary>停滞格豁免：Speed = 0 的海格（POCS 停滞/静风哨兵）12 月温度逐位不动。</summary>
	[Test]
	public void InfluenceOn_StagnantCellsBitwiseUntouched()
	{
		var closed = Solve(0f);
		var on = Solve(ClimateParameters.OceanCurrentInfluenceGain);
		int stagnant = 0;
		for (int i = 0; i < Final.FinalLand.Length; i++)
		{
			if (Final.FinalLand[i] || Currents.SpeedKmPerMonth[i] > 0f) continue;
			stagnant++;
			for (int m = 0; m < MonthlyField.Months; m++)
				Assert.That(on.Temperature.At(m, i), Is.EqualTo(closed.Temperature.At(m, i)),
					$"停滞海格 {i} 月 {m}：无流 ⇒ 无影响（须逐位不动）");
		}
		Assert.That(stagnant, Is.GreaterThan(0), "res2 fixture 应存在停滞海格（D2 贴岸投影的真实输出）");
	}

	// ── 水量边界（影响开启下收支仍是结构性质——水循环路径未动）────────────────

	[Test]
	public void InfluenceOn_WaterBalanceStillHolds()
	{
		var on = Solve(ClimateParameters.OceanCurrentInfluenceGain);
		Assert.That(on.GlobalAnnualEvapMm, Is.GreaterThan(100f));
		double relErr = Math.Abs(on.GlobalAnnualPrecipMm - on.GlobalAnnualEvapMm) / on.GlobalAnnualEvapMm;
		Assert.That(relErr, Is.LessThan(1e-3),
			$"影响开启下全球收支：P={on.GlobalAnnualPrecipMm:F2} vs E={on.GlobalAnnualEvapMm:F2}（相对差 {relErr:E2}）");
	}
}
