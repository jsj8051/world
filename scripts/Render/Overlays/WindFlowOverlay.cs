using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using World.Logic;

namespace World.Render;

/// <summary>
/// 风场粒子层：把风矢积成静态 ribbon 流线，运行时由 shader 沿 UV.x 播放短促风丝。
/// 积分、分桶与网格都在 <see cref="FlowParticleOverlay"/>；本类负责取"哪个月 / 哪一档风"、
/// 近白色配色，以及换月/换档的异步构建与网格缓存。
/// 视觉参考 cambecc/earth（MIT）的 wind map；这里是 Godot 流线粒子的重实现，不含其源码。
///
/// @example
/// <code>
/// var wind = new WindFlowOverlay { Name = "WindFlow" };
/// AddChild(wind);
/// wind.SetContext(facts, windMode);
/// </code>
/// </summary>
internal sealed partial class WindFlowOverlay : FlowParticleOverlay, IMapLayer
{
	WorldFacts _facts;
	int _month = -1;
	WindKind _kind = WindKind.Surface;
	float[] _strength;

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

	protected override int SeedCount => 5200;
	protected override float MinSpeedMul => 0.35f;
	protected override float FilledSpeed => 0.08f;
	protected override float MinSeedSpeed => 0.30f;
	protected override float BaseWidthPx => 1.4f;
	protected override float WidthPerStrengthPx => 0.8f;
	protected override float CycleU => 0.28f;
	protected override float CometRate => 0.9f;
	protected override string LogTag => "[WIND]";
	protected override float LutDutyBase => 0.62f;
	protected override float LutDutyStep => 0.04f;
	protected override float LutPowerBase => 1.00f;
	protected override float LutPowerStep => 0.10f;
	protected override float LutTailAmplitude => 0.55f;

	/// <summary>风丝用显示风速（与底色同一个取数口），不用采样出的积分速度。</summary>
	float FieldStrength(int cell) => Mathf.Clamp(_strength[Math.Clamp(cell, 0, _strength.Length - 1)], 0f, 1f);

	/// <summary>风速 → 近白色；强风更亮。速度本身由底图色带表达。</summary>
	protected override Color ColorAt(int cell, float strength)
		=> new Color(0.93f, 0.97f, 1f) * (0.70f + 0.30f * FieldStrength(cell));

	protected override float WidthAt(int cell, float strength)
		=> BaseWidthPx + WidthPerStrengthPx * FieldStrength(cell);

	protected override void ConfigureMaterial(ShaderMaterial material)
		=> material.SetShaderParameter("u_base", 0.03f);   // 只留一条极淡的风路，近景不至于空；风丝仍是主角

	public void SetContext(WorldFacts facts, MapMode mode)
	{
		Visible = facts != null && mode is WindMode;
		if (Visible && mode is WindMode wind)
			BuildFromFacts(facts, wind.SelectedMonth, wind.SelectedKind);
	}

	/// <summary>按本次发布的世界事实重装风丝（异步：流线积分在后台，主线程只组装网格）。
	/// 每个 (月份, 风档) 首次访问时烘焙并缓存，再次切换直接换 Mesh（O(1)）。
	/// <paramref name="kind"/> 必须与底色选的风档一致，否则风丝与底色不同源。</summary>
	void BuildFromFacts(WorldFacts facts, int month, WindKind kind)
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

		ApplyMesh(mesh);
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

		PrepareGrid(facts.Ball);

		var set = facts.Wind.Get(kind);
		var wind = month <= 0 ? set.Annual
			: month <= set.MonthCount ? set.Monthly[month - 1] : null;
		if (wind == null || wind.Length != CellDirs.Length)
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

		Land = new bool[CellDirs.Length];
		_strength = new float[CellDirs.Length];
		var directions = new Vector3[CellDirs.Length];
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
			GD.PushError($"{LogTag} build failed: {error}");
			Mesh = null;
		}
		else if (lines != null && lines.Count > 0)
		{
			int points = 0;
			foreach (var line in lines) points += line.Pts.Length;

			var built = BuildMesh(lines);
			ApplyMesh(built);
			_facts = _buildingFacts;
			_month = _buildingMonth;
			_kind = _buildingKind;
			CacheMesh(_month, _kind, built);
			GD.Print($"{LogTag} month={_month} kind={_kind} lines={lines.Count} points={points} build={_buildWatch.ElapsedMilliseconds}ms");
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

	protected override void OnProcessing(double delta) => PumpBuild();
}
