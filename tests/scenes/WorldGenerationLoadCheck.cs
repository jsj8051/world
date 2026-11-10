using Godot;
using System;
using System.Threading;
using World.Data.Generation;
using World.Scene;

namespace World.Tests;

/// <summary>
/// 生成端到端检查：实例化 WorldGenWorld，走异步生成 + 分帧渲染，等待操作终态。
/// 用法：godot --headless --path . res://tests/scenes/WorldGenerationLoadCheck.tscn
/// </summary>
public partial class WorldGenerationLoadCheck : Node
{
	sealed class Sink : ILoadProgressSink
	{
		public LoadOperation Current;

		public bool HasActive => Current != null && !Current.IsTerminal;

		public LoadOperation Begin(LoadFlow flow, string title)
		{
			Current = new LoadOperation(Guid.NewGuid(), flow, title, new CancellationTokenSource());
			return Current;
		}
	}

	public override async void _Ready()
	{
		var packed = ResourceLoader.Load<PackedScene>("res://scenes/core/WorldGenWorld.tscn");
		if (packed == null)
		{
			GD.PushError("[GEN-CHECK] WorldGenWorld 场景缺失");
			GetTree().Quit(2);
			return;
		}

		var root = packed.Instantiate<WorldRoot>();
		var sink = new Sink();
		((IAppPage)root).Configure(new PageContext(null, CancellationToken.None, sink));
		Exception failure = null;
		((IGenerationPage)root).ConfigureGeneration(new GenerationConfig(), error => failure = error);
		AddChild(root);

		ulong started = Time.GetTicksMsec();
		ulong last = started;
		ulong maxGap = 0;
		while (sink.HasActive && Time.GetTicksMsec() - started < 120_000)
		{
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			ulong now = Time.GetTicksMsec();
			if (now - last > maxGap) maxGap = now - last;
			last = now;
		}

		bool ok = !sink.HasActive && failure == null && root.World?.IsInitialized == true;
		GD.Print($"[GEN-CHECK] ok={ok} ms={Time.GetTicksMsec() - started} maxGap={maxGap} " +
			$"state={sink.Current?.State.ToString() ?? "null"} error={failure?.Message ?? "none"}");
		GetTree().Quit(ok ? 0 : 1);
	}
}
