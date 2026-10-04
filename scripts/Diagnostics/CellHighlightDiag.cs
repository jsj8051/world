using Godot;
using System;
using System.Collections.Generic;
using World.Services;                    // UserPaths
using World.Render;                      // BallView / CellQuery
using World.NoiseWorld.WorldGen;         // WorldGenPlanet
using World.Utils;                       // CoordUtil
using World.Utils.H3;

namespace World.Diagnostics;

// 选中高亮 · 实机数值诊断（2026-10-04；对齐 RiverShotDiag 惯例：窗口模式、像素差分、自动退出）。
//
// ★为什么要有这个工具：选中高亮曾把整格扇面画在 R×1.0005 上，径向抬升在掠射角下换算成
//   横向屏幕位移（实测 1.02R 最近视距的屏幕边缘达 14.6 px ≈ 10% 格宽）⇒ "选中和实际有点偏差"。
//   现方案改成 **R 上的球面环带 + clip-space 深度偏移**，本工具用像素把结论钉死，不靠肉眼。
//
// 用户要求的 4 项验证：
//   ① 中心位置：环带墨迹质心 vs 真实地形格多边形质心 ⇒ 偏移应 ≈ 0（旧 lift 方案显著 > 0）
//   ② 屏幕边缘 @ 1.02R：同上；同时用 unproject 口径复算旧 lift 的位移做对照
//   ③ 六个角/六条边：逐边宽度 = 墨水/边长 ⇒ 无缺口（无 0 桶）、无过宽过窄（离散度小）
//   ④ 相机移动：4 档视距 × 3 个屏幕位置（正中/中段/边缘）⇒ 宽度恒 px、轮廓始终贴同一边界
//
// 用法：
//   Godot --path . res://scenes/diag/CellHighlightDiag.tscn --
//   Godot --path . res://scenes/diag/CellHighlightDiag.tscn -- --lift=1.0005   ← 旧方案 A/B 对照
//   Godot --path . res://scenes/diag/CellHighlightDiag.tscn -- --res=3         ← 覆盖分辨率档
public partial class CellHighlightDiag : Node
{
	const float R = 2.0f;                 // worldgen 单位球尺度（OrbitalCamera 同款）
	const int Warmup = 14;
	const int Block = 11;                 // 每次测量的帧块（两段相机摆位 + 两帧空场噪声检验）
	static readonly float[] Tiers = { 1.7f, 1.15f, 1.05f, 1.02f };
	static readonly float[] Fracs = { 0f, 0.5f, 1.0f };   // 目标屏幕半径 = frac × 0.8 × min(W,H)/2
	const float ScreenFrac = 0.8f;
	const float ControlLift = 1.0005f;    // 旧方案抬升值：每档都复算一次位移做对照

	Node3D _world;
	Camera3D _cam;
	Node _orbital;
	WorldGenPlanet _planet;
	BallView _view;
	MeshInstance3D _hl;
	bool _camFound;

	ulong _cell;
	Vector3 _cellDir;
	Vector3 _e1;                 // 与格心正交的切向（用来把格子推离屏幕中心）
	float _lift = 1.0f;                   // >1 时复现旧方案的 world-space radial lift（A/B 对照）
	float _forcedWidth = -1f;             // 标定量：>0 时把所有顶点 UV2.x 强制成同一个 px 宽度

	int _frame;
	Image _bg;

	public override void _Ready()
	{
		var args = DiagSceneBase.ParseUserArgs();
		if (args.TryGetValue("lift", out var lf) && float.TryParse(lf, out float l)) _lift = l;
		if (args.TryGetValue("width", out var w) && float.TryParse(w, out float fw)) _forcedWidth = fw;

		var packed = GD.Load<PackedScene>("res://scenes/core/WorldGenWorld.tscn");
		_world = (Node3D)packed.Instantiate();
		_planet = _world.GetNode<WorldGenPlanet>("WorldGenPlanet");
		if (args.TryGetValue("res", out var rs) && int.TryParse(rs, out int res))
			_planet.ResLevel = res;
		AddChild(_world);
		_view = _planet.View;

		// 取一个避开极点/五边形的格子（确定性：固定方向 + 固定 res）
		var d0 = new Vector3(0.3f, 0.8f, 0.5f).Normalized();
		_e1 = d0.Cross(Vector3.Up).Normalized();
		if (_e1.LengthSquared() < 0.5f) _e1 = d0.Cross(Vector3.Right).Normalized();
		double lat = Math.Asin(d0.Y), lng = Math.Atan2(d0.Z, d0.X);
		_cell = H3.LatLngToCell(new LatLng(lat, lng), _planet.ResLevel);
		_cellDir = CoordUtil.LatLngToSphere(H3.CellToLatLng(_cell), 1f);

		GD.Print($"[CELL-HL] res={_planet.ResLevel} cell={_cell:X} dir={_cellDir:F4} " +
				 $"lift={_lift:F5}（lift>1 = 复现旧 radial-lift 方案做 A/B）");
	}

	public override void _Process(double delta)
	{
		_frame++;
		if (!_camFound && _frame <= 10)
		{
			_camFound = TryFindCamera();
			if (!_camFound) return;
		}

		int total = Tiers.Length * Fracs.Length;
		if (_frame < Warmup) return;
		int rel = _frame - Warmup;
		int mi = rel / Block, b = rel % Block;
		if (mi >= total) { GetTree().Quit(); return; }

		if (b == 0)
		{
			// 重新生成**原始**环带（避免上一块把 mesh 改脏后累计缩放），再上探针色并隐藏
			_view.HighlightCell(_cell);
			_hl = _view?.GetNodeOrNull<MeshInstance3D>(CellQuery.HighlightNodeName);
			if (_hl == null) { GD.Print("[CELL-HL] **未找到 CellHighlight 节点**（主场景结构变了？）"); GetTree().Quit(); return; }
			MoveTo(_cellDir, Tiers[mi / Fracs.Length]);   // 先摆到本档视距并居中
			MakeProbeMesh();
			_hl.Visible = false;
		}
		else if (b == 1)
		{
			// 相机已在本档视距 ⇒ 此刻才能按**屏幕半径**二分定位（视锥比地平线窄得多）
			MoveTo(AimForScreenRadius(mi), Tiers[mi / Fracs.Length]);
		}
		else if (b == 4) _bg = Snap();                       // 空场 A
		else if (b == 5)
		{
			var bgB = Snap();                                // 空场 B
			double noise = Ink(bgB, _bg);                    // 噪声底：连续两帧都无高亮 ⇒ 应 ≈0
			if (noise > 1.0) GD.Print($"  [CELL-HL] ⚠️ 噪声底={noise:F2}px²（场景不确定，测量可疑）");
			_bg = bgB;
		}
		else if (b == 6) _hl.Visible = true;
		else if (b == 9)
		{
			var fg = Snap();
			Analyze(fg, _bg, mi);
		}
	}

	Vector3 DirAt(float a) => (_cellDir * MathF.Cos(a) + _e1 * MathF.Sin(a)).Normalized();

	/// <summary>
	/// ★不能直接按"可见球冠/地平线"角度定位：近距离下**视锥**远比地平线窄
	///   （实测 1.05R 时冠 0.5 已跑到屏幕外 ⇒ 大量"跳过"）。
	///   改为二分球面角，使格子落在**目标屏幕半径**上 ⇒ 中心/中段/边缘三档真正可比。
	/// </summary>
	Vector3 AimForScreenRadius(int mi)
	{
		float d = Tiers[mi / Fracs.Length];
		var vp = GetViewport().GetVisibleRect().Size;
		float target = Fracs[mi % Fracs.Length] * ScreenFrac * MathF.Min(vp.X, vp.Y) * 0.5f;
		var center = vp * 0.5f;

		float lo = 0f, hi = MathF.Acos(MathF.Min(1f, 1f / d)) * 0.999f;   // 上界 = 地平线
		for (int it = 0; it < 30; it++)
		{
			float mid = (lo + hi) * 0.5f;
			var p = DirAt(mid) * R;
			if (!_cam.IsPositionInFrustum(p)) { hi = mid; continue; }     // 出了视锥 ⇒ 收小
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

	// ── 探针：把环带染成洋红（R=1,G=0,B=1），并可选复现旧方案的径向抬升 ──
	void MakeProbeMesh()
	{
		if (_hl?.Mesh is not ArrayMesh src) return;
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
			Copy(Mesh.ArrayType.Normal);
			Copy(Mesh.ArrayType.TexUV);
			Copy(Mesh.ArrayType.TexUV2);
			Copy(Mesh.ArrayType.Index);
			if (arr[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.Nil)
			{
				var V = arr[(int)Mesh.ArrayType.Vertex].AsVector3Array();
				if (_lift > 1.0f) for (int k = 0; k < V.Length; k++) V[k] *= _lift;
				na[(int)Mesh.ArrayType.Vertex] = V;
			}
			if (arr[(int)Mesh.ArrayType.TexUV2].VariantType != Variant.Type.Nil && _forcedWidth > 0)
			{
				// 标定：把全部顶点 px 宽度强制成同一个值（用来验证"测量管线是否线性"）
				var uv2 = arr[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
				for (int k = 0; k < uv2.Length; k++) uv2[k] = new Vector2(_forcedWidth, uv2[k].Y);
				na[(int)Mesh.ArrayType.TexUV2] = uv2;
			}
			if (arr[(int)Mesh.ArrayType.Color].VariantType != Variant.Type.Nil)
			{
				var col = arr[(int)Mesh.ArrayType.Color].AsColorArray();
				for (int k = 0; k < col.Length; k++) col[k] = new Color(1f, 0f, 1f, 1f);
				na[(int)Mesh.ArrayType.Color] = col;
			}
			dst.AddSurfaceFromArrays(src.SurfaceGetPrimitiveType(i), na);
		}
		_hl.Mesh = dst;
	}

	// ★洋红探针（R=1,G=0,B=1）覆盖背景 bg 时：fg_c = α·P_c + (1−α)·bg_c ⇒ α = (fg_c − bg_c)/(P_c − bg_c)。
	//   三个通道的分母不同：R ⇒ 1−bg.R（近白/雪地失效）、G ⇒ −bg.G（深色海洋失效）、B ⇒ 1−bg.B（蓝天碧海失效）。
	//   ⇒ **取 |分母| 最大的通道**。
	//   ⚠️ RiverShotDiag 用的单通道公式 α=[(fg.R−fg.G)−(bg.R−bg.G)]/[1−(bg.R−bg.G)] 在偏红地形上分母趋零，
	//      实测把 α 放大到几十 ⇒ 墨迹质心被拖飞 733px。这里必须按通道择优。
	static float AlphaAt(byte[] fg, byte[] bg, int i)
	{
		float rF = fg[i] / 255f, gF = fg[i + 1] / 255f, bF = fg[i + 2] / 255f;
		float rB = bg[i] / 255f, gB = bg[i + 1] / 255f, bB = bg[i + 2] / 255f;
		float den = 1f - rB, val = rF - rB;               // 默认走 R 通道
		if (Math.Abs(-gB) > Math.Abs(den)) { den = -gB; val = gF - gB; }
		if (Math.Abs(1f - bB) > Math.Abs(den)) { den = 1f - bB; val = bF - bB; }
		if (Math.Abs(den) < 0.05f) return 0f;             // 三通道都不可用 ⇒ 判为无覆盖
		float a = val / den;
		return a > 0f ? MathF.Min(a, 1f) : 0f;            // α 是覆盖率，不可能 >1
	}

	static int BppOf(Image img) => img.GetFormat() switch { Image.Format.Rgba8 => 4, Image.Format.Rgb8 => 3, _ => 0 };

	// 洋红墨水 = Σ α
	double Ink(Image fgImg, Image bgImg)
	{
		int w = fgImg.GetWidth(), h = fgImg.GetHeight();
		if (bgImg.GetWidth() != w || bgImg.GetHeight() != h) return -1;
		int bpp = BppOf(fgImg);
		if (bpp == 0) return -1;
		byte[] fg = fgImg.GetData(), bg = bgImg.GetData();
		double ink = 0;
		for (int p = 0, n = w * h; p < n; p++) ink += AlphaAt(fg, bg, p * bpp);
		return ink;
	}

	void Analyze(Image fgImg, Image bgImg, int mi)
	{
		if (_hl?.Mesh is not ArrayMesh mesh || _cam == null) return;
		float d = _cam.GlobalPosition.Length();
		if (d < 1e-6f) return;
		var chat = _cam.GlobalPosition / d;
		float cosVis = R / d;

		var V = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
		int m = V.Length / 2;                     // 每角点 emit 内外两个顶点

		// 角点方向（取偶数下标；lift 刻度不影响方向）
		var dirs = new Vector3[m];
		for (int k = 0; k < m; k++) dirs[k] = V[2 * k].Normalized();

		// 投影：真实地形边界（R）与当前渲染位置（R×lift）
		var sp = new Vector2[m];
		var vis = new bool[m];
		for (int k = 0; k < m; k++)
		{
			sp[k] = _cam.UnprojectPosition(dirs[k] * R);
			vis[k] = dirs[k].Dot(chat) > cosVis && _cam.IsPositionInFrustum(dirs[k] * R);
		}

		// 逐边长度（只算两端都可见的边）
		var len = new double[m];
		double totalLen = 0, visLen = 0;
		for (int k = 0; k < m; k++)
		{
			int k2 = (k + 1) % m;
			len[k] = sp[k].DistanceTo(sp[k2]);
			totalLen += len[k];
			if (vis[k] && vis[k2]) visLen += len[k];
		}
		if (visLen < 20.0)
		{
			GD.Print($"  [CELL-HL {Tag(mi)}] 跳过：可见边长 {visLen:F1}px 过短（格子被遮挡或过于掠射）");
			return;
		}

		int bpp = BppOf(fgImg);
		if (bpp == 0) { GD.Print("  [CELL-HL] 未知图像格式"); return; }
		int W = fgImg.GetWidth(), H = fgImg.GetHeight();
		byte[] fg = fgImg.GetData(), bg = bgImg.GetData();

		// 只在格子外接盒（外扩 12px）里扫描 ⇒ O(格面积) 而非 O(全屏)
		float x0 = sp[0].X, x1 = sp[0].X, y0 = sp[0].Y, y1 = sp[0].Y;
		for (int k = 1; k < m; k++)
		{
			x0 = MathF.Min(x0, sp[k].X); x1 = MathF.Max(x1, sp[k].X);
			y0 = MathF.Min(y0, sp[k].Y); y1 = MathF.Max(y1, sp[k].Y);
		}
		int pad = 14;
		int ix0 = Math.Max(0, (int)(x0 - pad)), ix1 = Math.Min(W - 1, (int)(x1 + pad));
		int iy0 = Math.Max(0, (int)(y0 - pad)), iy1 = Math.Min(H - 1, (int)(y1 + pad));

		var inkEdge = new double[m];
		double ink = 0, cx = 0, cy = 0;
		double off = 0, nOff = 0;      // 带符号离界距离（×α 加权）

		// 屏幕空间"朝外"方向的参考点：可见角点的平均
		var polyCenter = Vector2.Zero;
		int nc = 0;
		for (int k = 0; k < m; k++) if (vis[k]) { polyCenter += sp[k]; nc++; }
		polyCenter /= Math.Max(nc, 1);
		for (int y = iy0; y <= iy1; y++)
		{
			for (int x = ix0; x <= ix1; x++)
			{
				int i = (y * W + x) * bpp;
				float a = AlphaAt(fg, bg, i);
				if (a <= 0.004f) continue;

				// ★用**像素中心** x+0.5/y+0.5：直接用整数坐标会引入 (−0.5,−0.5) 常量偏置，
				//   实测质心偏移稳定在 0.707px（= √(0.5²+0.5²)）——不是真偏移。
				ink += a; cx += a * (x + 0.5f); cy += a * (y + 0.5f);
				// 归到最近的边（沿边参数裁剪到段内），并累计**带符号**离界距离
				int best = -1; double bd = double.MaxValue;
				var p = new Vector2(x + 0.5f, y + 0.5f);
				for (int k = 0; k < m; k++)
				{
					int k2 = (k + 1) % m;
					if (!vis[k] || !vis[k2]) continue;
					double dd = DistToSegment(p, sp[k], sp[k2]);
					if (dd < bd) { bd = dd; best = k; }
				}
				if (best >= 0)
				{
					inkEdge[best] += a;
					off += a * SignedOffset(p, sp[best], sp[(best + 1) % m], polyCenter);
					nOff += a;
				}
			}
		}
		if (ink <= 0) { GD.Print($"  [CELL-HL {Tag(mi)}] ⚠️ 墨水=0：环带没画出来（shader 缺失或被地形挡住）"); return; }

		// ★横截面不是硬边：|cross|<u_core 实心，core→1 是抗锯齿淡出 ⇒ 平均 α = (1+u_core)/2。
		//   不除掉这个系数会把真实几何宽度系统性低估 ~7.5%（实测 3.0px 目标只量到 2.74px）。
		float core = _hl.MaterialOverride is ShaderMaterial sm
			? sm.GetShaderParameter("u_core").As<float>() : 0.85f;
		double soft = (1.0 + core) / 2.0;

		// 逐边宽度（③）：无缺口 = 每桶 > 0；不过宽过窄 = 离散度小
		double wMin = double.MaxValue, wMax = 0; int zero = 0;
		for (int k = 0; k < m; k++)
		{
			if (!vis[k] || !vis[(k + 1) % m]) continue;
			double wk = inkEdge[k] / len[k] / soft;
			if (wk <= 1e-6) { zero++; continue; }
			wMin = Math.Min(wMin, wk); wMax = Math.Max(wMax, wk);
		}

		// ① 质心偏移：墨迹质心 vs 真实地形格多边形质心（有抬升 ⇒ 沿屏幕径向外移）
		var poly = PolyCentroid(sp);
		var inkC = new Vector2((float)(cx / ink), (float)(cy / ink));

		// ② unproject 口径：真实地表位置 vs 旧方案渲染位置（**恒定用 ControlLift 做对照**，
		//    与当前是否真的抬升无关 ⇒ 单次运行就能同时给出"新方案实测"与"旧方案会有多歪"）
		var par = new List<double>();
		for (int k = 0; k < m; k++)
		{
			if (!vis[k]) continue;
			var onSurf = dirs[k] * R;
			var lifted = dirs[k] * (R * ControlLift);
			if (!_cam.IsPositionInFrustum(lifted)) continue;
			par.Add(_cam.UnprojectPosition(onSurf).DistanceTo(_cam.UnprojectPosition(lifted)));
		}
		par.Sort();
		double parMed = par.Count > 0 ? par[par.Count / 2] : 0;

		double width = ink / visLen / soft;
		double spread = wMax > 0 ? (wMax - wMin) / wMax * 100.0 : 0;
		double screenR = new Vector2(W / 2f, H / 2f).DistanceTo(inkC);
		double offMean = nOff > 0 ? off / nOff : 0;   // ④ 轮廓是否始终贴着同一条地形边界
		// ⚠️ 格子在屏幕上太小 ⇒ 六条边的墨水互相重叠，"宽"会被系统性低估（不是真实缺陷）
		double minEdge = double.MaxValue;
		for (int k = 0; k < m; k++) if (vis[k] && vis[(k + 1) % m]) minEdge = Math.Min(minEdge, len[k]);
		bool squashed = minEdge < 4.0 * width;

		if (mi == 0)
		{
			var vpSize = GetViewport().GetVisibleRect().Size;
			GD.Print($"  [CELL-HL {Tag(mi)}] SPACE img={W}×{H} vp={vpSize.X}×{vpSize.Y} " +
					 $"poly={poly:F1} inkC={inkC:F1}（坐标系自检：两者应重合）");
		}
		GD.Print($"  [CELL-HL {Tag(mi)}] 宽={width:F2}px 边[min={wMin:F2} max={wMax:F2} 离散={spread:F1}% 缺口={zero}]" +
				 $" | 质心偏移={inkC.DistanceTo(poly):F3}px 离界={offMean:F3}px | 旧lift({ControlLift:F5})视差={parMed:F4}px" +
				 $" | 屏幕半径={screenR:F0}px 边长={visLen:F1}/{totalLen:F1}px 最短边={minEdge:F1}px" +
				 (squashed ? " ⚠️格太小⇒宽度低估" : ""));
	}

	static string Tag(int mi) => $"{Tiers[mi / Fracs.Length]:F2}R·屏{Fracs[mi % Fracs.Length]:F2}";

	/// <summary>点到边的**带符号**距离，法线统一取"背离多边形中心"= 朝外。
	/// ★这才是"轮廓是否贴着真实地形边界"的判别量：笔画跨在边界上时正负对称 ⇒ 均值 0；
	///   被径向抬升后整体外移 ⇒ 均值 = 位移量。（质心指标对径向缩放不敏感，不可用。）</summary>
	static double SignedOffset(Vector2 p, Vector2 a, Vector2 b, Vector2 center)
	{
		var e = b - a;
		float L = e.Length();
		if (L < 1e-6f) return 0;
		var n = new Vector2(-e.Y, e.X) / L;
		var mid = (a + b) * 0.5f;
		if (n.Dot(center - mid) > 0f) n = -n;      // 朝内 ⇒ 翻向朝外
		return (p - a).Dot(n);
	}

	static double DistToSegment(Vector2 p, Vector2 a, Vector2 b)
	{
		var ab = b - a;
		float L2 = ab.LengthSquared();
		float t = L2 > 1e-9f ? Math.Clamp((p - a).Dot(ab) / L2, 0f, 1f) : 0f;
		return p.DistanceTo(a + ab * t);
	}

	static Vector2 PolyCentroid(Vector2[] p)
	{
		int m = p.Length;
		double A = 0, cx = 0, cy = 0;
		for (int i = 0; i < m; i++)
		{
			int j = (i + 1) % m;
			double cross = p[i].X * p[j].Y - p[j].X * p[i].Y;
			A += cross;
			cx += (p[i].X + p[j].X) * cross;
			cy += (p[i].Y + p[j].Y) * cross;
		}
		// ⚠️ A 累加的是 **2×面积**（shoelace 未折半）⇒ 必须先 *0.5 再代 1/(6A) 公式。
		//    漏了这步实测会让质心整体变成真实值的一半（偏移恒定 733px 就是这个 bug）。
		A *= 0.5;
		if (Math.Abs(A) < 1e-6)   // 退化（近乎掠射 ⇒ 投影面积≈0）⇒ 退回顶点平均
		{
			float sx = 0, sy = 0;
			foreach (var q in p) { sx += q.X; sy += q.Y; }
			return new Vector2(sx / m, sy / m);
		}
		return new Vector2((float)(cx / (6 * A)), (float)(cy / (6 * A)));
	}
}
