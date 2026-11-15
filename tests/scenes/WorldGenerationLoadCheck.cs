using System;
using Godot;
using World.Application.Generation;
using World.Application.Loading;
using World.Data;
using World.Data.Generation;

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
		WorldSpec spec = WorldGenerationSpecs.Create(new GenerationConfig());
		using var flow = new WorldGenerationFlow(spec, WorldGenerationPresets.Production);

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

		bool ok = outcome.State == WorldGenerationState.Succeeded && outcome.Result?.Simulation != null;
		GD.Print($"[GEN-CHECK] ok={ok} state={outcome.State} ms={Time.GetTicksMsec() - started} maxGap={maxGap}");
		GetTree().Quit(ok ? 0 : 1);
	}
}
