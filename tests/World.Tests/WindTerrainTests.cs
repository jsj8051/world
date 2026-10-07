using System;
using System.Linq;
using NUnit.Framework;
using World.Spatial;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · WindField 批次 3 护栏（⑯ 地形阻挡/绕流 + D4；设计 §4.4/§十四）：
///   · ★拍板①：地形发生在基础风方向之后（流水线内位置，不与 Coriolis/环流带耦合）；
///   · ★拍板②：地形**只改方向，永不改速度**（0.7 减速因子已否决）；
///   · ★拍板③：D4 关断（TerrainBarrierHeightM=1e9）⇒ 输出与无地形版**逐格逐位**相等；
///   · 三楔几何：中心 az±22.5°｜右（顺时针）az+45±22.5°｜左（逆时针）az−45±22.5°，边界归属 中&gt;右&gt;左；
///   · 决策：中心无屏障 ⇒ 原向｜两侧皆阻 ⇒ 原向｜平局（含双侧皆通）⇒ 逆时针｜否则取屏障较低侧。
/// 纪律：只用 [Test]；不写文件；不碰 GD.*/LogService。
/// </summary>
public class WindTerrainTests
{
	const float D2R = MathF.PI / 180f;

	static double AngDiff(double a, double b)
	{
		double d = Math.Abs(a - b) % 360.0;
		return d > 180.0 ? 360.0 - d : d;
	}

	// ── 决策核心（纯函数）────────────────────────────────────────────────────────

	[Test]
	public void TerrainDeflectDeg_DecisionMatrix()
	{
		Assert.That(WindFieldModel.TerrainDeflectDeg(90, 0, 0, 0), Is.EqualTo(90), "中心无屏障 ⇒ 原向");
		Assert.That(WindFieldModel.TerrainDeflectDeg(90, 3000, 0, 0), Is.EqualTo(45),
			"仅中心有屏障、双侧皆通 ⇒ 平局 ⇒ 逆时针（az−45）");
		Assert.That(WindFieldModel.TerrainDeflectDeg(90, 3000, 3500, 0), Is.EqualTo(45),
			"右侧屏障更高 ⇒ 取左侧（逆时针）");
		Assert.That(WindFieldModel.TerrainDeflectDeg(90, 3000, 0, 3500), Is.EqualTo(135),
			"左侧屏障更高 ⇒ 取右侧（顺时针）");
		Assert.That(WindFieldModel.TerrainDeflectDeg(90, 3000, 3000, 3000), Is.EqualTo(90),
			"两侧皆阻（平局）⇒ 原向（★拍板②：不减速不第三向）");
		Assert.That(WindFieldModel.TerrainDeflectDeg(90, 3000, 2800, 3500), Is.EqualTo(90),
			"两侧皆阻（即使高度不等）⇒ 原向——'取较低侧'只在单侧有屏障时生效");
	}

	// ── 合成图：三楔归属 + 罗盘方位角口径 ────────────────────────────────────────

	/// <summary>合成方向向量（口径 = CoordUtil.LatLngToSphere 归一化：X=cosφcosλ, Y=sinφ, Z=cosφsinλ）。</summary>
	static (float X, float Y, float Z) DirOf(double latDeg, double lngDeg)
	{
		double p = latDeg * Math.PI / 180.0, l = lngDeg * Math.PI / 180.0;
		return ((float)(Math.Cos(p) * Math.Cos(l)), (float)Math.Sin(p), (float)(Math.Cos(p) * Math.Sin(l)));
	}

	/// <summary>
	/// 手工图：格 0 = 40°N/0°E；格 1..8 = 方位角 0/45/.../315、角距 0.5° 的邻居。
	/// </summary>
	static WindTerrain MakeSyntheticTerrain(float hCenter, float h0, float h45, float h90, float h135,
		float h180, float h225, float h270, float h315)
	{
		var dirs = new (float, float, float)[9];
		dirs[0] = DirOf(40, 0);
		for (int k = 0; k < 8; k++)
		{
			// 球面正解：从 (40°N,0°) 沿方位角 45k° 走 0.5°
			double p0 = 40 * Math.PI / 180, e = 0.5 * Math.PI / 180, b = 45.0 * k * Math.PI / 180;
			double lat = Math.Asin(Math.Sin(p0) * Math.Cos(e) + Math.Cos(p0) * Math.Sin(e) * Math.Cos(b));
			double lng = Math.Atan2(Math.Sin(b) * Math.Sin(e) * Math.Cos(p0),
				Math.Cos(e) - Math.Sin(p0) * Math.Sin(lat));
			dirs[k + 1] = DirOf(lat * 180 / Math.PI, lng * 180 / Math.PI);
		}
		var heights = new[] { hCenter, h0, h45, h90, h135, h180, h225, h270, h315 };
		var neighbors = new int[9][];
		neighbors[0] = Enumerable.Range(1, 8).ToArray();
		for (int k = 1; k <= 8; k++) neighbors[k] = new[] { 0 };
		var dx = dirs.Select(d => d.Item1).ToArray();
		var dy = dirs.Select(d => d.Item2).ToArray();
		var dz = dirs.Select(d => d.Item3).ToArray();
		return new WindTerrain(heights, neighbors, dx, dy, dz);
	}

	[Test]
	public void SyntheticGraph_WedgeSelection_MatchesCompassConvention()
	{
		var ter = MakeSyntheticTerrain(0, 0, 0, 3000, 0, 0, 0, 0, 0);
		// 屏障在罗盘 90°（正东）⇒ 中心楔（az=90±22.5）应命中；左右楔皆通 ⇒ 逆时针绕行
		WindFieldModel.WedgeBarrierMaxima(0, 90, ter, new int[9], 1, new int[9], new int[9],
			new double[9], new float[9], null, out var c, out var cw, out var ccw);
		Assert.That(c, Is.EqualTo(3000), "正东屏障落入中心楔（罗盘口径自检）");
		Assert.That(cw, Is.EqualTo(0));
		Assert.That(ccw, Is.EqualTo(0));

		// 135° 屏障 → 右楔；45° 屏障 → 左楔
		var ter2 = MakeSyntheticTerrain(0, 0, 0, 3000, 3500, 0, 0, 0, 0);
		WindFieldModel.WedgeBarrierMaxima(0, 90, ter2, new int[9], 1, new int[9], new int[9],
			new double[9], new float[9], null, out c, out cw, out ccw);
		Assert.That(c, Is.EqualTo(3000));
		Assert.That(cw, Is.EqualTo(3500), "135° ∈ az+45±22.5 ⇒ 右（顺时针）楔");
		Assert.That(ccw, Is.EqualTo(0));

		var ter3 = MakeSyntheticTerrain(0, 0, 3500, 3000, 0, 0, 0, 0, 0);
		WindFieldModel.WedgeBarrierMaxima(0, 90, ter3, new int[9], 1, new int[9], new int[9],
			new double[9], new float[9], null, out c, out cw, out ccw);
		Assert.That(ccw, Is.EqualTo(3500), "45° ∈ az−45±22.5 ⇒ 左（逆时针）楔");

		// 门槛语义：1500 m < 2000 m ⇒ 非屏障；相对抬升：本格 2500 ⇒ 门槛 3300
		var ter4 = MakeSyntheticTerrain(0, 0, 0, 1500, 0, 0, 0, 0, 0);
		WindFieldModel.WedgeBarrierMaxima(0, 90, ter4, new int[9], 1, new int[9], new int[9],
			new double[9], new float[9], null, out c, out _, out _);
		Assert.That(c, Is.EqualTo(0), "低于绝对门槛 ⇒ 非屏障");
		var ter5 = MakeSyntheticTerrain(2500, 0, 0, 3000, 0, 0, 0, 0, 0);
		WindFieldModel.WedgeBarrierMaxima(0, 90, ter5, new int[9], 1, new int[9], new int[9],
			new double[9], new float[9], null, out c, out _, out _);
		Assert.That(c, Is.EqualTo(0), "3000 < 本格 2500+800 ⇒ 低于相对门槛");
		var ter6 = MakeSyntheticTerrain(2500, 0, 0, 3500, 0, 0, 0, 0, 0);
		WindFieldModel.WedgeBarrierMaxima(0, 90, ter6, new int[9], 1, new int[9], new int[9],
			new double[9], new float[9], null, out c, out _, out _);
		Assert.That(c, Is.EqualTo(3500), "3500 ≥ 3300 ⇒ 屏障成立");
	}

	// ── Ball 集成：真实 H3 邻接 + D4 逐位对照 ─────────────────────────────────────

	[Test]
	public void BallIntegration_TerrainDeflects_D4Shutoff_MatchesBatch2Bitwise()
	{
		var ball = new Ball(1, 1f);
		int n = ball.CellDirs.Length;
		float[] LatRad() => ball.CellDirs.Select(d => MathF.Asin(d.Normalized().Y)).ToArray();
		var latRad = LatRad();

		// 找一个全年西风带的 NH 格（38–52°N：脊 27–33、锋 55.5–63 ⇒ 12 个月恒西风）
		int target = -1;
		for (int i = 0; i < n && target < 0; i++)
		{
			double latDeg = latRad[i] * 180.0 / Math.PI;
			if (latDeg is >= 38.0 and <= 52.0) target = i;
		}
		Assert.That(target, Is.GreaterThanOrEqualTo(0), "Ball(1) 应含 38–52°N 的格");
		double az = WindFieldModel.CoriolisDeflectionDeg(latRad[target]);   // 西风 NH 基矢 0 + δ

		// 用生产罗盘口径把 3-hop 视域分楔：中心楔 = 山脊（3500），右楔 = 山（3000），左楔 = 平地
		var ter0 = WindTerrain.FromBall(ball, new float[n]);
		var heights = new float[n];
		FillWedges(ball, target, az, ter0, heights, centerH: 3500f, cwH: 3000f, ccwH: 0f);
		var ter = WindTerrain.FromBall(ball, heights);

		var baseWind = WindFieldModel.Generate(ball);                                   // 无地形（= 批次 2）
		var terrWind = WindFieldModel.Generate(ball, heights);                          // 地形开启
		var d4Wind = WindFieldModel.Generate(ball, heights, null, new WindFieldModel.Tuning
		{
			TerrainBarrierHeightM = 1e9f
		});                                                                             // D4 关断

		// ★拍板③：D4 关断 ⇒ 与无地形版逐格逐位相等
		Assert.That(d4Wind.DirectionTo, Is.EqualTo(baseWind.DirectionTo), "D4：方向逐位相等");
		Assert.That(d4Wind.SpeedMs, Is.EqualTo(baseWind.SpeedMs), "D4：速度逐位相等");
		Assert.That(d4Wind.MonsoonIndex, Is.EqualTo(baseWind.MonsoonIndex), "D4：MRI 逐位相等");

		// ★拍板②：地形不改速度（全场逐位）
		Assert.That(terrWind.SpeedMs, Is.EqualTo(baseWind.SpeedMs), "地形只改方向，速度全场逐位不变");

		// 中心楔有屏障、右楔更低 ⇒ 目标格方向应被偏转（DirectionTo 改变；速度不变）
		Assert.That(terrWind.DirectionTo[target], Is.Not.EqualTo(baseWind.DirectionTo[target]),
			$"中心楔山脊应使 {az:F1}° 的西风绕行（偏转 ±45°）");
	}

	/// <summary>按生产罗盘口径把目标格 3-hop 视域分楔并填充高度（测试辅助）。</summary>
	static void FillWedges(Ball ball, int target, double az, WindTerrain ter, float[] heights,
		float centerH, float cwH, float ccwH)
	{
		var stamp = new bool[ball.CellDirs.Length];
		var queue = new System.Collections.Generic.Queue<(int c, int hop)>();
		stamp[target] = true;
		queue.Enqueue((target, 0));
		while (queue.Count > 0)
		{
			var (c, hop) = queue.Dequeue();
			if (hop == 3) continue;
			foreach (var j in ter.Neighbors[c])
			{
				if (stamp[j]) continue;
				stamp[j] = true;
				queue.Enqueue((j, hop + 1));
				double b = WindFieldModel.BearingToDeg(target, j, ter);
				double dCenter = AngDiff(b, az);
				double dCw = AngDiff(b, az + 45);
				double dCcw = AngDiff(b, az - 45);
				if (dCenter <= 22.5) heights[j] = centerH;       // 边界归属：中心 > 右 > 左（与生产一致）
				else if (dCw <= 22.5) heights[j] = cwH;
				else if (dCcw <= 22.5) heights[j] = ccwH;
			}
		}
	}

	// ── res4 + 地形性能验收（手动）───────────────────────────────────────────────

	[Test]
	[Explicit("res4 全世界 + 地形验收：手动运行 dotnet test --filter Res4_Terrain_PerfAcceptance")]
	public void Res4_Terrain_PerfAcceptance()
	{
		var ball = new Ball(4, 1f);
		int n = ball.CellDirs.Length;
		// 确定性合成山系：每 97 格一座 3000 m 山（覆盖足够多的视域命中面）
		var heights = new float[n];
		for (int i = 0; i < n; i++) heights[i] = i % 97 == 0 ? 3000f : 0f;

		var sw = System.Diagnostics.Stopwatch.StartNew();
		var wind = WindFieldModel.Generate(ball, heights);
		long genMs = sw.ElapsedMilliseconds;

		Assert.That(wind.CellCount, Is.EqualTo(288_122));
		Assert.That(wind.SpeedMs, Is.All.InRange(0f, 25f));
		TestContext.Out.WriteLine($"[WindField res4+terrain] Generate {genMs} ms（批次 2 无地形 = 133 ms）");
		Assert.That(genMs, Is.LessThan(60_000));
	}
}
