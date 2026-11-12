using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace World.Scene;

/// <summary>加载入口：浏览、生成、演化。</summary>
internal enum LoadFlow
{
	MapBrowse,
	WorldGeneration,
	Evolution,
}

/// <summary>操作生命周期状态（终态吸收）。</summary>
internal enum LoadState
{
	Running,
	Completed,
	Failed,
	Cancelled,
}

/// <summary>新帧换入状态（冻结 → 重烘 → 恢复）。</summary>
internal enum FrameSwapState
{
	Idle,
	Freezing,
	Rebaking,
	Completed,
	Failed,
}

/// <summary>失败码、阶段与消息；只读，仅 <see cref="LoadState.Failed"/> 时存在。</summary>
internal sealed class LoadFailure
{
	public LoadFailure(string code, string stage, string message)
	{
		if (string.IsNullOrWhiteSpace(code) || code.Length > LoadContract.MaxCodeLength)
			throw new ArgumentException($"失败码必须为 1–{LoadContract.MaxCodeLength} 个字符。", nameof(code));
		if (stage == null || stage.Length > LoadContract.MaxStageLength)
			throw new ArgumentException($"阶段文字不能超过 {LoadContract.MaxStageLength} 个字符。", nameof(stage));
		if (string.IsNullOrWhiteSpace(message) || message.Length > LoadContract.MaxMessageLength)
			throw new ArgumentException($"失败消息必须为 1–{LoadContract.MaxMessageLength} 个字符。", nameof(message));

		Code = code;
		Stage = stage;
		Message = message;
	}

	public string Code { get; }
	public string Stage { get; }
	public string Message { get; }
}

/// <summary>进度契约的结构层校验：范围、必填、状态一致性。不含状态迁移。</summary>
internal static class LoadContract
{
	internal const int MaxTitleLength = 40;
	internal const int MaxStageLength = 80;
	internal const int MaxCodeLength = 64;
	internal const int MaxMessageLength = 500;

	internal static void RequireFlow(LoadFlow flow)
	{
		if (flow is not (LoadFlow.MapBrowse or LoadFlow.WorldGeneration or LoadFlow.Evolution))
			throw new ArgumentOutOfRangeException(nameof(flow), flow, "未知的加载入口。");
	}

	internal static void RequireState(LoadState state)
	{
		if (state is not (LoadState.Running or LoadState.Completed or LoadState.Failed or LoadState.Cancelled))
			throw new ArgumentOutOfRangeException(nameof(state), state, "未知的操作状态。");
	}

	internal static void RequireTitle(string title)
	{
		string trimmed = title?.Trim();
		if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxTitleLength)
			throw new ArgumentException($"标题必须为 1–{MaxTitleLength} 个字符。", nameof(title));
	}

	internal static void RequireStage(string stage, bool allowEmpty)
	{
		if (stage == null || stage.Length > MaxStageLength)
			throw new ArgumentException($"阶段文字不能超过 {MaxStageLength} 个字符。", nameof(stage));
		if (!allowEmpty && string.IsNullOrWhiteSpace(stage))
			throw new ArgumentException("阶段文字不能为空。", nameof(stage));
	}

	internal static void RequireProgress(float? progress)
	{
		if (progress is float value && (!float.IsFinite(value) || value < 0f || value > 1f))
			throw new ArgumentOutOfRangeException(nameof(progress), progress, "进度必须是 [0,1] 内的有限值或 null。");
	}

	internal static void RequireConsistency(LoadState state, float? progress, LoadFailure failure)
	{
		RequireProgress(progress);
		if (state == LoadState.Completed && progress is not 1f)
			throw new ArgumentException("Completed 操作的进度必须正好为 1。", nameof(progress));
		if (state == LoadState.Failed && failure == null)
			throw new ArgumentException("Failed 操作必须带失败详情。", nameof(failure));
		if (state != LoadState.Failed && failure != null)
			throw new ArgumentException("只有 Failed 操作可以带失败详情。", nameof(failure));
	}
}

/// <summary>线程安全的进度与操作真源；通知在状态锁外发送。</summary>
internal sealed class LoadOperation : IDisposable
{
	readonly object _gate = new();
	readonly CancellationTokenSource _cancellation;
	readonly bool _ownsCancellation;
	readonly Dictionary<Guid, LoadProgressSubscription> _observers = new();
	LoadState _state = LoadState.Running;
	float? _progress;
	string _stage = string.Empty;
	LoadFailure _failure;
	long _version;
	bool _disposed;

	public LoadOperation(Guid id, LoadFlow flow, string title)
		: this(id, flow, title, new CancellationTokenSource(), ownsCancellation: true) { }

	public LoadOperation(Guid id, LoadFlow flow, string title, CancellationToken token)
		: this(id, flow, title, null, token, false) { }

	public LoadOperation(Guid id, LoadFlow flow, string title, CancellationTokenSource cancellation)
		: this(id, flow, title, cancellation, ownsCancellation: false) { }

	LoadOperation(Guid id, LoadFlow flow, string title, CancellationTokenSource cancellation, bool ownsCancellation)
		: this(id, flow, title, cancellation, cancellation?.Token ?? CancellationToken.None, ownsCancellation) { }

	LoadOperation(Guid id, LoadFlow flow, string title, CancellationTokenSource cancellation,
		CancellationToken token, bool ownsCancellation)
	{
		if (id == Guid.Empty) throw new ArgumentException("操作标识不能为空。", nameof(id));
		LoadContract.RequireFlow(flow);
		LoadContract.RequireTitle(title);
		Id = id;
		Flow = flow;
		Title = title.Trim();
		Token = token;
		_cancellation = cancellation;
		_ownsCancellation = ownsCancellation;
		StartedTimestamp = Stopwatch.GetTimestamp();
	}

	public Guid Id { get; }
	public LoadFlow Flow { get; }
	public string Title { get; }
	public LoadState State { get { lock (_gate) return _state; } }
	public float? Progress { get { lock (_gate) return _progress; } }
	public string Stage { get { lock (_gate) return _stage; } }
	public LoadFailure Failure { get { lock (_gate) return _failure; } }
	public CancellationToken Token { get; }
	public long Version { get { lock (_gate) return _version; } }
	public long StartedTimestamp { get; }
	public bool IsTerminal { get { lock (_gate) return _state != LoadState.Running; } }
	public TimeSpan Elapsed => Stopwatch.GetElapsedTime(StartedTimestamp);

	public LoadProgressSnapshot Snapshot()
	{
		lock (_gate) return SnapshotLocked();
	}

	LoadProgressSnapshot SnapshotLocked()
		=> new(Id, Flow, Title, _state, _progress, _stage, _failure, Elapsed, _version);

	/// <summary>
	/// 立即投递当前快照，之后按版本递增投递；回调运行于上报线程，不可访问 Godot 节点。
	/// 允许合并并发通知；释放返回句柄后停止后续投递，已开始的回调可以完成。
	/// </summary>
	internal LoadProgressSubscription Subscribe(ILoadProgressObserver observer)
	{
		if (observer == null) throw new ArgumentNullException(nameof(observer));
		LoadProgressSubscription subscription;
		LoadProgressSnapshot snapshot;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			subscription = new LoadProgressSubscription(Id, Guid.NewGuid(), observer, Unsubscribe);
			_observers.Add(subscription.SubscriptionId, subscription);
			snapshot = SnapshotLocked();
		}
		subscription.Deliver(snapshot);
		return subscription;
	}

	void Unsubscribe(Guid id)
	{
		lock (_gate) _observers.Remove(id);
	}

	void Change(Action update)
	{
		LoadProgressSnapshot snapshot;
		LoadProgressSubscription[] observers;
		lock (_gate)
		{
			if (_disposed || _state != LoadState.Running) return;
			update();
			_version++;
			snapshot = SnapshotLocked();
			observers = _observers.Values.ToArray();
		}
		foreach (var observer in observers) observer.Deliver(snapshot);
	}

	public void Report(float? fraction, string stage)
	{
		LoadContract.RequireProgress(fraction);
		LoadContract.RequireStage(stage, allowEmpty: false);
		Change(() =>
		{
			if (fraction is float next) _progress = _progress is float current ? Math.Max(current, next) : next;
			_stage = stage;
		});
	}

	public void ReportIndeterminate(string stage) => Report(null, stage);

	public void Complete() => Change(() =>
	{
		_state = LoadState.Completed;
		_progress = 1f;
		_stage = "完成";
		_failure = null;
	});

	public void Fail(LoadFailure failure)
	{
		if (failure == null) throw new ArgumentNullException(nameof(failure));
		Change(() =>
		{
			_state = LoadState.Failed;
			_failure = failure;
			if (string.IsNullOrEmpty(_stage)) _stage = string.IsNullOrEmpty(failure.Stage) ? "失败" : failure.Stage;
		});
	}

	public void Cancel()
	{
		Change(() =>
		{
			_state = LoadState.Cancelled;
			if (string.IsNullOrEmpty(_stage)) _stage = "已取消";
		});
		if (State != LoadState.Cancelled) return;
		try { _cancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	public void Dispose()
	{
		Cancel();
		LoadProgressSubscription[] observers;
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			observers = _observers.Values.ToArray();
			_observers.Clear();
		}
		foreach (var observer in observers) observer.Dispose();
		if (_ownsCancellation) _cancellation.Dispose();
	}
}

/// <summary>不可变进度快照；字段来自同一 Version。</summary>
internal readonly struct LoadProgressSnapshot
{
	public LoadProgressSnapshot(Guid id, LoadFlow flow, string title, LoadState state,
		float? progress, string stage, LoadFailure failure, TimeSpan elapsed, long version)
	{
		if (id == Guid.Empty) throw new ArgumentException("操作标识不能为空。", nameof(id));
		LoadContract.RequireFlow(flow);
		LoadContract.RequireTitle(title);
		LoadContract.RequireState(state);
		LoadContract.RequireStage(stage ?? string.Empty, allowEmpty: true);
		LoadContract.RequireConsistency(state, progress, failure);
		if (progress != null && string.IsNullOrWhiteSpace(stage))
			throw new ArgumentException("有进度的快照必须带阶段文字。", nameof(stage));
		if (state != LoadState.Running && string.IsNullOrWhiteSpace(stage))
			throw new ArgumentException("非 Running 快照必须带阶段文字。", nameof(stage));
		if (elapsed < TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "已用时长不能为负。");
		if (version < 0)
			throw new ArgumentOutOfRangeException(nameof(version), version, "版本号不能为负。");

		Id = id;
		Flow = flow;
		Title = title.Trim();
		State = state;
		Progress = progress;
		Stage = stage ?? string.Empty;
		Failure = failure;
		Elapsed = elapsed;
		Version = version;
	}

	public Guid Id { get; }
	public LoadFlow Flow { get; }
	public string Title { get; }
	public LoadState State { get; }
	public float? Progress { get; }
	public string Stage { get; }
	public LoadFailure Failure { get; }
	public TimeSpan Elapsed { get; }
	public long Version { get; }
}

/// <summary>演化按步进度；单步时不上报百分比。</summary>
internal readonly struct EvolutionProgress
{
	public EvolutionProgress(long completedSteps, long totalSteps)
	{
		if (totalSteps < 1)
			throw new ArgumentOutOfRangeException(nameof(totalSteps), totalSteps, "总步数必须 ≥1。");
		if (completedSteps < 0 || completedSteps > totalSteps)
			throw new ArgumentOutOfRangeException(nameof(completedSteps), completedSteps, "已完成步数必须在 [0,总步数] 内。");

		CompletedSteps = completedSteps;
		TotalSteps = totalSteps;
	}

	public long CompletedSteps { get; }
	public long TotalSteps { get; }
	public bool SingleStep => TotalSteps == 1;
}

/// <summary>新帧换入的计数状态；不持世界数据与网格。</summary>
internal readonly struct FrameSwap
{
	public FrameSwap(FrameSwapState state, int completedSlices, int totalSlices, string stage)
	{
		if (state is not (FrameSwapState.Idle or FrameSwapState.Freezing or FrameSwapState.Rebaking
			or FrameSwapState.Completed or FrameSwapState.Failed))
			throw new ArgumentOutOfRangeException(nameof(state), state, "未知的换帧状态。");
		if (totalSlices < 1)
			throw new ArgumentOutOfRangeException(nameof(totalSlices), totalSlices, "总分片数必须 ≥1。");
		if (completedSlices < 0 || completedSlices > totalSlices)
			throw new ArgumentOutOfRangeException(nameof(completedSlices), completedSlices, "已完成分片必须在 [0,总分片] 内。");
		LoadContract.RequireStage(stage ?? string.Empty, allowEmpty: state == FrameSwapState.Idle);

		State = state;
		CompletedSlices = completedSlices;
		TotalSlices = totalSlices;
		Stage = stage ?? string.Empty;
	}

	public FrameSwapState State { get; }
	public int CompletedSlices { get; }
	public int TotalSlices { get; }
	public string Stage { get; }
}

/// <summary>加权阶段：标识、文案、权重。</summary>
internal readonly struct LoadStage
{
	public LoadStage(string id, string label, double weight)
	{
		if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || !IsStageId(id))
			throw new ArgumentException("阶段标识必须为 1–32 个小写标识符字符。", nameof(id));
		if (string.IsNullOrWhiteSpace(label) || label.Length > 40)
			throw new ArgumentException("阶段文案必须为 1–40 个字符。", nameof(label));
		if (!double.IsFinite(weight) || weight <= 0)
			throw new ArgumentOutOfRangeException(nameof(weight), weight, "阶段权重必须是正的有限值。");

		Id = id;
		Label = label;
		Weight = weight;
	}

	public string Id { get; }
	public string Label { get; }
	public double Weight { get; }

	static bool IsStageId(string id)
		=> id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

/// <summary>浏览/生成的加权阶段计划；权重总和必须为 1。</summary>
internal sealed class LoadStagePlan
{
	public LoadStagePlan(LoadFlow flow, IEnumerable<LoadStage> stages)
	{
		if (flow is not (LoadFlow.MapBrowse or LoadFlow.WorldGeneration))
			throw new ArgumentOutOfRangeException(nameof(flow), flow, "演化不走加权阶段计划。");
		if (stages == null) throw new ArgumentNullException(nameof(stages));

		var list = stages.ToList();
		if (list.Count is < 2 or > 8)
			throw new ArgumentException("阶段数必须在 2–8 之间。", nameof(stages));

		var ids = new HashSet<string>(StringComparer.Ordinal);
		double total = 0;
		foreach (LoadStage stage in list)
		{
			if (!ids.Add(stage.Id))
				throw new ArgumentException($"阶段标识重复：{stage.Id}。", nameof(stages));
			total += stage.Weight;
		}
		if (!double.IsFinite(total) || Math.Abs(total - 1.0) > 1e-6)
			throw new ArgumentException($"阶段权重总和必须为 1，实得 {total:G17}。", nameof(stages));

		Flow = flow;
		Stages = list.AsReadOnly();
		TotalWeight = total;
	}

	public LoadFlow Flow { get; }
	public IReadOnlyList<LoadStage> Stages { get; }
	public double TotalWeight { get; }

	/// <summary>阶段内完成比例换算为整份计划的 0–1 进度；未知阶段视为已完成。</summary>
	internal float FractionThrough(string stageId, long done, long total)
	{
		double before = 0;
		foreach (LoadStage stage in Stages)
		{
			if (string.Equals(stage.Id, stageId, StringComparison.Ordinal))
			{
				double part = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 1;
				return (float)Math.Clamp((before + stage.Weight * part) / TotalWeight, 0, 1);
			}
			before += stage.Weight;
		}
		return 1f;
	}
}

/// <summary>冻结的出厂阶段计划；数值在第 5 步实测后可调，结构不变。</summary>
internal static class LoadStagePlans
{
	public static readonly LoadStagePlan Browse = new(LoadFlow.MapBrowse, new[]
	{
		new LoadStage("read", "读取与清单", 0.10),
		new LoadStage("decode", "并行解压/解码", 0.35),
		new LoadStage("facts", "世界事实装配", 0.30),
		new LoadStage("render", "画面装配", 0.25),
	});

	public static readonly LoadStagePlan Generation = new(LoadFlow.WorldGeneration, new[]
	{
		new LoadStage("plate", "板块", 0.10),
		new LoadStage("terrain", "地形", 0.20),
		new LoadStage("climate", "气候", 0.30),
		new LoadStage("hydrology", "水文", 0.15),
		new LoadStage("index", "空间索引", 0.10),
		new LoadStage("render", "画面装配", 0.15),
	});
}

/// <summary>页面加载创建操作的入口；同一时刻最多一个非终态操作。</summary>
internal interface ILoadProgressSink
{
	bool HasActive { get; }

	LoadOperation Begin(LoadFlow flow, string title);
}
