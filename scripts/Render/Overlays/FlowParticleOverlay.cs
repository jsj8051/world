using System;
using System.Collections.Generic;
using Godot;
using World.H3Grid;
using World.Logic;
using World.Utils;

namespace World.Render;

/// <summary>
/// 预积分流线 + GPU 沿程动画的公共实现：方向场平滑、球面分桶寻格、RK4 积分、ribbon 网格与彗星 LUT。
/// 子类只声明"参数多大、每格画什么颜色、何时构建"；场本身由子类从世界事实取出后交给 <see cref="BuildField"/>。
/// 与具体场解耦：本类不认识洋流/风场，因此新增同类图层不需要复制积分与网格代码。
/// </summary>
internal abstract partial class FlowParticleOverlay : MeshInstance3D
{
	// ── 共用标定（两族图层必须一致；各层差异放进虚属性）──────────────────────
	protected const float StepCells = 0.4f;              // 积分步长 = 邻格中心距 × 此系数（res4 约 18.6 km）
	protected const float MaxHalfArcRad = 0.25f;         // 单向最大弧长（rad）：双向合计 ≤ 0.5 rad
	protected const float MinLineArcRad = 0.02f;         // 短于此弧长的线不画
	protected const int MaxTraceSteps = 260;
	protected const float MinTraceSpeed = 0.22f;         // 低于此流速视为无场（积分到边界）
	protected const float TailScaleReference = 0.7f;     // 近景尾长压缩参考缩放
	protected const float TailScaleMax = 30f;
	protected const float DepthBias = 6.0e-5f;           // NDC 深度偏移：比河流的 1e-4 小，让河线压在最上层
	protected const int CometLutWidth = 512;
	protected const int CometLutVariants = 4;
	protected const string ShaderPath = "res://shaders/flow_particles.gdshader";

	/// <summary>线头/线尾淡出区宽度（占线长比例）：两端各 7% 内收到全透明。</summary>
	const float EndTaperWidth = 0.07f;

	// ── 场与几何 ──
	protected Vector3[] CellDirs;
	protected Vector3[] SmoothDirs;
	protected float[] Speed;
	protected bool[] Land;
	protected int[][] Neighbors;
	protected float Radius;
	protected float GridArc;
	NearestCellIndex _nearestCells;   // 球面最近格（分桶）：高频采样用，构造后只读
	ShaderMaterial _material;
	Vector2 _lastViewport;
	float _lastTailScale = -1f;
	float _flowTime;
	float _rateScale = 1f;
	readonly Random _rng = new(42);

	// ── 子类参数（差异全在这里）────────────────────────────────────────────
	protected abstract int SeedCount { get; }
	protected abstract float MinSpeedMul { get; }
	protected abstract float FilledSpeed { get; }
	protected abstract float MinSeedSpeed { get; }
	protected abstract float BaseWidthPx { get; }
	protected abstract float WidthPerStrengthPx { get; }
	protected abstract float CycleU { get; }
	protected abstract float CometRate { get; }
	protected abstract string LogTag { get; }

	/// <summary>LUT 每行的占空比 = 基值 + 行号 × 步进（各层头尾比例不同）。</summary>
	protected abstract float LutDutyBase { get; }
	protected abstract float LutDutyStep { get; }
	protected abstract float LutPowerBase { get; }
	protected abstract float LutPowerStep { get; }
	protected abstract float LutTailAmplitude { get; }

	/// <summary>顶点颜色。strength 已按 <see cref="MinSpeedMul"/> 归一化。</summary>
	protected abstract Color ColorAt(int cell, float strength);

	/// <summary>ribbon 顶点宽度（px）。默认按同一 strength 线性加宽。</summary>
	protected virtual float WidthAt(int cell, float strength) => BaseWidthPx + WidthPerStrengthPx * strength;

	/// <summary>材质通用参数就绪后的额外开关（如底色强度）。</summary>
	protected virtual void ConfigureMaterial(ShaderMaterial material) { }

	/// <summary>每帧先于时间积分调用；后台构建类的图层在这里收尾。</summary>
	protected virtual void OnProcessing(double delta) { }

	/// <summary>世界事实换了：取出网格、积分尺度与最近格索引（球壳/邻接/半径只在这里写一次）。</summary>
	protected void PrepareGrid(Ball ball)
	{
		CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		CellDirs = ball.CellDirs;
		Neighbors = ball.CellNeighbors;
		Radius = ball.Radius;
		GridArc = (float)(SpatialScale.Of(ball).NeighborCenterKm / SphericalFbmNoise.WorldRadiusKm);
		_nearestCells = new NearestCellIndex(ball);
	}

	public void UpdateCamera(Camera3D camera)
	{
		if (_material == null || camera == null || Radius <= 0f) return;

		var viewport = camera.GetViewport();
		if (viewport != null)
		{
			var size = viewport.GetVisibleRect().Size;
			if (size != _lastViewport && size.X > 0f && size.Y > 0f)
			{
				_lastViewport = size;
				_material.SetShaderParameter("u_viewport", size);
			}
		}

		float zoom = Mathf.Clamp(camera.GlobalPosition.DistanceTo(GlobalTransform.Origin) / Radius, 1.02f, 8f);
		float tailScale = Mathf.Clamp(TailScaleReference / Mathf.Max(zoom - 1f, 0.02f), 1f, TailScaleMax);
		if (Math.Abs(tailScale - _lastTailScale) > 0.01f)
		{
			_lastTailScale = tailScale;
			_material.SetShaderParameter("u_tail_scale", tailScale);
		}
		// 屏幕速度 = 世界速度 × px/rad × rateScale；px/rad ∝ 1/(zoom−1)，所以 rateScale 同步反比
		// 才能让贴近地表的彗星不飞成一片。时间在 _Process 里积分，rateScale 变化不跳相位。
		_rateScale = Mathf.Clamp((zoom - 1f) / 0.7f, 0.03f, 1f);
	}

	public override void _Process(double delta)
	{
		OnProcessing(delta);
		if (!Visible || _material == null) return;
		_flowTime += (float)delta * _rateScale;
		_material.SetShaderParameter("u_time", _flowTime);
	}

	// ── 1. 方向场 / 速度场 ────────────────────────────────────────────────

	protected void BuildField(Vector3[] raw, float[] strength)
	{
		int n = CellDirs.Length;

		// 两遍 1 环均值：一遍只抹掉一格尺度的抖动，两遍才够让流线不沿格边抽搐。
		var source = raw;
		for (int pass = 0; pass < 2; pass++)
		{
			var target = new Vector3[n];
			for (int i = 0; i < n; i++)
			{
				if (Land[i] || source[i].LengthSquared() < 1e-9f) continue;

				var sum = source[i];
				int weight = 1;
				foreach (int nb in Neighbors[i])
				{
					if (source[nb].LengthSquared() < 1e-9f) continue;
					sum += source[nb];
					weight++;
				}
				var mean = sum / weight;
				mean -= CellDirs[i] * mean.Dot(CellDirs[i]);
				if (mean.LengthSquared() > 0.0036f) target[i] = mean.Normalized();
			}
			source = target;
		}
		SmoothDirs = source;

		var coherent = new bool[n];
		for (int i = 0; i < n; i++) coherent[i] = SmoothDirs[i].LengthSquared() > 1e-9f;

		// 涡心 / 弱场区没有源方向：从邻居逐轮扩散出方向，只用来把流线导出去，不给流速。
		for (int pass = 1; pass <= 24; pass++)
		{
			bool changed = false;
			for (int i = 0; i < n; i++)
			{
				if (Land[i] || SmoothDirs[i].LengthSquared() > 1e-9f) continue;

				var sum = Vector3.Zero;
				int count = 0;
				foreach (int nb in Neighbors[i])
					if (SmoothDirs[nb].LengthSquared() > 1e-9f) { sum += SmoothDirs[nb]; count++; }

				if (count < 2) continue;
				var mean = sum / count;
				mean -= CellDirs[i] * mean.Dot(CellDirs[i]);
				if (mean.LengthSquared() < 0.04f * count * count) continue;   // 邻居方向互相抵消 ⇒ 保持无方向

				SmoothDirs[i] = mean.Normalized();
				changed = true;
			}
			if (!changed) break;
		}

		float maxStrength = 1e-6f;
		if (strength != null) foreach (float s in strength) if (s > maxStrength) maxStrength = s;

		Speed = new float[n];
		for (int i = 0; i < n; i++)
		{
			if (Land[i] || !coherent[i] || strength == null || strength[i] <= 1e-5f)
			{
				Speed[i] = FilledSpeed;
				continue;
			}
			float norm = Mathf.Clamp(strength[i] / maxStrength, 0f, 1f);
			Speed[i] = MinSpeedMul + (1f - MinSpeedMul) * norm;
		}
	}

	/// <summary>该点的方向 / 流速：最近格 + 1 环 cos⁴ 加权；相干性过低视为无场。</summary>
	bool TrySample(Vector3 p, ref int cell, out Vector3 dir, out float speed)
	{
		dir = Vector3.Zero;
		speed = 0f;
		if (cell < 0) cell = _nearestCells.Nearest(p);
		else cell = _nearestCells.Refine(p, cell, GridArc * 1.5f);

		if (cell < 0 || Land[cell]) return false;

		var sum = Vector3.Zero;
		float weightSum = 0f;
		float speedSum = 0f;

		void Blend(int c)
		{
			var d = SmoothDirs[c];
			if (d.LengthSquared() < 1e-9f) return;
			float w = MathOps.CosFourthWeight(CellDirs[c], p);
			sum += d * w;
			speedSum += Speed[c] * w;
			weightSum += w;
		}

		Blend(cell);
		foreach (int nb in Neighbors[cell]) Blend(nb);

		if (!(weightSum > 1e-6f)) return false;
		speed = speedSum / weightSum;
		sum -= p * sum.Dot(p);
		if (sum.LengthSquared() < 0.0625f * weightSum * weightSum) return false;
		dir = sum.Normalized();
		return speed >= MinTraceSpeed;
	}

	// ── 3. 种子 / 流线积分 ────────────────────────────────────────────────

	List<int> BuildSeeds()
	{
		var seeds = new List<int>(SeedCount);
		var used = new bool[CellDirs.Length];
		double golden = Math.PI * (3.0 - Math.Sqrt(5.0));

		for (int k = 0; k < SeedCount && seeds.Count < SeedCount; k++)
		{
			float y = 1f - 2f * ((k + 0.5f) / SeedCount);
			float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
			float phi = (float)(k * golden);
			var p = new Vector3(Mathf.Cos(phi) * radius, y, Mathf.Sin(phi) * radius);

			// 抖动小于半格：打散 Fibonacci 的规则感，又不把种子推进邻格。
			p = (p + RandomTangent(p, 0.35f * GridArc)).Normalized();
			int cell = NearestSeedCell(p);
			if (cell < 0 || used[cell]) continue;

			used[cell] = true;
			seeds.Add(cell);
		}
		return seeds;
	}

	Vector3 RandomTangent(Vector3 p, float scale)
	{
		var axis = Mathf.Abs(p.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		var t1 = p.Cross(axis).Normalized();
		var t2 = p.Cross(t1).Normalized();
		float angle = (float)_rng.NextDouble() * Mathf.Tau;
		return (t1 * Mathf.Cos(angle) + t2 * Mathf.Sin(angle)) * scale;
	}

	/// <summary>种子吸附：最近格不可用时取 1 环内最近的可撒点格；无可用格返回 −1。</summary>
	int NearestSeedCell(Vector3 p)
	{
		int cell = _nearestCells.Nearest(p);
		if (cell < 0) return -1;
		if (IsSeedable(cell)) return cell;

		int best = -1;
		float bestDistance = float.MaxValue;
		foreach (int nb in Neighbors[cell])
		{
			if (!IsSeedable(nb)) continue;
			float distance = (CellDirs[nb] - p).LengthSquared();
			if (distance < bestDistance) { bestDistance = distance; best = nb; }
		}
		return best;
	}

	protected bool IsSeedable(int cell) =>
		!Land[cell] && SmoothDirs[cell].LengthSquared() > 1e-9f && Speed[cell] >= MinSeedSpeed;

	protected List<FlowLine> BuildLines()
	{
		var seeds = BuildSeeds();
		var lines = new List<FlowLine>(seeds.Count);

		foreach (int seed in seeds)
		{
			int cell = seed;
			var start = CellDirs[seed];
			if (!TrySample(start, ref cell, out _, out _)) continue;

			var downstream = Trace(start, seed, +1f);
			var upstream = Trace(start, seed, -1f);
			if (downstream.Count + upstream.Count < 6) continue;

			var pts = new List<TracedPoint>(upstream.Count + downstream.Count);
			for (int i = upstream.Count - 1; i >= 1; i--) pts.Add(upstream[i]);
			pts.AddRange(downstream);

			var line = BuildLine(pts);
			if (line != null) lines.Add(line);
		}
		return lines;
	}

	/// <summary>方向场积分（RK4，球面归一化）；<paramref name="directionSign"/> = −1 时逆流上行。</summary>
	List<TracedPoint> Trace(Vector3 start, int startCell, float directionSign)
	{
		var points = new List<TracedPoint>(MaxTraceSteps) { new(start, startCell, Speed[startCell]) };
		int cell = startCell;
		var p = start;
		float arc = 0f;
		float stepLength = GridArc * StepCells;

		for (int step = 0; step < MaxTraceSteps && arc < MaxHalfArcRad; step++)
		{
			if (!TrySample(p, ref cell, out var k1, out _)) break;
			k1 *= directionSign;

			if (!TrySample((p + k1 * (stepLength * 0.5f)).Normalized(), ref cell, out var k2, out _)) break;
			k2 *= directionSign;

			if (!TrySample((p + k2 * (stepLength * 0.5f)).Normalized(), ref cell, out var k3, out _)) break;
			k3 *= directionSign;

			if (!TrySample((p + k3 * stepLength).Normalized(), ref cell, out var k4, out _)) break;
			k4 *= directionSign;

			var tangent = (k1 + 2f * k2 + 2f * k3 + k4) / 6f;
			var next = (p + tangent * stepLength).Normalized();
			if (!TrySample(next, ref cell, out _, out var speed)) break;

			arc += stepLength;
			points.Add(new TracedPoint(next, cell, speed));
			p = next;

			// 近似闭合：回到起点且已拉开足够弧长 ⇒ 不再绕第二圈。
			if (points.Count > 3 && arc > 0.05f && next.Dot(start) > 0.99995f) break;
		}
		return points;
	}

	FlowLine BuildLine(List<TracedPoint> src)
	{
		int n = src.Count;
		if (n < 4) return null;

		var pts = new Vector3[n];
		var cells = new int[n];
		var travel = new float[n];
		var colors = new Color[n];
		var widths = new float[n];
		var fade = new float[n];

		float arc = 0f;
		float u = 0f;
		pts[0] = src[0].P;
		cells[0] = src[0].Cell;

		for (int i = 1; i < n; i++)
		{
			pts[i] = src[i].P;
			cells[i] = src[i].Cell;
			float step = (pts[i] - pts[i - 1]).Length();
			arc += step;
			u += step / Mathf.Max(src[i - 1].Speed, 0.05f);
			travel[i] = u;
		}
		if (arc < MinLineArcRad) return null;

		for (int i = 0; i < n; i++)
		{
			float strength = Mathf.Clamp((src[i].Speed - MinSpeedMul) / (1f - MinSpeedMul), 0f, 1f);
			colors[i] = ColorAt(cells[i], strength);
			widths[i] = WidthAt(cells[i], strength);
			fade[i] = MathOps.EndTaper(i / (n - 1f), EndTaperWidth);
		}

		return new FlowLine
		{
			Pts = pts,
			Cells = cells,
			U = travel,
			Colors = colors,
			Widths = widths,
			Fade = fade,
			Phase = (float)_rng.NextDouble(),
		};
	}

	// ── 4. 静态 ribbon 网格 ───────────────────────────────────────────────

	protected ArrayMesh BuildMesh(List<FlowLine> lines)
	{
		int pointCount = 0;
		int indexCount = 0;
		foreach (var line in lines)
		{
			pointCount += line.Pts.Length;
			indexCount += (line.Pts.Length - 1) * 6;
		}

		var verts = new Vector3[pointCount * 2];
		var normals = new Vector3[pointCount * 2];
		var uvs = new Vector2[pointCount * 2];
		var uv2s = new Vector2[pointCount * 2];
		var colors = new Color[pointCount * 2];
		var indices = new int[indexCount];

		int v = 0, idx = 0;
		foreach (var line in lines)
		{
			int n = line.Pts.Length;
			int baseVertex = v;

			for (int i = 0; i < n; i++)
			{
				var position = line.Pts[i] * Radius;
				var side = SideAt(line.Pts, i);
				var color = new Color(line.Colors[i].R, line.Colors[i].G, line.Colors[i].B, line.Fade[i]);
				var uv2 = new Vector2(line.Widths[i], line.Phase);

				verts[v] = position;
				normals[v] = side;
				uvs[v] = new Vector2(line.U[i], -1f);
				uv2s[v] = uv2;
				colors[v] = color;
				v++;

				verts[v] = position;
				normals[v] = side;
				uvs[v] = new Vector2(line.U[i], 1f);
				uv2s[v] = uv2;
				colors[v] = color;
				v++;
			}

			idx = SphericalRibbon.AppendQuadStrip(indices, idx, baseVertex, n);
		}

		var mesh = new ArrayMesh();
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = verts;
		arrays[(int)Mesh.ArrayType.Normal] = normals;
		arrays[(int)Mesh.ArrayType.TexUV] = uvs;
		arrays[(int)Mesh.ArrayType.TexUV2] = uv2s;
		arrays[(int)Mesh.ArrayType.Color] = colors;
		arrays[(int)Mesh.ArrayType.Index] = indices;
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	/// <summary>采样点 i 处的球面横向单位向量（ribbon 加宽方向）。</summary>
	static Vector3 SideAt(Vector3[] pts, int i)
	{
		var side = SphericalRibbon.SideAt(pts, i, out bool degenerate);
		if (!degenerate) return side;

		var p = pts[i];
		var axis = Mathf.Abs(p.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		var fallback = p.Cross(axis);
		return fallback.LengthSquared() > 1e-12f ? fallback.Normalized() : Vector3.Zero;
	}

	// ── 5. 材质 / 彗星 LUT ────────────────────────────────────────────────

	protected bool EnsureMaterial()
	{
		if (_material != null) return true;
		if (!ShaderMaterialFactory.TryCreate(ShaderPath, LogTag, out _material)) return false;

		_material.SetShaderParameter("u_viewport", new Vector2(1920f, 1080f));
		_material.SetShaderParameter("u_cycle", CycleU);
		_material.SetShaderParameter("u_rate", CometRate);
		_material.SetShaderParameter("u_tail_scale", 1.0f);
		_material.SetShaderParameter("u_time", 0.0f);
		_material.SetShaderParameter("u_depth_bias", DepthBias);
		_material.SetShaderParameter("u_comet_tex", BuildCometTexture());
		ConfigureMaterial(_material);
		return true;
	}

	/// <summary>装好网格与材质（各层构建收尾统一走这里，避免重复赋值顺序）。</summary>
	protected void ApplyMesh(ArrayMesh mesh)
	{
		Mesh = mesh;
		MaterialOverride = _material;
	}

	/// <summary>512×4 的剖面 LUT：p=0 是亮头，随后衰减成尾，duty 之后是暗间隙。</summary>
	ImageTexture BuildCometTexture()
	{
		int w = CometLutWidth, h = CometLutVariants;
		var bytes = new byte[w * h];

		for (int row = 0; row < h; row++)
		{
			float duty = LutDutyBase + LutDutyStep * row;
			float power = LutPowerBase + LutPowerStep * row;

			for (int x = 0; x < w; x++)
			{
				float p = x / (float)w;
				float value = 0f;
				if (p < duty)
				{
					float t = p / duty;
					float head = Mathf.Exp(-t * t * 120f);
					float tail = Mathf.Pow(1f - t, power) * LutTailAmplitude;
					float grain = 0.86f + 0.14f * Mathf.Sin(t * 33f + row * 1.9f);
					value = Mathf.Max(head, tail * grain);
				}
				bytes[row * w + x] = (byte)Math.Round(Mathf.Clamp(value, 0f, 1f) * 255f);
			}
		}

		var image = Image.CreateFromData(w, h, false, Image.Format.R8, bytes);
		return ImageTexture.CreateFromImage(image);
	}

	// ── 6. 数据结构 ───────────────────────────────────────────────────────

	protected readonly struct TracedPoint
	{
		public readonly Vector3 P;
		public readonly int Cell;
		public readonly float Speed;

		public TracedPoint(Vector3 p, int cell, float speed)
		{
			P = p;
			Cell = cell;
			Speed = speed;
		}
	}

	/// <summary>一条流线。Cells 供"合并同路线"几何去重使用（不画的图层也照填，代价是一次 int[]）。</summary>
	protected sealed class FlowLine
	{
		public Vector3[] Pts;
		public int[] Cells;
		public float[] U;
		public Color[] Colors;
		public float[] Widths;
		public float[] Fade;
		public float Phase;
	}
}
