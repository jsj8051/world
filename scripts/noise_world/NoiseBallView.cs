using Godot;
using System;
using System.Collections.Generic;
using World.NewHexWorld;        // Ball（复用 new_HexWorld 球壳数据层）
using World.Utils;              // SphericalFbmNoise / CoordUtil
using World.Utils.H3;

namespace World.NoiseWorld;

// 噪声地形 · 视图层（P0 修订版，2026-09-29 用户拍板）：
//   ★① 球体 = **单一半径**，地形不做顶点位移——海拔全部用**颜色分档**表达（地图风）；
//   ★② 材质 = **Unshaded 平光**（shader 内联，ALBEDO = 分档色）——法线/位移/夸张整条线退役；
//   ③ LOD（远低近高）+ 背面整块剔除（看不到的不画）保留。
// 颜色数据通路（本项目实测定案）：全星一张 **RGBA8 颜色数据纹理**（每格 1 纹素，UV=纹素中心，
//   R/G/B = CPU 分档色已转 linear），fragment 直采输出——BallView sphere_region_material 同款
//   已验证模式（烘色进纹理 ⇒ 无 8 位海拔量化误差翻转色档问题）。
//   （顶点色 COLOR 通道、TEXUV 塞米级大数、片元 band() 现算三条路在 D3D12/Forward+ 下实测
//   不可用或已废，详见 git 历史。）
// 分层：本类只显示；海拔来自 NoiseTerrain（逻辑层，数据分辨率独立）；格表来自 Ball。
public sealed partial class NoiseBallView : Node3D   // partial = Godot 源生成器要求（GD0001）
{
	readonly Ball _ball;
	float[] _elevation;   // 逐格海拔（米）——海拔源抽象（NoiseTerrain 或 worldgen 投影层供给；重算可经 SetElevationSource 重绑）
	NoiseMapMode _mode;         // 当前地图模式（取色函数；null = 未设 → 首烘时回落海拔分档）

	float _lodNearRatio;        // 拍板①旋钮：近距 = 球半径 × 此值
	float _backfaceCullRatio;   // 拍板②旋钮（保留接口；现版可见角限 = 90°+块角半径+0.6rad）

	// ── 分块（res0 基格，122 块；每块两档网格 + 可见性状态）──
	sealed class Chunk
	{
		public ulong Parent;         // res0 基格 id
		public Vector3 Dir;          // 基格中心单位方向（剔除判据用）
		public MeshInstance3D Hi;    // 高档网格（本档子格面）
		public MeshInstance3D Lo;    // 低档网格（res-1 子格面；最低档球为 null）
		public bool IsHi = true;     // 当前显示档
		public bool VisNow = true;   // 上一帧剔除结果（未变不写属性）
	}

	readonly List<Chunk> _chunks = new();
	readonly Dictionary<(ulong, int), ArrayMesh> _geomCache = new();   // (父格, 子档) → 网格
	float _maxChunkAngular = 0.42f;   // res0 块角半径上限（rad；五边形块略大，P1 逐块实测替换）
	float _cameraFovMargin = 0.6f;    // 透视外扩余量（rad）：相机在 1.7R 处 FOV 75° 能看到半球外 ~35° 的侧面

	public NoiseBallView(Ball ball, NoiseTerrain terrain, float lodNearRatio, float backfaceCullRatio)
		: this(ball, terrain.ElevationM, lodNearRatio, backfaceCullRatio) { }

	/// <summary>海拔源直供构造：任何逐格海拔数组（worldgen 投影层等）；引用须稳定（重烘读同一份）。</summary>
	public NoiseBallView(Ball ball, float[] elevationM, float lodNearRatio, float backfaceCullRatio)
	{
		_ball = ball;
		_elevation = elevationM ?? throw new ArgumentNullException(nameof(elevationM));
		_lodNearRatio = lodNearRatio;
		_backfaceCullRatio = backfaceCullRatio;
	}

	int _visDiag;   // 诊断限次
	/// <summary>建 122 块 + 两档网格（首帧全量；惰性按需建 = P1）。</summary>
	public void BuildChunks()
	{
		RebuildElevTex();    // 先有数据纹理（UV 布局依赖 _texW/_texH）
		int res = _ball.Res;
		foreach (ulong parent in Res0Cells())
		{
			var c = new Chunk { Parent = parent, Dir = CellDir(parent) };
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
		Vector3 camPos = camera.GlobalPosition;
		bool near = camPos.Length() < _ball.Radius * _lodNearRatio;
		Vector3 camDir = camPos / Math.Max(camPos.Length(), 1e-6f);
		// 背面剔除：块中心方向 vs 相机方向夹角 θ 的可见条件 = θ < 90° + 余量
		// （余量 = 块角半径 + 透视外扩；旧式 ratio×0.42 在 72°<90° 只画球顶帽——已废）。
		float margin = _maxChunkAngular + _cameraFovMargin;
		float cosLimit = MathF.Cos(MathF.PI / 2f + margin);

		int visCount = 0;
		foreach (var c in _chunks)
		{
			bool vis = c.Dir.Dot(camDir) > cosLimit;   // 夹角 < 90°+余量 ⇒ 可见
			bool hi = near;                                     // 远低近高：近距 = 高档
			if (vis) visCount++;
			if (vis == c.VisNow && hi == c.IsHi) continue;      // 状态未变不写属性
			c.Hi.Visible = vis && hi;
			if (c.Lo != null) c.Lo.Visible = vis && !hi;
			c.VisNow = vis;
			c.IsHi = hi;
		}
		if (_visDiag++ < 2) GD.Print($"[CULL-DIAG] chunks={_chunks.Count} visible={visCount} near={near} cosLimit={cosLimit:F3} camDist={camPos.Length():F2} R={_ball.Radius}");
	}

	/// <summary>海拔场变化后：清缓存重建两档网格（块结构不变）。</summary>
	public void Rebuild()
	{
		RebuildElevTex();    // 海拔场变了：重烘数据纹理（UV 布局不变）
		_geomCache.Clear();
		int res = _ball.Res;
		foreach (var c in _chunks)
		{
			c.Hi.Mesh = MeshFor(c.Parent, res);
			if (c.Lo != null) c.Lo.Mesh = MeshFor(c.Parent, res - 1);
		}
	}

	/// <summary>海拔场变化后的轻量刷新：只重烘颜色纹理（颜色全在纹理里，几何/UV 与海拔无关）。</summary>
	public void RefreshColors() => RebuildElevTex();

	/// <summary>切换地图模式：换取色函数重烘颜色纹理（几何/UV 不动；O(n) CPU 循环，毫秒级）。</summary>
	public void SetMode(NoiseMapMode mode)
	{
		_mode = mode ?? throw new ArgumentNullException(nameof(mode));
		RefreshColors();
	}

	public void SetLodRatio(float v) => _lodNearRatio = v;            // 只改判据，下帧生效
	public void SetBackfaceRatio(float v) => _backfaceCullRatio = v;

	/// <summary>重绑海拔源（重算换了数组实例后调用；随后的重烘/查询读新数组）。</summary>
	public void SetElevationSource(float[] elevationM) =>
		_elevation = elevationM ?? throw new ArgumentNullException(nameof(elevationM));

	/// <summary>格 id → 全局下标（逻辑层数组对位查询）；格表外返回 −1。</summary>
	public int PickCellIndex(ulong cell) => CellIndex().TryGetValue(cell, out int i) ? i : -1;

	// ── 颜色数据纹理（全星一张，static 共享；ResLevel/噪声变化时 RebuildElevTex 重烘）──
	// 布局 = BallView 同款：格 i → 纹素 ((i%W)+0.5)/W, ((i/W)+0.5)/H（W=ceil(sqrt(N))）。
	// 块网格 UV = 该格纹素中心；fragment 直采 RGB = 分档色（已转 linear，nearest ⇒ 硬色阶）。
	ImageTexture _elevTex;      // RGBA8；RGB = 分档色（linear），A = 255
	int _texW, _texH;

	void RebuildElevTex()
	{
		_mode ??= new ElevationBandMode(_elevation);   // 默认模式 = 海拔分档（首烘/未显式设置时）
		_mode.BeginBake();                           // 自适应域模式在此刷新 min-max
		int n = _ball.CellIds.Length;
		_texW = (int)Math.Ceiling(Math.Sqrt(n));
		_texH = (n + _texW - 1) / _texW;
		var bytes = new byte[_texW * _texH * 4];
		for (int i = 0; i < n; i++)
		{
			Color c = _mode.CellColorAt(i).SrgbToLinear();   // 模式给 sRGB 意图色 ⇒ 统一转 linear（防二次提亮发白）
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

	// ── 网格构建：块 = 基格 p 在子档 childRes 的全部子格（CellToChildren 全展开；compact P1）──
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
			// 格 → 纹素下标：本档格表命中 = 全局下标；低档中间档子格不在表内 ⇒ 原生查询
			// LatLngToCell(方向, 本档) 命中包含它的本档格（数据纹理只铺本档格表）。
			int texel;
			Vector3 dir;
			if (cellIdx.TryGetValue(cell, out int ci)) { texel = ci; dir = _ball.CellDirs[ci]; }
			else
			{
				// ★不能 CellToParent(cell, res)：res-1 → res 是细化方向，H3 只支持粗化 ⇒ 错误码 12。
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
		am.SurfaceSetMaterial(0, SharedMaterial());   // ★static 单例材质（成功案例结构）
		return am;
	}

	// 材质：Unshaded 平光 shader（static 单例——红球 74% 成功案例的同构；纹理经 uniform 注入）。
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
					void fragment() { ALBEDO = texture(elev_tex, UV).rgb; }   // 直采格分档色（已 linear）
					""",
			};
		}
		_mat = new ShaderMaterial { Shader = _shader };
		return _mat;
	}

	// 一格 = 格心 + m 角点 + m 扇形三角（外向缠绕）。单半径：所有顶点 = 方向 × R。
	// 全部顶点 UV = 本格纹素中心 ⇒ 一格一色（nearest ⇒ 色档按格离散，边界即等高线）。
	// ★顶点必须排成环序：H3 CellToVertexes 返回集不保证顺序，乱序扇面会交叉重叠
	//   （不透明地形块被同色 overdraft 掩盖，半透明高亮则缩成中心小多边形——实测踩坑）。
	void AddCellFan(List<Vector3> verts, List<Vector2> uvs, List<int> idx,
		ulong cell, Vector3 centerDir, Vector2 cellUv, float R)
	{
		int centerIdx = verts.Count;
		verts.Add(centerDir * R);
		uvs.Add(cellUv);

		// 环序化：格心切平面（t1,t2 与 centerDir 构成右手系）上按方位角升序 = 外视 CCW。
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

		var corner = new int[m];
		for (int k = 0; k < m; k++)
		{
			corner[k] = verts.Count;
			verts.Add(ring[k].dir * R);
			uvs.Add(cellUv);
		}
		for (int k = 0; k < m; k++)            // 外向缠绕：心 → 角k → 角k+1
		{
			idx.Add(centerIdx);
			idx.Add(corner[k]);
			idx.Add(corner[(k + 1) % m]);
		}
	}

	// 分档色与档位名已迁 NoiseMapModes.ElevationBandMode（画面/信息面板同一色带源）；
	// 本类只留 BandName 委托（NoiseCellPanel 现有调用口不动）。
	public static string BandName(float m) => ElevationBandMode.BandName(m);

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

	// 基格中心方向（剔除用；绕开 cellIdx——基格必不在本档格表）
	Vector3 CellDir(ulong cell)
	{
		var ll = H3.CellToLatLng(cell);
		return CoordUtil.LatLngToSphere(ll, 1f);
	}

	// ── 格子 UI：拾取 / 高亮 / 信息查询 ──

	/// <summary>屏幕点 → 命中格 id；未命中（射线不交球/球在身后）返回 null。
	/// 数学同 BallView.PickCell（本视图无自身变换，直接世界系求交）。</summary>
	public ulong? PickCell(Vector2 screenPos, Camera3D camera)
	{
		Vector3 o = camera.ProjectRayOrigin(screenPos);
		Vector3 d = camera.ProjectRayNormal(screenPos);
		float b = o.Dot(d);
		float c = o.LengthSquared() - _ball.Radius * _ball.Radius;
		float disc = b * b - c;
		if (disc < 0f) return null;
		float t = -b - MathF.Sqrt(disc);          // 最近交点（正面）
		if (t < 0f) return null;                  // 球在相机背后
		Vector3 dir = (o + d * t) / _ball.Radius; // 单位球方向
		double lat = Math.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
		double lng = Math.Atan2(dir.Z, dir.X);
		return H3.LatLngToCell(new LatLng(lat, lng), _ball.Res);
	}

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

	MeshInstance3D _highlight;

	/// <summary>选中格高亮：单格扇面微抬升的半透明白片；传 null 隐藏。</summary>
	public void HighlightCell(ulong? cell)
	{
		if (_highlight == null)
		{
			var mat = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				AlbedoColor = new Color(1f, 1f, 1f, 0.55f),
			};
			_highlight = new MeshInstance3D { MaterialOverride = mat, Visible = false };
			AddChild(_highlight);
		}
		if (cell == null)
		{
			_highlight.Visible = false;
			return;
		}

		ulong c = cell.Value;
		var ll = H3.CellToLatLng(c);
		var dir = CoordUtil.LatLngToSphere(ll, 1f);
		var verts = new List<Vector3>(8);
		var uvs = new List<Vector2>(8);
		var idx = new List<int>(14);
		// 抬升 1.0005：够防 z-fighting，又不会在斜视角出现"悬浮片"的脱离感
		AddCellFan(verts, uvs, idx, c, dir, Vector2.Zero, _ball.Radius * 1.0005f);
		var am = new ArrayMesh();
		var arr = new Godot.Collections.Array();
		arr.Resize((int)Mesh.ArrayType.Max);
		arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
		arr[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
		arr[(int)Mesh.ArrayType.Index] = idx.ToArray();
		am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
		_highlight.Mesh = am;
		_highlight.Visible = true;
	}
}
