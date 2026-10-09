using Godot;
using System;
using System.Collections.Generic;
using World.Spatial;        // Ball（H3 球壳数据层）
using World.Utils;              // CoordUtil
using World.Utils.H3;

namespace World.Render;

// 格子查询与高亮环带（决策 08 §4.4 表现层保留资产 · 从 `NoiseBallView` 拆出）。
//
// ★为什么拆：拾取 / 高亮 / 海拔查询是**三种正交能力**，与"怎么把球画出来"（网格构建、
//   LOD、颜色纹理烘焙）无关。原`NoiseBallView` 把它们塞在一起，导致：
//     · 想要"点选一个格子"必须构造整个视图（含 122 块网格 + 纹理烘焙）；
//     · 诊断工具 `CellHighlightDiag` 只能反射整个视图才能测高亮环带。
//   拆开后高亮几何是**纯函数**（`AddCellRing`：给格 id 与半径，产出顶点/法线/UV/索引），
//   可独立测试，不碰Godot 场景树。
//
// ★表现层渲染规范（与河流同一套，勿退回旧写法）：
//   ① 几何必须贴真实表面——内外两条轮廓都在半径 R 上 ⇒ distance(顶点, 地形) = 0；
//   ② 环带厚度由 shader 沿切向在屏幕空间展开（恒 px）⇒ 与相机距离无关；
//   ③ 图层分层只由 clip-space Z 偏移（UV2.y）解决，不产生任何世界位移。
//   ⚠️ 严禁 `radius * 1.0005f` 这类 world-space radial lift：径向抬升在掠射角下会
//      换算成横向屏幕位移，实测旧写法在 1.02R 最近视距的屏幕边缘位移达 14.6px（≈10% 格宽）。
//
// 顶点属性契约（与 `shaders/river_surface.gdshader` **完全一致**，改动须同步）：
//   VERTEX  = 角点方向 × R（内外轮廓同位置）
//   NORMAL  = 角点切平面内的**内角平分线**单位向量（指向格心）
//   UV.y    = ∓1 ⇒ 片元里插值为 [-1,+1]，shader 据此在屏幕空间向内外各展开半个宽度
//   UV2.x   = 目标屏幕宽度（px）★不能走 COLOR.a（Godot 会把顶点色夹到 [0,1]）
//   UV2.y   = NDC 深度偏移（只改 z，不改 x/y ⇒ 零视差）
//   COLOR   = 高亮色
public static class CellQuery
{
	public const string HighlightShaderPath = "res://shaders/river_surface.gdshader";
	public const string HighlightNodeName = "CellHighlight";
	public const float HighlightWidthPx = 3.0f;      // 目标屏幕宽度（px）
	public const float HighlightDepthBias = 4.0e-4f; // 深度层次：> 河流最粗档(3e-4) ⇒ 选中压在河线之上
	public const float HighlightCore = 0.85f;        // |cross| < core ⇒ 实心；core→1 为抗锯齿淡出
	public static readonly Color HighlightColor = new(1f, 1f, 1f);

	/// <summary>格 cell 的边界角点**单位方向**，按环序返回（格心切平面上方位角升序⇒ 外视 CCW）。
	/// ★必须环序：H3 <c>CellToVertexes</c> 返回集不保证顺序，乱序扇面会交叉重叠
	///   （不透明地形块被同色 overdraft 掩盖，半透明高亮则缩成中心小多边形——实测踩坑）。
	/// 地形扇面（<see cref="BallView"/> 的 AddCellFan）与选中环带（<see cref="AddCellRing"/>）
	/// 共用本方法，保证两者边界**逐角点一致**。</summary>
	public static Vector3[] RingOrdered(ulong cell, Vector3 centerDir)
	{
		ulong[] vids = H3.CellToVertexes(cell);
		int m = vids.Length;
		Vector3 refUp = MathF.Abs(centerDir.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		Vector3 t1 = refUp.Cross(centerDir).Normalized();
		Vector3 t2 = centerDir.Cross(t1);
		var ring = new (float ang, Vector3 dir)[m];
		for (int k = 0; k < m; k++)
		{
			Vector3 vdir = CoordUtil.LatLngToSphere(H3.VertexToLatLng(vids[k]), 1f);
			Vector3 tangent = vdir - centerDir * vdir.Dot(centerDir);   // 切平面投影
			ring[k] = (MathF.Atan2(tangent.Dot(t2), tangent.Dot(t1)), vdir);
		}
		Array.Sort(ring, (a, b) => a.ang.CompareTo(b.ang));
		var outArr = new Vector3[m];
		for (int k = 0; k < m; k++) outArr[k] = ring[k].dir;
		return outArr;
	}

	/// <summary>一格 = 闭合环带：m 个角点各 emit 内外两个顶点 ⇒ m 个四边形（2m 三角）。
	/// ★没有格心顶点、没有共面填充 ⇒ z-fighting 面积为零，且天然只描边界。
	/// ★两条轮廓都落在半径 R 上（顶点 = 角点方向 × R），**不做任何径向抬升**。
	/// <para>纯函数：给格与半径，产出几何。不碰场景树、不读海拔、不依赖相机 ⇒ 可独立测试。</para></summary>
	public static void AddCellRing(List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs,
		List<Vector2> uv2, List<Color> cols, List<int> idx,
		ulong cell, Vector3 centerDir, float R, float widthPx, Color color, float depthBias)
	{
		var ringDirs = RingOrdered(cell, centerDir);
		int m = ringDirs.Length;

		// ★正 m 边形下"角点的径向"恰好是**内角平分线** ⇒ 就是标准 miter 接头，六个转角不断、不豁口。
		//   但沿平分线偏移 h 时，环带垂直于边的净宽度 = h·cos(π/m)（两端点垂距相同 ⇒ 外边界与边平行，
		//   整条边等宽，转角**不会**变细）。故此处预先补 1/cos(π/m)，使实测宽度等于 widthPx。
		float miterComp = 1f / MathF.Cos(MathF.PI / m);
		float w = widthPx * miterComp;

		int base0 = verts.Count;
		for (int k = 0; k < m; k++)
		{
			Vector3 v = ringDirs[k];
			// 角点 v 处切平面内指向格心的单位方向（= 内角平分线；退化时取任一切向）
			Vector3 t = centerDir - v * centerDir.Dot(v);
			if (t.LengthSquared() < 1e-18f)
			{
				Vector3 ru = MathF.Abs(v.Y) < 0.9f ? Vector3.Up : Vector3.Right;
				t = ru.Cross(v);
			}
			t = t.Normalized();

			var uv2v = new Vector2(w, depthBias);
			verts.Add(v * R); normals.Add(t); uvs.Add(new Vector2(0f, +1f)); uv2.Add(uv2v); cols.Add(color);
			verts.Add(v * R); normals.Add(t); uvs.Add(new Vector2(0f, -1f)); uv2.Add(uv2v); cols.Add(color);
		}
		for (int k = 0; k < m; k++)
		{
			int k2 = (k + 1) % m;
			int a = base0 + 2 * k;          // 外 k
			int b = a + 1;                  // 内 k
			int c = base0 + 2 * k2 + 1;     // 内 k+1
			int d = base0 + 2 * k2;         // 外 k+1
			idx.Add(a); idx.Add(b); idx.Add(c);
			idx.Add(a); idx.Add(c); idx.Add(d);
		}
	}

	/// <summary>屏幕点 → 命中格 id；未命中（射线不交球 / 球在身后）返回 null。
	/// 数学同旧 <c>BallView.PickCell</c>：本视图无自身变换，直接世界系求交。</summary>
	public static ulong? PickCell(Ball ball, Vector2 screenPos, Camera3D camera)
	{
		Vector3 o = camera.ProjectRayOrigin(screenPos);
		Vector3 d = camera.ProjectRayNormal(screenPos);
		float b = o.Dot(d);
		float c = o.LengthSquared() - ball.Radius * ball.Radius;
		float disc = b * b - c;
		if (disc < 0f) return null;
		float t = -b - MathF.Sqrt(disc);          // 最近交点（正面）
		if (t < 0f) return null;                  // 球在相机背后
		Vector3 dir = (o + d * t) / ball.Radius;  // 单位球方向
		// 公式收口在 BallGeoIndex.CellAt（原先与此处各一份相同实现）
		return ball.CellIds[BallGeoIndex.CellAt(ball, dir)];
	}

	/// <summary>格中心单位方向（剔除判据、高亮定位共用；绕开格表——基格必不在本档格表内）。</summary>
	public static Vector3 CellDir(ulong cell)
	{
		var ll = H3.CellToLatLng(cell);
		return CoordUtil.LatLngToSphere(ll, 1f);
	}
}
