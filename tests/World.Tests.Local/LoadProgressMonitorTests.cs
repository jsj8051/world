using System;
using System.Reflection;
using System.Threading;
using World.Data.Work;
using World.Logic;
using World.Scene;

namespace World.Tests;

/// <summary>第 5 步解耦测试：生成只发信号，监控器独立订阅并推进进度。</summary>
internal static class LoadProgressMonitorTests
{
	sealed class Source : IWorkSignalSource
	{
		public event Action<WorkSignal> Signal;

		public void Raise(WorkSignal signal) => Signal?.Invoke(signal);
	}

	public static void MonitorAdvancesFromSignals()
	{
		var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "加载地图", CancellationToken.None);
		var source = new Source();
		using var monitor = new LoadProgressMonitor(operation, LoadStagePlans.Browse);
		monitor.Attach(source);

		source.Raise(WorkSignal.Started("decode", "并行解码"));
		Program.True(operation.Progress == null, "阶段开始不应伪造百分比");

		source.Raise(WorkSignal.Unit("decode", "并行解码", 86, 173));
		Program.True(operation.Progress is > 0.25f and < 0.30f,
			$"解码一半的加权进度异常：{operation.Progress}");

		source.Raise(WorkSignal.Finished());
		Program.True(operation.Progress == 1f, "Finished 未推进到 100%");
		Program.True(operation.State == LoadState.Running, "非完成型监控器不应结束操作");
	}

	public static void MonitorCompletesOrFailsIndependently()
	{
		var completed = new LoadOperation(Guid.NewGuid(), LoadFlow.WorldGeneration, "生成世界", CancellationToken.None);
		var source = new Source();
		using (var monitor = new LoadProgressMonitor(completed, LoadStagePlans.Generation,
			0f, 0.70f, completesOperation: false))
		{
			monitor.Attach(source);
			source.Raise(WorkSignal.Unit("climate", "气候", 1, 1));
			source.Raise(WorkSignal.Finished());
			Program.True(completed.Progress is > 0.50f and < 0.71f,
				$"生成阶段加权异常：{completed.Progress}");
			Program.True(completed.State == LoadState.Running, "生成数据完成不应提前结束操作");
		}

		var failed = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "加载地图", CancellationToken.None);
		using (var monitor = new LoadProgressMonitor(failed, LoadStagePlans.Browse))
		{
			monitor.Attach(new Source { });
			// 用一个独立源触发失败
			var failing = new Source();
			monitor.Attach(failing);
			failing.Raise(WorkSignal.Failed("boom"));
			Program.True(failed.State == LoadState.Failed && failed.Failure?.Message == "boom", "失败信号未落成 LoadFailure");
		}
	}

	public static void GenerationJobDoesNotExposeProgress()
	{
		MethodInfo run = typeof(WorldGenerationJob).GetMethod("Run",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Program.True(run != null, "找不到 WorldGenerationJob.Run");
		foreach (ParameterInfo parameter in run.GetParameters())
			Program.True(parameter.ParameterType != typeof(LoadOperation)
				&& parameter.ParameterType != typeof(ILoadProgressSink),
				$"生成 Job 不应接收进度类型参数：{parameter.Name}");

		EventInfo signal = typeof(WorldGenSimulation).GetEvent("Signal");
		Program.True(signal != null && signal.EventHandlerType == typeof(Action<WorkSignal>),
			"WorldGenSimulation 应只暴露 WorkSignal 事件");
	}
}
