using System;
using System.IO;
using System.Threading;
using World.Application.Loading;

namespace World.Tests;

/// <summary>第 4 步结构测试：只验证进度类型、阶段计划与边界，不推进任何加载。</summary>
internal static class LoadStructureTests
{
	public static void OperationValidatesIdentityAndState()
	{
		Program.Throws(() => new LoadOperation(Guid.Empty, LoadFlow.MapBrowse, "x", CancellationToken.None), "空操作标识被接受");
		Program.Throws(() => new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, " ", CancellationToken.None), "空标题被接受");
		Program.Throws(() => new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse,
			new string('x', LoadContract.MaxTitleLength + 1), CancellationToken.None), "超长标题被接受");
		Program.Throws(() => new LoadOperation(Guid.NewGuid(), (LoadFlow)99, "x", CancellationToken.None), "未知入口被接受");

		var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, " 加载地图 ", CancellationToken.None);
		Program.True(operation.State == LoadState.Running, "初始状态不是 Running");
		Program.True(operation.Progress == null && operation.Stage == string.Empty, "初始进度/阶段不是空");
		Program.True(!operation.IsTerminal && operation.Version == 0, "初始终态/版本错误");

		LoadProgressSnapshot snapshot = operation.Snapshot();
		Program.True(snapshot.Id == operation.Id && snapshot.Title == "加载地图", "快照身份/标题不一致");
		Program.True(snapshot.State == LoadState.Running && snapshot.Progress == null, "快照初始状态错误");
	}

	public static void SnapshotEnforcesTerminalInvariants()
	{
		Guid id = Guid.NewGuid();
		Program.Throws(() => new LoadProgressSnapshot(id, LoadFlow.MapBrowse, "t",
			LoadState.Completed, 0.5f, "完成", null, TimeSpan.Zero, 1), "Completed 非 100% 被接受");
		Program.Throws(() => new LoadProgressSnapshot(id, LoadFlow.MapBrowse, "t",
			LoadState.Failed, 0.3f, "读取与校验", null, TimeSpan.Zero, 1), "Failed 缺失败详情被接受");
		Program.Throws(() => new LoadProgressSnapshot(id, LoadFlow.MapBrowse, "t",
			LoadState.Running, 1.5f, "x", null, TimeSpan.Zero, 1), "越界进度被接受");
		Program.Throws(() => new LoadProgressSnapshot(id, LoadFlow.MapBrowse, "t",
			LoadState.Running, 0.2f, "", null, TimeSpan.Zero, 1), "有进度但阶段为空被接受");

		var failure = new LoadFailure("ChecksumMismatch", "读取与校验", "校验和不匹配");
		var failed = new LoadProgressSnapshot(id, LoadFlow.MapBrowse, "t",
			LoadState.Failed, 0.3f, "读取与校验", failure, TimeSpan.FromSeconds(1), 2);
		Program.True(ReferenceEquals(failed.Failure, failure), "失败详情丢失");

		var completed = new LoadProgressSnapshot(id, LoadFlow.MapBrowse, "t",
			LoadState.Completed, 1f, "完成", null, TimeSpan.FromSeconds(2), 3);
		Program.True(completed.Progress == 1f, "完成进度不是 1");
	}

	public static void StagePlansAreWellFormed()
	{
		Program.Near(LoadStagePlans.Browse.TotalWeight, 1.0, 1e-9, "浏览阶段权重总和不为一");
		Program.Near(LoadStagePlans.Generation.TotalWeight, 1.0, 1e-9, "生成阶段权重总和不为一");
		Program.True(LoadStagePlans.Browse.Stages.Count == 4, "浏览阶段数错误");
		Program.True(LoadStagePlans.Generation.Stages.Count == 6, "生成阶段数错误");

		Program.Throws(() => new LoadStagePlan(LoadFlow.MapBrowse, new[]
		{
			new LoadStage("a", "A", 0.5), new LoadStage("a", "B", 0.5),
		}), "重复阶段标识被接受");
		Program.Throws(() => new LoadStagePlan(LoadFlow.MapBrowse, new[]
		{
			new LoadStage("a", "A", 0.4), new LoadStage("b", "B", 0.4),
		}), "权重总和非 1 被接受");
		Program.Throws(() => new LoadStagePlan(LoadFlow.Evolution, new[]
		{
			new LoadStage("a", "A", 0.5), new LoadStage("b", "B", 0.5),
		}), "演化加权计划被接受");
		Program.Throws(() => new LoadStage("BAD", "x", 1), "非法阶段标识被接受");
		Program.Throws(() => new LoadStage("ok", "x", 0), "零权重被接受");
	}

	public static void OperationTransitionsAreMonotonic()
	{
		var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "加载地图", CancellationToken.None);
		operation.Report(0.40f, "并行解码 40/173");
		Program.True(operation.Progress == 0.40f, "首次进度未写入");

		long version = operation.Version;
		operation.Report(0.20f, "并行解码 20/173");
		Program.True(operation.Progress == 0.40f, "进度回退未被钳制");
		Program.True(operation.Version == version + 1, "低报也应更新阶段与版本");

		Program.Throws(() => operation.Report(float.NaN, "x"), "NaN 进度被接受");
		Program.Throws(() => operation.Report(0.5f, ""), "空阶段被接受");

		operation.Complete();
		Program.True(operation.State == LoadState.Completed && operation.Progress == 1f, "Complete 未置 100%");
		long terminal = operation.Version;
		operation.Report(0.1f, "迟到");
		operation.Fail(new LoadFailure("x", "y", "z"));
		Program.True(operation.Version == terminal && operation.State == LoadState.Completed, "终态被改写");
	}

	public static void CancelCancelsToken()
	{
		using var cancellation = new CancellationTokenSource();
		var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.WorldGeneration, "生成世界", cancellation);
		operation.Cancel();
		Program.True(operation.State == LoadState.Cancelled, "取消未置终态");
		Program.True(cancellation.IsCancellationRequested, "取消未触发令牌");

		operation.Complete();
		Program.True(operation.State == LoadState.Cancelled, "取消后被完成");
	}
}
