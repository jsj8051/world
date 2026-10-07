using System;
using System.Collections.Generic;
using Godot;
using World.Spatial;    // Ball（H3 球壳数据层）

namespace World.WorldGen;

// 表现层 · 河流表面叠加（**River v2.0**：Presentation Spline + River Surface Shader）
//
// ★职责三分：H3 河网决定"河流在哪里"（RiverNetwork/Graph/Geometry，**一律不动**）；
//   Spline 决定"看起来怎么连续"（穿过每个 H3 锚点）；Shader 决定"屏幕上怎么表现"（恒 px + 边缘柔化）。
// ★冻结定义：生产档河流 = **res 无关的连续中心线** + res 绑定的 H3 覆盖索引 + **屏幕空间符号宽度**；
//   **当前不表达物理河宽**（res4 下真实河宽是亚格量）⇒ 不建 WidthM/DepthM/BankGeometry/Floodplain/LOD2-3。
// ★两条渲染路径：① ScreenSpace（默认）＝样条几何 + river_surface.gdshader，恒 px 宽度；
//   ② WorldSpace（兜底）＝ camera-aware 世界宽度 ribbon，仅 shader 加载失败或 `UseScreenSpace=false` 时启用。
// ★粗细**属于河段**（上游格径流累积 → `RiverSymbolWidth` 分档）；`NodeKind` 只决定颜色与河口打点，
//   **不参与粗细**——否则单调性不成立。`RiverSoftEdge` **不是** RiverBank，是纯视觉过渡带（真实河岸 res4 下 ≈0 px）。
// ★chain 化 + 边去重的必要性：`LineCells` 是"每河源一条完整路径"，干流段被 N 条线共有；
//   ribbon（三角带）下重复边 = 完全共面的重叠三角形 ⇒ z-fighting。`BuildChains` 经 `nextOf` 去重
//   ⇒ 每条河流边恰好属于一条 chain，几何零重复。
// 抬升分层：软边(支流) < 软边(干流) < 河水(支流) < 河水(干流) < 河口点——干流覆盖支流末端，
//   不需要 junction mesh。⚠️ 渲染不得用 world-space radial lift 排序图层（见 SS 路径的 NDC 深度偏移）。
public sealed partial class RiverLineOverlay : MeshInstance3D
{
	// ── 抬升（单半径球：地表 = 方向 × R）──────────────────────────────────
	// ⚠️⚠️ 物理抬升在**掠射角下会换算成很大的横向位移**（河线偏离真实河道 = "浮起来"的观感来源）。
	//   现值 = 离地 1.6 / 2.5 / 3.8 km（全球视图 ≈0.14 px，肉眼不可见但仍高于地形）。
	//   下限依据（实测口径 R=2、Near=0.02R、Far=10R、24-bit 深度）：深度精度 5R 处 9.5e-5、
	//   地形多边形凹陷 res3→2.9e-5 ⇒ 取 5e-4 起（最坏 ~5× / 17× 余量）。勿再调大。
	const float SoftLift = 1.00020f;      // 软边（支流档）
	const float WaterLift = 1.00030f;     // 河水（支流档）
	const float MainLiftStep = 0.00010f;  // 干流档再抬一层 ⇒ 汇流处干流覆盖支流
	const float MouthLift = 1.00060f;     // 河口点（v1.x 兜底路径用）

	// ── v2 屏幕空间路径：几何**完全贴地**（lift = 1.0）────────────────────
	// ★深度层次不靠物理抬升，靠 shader 的 **NDC 深度偏移**（`UV2.y`，只改 z 不改 x/y）：
	//   物理抬升在掠射角下换算成大横向位移（旧 lift=1.0040 实测近景中位 55px ⇒ 河线偏离河道）；
	//   NDC 偏移 ⇒ 屏幕位置零视差，且恒定偏移对应的世界距离 ∝ z² 正好匹配深度精度的 1/z² 分布。
	const float SsLiftBranch = 1.0f;   // 支流：贴地
	const float SsLiftMain = 1.0f;     // 干流：贴地（靠 bias 更大 ⇒ 覆盖支流末端）
	const float SsLiftMouth = 1.0f;    // 河口点：贴地
	//   bias 量级**由实测扫参确定**（墨水判据，res4，目标宽 3.08px）：1e-4 起饱和
	//   （与 1e-3 完全一致 ⇒ 再大无收益也不会让背面河线穿透）。⚠️ 解析式估算严重偏小，别再按理论调。
	const float DepthBiasBranch = 1.0e-4f;   // 支流
	const float DepthBiasMain = 2.0e-4f;     // 干流（更大 ⇒ 覆盖支流末端）
	const float DepthBiasMouth = 3.0e-4f;    // 河口点
	const string ShaderPath = "res://shaders/river_surface.gdshader";

	// ── v1.1 视觉参数（重标定：软边收窄、不再往纯白混）────────────────────
	const float SoftMix = 0.28f;         // 软边向"同亮度淡灰"混合的比例（v1 是 0.62 → 纯白，太像 UI）
	const float SoftLift_0 = 0.45f;      // 该淡灰相对原亮度的提亮幅度
	const int CapSegments = 6;           // 圆头 cap / 急弯填充的扇形分段数
	const float MiterLimit = 2.2f;       // miter 长度上限（≈ 拐角 >127° 时截断并用圆盘补角）

	static readonly Color TrunkColor = new(0.13f, 0.29f, 0.45f);      // 干流：深蓝灰（降饱和/降亮度）
	static readonly Color TributaryColor = new(0.30f, 0.47f, 0.60f);  // 支流：浅蓝灰
	static readonly Color MouthColor = new(0.20f, 0.62f, 0.60f);      // 河口：低饱和青

	// v1.2 缓存：相机补偿只改宽度 ⇒ 换 zoom 时重烘网格，不必重算 chain / 档位
	Vector3[] _dirs;
	RiverGraph _graph;
	List<Chain> _chains;
	float _radius;
	float _zoomComp = 1f;
	bool _hasData;

	ShaderMaterial _shaderMat;
	bool _shaderTried;

	/// <summary>
	/// **v2 正式路径**：true = 样条几何 + river_surface.gdshader（**恒 px** 宽度，不再需要 v1.2 zoom 补偿）；
	/// false = 退回 v1.2 的 camera-aware 世界空间 ribbon。shader 加载失败时自动退回 false。
	/// </summary>
	public bool UseScreenSpace { get; set; } = true;

	/// <summary>shader 是否真的可用（false ⇒ 已退回 world-space 兜底）。</summary>
	public bool ShaderActive => _shaderMat != null;

	/// <summary>
	/// 建 chain 时检测到的**拓扑冲突数**（同一上游出现两个不同下游）。
	/// 正常河网必须为 0；非 0 说明上游事实层数据异常 ⇒ 这里**暴露**，不静默覆盖。
	/// </summary>
	public int TopologyConflicts { get; private set; }

	/// <summary>一条 polyline chain（在 source / confluence 处断开；出口终止）。</summary>
	sealed class Chain
	{
		public int[] Cells;         // 格序列（含亚阈值中间格 ⇒ 几何连续）
		public Vector3[] Pts;       // 单位球心方向
		public int[] Class;         // 每段（Cells[k] → Cells[k+1]）的符号宽度档位
		public bool Main;           // 干流（Trunk/Outlet）⇒ 抬得更高、覆盖支流末端
	}

	/// <summary>按当前河网重建网格（Regenerate 后调用；无河 ⇒ 隐藏）。</summary>
	public void Build(Ball ball, RiverGeometry geo, RiverGraph graph)
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
		float radius = ball.Radius;

		// ① chain 化（边去重）+ 组装几何
		var chains = new List<Chain>();
		var segAccum = new List<float>();
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
			// 河段累积量 = **上游格**的径流累积（沿 flow 单调不减 ⇒ 下游自然更粗）
			for (int k = 0; k + 1 < cells.Length; k++)
				segAccum.Add(cells[k] < graph.AccumMm.Length ? graph.AccumMm[cells[k]] : 0f);
		}

		// ② 符号宽度分档（纯函数，全链一次性分档；不改任何水文事实）
		var clsAll = RiverSymbolWidth.Classify(segAccum);
		int q = 0;
		foreach (var ch in chains)
		{
			int m = Math.Max(0, ch.Cells.Length - 1);
			ch.Class = new int[m];
			for (int k = 0; k < m; k++) ch.Class[k] = clsAll[q++];
		}

		// 冲突**暴露**而不是吞掉：保留首条（确定性），计数挂到 TopologyConflicts 供诊断读取
		TopologyConflicts = conflicts;

		// 缓存：v1.2 的相机补偿只改宽度、不改拓扑 ⇒ 换 zoom 时重烘网格即可，不必重算 chain
		_dirs = dirs;
		_graph = graph;
		_chains = chains;
		_radius = radius;
		_hasData = true;
		RebuildMesh();
	}

	/// <summary>
	/// **v1.2：Camera-aware Symbol Width**——按当前相机重算**表现宽度**。
	/// ★只改宽度系数：chain / 档位分类 / RiverGraph / 水文事实**一个字节都不动**。
	///   Camera → presentation parameter → RiverLineOverlay（单向，绝不反向污染 RiverGeometry/RiverGraph，
	///   更不会"zoom 越近就重算河网"）。
	/// 变化小于 2% 不重建（避免每帧重烘几十万顶点）。
	/// </summary>
	public void UpdateCameraScale(Camera3D camera)
	{
		if (!_hasData) return;

		if (_shaderMat != null)
		{
			var vp0 = camera?.GetViewport();
			if (vp0 != null) _shaderMat.SetShaderParameter("u_viewport", vp0.GetVisibleRect().Size);
			_shaderMat.SetShaderParameter("u_world_per_px",
				WorldUnitsPerPixel(camera, _radius, GlobalTransform.Origin));
		}

		// ★屏幕空间路径：宽度是 px，相机**不改变几何** ⇒ 无需重烘（这也是 v2 相对 v1.2 的一大收益）
		if (UseScreenSpace) return;

		float comp = RiverSymbolWidth.ZoomCompensation(ZoomOf(camera, _radius, GlobalTransform.Origin));
		if (Math.Abs(comp - _zoomComp) < 0.02f) return;
		_zoomComp = comp;
		RebuildMesh();
	}

	void RebuildMesh()
	{
		if (_graph == null || _chains == null || _dirs == null) return;
		if (UseScreenSpace && EnsureShaderMaterial()) BuildScreenSpace();
		else BuildWorldSpace();
	}

	bool EnsureShaderMaterial()
	{
		if (_shaderMat != null) return true;
		if (_shaderTried) return false;
		_shaderTried = true;
		var sh = GD.Load<Shader>(ShaderPath);
		if (sh == null) return false;                    // shader 缺失 ⇒ 自动退回 world-space 兜底
		_shaderMat = new ShaderMaterial { Shader = sh };
		_shaderMat.SetShaderParameter("u_screen_space", 1.0f);
		_shaderMat.SetShaderParameter("u_viewport", new Vector2(1920f, 1080f));
		return true;
	}

	/// <summary>
	/// **v2 正式路径**：样条几何 + shader。宽度直接给 **px**（`StrokeWidthPx`），
	/// 由 shader 在屏幕空间展开 ⇒ **恒视觉宽度**，v1.2 的 zoom 补偿在这里彻底不再需要。
	/// </summary>
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
			var widthPx = new float[n];
			var cols = new Color[n];
			for (int i = 0; i < n; i++)
			{
				// ★统一最粗档（用户 2026-10-04 视觉决策）：不再按径流量分档变粗细。
				//   `segOf` / `ch.Class` 仍算（兜底路径要用），但屏幕空间路径固定取最大档。
				widthPx[i] = RiverSymbolWidth.UniformWidthPx;   // ★px，不是世界单位
				cols[i] = SegColor(KindOf(ch, graph, segOf[i]), soft: false);
			}
			(ch.Main ? main : branch).AddPath(pts, widthPx, cols, radius,
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
		int n = pts.Length;
		var p = pts[i];
		var prev = pts[i > 0 ? i - 1 : 0];
		var next = pts[i + 1 < n ? i + 1 : n - 1];
		var v = next - prev;
		v -= p * p.Dot(v);                       // 投影到 p 的切平面
		float len = v.Length();
		if (len < 1e-9f)
		{
			BasisAt(p, out var u, out _);
			return u;
		}
		return p.Cross(v / len).Normalized();
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

		public void AddPath(Vector3[] pts, float[] widthPx, Color[] color, float radius, float lift, float depthBias)
		{
			int n = pts.Length;
			if (n < 2) return;
			float r = radius * lift;
			int baseIdx = V.Count;
			for (int i = 0; i < n; i++)
			{
				var v = pts[i] * r;
				var side = SideAt(pts, i);
				float u = i / (float)(n - 1);
				var c = new Color(color[i].R, color[i].G, color[i].B, 1f);   // 宽度不再走 A
				// UV2 = (目标 px 宽度, NDC 深度偏移)
				var uv2 = new Vector2(widthPx[i], depthBias);
				V.Add(v); N.Add(side); Uv.Add(new Vector2(u, -1f)); Uv2.Add(uv2); C.Add(c);
				V.Add(v); N.Add(side); Uv.Add(new Vector2(u, 1f)); Uv2.Add(uv2); C.Add(c);
			}
			for (int i = 0; i + 1 < n; i++)
			{
				int a = baseIdx + 2 * i, b = a + 1, c = a + 2, d = a + 3;
				I.Add(a); I.Add(b); I.Add(c);
				I.Add(b); I.Add(d); I.Add(c);
			}
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

	/// <summary>v1.2 兜底路径：camera-aware **世界空间**宽度 ribbon（shader 不可用时使用）。</summary>
	void BuildWorldSpace()
	{
		var graph = _graph;
		var chains = _chains;
		var dirs = _dirs;
		float radius = _radius;
		if (graph == null || chains == null || dirs == null) return;

		// 同一套 ribbon 拓扑生成两层：软边（下）→ 河水（上）；干流最后 ⇒ 覆盖汇流处
		var softV = new List<Vector3>();
		var softC = new List<Color>();
		var waterV = new List<Vector3>();
		var waterC = new List<Color>();
		foreach (var ch in chains) if (!ch.Main) AddChain(ch, graph, radius, SoftLift, true, softV, softC, _zoomComp);
		foreach (var ch in chains) if (ch.Main) AddChain(ch, graph, radius, SoftLift + MainLiftStep, true, softV, softC, _zoomComp);
		foreach (var ch in chains) if (!ch.Main) AddChain(ch, graph, radius, WaterLift, false, waterV, waterC, _zoomComp);
		foreach (var ch in chains) if (ch.Main) AddChain(ch, graph, radius, WaterLift + MainLiftStep, false, waterV, waterC, _zoomComp);

		// 河口点（用 RiverGraph.Outlets ⇒ 天然去重，v1 用 LineOutlet 会按河源重复打点）
		var mouths = new List<Vector3>();
		var mouthCols = new List<Color>();
		foreach (int o in graph.Outlets)
		{
			if (o < 0 || o >= dirs.Length) continue;
			mouths.Add(dirs[o] * (radius * MouthLift));
			mouthCols.Add(MouthColor);
		}

		var mesh = new ArrayMesh();
		AddSurface(mesh, Mesh.PrimitiveType.Triangles, softV, softC);
		AddSurface(mesh, Mesh.PrimitiveType.Triangles, waterV, waterC);
		if (mouths.Count > 0) AddSurface(mesh, Mesh.PrimitiveType.Points, mouths, mouthCols);

		Mesh = mesh;
		MaterialOverride = SharedMaterial();
		Visible = true;
	}

	/// <summary>
	/// 当前相机下的 zoom = uRef / uNow（uRef = 参考视图的"世界单位 / 屏幕像素"）。
	///   zoom = 1 ⇒ 星球直径正好铺满 <see cref="RiverSymbolWidth.ReferenceGlobePx"/> px（全球远景参考视图）
	///   zoom &gt; 1 ⇒ 拉近（同一世界宽度占更多像素 ⇒ 不补偿就会变粗）
	/// 透视按"星球中心所在深度"的可见高度算；正交按 `cam.Size` 算。拿不到相机 ⇒ 退回 1（= v1.1 行为）。
	/// </summary>
	public static float WorldUnitsPerPixel(Camera3D cam, float radius, Vector3 planetCenter)
	{
		if (cam == null || radius <= 0f) return 0f;
		var vp = cam.GetViewport();
		float h = vp != null ? vp.GetVisibleRect().Size.Y : 0f;
		if (!(h > 0f)) return 0f;

		double visibleH;
		if (cam.Projection == Camera3D.ProjectionType.Orthogonal)
		{
			visibleH = 2.0 * cam.Size;
		}
		else
		{
			float d = cam.GlobalPosition.DistanceTo(planetCenter);
			if (!(d > 1e-6f)) return 0f;
			visibleH = 2.0 * d * Math.Tan(cam.Fov * Math.PI / 360.0);   // 2·d·tan(fov/2)
		}
		if (!(visibleH > 1e-9)) return 0f;
		return (float)(visibleH / h);
	}

	public static float ZoomOf(Camera3D cam, float radius, Vector3 planetCenter)
	{
		float uNow = WorldUnitsPerPixel(cam, radius, planetCenter);
		if (!(uNow > 0f)) return 1f;
		float uRef = 2f * radius / RiverSymbolWidth.ReferenceGlobePx;
		return uRef / uNow;
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

	// ── ribbon 生成（一条 chain 一次成型，带 join / cap）──────────────────

	static void AddChain(Chain ch, RiverGraph graph, float radius, float lift, bool soft,
		List<Vector3> verts, List<Color> cols, float zoomComp)
	{
		var pts = ch.Pts;
		int n = pts.Length;
		if (n == 0) return;
		float r = radius * lift;

		// zoomComp = v1.2 相机感知补偿（★只缩放**表现宽度**，不改档位分类、不改拓扑）
		float HalfOf(int cls) => soft
			? RiverSymbolWidth.SoftHalfWidthWorld(radius, cls, zoomComp)
			: RiverSymbolWidth.HalfWidthWorld(radius, cls, zoomComp);

		// 逐**顶点**半宽：段宽取上游格档位，顶点取相邻段均值 ⇒ 档位切换处平滑过渡（不是硬台阶）
		var w = new float[n];
		if (n == 1)
		{
			w[0] = HalfOf(0);
		}
		else
		{
			w[0] = HalfOf(ch.Class[0]);
			for (int i = 1; i < n - 1; i++) w[i] = 0.5f * (HalfOf(ch.Class[i - 1]) + HalfOf(ch.Class[i]));
			w[n - 1] = HalfOf(ch.Class[n - 2]);
		}

		// 单格河（孤立河 / 既是源也是出口）：没有方向 ⇒ 只画一个圆点（round cap）
		if (n == 1)
		{
			BasisAt(pts[0], out var u0, out _);
			AddDisc(pts[0], u0, w[0], r, SegColor(KindOf(ch, graph, 0), soft), verts, cols);
			return;
		}

		// 逐顶点的**共享**横向 + miter 缩放（共享是关键：相邻两段用同一个偏移 ⇒ 没有缺口）
		var side = new Vector3[n];
		var scale = new float[n];
		var needFill = new bool[n];
		side[0] = SegmentSide(pts[0], pts[1], out var s0) ? s0 : BasisAt(pts[0], out var t0, out _) ? t0 : pts[0];
		scale[0] = 1f;
		side[n - 1] = SegmentSide(pts[n - 2], pts[n - 1], out var sN) ? sN : side[0];
		scale[n - 1] = 1f;
		for (int i = 1; i < n - 1; i++)
		{
			if (JoinBasis(pts[i], pts[i - 1], pts[i + 1], out _, out _, out var sj, out float ms))
			{
				side[i] = sj;
				scale[i] = Math.Min(ms, MiterLimit);
				needFill[i] = ms > MiterLimit;     // 截断处补一个圆盘，避免出现凹口
			}
			else
			{
				// 180° 折返：横向无定义 ⇒ 退化处理（沿用前一段方向 + 补角）
				side[i] = side[i - 1];
				scale[i] = MiterLimit;
				needFill[i] = true;
			}
		}

		// 主体：连续三角带
		for (int i = 0; i + 1 < n; i++)
		{
			var a = pts[i];
			var b = pts[i + 1];
			var oa = side[i] * (w[i] * scale[i]);
			var ob = side[i + 1] * (w[i + 1] * scale[i + 1]);
			var p1 = (a + oa) * r;
			var p2 = (a - oa) * r;
			var p3 = (b - ob) * r;
			var p4 = (b + ob) * r;
			var c = SegColor(KindOf(ch, graph, i), soft);

			verts.Add(p1); cols.Add(c);
			verts.Add(p2); cols.Add(c);
			verts.Add(p3); cols.Add(c);

			verts.Add(p1); cols.Add(c);
			verts.Add(p3); cols.Add(c);
			verts.Add(p4); cols.Add(c);
		}

		// round cap：两端各补一个圆盘（v1 的平切端点不再出现）
		AddDisc(pts[0], side[0], w[0], r, SegColor(KindOf(ch, graph, 0), soft), verts, cols);
		AddDisc(pts[n - 1], side[n - 1], w[n - 1], r, SegColor(KindOf(ch, graph, n - 2), soft), verts, cols);
		// 急弯补角（miter 被截断处）
		for (int i = 1; i < n - 1; i++)
			if (needFill[i])
				AddDisc(pts[i], side[i], w[i], r, SegColor(KindOf(ch, graph, i), soft), verts, cols);
	}

	static int KindOf(Chain ch, RiverGraph graph, int segIndex)
	{
		int cell = ch.Cells[Math.Min(segIndex, ch.Cells.Length - 1)];
		return cell < graph.NodeKind.Length ? graph.NodeKind[cell] : -1;
	}

	/// <summary>在 p 的切平面内画一个圆盘（round cap / 急弯补角）：u 为任一单位切向。</summary>
	static void AddDisc(Vector3 p, Vector3 u, float wWorld, float r, Color c,
		List<Vector3> verts, List<Color> cols)
	{
		if (!(wWorld > 0f)) return;
		var v = p.Cross(u).Normalized();
		var center = p * r;
		for (int k = 0; k < CapSegments; k++)
		{
			double a0 = k * 2.0 * Math.PI / CapSegments;
			double a1 = (k + 1) * 2.0 * Math.PI / CapSegments;
			var q0 = (p + (u * (float)Math.Cos(a0) + v * (float)Math.Sin(a0)) * wWorld) * r;
			var q1 = (p + (u * (float)Math.Cos(a1) + v * (float)Math.Sin(a1)) * wWorld) * r;
			verts.Add(center); cols.Add(c);
			verts.Add(q0); cols.Add(c);
			verts.Add(q1); cols.Add(c);
		}
	}

	// ── 球面 join 数学 ────────────────────────────────────────────────────

	/// <summary>
	/// 内部顶点的 join 基：同时看前后方向，给出**共享横向**与 miter 缩放。
	///   tPrev = 投影到 p 切平面的"来向"切向；tNext = "去向"切向
	///   n1 = cross(p, tPrev)；n2 = cross(p, tNext)
	///   miter 方向 = normalize(n1 + n2)；miter 长度系数 = |n1+n2| / (1 + n1·n2) = 1 / cos(φ/2)
	/// ⇒ 拐角 φ=0 时系数 = 1（与直线完全一致）；φ=90° 时 = √2。
	/// 返回 false = 退化（180° 折返，横向无定义）。
	/// </summary>
	public static bool JoinBasis(Vector3 p, Vector3 prev, Vector3 next,
		out Vector3 n1, out Vector3 n2, out Vector3 side, out float miterScale)
	{
		n1 = n2 = side = Vector3.Zero;
		miterScale = 1f;
		if (!TangentAt(p, prev, p, out var tPrev)) return false;
		if (!TangentAt(p, p, next, out var tNext)) return false;
		n1 = p.Cross(tPrev).Normalized();
		n2 = p.Cross(tNext).Normalized();
		var m = n1 + n2;
		float denom = 1f + n1.Dot(n2);
		if (m.LengthSquared() < 1e-12f || denom < 1e-6f) return false;
		side = m.Normalized();
		miterScale = m.Length() / denom;
		return float.IsFinite(miterScale) && miterScale > 0f ? true : false;
	}

	/// <summary>p 处由 from→to 的大圆切向（先把差向量投影到 p 的切平面，再归一）。</summary>
	static bool TangentAt(Vector3 p, Vector3 from, Vector3 to, out Vector3 t)
	{
		t = Vector3.Zero;
		var v = to - from;
		v -= p * p.Dot(v);
		float len = v.Length();
		if (!(len > 1e-9f)) return false;
		t = v / len;
		return true;
	}

	/// <summary>p 处任取一组切平面正交基（单点河 / 退化回退时用）。</summary>
	static bool BasisAt(Vector3 p, out Vector3 u, out Vector3 v)
	{
		var axis = Math.Abs(p.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		var a = p.Cross(axis);
		if (!(a.LengthSquared() > 1e-12f)) { u = Vector3.Zero; v = Vector3.Zero; return false; }
		u = a.Normalized();
		v = p.Cross(u).Normalized();
		return true;
	}

	/// <summary>
	/// 河段的**球面横向**单位向量（ribbon 的加宽方向）；退化段（端点重合 / 反向 180°）返回 false。
	///
	/// ★数学语义（针对"cross(d1,d2) 到底是不是横向"的实测确认，见 RiverRibbonGeometryTests）：
	///   设 θ = d1→d2 的圆心角，t̂ = d1 处沿大圆的**单位切向**，则
	///     d2 = cosθ·d1 + sinθ·t̂
	///     cross(d1, d2) = sinθ · cross(d1, t̂)
	///   ⇒ normalize(cross(d1,d2)) = cross(d1, t̂) = cross(p, tangent)，
	///     与"径向 × 切向"写出来的横向**完全等价**，不是两个候选。
	///   它同时满足：① ⊥ d1（径向）⇒ **贴着球面**，不翘起来；② ⊥ t̂（流向）⇒ **垂直于河段**。
	///   直觉上 cross(d1,d2) 是"大圆平面的法向"，看似不是横向——但**大圆平面过球心**，
	///   故其法向必然垂直于径向 ⇒ 该法向本身就在球面切平面内。两个身份在此重合。
	///   （符号只决定"左/右"，ribbon 对称，无所谓。）
	/// </summary>
	public static bool SegmentSide(Vector3 d1, Vector3 d2, out Vector3 side)
	{
		side = Vector3.Zero;
		var o = d1.Cross(d2);
		// ⚠️ 判的是**长度**不是长度平方：旧写法 `LengthSquared < 1e-12` 等价于"圆心角 < 1e-6 rad 就跳过"，
		//    会把**合法短河段静默丢掉**（res 越高格间距越小）。float32 下圆心角 < ~1e-9 rad 的叉积
		//    已无可信有效位，故取 1e-9 作为"真退化"的下界（res4 邻格角距 ≈ 7.1e-3，安全余量 6 个量级）。
		float len = o.Length();
		if (len < 1e-9f || !float.IsFinite(len)) return false;
		side = o / len;
		return true;
	}

	// ── 颜色 ──────────────────────────────────────────────────────────────

	static Color SegColor(int kind, bool soft)
	{
		var c = IsMainStem(kind) ? TrunkColor : TributaryColor;
		return soft ? SoftTint(c) : c;
	}

	/// <summary>
	/// 软边色 = 水色向"**同亮度的淡灰**"混合（v1 是直接向**纯白**混 62% ⇒ 像 UI overlay）。
	/// 保持同色系、去饱和、轻微提亮，而不是变成白边。
	/// </summary>
	static Color SoftTint(Color water)
	{
		float lum = 0.2126f * water.R + 0.7152f * water.G + 0.0722f * water.B;
		float t = Math.Clamp(lum + (1f - lum) * SoftLift_0, 0f, 1f);
		return water.Lerp(new Color(t, t, t), SoftMix);
	}

	static bool IsMainStem(int kind) =>
		kind == (int)RiverGraph.RiverNodeKind.Trunk ||
		kind == (int)RiverGraph.RiverNodeKind.Confluence ||
		kind == (int)RiverGraph.RiverNodeKind.Outlet;

	static void AddSurface(ArrayMesh mesh, Mesh.PrimitiveType prim, List<Vector3> v, List<Color> c)
	{
		if (v.Count == 0) return;
		var arr = new Godot.Collections.Array();
		arr.Resize((int)Mesh.ArrayType.Max);
		arr[(int)Mesh.ArrayType.Vertex] = v.ToArray();
		arr[(int)Mesh.ArrayType.Color] = c.ToArray();
		mesh.AddSurfaceFromArrays(prim, arr);
	}

	static StandardMaterial3D _mat;
	static StandardMaterial3D SharedMaterial()
	{
		if (_mat != null) return _mat;
		_mat = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,   // 平光：河线是符号表达，不参与光照
			VertexColorUseAsAlbedo = true,
			AlbedoColor = Colors.White,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,         // ribbon 双面可见：绕序不敏感
		};
		return _mat;
	}
}
