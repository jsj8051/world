using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace World.Application.Loading;

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
