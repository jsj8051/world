using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Godot;
using World.Spatial;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 河流**符号宽度**护栏（2026-10-04 冻结决策：生产档只做"符号粗细系统"，**不表达物理河宽**）。
/// 只钉四件事：
///   ① 宽度只是视觉量（纯函数 + 不得在水文事实类型里留下任何宽度/深度/河岸成员）；
///   ② 单调性（累积量大 ⇒ 档位不得更小）；
///   ③ 确定性（同一河段永远同一档位）；
///   ④ Resolution 契约（Lines = res 无关几何；LineCells = 绑定生成 res 的覆盖索引）。
/// 纪律（同 RiverGraphTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class RiverSymbolWidthTests
{
	// ── ① 宽度只是视觉量 ────────────────────────────────────────────────────

	[Test]
	public void Width_IsPureVisual_DoesNotLeakIntoHydrologyFacts()
	{
		// (a) 水文事实类型里**不得**出现任何宽度/深度/河岸/洪泛区成员——
		//     这是"不会以任何形式回写"的结构性钉子（含编译器生成的 backing field）。
		var rx = new Regex("Width|Depth|Px|Stroke|Bank|Floodplain|Wetland|Polygon", RegexOptions.IgnoreCase);
		var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
					BindingFlags.Static | BindingFlags.DeclaredOnly;
		foreach (var t in new[] { typeof(RiverNetwork), typeof(RiverGraph), typeof(RiverGeometry) })
			foreach (var m in t.GetMembers(flags))
				Assert.That(rx.IsMatch(m.Name), Is.False,
					$"{t.Name}.{m.Name}：水文事实层不得承载宽度/深度/河岸等表现量");

		// (b) RiverSymbolWidth 的公开 API **不得**接收水文/几何对象 ⇒ 物理上无法回写
		var forbidden = new[] { typeof(RiverNetwork), typeof(RiverGraph), typeof(RiverGeometry), typeof(Ball) };
		foreach (var m in typeof(RiverSymbolWidth).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
			foreach (var p in m.GetParameters())
				Assert.That(forbidden, Does.Not.Contain(p.ParameterType),
					$"RiverSymbolWidth.{m.Name}({p.ParameterType.Name})：符号宽度是纯视觉函数，不得吃水文对象");

		// (c) 无公开可变状态
		foreach (var f in typeof(RiverSymbolWidth).GetFields(BindingFlags.Public | BindingFlags.Static))
			Assert.That(f.IsInitOnly || f.IsLiteral, Is.True,
				$"RiverSymbolWidth.{f.Name} 必须是 readonly/const（视觉常量不得被写）");
	}

	[Test]
	public void Width_Classify_DoesNotMutateInput()
	{
		var input = new List<float>();
		var rnd = new Random(7);
		for (int i = 0; i < 512; i++) input.Add((float)rnd.NextDouble() * 1000f);
		var snapshot = input.ToArray();

		var cls = RiverSymbolWidth.Classify(input);

		Assert.That(cls.Length, Is.EqualTo(input.Count));
		for (int i = 0; i < input.Count; i++)
			Assert.That(input[i], Is.EqualTo(snapshot[i]), $"i={i}：Classify 不得改动输入");
	}

	[Test]
	public void Width_IsSymbolic_NotPhysicalMeters()
	{
		// 真实河宽在 res4（格边长 ≈26.1 km）是**亚格量**：800 m = 球半径的 1.26e-4。
		// 符号笔画必须显著大于它（否则全球视图下不可见）⇒ 也就**绝不可能是米数**。
		const float RealGiantRiverOverRadius = 800f / 6371000f;
		for (int c = 0; c < RiverSymbolWidth.ClassCount; c++)
		{
			float fullWorld = 2f * RiverSymbolWidth.HalfWidthWorld(1f, c);
			Assert.That(fullWorld, Is.GreaterThan(RealGiantRiverOverRadius * 5f),
				$"Class {c}：符号笔画必须远大于真实河宽（否则就是拿它当米数用了）");
		}

		// 离散档位：档位值必须严格递增（不出现 1.37 / 1.42 / 1.51 px 的噪声抖动）
		for (int c = 1; c < RiverSymbolWidth.ClassCount; c++)
			Assert.That(RiverSymbolWidth.StrokeWidthPx[c],
				Is.GreaterThan(RiverSymbolWidth.StrokeWidthPx[c - 1]),
				$"档位 {c} 必须比 {c - 1} 粗（离散分级，非连续映射）");

		// 软边只比笔画宽一点点（纯视觉过渡带，不是地理河岸）
		Assert.That(RiverSymbolWidth.SoftHalfWidthWorld(1f, 0),
			Is.GreaterThan(RiverSymbolWidth.HalfWidthWorld(1f, 0)));
	}

	// ── ② 单调性 ────────────────────────────────────────────────────────────

	[Test]
	public void Width_Classify_IsMonotoneInAccumulation()
	{
		var input = new List<float>();
		var rnd = new Random(11);
		for (int i = 0; i < 256; i++)
			input.Add(i % 16 == 0 ? 250f : (float)rnd.NextDouble() * 1000f);   // 含大量并列值

		var cls = RiverSymbolWidth.Classify(input);

		for (int i = 0; i < input.Count; i++)
			for (int j = 0; j < input.Count; j++)
				if (input[i] < input[j])
					Assert.That(cls[i], Is.LessThanOrEqualTo(cls[j]),
						$"单调性破缺：accum[{i}]={input[i]:F2} < accum[{j}]={input[j]:F2}，但档位 {cls[i]} > {cls[j]}");
	}

	[Test]
	public void Width_Classify_EqualAccumulationGivesEqualClass()
	{
		// 并列值必须落同一档（"没有差异就没有等级"）
		var input = new List<float> { 5f, 5f, 5f, 5f, 5f };
		var cls = RiverSymbolWidth.Classify(input);
		for (int i = 1; i < cls.Length; i++)
			Assert.That(cls[i], Is.EqualTo(cls[0]), "累积量相等 ⇒ 档位必须相等");
	}

	// ── ③ 确定性 ────────────────────────────────────────────────────────────

	[Test]
	public void Width_Classify_IsDeterministicAndOrderInvariant()
	{
		var input = new List<float>();
		var rnd = new Random(23);
		for (int i = 0; i < 300; i++) input.Add((float)rnd.NextDouble() * 5000f);

		var a = RiverSymbolWidth.Classify(input);
		var b = RiverSymbolWidth.Classify(input);
		Assert.That(a, Is.EqualTo(b), "同一输入两次分档必须逐段一致");

		// 顺序无关：档位由"值"决定，不由"遍历顺序"决定 ⇒ 重排后每个元素档位不变
		var perm = new List<int>();
		for (int i = 0; i < input.Count; i++) perm.Add(i);
		for (int i = perm.Count - 1; i > 0; i--)
		{
			int k = rnd.Next(i + 1);
			(perm[i], perm[k]) = (perm[k], perm[i]);
		}
		var shuffled = new List<float>();
		foreach (int p in perm) shuffled.Add(input[p]);
		var s = RiverSymbolWidth.Classify(shuffled);
		for (int i = 0; i < input.Count; i++)
			Assert.That(s[i], Is.EqualTo(a[perm[i]]), $"重排后元素 {perm[i]} 的档位必须不变");
	}

	// ── ③b v1.2：Camera-aware Symbol Width（不是 LOD，不换几何/数据）──────

	[Test]
	public void ZoomCompensation_IsBoundedAndMonotone()
	{
		Assert.That(RiverSymbolWidth.ZoomCompensation(1f), Is.EqualTo(1f).Within(1e-6f),
			"zoom = 1（全球远景参考视图）⇒ 不补偿（= v1.1 行为）");

		float prev = float.MaxValue;
		for (float z = 0.1f; z <= 64f; z *= 1.25f)
		{
			float c = RiverSymbolWidth.ZoomCompensation(z);
			Assert.That(c, Is.InRange(RiverSymbolWidth.CompMin, 1f), $"zoom={z:F2}：补偿必须落在 [CompMin, 1]");
			Assert.That(c, Is.LessThanOrEqualTo(prev + 1e-6f), $"zoom={z:F2}：补偿必须随 zoom 单调不增");
			prev = c;
		}
	}

	[Test]
	public void ZoomCompensation_ClampsOutsideSupportedRange()
	{
		// 超出正式支持区间 ⇒ 停在边界值（行为**确定**，不是"随它去"）
		Assert.That(RiverSymbolWidth.ZoomCompensation(0.01f),
			Is.EqualTo(RiverSymbolWidth.ZoomCompensation(RiverSymbolWidth.MinSupportedZoom)).Within(1e-6f));
		Assert.That(RiverSymbolWidth.ZoomCompensation(1000f),
			Is.EqualTo(RiverSymbolWidth.ZoomCompensation(RiverSymbolWidth.MaxSupportedZoom)).Within(1e-6f));

		foreach (float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
			Assert.That(RiverSymbolWidth.ZoomCompensation(bad), Is.EqualTo(1f),
				$"非法 zoom={bad} 必须退回 1（不补偿），绝不产生 NaN");
	}

	[Test]
	public void ZoomCompensation_ShrinksButDoesNotPretendConstantPx()
	{
		// v1.2 的目的**不是**恒 px，而是"不失控"：拉近时宽度必须变细，但不得细于 CompMin 比例。
		const float radius = 2f;
		float global = RiverSymbolWidth.HalfWidthWorld(radius, 2, RiverSymbolWidth.ZoomCompensation(1f));
		float regional = RiverSymbolWidth.HalfWidthWorld(radius, 2, RiverSymbolWidth.ZoomCompensation(3f));
		Assert.That(regional, Is.LessThan(global), "拉近时符号宽度必须被压缩（否则就是截图里'肥成高速公路'）");
		Assert.That(regional, Is.GreaterThanOrEqualTo(global * RiverSymbolWidth.CompMin - 1e-6f),
			"补偿有下界：不得假装自己是恒 px");
	}

	// ── ④ Resolution 契约 ───────────────────────────────────────────────────

	[Test]
	public void Geometry_LinesAreResIndependent_LineCellsAreResBound()
	{
		var b1 = new Ball(1, 1f);
		var (_, g1) = BuildWorld(b1);
		var b2 = new Ball(2, 1f);
		var (_, g2) = BuildWorld(b2);

		Assert.That(g1.LineCells.Count, Is.GreaterThan(0), "res1 基线必须有河（否则本测试无判别力）");
		Assert.That(g2.LineCells.Count, Is.GreaterThan(0), "res2 基线必须有河（否则本测试无判别力）");

		// (a) LineCells 必须绑定**生成它的** resolution
		Assert.That(g1.SourceRes, Is.EqualTo(1));
		Assert.That(g2.SourceRes, Is.EqualTo(2));
		foreach (var cells in g1.LineCells)
			foreach (int c in cells)
				Assert.That(c, Is.LessThan(b1.CellDirs.Length), "LineCells 索引必须落在生成 res 的格数内");
		foreach (var cells in g2.LineCells)
			foreach (int c in cells)
				Assert.That(c, Is.LessThan(b2.CellDirs.Length), "LineCells 索引必须落在生成 res 的格数内");

		// (b) Lines = **单位球心方向向量**：不含 res 相关尺度/索引语义 ⇒ 可叠到任意 res 网格
		Assert.That(g1.Lines.Count, Is.EqualTo(g1.LineCells.Count), "Lines 与 LineCells 必须一一对应");
		for (int li = 0; li < g1.Lines.Count; li++)
		{
			Assert.That(g1.Lines[li].Length, Is.EqualTo(g1.LineCells[li].Length));
			foreach (var p in g1.Lines[li])
				Assert.That(Math.Abs(p.Length() - 1.0), Is.LessThan(1e-4),
					"Lines 必须是单位方向向量（res 无关的连续几何）");
		}
	}

	// ── ⑤ 渲染通道护栏（2026-10-05 实测踩坑）─────────────────────────────────

	/// <summary>
	/// px 宽度必须走 UV2.x，不能走 COLOR.a。
	/// 原因：顶点色 COLOR 在 Godot 内部会被夹到 [0,1]，0.9~3.8 px 全被截成 1.0 ⇒ 河流退化成 1px 细线。
	/// 这是静默破坏整个 v2 屏幕空间宽度系统的 bug，必须用源码级断言钉死。
	/// </summary>
	[Test]
	public void Width_MustUseUv2_NotColorAlpha()
	{
		string path = FindShaderFile() ?? throw new System.IO.FileNotFoundException(
			"未找到 river_surface.gdshader；跳过（路径脆弱，非失败）");
		string src = System.IO.File.ReadAllText(path);
		StringAssert.Contains("target_px = UV2.x", src,
			"河流 shader 必须把 px 宽度读自 UV2.x");
		StringAssert.DoesNotContain("target_px = COLOR.a", src,
			"河流 shader 不能把 px 宽度读到 COLOR.a——会被夹到 [0,1]");
	}

	static string FindShaderFile()
	{
		var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		for (int i = 0; i < 6 && dir != null; i++)
		{
			var candidate = System.IO.Path.Combine(dir, "shaders", "river_surface.gdshader");
			if (System.IO.File.Exists(candidate)) return candidate;
			dir = System.IO.Directory.GetParent(dir)?.FullName;
		}
		return null!;
	}

	// ── 辅助 ────────────────────────────────────────────────────────────────

	public static (RiverGraph graph, RiverGeometry geo) BuildWorld(Ball ball, int seed = 42, int threshold = 12)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var g = new GeologicalRegions(seed);
		g.Generate(ball, proj, 8_000_000f);
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);
		m.Generate(ball, g, surface);
		var l = new RegionalLandforms(seed);
		l.Generate(ball, g);
		var c = new HeightComposer(seed);
		c.Generate(ball, surface, g, new List<FeatureField>
		{
			new(l, TerrainDomain.LandOnly),
			new(m, TerrainDomain.LandAndSea),
		});
		var f = new FinalGeography();
		f.Generate(ball, c, g);
		var r = new RiverNetwork();
		r.Generate(ball, f, c, riverThresholdCells: threshold);
		var graph = new RiverGraph();
		graph.Generate(ball, f, r);
		var geo = new RiverGeometry();
		geo.Generate(ball, graph, r);
		return (graph, geo);
	}
}
