using System;
using Godot;
using World.Application.Loading;
using World.Data.Work;
using World.UI.Components;
using World.Utils;

namespace World.UI.Pages;

/// <summary>从开始读档到地图就绪，呈现同一条总进度。</summary>
public partial class MapLoadingView : CanvasLayer
{
	Label _status;
	LoadProgressBar _progress;
	WorldGenerationFlow _generation;
	bool _generationMode;

	internal static MapLoadingView Create()
		=> GD.Load<PackedScene>("res://scenes/pages/MapLoading/MapLoadingPage.tscn").Instantiate<MapLoadingView>();

	internal static MapLoadingView CreateGeneration(WorldGenerationFlow generation)
	{
		ArgumentNullException.ThrowIfNull(generation);
		var page = Create();
		page._generation = generation;
		page._generationMode = true;
		return page;
	}

	public override void _Ready()
	{
		_status = GetNode<Label>("%Status");
		_progress = GetNode<LoadProgressBar>("%Progress");
		if (_generationMode)
		{
			try { BeginGeneration(); }
			catch (Exception error)
			{
				_generation?.Dispose();
				_generation = null;
				SceneReplacement.Replace(this, GenerationConfigView.Create(error.Message));
			}
		}
	}

	internal void Begin()
	{
		_generationMode = false;
		_status.Text = "正在加载地图";
		_status.Show();
		_progress.Hide();
	}

	void BeginGeneration()
	{
		_status.Hide();
		_progress.Init(100, "生成地形");
		ShowProgress(0, "正在生成世界");
		_generation.Start();
		SetProcess(true);
	}

	public override void _Process(double delta)
	{
		if (!_generationMode || _generation == null) return;
		try
		{
			WorldGenerationOutcome outcome = _generation.Poll();
			if (outcome.State == WorldGenerationState.Running)
			{
				string stage = null;
				while (_generation.TryReadProgress(out WorkSignal signal))
					if (signal.Kind == WorkSignalKind.StageStarted) stage = signal.Stage;
				if (stage != null) ShowProgress(0, stage);
				return;
			}
			_generation.Dispose();
			_generation = null;
			_generationMode = false;
			if (outcome.State == WorldGenerationState.Failed)
			{
				SceneReplacement.Replace(this, GenerationConfigView.Create(outcome.Failure.Message));
				return;
			}
			if (outcome.State == WorldGenerationState.Succeeded)
				SceneReplacement.Replace(this, GeographyEvolutionView.Create(outcome.Result));
		}
		catch (Exception error)
		{
			_generation?.Dispose();
			_generation = null;
			SceneReplacement.Replace(this, GenerationConfigView.Create(error.Message));
		}
	}

	public override void _ExitTree()
	{
		_generation?.Dispose();
		_generation = null;
	}

	internal void ShowProgress(int percent, string stage)
		=> _progress.Update(percent, stage, indeterminate: percent == 0);
}
