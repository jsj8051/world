using System;
using System.Threading.Tasks;
using World.Data;
using World.Data.Generation;
using World.Logic;
using World.Params;
using World.Params.Generation;

namespace World.Application.Loading;

/// <summary>一次世界生成轮询的结果种类。</summary>
internal enum WorldGenerationState
{
	Running,
	Succeeded,
	Failed,
	Abandoned,
}

/// <summary>不可变的轮询结果：仅 Succeeded 带生成结果，仅 Failed 带失败详情。</summary>
internal readonly struct WorldGenerationOutcome
{
	WorldGenerationOutcome(WorldGenerationState state, WorldGenerationResult result, LoadFailure failure)
	{
		if (state is not (WorldGenerationState.Running or WorldGenerationState.Succeeded
			or WorldGenerationState.Failed or WorldGenerationState.Abandoned))
			throw new ArgumentOutOfRangeException(nameof(state), state, "未知的生成结果状态。");
		if (state == WorldGenerationState.Succeeded && result == null)
			throw new ArgumentException("成功结果必须带生成产物。", nameof(result));
		if (state != WorldGenerationState.Succeeded && result != null)
			throw new ArgumentException("只有成功结果可以带生成产物。", nameof(result));
		if (state == WorldGenerationState.Failed && failure == null)
			throw new ArgumentException("失败结果必须带失败详情。", nameof(failure));
		if (state != WorldGenerationState.Failed && failure != null)
			throw new ArgumentException("只有失败结果可以带失败详情。", nameof(failure));

		State = state;
		Result = result;
		Failure = failure;
	}

	public static WorldGenerationOutcome Running => new(WorldGenerationState.Running, null, null);

	public static WorldGenerationOutcome Abandoned => new(WorldGenerationState.Abandoned, null, null);

	public static WorldGenerationOutcome Succeeded(WorldGenerationResult result)
		=> new(WorldGenerationState.Succeeded, result, null);

	public static WorldGenerationOutcome Failed(LoadFailure failure)
		=> new(WorldGenerationState.Failed, null, failure);

	public WorldGenerationState State { get; }
	public WorldGenerationResult Result { get; }
	public LoadFailure Failure { get; }
}

/// <summary>把用户配置映射成生成规格：默认参数来自游戏目录的参数存档，不依赖页面节点。</summary>
internal static class WorldGenerationSpecs
{
	internal static WorldSpec Create(GenerationConfig config)
	{
		if (config == null) throw new ArgumentNullException(nameof(config));
		config.Validate();

		string json = WorldParamStore.ReadPresetText(out var ioProblems);
		WorldSpec defaults = WorldParams.ReadJson(json, out var codecProblems);
		if (defaults == null || ioProblems.Count != 0 || codecProblems.Count != 0)
			throw new InvalidOperationException("默认世界参数无法加载："
				+ string.Join("；", ioProblems) + string.Join("；", codecProblems));
		return GenerationConfigMapper.CreateWorldSpec(config, defaults);
	}
}

/// <summary>
/// 一次世界生成的流水线：后台跑纯数据 Job、按提交门判定结果，把加载操作推进到 0.70；
/// 页面接手画面装配并收尾。只认操作与绑定，不认页面；页面切换由外壳负责。
/// </summary>
internal sealed class WorldGenerationFlow : IDisposable
{
	readonly LoadOperation _operation;
	readonly LoadOperationBinding _binding;
	readonly Func<Guid> _currentPageId;
	readonly WorldSpec _spec;
	readonly WorldGenerationOptions _options;

	Task<WorldGenerationResult> _task;
	LoadProgressMonitor _monitor;
	WorldGenerationOutcome _outcome = WorldGenerationOutcome.Running;
	bool _started;
	bool _disposed;

	internal WorldGenerationFlow(LoadOperation operation, LoadOperationBinding binding,
		Func<Guid> currentPageId, WorldSpec spec, WorldGenerationOptions options)
	{
		_operation = operation ?? throw new ArgumentNullException(nameof(operation));
		_binding = binding ?? throw new ArgumentNullException(nameof(binding));
		_currentPageId = currentPageId ?? throw new ArgumentNullException(nameof(currentPageId));
		_spec = spec ?? throw new ArgumentNullException(nameof(spec));
		_options = options;
		if (!ReferenceEquals(binding.Operation, operation))
			throw new ArgumentException("绑定必须属于同一加载操作。", nameof(binding));
	}

	internal void Start()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_started) throw new InvalidOperationException("一次生成只能启动一次。");
		_started = true;

		var work = new LoadWorkContext(_binding);
		var job = new WorldGenerationJob();
		_monitor = new LoadProgressMonitor(_operation, LoadStagePlans.Generation, 0f, 0.70f);
		_monitor.Attach(job);
		_task = Task.Run(() =>
		{
			using (work)
			{
				work.Token.ThrowIfCancellationRequested();
				var result = job.Run(_spec, _options.Resolution, PlanetGeometry.ProductionRadius,
					_options.EnableEvolution, _options.EvolutionSteps, work.Token);
				work.Token.ThrowIfCancellationRequested();
				return result;
			}
		});
	}

	/// <summary>主线程轮询；终态之后重复调用返回同一结果。</summary>
	internal WorldGenerationOutcome Poll()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!_started) throw new InvalidOperationException("生成尚未启动。");
		if (_outcome.State != WorldGenerationState.Running) return _outcome;
		if (!_task.IsCompleted) return _outcome;

		Task<WorldGenerationResult> task = _task;
		_monitor?.Dispose();
		_monitor = null;

		if (!LoadCommitGuard.IsCurrent(_binding, _currentPageId()))
		{
			_ = task.Exception;
			return _outcome = WorldGenerationOutcome.Abandoned;
		}
		if (task.IsCanceled)
		{
			_operation.Cancel();
			return _outcome = WorldGenerationOutcome.Abandoned;
		}
		if (task.IsFaulted)
		{
			LoadFailure failure = ToFailure(task.Exception);
			_operation.Fail(failure);
			return _outcome = WorldGenerationOutcome.Failed(failure);
		}
		if (!LoadCommitGuard.CanCommit(_binding, _currentPageId()))
			return _outcome = WorldGenerationOutcome.Abandoned;

		_operation.Report(0.70f, "画面装配");
		return _outcome = WorldGenerationOutcome.Succeeded(task.Result);
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
		return new LoadFailure("GenerationError", "生成世界", root.Message);
	}
}
