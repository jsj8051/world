using System;
using System.Threading;
using System.Threading.Tasks;
using World.Application.Storage;
using World.Data.Maps;
using World.Data.Work;
using World.Logic;

namespace World.Application.Loading;

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

/// <summary>一次地图浏览的流水线：后台读包/解码/装配事实，主线程轮询结果；取消由自己的令牌收起。</summary>
internal sealed class MapBrowseFlow : IDisposable
{
	readonly IGeographyMapRepository _mapRepository;
	readonly CancellationTokenSource _cancellation = new();

	Task<ImportedWorldFactsSource> _task;
	BrowseOutcome _outcome = BrowseOutcome.Running;
	bool _started;
	bool _disposed;

	internal MapBrowseFlow(IGeographyMapRepository mapRepository)
		=> _mapRepository = mapRepository ?? throw new ArgumentNullException(nameof(mapRepository));

	internal void Start(string path)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_started) throw new InvalidOperationException("一次浏览只能启动一次。");
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("地图路径不能为空。", nameof(path));
		_started = true;

		var job = new MapBrowseJob(_mapRepository);
		CancellationToken token = _cancellation.Token;
		_task = Task.Run(() =>
		{
			token.ThrowIfCancellationRequested();
			ImportedWorldFactsSource source = job.Run(path, token);
			token.ThrowIfCancellationRequested();
			return source;
		});
	}

	/// <summary>主线程轮询；终态之后重复调用返回同一结果。</summary>
	internal BrowseOutcome Poll()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_started) throw new InvalidOperationException("浏览尚未启动。");
		if (_outcome.State != BrowseState.Running) return _outcome;
		if (!_task.IsCompleted) return _outcome;

		Task<ImportedWorldFactsSource> task = _task;
		if (task.IsCanceled) return _outcome = BrowseOutcome.Abandoned;
		if (task.IsFaulted) return _outcome = BrowseOutcome.Failed(ToFailure(task.Exception));
		return _outcome = BrowseOutcome.Succeeded(task.Result);
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_cancellation.Cancel();
		if (_task != null)
		{
			_ = _task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
			_task = null;
		}
		_cancellation.Dispose();
	}

	static LoadFailure ToFailure(Exception error)
	{
		Exception root = error is AggregateException aggregate ? aggregate.GetBaseException() : error;
		if (root is MapStructureException map)
			return new LoadFailure(map.Issue.Code.ToString(), map.Issue.Location ?? "加载地图", map.Issue.Message);
		return new LoadFailure("LoadError", "加载地图", root.Message);
	}
}

/// <summary>浏览加载的纯数据 Job：读包/校验/解码 + 世界事实装配，只发领域信号。</summary>
internal sealed class MapBrowseJob : IWorkSignalSource
{
	readonly IGeographyMapRepository _mapRepository;

	internal MapBrowseJob(IGeographyMapRepository mapRepository)
		=> _mapRepository = mapRepository ?? throw new ArgumentNullException(nameof(mapRepository));

	public event Action<WorkSignal> Signal;

	internal ImportedWorldFactsSource Run(string path, CancellationToken token)
	{
		Signal?.Invoke(WorkSignal.Started("decode", "并行解码"));
		GeographySnapshot snapshot = _mapRepository.Load(path, new DecodeProgress(this), token);

		token.ThrowIfCancellationRequested();
		Signal?.Invoke(WorkSignal.Started("facts", "装配世界事实"));
		ImportedWorldFactsSource source = ImportedWorldFactsFactory.Build(snapshot);
		Signal?.Invoke(WorkSignal.Unit("facts", "世界事实就绪", 1, 1));
		Signal?.Invoke(WorkSignal.Finished());
		return source;
	}

	sealed class DecodeProgress : IFileImportObserver
	{
		readonly MapBrowseJob _job;
		internal DecodeProgress(MapBrowseJob job) => _job = job;

		public void Report(int completed, int total)
			=> _job.Signal?.Invoke(WorkSignal.Unit("decode", $"并行解码 {completed}/{total}", completed, total));
	}
}
