using System;

namespace World.Data.Work;

/// <summary>领域工作信号：生产者只描述“哪个阶段、完成了多少”，不认识进度条与 UI。</summary>
public enum WorkSignalKind
{
	StageStarted,
	UnitCompleted,
	StageCompleted,
	JobCompleted,
	JobFailed,
}

/// <summary>一次工作进展信号；<see cref="Done"/>/<see cref="Total"/> 为 0 表示不确定。</summary>
public readonly record struct WorkSignal(
	WorkSignalKind Kind, string StageId, string Stage, long Done, long Total, string Message)
{
	public static WorkSignal Started(string stageId, string stage)
		=> new(WorkSignalKind.StageStarted, stageId, stage, 0, 0, "");

	public static WorkSignal Unit(string stageId, string stage, long done, long total)
		=> new(WorkSignalKind.UnitCompleted, stageId, stage, done, total, "");

	public static WorkSignal StageFinished(string stageId, string stage)
		=> new(WorkSignalKind.StageCompleted, stageId, stage, 1, 1, "");

	public static WorkSignal Finished()
		=> new(WorkSignalKind.JobCompleted, "", "完成", 0, 0, "");

	public static WorkSignal Failed(string message)
		=> new(WorkSignalKind.JobFailed, "", "失败", 0, 0, message ?? "");
}

/// <summary>可被监控器订阅的信号源；不引用 Scene/进度/UI。</summary>
public interface IWorkSignalSource
{
	event Action<WorkSignal> Signal;
}
