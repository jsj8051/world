using System;
using Godot;
using World.Application.Loading;
using World.Utils;

namespace World.UI.Components;

/// <summary>可复用进度视图：标题、进度条、阶段与百分比。只渲染快照，不持有操作。</summary>
public partial class LoadProgressView : VBoxContainer
{
	[Export] public NodePath TitlePath { get; set; }
	[Export] public NodePath BarPath { get; set; }
	[Export] public NodePath StagePath { get; set; }
	[Export] public NodePath PercentPath { get; set; }

	Label _title;
	Label _stage;
	Label _percent;
	ProgressBar _bar;

	public override void _Ready()
	{
		_title = this.Require<Label>(TitlePath, nameof(TitlePath));
		_bar = this.Require<ProgressBar>(BarPath, nameof(BarPath));
		_stage = this.Require<Label>(StagePath, nameof(StagePath));
		_percent = this.Require<Label>(PercentPath, nameof(PercentPath));
	}

	/// <summary>在主线程显示进度快照，并可用本地时钟补偿快照之后的等待时间。</summary>
	/// <example><code>view.Refresh(snapshot, snapshot.Elapsed);</code></example>
	internal void Refresh(LoadProgressSnapshot snapshot, TimeSpan elapsed)
	{
		if (_bar == null) return;
		_title.Text = snapshot.Title;
		if (snapshot.Progress is float progress)
		{
			int percent = (int)Math.Round(progress * 100f);
			_bar.Value = percent;
			_percent.Text = $"{percent}%";
		}
		else
		{
			_bar.Value = 0;
			_percent.Text = "处理中";
		}
		_stage.Text = $"{snapshot.Stage} · {elapsed.TotalSeconds:F1}s";
	}
}
