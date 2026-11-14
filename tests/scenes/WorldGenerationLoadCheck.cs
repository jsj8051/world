using System;
using System.Threading;
using Godot;
using World.Data;
using World.Data.Generation;
using World.Application.Loading;
using World.Scene;

namespace World.Tests;

/// <summary>
/// 世界生成流程端到端检查：不经页面，直接驱动 <see cref="WorldGenerationFlow"/> 跑后台 Job，
/// 等待轮询到终态并统计主线程最大停顿。
/// 用法：godot --headless --path . res://tests/scenes/WorldGenerationLoadCheck.tscn
/// </summary>
public partial class WorldGenerationLoadCheck : Node
{
	public override async void _Ready()
	{
		LoadOperation operation = new(Guid.NewGuid(), LoadFlow.WorldGeneration, "生成世界");
		Guid pageId = Guid.NewGuid();
		var binding = new LoadOperationBinding(operation, pageId, CancellationToken.None);
		WorldSpec spec = WorldGenerationSpecs.Create(new GenerationConfig());
		using var flow = new WorldGenerationFlow(operation, binding, () => pageId, spec,
			WorldGenerationPresets.Production);

		ulong started = Time.GetTicksMsec();
		ulong last = started;
		ulong maxGap = 0;
		WorldGenerationOutcome outcome = WorldGenerationOutcome.Running;
		flow.Start();
		while (outcome.State == WorldGenerationState.Running && Time.GetTicksMsec() - started < 120_000)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			ulong now = Time.GetTicksMsec();
			if (now - last > maxGap) maxGap = now - last;
			last = now;
			outcome = flow.Poll();
		}

		// 流程只推进到 0.70，收尾（Complete）归世界页；这里断言的是后台产物与操作仍可提交。
		bool ok = outcome.State == WorldGenerationState.Succeeded
			&& outcome.Result?.Simulation != null
			&& operation.State == LoadState.Running;
		GD.Print($"[GEN-CHECK] ok={ok} state={outcome.State} operation={operation.State} " +
			$"ms={Time.GetTicksMsec() - started} maxGap={maxGap}");
		GetTree().Quit(ok ? 0 : 1);
	}
}
