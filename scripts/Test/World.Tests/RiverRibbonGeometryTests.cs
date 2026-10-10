using System;
using System.Collections.Generic;
using NUnit.Framework;
using Godot;
using World.H3Grid;
using World.Logic;
using World.Render;                 // RiverLineOverlay / 地图模式（表现层）

namespace World.Tests;

/// <summary>
/// Ribbon **球面几何正确性**护栏（2026-10-04，河流表现 v1 冻结前最后一轮）：
/// 针对 `RiverLineOverlay.SegmentSide` 的横向向量语义，用极端 case 钉死——
/// 直线（不得摆动/缩窄）、急弯（必须转向）、接近极点（不得退化）、
/// 短河段/两点河段（不得被静默丢弃）、退化段（必须返回 false）。
///
/// ★数学语义（本套测试要守住的事实）：
///   d2 = cosθ·d1 + sinθ·t̂ ⇒ cross(d1,d2) = sinθ·cross(d1,t̂)
///   ⇒ normalize(cross(d1,d2)) = cross(p, tangent)，即**球面横向**。
///   它既 ⊥ 径向（贴着球面）又 ⊥ 流向（垂直于河段）——"大圆平面法向"与"球面横向"
///   在此是同一个向量，因为**大圆平面过球心**，其法向必然垂直于径向。
/// 纪律（同 RiverGraphTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class RiverRibbonGeometryTests
{
	const float Eps = 1e-4f;
	const float EpsLoose = 1e-3f;                 // 小角度：float32 叉积有效位有限，放宽容差
	const double Res4StepRad = 7.095e-3;          // res4 邻格角距 = 45.2 km / 6371 km

	static readonly Vector3 U = new Vector3(1f, 0.3f, -0.7f).Normalized();
	static readonly Vector3 V = U.Cross(Vector3.Up).Normalized();     // {U, V} 正交归一 ⇒ 张成大圆

	static Vector3 OnGreat(double theta) =>
		(U * (float)Math.Cos(theta) + V * (float)Math.Sin(theta)).Normalized();

	static void AssertIsSurfaceLateral(Vector3 d1, Vector3 d2, Vector3 side, float tol, string tag)
	{
		Assert.That(Math.Abs(side.Length() - 1.0), Is.LessThan(tol), $"{tag}：横向必须是单位向量");
		Assert.That(float.IsFinite(side.X) && float.IsFinite(side.Y) && float.IsFinite(side.Z),
			Is.True, $"{tag}：横向不得出现 NaN/Inf");
		// ① ⊥ 径向 ⇒ 贴着球面（不会翘起来）
		Assert.That(Math.Abs(side.Dot(d1)), Is.LessThan(tol), $"{tag}：横向必须垂直于径向");
		// ② ⊥ 流向 ⇒ 确实是"横向"而不是沿河方向
		Assert.That(Math.Abs(side.Dot(d2 - d1)), Is.LessThan(tol), $"{tag}：横向必须垂直于河段走向");
	}

	// ── ① 直线段：side 必须恒定（不摆动）且宽度不缩 ──────────────────────────

	[Test]
	public void StraightLine_SideIsConstant_NoZigzag()
	{
		// 完全"笔直"的河 = 一串共大圆、等角距的采样点。
		// ⇒ 每段的横向必须是**同一个向量**，否则 ribbon 会左右摆动成锯齿。
		var sides = new System.Collections.Generic.List<Vector3>();
		int n = 12;
		for (int k = 0; k < n; k++)
		{
			var d1 = OnGreat(k * Res4StepRad);
			var d2 = OnGreat((k + 1) * Res4StepRad);
			Assert.That(RiverLineOverlay.SegmentSide(d1, d2, out var s), Is.True, $"段 {k}：直线上不得判为退化");
			AssertIsSurfaceLateral(d1, d2, s, Eps, $"直线段 {k}");
			sides.Add(s);
		}
		for (int k = 1; k < sides.Count; k++)
			Assert.That(sides[k].Dot(sides[0]), Is.GreaterThan(1f - Eps),
				$"直线段 {k} 的横向与首段不一致（ribbon 会摆动成锯齿）");
	}

	[Test]
	public void RibbonWidth_IsConstantAcrossCurvatureAndClass()
	{
		// 宽度异常缩小的最直接判据：四边形"上游边"长度 = 2 × 半宽 × 半径，与曲率/档位无关。
		const float radius = 2.0f;
		for (int cls = 0; cls < RiverSymbolWidth.ClassCount; cls++)
		{
			float hw = RiverSymbolWidth.HalfWidthWorld(radius, cls);
			float expect = 2f * hw * radius;
			for (int k = 0; k < 6; k++)
			{
				var d1 = OnGreat(k * Res4StepRad);
				var d2 = OnGreat((k + 1) * Res4StepRad);
				Assert.That(RiverLineOverlay.SegmentSide(d1, d2, out var s), Is.True);
				var p1 = (d1 + s * hw) * radius;
				var p2 = (d1 - s * hw) * radius;
				Assert.That(Math.Abs(p1.DistanceTo(p2) - expect), Is.LessThan(1e-5f),
					$"档位 {cls} 段 {k}：ribbon 宽度必须恒为 2×半宽×半径（不得随曲率缩小）");
			}
		}
	}

	// ── ② 急弯：side 必须跟着转，但仍然合法 ────────────────────────────────

	[Test]
	public void SharpBend_SideTurnsWithFlow()
	{
		// 构造一个精确 90° 的转弯：第二段沿"第一段的横向"走出去
		// ⇒ 数学上 dot(side1, side2) = w·cross(d2, w) = 0（严格为 0）。
		double d = Res4StepRad;
		var d1 = OnGreat(0.0);
		var d2 = OnGreat(d);
		Assert.That(RiverLineOverlay.SegmentSide(d1, d2, out var s1), Is.True);

		var w = s1;
		var d3 = (d2 * (float)Math.Cos(d) + w * (float)Math.Sin(d)).Normalized();
		Assert.That(RiverLineOverlay.SegmentSide(d2, d3, out var s2), Is.True, "急弯第二段不得判为退化");

		AssertIsSurfaceLateral(d2, d3, s2, Eps, "急弯第二段");
		Assert.That(Math.Abs(s1.Dot(s2)), Is.LessThan(Eps),
			"90° 急弯处两段的横向必须近似正交（ribbon 必须跟着河流转，不能沿用上一段方向）");
	}

	// ── ③ 接近极点：不得退化 ────────────────────────────────────────────────

	[Test]
	public void NearPole_NoDegeneracy()
	{
		// (a) 起点就是极点本身
		var pole = Vector3.Up;
		var nearPole = new Vector3(0f, 1f, (float)Res4StepRad).Normalized();
		Assert.That(RiverLineOverlay.SegmentSide(pole, nearPole, out var s), Is.True, "极点起步不得退化");
		AssertIsSurfaceLateral(pole, nearPole, s, Eps, "极点起步");

		// (b) 横跨极点（两点的经度相反）——H3 极区五边形邻域的真实形态
		var a = new Vector3(0.001f, 1f, 0f).Normalized();
		var b = new Vector3(-0.001f, 1f, 0f).Normalized();
		Assert.That(RiverLineOverlay.SegmentSide(a, b, out var sCross), Is.True, "跨极点段不得退化");
		AssertIsSurfaceLateral(a, b, sCross, Eps, "跨极点");

		// (c) 极点附近的连续折线：全程不得出现退化/NaN
		var u = new Vector3(0.001f, 1f, 0f).Normalized();
		var v = u.Cross(Vector3.Right).Normalized();
		for (int k = 0; k < 8; k++)
		{
			var p1 = (u * (float)Math.Cos(k * Res4StepRad) + v * (float)Math.Sin(k * Res4StepRad)).Normalized();
			var p2 = (u * (float)Math.Cos((k + 1) * Res4StepRad) + v * (float)Math.Sin((k + 1) * Res4StepRad)).Normalized();
			Assert.That(RiverLineOverlay.SegmentSide(p1, p2, out var sp), Is.True, $"极区段 {k} 不得退化");
			AssertIsSurfaceLateral(p1, p2, sp, Eps, $"极区段 {k}");
		}
	}

	// ── ④ 短河段 / 两点河段：不得被静默丢弃 ────────────────────────────────

	[Test]
	public void ShortSegment_IsNotSilentlyDropped()
	{
		// ⚠️ 回归钉子：旧写法 `LengthSquared < 1e-12` 等价于"圆心角 < 1e-6 rad 即跳过"，
		//    会把合法短河段**静默丢掉**（res 越高格间距越小 ⇒ 河线出现空洞）。
		//    这里要求：比 res4 短 2~5 个量级的段仍然成立。
		foreach (double d in new[] { 1e-3, 1e-4, 1e-5 })
		{
			var d1 = OnGreat(0.0);
			var d2 = OnGreat(d);
			Assert.That(RiverLineOverlay.SegmentSide(d1, d2, out var s), Is.True,
				$"角距 {d:e0} rad 的短河段不得被判为退化");
			AssertIsSurfaceLateral(d1, d2, s, EpsLoose, $"短河段 {d:e0}");
		}

		// 更短的段：float32 叉积已到有效位边缘，只要求"仍返回 true、有限、单位长"
		foreach (double d in new[] { 1e-6, 1e-7 })
		{
			var d1 = OnGreat(0.0);
			var d2 = OnGreat(d);
			Assert.That(RiverLineOverlay.SegmentSide(d1, d2, out var s), Is.True,
				$"角距 {d:e0} rad 的极短段仍不得被丢弃");
			Assert.That(float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Z), Is.True);
			Assert.That(Math.Abs(s.Length() - 1.0), Is.LessThan(EpsLoose));
		}
	}

	[Test]
	public void TwoCellLine_SingleSegmentKeepsFullWidth()
	{
		// 两点河段（LineCells.Length == 2 ⇒ 恰好 1 段）：单段四边形仍须等宽、不自闭。
		const float radius = 2.0f;
		float hw = RiverSymbolWidth.HalfWidthWorld(radius, RiverSymbolWidth.ClassCount - 1);
		var d1 = OnGreat(0.0);
		var d2 = OnGreat(Res4StepRad);
		Assert.That(RiverLineOverlay.SegmentSide(d1, d2, out var s), Is.True);

		var p1 = (d1 + s * hw) * radius;
		var p2 = (d1 - s * hw) * radius;
		var p3 = (d2 - s * hw) * radius;
		var p4 = (d2 + s * hw) * radius;

		Assert.That(Math.Abs(p1.DistanceTo(p2) - 2f * hw * radius), Is.LessThan(1e-5f), "两点河段上游边宽");
		Assert.That(Math.Abs(p4.DistanceTo(p3) - 2f * hw * radius), Is.LessThan(1e-5f), "两点河段下游边宽");
		Assert.That(p1.DistanceTo(p3), Is.GreaterThan(hw * radius), "两点河段必须是一个张开的长条，不能退化成一点");
	}

	// ── ⑥ v1.1：chain 化（边去重 / 覆盖性）────────────────────────────────

	static (RiverGraph graph, RiverGeometry geo) World() => RiverSymbolWidthTests.BuildWorld(new Ball(1, 1f));

	static long Key(int a, int b) => ((long)a << 32) | (uint)b;

	[Test]
	public void Chains_CoverEveryRiverEdgeExactlyOnce()
	{
		// ★v1.1 的核心不变量：LineCells 是"每河源一条 source→outlet" ⇒ 干流段被 N 条线共有。
		//   ribbon 化后重复几何 = 完全共面三角形 = z-fighting ⇒ chain 化必须把边去重。
		var (graph, geo) = World();
		var chains = RiverLineOverlay.BuildChains(geo, graph, out int conflicts);
		Assert.That(conflicts, Is.EqualTo(0), "真实河网不得出现「同一上游两个不同下游」的拓扑冲突");
		Assert.That(chains.Count, Is.GreaterThan(0), "res1 基线必须有 chain");

		var expected = new HashSet<long>();
		foreach (var cells in geo.LineCells)
			for (int k = 0; k + 1 < cells.Length; k++)
				expected.Add(Key(cells[k], cells[k + 1]));

		var actual = new HashSet<long>();
		int total = 0;
		foreach (var ch in chains)
			for (int k = 0; k + 1 < ch.Length; k++)
			{
				total++;
				actual.Add(Key(ch[k], ch[k + 1]));
			}

		Assert.That(actual.Count, Is.EqualTo(expected.Count), "chain 必须覆盖到 LineCells 的每一条边");
		Assert.That(total, Is.EqualTo(expected.Count), "每条边只能属于**一条** chain（否则干流被重复绘制 ⇒ z-fighting）");
		Assert.That(actual.SetEquals(expected), Is.True, "chain 边集必须与去重后的河网边集完全一致");
	}

	[Test]
	public void Chains_HeadsAreSourceOrConfluence()
	{
		// chain 头必须是入度 ≠ 1 的河流格（source 入度 0 / confluence 入度 ≥2）
		var (graph, geo) = World();
		RiverLineOverlay.BuildChains(geo, graph, out _);   // 冲突由上一个测试专门钉
		foreach (var ch in RiverLineOverlay.BuildChains(geo, graph, out _))
		{
			Assert.That(ch.Length, Is.GreaterThan(0));
			int h = ch[0];
			Assert.That(graph.NodeKind[h], Is.GreaterThanOrEqualTo(0), $"chain 头 {h} 必须是河流格");
			Assert.That(graph.UpstreamCount[h], Is.Not.EqualTo(1), $"chain 头 {h} 入度必须是 0（源）或 ≥2（汇流）");
		}
	}

	[Test]
	public void TryAddEdge_DuplicateIsDedup_ConflictIsExposed()
	{
		// ★不能静默覆盖：同一上游出现两个不同下游是**拓扑错误**，必须暴露而不是被吞掉。
		var map = new Dictionary<int, int>();
		int conflicts = 0;

		Assert.That(RiverLineOverlay.TryAddEdge(map, 1, 2, ref conflicts), Is.True, "新边应加入");
		Assert.That(RiverLineOverlay.TryAddEdge(map, 1, 2, ref conflicts), Is.False, "同一条边重复 ⇒ 去重（正常）");
		Assert.That(conflicts, Is.EqualTo(0), "同一条边重复**不是**冲突");

		Assert.That(RiverLineOverlay.TryAddEdge(map, 1, 3, ref conflicts), Is.False, "冲突边不得写入");
		Assert.That(conflicts, Is.EqualTo(1), "同一上游出现不同下游 ⇒ 必须记为冲突");
		Assert.That(map[1], Is.EqualTo(2), "冲突时保留**首条**（确定性）");
	}

	// ── ⑦ v1.1：join 基（miter 长度系数）──────────────────────────────────

	static void BasisAtPoint(Vector3 p, out Vector3 e1, out Vector3 e2)
	{
		e1 = p.Cross(Math.Abs(p.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
		e2 = p.Cross(e1).Normalized();
	}

	static (Vector3 prev, Vector3 next) TurnAround(Vector3 p, Vector3 e1, Vector3 e2, double phiDeg, double step)
	{
		// 来向沿 −e1，去向与 e1 成 phi ⇒ 拐角 = phi
		double phi = phiDeg * Math.PI / 180.0;
		var prev = (p * (float)Math.Cos(step) - e1 * (float)Math.Sin(step)).Normalized();
		var t2 = e1 * (float)Math.Cos(phi) + e2 * (float)Math.Sin(phi);
		var next = (p * (float)Math.Cos(step) + t2 * (float)Math.Sin(step)).Normalized();
		return (prev, next);
	}

	[Test]
	public void JoinBasis_Straight_MiterScaleIsOne()
	{
		var p = OnGreat(0.7);
		BasisAtPoint(p, out var e1, out var e2);
		var (prev, next) = TurnAround(p, e1, e2, 0.0, Res4StepRad);
		Assert.That(RiverLineOverlay.JoinBasis(p, prev, next, out _, out _, out var side, out float ms), Is.True);
		Assert.That(ms, Is.EqualTo(1f).Within(EpsLoose), "笔直河段的 miter 系数必须是 1（不能无缘无故变宽）");
		AssertIsSurfaceLateral(p, next, side, Eps, "直线 join");
	}

	[Test]
	public void JoinBasis_RightAngle_MiterScaleIsSqrt2()
	{
		var p = OnGreat(0.7);
		BasisAtPoint(p, out var e1, out var e2);
		var (prev, next) = TurnAround(p, e1, e2, 90.0, Res4StepRad);
		Assert.That(RiverLineOverlay.JoinBasis(p, prev, next, out _, out _, out _, out float ms), Is.True);
		Assert.That(ms, Is.EqualTo(Math.Sqrt(2.0)).Within(EpsLoose),
			"90° 拐角的 miter 系数必须是 √2 = 1/cos(45°)");
	}

	[Test]
	public void JoinBasis_SharpTurn_ExceedsMiterLimitButStaysFinite()
	{
		// 170° 急弯：miter 系数 = 1/cos(85°) ≈ 11.5，远超 MiterLimit(2.2)
		// ⇒ AddChain 必须截断并用圆盘补角；这里只要求系数**有限且确实超限**（不能是 NaN/Inf）
		var p = OnGreat(0.7);
		BasisAtPoint(p, out var e1, out var e2);
		var (prev, next) = TurnAround(p, e1, e2, 170.0, Res4StepRad);
		Assert.That(RiverLineOverlay.JoinBasis(p, prev, next, out _, out _, out _, out float ms), Is.True);
		Assert.That(float.IsFinite(ms), Is.True, "急弯的 miter 系数不得是 NaN/Inf");
		Assert.That(ms, Is.GreaterThan(2.2f), "170° 急弯的 miter 系数必须超过上限 ⇒ 触发截断 + 补角");
	}

	[Test]
	public void JoinBasis_Reversal180_ReturnsFalse()
	{
		// 180° 折返：横向无定义 ⇒ 必须明确返回 false（由调用方退化处理），不能产出垃圾向量
		var p = OnGreat(0.7);
		BasisAtPoint(p, out var e1, out var e2);
		var (prev, next) = TurnAround(p, e1, e2, 180.0, Res4StepRad);
		Assert.That(RiverLineOverlay.JoinBasis(p, prev, next, out _, out _, out _, out _), Is.False,
			"180° 折返必须判为退化");
	}

	// ── ⑤ 退化段：必须返回 false，绝不产生 NaN ──────────────────────────────

	[Test]
	public void DegenerateSegments_ReturnFalse()
	{
		var d = OnGreat(0.3);
		Assert.That(RiverLineOverlay.SegmentSide(d, d, out var s0), Is.False, "端点重合 ⇒ 退化");
		Assert.That(s0, Is.EqualTo(Vector3.Zero));

		Assert.That(RiverLineOverlay.SegmentSide(d, -d, out var s1), Is.False, "反向 180° ⇒ 退化（横向无定义）");
		Assert.That(s1, Is.EqualTo(Vector3.Zero));

		Assert.That(RiverLineOverlay.SegmentSide(Vector3.Zero, d, out _), Is.False, "零向量 ⇒ 退化");
	}
}
