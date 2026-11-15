using System;
using System.Collections.Generic;
using Godot;
using World.H3Grid;    // Ball（H3 球壳数据层）
using World.Logic;     // RiverGeometry / RiverGraph（水文事实）
using World.Utils;     // SphericalRibbon（球面 ribbon 几何）

namespace World.Render;

// 表现层 · 河流表面叠加（**River v2.0**：Presentation Spline + River Surface Shader）
//
// ★职责三分：H3 河网决定"河流在哪里"（RiverNetwork/Graph/Geometry，**一律不动**）；
//   Spline 决定"看起来怎么连续"（穿过每个 H3 锚点）；Shader 决定"屏幕上怎么表现"（恒 px + 边缘柔化）。
// ★冻结定义：生产档河流 = **res 无关的连续中心线** + res 绑定的 H3 覆盖索引 + **屏幕空间符号宽度**；
//   **当前不表达物理河宽**（res4 下真实河宽是亚格量）⇒ 不建 WidthM/DepthM/BankGeometry/Floodplain/LOD2-3。
// ★唯一渲染路径：样条几何 + `river_surface.gdshader`，宽度以 px 交给 shader 展开 ⇒ 恒视觉宽度，
//   相机只改 shader 参数、不改几何（没有世界空间兜底版本，也没有 zoom 重烘）。
// ★颜色只由 `NodeKind` 决定（干流/支流/河口）；**不参与粗细**——粗细统一取 `RiverSymbolWidth.UniformWidthPx`。
// ★chain 化 + 边去重的必要性：`LineCells` 是"每河源一条完整路径"，干流段被 N 条线共有；
//   ribbon（三角带）下重复边 = 完全共面的重叠三角形 ⇒ z-fighting。`BuildChains` 经 `nextOf` 去重
//   ⇒ 每条河流边恰好属于一条 chain，几何零重复。
// 图层分层不靠物理抬升，靠 shader 的 NDC 深度偏移（`UV2.y`）：支流 < 干流 < 河口。
public sealed partial class RiverLineOverlay : MeshInstance3D, IMapLayer
{
	WorldFacts _layerFacts;

	public void SetContext(WorldFacts facts, MapMode mode)
	{
		Visible = facts != null;
		if (facts == null || ReferenceEquals(_layerFacts, facts)) return;
		BuildFromFacts(facts.Ball, facts.RiverLines, facts.RiverTopology);
		_layerFacts = facts;
	}

	// 几何**完全贴地**（lift = 1.0）：物理抬升在掠射角下会换算成大横向位移（旧 lift=1.0040 实测近景中位 55px）。
	const float SsLiftBranch = 1.0f;   // 支流：贴地
	const float SsLiftMain = 1.0f;     // 干流：贴地（靠 bias 更大 ⇒ 覆盖支流末端）
	const float SsLiftMouth = 1.0f;    // 河口点：贴地
	//   bias 量级**由实测扫参确定**（墨水判据，res4，目标宽 3.08px）：1e-4 起饱和
	//   （与 1e-3 完全一致 ⇒ 再大无收益也不会让背面河线穿透）。⚠️ 解析式估算严重偏小，别再按理论调。
	const float DepthBiasBranch = 1.0e-4f;   // 支流
	const float DepthBiasMain = 2.0e-4f;     // 干流（更大 ⇒ 覆盖支流末端）
	const float DepthBiasMouth = 3.0e-4f;    // 河口点
	const string ShaderPath = "res://shaders/river_surface.gdshader";

	static readonly Color TrunkColor = new(0.13f, 0.29f, 0.45f);      // 干流：深蓝灰（降饱和/降亮度）
	static readonly Color TributaryColor = new(0.30f, 0.47f, 0.60f);  // 支流：浅蓝灰
	static readonly Color MouthColor = new(0.20f, 0.62f, 0.60f);      // 河口：低饱和青

	// 上次发布的河网（重建网格只读这些缓存；相机变化不再触发重烘）
	Vector3[] _dirs;
	RiverGraph _graph;
	List<Chain> _chains;
	float _radius;
	bool _hasData;

	ShaderMaterial _shaderMat;

	/// <summary>一条 polyline chain（在 source / confluence 处断开；出口终止）。</summary>
	sealed class Chain
	{
		public int[] Cells;         // 格序列（含亚阈值中间格 ⇒ 几何连续）
		public Vector3[] Pts;       // 单位球心方向
		public bool Main;           // 干流（Trunk/Outlet）⇒ 抬得更高、覆盖支流末端
	}

	/// <summary>按本次发布的河网重建几何与网格（无河 ⇒ 隐藏）。世界事实换了才调一次。</summary>
	void BuildFromFacts(Ball ball, RiverGeometry geo, RiverGraph graph)
	{
		if (ball == null || geo == null || graph == null || geo.LineCells.Count == 0)
		{
			Visible = false;
			Mesh = null;
			_hasData = false;
			return;
		}

		var dirs = ball.CellDirs;
		var kinds = graph.NodeKind;

		// ① chain 化（边去重）+ 组装几何
		var chains = new List<Chain>();
		int conflicts = 0;
		foreach (var cells in BuildChains(geo, graph, out conflicts))
		{
			var pts = new Vector3[cells.Length];
			bool main = false;
			for (int i = 0; i < cells.Length; i++)
			{
				pts[i] = dirs[cells[i]];
				int k = cells[i] < kinds.Length ? kinds[cells[i]] : -1;
				if (k == (int)RiverGraph.RiverNodeKind.Trunk || k == (int)RiverGraph.RiverNodeKind.Outlet)
					main = true;
			}
			chains.Add(new Chain { Cells = cells, Pts = pts, Main = main });
		}

		// 冲突**暴露**而不是吞掉：保留首条（确定性），非 0 说明上游事实层数据异常。
		if (conflicts > 0)
			GD.PushWarning($"[RIVER] 河网拓扑冲突 {conflicts} 处：同一上游出现多个下游（保留首条）");

		_dirs = dirs;
		_graph = graph;
		_chains = chains;
		_radius = ball.Radius;
		_hasData = true;
		if (!EnsureShaderMaterial())
		{
			Visible = false;
			Mesh = null;
			_hasData = false;
			return;
		}
		BuildScreenSpace();
	}

	/// <summary>相机只喂 shader 参数（视口尺寸），几何与网格不动。</summary>
	public void UpdateCamera(Camera3D camera)
	{
		if (!_hasData || _shaderMat == null) return;
		var viewport = camera?.GetViewport();
		if (viewport != null) _shaderMat.SetShaderParameter("u_viewport", viewport.GetVisibleRect().Size);
	}

	bool EnsureShaderMaterial()
	{
		if (_shaderMat != null) return true;
		if (!ShaderMaterialFactory.TryCreate(ShaderPath, "[RIVER]", out _shaderMat)) return false;
		_shaderMat.SetShaderParameter("u_viewport", new Vector2(1920f, 1080f));
		return true;
	}

	/// <summary>样条几何 + shader：宽度以 px 交给 shader 在屏幕空间展开 ⇒ 恒视觉宽度。</summary>
	void BuildScreenSpace()
	{
		var graph = _graph;
		float radius = _radius;
		var branch = new RibbonBuf();
		var main = new RibbonBuf();

		foreach (var ch in _chains)
		{
			RiverPresentationSpline.Sample(ch.Pts, out var pts, out var segOf);
			if (pts.Length < 2) continue;
			int n = pts.Length;
			var cols = new Color[n];
			// ★统一最粗档（用户 2026-10-04 视觉决策）：不再按径流量分档变粗细。
			for (int i = 0; i < n; i++) cols[i] = SegColor(KindOf(ch, graph, segOf[i]));
			(ch.Main ? main : branch).AddPath(pts, RiverSymbolWidth.UniformWidthPx, cols, radius,
				ch.Main ? SsLiftMain : SsLiftBranch,
				ch.Main ? DepthBiasMain : DepthBiasBranch);
		}

		// 河口点（v2.0 不做河口形变；仍用点表达，UV2.x=0 ⇒ shader 不加宽，保持为点）
		var mouths = new List<Vector3>();
		var mouthN = new List<Vector3>();
		var mouthUv = new List<Vector2>();
		var mouthUv2 = new List<Vector2>();
		var mouthC = new List<Color>();
		foreach (int o in graph.Outlets)
		{
			if (o < 0 || o >= _dirs.Length) continue;
			mouths.Add(_dirs[o] * (radius * SsLiftMouth));
			mouthN.Add(_dirs[o]);
			mouthUv.Add(Vector2.Zero);
			mouthUv2.Add(new Vector2(0f, DepthBiasMouth));
			mouthC.Add(MouthColor);
		}

		var mesh = new ArrayMesh();
		AddIndexedSurface(mesh, branch);
		AddIndexedSurface(mesh, main);
		if (mouths.Count > 0) AddPointsSurface(mesh, mouths, mouthN, mouthUv, mouthUv2, mouthC);

		Mesh = mesh;
		MaterialOverride = _shaderMat;
		Visible = true;
	}

	/// <summary>采样点 i 处的球面横向单位向量（样条切线 × 径向）。</summary>
	static Vector3 SideAt(Vector3[] pts, int i)
	{
		var side = SphericalRibbon.SideAt(pts, i, out bool degenerate);
		if (!degenerate) return side;
		BasisAt(pts[i], out var u, out _);
		return u;
	}

	/// <summary>屏幕空间 ribbon 顶点缓冲：每个采样点 emit 两个同位置顶点，靠 UV.y = ∓1 在 shader 里展开。</summary>
	sealed class RibbonBuf
	{
		public readonly List<Vector3> V = new();
		public readonly List<Vector3> N = new();
		public readonly List<Vector2> Uv = new();
		public readonly List<Vector2> Uv2 = new();  // ★px 宽度走 UV2.x（COLOR.a 会被夹到 [0,1]）
		public readonly List<Color> C = new();
		public readonly List<int> I = new();

		public void AddPath(Vector3[] pts, float widthPx, Color[] color, float radius, float lift, float depthBias)
		{
			int n = pts.Length;
			if (n < 2) return;
			float r = radius * lift;
			int baseIdx = V.Count;
			// 预分配：重烘时按 chain 逐条追加，不预留会反复翻倍重建
			V.EnsureCapacity(baseIdx + 2 * n);
			N.EnsureCapacity(baseIdx + 2 * n);
			Uv.EnsureCapacity(baseIdx + 2 * n);
			Uv2.EnsureCapacity(baseIdx + 2 * n);
			C.EnsureCapacity(baseIdx + 2 * n);
			I.EnsureCapacity(I.Count + 6 * (n - 1));
			var uv2 = new Vector2(widthPx, depthBias);
			for (int i = 0; i < n; i++)
			{
				var v = pts[i] * r;
				var side = SideAt(pts, i);
				float u = i / (float)(n - 1);
				var c = new Color(color[i].R, color[i].G, color[i].B, 1f);   // 宽度不再走 A
				// UV2 = (目标 px 宽度, NDC 深度偏移)
				V.Add(v); N.Add(side); Uv.Add(new Vector2(u, -1f)); Uv2.Add(uv2); C.Add(c);
				V.Add(v); N.Add(side); Uv.Add(new Vector2(u, 1f)); Uv2.Add(uv2); C.Add(c);
			}
			SphericalRibbon.AppendQuadStrip(I, baseIdx, n);
		}
	}

	static void AddIndexedSurface(ArrayMesh mesh, RibbonBuf b)
	{
		if (b.I.Count == 0) return;
		var arr = new Godot.Collections.Array();
		arr.Resize((int)Mesh.ArrayType.Max);
		arr[(int)Mesh.ArrayType.Vertex] = b.V.ToArray();
		arr[(int)Mesh.ArrayType.Normal] = b.N.ToArray();
		arr[(int)Mesh.ArrayType.TexUV] = b.Uv.ToArray();
		arr[(int)Mesh.ArrayType.TexUV2] = b.Uv2.ToArray();
		arr[(int)Mesh.ArrayType.Color] = b.C.ToArray();
		arr[(int)Mesh.ArrayType.Index] = b.I.ToArray();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
	}

	static void AddPointsSurface(ArrayMesh mesh, List<Vector3> v, List<Vector3> n,
		List<Vector2> uv, List<Vector2> uv2, List<Color> c)
	{
		if (v.Count == 0) return;
		var arr = new Godot.Collections.Array();
		arr.Resize((int)Mesh.ArrayType.Max);
		arr[(int)Mesh.ArrayType.Vertex] = v.ToArray();
		arr[(int)Mesh.ArrayType.Normal] = n.ToArray();
		arr[(int)Mesh.ArrayType.TexUV] = uv.ToArray();
		arr[(int)Mesh.ArrayType.TexUV2] = uv2.ToArray();
		arr[(int)Mesh.ArrayType.Color] = c.ToArray();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Points, arr);
	}

	// ── chain 化 ──────────────────────────────────────────────────────────

	/// <summary>
	/// 把河网压成 polyline chain：在 **source（入度 0）/ confluence（入度 ≥2）** 处断开，出口终止。
	/// ★顺带做**边去重**：`LineCells` 是"每河源一条完整路径"，干流段被 N 条线共有；
	///   这里把所有边灌进字典（每格下游唯一）⇒ **每条河流边恰好属于一条 chain**，无重复几何。
	/// ★汇流点会同时是"支流 chain 的尾"和"自己 chain 的头" ⇒ 支流末端自然延伸进干流。
	/// ★保留 `LineCells` 里的**亚阈值中间格** ⇒ 几何仍连续（不是 river-cell 到 river-cell 的跳线）。
	/// </summary>
	public static List<int[]> BuildChains(RiverGeometry geo, RiverGraph graph, out int conflicts)
	{
		conflicts = 0;
		var res = new List<int[]>();
		if (geo == null || graph == null) return res;
		var kinds = graph.NodeKind;
		var up = graph.UpstreamCount;

		var nextOf = new Dictionary<int, int>();
		foreach (var cells in geo.LineCells)
			for (int k = 0; k + 1 < cells.Length; k++)
				TryAddEdge(nextOf, cells[k], cells[k + 1], ref conflicts);

		int n = kinds.Length;
		for (int h = 0; h < n; h++)
		{
			if (kinds[h] < 0) continue;                 // 非河流格
			if (up[h] == 1) continue;                   // 内部点：不可能是 chain 头
			var list = new List<int> { h };
			int cur = h;
			int guard = 0;
			while (guard++ <= n && nextOf.TryGetValue(cur, out var nx))
			{
				list.Add(nx);
				// nx 是汇流点 ⇒ 收尾（它自己也是下一条 chain 的头 ⇒ 重叠一格，正是汇流覆盖所需）
				if (kinds[nx] >= 0 && up[nx] != 1) break;
				cur = nx;
			}
			res.Add(list.ToArray());
		}
		return res;
	}

	/// <summary>
	/// successor map 加边：
	///   · **同一条边重复出现** ⇒ 正常去重（这正是干流被多条 source→outlet 路径共有时的预期行为）；
	///   · **同一上游出现不同下游** ⇒ **拓扑错误**，计数暴露，**绝不静默覆盖**。
	/// ⚠️ 绝不能写 `nextOf[from] = to` 了事——那不是去重，是**吞掉拓扑错误**。
	///    冲突时保留**首条**（确定性），由调用方决定如何暴露（日志 / 断言 / 测试）。
	/// 返回 true = 新加入了一条边。
	/// </summary>
	public static bool TryAddEdge(Dictionary<int, int> nextOf, int from, int to, ref int conflicts)
	{
		if (nextOf.TryGetValue(from, out var had))
		{
			if (had == to) return false;      // 同一条边 ⇒ 去重（正常）
			conflicts++;                      // 同一上游两个不同下游 ⇒ 拓扑错误，必须暴露
			return false;
		}
		nextOf[from] = to;
		return true;
	}

	/// <summary>p 处任取一组切平面正交基（退化回退时用）。</summary>
	static bool BasisAt(Vector3 p, out Vector3 u, out Vector3 v)
	{
		var axis = Math.Abs(p.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		var a = p.Cross(axis);
		if (!(a.LengthSquared() > 1e-12f)) { u = Vector3.Zero; v = Vector3.Zero; return false; }
		u = a.Normalized();
		v = p.Cross(u).Normalized();
		return true;
	}

	static int KindOf(Chain ch, RiverGraph graph, int segIndex)
	{
		int cell = ch.Cells[Math.Min(segIndex, ch.Cells.Length - 1)];
		return cell < graph.NodeKind.Length ? graph.NodeKind[cell] : -1;
	}

	// ── 颜色 ──────────────────────────────────────────────────────────────

	static Color SegColor(int kind) => IsMainStem(kind) ? TrunkColor : TributaryColor;

	static bool IsMainStem(int kind) =>
		kind == (int)RiverGraph.RiverNodeKind.Trunk ||
		kind == (int)RiverGraph.RiverNodeKind.Confluence ||
		kind == (int)RiverGraph.RiverNodeKind.Outlet;
}
