using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using World.H3Grid;
using World.Logic;
using World.Utils;

namespace World.Render;

/// <summary>
/// 风场粒子层：预积分风矢流线 + GPU 沿程动画。
/// 风是全局场（陆海都画），运行时只让 shader 沿 UV.x 播放短促的风丝，CPU 不做逐帧工作。
/// 视觉参考 cambecc/earth（MIT）的 wind map；这里是 Godot 流线粒子的重实现，不含其源码。
/// 屏幕空间等宽与 NDC 深度偏移沿用 `river_surface.gdshader` 的做法。
///
/// @example
/// <code>
/// var wind = new WindFlowOverlay { Name = "WindFlow" };
/// AddChild(wind);
/// wind.Build(facts, 0);                  // 0 = 年均；1..N = 月份
/// </code>
/// </summary>
public sealed partial class WindFlowOverlay : MeshInstance3D
{
	/// <summary>Fibonacci 球面种子数（风覆盖陆海，比洋流层更密）。</summary>
	const int SeedCount = 5200;

	/// <summary>积分步长 = 邻格中心距 × 此系数；0.4 在 res4 约 18.6 km。</summary>
	const float StepCells = 0.4f;

	/// <summary>单向最大弧长（rad）：双向合计 ≤ 0.5 rad。</summary>
	const float MaxHalfArcRad = 0.25f;

	/// <summary>短于此弧长的流线不画（静风区碎线）。</summary>
	const float MinLineArcRad = 0.02f;

	const int MaxTraceSteps = 260;

	/// <summary>风速归一后的流速下限：收窄快慢差，避免风丝长短参差。</summary>
	const float MinSpeedMul = 0.35f;

	/// <summary>扩散补出来的静风格只给极低流速：风丝可以收尾，但不会假装有风。</summary>
	const float FilledSpeed = 0.08f;

	const float MinTraceSpeed = 0.22f;
	const float MinSeedSpeed = 0.30f;

	/// <summary>风丝宽度（px）：比洋流更细，靠数量与速度造“流动的空气”。</summary>
	const float BaseWidthPx = 1.4f;
	const float WidthPerStrengthPx = 0.8f;

	/// <summary>风丝周期（行进时间 u）与速度（周期/秒）：长尾、快速、白色流线。</summary>
	const float CycleU = 0.28f;
	const float CometRate = 0.9f;

	/// <summary>近景尾长压缩：只改 LUT 可见尾长与时间积分，不改彗星相位。</summary>
	const float TailScaleReference = 0.7f;
	const float TailScaleMax = 30f;

	/// <summary>NDC 深度偏移（reversed-Z，`+=`）；比河流的 1e-4 略小，让河线保持在最上层。</summary>
	const float DepthBias = 6.0e-5f;

	const int CometLutWidth = 512;
	const int CometLutVariants = 4;
	const string ShaderPath = "res://shaders/flow_particles.gdshader";

	const int BucketLat = 128, BucketLon = 256;

	WorldFacts _facts;
	int _month = -1;
	WindKind _kind = WindKind.Surface;

	Vector3[] _cellDirs;
	Vector3[] _smoothDirs;
	float[] _speed;
	float[] _strength;
	bool[] _land;
	int[][] _neighbors;
	int[][] _buckets;
	float _radius;
	float _gridArc;

	ShaderMaterial _material;
	Vector2 _lastViewport;
	float _lastTailScale = -1f;
	float _flowTime;
	float _rateScale = 1f;

	readonly Random _rng = new(42);

	// 流线积分在后台线程做，主线程只组装网格；换月不再阻塞 UI。
	volatile bool _workerRunning;
	volatile bool _workerDone;
	bool _workerFailed;
	string _workerError;
	List<FlowLine> _workerLines;
	WorldFacts _buildingFacts;
	int _buildingMonth;
	WindKind _buildingKind;
	WorldFacts _pendingFacts;
	int _pendingMonth = -1;
	WindKind _pendingKind;
	System.Diagnostics.Stopwatch _buildWatch;

	// 换月 / 换风档只换网格：每对 (月份, 风档) 烘焙一次后缓存，重复切换不再重积。
	readonly Dictionary<(int Month, WindKind Kind), ArrayMesh> _meshCache = new();
	readonly Queue<(int Month, WindKind Kind)> _cacheOrder = new();
	WorldFacts _cacheFacts;

	/// <summary>按世界事实重装风丝（异步：流线积分在后台，主线程只组装网格）。
	/// 每个 (月份, 风档) 首次访问时烘焙并缓存，再次切换直接换 Mesh（O(1)）。
	/// <paramref name="kind"/> 必须与底色选的风档一致，否则风丝与底色不同源。</summary>
	public void Build(WorldFacts facts, int month, WindKind kind)
	{
		if (facts == null) throw new ArgumentNullException(nameof(facts));

		if (_cacheFacts != facts)
		{
			_meshCache.Clear();
			_cacheOrder.Clear();
			_cacheFacts = facts;
		}

		if (_workerRunning)
		{
			if (_buildingFacts == facts && _buildingMonth == month && _buildingKind == kind)
			{
				_pendingFacts = null;
				_pendingMonth = -1;
				return;
			}
			_pendingFacts = facts;
			_pendingMonth = month;
			_pendingKind = kind;
			return;
		}

		if (_facts == facts && _month == month && _kind == kind && Mesh != null) return;
		if (TryApplyCached(facts, month, kind)) return;
		StartBuild(facts, month, kind);
	}

	/// <summary>命中 (月份, 风档) 缓存：直接换网格，成本 O(1)。</summary>
	bool TryApplyCached(WorldFacts facts, int month, WindKind kind)
	{
		if (!_meshCache.TryGetValue((month, kind), out var mesh)) return false;
		if (!EnsureMaterial()) return false;

		Mesh = mesh;
		MaterialOverride = _material;
		_facts = facts;
		_month = month;
		_kind = kind;
		return true;
	}

	void CacheMesh(int month, WindKind kind, ArrayMesh mesh)
	{
		if (mesh == null || _buildingFacts != _cacheFacts || _meshCache.ContainsKey((month, kind))) return;

		int limit = Math.Max(4, 3 * ((_cacheFacts?.Wind.MonthCount ?? 0) + 1));
		while (_meshCache.Count >= limit && _cacheOrder.Count > 0)
			_meshCache.Remove(_cacheOrder.Dequeue());

		_meshCache[(month, kind)] = mesh;
		_cacheOrder.Enqueue((month, kind));
	}

	void StartBuild(WorldFacts facts, int month, WindKind kind)
	{
		if (TryApplyCached(facts, month, kind)) return;

		CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
		var ball = facts.Ball;
		_cellDirs = ball.CellDirs;
		_neighbors = ball.CellNeighbors;
		_radius = ball.Radius;

		var set = facts.Wind.Get(kind);
		var wind = month <= 0 ? set.Annual
			: month <= set.MonthCount ? set.Monthly[month - 1] : null;
		if (wind == null || wind.Length != _cellDirs.Length)
		{
			Mesh = null;
			_month = -1;
			_facts = null;
			return;
		}

		_buildingFacts = facts;
		_buildingMonth = month;
		_buildingKind = kind;
		_buildWatch = System.Diagnostics.Stopwatch.StartNew();

		double scale = SpatialScale.Of(ball).NeighborCenterKm / SphericalFbmNoise.WorldRadiusKm;
		_gridArc = (float)scale;

		_land = new bool[_cellDirs.Length];
		_strength = new float[_cellDirs.Length];
		var directions = new Vector3[_cellDirs.Length];
		for (int i = 0; i < wind.Length; i++)
		{
			float len = wind[i].Length();
			// 几何方向仍取物理风矢；速度/颜色用显示风速（与底色同一个取数口）。
			_strength[i] = Mathf.Clamp(set.SpeedAt(month, i), 0f, 1f);
			if (len > 1e-4f) directions[i] = wind[i] / len;
		}

		if (!EnsureMaterial())
		{
			Mesh = null;
			return;
		}

		_workerRunning = true;
		_workerDone = false;
		_workerFailed = false;
		_workerError = null;
		_workerLines = null;

		Task.Run(() =>
		{
			try
			{
				BuildField(directions, _strength);
				BuildBuckets();
				_workerLines = BuildLines();
			}
			catch (Exception e)
			{
				_workerFailed = true;
				_workerError = e.Message;
			}
			finally
			{
				_workerDone = true;
			}
		});
	}

	/// <summary>主线程收尾：把后台算好的流线装成网格；有挂起的换月则接着跑。</summary>
	void PumpBuild()
	{
		if (!_workerRunning || !_workerDone) return;

		_workerRunning = false;
		_workerDone = false;

		var lines = _workerLines;
		_workerLines = null;
		bool failed = _workerFailed;
		string error = _workerError;
		_workerFailed = false;
		_workerError = null;

		if (failed)
		{
			GD.PushError($"[WIND] build failed: {error}");
			Mesh = null;
		}
		else if (lines != null && lines.Count > 0)
		{
			int points = 0;
			foreach (var line in lines) points += line.Pts.Length;

			var built = BuildMesh(lines);
			Mesh = built;
			MaterialOverride = _material;
			_facts = _buildingFacts;
			_month = _buildingMonth;
			_kind = _buildingKind;
			CacheMesh(_month, _kind, built);
			GD.Print($"[WIND] month={_month} kind={_kind} lines={lines.Count} points={points} build={_buildWatch.ElapsedMilliseconds}ms");
		}
		else
		{
			Mesh = null;
		}

		if (_pendingMonth >= 0)
		{
			var facts = _pendingFacts;
			int month = _pendingMonth;
			WindKind kind = _pendingKind;
			_pendingFacts = null;
			_pendingMonth = -1;
			StartBuild(facts, month, kind);
		}
	}

	/// <summary>每帧喂 viewport 尺寸与近景尾长压缩（只写变化值）。</summary>
	public void UpdateCamera(Camera3D camera)
	{
		if (_material == null || camera == null || _radius <= 0f) return;

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

		float zoom = Mathf.Clamp(camera.GlobalPosition.DistanceTo(GlobalTransform.Origin) / _radius, 1.02f, 8f);
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
		PumpBuild();
		if (!Visible || _material == null) return;
		_flowTime += (float)delta * _rateScale;
		_material.SetShaderParameter("u_time", _flowTime);
	}

	// ── 1. 方向场 / 速度场 ────────────────────────────────────────────────

	void BuildField(Vector3[] raw, float[] strength)
	{
		int n = _cellDirs.Length;

		// 两遍 1 环均值：一遍只抹掉一格尺度的抖动，两遍才够让流线不沿格边抽搐。
		var source = raw;
		for (int pass = 0; pass < 2; pass++)
		{
			var target = new Vector3[n];
			for (int i = 0; i < n; i++)
			{
				if (_land[i] || source[i].LengthSquared() < 1e-9f) continue;

				var sum = source[i];
				int weight = 1;
				foreach (int nb in _neighbors[i])
				{
					if (source[nb].LengthSquared() < 1e-9f) continue;
					sum += source[nb];
					weight++;
				}
				var mean = sum / weight;
				mean -= _cellDirs[i] * mean.Dot(_cellDirs[i]);
				if (mean.LengthSquared() > 0.0036f) target[i] = mean.Normalized();
			}
			source = target;
		}
		_smoothDirs = source;

		var coherent = new bool[n];
		for (int i = 0; i < n; i++) coherent[i] = _smoothDirs[i].LengthSquared() > 1e-9f;

		// 静风 / 弱风区没有源方向：从邻居逐轮扩散出方向，只用来把风丝导出去，不给流速。
		for (int pass = 1; pass <= 24; pass++)
		{
			bool changed = false;
			for (int i = 0; i < n; i++)
			{
				if (_land[i] || _smoothDirs[i].LengthSquared() > 1e-9f) continue;

				var sum = Vector3.Zero;
				int count = 0;
				foreach (int nb in _neighbors[i])
					if (_smoothDirs[nb].LengthSquared() > 1e-9f) { sum += _smoothDirs[nb]; count++; }

				if (count < 2) continue;
				var mean = sum / count;
				mean -= _cellDirs[i] * mean.Dot(_cellDirs[i]);
				if (mean.LengthSquared() < 0.04f * count * count) continue;   // 邻居方向互相抵消 ⇒ 保持无方向

				_smoothDirs[i] = mean.Normalized();
				changed = true;
			}
			if (!changed) break;
		}

		float maxStrength = 1e-6f;
		if (strength != null) foreach (float s in strength) if (s > maxStrength) maxStrength = s;

		_speed = new float[n];
		for (int i = 0; i < n; i++)
		{
			if (_land[i] || !coherent[i] || strength == null || strength[i] <= 1e-5f)
			{
				_speed[i] = FilledSpeed;
				continue;
			}
			float norm = Mathf.Clamp(strength[i] / maxStrength, 0f, 1f);
			_speed[i] = MinSpeedMul + (1f - MinSpeedMul) * norm;
		}
	}

	/// <summary>该点的风向 / 风速：最近格 + 1 环 cos⁴ 加权；相干性过低视为无风。</summary>
	bool TrySample(Vector3 p, ref int cell, out Vector3 dir, out float speed)
	{
		dir = Vector3.Zero;
		speed = 0f;
		if (cell < 0) cell = NearestCell(p);
		else cell = NearestAround(p, cell);
		if (cell < 0 || _land[cell]) return false;

		var sum = Vector3.Zero;
		float weightSum = 0f;
		float speedSum = 0f;

		void Blend(int c)
		{
			var d = _smoothDirs[c];
			if (d.LengthSquared() < 1e-9f) return;
			float cos = Mathf.Clamp(_cellDirs[c].Dot(p), -1f, 1f);
			float w = cos * cos;
			w *= w;
			sum += d * w;
			speedSum += _speed[c] * w;
			weightSum += w;
		}

		Blend(cell);
		foreach (int nb in _neighbors[cell]) Blend(nb);

		if (!(weightSum > 1e-6f)) return false;
		speed = speedSum / weightSum;
		sum -= p * sum.Dot(p);
		if (sum.LengthSquared() < 0.0625f * weightSum * weightSum) return false;
		dir = sum.Normalized();
		return speed >= MinTraceSpeed;
	}

	/// <summary>只查当前格 + 1 环：积分步长小于半格，最近格不会跳出 1 环。</summary>
	int NearestAround(Vector3 p, int cell)
	{
		int best = cell;
		float bestDistance = (_cellDirs[cell] - p).LengthSquared();
		foreach (int nb in _neighbors[cell])
		{
			float distance = (_cellDirs[nb] - p).LengthSquared();
			if (distance < bestDistance) { bestDistance = distance; best = nb; }
		}
		return bestDistance <= _gridArc * _gridArc * 2.25f ? best : NearestCell(p);
	}

	// ── 2. 球面分桶 / 最近格 ──────────────────────────────────────────────

	void BuildBuckets()
	{
		_buckets = new int[BucketLat * BucketLon][];
		var lists = new List<int>[BucketLat * BucketLon];
		for (int i = 0; i < lists.Length; i++) lists[i] = new List<int>(4);

		for (int i = 0; i < _cellDirs.Length; i++)
		{
			var (by, bx) = BucketOf(_cellDirs[i]);
			lists[by * BucketLon + bx].Add(i);
		}
		for (int i = 0; i < lists.Length; i++) _buckets[i] = lists[i].ToArray();
	}

	static (int by, int bx) BucketOf(Vector3 p)
	{
		float lat = Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f));
		float lon = Mathf.Atan2(p.Z, p.X);
		int by = Mathf.Clamp((int)((lat + Mathf.Pi / 2f) / Mathf.Pi * BucketLat), 0, BucketLat - 1);
		int bx = Mathf.Clamp((int)((lon + Mathf.Pi) / Mathf.Tau * BucketLon), 0, BucketLon - 1);
		return (by, bx);
	}

	int NearestCell(Vector3 p)
	{
		var (by, bx) = BucketOf(p);
		int best = -1;
		float bestDistance = float.MaxValue;

		for (int dy = -1; dy <= 1; dy++)
		{
			int y = (by + dy + BucketLat) % BucketLat;
			for (int dx = -1; dx <= 1; dx++)
			{
				int x = (bx + dx + BucketLon) % BucketLon;
				foreach (int cell in _buckets[y * BucketLon + x])
				{
					float distance = (_cellDirs[cell] - p).LengthSquared();
					if (distance < bestDistance) { bestDistance = distance; best = cell; }
				}
			}
		}
		return best;
	}

	// ── 3. 种子 / 风丝积分 ────────────────────────────────────────────────

	List<int> BuildSeeds()
	{
		var seeds = new List<int>(SeedCount);
		var used = new bool[_cellDirs.Length];
		double golden = Math.PI * (3.0 - Math.Sqrt(5.0));

		for (int k = 0; k < SeedCount && seeds.Count < SeedCount; k++)
		{
			float y = 1f - 2f * ((k + 0.5f) / SeedCount);
			float radius = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
			float phi = (float)(k * golden);
			var p = new Vector3(Mathf.Cos(phi) * radius, y, Mathf.Sin(phi) * radius);

			// 抖动小于半格：打散 Fibonacci 的规则感，又不把种子推进邻格。
			p = (p + RandomTangent(p, 0.35f * _gridArc)).Normalized();
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

	/// <summary>种子吸附：最近格不可用时取 1 环内最近的可撒点格；静风区返回 −1。</summary>
	int NearestSeedCell(Vector3 p)
	{
		int cell = NearestCell(p);
		if (cell < 0) return -1;
		if (IsSeedable(cell)) return cell;

		int best = -1;
		float bestDistance = float.MaxValue;
		foreach (int nb in _neighbors[cell])
		{
			if (!IsSeedable(nb)) continue;
			float distance = (_cellDirs[nb] - p).LengthSquared();
			if (distance < bestDistance) { bestDistance = distance; best = nb; }
		}
		return best;
	}

	bool IsSeedable(int cell) =>
		!_land[cell] && _smoothDirs[cell].LengthSquared() > 1e-9f && _speed[cell] >= MinSeedSpeed;

	List<FlowLine> BuildLines()
	{
		var seeds = BuildSeeds();
		var lines = new List<FlowLine>(seeds.Count);

		foreach (int seed in seeds)
		{
			int cell = seed;
			var start = _cellDirs[seed];
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
		var points = new List<TracedPoint>(MaxTraceSteps) { new(start, startCell, _speed[startCell]) };
		int cell = startCell;
		var p = start;
		float arc = 0f;
		float stepLength = _gridArc * StepCells;

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
		var travel = new float[n];
		var colors = new Color[n];
		var widths = new float[n];
		var fade = new float[n];

		float arc = 0f;
		float u = 0f;
		pts[0] = src[0].P;

		for (int i = 1; i < n; i++)
		{
			pts[i] = src[i].P;
			float step = (pts[i] - pts[i - 1]).Length();
			arc += step;
			u += step / Mathf.Max(src[i - 1].Speed, 0.05f);
			travel[i] = u;
		}
		if (arc < MinLineArcRad) return null;

		for (int i = 0; i < n; i++)
		{
			int cell = Math.Clamp(src[i].Cell, 0, _strength.Length - 1);
			float speed = Mathf.Clamp(_strength[cell], 0f, 1f);
			colors[i] = WindColor(speed);
			widths[i] = BaseWidthPx + WidthPerStrengthPx * speed;
			fade[i] = EndFade(i / (n - 1f));
		}

		return new FlowLine
		{
			Pts = pts,
			U = travel,
			Colors = colors,
			Widths = widths,
			Fade = fade,
			Phase = (float)_rng.NextDouble(),
		};
	}

	/// <summary>风速 → 近白色；强风更亮。速度本身由底图色带表达。</summary>
	static Color WindColor(float speed)
	{
		float s = Mathf.Clamp(speed, 0f, 1f);
		return new Color(0.93f, 0.97f, 1f) * (0.70f + 0.30f * s);
	}

	static float EndFade(float t)
	{
		float a = Mathf.Clamp(t / 0.07f, 0f, 1f);
		float b = Mathf.Clamp((1f - t) / 0.07f, 0f, 1f);
		return a * a * (3f - 2f * a) * b * b * (3f - 2f * b);
	}

	// ── 4. 静态 ribbon 网格 ───────────────────────────────────────────────

	ArrayMesh BuildMesh(List<FlowLine> lines)
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
				var position = line.Pts[i] * _radius;
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

			for (int i = 0; i + 1 < n; i++)
			{
				int a = baseVertex + 2 * i, b = a + 1, c = a + 2, d = a + 3;
				indices[idx++] = a;
				indices[idx++] = b;
				indices[idx++] = c;
				indices[idx++] = b;
				indices[idx++] = d;
				indices[idx++] = c;
			}
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
		int n = pts.Length;
		var p = pts[i];
		var prev = pts[i > 0 ? i - 1 : 0];
		var next = pts[i + 1 < n ? i + 1 : n - 1];
		var v = next - prev;
		v -= p * p.Dot(v);
		float len = v.Length();
		if (len < 1e-9f)
		{
			var axis = Mathf.Abs(p.Y) < 0.9f ? Vector3.Up : Vector3.Right;
			var fallback = p.Cross(axis);
			return fallback.LengthSquared() > 1e-12f ? fallback.Normalized() : Vector3.Zero;
		}
		return p.Cross(v / len).Normalized();
	}

	// ── 5. 材质 / 彗星 LUT ────────────────────────────────────────────────

	bool EnsureMaterial()
	{
		if (_material != null) return true;
		var shader = GD.Load<Shader>(ShaderPath);
		if (shader == null)
		{
			GD.PushError($"[WIND] shader 缺失：{ShaderPath}");
			return false;
		}

		_material = new ShaderMaterial { Shader = shader };
		_material.SetShaderParameter("u_viewport", new Vector2(1920f, 1080f));
		_material.SetShaderParameter("u_cycle", CycleU);
		_material.SetShaderParameter("u_rate", CometRate);
		_material.SetShaderParameter("u_tail_scale", 1.0f);
		_material.SetShaderParameter("u_time", 0.0f);
		_material.SetShaderParameter("u_base", 0.03f);   // 只留一条极淡的风路，近景不至于空；风丝仍是主角
		_material.SetShaderParameter("u_depth_bias", DepthBias);
		_material.SetShaderParameter("u_comet_tex", BuildCometTexture());
		return true;
	}

	/// <summary>512×4 的风丝剖面 LUT：短亮头 + 快衰减尾，duty 之后是暗间隙。</summary>
	static ImageTexture BuildCometTexture()
	{
		int w = CometLutWidth, h = CometLutVariants;
		var bytes = new byte[w * h];

		for (int row = 0; row < h; row++)
		{
			float duty = 0.62f + 0.04f * row;
			float power = 1.00f + 0.10f * row;

			for (int x = 0; x < w; x++)
			{
				float p = x / (float)w;
				float value = 0f;
				if (p < duty)
				{
					float t = p / duty;
					float head = Mathf.Exp(-t * t * 120f);
					float tail = Mathf.Pow(1f - t, power) * 0.55f;
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

	readonly struct TracedPoint
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

	sealed class FlowLine
	{
		public Vector3[] Pts;
		public float[] U;
		public Color[] Colors;
		public float[] Widths;
		public float[] Fade;
		public float Phase;
	}
}
