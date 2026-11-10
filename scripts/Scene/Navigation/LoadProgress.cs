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

/// <summary>
/// 进度真源：线程安全、无 Godot 类型。字段在结构层只读，状态迁移由第 5 步实现。
/// </summary>
internal sealed class LoadOperation
{
	readonly object _gate = new();
	readonly CancellationTokenSource _cancellation;

	public LoadOperation(Guid id, LoadFlow flow, string title, CancellationToken token)
		: this(id, flow, title, null, token)
	{
	}

	public LoadOperation(Guid id, LoadFlow flow, string title, CancellationTokenSource cancellation)
		: this(id, flow, title, cancellation, cancellation?.Token ?? CancellationToken.None)
	{
	}

	LoadOperation(Guid id, LoadFlow flow, string title, CancellationTokenSource cancellation, CancellationToken token)
	{
		if (id == Guid.Empty) throw new ArgumentException("操作标识不能为空。", nameof(id));
		LoadContract.RequireFlow(flow);
		LoadContract.RequireTitle(title);

		Id = id;
		Flow = flow;
		Title = title.Trim();
		Token = token;
		_cancellation = cancellation;
		State = LoadState.Running;
		Progress = null;
		Stage = string.Empty;
		Failure = null;
		Version = 0;
		StartedTimestamp = Stopwatch.GetTimestamp();
	}

	public Guid Id { get; }
	public LoadFlow Flow { get; }
	public string Title { get; }
	public LoadState State { get; private set; }
	public float? Progress { get; private set; }
	public string Stage { get; private set; }
	public LoadFailure Failure { get; private set; }
	public CancellationToken Token { get; }
	public long Version { get; private set; }
	public long StartedTimestamp { get; }

	/// <summary>终态不可再变。</summary>
	public bool IsTerminal => State is LoadState.Completed or LoadState.Failed or LoadState.Cancelled;

	/// <summary>已用时长（展示用，不持久化）。</summary>
	public TimeSpan Elapsed => Stopwatch.GetElapsedTime(StartedTimestamp);

	/// <summary>同一 Version 的字段快照，供视图轮询。</summary>
	public LoadProgressSnapshot Snapshot()
	{
		lock (_gate)
		{
			return new LoadProgressSnapshot(Id, Flow, Title, State, Progress, Stage, Failure, Elapsed, Version);
		}
	}

	/// <summary>上报进度与阶段；终态后静默忽略。低于当前值的进度钳到当前值。</summary>
	public void Report(float? fraction, string stage)
	{
		LoadContract.RequireProgress(fraction);
		LoadContract.RequireStage(stage, allowEmpty: false);
		lock (_gate)
		{
			if (IsTerminal) return;
			if (fraction is float next)
			{
				if (Progress is float current && next < current) next = current;
				Progress = next;
			}
			Stage = stage;
			Version++;
		}
	}

	/// <summary>只更新阶段、不推进百分比（无法细分的阶段）。</summary>
	public void ReportIndeterminate(string stage)
	{
		LoadContract.RequireStage(stage, allowEmpty: false);
		lock (_gate)
		{
			if (IsTerminal) return;
			Stage = stage;
			Version++;
		}
	}

	/// <summary>完成：进度置 1；终态后忽略。</summary>
	public void Complete()
	{
		lock (_gate)
		{
			if (IsTerminal) return;
			State = LoadState.Completed;
			Progress = 1f;
			if (string.IsNullOrEmpty(Stage)) Stage = "完成";
			Failure = null;
			Version++;
		}
	}

	/// <summary>失败：必须带失败详情；终态后忽略。</summary>
	public void Fail(LoadFailure failure)
	{
		if (failure == null) throw new ArgumentNullException(nameof(failure));
		lock (_gate)
		{
			if (IsTerminal) return;
			State = LoadState.Failed;
			Failure = failure;
			if (string.IsNullOrEmpty(Stage)) Stage = failure.Stage;
			Version++;
		}
	}

	/// <summary>取消：置终态并取消令牌；终态后忽略。</summary>
	public void Cancel()
	{
		lock (_gate)
		{
			if (IsTerminal) return;
			State = LoadState.Cancelled;
			if (string.IsNullOrEmpty(Stage)) Stage = "已取消";
			Version++;
		}
		try { _cancellation?.Cancel(); }
		catch (ObjectDisposedException) { }
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
