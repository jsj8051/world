using System;
using System.Collections.Generic;
using NUnit.Framework;
using Godot;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 河流**表现样条**护栏（River v2.0）：v1.x 测的是 edge 覆盖 / 宽度单调 / 拓扑；
/// v2 新增的是"**平滑不得篡改事实**"——
///   ① 样条必须**穿过**每个 H3 锚点（不是拟合、不是拽走河道）；
///   ② 视觉偏移必须**有界**（平滑只是去锯齿，不能重新规划河流）；
///   ③ 采样密度随曲率自适应（直线不浪费三角形，急弯才加密）。
/// 纪律（同 RiverGraphTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class RiverPresentationSplineTests
{
	const float Eps = 1e-5f;
	const double Res4StepRad = 7.095e-3;          // res4 邻格角距

	static readonly Vector3 U = new Vector3(1f, 0.3f, -0.7f).Normalized();
	static readonly Vector3 V = U.Cross(Vector3.Up).Normalized();

	static Vector3 OnGreat(double theta) =>
		(U * (float)Math.Cos(theta) + V * (float)Math.Sin(theta)).Normalized();

	static Vector3[] StraightLine(int n)
	{
		var a = new Vector3[n];
		for (int i = 0; i < n; i++) a[i] = OnGreat(i * Res4StepRad);
		return a;
	}

	/// <summary>带一个 90° 急弯的路径：前半沿大圆，后半沿"横向"走出去。</summary>
	static Vector3[] WithSharpTurn(int n)
	{
		var a = new Vector3[n];
		int mid = n / 2;
		for (int i = 0; i <= mid; i++) a[i] = OnGreat(i * Res4StepRad);
		var p = a[mid];
		var side = RiverLineOverlay.SegmentSide(a[mid - 1], p, out var s) ? s : Vector3.Right;
		var cur = p;
		for (int i = mid + 1; i < n; i++)
		{
			cur = (cur * (float)Math.Cos(Res4StepRad) + side * (float)Math.Sin(Res4StepRad)).Normalized();
			a[i] = cur;
		}
		return a;
	}

	[Test]
	public void Spline_PassesAllAnchors()
	{
		// ★最重要的一条：样条**穿过**锚点（Spline(Pi) = Pi）。
		//   若改用 B 样条/最小二乘拟合，这条会立刻挂 ⇒ 河道被"拽走"能被挡住。
		foreach (var anchors in new[] { StraightLine(7), WithSharpTurn(9) })
		{
			RiverPresentationSpline.Sample(anchors, out var pts, out var segOf);
			Assert.That(pts[0].DistanceTo(anchors[0]), Is.LessThan(Eps), "首点必须是 anchors[0]");

			for (int j = 0; j + 1 < anchors.Length; j++)
			{
				int last = -1;
				for (int i = 0; i < segOf.Length; i++) if (segOf[i] == j) last = i;
				Assert.That(last, Is.GreaterThanOrEqualTo(0), $"段 {j} 必须有采样点");
				Assert.That(pts[last].DistanceTo(anchors[j + 1]), Is.LessThan(Eps),
					$"段 {j} 的末点必须精确落在 anchors[{j + 1}]（样条必须穿过锚点）");
			}
			Assert.That(pts[^1].DistanceTo(anchors[^1]), Is.LessThan(Eps), "尾点必须是最后一个锚点");
		}
	}

	[Test]
	public void Spline_DeviationWithinBound()
	{
		// ★★★安全阀：偏移 ≤ 锚点间距 × MaxDeviationRatio。
		//   否则"平滑"就变成了"重新规划河道"。
		var anchors = WithSharpTurn(9);
		RiverPresentationSpline.Sample(anchors, out var pts, out var segOf);

		for (int i = 0; i < pts.Length; i++)
		{
			int j = segOf[i];
			var p1 = anchors[j];
			var p2 = anchors[j + 1];
			float dev = ChordDeviation(pts[i], p1, p2);
			float maxDev = RiverPresentationSpline.MaxDeviationRatio * p1.DistanceTo(p2);
			Assert.That(dev, Is.LessThanOrEqualTo(maxDev + Eps),
				$"采样点 {i}（段 {j}）偏移 {dev:E3} 超过护栏 {maxDev:E3}");
		}
	}

	[Test]
	public void Spline_StraightLine_StaysOnChord()
	{
		// 完全笔直 ⇒ 样条不应产生可见弯曲（平滑只在有曲率的地方起作用）
		var anchors = StraightLine(7);
		RiverPresentationSpline.Sample(anchors, out var pts, out var segOf);
		for (int i = 0; i < pts.Length; i++)
		{
			int j = segOf[i];
			Assert.That(ChordDeviation(pts[i], anchors[j], anchors[j + 1]), Is.LessThan(1e-4f),
				$"直线段上的采样点 {i} 不应偏离弦");
		}
	}

	[Test]
	public void Spline_AdaptiveSampling_MoreSamplesAtSharpTurn()
	{
		var straight = StraightLine(7);
		var sharp = WithSharpTurn(9);

		int straightMid = RiverPresentationSpline.SampleCount(straight, straight.Length / 2 - 1);
		int sharpMid = RiverPresentationSpline.SampleCount(sharp, sharp.Length / 2 - 1);

		Assert.That(straightMid, Is.EqualTo(1), "直线段只需 1 段（不浪费三角形）");
		Assert.That(sharpMid, Is.GreaterThan(1), "急弯段必须加密采样");
		Assert.That(sharpMid, Is.LessThanOrEqualTo(RiverPresentationSpline.MaxSamplesPerEdge));

		RiverPresentationSpline.Sample(straight, out var ps, out _);
		RiverPresentationSpline.Sample(sharp, out var pk, out _);
		Assert.That(pk.Length, Is.GreaterThan(ps.Length), "急弯路径的采样点必须多于等长直线路径");
	}

	[Test]
	public void Spline_SampleCount_IsClamped()
	{
		// 极端曲率也不得爆掉采样数（几何量必须有上界）
		var wild = new List<Vector3>();
		var p = OnGreat(0.0);
		wild.Add(p);
		var side = Vector3.Right;
		for (int i = 1; i < 12; i++)
		{
			var axis = i % 2 == 0 ? side : -side;      // 来回 180° 折返（最坏情况）
			p = (p * (float)Math.Cos(Res4StepRad) + axis * (float)Math.Sin(Res4StepRad)).Normalized();
			wild.Add(p);
		}
		foreach (int j in new[] { 0, 1, wild.Count / 2 })
			Assert.That(RiverPresentationSpline.SampleCount(wild, j),
				Is.InRange(1, RiverPresentationSpline.MaxSamplesPerEdge));
	}

	[Test]
	public void Spline_OutputStaysOnUnitSphere()
	{
		// 样条在 R³ 里插值后 normalize 回单位球 ⇒ 所有采样点必须是单位方向
		RiverPresentationSpline.Sample(WithSharpTurn(9), out var pts, out _);
		foreach (var q in pts)
		{
			Assert.That(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z), Is.True,
				"采样点不得出现 NaN（端点重复控制点曾导致 0×∞ = NaN）");
			Assert.That(Math.Abs(q.Length() - 1.0), Is.LessThan(Eps), "采样点必须落在单位球上");
		}
	}

	[Test]
	public void Spline_DuplicateControlPoint_DoesNotProduceNaN()
	{
		// ★回归钉子：CR 的控制点若重复（如用 anchors[m-1] 当 p3 而 p2 已是它），
		//   节点步长取 1e-9 会被 float32 舍掉（0.168 + 1e-9 == 0.168）⇒ t3-t2==0
		//   ⇒ 除零得 ±∞ ⇒ (p3-p2)·∞ = 0×∞ = NaN，整段采样点全变 NaN。
		var a = StraightLine(4);
		foreach (float t in new[] { 0.0f, 0.5f, 1.0f })
		{
			var q = RiverPresentationSpline.Centripetal(a[1], a[2], a[3], a[3], t);
			Assert.That(float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z), Is.True,
				$"重复控制点不得产生 NaN（t={t}）");
		}
	}

	[Test]
	public void Spline_DegenerateChains_DoNotCrash()
	{
		RiverPresentationSpline.Sample(Array.Empty<Vector3>(), out var e0, out var s0);
		Assert.That(e0.Length, Is.EqualTo(0));
		Assert.That(s0.Length, Is.EqualTo(0));

		var one = new[] { OnGreat(0.3) };
		RiverPresentationSpline.Sample(one, out var e1, out var s1);
		Assert.That(e1.Length, Is.EqualTo(1));
		Assert.That(e1[0].DistanceTo(one[0]), Is.LessThan(Eps));

		var two = new[] { OnGreat(0.3), OnGreat(0.3 + Res4StepRad) };
		RiverPresentationSpline.Sample(two, out var e2, out _);
		Assert.That(e2.Length, Is.GreaterThanOrEqualTo(2));
	}

	static float ChordDeviation(Vector3 q, Vector3 p1, Vector3 p2)
	{
		var d = p2 - p1;
		float len2 = d.LengthSquared();
		if (len2 < 1e-20f) return q.DistanceTo(p1);
		float t = Math.Clamp((q - p1).Dot(d) / len2, 0f, 1f);
		return q.DistanceTo(p1 + d * t);
	}
}
