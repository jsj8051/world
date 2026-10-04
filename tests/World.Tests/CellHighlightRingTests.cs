using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Godot;
using World.NoiseWorld;
using World.Utils;
using World.Utils.H3;

namespace World.Tests;

/// <summary>
/// 选中高亮**球面环带**护栏（2026-10-04 用户拍板，取代"整格扇面 + 径向抬升 1.0005"）。
/// 只钉五件事（对应用户列的验收项）：
///   ① 无浮起  —— 所有顶点严格落在半径 R 上 ⇒ distance(顶点, 地形表面) = 0；
///   ② 无整格填充 —— 只有 2m 个边界顶点 + m 个四边形，没有格心顶点、没有扇面；
///   ③ ring 闭合 —— 四边形链首尾相接成单一闭环，每个顶点恰好被两个四边形共用；
///   ④ ring 宽度 —— UV2.x = widthPx / cos(π/m)（miter 补偿），UV.y = ∓1，UV2.y = depthBias；
///   ⑤ 法线 —— 角点切平面内的内角平分线（单位、切向、指向格心），不得含径向分量。
///
/// ★为什么"无浮起"要用断言钉死而不是靠肉眼：
///   旧写法把高亮画在 R×1.0005 上，径向抬升在掠射角下换算成横向屏幕位移——
///   实测 1.02R 最近视距的屏幕边缘位移达 14.6 px（≈10% 格宽），表现为"选中和实际有点偏差"。
///   这与河流"浮在表面上"是同一个病，已升格为表现层通用规则：
///   **分层只允许用 depth bias / stencil / overlay pass，不允许 position += normal * epsilon。**
///
/// 纪律（同 RiverSymbolWidthTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class CellHighlightRingTests
{
	const float R = 6371f;
	const float WidthPx = 3.0f;
	const float Bias = 4.0e-4f;

	// ── ① 无浮起 ────────────────────────────────────────────────────────────

	[Test]
	public void Ring_VerticesSitExactlyOnSurfaceRadius()
	{
		var g = Build(out _);
		Assert.That(g.V.Count, Is.GreaterThan(0), "本测试必须有顶点才有判别力");
		for (int i = 0; i < g.V.Count; i++)
		{
			// 旧写法的抬升量 = R×0.0005 = 3.19（km）⇒ 容差取 R×1e-5 = 0.064 足以判定，
			// 又远大于 float 在 R=6371 上的求模误差（~3e-4）。
			Assert.That(Math.Abs(g.V[i].Length() - R), Is.LessThan(R * 1e-5f),
				$"顶点 {i}：|v|={g.V[i].Length()} 必须严格等于 R={R}（径向抬升被明令禁止）");
		}
	}

	// ── ② 无整格填充 ────────────────────────────────────────────────────────

	[Test]
	public void Ring_HasNoCenterVertex_AndEveryVertexLiesOnBoundary()
	{
		var g = Build(out int m);

		Assert.That(g.V.Count, Is.EqualTo(2 * m),
			"环带 = m 个角点各内外两个顶点 ⇒ 恰好 2m，不得有格心顶点");
		Assert.That(g.Idx.Count, Is.EqualTo(6 * m),
			"m 个四边形 = 2m 三角 = 6m 索引 ⇒ 没有扇形填充面");

		// 更强的一条：每个顶点方向都必须等于某个 H3 角点方向（即"都在边界上"）
		var corners = CornerDirs(g.Cell, g.Center);
		for (int i = 0; i < g.V.Count; i++)
		{
			var d = (g.V[i] / R).Normalized();
			bool onBoundary = false;
			foreach (var c in corners)
				if (d.Dot(c) > 0.999999f) { onBoundary = true; break; }
			Assert.That(onBoundary, Is.True,
				$"顶点 {i} 不在 H3 角点方向上 ⇒ 环带里混进了非边界顶点（可能又画成扇面）");
		}
	}

	// ── ③ ring 闭合 ────────────────────────────────────────────────────────

	[Test]
	public void Ring_IsClosedSingleLoop_NoGapsNoDuplicates()
	{
		var g = Build(out int m);

		// (a) 闭环下每个顶点恰好被 3 个索引引用：
		//     本四边形里作为**对角顶点**被两个三角形共用 ⇒ 2；相邻四边形里作为端角 ⇒ 1。
		//     （少了 = 断口/豁口，多了 = 重复面）
		var refs = new int[g.V.Count];
		foreach (int i in g.Idx) refs[i]++;
		for (int i = 0; i < refs.Length; i++)
			Assert.That(refs[i], Is.EqualTo(3),
				$"顶点 {i} 被引用 {refs[i]} 次 ⇒ 闭环下必须恰好 3 次（2 本四边形对角 + 1 邻四边形端角）");

		// (b) 四边形链首尾相接：第 k 个的 (内k+1, 外k+1) 必须就是第 k+1 个的 (内k, 外k)
		for (int k = 0; k < m; k++)
		{
			int k2 = (k + 1) % m;
			int q0 = k * 6, q1 = k2 * 6;
			Assert.That(g.Idx[q0 + 4], Is.EqualTo(g.Idx[q1 + 1]), $"四边形 {k}→{k2} 内侧边不连续（有豁口）");
			Assert.That(g.Idx[q0 + 5], Is.EqualTo(g.Idx[q1 + 0]), $"四边形 {k}→{k2} 外侧边不连续（有豁口）");
		}
	}

	// ── ④ ring 宽度 ────────────────────────────────────────────────────────

	[Test]
	public void Ring_WidthAndDepthBiasTravelOnUv2()
	{
		var g = Build(out int m);

		// miter 补偿：沿内角平分线偏移 h 时，垂直于边的净宽度 = h·cos(π/m)
		// （两端垂距相同 ⇒ 外边界与边平行 ⇒ 整条边等宽、转角**不会**变细）。
		// 故预补 1/cos(π/m)，使实测宽度等于目标 px。
		float expected = WidthPx / MathF.Cos(MathF.PI / m);

		for (int i = 0; i < g.V.Count; i++)
		{
			Assert.That(g.Uv2[i].X, Is.EqualTo(expected).Within(1e-5f),
				$"顶点 {i}：UV2.x 必须是补偿后的目标 px 宽度 {expected:F4}");
			Assert.That(g.Uv2[i].Y, Is.EqualTo(Bias).Within(1e-9f),
				$"顶点 {i}：UV2.y 必须是 NDC 深度偏移（只改 z，不改 x/y ⇒ 零视差）");
			Assert.That(g.Uv[i].Y, Is.EqualTo(i % 2 == 0 ? 1f : -1f),
				$"顶点 {i}：UV.y 必须交替 ∓1（shader 据此在屏幕空间向内外各展开半个宽度）");
			Assert.That(g.Uv2[i].X, Is.GreaterThan(1f),
				"宽度必须 >1px 才有意义；★若走 COLOR.a 会被 Godot 夹到 1（河流已踩过此坑）");
		}
	}

	// ── ⑤ 法线 = 切平面内的内角平分线 ──────────────────────────────────────

	[Test]
	public void Ring_NormalsAreTangentialInwardBisectors()
	{
		var g = Build(out _);
		for (int i = 0; i < g.V.Count; i++)
		{
			Vector3 v = (g.V[i] / R).Normalized();
			Vector3 n = g.N[i];
			Assert.That(Math.Abs(n.Length() - 1f), Is.LessThan(1e-5f), $"顶点 {i}：法线必须是单位向量");
			Assert.That(Math.Abs(n.Dot(v)), Is.LessThan(1e-3f),
				$"顶点 {i}：法线不得含径向分量（切平面内 ⇒ 才可能沿球面展开而不是抬起来）");
			Vector3 inward = (g.Center - v * g.Center.Dot(v)).Normalized();
			Assert.That(n.Dot(inward), Is.GreaterThan(0.9999f),
				$"顶点 {i}：法线必须指向格心（正 m 边形下这就是内角平分线 = miter 方向）");
		}
	}

	// ── ⑥ 源码级：禁止 world-space radial lift ─────────────────────────────

	[Test]
	public void Highlight_MustNotLiftGeometryRadially()
	{
		string path = FindViewSource() ?? throw new System.IO.FileNotFoundException(
			"未找到 NoiseBallView.cs；跳过（路径脆弱，非失败）");

		string src = System.IO.File.ReadAllText(path);

		// ① 全文件：不得出现任何 radial lift 常量（注释里允许提反面例子 ⇒ 跳过注释行）
		foreach (var raw in System.IO.File.ReadLines(path))
		{
			var line = raw.Trim();
			if (line.StartsWith("//") || line.StartsWith("*") || line.StartsWith("/*")) continue;
			Assert.That(line, Does.Not.Contain("1.0005"),
				"选中高亮禁止 world-space radial lift：分层只能用 depth bias，不能用 position += normal * epsilon");
		}

		// ② 高亮**方法区内**：必须走 AddCellRing（环带），不得走 AddCellFan（整格扇面）。
		//    ⚠️ 不能全文件禁 AddCellFan——地形块 BuildChunkMesh 仍在合法使用它（地形本来就该是扇面）。
		int from = src.IndexOf("public void HighlightCell", StringComparison.Ordinal);
		int to = src.IndexOf("bool EnsureRingMaterial", StringComparison.Ordinal);
		Assert.That(from >= 0 && to > from, Is.True,
			"NoiseBallView.cs 结构变了（找不到 HighlightCell/EnsureRingMaterial 标记）⇒ 本断言需同步更新");
		var region = src.Substring(from, to - from);
		StringAssert.Contains("AddCellRing(", region, "高亮必须生成环带（AddCellRing）");
		StringAssert.DoesNotContain("AddCellFan(", region, "高亮不得再生成整格扇面（AddCellFan）");
	}

	// ── 辅助 ────────────────────────────────────────────────────────────────

	sealed class RingData
	{
		public ulong Cell;
		public Vector3 Center;
		public readonly List<Vector3> V = new();
		public readonly List<Vector3> N = new();
		public readonly List<Vector2> Uv = new();
		public readonly List<Vector2> Uv2 = new();
		public readonly List<Color> C = new();
		public readonly List<int> Idx = new();
	}

	static RingData Build(out int cornerCount)
	{
		ulong cell = H3.LatLngToCell(new LatLng(0.35, 0.9), 1);
		var center = CoordUtil.LatLngToSphere(H3.CellToLatLng(cell), 1f);
		cornerCount = H3.CellToVertexes(cell).Length;

		var d = new RingData { Cell = cell, Center = center };
		NoiseBallView.AddCellRing(d.V, d.N, d.Uv, d.Uv2, d.C, d.Idx,
			cell, center, R, WidthPx, new Color(1f, 1f, 1f), Bias);
		return d;
	}

	static Vector3[] CornerDirs(ulong cell, Vector3 centerDir)
	{
		var vids = H3.CellToVertexes(cell);
		var res = new Vector3[vids.Length];
		for (int k = 0; k < vids.Length; k++)
			res[k] = CoordUtil.LatLngToSphere(H3.VertexToLatLng(vids[k]), 1f).Normalized();
		return res;
	}

	static string FindViewSource()
	{
		var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		for (int i = 0; i < 6 && dir != null; i++)
		{
			var candidate = System.IO.Path.Combine(dir, "scripts", "noise_world", "NoiseBallView.cs");
			if (System.IO.File.Exists(candidate)) return candidate;
			dir = System.IO.Directory.GetParent(dir)?.FullName;
		}
		return null!;
	}
}
