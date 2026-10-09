using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using World.Services;                    // UserPaths（写盘不落 C 盘纪律：统一经 UserPaths）
using World.WorldGen;         // WorldGenPlanet / RiverLineOverlay / RiverGeometry / RiverPresentationSpline

namespace World.Diagnostics;

// River v2.0/v1.2 · 河流**局部连续性**实测诊断（2026-10-04；窗口模式、像素差分、自动退出）。
//
// ★本轮定位（用户拍板）：**只产出诊断，不改正式参数**——不动 spline 参数、不动 shader、
//   不动宽度档位、不动颜色。目的是先把"局部河流表现是否连续"变成可量化的数，再决定要不要改。
//
// ── --aim：先把相机对准"该看的地方"─────────────────────────────────────
//   longest     最长河线中点（**既有基线回归**：总墨水/投影河长 ⇒ 应与历史记录一致）
//   confluence  最大汇流点（支流 → junction → 干流 三段分解）
//   outlet      最大出口（断头 / 异常端头）
//   bend        转角最大的锚点（曲线连续性 + 宽度稳定性）
//
// ── 汇流（confluence）三段分解───────────────────────────────────────────
//   W_tributary / W_junction / W_trunk：支流段 / 汇流点 / 干流段的实测笔画宽（px）
//   gap_px            = 理想走廊内"没被覆盖"的像素面积 ÷ 标称宽 ⇒ **硬要求 = 0**
//   width_jump_ratio  = 相邻两段的最大跳变倍率（支流1.3/汇1.4/干1.9 ⇒ 1.36 OK；
//                                             支流1.3/汇0.3/干1.9 ⇒ 6.33 不行）
//   overlap_px        支流带与干流带的重叠长度（几何量，用于定位哪些像素该被排序判据检验）
//   depthOrderingMismatch 重叠区里"支流(洋红)占主导"的像素比例（**bias 分层是否真的生效**）
//
// ★★命名与语义（2026-10-05 用户拍板，**Mismatch ≠ 渲染失败**，勿改回 Error）：
//   这个名字不能叫 `depthOrderingError`——单看 "33% 深度排序错误" 会让维护者误以为
//   v2 汇流存在系统性 depth inversion。它实际测的是**主导归属**，不是二值反转：
//
//     本工具      = 在"两条带都实心覆盖(α>0.5)"的重叠区内，支流色占主导的像素比例
//     failure    = **严格定义为**"本应前置的 surface 被后置 surface 覆盖"的像素比例
//                  （本工具不直接测这个——交叉区里两层本来就 alpha 混合，
//                   边界像素的归属是连续过渡，不是二值事件）
//
//   ⇒ Mismatch 高 ≠ 拓扑/渲染失败。它包含：① 真 depth inversion；② 预期的像素归属混合
//     （两条带半透明边界叠在一起时，谁"占主导"本就是程度问题）。
//   ⇒ **唯一可靠的判别方式是灵敏度对照**：`--bias=0.0001` 把两层 bias 拉平 ⇒
//     若指标从 X% 跳到 ~100%，说明它确实在量 bias 分层机制（实测：0–33% → 100%）。
//     若拉平后不跳，说明指标与排序无关，读数无意义。
//
// ── 探针配色（双色才能测排序）───────────────────────────────────────────
//   支流面 = 洋红 (1,0,1)；干流面 = 黄 (1,1,0)。二者 **R 通道恒为 1** ⇒ α 由 R 通道恢复：
//     α = (fg.R − bg.R) / (1 − bg.R)
//   归属由 G−B 判：s = (fg.G−fg.B) − (1−α)(bg.G−bg.B) ⇒ s>0 黄（干流）/ s<0 洋红（支流）。
//
// 用法：
//   Godot --path . res://scenes/diag/RiverShotDiag.tscn -- --aim=confluence
//   Godot --path . res://scenes/diag/RiverShotDiag.tscn -- --aim=bend --res=3
//   Godot --path . res://scenes/diag/RiverShotDiag.tscn -- --aim=longest --probe=1 --out=maps/river_v2_r4.png
public partial class RiverShotDiag : Node
{
	const float R = 2.0f;                 // worldgen 单位球尺度（OrbitalCamera 同款 _planetRadius=2）
	const int Warmup = 16;
	const int Block = 10;                 // 每次测量的帧块（摆位×2 + 空场噪声检验×2 + 采样）
	const int ExitFrame = 132;

	static readonly float[] Fracs = { 0f, 0.5f, 0.9f };   // 目标屏幕半径 = frac × ScreenFrac × min(W,H)/2
	const float ScreenFrac = 0.75f;
	static readonly float[] TiersLongest = { 1.7f, 1.15f, 1.05f };   // 既有基线档（回归可比）
	static readonly float[] TiersLocal = { 1.25f, 1.08f, 1.03f };    // 局部特写档（汇流/出口/弯道）

	// 探针配色（R 通道恒 1 ⇒ 单通道即可恢复 α；G−B 判归属）
	static readonly Color ProbeBranch = new(1f, 0f, 1f);   // 洋红
	static readonly Color ProbeMain = new(1f, 1f, 0f);     // 黄

	string _out = UserPaths.Resolve("maps/river_v2.png");
	Node3D _world;
	Camera3D _cam;
	Node _orbital;
	bool _camFound;

	RiverLineOverlay _rivers;
	WorldGenPlanet _planet;
	bool _probeMode = true;
	bool _fallback;
	bool _blendLinear;       // ★默认 false：实测渲染目标的混合发生在**编码空间**（见下方 A/B 说明）
	float _forcedWidth;      // 标定量：>0 时把所有顶点 UV2.x 强制成同一个 px 宽度
	float _forcedBias = -1;  // 标定量：>=0 时把所有顶点 UV2.y 强制成同一个 NDC 深度偏移

	string _aimName = "longest";
	int _pick;
	float[] _tiers = TiersLongest;
	Vector3 _aimDir = Vector3.Up;
	Vector3 _e1;

	readonly Dictionary<int, Vector3> _dirOf = new();   // 河流格 → 单位方向（从 RiverGeometry 反查，无需 Ball）
	List<PathD> _all;                                   // 全部 chain 的表现样条（= 渲染网格）
	Target _target;
	int _conflicts;

	int _frame;
	Image _bgImage;
	int _stop;
	float _nominalPx;        // 标称笔画宽（px）
	double _soft;            // 横截面柔化系数 = (1+u_core)/2 ⇒ 墨水宽 = 标称 × soft
	double _core = 0.62f;    // |cross| < core ⇒ 实心（横截面期望剖面用）
	double _expectInk;       // 期望墨水宽（px）= 标称 × soft

	// ── 目标模型 ────────────────────────────────────────────────────────
	sealed class PathD
	{
		public string Role = "";
		public bool Main;
		public int[] Cells = Array.Empty<int>();
		public Vector3[] Anchors = Array.Empty<Vector3>();  // H3 锚点（单位方向）
		public Vector3[] Pts = Array.Empty<Vector3>();      // 表现样条采样（单位方向）
		public int[] SegOf = Array.Empty<int>();
		public int Mark;                                    // 关注点在 Pts 中的下标
		public int MarkAnchor = -1;                         // 关注点对应的锚点下标（bend 用）
		public int[] AnchorSample = Array.Empty<int>();     // 锚点 k → 采样下标
	}

	sealed class Target
	{
		public string Aim = "";
		public string Desc = "";
		public int Cell = -1;
		public List<PathD> Paths = new();
	}

	public override void _Ready()
	{
		var args = DiagSceneBase.ParseUserArgs();
		if (args.TryGetValue("out", out var o)) _out = UserPaths.Resolve(o);
		_fallback = args.TryGetValue("fallback", out var f) && f != "0" && f != "false";
		if (args.TryGetValue("probe", out var p)) _probeMode = p != "0" && p != "false";
		// ★A/B 实测（2026-10-04，res3 longest）：`--blend=lin`（先把字节反编码到线性再解 α）
		//   会让"相对期望剖面缺失"从 0.01~0.12px 抬到 0.08~1.68px ⇒ α 被系统性**低估**
		//   ⇒ 说明本渲染目标的 alpha 混合是在 **sRGB 编码值**上做的（D3D12/Vulkan 对
		//      UNORM_SRGB 目标的常见行为），编码空间里直接线性组合才是正确模型。
		//   故默认走编码空间；`--blend=lin` 仅保留作对照。
		_blendLinear = args.TryGetValue("blend", out var bl) && bl == "lin";
		if (args.TryGetValue("width", out var w) && float.TryParse(w, out float fw)) _forcedWidth = fw;
		if (args.TryGetValue("bias", out var bs) && float.TryParse(bs, out float fb)) _forcedBias = fb;
		if (args.TryGetValue("aim", out var am)) _aimName = am.ToLowerInvariant();
		if (args.TryGetValue("pick", out var pk) && int.TryParse(pk, out int pi)) _pick = pi;

		var packed = GD.Load<PackedScene>("res://scenes/core/WorldGenWorld.tscn");
		_world = (Node3D)packed.Instantiate();
		_planet = _world.GetNode<WorldGenPlanet>("WorldGenPlanet");
		if (args.TryGetValue("res", out var rs) && int.TryParse(rs, out int res))
			_planet.ResLevel = res;
		AddChild(_world);

		_rivers = FindRivers(_world);
		if (_rivers != null)
		{
			_rivers.UseScreenSpace = !_fallback;
			GD.Print($"[RIVER-DIAG] 已找到 RiverLineOverlay；UseScreenSpace={_rivers.UseScreenSpace} res={_planet.ResLevel}");
		}
		else
		{
			GD.Print("[RIVER-DIAG] **未找到 RiverLineOverlay**（主场景结构变了？）");
		}

		// 标称宽 / 柔化系数（与 CellHighlightDiag 同口径：平均 α = (1+u_core)/2）
		float core = 0.62f;
		if (_rivers?.MaterialOverride is ShaderMaterial sm0 &&
			sm0.GetShaderParameter("u_core").VariantType != Variant.Type.Nil)
			core = sm0.GetShaderParameter("u_core").As<float>();
		_nominalPx = _forcedWidth > 0 ? _forcedWidth : RiverSymbolWidth.UniformWidthPx;
		_core = core;
		_soft = (1.0 + core) / 2.0;
		_expectInk = _nominalPx * _soft;

		_tiers = _aimName == "longest" ? TiersLongest : TiersLocal;
		BuildTarget();

		var vp = GetViewport().GetVisibleRect().Size;
		GD.Print($"[RIVER-DIAG] aim={_aimName} 目标={_target.Desc} 视口={vp.X}×{vp.Y} " +
				 $"标称宽={_nominalPx:F2}px u_core={core:F2} 期望墨水={_expectInk:F2}px " +
				 $"档位=[{string.Join(",", _tiers)}] 屏位=[{string.Join(",", Fracs)}]");
	}

	// ── 目标选取（全部由既有事实层派生，不加任何新字段）───────────────────
	void BuildTarget()
	{
		var geo = _planet?.Sim.RiverLines;
		var graph = _planet?.Sim.RiverTopology;
		_target = new Target { Aim = _aimName, Desc = "（无河网）" };
		if (geo == null || graph == null || geo.LineCells.Count == 0) { _aimDir = Vector3.Up; return; }

		// 河流格 → 单位方向（RiverGeometry 自带，等价于 ball.CellDirs[cell]）
		for (int k = 0; k < geo.LineCells.Count; k++)
		{
			var cells = geo.LineCells[k];
			var pts = geo.Lines[k];
			for (int j = 0; j < cells.Length && j < pts.Length; j++)
				_dirOf[cells[j]] = pts[j];
		}

		var raw = RiverLineOverlay.BuildChains(geo, graph, out _conflicts);
		_all = new List<PathD>(raw.Count);
		foreach (var cells in raw) _all.Add(MakePath(graph, cells, ""));

		switch (_aimName)
		{
			case "confluence": BuildConfluence(graph); break;
			case "outlet": BuildOutlet(graph); break;
			case "bend": BuildBend(); break;
			default: BuildLongest(); break;
		}

		_e1 = _aimDir.Cross(Vector3.Up);
		if (_e1.LengthSquared() < 0.25f) _e1 = _aimDir.Cross(Vector3.Right);
		_e1 = _e1.Normalized();
	}

	PathD MakePath(RiverGraph graph, int[] cells, string role)
	{
		var p = new PathD { Role = role, Cells = cells };
		p.Anchors = new Vector3[cells.Length];
		for (int i = 0; i < cells.Length; i++)
			p.Anchors[i] = _dirOf.TryGetValue(cells[i], out var d) ? d : Vector3.Up;
		bool main = false;
		for (int i = 0; i < cells.Length; i++)
		{
			int k = cells[i] < graph.NodeKind.Length ? graph.NodeKind[cells[i]] : -1;
			if (k == (int)RiverGraph.RiverNodeKind.Trunk || k == (int)RiverGraph.RiverNodeKind.Outlet)
				main = true;
		}
		p.Main = main;
		RiverPresentationSpline.Sample(p.Anchors, out var pts, out var segOf);
		p.Pts = pts;
		p.SegOf = segOf;

		// 锚点 k → 采样下标（段 j 的 t=1 采样点即 anchors[j+1]）
		if (segOf.Length > 0)
		{
			p.AnchorSample = new int[p.Anchors.Length];
			p.AnchorSample[0] = 0;
			for (int i = 0, k = 1; i < segOf.Length && k < p.Anchors.Length; i++)
				if (i + 1 >= segOf.Length || segOf[i + 1] != segOf[i]) p.AnchorSample[k++] = i;
			for (int k = 0; k < p.Anchors.Length; k++)
				p.AnchorSample[k] = Math.Min(p.AnchorSample[k], segOf.Length - 1);
		}
		return p;
	}

	void BuildConfluence(RiverGraph graph)
	{
		var cands = new List<int>();
		for (int c = 0; c < graph.NodeKind.Length; c++)
			if (graph.NodeKind[c] == (int)RiverGraph.RiverNodeKind.Confluence && graph.UpstreamCount[c] >= 2)
				cands.Add(c);
		if (cands.Count == 0) { BuildLongest(); _target.Desc = "（无汇流点，退回 longest）"; return; }
		cands.Sort((a, b) => graph.AccumMm[b].CompareTo(graph.AccumMm[a]));
		int j = cands[Math.Min(_pick, cands.Count - 1)];

		var trunk = _all.FirstOrDefault(p => p.Cells.Length > 0 && p.Cells[0] == j);
		var tribs = _all.Where(p => p.Cells.Length > 0 && p.Cells[^1] == j && p != trunk)
						.OrderByDescending(p => p.Cells.Length).ToList();
		if (trunk == null || tribs.Count == 0) { BuildLongest(); _target.Desc = "（汇流点无 chain，退回 longest）"; return; }

		trunk.Role = "trunk";
		tribs[0].Role = "tributary";
		trunk.Mark = 0;
		tribs[0].Mark = tribs[0].Pts.Length - 1;
		_target.Cell = j;
		_target.Paths.Add(tribs[0]);
		_target.Paths.Add(trunk);
		_aimDir = _dirOf[j];
		_target.Desc = $"汇流格={j} 累积={graph.AccumMm[j]:F0}mm 上游={graph.UpstreamCount[j]} " +
					   $"支流长={tribs[0].Cells.Length} 干流长={trunk.Cells.Length}";
	}

	void BuildOutlet(RiverGraph graph)
	{
		var cands = new List<int>();
		for (int c = 0; c < graph.NodeKind.Length; c++)
			if (graph.NodeKind[c] == (int)RiverGraph.RiverNodeKind.Outlet && graph.UpstreamCount[c] >= 1)
				cands.Add(c);
		if (cands.Count == 0) { BuildLongest(); _target.Desc = "（无出口，退回 longest）"; return; }
		cands.Sort((a, b) => graph.AccumMm[b].CompareTo(graph.AccumMm[a]));
		int o = cands[Math.Min(_pick, cands.Count - 1)];

		var tails = _all.Where(p => p.Cells.Length > 0 && p.Cells[^1] == o)
						.OrderByDescending(p => p.Cells.Length).ToList();
		if (tails.Count == 0) { BuildLongest(); _target.Desc = "（出口无 chain，退回 longest）"; return; }
		tails[0].Role = "tail";
		tails[0].Mark = tails[0].Pts.Length - 1;
		_target.Cell = o;
		_target.Paths.Add(tails[0]);
		_aimDir = _dirOf[o];
		_target.Desc = $"出口格={o} 累积={graph.AccumMm[o]:F0}mm 河线长={tails[0].Cells.Length}";
	}

	void BuildBend()
	{
		double best = 0.3;                 // < 0.3 rad（17°）不算弯
		PathD bp = null; int bk = -1;
		foreach (var p in _all)
		{
			if (p.Anchors.Length < 4) continue;
			for (int k = 1; k + 1 < p.Anchors.Length; k++)
			{
				float t = RiverPresentationSpline.TurnAt(p.Anchors, k);
				if (t > best) { best = t; bp = p; bk = k; }
			}
		}
		if (bp == null) { BuildLongest(); _target.Desc = "（无明显弯道，退回 longest）"; return; }
		bp.Role = "bend";
		bp.Mark = bp.AnchorSample[bk];
		bp.MarkAnchor = bk;
		_target.Cell = bp.Cells[bk];
		_target.Paths.Add(bp);
		_aimDir = bp.Anchors[bk];
		_target.Desc = $"弯道锚点={bk}/{bp.Anchors.Length - 1} 原生转角={best * 180.0 / Math.PI:F1}° " +
					   $"格={_target.Cell}";
	}

	void BuildLongest()
	{
		var best = _all?.OrderByDescending(p => p.Pts.Length).FirstOrDefault();
		if (best == null || best.Pts.Length == 0) { _aimDir = Vector3.Up; _target.Desc = "（无河线）"; return; }
		best.Role = "longest";
		best.Mark = best.Pts.Length / 2;
		_target.Cell = best.Cells[Math.Min(best.Mark, best.Cells.Length - 1)];
		_target.Paths.Add(best);
		_aimDir = best.Pts[best.Mark];
		_target.Desc = $"最长河线 采样={best.Pts.Length} 锚点={best.Anchors.Length} 中点格={_target.Cell}";
	}

	static RiverLineOverlay FindRivers(Node root)
	{
		if (root is RiverLineOverlay r) return r;
		foreach (var c in root.GetChildren())
		{
			var hit = FindRivers(c);
			if (hit != null) return hit;
		}
		return null;
	}

	// ── 帧循环 ──────────────────────────────────────────────────────────
	public override void _Process(double delta)
	{
		_frame++;
		if (!_camFound && _frame <= 10)
		{
			_camFound = TryFindCamera();
			if (!_camFound) return;
		}

		int total = _tiers.Length * Fracs.Length;
		if (_frame < Warmup) return;
		int rel = _frame - Warmup;
		int mi = rel / Block, b = rel % Block;
		if (mi >= total) { GetTree().Quit(); return; }
		int ti = mi / Fracs.Length, fi = mi % Fracs.Length;

		if (b == 0)
		{
			if (_probeMode) MakeProbeMesh();
			MoveTo(_aimDir, _tiers[ti]);        // 先摆到本档视距并居中
			if (_rivers != null) _rivers.Visible = true;
		}
		else if (b == 1)
		{
			// 相机已在本档视距 ⇒ 此刻才能按**屏幕半径**二分定位（视锥比地平线窄得多）
			MoveTo(AimForScreenRadius(mi), _tiers[ti]);
		}
		else if (b == 2)
		{
			if (_rivers != null) _rivers.Visible = false;          // 空场 A
		}
		else if (b == 4) _bgImage = Snap();
		else if (b == 5)
		{
			var bgB = Snap();
			double noise = Ink(bgB, _bgImage);                      // 噪声底：连续两帧都无河 ⇒ 应 ≈0
			if (noise > 1.0) GD.Print($"  [RIVER-DIAG] ⚠️ 噪声底={noise:F2}px²（场景不确定，测量可疑）");
			_bgImage = bgB;
		}
		else if (b == 7)
		{
			if (_rivers != null) _rivers.Visible = true;
		}
		else if (b == 9)
		{
			var fg = Snap();
			if (fi == 0) Shot(fg, InsertSuffix(_out, Suffix(ti)), Tag(mi));
			if (_probeMode) Analyze(fg, _bgImage, mi);
			DumpState();
		}
	}

	static string Suffix(int ti) => ti switch { 0 => "", 1 => "_regional", _ => "_near" };
	string Tag(int mi) => $"{_tiers[mi / Fracs.Length]:F2}R·屏{Fracs[mi % Fracs.Length]:F2}";

	Vector3 DirAt(float a) => (_aimDir * MathF.Cos(a) + _e1 * MathF.Sin(a)).Normalized();

	/// <summary>
	/// ★近距离下**视锥**远比地平线窄 ⇒ 不能按可见球冠角定位，必须二分到**目标屏幕半径**。
	///   这样"正中 / 中段 / 边缘"三档才真正可比，也能检出"中心正常、边缘反向"的深度精度问题。
	/// </summary>
	Vector3 AimForScreenRadius(int mi)
	{
		float d = _tiers[mi / Fracs.Length];
		var vp = GetViewport().GetVisibleRect().Size;
		float target = Fracs[mi % Fracs.Length] * ScreenFrac * MathF.Min(vp.X, vp.Y) * 0.5f;
		var center = vp * 0.5f;
		if (target < 1f) return _aimDir;

		float lo = 0f, hi = MathF.Acos(MathF.Min(1f, 1f / d)) * 0.999f;   // 上界 = 地平线
		for (int it = 0; it < 28; it++)
		{
			float mid = (lo + hi) * 0.5f;
			var p = DirAt(mid) * R;
			if (!_cam.IsPositionInFrustum(p)) { hi = mid; continue; }
			if (_cam.UnprojectPosition(p).DistanceTo(center) < target) lo = mid; else hi = mid;
		}
		return DirAt((lo + hi) * 0.5f);
	}

	bool TryFindCamera()
	{
		var orbital = _world.GetNodeOrNull("OrbitalCamera");
		if (orbital == null) return false;
		foreach (var child in orbital.GetChildren())
			if (child is Camera3D c) { _cam = c; _orbital = orbital; return true; }
		return false;
	}

	// 直接设定 _distance 和 _targetDistance ⇒ 无 lerp，两帧对齐。
	void MoveTo(Vector3 dir, float distR)
	{
		if (_cam == null || _orbital == null) return;
		float dist = R * distR;
		_orbital.Set("_targetDistance", dist);
		_orbital.Set("_distance", dist);
		_orbital.Set("_theta", Mathf.Atan2(dir.Z, dir.X));
		_orbital.Set("_phi", Mathf.Clamp(Mathf.Acos(Mathf.Clamp(dir.Y, -1f, 1f)), 0.05f, Mathf.Pi - 0.05f));
	}

	Image Snap() => GetViewport().GetTexture().GetImage();

	void Shot(Image img, string path, string tag)
	{
		string full = UserPaths.Resolve(path);
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
		img.SavePng(full);
		GD.Print($"RiverShotDiag [{tag}] → {full} ({img.GetWidth()}×{img.GetHeight()})");
	}

	void DumpState()
	{
		if (_rivers == null) return;
		int verts = 0, surf = 0;
		var mesh = _rivers.Mesh as ArrayMesh;
		if (mesh != null)
		{
			surf = mesh.GetSurfaceCount();
			for (int i = 0; i < surf; i++)
				verts += mesh.SurfaceGetArrays(i)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
		}
		float zoom = RiverLineOverlay.ZoomOf(_cam, 2.0f, Vector3.Zero);
		float uScreen = _rivers.MaterialOverride is ShaderMaterial sm
			? sm.GetShaderParameter("u_screen_space").As<float>() : -1f;
		float firstWidth = -1f;
		if (mesh != null && mesh.GetSurfaceCount() > 0)
		{
			var uv2 = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
			if (uv2.Length > 0) firstWidth = uv2[0].X;
		}
		GD.Print($"[RIVER-DIAG] shader={(_rivers.ShaderActive ? 1 : 0)} " +
				 $"conflicts={_rivers.TopologyConflicts} surfaces={surf} verts={verts} " +
				 $"visible={(_rivers.Visible ? 1 : 0)} zoom={zoom:F3} " +
				 $"comp={RiverSymbolWidth.ZoomCompensation(zoom):F3} " +
				 $"u_screen_space={uScreen} firstUV2x={firstWidth:F2}");
	}

	// ── 探针网格：按面染色（支流=洋红 / 干流=黄）────────────────────────
	// ⚠️ 不能按"surface 0 = 支流"硬编码：`AddIndexedSurface` 在空缓冲时会跳过 ⇒
	//    没有支流链时 surface 0 其实是干流面。改按 **UV2.y（bias）** 识别，bias 是唯一标签。
	void MakeProbeMesh()
	{
		if (_rivers?.Mesh is not ArrayMesh src) return;
		var dst = new ArrayMesh();
		for (int i = 0; i < src.GetSurfaceCount(); i++)
		{
			var arr = src.SurfaceGetArrays(i);
			var na = new Godot.Collections.Array();
			na.Resize((int)Mesh.ArrayType.Max);

			void Copy(Mesh.ArrayType t)
			{
				var v = arr[(int)t];
				if (v.VariantType != Variant.Type.Nil) na[(int)t] = v;
			}

			Copy(Mesh.ArrayType.Vertex);
			Copy(Mesh.ArrayType.Normal);
			Copy(Mesh.ArrayType.TexUV);
			Copy(Mesh.ArrayType.Index);

			var uv2 = arr[(int)Mesh.ArrayType.TexUV2].VariantType != Variant.Type.Nil
				? arr[(int)Mesh.ArrayType.TexUV2].AsVector2Array() : null;
			bool isMain = false;
			if (uv2 != null && uv2.Length > 0)
			{
				float bias = uv2[0].Y;                      // 1e-4 = 支流 / 2e-4 = 干流 / 3e-4 = 河口点
				isMain = bias > 1.5e-4f && bias < 2.5e-4f;
				if (arr[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil)
					isMain = false;                          // 河口点面（Points）按支流色处理
				if (_forcedWidth > 0 || _forcedBias >= 0)
				{
					var u2 = (Vector2[])uv2.Clone();
					for (int k = 0; k < u2.Length; k++)
						u2[k] = new Vector2(_forcedWidth > 0 ? _forcedWidth : u2[k].X,
											_forcedBias >= 0 ? _forcedBias : u2[k].Y);
					uv2 = u2;
				}
				na[(int)Mesh.ArrayType.TexUV2] = uv2;
			}

			if (arr[(int)Mesh.ArrayType.Color].VariantType != Variant.Type.Nil)
			{
				var col = arr[(int)Mesh.ArrayType.Color].AsColorArray();
				var c = isMain ? ProbeMain : ProbeBranch;
				for (int k = 0; k < col.Length; k++) col[k] = c;   // 宽度走 UV2.x，不走 COLOR.a
				na[(int)Mesh.ArrayType.Color] = col;
			}
			dst.AddSurfaceFromArrays(src.SurfaceGetPrimitiveType(i), na);
		}
		_rivers.Mesh = dst;
	}

	// ── 像素差分 ────────────────────────────────────────────────────────
	// ★两个探针色 R 通道都是 1 ⇒ α 单通道可解：fg.R = α + (1−α)bg.R。
	//   ⚠️ 与旧版"洋红两通道"公式不同（旧式在偏红地形上分母趋零会把 α 放大到几十）。
	//   ⚠️★GetImage() 读回的是 **sRGB 编码**字节，但本渲染目标的 alpha 混合**也在编码空间**完成
	//      （A/B 实测：`--blend=lin` 先反编码再解 α 会把"相对期望剖面缺失"从 0.01~0.12px 抬到
	//      0.08~1.68px ⇒ α 被系统性低估）。故默认**直接用编码值**做线性组合；
	//      `--blend=lin` 走反编码路径，仅作对照。
	//   ⚠️ 选通道时务必取 |分母| 有意义的那个：R 通道在近白背景（雪/高亮）上失效，已由 den<0.12 判掉。
	static readonly float[] SrgbToLin = BuildSrgbLut();
	static float[] BuildSrgbLut()
	{
		var t = new float[256];
		for (int i = 0; i < 256; i++)
		{
			float c = i / 255f;
			t[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
		}
		return t;
	}

	static float AlphaR(byte[] fg, byte[] bg, int i, bool lin)
	{
		float rF = lin ? SrgbToLin[fg[i]] : fg[i] / 255f;
		float rB = lin ? SrgbToLin[bg[i]] : bg[i] / 255f;
		float den = 1f - rB;
		if (den < 0.12f) return -1f;              // 背景近白 ⇒ 该通道不可用
		float a = (rF - rB) / den;
		return a > 0f ? MathF.Min(a, 1f) : 0f;
	}

	/// <summary>归属判据：s&gt;0 ⇒ 黄（干流面）；s&lt;0 ⇒ 洋红（支流面）；|s| ≈ α。</summary>
	static float SideSign(byte[] fg, byte[] bg, int i, float a, bool lin)
	{
		float gF = lin ? SrgbToLin[fg[i + 1]] : fg[i + 1] / 255f;
		float bF = lin ? SrgbToLin[fg[i + 2]] : fg[i + 2] / 255f;
		float gB = lin ? SrgbToLin[bg[i + 1]] : bg[i + 1] / 255f;
		float bB = lin ? SrgbToLin[bg[i + 2]] : bg[i + 2] / 255f;
		return (gF - bF) - (1f - a) * (gB - bB);
	}

	static int BppOf(Image img) => img.GetFormat() switch { Image.Format.Rgba8 => 4, Image.Format.Rgb8 => 3, _ => 0 };

	double Ink(Image fgImg, Image bgImg)
	{
		int w = fgImg.GetWidth(), h = fgImg.GetHeight();
		if (bgImg.GetWidth() != w || bgImg.GetHeight() != h) return -1;
		int bpp = BppOf(fgImg);
		if (bpp == 0) return -1;
		byte[] fg = fgImg.GetData(), bg = bgImg.GetData();
		double ink = 0;
		for (int p = 0, n = w * h; p < n; p++)
		{
			float a = AlphaR(fg, bg, p * bpp, _blendLinear);
			if (a > 0) ink += a;
		}
		return ink;
	}

	/// <summary>bbox 内的 α / 归属场（后续所有局部量都在这上面做双线性采样）。</summary>
	sealed class Probe
	{
		public int X0, Y0, W, H;
		public float[] A = Array.Empty<float>();
		public float[] S = Array.Empty<float>();

		public bool Inside(Vector2 p) => p.X >= X0 && p.Y >= Y0 && p.X <= X0 + W - 1 && p.Y <= Y0 + H - 1;

		public double At(float[] f, Vector2 p)
		{
			float x = p.X - X0, y = p.Y - Y0;
			if (x < 0 || y < 0 || x > W - 1 || y > H - 1) return 0;
			int ix = (int)x, iy = (int)y;
			float fx = x - ix, fy = y - iy;
			int ix1 = Math.Min(ix + 1, W - 1), iy1 = Math.Min(iy + 1, H - 1);
			double v00 = f[iy * W + ix], v10 = f[iy * W + ix1], v01 = f[iy1 * W + ix], v11 = f[iy1 * W + ix1];
			return (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
		}
		public double Alpha(Vector2 p) => At(A, p);
		public double Sign(Vector2 p) => At(S, p);
	}

	// ── 主分析 ──────────────────────────────────────────────────────────
	void Analyze(Image fgImg, Image bgImg, int mi)
	{
		if (_cam == null || _all == null) return;
		float d = _cam.GlobalPosition.Length();
		if (d < 1e-6f) return;
		var chat = _cam.GlobalPosition / d;
		float cosVis = R / d;
		int bpp = BppOf(fgImg);
		if (bpp < 3) { GD.Print("  [RIVER] 图像格式不支持（需要 RGB/RGBA8）"); return; }
		int IW = fgImg.GetWidth(), IH = fgImg.GetHeight();
		byte[] fg = fgImg.GetData(), bg = bgImg.GetData();

		var visScr = new List<Vector2[]>(_all.Count);
		var visOk = new List<bool[]>(_all.Count);
		foreach (var p in _all)
		{
			visScr.Add(Project(p.Pts, chat, cosVis, out var ok));
			visOk.Add(ok);
		}

		// ① 既有基线：总墨水 / 投影河长（与历史记录同口径）
		double projLen = 0;
		for (int i = 0; i < _all.Count; i++)
			for (int k = 0; k + 1 < visScr[i].Length; k++)
				if (visOk[i][k] && visOk[i][k + 1]) projLen += visScr[i][k].DistanceTo(visScr[i][k + 1]);
		double ink = Ink(fgImg, bgImg);
		double widthGlobal = projLen > 1 ? ink / projLen : 0;
		GD.Print($"  [RIVER {Tag(mi)}] 全局 ink={ink:F1}px² len={projLen:F1}px " +
				 $"墨水宽={widthGlobal:F3}px（期望 {_expectInk:F2}） 标称={widthGlobal / Math.Max(_soft, 1e-6):F3}px（期望 {_nominalPx:F2}）");

		// ② 局部：把关注窗口的采样点投影出来
		var win = WindowPaths(chat, cosVis);
		if (win.Count == 0)
		{
			GD.Print($"  [RIVER {Tag(mi)}] ⚠️ 关注窗口全部不可见（目标被遮挡或出视锥）");
			return;
		}

		var pr = BuildProbe(fg, bg, bpp, IW, IH, win, 20);
		if (pr.W <= 0 || pr.H <= 0) { GD.Print($"  [RIVER {Tag(mi)}] ⚠️ 探针窗口退化"); return; }

		var line = LocalMetrics(win, pr, chat, cosVis);
		double screenR = new Vector2(IW / 2f, IH / 2f).DistanceTo(win[0].Scr[win[0].Mark]);
		GD.Print($"  [RIVER {_aimName} {Tag(mi)}] {line} | 屏半径={screenR:F0}px");
	}

	Vector2[] Project(Vector3[] pts, Vector3 chat, float cosVis, out bool[] ok)
	{
		int n = pts.Length;
		var scr = new Vector2[n];
		ok = new bool[n];
		for (int i = 0; i < n; i++)
		{
			var w = pts[i] * R;
			ok[i] = pts[i].Dot(chat) > cosVis && _cam.IsPositionInFrustum(w);
			scr[i] = ok[i] ? _cam.UnprojectPosition(w) : new Vector2(float.NaN, float.NaN);
		}
		return scr;
	}

	sealed class WinPath
	{
		public string Role = "";
		public bool Main;
		public Vector2[] Scr = Array.Empty<Vector2>();   // 窗口（含 WinPad 外延）采样点的屏幕坐标
		public bool[] Ok = Array.Empty<bool>();
		public double[] Arc = Array.Empty<double>();     // 沿路径累计弧长（px）
		public int Mark;                                 // 关注点在窗口内的下标
		public int Lo, Hi;                               // 内层区间（参与逐点宽度统计，排除外延）
		public int[] G;                                  // 窗口内采样点 → 全局采样下标
		public PathD Src;
	}

	/// <summary>走廊折线（用于"理想河道"判据）：汇流时把支流尾 + 干流头**接成一条**，
	/// 否则汇流点会被当成端点平切，缺口判据会误报。</summary>
	sealed class Poly
	{
		public Vector2[] P = Array.Empty<Vector2>();
		public bool[] Ok = Array.Empty<bool>();
	}

	const int WinHalf = 6;      // 关注点前后各取 6 个样条采样点参与统计
	const int WinPad = 3;       // 走廊再向外延伸 3 点 ⇒ 走廊两端的平切不会污染缺口判据

	List<WinPath> WindowPaths(Vector3 chat, float cosVis)
	{
		var res = new List<WinPath>();
		foreach (var p in _target.Paths)
		{
			if (p.Pts.Length == 0) continue;
			int ext = WinHalf + WinPad;
			int a = Math.Max(0, p.Mark - ext), b = Math.Min(p.Pts.Length - 1, p.Mark + ext);
			var scr = new Vector2[b - a + 1];
			var ok = new bool[b - a + 1];
			var arc = new double[b - a + 1];
			var gidx = new int[b - a + 1];
			for (int i = a; i <= b; i++)
			{
				int k = i - a;
				gidx[k] = i;
				var w = p.Pts[i] * R;
				ok[k] = p.Pts[i].Dot(chat) > cosVis && _cam.IsPositionInFrustum(w);
				scr[k] = ok[k] ? _cam.UnprojectPosition(w) : new Vector2(float.NaN, float.NaN);
			}
			for (int k = 1; k < scr.Length; k++)
				arc[k] = arc[k - 1] + (ok[k] && ok[k - 1] ? scr[k].DistanceTo(scr[k - 1]) : 0);
			res.Add(new WinPath
			{
				Role = p.Role, Main = p.Main, Scr = scr, Ok = ok, Arc = arc,
				Mark = p.Mark - a, G = gidx, Src = p,
				Lo = Math.Max(0, p.Mark - WinHalf) - a,
				Hi = Math.Min(p.Pts.Length - 1, p.Mark + WinHalf) - a,
			});
		}
		return res;
	}

	static List<Poly> CorridorOf(List<WinPath> win)
	{
		var res = new List<Poly>();
		if (win.Count >= 2)
		{
			// 汇流：支流尾（含汇流格）+ 干流头（跳过重复的汇流格）⇒ 一条连续折线
			var pts = new List<Vector2>();
			var ok = new List<bool>();
			foreach (var w in win)
				for (int k = (w == win[0] ? 0 : 1); k < w.Scr.Length; k++) { pts.Add(w.Scr[k]); ok.Add(w.Ok[k]); }
			res.Add(new Poly { P = pts.ToArray(), Ok = ok.ToArray() });
			return res;
		}
		foreach (var w in win) res.Add(new Poly { P = w.Scr, Ok = w.Ok });
		return res;
	}

	/// <summary>点到走廊的最近距离；<c>atCap</c> = 最近点落在折线**端点平切**处（非缺陷，跳过）。</summary>
	static double DistToCorridor(Vector2 p, List<Poly> polys, out bool atCap)
	{
		atCap = true;
		double best = double.MaxValue;
		bool found = false;
		foreach (var q in polys)
		{
			int n = q.P.Length;
			for (int k = 0; k + 1 < n; k++)
			{
				if (!q.Ok[k] || !q.Ok[k + 1]) continue;
				var ab = q.P[k + 1] - q.P[k];
				float L2 = ab.LengthSquared();
				float t = L2 > 1e-9f ? Math.Clamp((p - q.P[k]).Dot(ab) / L2, 0f, 1f) : 0f;
				double d = p.DistanceTo(q.P[k] + ab * t);
				if (d < best)
				{
					best = d; found = true;
					atCap = (k == 0 && t <= 1e-6f) || (k == n - 2 && t >= 1f - 1e-6f);
				}
			}
		}
		return found ? best : double.MaxValue;
	}

	static double DistToPoly(Vector2 p, Poly q)
	{
		double best = double.MaxValue;
		for (int k = 0; k + 1 < q.P.Length; k++)
		{
			if (!q.Ok[k] || !q.Ok[k + 1]) continue;
			best = Math.Min(best, DistToSegment(p, q.P[k], q.P[k + 1]));
		}
		return best;
	}

	static double SmoothStep(double a, double b, double x)
	{
		double t = Math.Clamp((x - a) / Math.Max(b - a, 1e-9), 0.0, 1.0);
		return t * t * (3.0 - 2.0 * t);
	}

	Probe BuildProbe(byte[] fg, byte[] bg, int bpp, int IW, int IH, List<WinPath> win, int pad)
	{
		float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
		foreach (var w in win)
			for (int k = 0; k < w.Scr.Length; k++)
			{
				if (!w.Ok[k]) continue;
				x0 = MathF.Min(x0, w.Scr[k].X); x1 = MathF.Max(x1, w.Scr[k].X);
				y0 = MathF.Min(y0, w.Scr[k].Y); y1 = MathF.Max(y1, w.Scr[k].Y);
			}
		if (x0 > x1) return new Probe();
		var pr = new Probe
		{
			X0 = Math.Max(0, (int)(x0 - pad)), Y0 = Math.Max(0, (int)(y0 - pad)),
		};
		pr.W = Math.Min(IW - 1, (int)(x1 + pad)) - pr.X0 + 1;
		pr.H = Math.Min(IH - 1, (int)(y1 + pad)) - pr.Y0 + 1;
		if (pr.W <= 0 || pr.H <= 0) return new Probe();
		pr.A = new float[pr.W * pr.H];
		pr.S = new float[pr.W * pr.H];
		for (int y = 0; y < pr.H; y++)
		{
			for (int x = 0; x < pr.W; x++)
			{
				int i = ((y + pr.Y0) * IW + (x + pr.X0)) * bpp;
				float a = AlphaR(fg, bg, i, _blendLinear);
				int q = y * pr.W + x;
				pr.A[q] = a < 0 ? -1f : a;
				pr.S[q] = a <= 0 ? 0f : SideSign(fg, bg, i, a, _blendLinear);
			}
		}
		return pr;
	}

	/// <summary>沿局部法向做 1px 步进的墨水积分 ⇒ 该处笔画宽（px）。双线性采样消除取整量化。</summary>
	double WidthAt(Probe pr, Vector2 p, Vector2 n)
	{
		int K = (int)(0.5 * _nominalPx) + 6;
		double s = 0;
		for (int k = -K; k <= K; k++)
		{
			double a = pr.Alpha(p + n * k);
			if (a > 0) s += a;
		}
		return s;
	}

	/// <summary>局部法向（⊥ 屏幕切线）。取不到有效切线（邻居不可见/退化）⇒ 返回 false，
	/// 该采样点不计入宽度统计——用默认 (1,0) 硬撑会把斜扫当成横扫，系统性压低宽度。</summary>
	static bool NormalAt(Vector2[] scr, bool[] ok, int i, out Vector2 n)
	{
		n = new Vector2(1f, 0f);
		int m = scr.Length;
		int a = -1, b = -1;
		for (int k = i - 1; k >= 0; k--) if (ok[k]) { a = k; break; }
		for (int k = i + 1; k < m; k++) if (ok[k]) { b = k; break; }
		if (a < 0 && b < 0) return false;                 // 端点：用单侧切线，不能判为无效
		Vector2 t = a >= 0 && b >= 0 ? scr[b] - scr[a]
				  : b >= 0 ? scr[b] - scr[i] : scr[i] - scr[a];
		if (t.LengthSquared() < 1e-9f) return false;
		n = new Vector2(-t.Y, t.X).Normalized();
		return true;
	}

	string LocalMetrics(List<WinPath> win, Probe pr, Vector3 chat, float cosVis)
	{
		// ── 通用：窗口内层逐采样点宽度 ──
		var widths = new List<double>();
		var wAt = new Dictionary<WinPath, double[]>();
		foreach (var w in win)
		{
			var arr = new double[w.Scr.Length];
			for (int k = 0; k < w.Scr.Length; k++)
			{
				if (!w.Ok[k] || !NormalAt(w.Scr, w.Ok, k, out var nrm)) { arr[k] = double.NaN; continue; }
				arr[k] = WidthAt(pr, w.Scr[k], nrm) / _soft;   // 除掉柔化系数 ⇒ 标称宽
			}
			wAt[w] = arr;
			for (int k = w.Lo; k <= w.Hi; k++) if (!double.IsNaN(arr[k])) widths.Add(arr[k]);
		}
		string widthTxt = "—";
		if (widths.Count > 0)
		{
			var sorted = widths.OrderBy(x => x).ToList();
			double med = sorted[sorted.Count / 2];
			double mn = sorted[0], mx = sorted[^1];
			double varPct = med > 1e-6 ? (mx - mn) / med * 100.0 : 0;
			widthTxt = $"宽[中位={med:F2} min={mn:F2} max={mx:F2} 变幅={varPct:F1}%]";
		}

		// ── 缺口（gap）：走廊内**相对期望横截面**缺失的覆盖量 ⇒ 等效缺失长度 ──
		//   ★不能拿"固定半径 + α<常数"判：横截面本来就是软边（core→1 渐隐），
		//     外缘 α 天然为 0 ⇒ 会造出根本不存在的缺口。正确做法是与期望剖面比。
		var corridor = CorridorOf(win);
		double half = 0.5 * _nominalPx;
		double miss = 0, unknown = 0;
		for (int y = 0; y < pr.H; y++)
		{
			for (int x = 0; x < pr.W; x++)
			{
				var p = new Vector2(pr.X0 + x + 0.5f, pr.Y0 + y + 0.5f);
				double dist = DistToCorridor(p, corridor, out bool atCap);
				if (atCap || dist > half) continue;             // 端点平切处的越界不是缺陷
				double expect = 1.0 - SmoothStep(_core, 1.0, dist / half);
				if (expect < 0.05) continue;
				float a = pr.A[y * pr.W + x];
				if (a < 0) { unknown++; continue; }
				if (a < 0.5 * expect) miss += expect - a;
			}
		}
		double gapPx = miss / Math.Max(_nominalPx, 1e-6);

		string head = $"{widthTxt} | gap={gapPx:F2}px (缺失={miss:F2}px² 未知={unknown:F0})";

		// ── 分 aim 的专项量 ──
		if (_aimName == "confluence" && win.Count >= 2)
			return head + " | " + ConfluenceMetrics(win, wAt, pr, corridor, half);
		if (_aimName == "bend")
			return head + " | " + BendMetrics(win, wAt);
		if (_aimName == "outlet")
			return head + " | " + OutletMetrics(win, wAt, pr);
		return head;
	}

	string ConfluenceMetrics(List<WinPath> win, Dictionary<WinPath, double[]> wAt,
							 Probe pr, List<Poly> corridor, double half)
	{
		var trib = win.FirstOrDefault(w => w.Role == "tributary") ?? win[0];
		var trunk = win.FirstOrDefault(w => w.Role == "trunk") ?? win[^1];

		double Med(WinPath w, int from, int to)
		{
			var v = new List<double>();
			for (int k = Math.Max(0, from); k <= Math.Min(w.Scr.Length - 1, to); k++)
				if (!double.IsNaN(wAt[w][k])) v.Add(wAt[w][k]);
			if (v.Count == 0) return double.NaN;
			v.Sort();
			return v[v.Count / 2];
		}

		int jm = trib.Mark;                                   // 汇流点在支流窗口内的下标（= 链尾）
		double wTrib = Med(trib, Math.Max(trib.Lo, jm - 5), Math.Max(trib.Lo, jm - 1));
		double wJun = wAt[trib][jm];                          // 支流带在汇流格处的宽
		double wJunB = wAt[trunk][0];                         // 干流带在汇流格处的宽（同一点、另一朝向）
		double wTrunk = Med(trunk, 1, Math.Min(5, trunk.Hi));
		if (!double.IsNaN(wJunB))
			wJun = double.IsNaN(wJun) ? wJunB : 0.5 * (wJun + wJunB);

		// ── 汇流处两条带的夹角（3D）：W_junction 之所以会比两侧宽，根源就是这个角 ──
		//   ★交叉几何的**预期宽**：垂直于支流的扫描线会完整穿过支流带（w），再斜穿干流带
		//     （长度 w / (2|cosΔθ|)，且只在一侧）⇒ 预期 w·(1 + 1/(2|cosΔθ|))。
		//     不减掉这个预期，会把"两条带正常交叉"误读成"汇流点鼓包"。
		double turnJ = 0, expectJun = _nominalPx;
		var ta = trib.Src.Anchors; var tb = trunk.Src.Anchors;
		if (ta.Length >= 2 && tb.Length >= 2)
		{
			var dIn = (ta[^1] - ta[^2]).Normalized();
			var dOut = (tb[1] - tb[0]).Normalized();
			turnJ = Math.Acos(Math.Clamp(dIn.Dot(dOut), -1f, 1f));
			expectJun = _nominalPx * (1f + 1f / (2f * Math.Max(MathF.Abs(MathF.Cos((float)turnJ)), 0.2f)));
		}
		double turnJdeg = turnJ * 180.0 / Math.PI;

		double jump = 1.0, jumpVs = 1.0;
		void Acc(double a, double b, bool vs = false)
		{
			if (double.IsNaN(a) || double.IsNaN(b) || a < 1e-6 || b < 1e-6) return;
			double j = Math.Max(a / b, b / a);
			if (vs) jumpVs = Math.Max(jumpVs, j); else jump = Math.Max(jump, j);
		}
		Acc(wTrib, wJun); Acc(wJun, wTrunk); Acc(wJun, expectJun, vs: true);

		// 重叠区（几何）：沿走廊密集步进，统计"距两条带都 ≤ 半宽"的长度。
		// ⚠️ 不能用"段中点到另一条折线的距离"——采样间距远大于带宽，中点永远判不出重叠（实测恒 0）。
		double overlap = OverlapPx(corridor, trib, trunk, half);

		// ★depthOrderingMismatch（不是 Error，见文件头语义）：重叠区里"支流(洋红)占主导"的像素比例。
		//   干流 bias 更大 ⇒ 应当由干流压住支流末端；反过来说明 bias 方向/量级失效。
		//   ⚠️ 读数必须配合 `--bias` 拉平灵敏度对照一起看，否则无法区分"真反转"与"预期归属混合"。
		int total = 0, wrong = 0, unknown = 0;

		// 干流带平切于 J ⇒ J 的**上游侧**（沿干流方向的负向）没有干流墨水
		var jM = trunk.Scr[0];
		var dirM = trunk.Scr[Math.Min(1, trunk.Scr.Length - 1)] - jM;
		if (dirM.LengthSquared() > 1e-9f) dirM = dirM.Normalized();
		bool BehindTrunk(Vector2 p) => (p - jM).Dot(dirM) < -0.5f;

		// 支流带平切于 J ⇒ J 的**下游侧**没有支流墨水
		int tEnd = trib.Scr.Length - 1;
		var jT = trib.Scr[tEnd];
		var dirT = jT - trib.Scr[Math.Max(0, tEnd - 1)];
		if (dirT.LengthSquared() > 1e-9f) dirT = dirT.Normalized();
		bool PastTrib(Vector2 p) => (p - jT).Dot(dirT) > 0.5f;

		for (int y = 0; y < pr.H; y++)
		{
			for (int x = 0; x < pr.W; x++)
			{
				var p = new Vector2(pr.X0 + x + 0.5f, pr.Y0 + y + 0.5f);
				if (DistToPoly(p, corridor[0]) > half) continue;      // 只看走廊内（理想河道）
				if (DistToPoly(p, PolyOf(trib)) > half) continue;      // 且**两条带都够得着**
				if (DistToPoly(p, PolyOf(trunk)) > half) continue;
				// ★两条带都是**平切**端：汇流格 J 的"外侧半平面"各自不被自己覆盖。
				//   不排除这些像素，会把"该处本来只有一条带"误判成排序错误。
				if (BehindTrunk(p) || PastTrib(p)) continue;
				float a = pr.A[y * pr.W + x];
				if (a < 0.5f) continue;         // ★只看**实心覆盖**的像素：半透明边界上
												//   两层天然混合，归属本就无意义（会把 AA 误判成排序错误）
				double s = pr.Sign(p);
				if (Math.Abs(s) < 0.05f) { unknown++; continue; }
				total++;
				if (s < 0) wrong++;                      // 洋红 = 支流赢了 ⇒ 排序错误
			}
		}
		double errPct = total > 0 ? wrong * 100.0 / total : 0;

		return $"W[支={wTrib:F2} 汇={wJun:F2} 干={wTrunk:F2}] 夹角={turnJdeg:F0}° " +
			   $"交叉预期={expectJun:F2} jump={jump:F2}x 汇/预期={jumpVs:F2}x | " +
			   $"overlap={overlap:F1}px depthOrderingMismatch={errPct:F1}%(支流主导 {wrong}/{total}, 判不出={unknown})" +
			   (total < 8 ? " ⚠️重叠区过小" : "");
	}

	/// <summary>走廊上"两条带都够得着"的长度（px）。沿走廊密集步进，不用段中点。</summary>
	static double OverlapPx(List<Poly> corridor, WinPath trib, WinPath trunk, double half)
	{
		var poly = corridor.Count > 0 ? corridor[0] : null;
		if (poly == null) return 0;
		var pt = PolyOf(trib); var pk = PolyOf(trunk);
		const double Step = 0.25;
		double ov = 0;
		for (int k = 0; k + 1 < poly.P.Length; k++)
		{
			if (!poly.Ok[k] || !poly.Ok[k + 1]) continue;
			var a = poly.P[k]; var b = poly.P[k + 1];
			double L = a.DistanceTo(b);
			if (!(L > 0)) continue;
			int n = Math.Max(1, (int)(L / Step));
			for (int s = 0; s < n; s++)
			{
				var q = a + (b - a) * (s / (float)n);
				if (DistToPoly(q, pt) <= half && DistToPoly(q, pk) <= half) ov += L / n;
			}
		}
		return ov;
	}

	string BendMetrics(List<WinPath> win, Dictionary<WinPath, double[]> wAt)
	{
		var w = win[0];
		var p = w.Src;
		int mk = w.G[w.Mark];

		// ★护栏的**实测复算**：采样点相对所在段弦的偏移 / 锚点间距（样条内部保证 ≤ 0.15）
		//   ⚠️ 段 0 的采样数要 **−1**：`Sample` 在循环前先塞了 anchors[0]（segOf=0, t=0），
		//      那不是段内插值点。漏了这步会把 t 算成 ord/(n+1) ⇒ 首点偏移 = 间距/2 ⇒ 比=0.500（假警报）。
		var counts = new Dictionary<int, int>();
		foreach (var s in p.SegOf) counts[s] = counts.TryGetValue(s, out var c) ? c + 1 : 1;
		double devMax = 0, devMaxPx = 0;
		int cur = -1, ord = 0;
		for (int i = 0; i < p.Pts.Length; i++)
		{
			int j = p.SegOf[i];
			float t;
			if (i == 0) { cur = j; ord = 0; t = 0f; }          // anchors[0] 本身
			else
			{
				if (cur != j) { cur = j; ord = 0; }
				ord++;
				int n = counts[j] - (j == 0 ? 1 : 0);
				t = n > 0 ? ord / (float)n : 1f;
			}
			if (j < 0 || j + 1 >= p.Anchors.Length) continue;
			var a1 = p.Anchors[j]; var a2 = p.Anchors[j + 1];
			var chord = (a1 + (a2 - a1) * t).Normalized();
			double dev = p.Pts[i].DistanceTo(chord);
			double spacing = a1.DistanceTo(a2);
			if (spacing > 1e-9) devMax = Math.Max(devMax, dev / spacing);
			int iw = i - w.G[0];
			if (iw >= 0 && iw < w.Scr.Length && w.Ok[iw])
			{
				var sa = ScreenOf(a1); var sb = ScreenOf(a2);
				if (!(float.IsNaN(sa.X) || float.IsNaN(sb.X)))
					devMaxPx = Math.Max(devMaxPx, DistToSegment(w.Scr[iw], sa, sb));
			}
		}

		// 切线转折（3D 量，不受投影畸变影响）：原生 H3 折线在关注锚点处 vs 窗口内样条的最大逐点转折
		int ka = p.MarkAnchor;
		double turnRaw = ka > 0 && ka + 1 < p.Anchors.Length
			? RiverPresentationSpline.TurnAt(p.Anchors, ka) * 180.0 / Math.PI : 0;
		double turnSpline = 0;
		int lo = Math.Max(1, w.G[0]), hi = Math.Min(p.Pts.Length - 2, w.G[^1]);
		for (int i = lo; i <= hi; i++)
			turnSpline = Math.Max(turnSpline, TurnDeg(p.Pts[i], p.Pts[i - 1], p.Pts[i + 1]));

		var vals = wAt[w].Where(x => !double.IsNaN(x)).ToList();
		double varPct = 0;
		if (vals.Count > 0)
		{
			vals.Sort();
			double med = vals[vals.Count / 2];
			varPct = med > 1e-6 ? (vals[^1] - vals[0]) / med * 100.0 : 0;
		}

		return $"dev[比={devMax:F3} ≤{RiverPresentationSpline.MaxDeviationRatio} 屏={devMaxPx:F2}px] " +
			   $"转折[锚点原生={turnRaw:F1}° 窗口样条={turnSpline:F1}°] 宽变幅={varPct:F1}%";

		Vector2 ScreenOf(Vector3 dir) => _cam.IsPositionInFrustum(dir * R)
			? _cam.UnprojectPosition(dir * R) : new Vector2(float.NaN, float.NaN);
	}

	string OutletMetrics(List<WinPath> win, Dictionary<WinPath, double[]> wAt, Probe pr)
	{
		var w = win[0];
		int last = w.Scr.Length - 1;
		if (!w.Ok[last]) return "出口点不可见";

		// 端头完整性：沿末端切线向外扫，看墨水在哪里停（平切端 ⇒ 越界应立刻为 0）；
		// 再向内扫，看是否提前断掉。
		var t = (w.Scr[last] - w.Scr[Math.Max(0, last - 1)]);
		if (t.LengthSquared() < 1e-9f) return "末端切线退化";
		t = t.Normalized();
		double over = 0;
		for (int k = 1; k <= 10; k++)
		{
			double a = pr.Alpha(w.Scr[last] + t * k);
			if (a > 0.35) over = k;
		}
		double shortfall = 0;
		for (int k = 0; k <= 10; k++)
		{
			double a = pr.Alpha(w.Scr[last] - t * k);
			if (a <= 0.35) { shortfall = k; break; }
		}
		double wTail = double.NaN;
		var v = new List<double>();
		for (int k = Math.Max(0, last - 4); k <= last; k++) if (!double.IsNaN(wAt[w][k])) v.Add(wAt[w][k]);
		if (v.Count > 0) { v.Sort(); wTail = v[v.Count / 2]; }

		return $"末端[宽={wTail:F2}px 外溢={over:F0}px 内缩={shortfall:F0}px]（平切端外溢应≈0）";
	}

	// ⚠️ 方向必须取 **p−prev** 与 **next−p**（同向），不能取 prev−p 与 next−p——
	//    后者在直线上得 180°（来向与去向本就相反），会把"完全平直"报成"最大折叠"。
	static double TurnDeg(Vector3 p, Vector3 prev, Vector3 next)
	{
		var a = p - prev; a -= p * p.Dot(a);
		var b = next - p; b -= p * p.Dot(b);
		if (a.Length() < 1e-9f || b.Length() < 1e-9f) return 0;
		double c = Math.Clamp(a.Normalized().Dot(b.Normalized()), -1f, 1f);
		return Math.Acos(c) * 180.0 / Math.PI;
	}

	static Poly PolyOf(WinPath w) => new Poly { P = w.Scr, Ok = w.Ok };

	static double DistToSegment(Vector2 p, Vector2 a, Vector2 b)
	{
		var ab = b - a;
		float L2 = ab.LengthSquared();
		float t = L2 > 1e-9f ? Math.Clamp((p - a).Dot(ab) / L2, 0f, 1f) : 0f;
		return p.DistanceTo(a + ab * t);
	}

	static string InsertSuffix(string path, string suffix)
	{
		int dot = path.LastIndexOf('.');
		return dot < 0 ? path + suffix : path[..dot] + suffix + path[dot..];
	}
}
