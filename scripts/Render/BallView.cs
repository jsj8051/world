using Godot;
using System;
using System.Collections.Generic;
using World.H3Grid;
using World.Utils;
using World.Utils.H3;

namespace World.Render;

/// <summary>
/// 星球球面视图：单一半径球壳 ＋ 逐格颜色分档，海拔只经颜色表达，不做顶点位移。
/// 材质为内联 Unshaded 平光 shader（ALBEDO = 分档色）；LOD 远低近高 ＋ 背面整块剔除。
/// 只负责显示与剔除/LOD；拾取与高亮几何在 <see cref="CellQuery"/>（纯函数，可独立测）。
/// 格表来自 Ball，海拔来自外部逐格数组，取色策略由装配层注入——本类不内置任何具体模式。
/// </summary>
public sealed partial class BallView : Node3D   // partial 是 Godot 源生成器的要求（GD0001）
{
	readonly Ball _ball;
	float[] _elevation;      // 逐格海拔（米）；重烘读同一份引用，换实例走 SetElevationSource
	MapMode _mode;            // 取色函数；构造注入初值，之后由 SetMode 切换，恒非 null

	float _lodNearRatio;        // 近距 = 球半径 × 此值（LOD 判据）
	float _backfaceCullRatio;   // 保留接口；现版可见角限 = 90° + 块角半径 + 0.6 rad，不读此值

	// ── 分块（res0 基格，122 块；每块两档网格）──
	sealed class Chunk
	{
		public ulong Parent;         // res0 基格 id
		public Vector3 Dir;          // 基格中心单位方向（剔除判据用）
		public MeshInstance3D Hi;    // 高档网格（本档子格面）
		public MeshInstance3D Lo;    // 低档网格（res-1 子格面；最低档球为 null）
		public bool IsHi = true;     // 当前显示档
		public bool VisNow = true;   // 上一帧剔除结果（未变不写属性）

		/// <summary>
		/// 应用本帧的剔除与 LOD 结果；返回是否可见（供诊断计数）。
		/// 属性写入是热路径开销 ⇒ 状态未变不写；该判据与它保护的状态同处一室。
		/// 不做 early-return——否则"跳过写入"与"跳过调用"会再度混成一件事。
		/// </summary>
		public bool ApplyLod(bool visible, bool highDetail)
		{
			if (visible != VisNow || highDetail != IsHi)
			{
				Hi.Visible = visible && highDetail;
				if (Lo != null) Lo.Visible = visible && !highDetail;
				VisNow = visible;
				IsHi = highDetail;
			}
			return visible;
		}
	}

	readonly List<Chunk> _chunks = new();
	readonly Dictionary<(ulong, int), ArrayMesh> _geomCache = new();   // (父格, 子档) → 网格
	float _maxChunkAngular = 0.42f;   // res0 块角半径上限（rad；五边形块略大）
	float _cameraFovMargin = 0.6f;    // 透视余量（rad）：相机在 1.7R、FOV 75° 时能看到半球外约 35° 的侧面

	/// <summary>海拔源直供构造：任意逐格海拔数组（米）＋ 初始地图模式（取色策略）。
	/// 初始模式由装配层注入（世界生成侧传注册序[0]）；本类不内置默认模式——默认显示什么是策略决策，
	/// 焊进通用渲染件既绑定具体模式类，又会让坞高亮与实际画面不一致。</summary>
	public BallView(Ball ball, float[] elevationM, MapMode initialMode, float lodNearRatio, float backfaceCullRatio)
	{
		_ball = ball;
		_elevation = elevationM ?? throw new ArgumentNullException(nameof(elevationM));
		_mode = initialMode ?? throw new ArgumentNullException(nameof(initialMode));
		_lodNearRatio = lodNearRatio;
		_backfaceCullRatio = backfaceCullRatio;
	}

	/// <summary>视图持有的球（诊断工具与拾取需要）。</summary>
	public Ball Ball => _ball;

	int _visDiag;   // 诊断打印限次

	/// <summary>建 122 块 + 两档网格（首帧全量）。</summary>
	public void BuildChunks()
	{
		RebuildElevTex();    // 先有数据纹理：网格 UV 依赖 _texW/_texH
		int res = _ball.Res;
		foreach (ulong parent in Res0Cells())
		{
			var c = new Chunk { Parent = parent, Dir = CellQuery.CellDir(parent) };
			c.Hi = new MeshInstance3D { Name = $"c{parent:X}_hi", Mesh = MeshFor(parent, res) };
			AddChild(c.Hi);
			if (res >= 1)
			{
				c.Lo = new MeshInstance3D { Name = $"c{parent:X}_lo", Mesh = MeshFor(parent, res - 1) };
				c.Lo.Visible = false;
				AddChild(c.Lo);
			}
			_chunks.Add(c);
		}
	}

	// ── 每帧：剔除 + LOD（先判可见再定档；两档节点互斥显示）──
	public void UpdateVisibility(Camera3D camera)
	{
		if (camera == null || _chunks.Count == 0) return;
		SyncRingViewport(camera.GetViewport());   // 环带恒 px 依赖 u_viewport；尺寸变了才写
		Vector3 camPos = camera.GlobalPosition;
		bool near = camPos.Length() < _ball.Radius * _lodNearRatio;
		Vector3 camDir = camPos / Math.Max(camPos.Length(), 1e-6f);
		// 背面剔除：块中心方向与相机方向夹角 θ，可见条件 θ < 90° + 余量
		// （余量 = 块角半径 + 透视余量；ratio×0.42 的旧式在 72°<90° 时只画球顶帽，已废）。
		float margin = _maxChunkAngular + _cameraFovMargin;
		float cosLimit = MathF.Cos(MathF.PI / 2f + margin);

		int visCount = 0;
		foreach (var c in _chunks)
		{
			bool vis = c.Dir.Dot(camDir) > cosLimit;
			bool hi = near;                           // 远低近高：近距 = 高档
			if (c.ApplyLod(vis, hi)) visCount++;
		}
		if (_visDiag++ < 2)
			GD.Print($"[CULL-DIAG] chunks={_chunks.Count} visible={visCount} near={near} " +
				$"cosLimit={cosLimit:F3} camDist={camPos.Length():F2} R={_ball.Radius}");
	}

	/// <summary>海拔场变化后：清缓存重建两档网格（块结构不变）。</summary>
	public void Rebuild()
	{
		RebuildElevTex();    // 重烘数据纹理（UV 布局不变）
		_geomCache.Clear();
		int res = _ball.Res;
		foreach (var c in _chunks)
		{
			c.Hi.Mesh = MeshFor(c.Parent, res);
			if (c.Lo != null) c.Lo.Mesh = MeshFor(c.Parent, res - 1);
		}
	}

	/// <summary>海拔场变化后的轻量刷新：只重烘颜色纹理（颜色全在纹理里，几何与海拔无关）。</summary>
	public void RefreshColors() => RebuildElevTex();

	/// <summary>切换地图模式：换取色函数重烘颜色纹理（几何/UV 不动；O(n) CPU 循环，毫秒级）。</summary>
	public void SetMode(MapMode mode)
	{
		_mode = mode ?? throw new ArgumentNullException(nameof(mode));
		RefreshColors();
	}

	/// <summary>LOD 近距比例：距离 &lt; 球半径 × 此值即用高档。只改判据，下一帧生效。</summary>
	public void SetLodRatio(float v) => _lodNearRatio = v;

	/// <summary>背面剔除比例（保留接口；当前剔除不读此值，见 <c>UpdateVisibility</c> 的角限）。</summary>
	public void SetBackfaceRatio(float v) => _backfaceCullRatio = v;

	/// <summary>重绑海拔源（重算换了数组实例后调用；随后的重烘/查询读新数组）。</summary>
	public void SetElevationSource(float[] elevationM) =>
		_elevation = elevationM ?? throw new ArgumentNullException(nameof(elevationM));

	/// <summary>格 id → 全局下标（逻辑层数组对位查询）；格表外返回 −1。</summary>
	public int PickCellIndex(ulong cell) => CellIndex().TryGetValue(cell, out int i) ? i : -1;

	/// <summary>格 id → 海拔（米）；格表外的格返回 false。</summary>
	public bool TryGetElevation(ulong cell, out float elevM)
	{
		if (CellIndex().TryGetValue(cell, out int i))
		{
			elevM = _elevation[i];
			return true;
		}
		elevM = 0f;
		return false;
	}

	/// <summary>屏幕点 → 命中格 id；未命中返回 null。委托 <see cref="CellQuery.PickCell"/>。</summary>
	public ulong? PickCell(Vector2 screenPos, Camera3D camera) => CellQuery.PickCell(_ball, screenPos, camera);

	// ── 颜色数据纹理：全星一张 RGBA8（N = 本档格数，W = ceil(sqrt(N))，H = ceil(N/W)）──
	// 格 i 的纹素中心 UV = ((i%W)+0.5)/W, ((i/W)+0.5)/H；块网格 UV 直接取该点。
	// 纹理经 static 单例材质注入；RGB = 该格颜色（已转 linear），nearest ⇒ 格与格之间硬边。
	ImageTexture _elevTex;      // RGB = 逐格颜色（linear），A = 255
	int _texW, _texH;

	void RebuildElevTex()
	{
		_mode.BeginBake();                           // 自适应域模式在这里刷新 min-max
		int n = _ball.CellIds.Length;
		_texW = (int)Math.Ceiling(Math.Sqrt(n));
		_texH = (n + _texW - 1) / _texW;
		var bytes = new byte[_texW * _texH * 4];
		for (int i = 0; i < n; i++)
		{
			Color c = _mode.CellColorAt(i).SrgbToLinear();   // 模式给的是 sRGB 意图色 ⇒ 统一转 linear（防二次提亮发白）
			int o = i * 4;
			bytes[o] = (byte)Math.Round(c.R * 255f);
			bytes[o + 1] = (byte)Math.Round(c.G * 255f);
			bytes[o + 2] = (byte)Math.Round(c.B * 255f);
			bytes[o + 3] = 255;
		}
		_elevTex = ImageTexture.CreateFromImage(Image.CreateFromData(_texW, _texH, false, Image.Format.Rgba8, bytes));
		SharedMaterial().SetShaderParameter("elev_tex", _elevTex);   // 单例材质统一换纹理
	}

	// 格 i → 数据纹理纹素中心 UV（采样地址；nearest ⇒ 一格一色）
	Vector2 TexelUv(int i) => new(((i % _texW) + 0.5f) / _texW, ((i / _texW) + 0.5f) / _texH);

	// ── 网格构建：块 = 基格 p 在子档 childRes 的全部子格（CellToChildren 全展开）──
	ArrayMesh MeshFor(ulong parent, int childRes)
	{
		if (_geomCache.TryGetValue((parent, childRes), out var hit)) return hit;
		var mesh = BuildChunkMesh(parent, childRes);
		_geomCache[(parent, childRes)] = mesh;
		return mesh;
	}

	ArrayMesh BuildChunkMesh(ulong parent, int childRes)
	{
		ulong[] cells = H3.CellToChildren(parent, childRes);
		var cellIdx = CellIndex();
		float R = _ball.Radius;
		int n = cells.Length;

		int vCap = n * 7, iCap = n * 21;
		var verts = new List<Vector3>(vCap);
		var uvs = new List<Vector2>(vCap);     // UV = 本格纹素中心 = 颜色纹理采样地址（一格一色）
		var idx = new List<int>(iCap);

		foreach (ulong cell in cells)
		{
			// 格 → 纹素下标：命中本档格表就用全局下标；低档块（res-1）的子格不在表内 ⇒ 原生查询。
			// LatLngToCell(方向, 本档 res) 得到包含它的本档格——数据纹理只铺本档格表。
			int texel;
			Vector3 dir;
			if (cellIdx.TryGetValue(cell, out int ci)) { texel = ci; dir = _ball.CellDirs[ci]; }
			else
			{
				// 不能 CellToParent(cell, res)：res-1 → res 是细化方向，H3 只支持粗化 ⇒ 错误码 12。
				var ll = H3.CellToLatLng(cell);
				dir = CoordUtil.LatLngToSphere(ll, 1f);
				texel = cellIdx.TryGetValue(H3.LatLngToCell(ll, _ball.Res), out int ci2) ? ci2 : 0;
			}
			AddCellFan(verts, uvs, idx, cell, dir, TexelUv(texel), R);
		}

		var am = new ArrayMesh();
		var arr = new Godot.Collections.Array();
		arr.Resize((int)Mesh.ArrayType.Max);
		arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
		arr[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
		arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
		am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
		am.SurfaceSetMaterial(0, SharedMaterial());
		return am;
	}

	// 材质：Unshaded 平光 shader，static 单例（纹理经 uniform 注入）。
	static Shader _shader;
	static ShaderMaterial _mat;
	static ShaderMaterial SharedMaterial()
	{
		if (_mat != null) return _mat;
		if (_shader == null)
		{
			_shader = new Shader
			{
				Code = """
					shader_type spatial;
					render_mode unshaded, cull_disabled;   // 双面保险（背面经济性由块级剔除承担）
					uniform sampler2D elev_tex : filter_nearest, repeat_disable;
					void fragment() { ALBEDO = texture(elev_tex, UV).rgb; }   // 直采格色（已 linear）
					""",
			};
		}
		_mat = new ShaderMaterial { Shader = _shader };
		return _mat;
	}

	// 一格 = 格心 + m 角点 + m 个扇形三角（外向缠绕）；单半径 ⇒ 顶点 = 方向 × R。
	// 顶点 UV 全取本格纹素中心 ⇒ 一格一色，nearest 下色档边界即等高线。
	// 顶点必须排成环序（由 RingOrdered 保证）：CellToVertexes 的返回集不保证顺序，
	// 乱序扇面会交叉重叠，半透明高亮会缩成中心小多边形。
	void AddCellFan(List<Vector3> verts, List<Vector2> uvs, List<int> idx,
		ulong cell, Vector3 centerDir, Vector2 cellUv, float R)
	{
		int centerIdx = verts.Count;
		verts.Add(centerDir * R);
		uvs.Add(cellUv);

		var ringDirs = CellQuery.RingOrdered(cell, centerDir);
		int m = ringDirs.Length;

		var corner = new int[m];
		for (int k = 0; k < m; k++)
		{
			corner[k] = verts.Count;
			verts.Add(ringDirs[k] * R);
			uvs.Add(cellUv);
		}
		for (int k = 0; k < m; k++)            // 外向缠绕：心 → 角k → 角k+1
		{
			idx.Add(centerIdx);
			idx.Add(corner[k]);
			idx.Add(corner[(k + 1) % m]);
		}
	}

	// ── 工具 ──

	// cellId → 全局下标（一次构建复用）
	Dictionary<ulong, int> _idxCache;
	Dictionary<ulong, int> CellIndex()
	{
		if (_idxCache != null) return _idxCache;
		var d = new Dictionary<ulong, int>(_ball.CellIds.Length);
		for (int i = 0; i < _ball.CellIds.Length; i++) d[_ball.CellIds[i]] = i;
		return _idxCache = d;
	}

	// res0 基格全集：对本档全格取 CellToParent(cell, 0) 去重（28.8 万次整数查表，毫秒级）
	readonly HashSet<ulong> _res0 = new();
	bool _res0Built;
	IEnumerable<ulong> Res0Cells()
	{
		if (!_res0Built)
		{
			foreach (ulong id in _ball.CellIds)
			{
				ulong p = H3.CellToParent(id, 0);
				if (p != 0) _res0.Add(p);
			}
			_res0Built = true;
		}
		return _res0;
	}

	// ── 选中高亮：球面上贴地的闭合环带（几何生成在 CellQuery.AddCellRing，纯函数）──
	// 本节只管 MeshInstance3D 的生命周期与材质。
	MeshInstance3D _highlight;
	ShaderMaterial _ringMat;
	bool _ringTried;
	Vector2 _ringViewport = Vector2.Zero;

	/// <summary>选中格高亮：贴地闭合环带（只描边界，不填整格）；传 null 隐藏。</summary>
	public void HighlightCell(ulong? cell)
	{
		if (_highlight == null)
		{
			// 名字要稳定：诊断（CellHighlightDiag）与调试靠它在场景树里定位环带
			_highlight = new MeshInstance3D { Name = CellQuery.HighlightNodeName, Visible = false };
			AddChild(_highlight);
		}
		if (cell == null)
		{
			_highlight.Visible = false;
			return;
		}
		if (!EnsureRingMaterial()) return;   // shader 缺失 ⇒ 宁可不画，也不退回整格扇面

		ulong c = cell.Value;
		var dir = CellQuery.CellDir(c);

		var verts = new List<Vector3>(16);
		var norms = new List<Vector3>(16);
		var uvs = new List<Vector2>(16);
		var uv2 = new List<Vector2>(16);
		var cols = new List<Color>(16);
		var idx = new List<int>(24);
		CellQuery.AddCellRing(verts, norms, uvs, uv2, cols, idx, c, dir, _ball.Radius,
			CellQuery.HighlightWidthPx, CellQuery.HighlightColor, CellQuery.HighlightDepthBias);

		var am = new ArrayMesh();
		var arr = new Godot.Collections.Array();
		arr.Resize((int)Mesh.ArrayType.Max);
		arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
		arr[(int)Mesh.ArrayType.Normal] = norms.ToArray();
		arr[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
		arr[(int)Mesh.ArrayType.TexUV2] = uv2.ToArray();
		arr[(int)Mesh.ArrayType.Color] = cols.ToArray();
		arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
		am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
		_highlight.Mesh = am;
		_highlight.MaterialOverride = _ringMat;
		_highlight.Visible = true;
		SyncRingViewport(GetViewport());
	}

	bool EnsureRingMaterial()
	{
		if (_ringMat != null) return true;
		if (_ringTried) return false;
		_ringTried = true;
		var sh = GD.Load<Shader>(CellQuery.HighlightShaderPath);
		if (sh == null) return false;
		_ringMat = new ShaderMaterial { Shader = sh };
		_ringMat.SetShaderParameter("u_screen_space", 1.0f);   // 1 = 屏幕空间恒 px
		_ringMat.SetShaderParameter("u_core", CellQuery.HighlightCore);
		_ringMat.SetShaderParameter("u_edge", 1.0f);
		_ringMat.SetShaderParameter("u_edge_gain", 1.0f);      // 不取河流的"边缘提亮"：那是水色效果
		_ringMat.SetShaderParameter("u_viewport", new Vector2(1920f, 1080f));
		return true;
	}

	// 恒 px 依赖 u_viewport；尺寸变了只改 uniform，不重烘几何（窗口缩放/切档都要跟上）。
	void SyncRingViewport(Viewport vp)
	{
		if (_ringMat == null || vp == null) return;
		var size = vp.GetVisibleRect().Size;
		if (size == _ringViewport) return;
		_ringViewport = size;
		_ringMat.SetShaderParameter("u_viewport", size);
	}
}
