using System;
using World.Data.Work;

namespace World.Scene;

/// <summary>
/// 独立进度监控器：订阅任意 <see cref="IWorkSignalSource"/>，把领域信号推进到 <see cref="LoadOperation"/>。
/// 生产者不认识它；不创建它时生产照常运行。
/// </summary>
internal sealed class LoadProgressMonitor : IDisposable
{
	readonly LoadOperation _operation;
	readonly LoadStagePlan _plan;
	readonly float _start;
	readonly float _span;
	readonly bool _completesOperation;
	IWorkSignalSource _source;

	internal LoadProgressMonitor(LoadOperation operation, LoadStagePlan plan,
		float start = 0f, float span = 1f, bool completesOperation = false)
	{
		_operation = operation ?? throw new ArgumentNullException(nameof(operation));
		if (!(start >= 0f && start <= 1f) || !(span >= 0f && span <= 1f) || start + span > 1.000001f)
			throw new ArgumentOutOfRangeException(nameof(start), "进度区间必须落在 [0,1]。");
		_plan = plan;
		_start = start;
		_span = span;
		_completesOperation = completesOperation;
	}

	internal void Attach(IWorkSignalSource source)
	{
		if (source == null) throw new ArgumentNullException(nameof(source));
		Detach();
		_source = source;
		_source.Signal += OnSignal;
	}

	internal void Detach()
	{
		if (_source != null) _source.Signal -= OnSignal;
		_source = null;
	}

	public void Dispose() => Detach();

	void OnSignal(WorkSignal signal)
	{
		switch (signal.Kind)
		{
			case WorkSignalKind.StageStarted:
				_operation.ReportIndeterminate(string.IsNullOrEmpty(signal.Stage) ? "处理中" : signal.Stage);
				break;

			case WorkSignalKind.UnitCompleted:
				if (_plan != null)
				{
					float fraction = _plan.FractionThrough(signal.StageId, signal.Done, signal.Total);
					_operation.Report(_start + _span * fraction, signal.Stage);
				}
				else if (signal.Total > 1)
				{
					float fraction = (float)Math.Clamp((double)signal.Done / signal.Total, 0, 1);
					_operation.Report(_start + _span * fraction, signal.Stage);
				}
				else
				{
					_operation.ReportIndeterminate(string.IsNullOrEmpty(signal.Stage) ? "处理中" : signal.Stage);
				}
				break;

			case WorkSignalKind.StageCompleted:
				_operation.Report(_start + _span * (_plan?.FractionThrough(signal.StageId, 1, 1) ?? 1f),
					string.IsNullOrEmpty(signal.Stage) ? "处理中" : signal.Stage);
				break;

			case WorkSignalKind.JobCompleted:
				_operation.Report(_start + _span, "完成");
				if (_completesOperation) _operation.Complete();
				break;

			case WorkSignalKind.JobFailed:
				_operation.Fail(new LoadFailure("WorkFailed", "加载",
					string.IsNullOrEmpty(signal.Message) ? "任务失败" : signal.Message));
				break;
		}
	}
}
