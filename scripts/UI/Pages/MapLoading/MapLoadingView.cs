using Godot;
using World.UI.Components;

namespace World.UI.Pages;

/// <summary>从开始读档到地图就绪，呈现同一条总进度。</summary>
public partial class MapLoadingView : CanvasLayer
{
	Label _status;
	LoadProgressBar _progress;

	internal static MapLoadingView Create()
		=> GD.Load<PackedScene>("res://scenes/pages/MapLoading/MapLoadingPage.tscn").Instantiate<MapLoadingView>();

	public override void _Ready()
	{
		_status = GetNode<Label>("%Status");
		_progress = GetNode<LoadProgressBar>("%Progress");
	}

	internal void Begin()
	{
		_status.Hide();
		_progress.Init(100, "加载地图");
		ShowProgress(0, "准备读取地图");
	}

	internal void ShowProgress(int percent, string stage)
		=> _progress.Update(percent, stage, indeterminate: percent == 0);
}
