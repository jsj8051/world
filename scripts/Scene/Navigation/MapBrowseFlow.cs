using System;
using System.Threading.Tasks;
using World.Data.Maps;
using World.Logic;

namespace World.Scene;

/// <summary>一次浏览轮询的结果种类。</summary>
internal enum BrowseState
{
	Running,
	Succeeded,
	Failed,
	Abandoned,
}

/// <summary>不可变的轮询结果：仅 Succeeded 带来源，仅 Failed 带失败详情。</summary>
internal readonly struct BrowseOutcome
{
	BrowseOutcome(BrowseState state, ImportedWorldFactsSource source, LoadFailure failure)
	{
		if (state is not (BrowseState.Running or BrowseState.Succeeded or BrowseState.Failed or BrowseState.Abandoned))
			throw new ArgumentOutOfRangeException(nameof(state), state, "未知的浏览结果状态。");
		if (state == BrowseState.Succeeded && source == null)
			throw new ArgumentException("成功结果必须带地图来源。", nameof(source));
		if (state != BrowseState.Succeeded && source != null)
			throw new ArgumentException("只有成功结果可以带地图来源。", nameof(source));
		if (state == BrowseState.Failed && failure == null)
			throw new ArgumentException("失败结果必须带失败详情。", nameof(failure));
		if (state != BrowseState.Failed && failure != null)
			throw new ArgumentException("只有失败结果可以带失败详情。", nameof(failure));

		State = state;
		Source = source;
		Failure = failure;
	}

	public static BrowseOutcome Running => new(BrowseState.Running, null, null);

	public static BrowseOutcome Abandoned => new(BrowseState.Abandoned, null, null);

	public static BrowseOutcome Succeeded(ImportedWorldFactsSource source) => new(BrowseState.Succeeded, source, null);

	public static BrowseOutcome Failed(LoadFailure failure) => new(BrowseState.Failed, null, failure);

	public BrowseState State { get; }
	public ImportedWorldFactsSource Source { get; }
	public LoadFailure Failure { get; }
}

/// <summary>
/// 一次地图浏览的流水线：起后台读包、按提交门判定结果，并把加载操作推进到终态。
/// 只认操作与绑定，不认页面；页面切换由外壳负责。
/// </summary>
internal sealed class MapBrowseFlow : IDisposable
{
	readonly LoadOperation _operation;
	readonly LoadOperationBinding _binding;
	readonly Func<Guid> _currentPageId;

	Task<ImportedWorldFactsSource> _task;
	LoadProgressMonitor _monitor;
	LoadCommitKey _key;
	BrowseOutcome _outcome = BrowseOutcome.Running;
	bool _started;
	bool _disposed;

	internal MapBrowseFlow(LoadOperation operation, LoadOperationBinding binding, Func<Guid> currentPageId)
	{
		_operation = operation ?? throw new ArgumentNullException(nameof(operation));
		_binding = binding ?? throw new ArgumentNullException(nameof(binding));
		_currentPageId = currentPageId ?? throw new ArgumentNullException(nameof(currentPageId));
		if (!ReferenceEquals(binding.Operation, operation))
			throw new ArgumentException("绑定必须属于同一加载操作。", nameof(binding));
	}

	internal void Start(string path)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_started) throw new InvalidOperationException("一次浏览只能启动一次。");
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("地图路径不能为空。", nameof(path));
		_started = true;

		var work = new LoadWorkContext(_binding);
		_key = work.Key;
		var job = new MapBrowseJob();
		_monitor = new LoadProgressMonitor(_operation, LoadStagePlans.Browse, 0f, 0.75f);
		_monitor.Attach(job);
		_task = Task.Run(() =>
		{
			using (work)
			{
				work.Token.ThrowIfCancellationRequested();
				var result = job.Run(path, work.Token);
				work.Token.ThrowIfCancellationRequested();
				return result;
			}
		});
	}

	/// <summary>主线程轮询；终态之后重复调用返回同一结果。</summary>
	internal BrowseOutcome Poll()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_started) throw new InvalidOperationException("浏览尚未启动。");
		if (_outcome.State != BrowseState.Running) return _outcome;
		if (!_task.IsCompleted) return BrowseOutcome.Running;

		Task<ImportedWorldFactsSource> task = _task;
		_monitor?.Dispose();
		_monitor = null;

		if (!LoadCommitGuard.IsCurrent(_key, _binding, _currentPageId()))
		{
			_ = task.Exception;
			return _outcome = BrowseOutcome.Abandoned;
		}
		if (task.IsCanceled)
		{
			_operation.Cancel();
			return _outcome = BrowseOutcome.Abandoned;
		}
		if (task.IsFaulted)
		{
			LoadFailure failure = ToFailure(task.Exception);
			_operation.Fail(failure);
			return _outcome = BrowseOutcome.Failed(failure);
		}
		if (!LoadCommitGuard.CanCommit(_key, _binding, _currentPageId())) return _outcome = BrowseOutcome.Abandoned;

		_operation.Report(0.75f, "画面装配");
		return _outcome = BrowseOutcome.Succeeded(task.Result);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_monitor?.Dispose();
		_monitor = null;
		if (_task != null)
		{
			_ = _task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
			_task = null;
		}
	}

	static LoadFailure ToFailure(Exception error)
	{
		Exception root = error is AggregateException aggregate ? aggregate.GetBaseException() : error;
		if (root is MapStructureException map)
			return new LoadFailure(map.Issue.Code.ToString(), map.Issue.Location ?? "加载地图", map.Issue.Message);
		return new LoadFailure("LoadError", "加载地图", root.Message);
	}
}
