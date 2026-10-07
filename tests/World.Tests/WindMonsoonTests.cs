using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using World.Spatial;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · WindField 批次 4 护栏（⑯ 季风符号反转 + 最终统计收口 + D2；设计 §4.5/§十四）：
///   · ★硬线①：季风**只改方向，永不改速度**（全场 SpeedMs 逐位不变，延续批次 3 纪律）；
///   · ★硬线②：MonsoonIndex 只表达**全年季节性风向翻转强度**（MRI），与静风哨兵
///     （DirectionTo=0）语义独立、永不互替——静风格 MRI = 0；
///   · ★硬线③：D2 关断（Strength=0 / 门控 ∞ / 零对比）⇒ 输出与批次 3 **逐数组逐位**相等；
///   · ★硬线④：两岸衔接 = 既有 BFS 距离事实的**只读查询**（同一数组实例引用），
///     不为 Wind 生成任何新距离场。
/// 纪律：只用 [Test]；不写文件；不碰 GD.*/LogService。
/// </summary>
public class WindMonsoonTests
{
	const float D2R = MathF.PI / 180f;

	// ── ① 权重纯函数：门控 / 衰减 / 上限 / 三重关断（设计 §4.5）──────────────────

	[Test]
	public void MonsoonWeight_Gate_Decay_Reach_Shutoff()
	{
		// 门控：Cmax < 0.5 °C ⇒ 无季风（滤弱对比区）
		Assert.That(WindFieldModel.MonsoonWeight(0.49f, 0f), Is.EqualTo(0f), "低于门控 ⇒ 0");
		Assert.That(WindFieldModel.MonsoonWeight(0.5f, 0f), Is.EqualTo(1f).Within(1e-5f), "门控边界（含）");
		// 衰减：w = 1·exp(−d/1000)
		Assert.That(WindFieldModel.MonsoonWeight(5f, 1000f), Is.EqualTo(MathF.Exp(-1f)).Within(1e-4f), "decay 处 = e⁻¹");
		Assert.That(WindFieldModel.MonsoonWeight(5f, 2000f), Is.EqualTo(MathF.Exp(-2f)).Within(1e-4f), "reach 边界（含）");
		Assert.That(WindFieldModel.MonsoonWeight(5f, 2000.5f), Is.EqualTo(0f), "超出 reach ⇒ 0");
		// 三重关断（D2）
		Assert.That(WindFieldModel.MonsoonWeight(5f, 0f, new WindFieldModel.Tuning { MonsoonStrength = 0f }),
			Is.EqualTo(0f), "★D2 主关断：Strength = 0");
		Assert.That(WindFieldModel.MonsoonWeight(5f, 0f, new WindFieldModel.Tuning { MonsoonContrastThreshC = float.MaxValue }),
			Is.EqualTo(0f), "★D2 门控关断：thresh = ∞");
		Assert.That(WindFieldModel.MonsoonWeight(5f, 100f, new WindFieldModel.Tuning { MonsoonReachKm = 0f }),
			Is.EqualTo(0f), "★D2 距离关断：reach = 0");
	}

	// ── ② MRI：16 扇区中心角语义（硬线②：只表达反转强度）────────────────────────

	[Test]
	public void MonsoonReversalIndex_SectorCenterSemantics()
	{
		// 同扇区 ⇒ 0（无反转）
		Assert.That(WindFieldModel.MonsoonReversalIndexOf(11.25f, 5f), Is.EqualTo(0f).Within(1e-6f),
			"11.25° 与 5° 同属扇区 1 ⇒ MRI = 0");
		// 反向（扇区 1 vs 扇区 9，中心差 180°）⇒ 1
		Assert.That(WindFieldModel.MonsoonReversalIndexOf(11.25f, 191.25f), Is.EqualTo(1f).Within(1e-5f),
			"Δθ = 180° ⇒ MRI = 1（完全反转）");
		// 正交（扇区 1 vs 扇区 5，中心差 90°）⇒ 0.5
		Assert.That(WindFieldModel.MonsoonReversalIndexOf(11.25f, 101.25f), Is.EqualTo(0.5f).Within(1e-5f),
			"Δθ = 90° ⇒ MRI = 0.5");
		// 0°/360° 环绕：359°（扇区 16）与 1°（扇区 1）相邻 ⇒ MRI 取量化下界 (1−cos22.5°)/2 ≈ 0.038
		Assert.That(WindFieldModel.MonsoonReversalIndexOf(359f, 1f), Is.LessThan(0.05f),
			"环绕角不误判为反转（相邻扇区 = 扇区量化最小值）");
	}

	// ── ③ 端到端：合成海岸，季风主导去向 + MRI 高 + 速度逐位不变 ─────────────────

	/// <summary>合成月度温度事实（tempOf(格, 月) → °C）；用内部工厂（InternalsVisibleTo）。</summary>
	static MonthlyTemperature MakeTemp(int cellCount, Func<int, int, float> tempOf)
	{
		var t = MonthlyTemperature.Create(cellCount);
		for (int i = 0; i < cellCount; i++)
			for (int m = 0; m < MonthlyTemperature.Months; m++)
				t.Set(m, i, tempOf(i, m));
		t.FinishReadouts();
		return t;
	}

	/// <summary>
	/// 手工星形海岸：格 0 = 40°N/0°E **陆**（西风带全年），格 1..8 = 方位角 0/45/.../315、
	/// 角距 0.5° 的**海**邻居。distCoast = [1,0…0]、distLand = [0,1…1]（与海陆模式一致的 BFS 口径）。
	/// 回溯取最小索引邻居 ⇒ 源 = 格 1（正北海）⇒ 陆格季风方向 = 海→陆 = 正南 180°。
	/// </summary>
	static (WindTerrain ter, float[] latRad) MakeCoastalStar()
	{
		var dirs = new (float X, float Y, float Z)[9];
		double p0 = 40 * Math.PI / 180;
		dirs[0] = ((float)(Math.Cos(p0)), (float)Math.Sin(p0), 0f);
		for (int k = 0; k < 8; k++)
		{
			double e = 0.5 * Math.PI / 180, b = 45.0 * k * Math.PI / 180;
			double lat = Math.Asin(Math.Sin(p0) * Math.Cos(e) + Math.Cos(p0) * Math.Sin(e) * Math.Cos(b));
			double lng = Math.Atan2(Math.Sin(b) * Math.Sin(e) * Math.Cos(p0),
				Math.Cos(e) - Math.Sin(p0) * Math.Sin(lat));
			double p = lat, l = lng;
			dirs[k + 1] = ((float)(Math.Cos(p) * Math.Cos(l)), (float)Math.Sin(p), (float)(Math.Cos(p) * Math.Sin(l)));
		}
		var heights = new float[9];
		var distCoast = new int[9];
		var distLand = new int[9];
		heights[0] = 100f;                                   // 陆
		distCoast[0] = 1;                                    // 陆格离岸 1 hop
		for (int k = 1; k <= 8; k++) distLand[k] = 1;        // 海格离陆 1 hop
		var neighbors = new int[9][];
		neighbors[0] = Enumerable.Range(1, 8).ToArray();
		for (int k = 1; k <= 8; k++) neighbors[k] = new[] { 0 };
		var ter = new WindTerrain(heights, neighbors,
			dirs.Select(d => d.X).ToArray(), dirs.Select(d => d.Y).ToArray(), dirs.Select(d => d.Z).ToArray(),
			distCoast, distLand, kmPerHop: 45.2);            // I6 口径量级
		var latRad = dirs.Select(d => MathF.Asin(d.Y)).ToArray();
		return (ter, latRad);
	}

	[Test]
	public void Generate_SyntheticCoast_MonsoonDominatesDirection_MriHigh_SpeedBitwise()
	{
		var (ter, latRad) = MakeCoastalStar();
		// 陆格（i=0）1–8 月异常偏暖、9–12 月偏冷；海格 T′ ≡ 0 ⇒ C(0,m) = T′(0,m)
		var temp = MakeTemp(9, (i, m) => i == 0 ? (m <= 7 ? 20f : 0f) : 15f);
		var tuning = new WindFieldModel.Tuning { MonsoonDecayKm = 100_000f };   // w ≈ 1（近纯季风）

		var baseline = WindFieldModel.Generate(latRad, temp);                   // 无距离事实 = 批次 3
		var monsoon = WindFieldModel.Generate(latRad, temp, tuning, ter);       // 批次 4

		// 基线：40°N 全年西风 66.63° ⇒ 扇区 3；季风关闭 ⇒ MRI = 0
		Assert.That(baseline.DirectionTo[0], Is.EqualTo(3), "批次 3 语义：全年西风 ENE");
		Assert.That(baseline.MonsoonIndex[0], Is.EqualTo(0f), "无距离事实 ⇒ 季风关闭");

		// 暖月 8 个（C>0 吹向陆 180°）vs 冷月 4 个（反向 0°）⇒ 去向被季风拉成偏南（扇区 8）
		Assert.That(monsoon.DirectionTo[0], Is.EqualTo(8),
			"季风反转：暖月海→陆 / 冷月陆→海，暖月占多 ⇒ 全年去向偏南");
		// 极端月方向 ≈ 180°（扇区 8）vs ≈ 0°（扇区 1）⇒ Δθ = 157.5° ⇒ MRI ≈ 0.96
		Assert.That(monsoon.MonsoonIndex[0], Is.GreaterThan(0.9f),
			"★硬线②：MRI 只表达季节性反转强度（两极端月近完全反向）");

		// ★硬线①：季风只改方向——速度全场逐位不变
		Assert.That(monsoon.SpeedMs, Is.EqualTo(baseline.SpeedMs), "季风不改速度（全场逐位）");
	}

	// ── ④ D2：关断 ⇒ 与批次 3 逐数组逐位相等（Ball 真实 H3 邻接）──────────────────

	/// <summary>与生产同口径的海陆 BFS 距离（测试内复算 `FinalDistToCoast/Land` 定义）。</summary>
	static (int[] DistCoast, int[] DistLand) BfsDistances(WindTerrain ter)
	{
		int n = ter.HeightM.Length;
		var dc = new int[n];
		var dl = new int[n];
		var q = new Queue<int>();
		for (int i = 0; i < n; i++) dc[i] = ter.HeightM[i] <= 0 ? 0 : -1;
		for (int i = 0; i < n; i++) if (dc[i] == 0) q.Enqueue(i);
		while (q.Count > 0)
		{
			int c = q.Dequeue();
			foreach (var j in ter.Neighbors[c]) if (dc[j] < 0) { dc[j] = dc[c] + 1; q.Enqueue(j); }
		}
		q.Clear();
		for (int i = 0; i < n; i++) dl[i] = ter.HeightM[i] > 0 ? 0 : -1;
		for (int i = 0; i < n; i++) if (dl[i] == 0) q.Enqueue(i);
		while (q.Count > 0)
		{
			int c = q.Dequeue();
			foreach (var j in ter.Neighbors[c]) if (dl[j] < 0) { dl[j] = dl[c] + 1; q.Enqueue(j); }
		}
		return (dc, dl);
	}

	[Test]
	public void Generate_D2_Shutoff_MatchesBatch3Bitwise()
	{
		var ball = new Ball(1, 1f);
		int n = ball.CellDirs.Length;
		var latRad = ball.CellDirs.Select(d => MathF.Asin(d.Normalized().Y)).ToArray();
		// NH 大陆 + 海洋（seasonal 振幅陆 10 / 海 0 ⇒ 沿海存在真实陆海对比）
		var heights = new float[n];
		for (int i = 0; i < n; i++) heights[i] = latRad[i] > 0 ? 500f : 0f;
		var temp = MakeTemp(n, (i, m) =>
			15f + (latRad[i] > 0 ? 10f : 0f) * MathF.Cos(2f * MathF.PI * (m - 6) / 12f));

		var terNoDist = WindTerrain.FromBall(ball, heights);
		var (dc, dl) = BfsDistances(terNoDist);
		var terDist = WindTerrain.FromBall(ball, heights, dc, dl);

		var base3 = WindFieldModel.Generate(latRad, temp, null, terNoDist);      // 批次 3 语义
		var full4 = WindFieldModel.Generate(latRad, temp, null, terDist);        // 批次 4 全开
		var d2a = WindFieldModel.Generate(latRad, temp, new WindFieldModel.Tuning { MonsoonStrength = 0f }, terDist);
		var d2b = WindFieldModel.Generate(latRad, temp, new WindFieldModel.Tuning { MonsoonContrastThreshC = float.MaxValue }, terDist);

		// ★硬线③：D2 两种关断 ⇒ 与批次 3 逐数组逐位相等（非统计等价）
		Assert.That(d2a.DirectionTo, Is.EqualTo(base3.DirectionTo), "D2a：方向逐位回批次 3");
		Assert.That(d2a.SpeedMs, Is.EqualTo(base3.SpeedMs), "D2a：速度逐位回批次 3");
		Assert.That(d2a.MonsoonIndex, Is.EqualTo(base3.MonsoonIndex), "D2a：MRI 逐位回批次 3");
		Assert.That(d2b.DirectionTo, Is.EqualTo(base3.DirectionTo), "D2b：方向逐位回批次 3");
		Assert.That(d2b.SpeedMs, Is.EqualTo(base3.SpeedMs), "D2b：速度逐位回批次 3");
		Assert.That(d2b.MonsoonIndex, Is.EqualTo(base3.MonsoonIndex), "D2b：MRI 逐位回批次 3");

		// ★硬线①：季风全开也不改速度（全场逐位）
		Assert.That(full4.SpeedMs, Is.EqualTo(base3.SpeedMs), "季风只改方向，速度全场逐位不变");

		// 机制确实生效（否则上面的逐位对照是空洞的）：存在 MRI > 0.5 的格
		Assert.That(full4.MonsoonIndex, Has.Some.GreaterThan(0.5f),
			"沿海季风格应有显著反转读数（机制非空转）");
	}

	// ── ⑤ 硬线④：距离事实是只读引用，季风不为 Wind 生成新距离场 ─────────────────

	[Test]
	public void WindTerrain_DistArraysAreReadOnlyReferences_NoNewDistanceFacts()
	{
		var ball = new Ball(1, 1f);
		int n = ball.CellDirs.Length;
		var h = new float[n];
		var dc = new int[n];
		var dl = new int[n];
		var ter = WindTerrain.FromBall(ball, h, dc, dl);

		// 同一数组实例引用——视图只消费，不复制、不派生（★硬线④）
		Assert.That(ter.DistToCoastHops, Is.SameAs(dc), "DistToCoastHops = 调用方原数组引用");
		Assert.That(ter.DistToLandHops, Is.SameAs(dl), "DistToLandHops = 调用方原数组引用");
		Assert.That(ter.KmPerHop, Is.GreaterThan(0), "KmPerHop 由 I6 口径（√3×CellEdgeKm）自动换算");

		// 有温度、无距离事实 ⇒ 季风关闭（批次 3 语义保持：MonsoonIndex 恒 0）
		var temp = MakeTemp(n, (i, m) => 15f);
		var wind = WindFieldModel.Generate(ball, h, temp);
		Assert.That(wind.MonsoonIndex, Is.All.EqualTo(0f), "无距离事实 ⇒ 季风整体关闭");
	}

	// ── ⑥ res4 + 季风性能验收（手动）────────────────────────────────────────────

	[Test]
	[Explicit("res4 全世界 + 季风验收：手动运行 dotnet test --filter Res4_Monsoon_PerfAcceptance")]
	public void Res4_Monsoon_PerfAcceptance()
	{
		var ball = new Ball(4, 1f);
		int n = ball.CellDirs.Length;
		var latRad = ball.CellDirs.Select(d => MathF.Asin(d.Normalized().Y)).ToArray();
		// 确定性棋盘大陆（海岸线最长 = 回溯/带均值的最坏面）+ NH 季节振幅
		var heights = new float[n];
		for (int i = 0; i < n; i++)
		{
			bool land = Math.Abs(latRad[i]) < 1.0f && i % 2 == 0;
			heights[i] = land ? 300f : 0f;
		}
		var temp = MonthlyTemperature.Create(n);
		for (int i = 0; i < n; i++)
			for (int m = 0; m < MonthlyTemperature.Months; m++)
				temp.Set(m, i, 15f + (heights[i] > 0 ? 10f : 0f) * MathF.Cos(2f * MathF.PI * (m - 6) / 12f));
		temp.FinishReadouts();

		var ter = WindTerrain.FromBall(ball, heights);
		var (dc, dl) = BfsDistances(ter);

		var sw = System.Diagnostics.Stopwatch.StartNew();
		var wind = WindFieldModel.Generate(latRad, temp, null,
			WindTerrain.FromBall(ball, heights, dc, dl));
		long genMs = sw.ElapsedMilliseconds;

		Assert.That(wind.CellCount, Is.EqualTo(288_122));
		Assert.That(wind.DirectionTo, Is.All.InRange(0, 16));
		Assert.That(wind.MonsoonIndex, Is.All.InRange(0f, 1f));
		int monoCells = wind.MonsoonIndex.Count(v => v > 0f);
		TestContext.Out.WriteLine($"[WindField res4+monsoon] Generate {genMs} ms（批次 3 地形 = 997 ms）| " +
								  $"MRI>0 格数 = {monoCells}");
		Assert.That(genMs, Is.LessThan(60_000));
	}
}
