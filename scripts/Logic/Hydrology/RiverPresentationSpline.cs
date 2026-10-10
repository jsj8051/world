using System;
using System.Collections.Generic;
using Godot;

namespace World.Logic;

// 世界生成空间 · 河流**表现样条**（River v2.0，决策 07 §13.19——**纯表现层，非水文**）：
//
// ★定位：H3 负责"河流在哪里"，Spline 负责"河流看起来怎么连续"，Shader 负责"屏幕上怎么表现"。
//   ⇒ 本类**只读** RiverGeometry 的锚点（H3 格心方向），**绝不修改**它们，也绝不回写任何水文事实。
//   ⇒ RiverNetwork / RiverGraph / WaterTopology / RiverGeometry 一概不动。
//
// ★★为什么要它：v1.x 的 ribbon 本质是"把六边形折线伪装成曲线"——join/bevel/corner smoothing
//   做得再好，中心线仍然只有 6 个方向。v2 不再修 ribbon，而是**另外生成一条连续样条**。
//
// ★锚点不可篡改（这是你担心的"spline 把河道拽走"的硬保证）：
//   采用 **Centripetal Catmull-Rom**，样条**穿过**每个锚点（Spline(Pi) = Pi，数学上精确成立），
//   不是最小二乘拟合、不是 B 样条近似 ⇒ 河流经过的 H3 节点一个都不会被挪动。
//   选 centripetal（α=0.5）而非 uniform：H3 路径是"长段→短段→急弯→长段"的**非均匀点距**，
//   uniform CR 在这种点距下会过冲/打环，centripetal 不会。
//
// ★球面处理：在 R³ 里做 centripetal CR，再 normalize 回单位球。
//   （normalize 在锚点处是恒等映射 ⇒ 不破坏"穿过锚点"。）
//
// ★★★最大视觉偏移护栏（最重要的一道安全阀）：
//   平滑只能"去锯齿"，不能"重新规划河道" ⇒ 每个采样点相对**弦 P1→P2** 的偏移被夹在
//   `锚点间距 × MaxDeviationRatio` 以内。超界即按比例拉回。
public static class RiverPresentationSpline
{
	/// <summary>最大视觉偏移 / 相邻锚点间距。0.15 ⇒ 平滑幅度永远小于格距的 15%。</summary>
	public const float MaxDeviationRatio = 0.15f;

	/// <summary>Centripetal Catmull-Rom 的 α（0.5 = centripetal；0 = uniform，会过冲）。</summary>
	public const float Alpha = 0.5f;

	/// <summary>每条 H3 edge 的最大采样段数（直线用 1 段，急弯才加密）。</summary>
	public const int MaxSamplesPerEdge = 6;

	/// <summary>采样密度参考曲率：转过这个角度 ⇒ 加密 1 段（π/12 = 15°）。</summary>
	public const float CurveRefRad = MathF.PI / 12f;

	/// <summary>
	/// 把一条 chain 的锚点变成**连续采样路径**。
	/// </summary>
	/// <param name="anchors">锚点（单位球方向，来自 RiverGeometry；**只读**）</param>
	/// <param name="pts">输出：采样后的单位球方向点（首 = anchors[0]，尾 = anchors[^1]）</param>
	/// <param name="segOf">输出：每个采样点所属的**锚点段**下标（用于查该段的符号宽度档位）</param>
	public static void Sample(IReadOnlyList<Vector3> anchors, out Vector3[] pts, out int[] segOf)
	{
		if (anchors == null || anchors.Count == 0)
		{
			pts = Array.Empty<Vector3>();
			segOf = Array.Empty<int>();
			return;
		}
		if (anchors.Count == 1)
		{
			pts = new[] { anchors[0] };
			segOf = new[] { 0 };
			return;
		}

		int m = anchors.Count;
		var outPts = new List<Vector3>(m * 2);
		var outSeg = new List<int>(m * 2);
		outPts.Add(anchors[0]);
		outSeg.Add(0);

		for (int j = 0; j + 1 < m; j++)
		{
			// ★端点用**反射虚拟控制点**（标准 Catmull-Rom 端点处理），而不是重复端点。
			//   ⚠️ 重复端点会让节点步长退化：实测 `t2 + 1e-9` 在 float32 下被舍入成 `t2` 本身
			//      ⇒ `t3 - t2 == 0` ⇒ 除零得 ±∞ ⇒ `(p3-p2) * ∞ = 0 × ∞ = NaN`（整段采样点全变 NaN）。
			var p1 = anchors[j];
			var p2 = anchors[j + 1];
			var p0 = j > 0 ? anchors[j - 1] : p1 + (p1 - p2);
			var p3 = j + 2 < m ? anchors[j + 2] : p2 + (p2 - p1);

			int n = SampleCount(anchors, j);
			float maxDev = MaxDeviationRatio * p1.DistanceTo(p2);

			for (int s = 1; s <= n; s++)
			{
				float t = s / (float)n;
				var q = Centripetal(p0, p1, p2, p3, t);
				if (!IsFinite(q)) q = p1 + (p2 - p1) * t;      // 数值兜底：退回弦上线性插值
				q = q.Normalized();
				q = ClampDeviation(q, p1, p2, t, maxDev);
				outPts.Add(IsFinite(q) ? q : (p1 + (p2 - p1) * t).Normalized());
				outSeg.Add(j);
			}
		}

		pts = outPts.ToArray();
		segOf = outSeg.ToArray();
	}

	/// <summary>
	/// 自适应采样密度 = f(curvature)：直线 1 段（不浪费三角形），急弯加密到 <see cref="MaxSamplesPerEdge"/>。
	/// 曲率代理 = 该段两端锚点处的**转角**。
	/// </summary>
	public static int SampleCount(IReadOnlyList<Vector3> anchors, int seg)
	{
		float turn = Math.Max(TurnAt(anchors, seg), TurnAt(anchors, seg + 1));
		int n = 1 + (int)MathF.Round(turn / CurveRefRad);
		return Math.Clamp(n, 1, MaxSamplesPerEdge);
	}

	/// <summary>锚点 k 处的转角（该点前后两段方向的夹角；端点 = 0）。</summary>
	public static float TurnAt(IReadOnlyList<Vector3> anchors, int k)
	{
		if (k <= 0 || k >= anchors.Count - 1) return 0f;
		var a = (anchors[k] - anchors[k - 1]).Normalized();
		var b = (anchors[k + 1] - anchors[k]).Normalized();
		return MathF.Acos(Math.Clamp(a.Dot(b), -1f, 1f));
	}

	/// <summary>
	/// Centripetal Catmull-Rom（Barry–Goldman 金字塔形式），t ∈ [0,1] 插值 P1→P2。
	/// ★t=0 ⇒ 精确等于 P1；t=1 ⇒ 精确等于 P2（样条**穿过**锚点，不是拟合）。
	/// </summary>
	public static Vector3 Centripetal(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
	{
		float d01 = KnotStep(p0, p1);
		float d12 = KnotStep(p1, p2);
		float d23 = KnotStep(p2, p3);

		float t0 = 0f, t1 = t0 + d01, t2 = t1 + d12, t3 = t2 + d23;
		// ⚠️ 节点必须**严格递增**：float32 下小的步长会被大的 t 吞掉（0.168 + 1e-9 == 0.168）
		//    ⇒ 一旦出现零区间，分母为 0 ⇒ ±∞ ⇒ 0×∞ = NaN。这里直接退回弦上线性插值。
		if (!(t1 > t0) || !(t2 > t1) || !(t3 > t2)) return p1 + (p2 - p1) * t;

		float tt = t1 + (t2 - t1) * t;

		var a1 = Lerp(p0, p1, (tt - t0) / (t1 - t0));
		var a2 = Lerp(p1, p2, (tt - t1) / (t2 - t1));
		var a3 = Lerp(p2, p3, (tt - t2) / (t3 - t2));
		var b1 = Lerp(a1, a2, (tt - t0) / (t2 - t0));
		var b2 = Lerp(a2, a3, (tt - t1) / (t3 - t1));
		return Lerp(b1, b2, (tt - t1) / (t2 - t1));
	}

	static float KnotStep(Vector3 a, Vector3 b)
	{
		// ⚠️ 下限取在**距离**上（1e-6）而不是幂之后：Pow(1e-6, 0.5) = 1e-3，
		//    相对典型节点 t≈0.17 仍高于 float32 分辨率（~1.2e-8）⇒ 不会被舍掉。
		float d = MathF.Max(a.DistanceTo(b), 1e-6f);
		return MathF.Pow(d, Alpha);
	}

	static bool IsFinite(Vector3 v) =>
		float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

	/// <summary>
	/// ★★★最大偏移护栏：把采样点相对"弦 P1→P2"的偏移夹到 maxDev 以内。
	/// 超过 ⇒ 沿偏移方向按比例拉回 ⇒ **平滑只是去锯齿，不会把河流重新规划**。
	/// （在锚点处偏移恒为 0 ⇒ 不破坏"穿过锚点"。）
	/// </summary>
	public static Vector3 ClampDeviation(Vector3 q, Vector3 p1, Vector3 p2, float t, float maxDev)
	{
		var chord = (p1 + (p2 - p1) * t).Normalized();
		var d = q - chord;
		float len = d.Length();
		if (len <= maxDev || len < 1e-12f) return q;
		return (chord + d * (maxDev / len)).Normalized();
	}

	static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;
}
