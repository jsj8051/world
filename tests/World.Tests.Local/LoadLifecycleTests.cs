using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.Scene;

namespace World.Tests;

/// <summary>进度观察者的线程边界、版本顺序与提交资格。</summary>
internal static class LoadLifecycleTests
{
	sealed class Recorder : ILoadProgressObserver
	{
		readonly object _gate = new();
		readonly List<LoadProgressSnapshot> _seen = new();

		public void OnProgress(LoadProgressSnapshot snapshot)
		{
			lock (_gate) _seen.Add(snapshot);
		}

		public LoadProgressSnapshot[] Seen()
		{
			lock (_gate) return _seen.ToArray();
		}
	}

	public static void SubscribeDeliversCurrentSnapshotImmediately()
	{
		using var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "加载地图");
		operation.Report(0.25f, "读取与清单");
		var recorder = new Recorder();
		using (operation.Subscribe(recorder))
		{
			LoadProgressSnapshot[] seen = recorder.Seen();
			Program.True(seen.Length == 1, $"订阅未立即收到当前快照：{seen.Length}");
			Program.True(seen[0].Version == operation.Version && seen[0].Progress == 0.25f,
				"订阅首帧不是当前版本的最新快照。");
		}
	}

	public static void SubscriptionVersionsIncreaseAndStopOnDispose()
	{
		using var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "加载地图");
		var recorder = new Recorder();
		LoadProgressSubscription subscription = operation.Subscribe(recorder);
		operation.Report(0.10f, "a");
		operation.ReportIndeterminate("b");
		operation.Report(0.30f, "c");
		subscription.Dispose();
		operation.Report(0.90f, "d");

		LoadProgressSnapshot[] seen = recorder.Seen();
		Program.True(seen.Length == 4, $"通知数量异常：{seen.Length}");
		for (int i = 1; i < seen.Length; i++)
			Program.True(seen[i].Version > seen[i - 1].Version, "订阅收到了回退版本。");
		Program.True(seen[seen.Length - 1].Progress == 0.30f, "释放后的上报仍被投递。");
		Program.True(subscription.IsDisposed, "释放后句柄状态未更新。");
	}

	public static void SubscribeAfterTerminalReplaysTerminalOnce()
	{
		using var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.WorldGeneration, "生成世界");
		operation.Report(0.5f, "地形");
		operation.Fail(new LoadFailure("GenerationError", "生成世界", "boom"));
		var recorder = new Recorder();
		using (operation.Subscribe(recorder))
		{
			LoadProgressSnapshot[] seen = recorder.Seen();
			Program.True(seen.Length == 1 && seen[0].State == LoadState.Failed,
				"终态后订阅没有收到终态快照。");
			Program.True(seen[0].Failure?.Message == "boom", "终态快照丢失失败详情。");
		}
		operation.Report(0.9f, "迟到");
		Program.True(recorder.Seen().Length == 1, "终态后仍投递了进度。");
		Program.True(operation.State == LoadState.Failed, "终态被迟到上报改写。");
	}

	public static void ConcurrentReportsKeepVersionsMonotonic()
	{
		using var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "加载地图");
		var recorder = new Recorder();
		using var subscription = operation.Subscribe(recorder);
		using var start = new ManualResetEventSlim(false);

		var workers = new Task[4];
		for (int w = 0; w < workers.Length; w++)
		{
			int id = w;
			workers[w] = Task.Run(() =>
			{
				start.Wait(TimeSpan.FromSeconds(5));
				for (int i = 0; i < 250; i++) operation.Report((i % 100) / 100f, $"worker{id}");
			});
		}

		start.Set();
		Program.True(Task.WaitAll(workers, TimeSpan.FromSeconds(20)), "并发上报超时，可能发生死锁。");
		operation.Complete();

		LoadProgressSnapshot[] seen = recorder.Seen();
		Program.True(seen.Length >= 2, $"并发期间通知丢失过多：{seen.Length}");
		for (int i = 1; i < seen.Length; i++)
			Program.True(seen[i].Version > seen[i - 1].Version, "并发下版本顺序被打乱。");
		Program.True(seen[seen.Length - 1].State == LoadState.Completed
			&& seen[seen.Length - 1].Version == operation.Version, "最后一帧不是最终终态。");
	}

	public static void ReentrantReportDoesNotDeadlock()
	{
		using var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.Evolution, "演化计算");
		int calls = 0;
		var observer = new DelegateObserver(snapshot =>
		{
			if (Interlocked.Increment(ref calls) == 1) operation.Report(0.5f, "重入");
		});
		using (operation.Subscribe(observer))
		{
			operation.Report(0.1f, "首次");
		}
		Program.True(calls >= 2, $"重入上报没有送达：{calls}");
		Program.True(operation.Progress >= 0.5f, "重入上报未生效。");
	}

	public static void ObserverFailureDoesNotBlockOthers()
	{
		using var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "加载地图");
		var recorder = new Recorder();
		using (operation.Subscribe(new DelegateObserver(_ => throw new InvalidOperationException("boom"))))
		using (operation.Subscribe(recorder))
		{
			operation.Report(0.4f, "解码");
		}
		Program.True(recorder.Seen().Length == 2, "一个观察者抛错导致其他观察者丢帧。");
		Program.True(operation.State == LoadState.Running, "观察者异常污染了操作状态。");
	}

	public static void MailboxKeepsLatestAndSticksToTerminal()
	{
		Guid id = Guid.NewGuid();
		using var mailbox = new LoadProgressMailbox(id);
		mailbox.OnProgress(Snapshot(id, 1, 0.2f, LoadState.Running, "a"));
		mailbox.OnProgress(Snapshot(id, 3, 0.8f, LoadState.Running, "c"));
		mailbox.OnProgress(Snapshot(id, 2, 0.5f, LoadState.Running, "b"));
		mailbox.OnProgress(Snapshot(Guid.NewGuid(), 9, 0.9f, LoadState.Running, "other"));

		Program.True(mailbox.TryTakeLatest(out LoadProgressSnapshot first) && first.Version == 3,
			"邮箱未保留最新匹配快照。");
		Program.True(!mailbox.TryTakeLatest(out _), "同一快照被重复消费。");

		mailbox.OnProgress(Snapshot(id, 4, 1f, LoadState.Completed, "完成"));
		mailbox.OnProgress(Snapshot(id, 5, 0.5f, LoadState.Running, "迟到"));
		Program.True(mailbox.TryTakeLatest(out LoadProgressSnapshot terminal), "终态快照未被投递。");
		Program.True(terminal.State == LoadState.Completed, "终态被迟到的进度覆盖。");
	}

	public static void MailboxDropsSnapshotsAfterClose()
	{
		Guid id = Guid.NewGuid();
		var mailbox = new LoadProgressMailbox(id);
		mailbox.OnProgress(Snapshot(id, 1, 0.3f, LoadState.Running, "a"));
		mailbox.Dispose();
		mailbox.OnProgress(Snapshot(id, 2, 0.6f, LoadState.Running, "b"));
		Program.True(mailbox.Closed, "邮箱关闭标志未生效。");
		Program.True(!mailbox.TryTakeLatest(out _), "关闭后仍能取到快照。");
		Program.True(mailbox.Latest == null, "关闭后仍保留引用。");
	}

	public static void MailboxFollowsOperationSubscriptions()
	{
		using var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "加载地图");
		using var mailbox = new LoadProgressMailbox(operation.Id);
		using var subscription = operation.Subscribe(mailbox);
		operation.Report(0.45f, "并行解码 86/173");
		Program.True(mailbox.TryTakeLatest(out LoadProgressSnapshot snapshot) && snapshot.Progress == 0.45f,
			"操作通知没有进入邮箱。");
		operation.Complete();
		Program.True(mailbox.TryTakeLatest(out LoadProgressSnapshot done) && done.State == LoadState.Completed,
			"终态通知没有进入邮箱。");
	}

	public static void DisposeCancelsOperationAndReleasesObservers()
	{
		var recorder = new Recorder();
		var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.WorldGeneration, "生成世界");
		LoadProgressSubscription subscription = operation.Subscribe(recorder);
		operation.Dispose();
		Program.True(operation.State == LoadState.Cancelled, "释放未取消操作。");
		Program.True(operation.Token.IsCancellationRequested, "释放未触发操作令牌。");
		Program.True(recorder.Seen()[^1].State == LoadState.Cancelled, "释放未投递取消终态。");
		Program.Throws(() => operation.Subscribe(new Recorder()), "释放后仍允许订阅。");
		subscription.Dispose();
	}

	static LoadProgressSnapshot Snapshot(Guid id, long version, float progress, LoadState state, string stage)
		=> new(id, LoadFlow.MapBrowse, "加载地图", state, progress, stage, null, TimeSpan.Zero, version);

	sealed class DelegateObserver(Action<LoadProgressSnapshot> onProgress) : ILoadProgressObserver
	{
		public void OnProgress(LoadProgressSnapshot snapshot) => onProgress(snapshot);
	}
}
