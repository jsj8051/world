using Godot;
using World.Application.Components;
using World.Utils;

namespace World.UI.Components;

/// <summary>进度条组件视图：把 <see cref="LoadProgressController"/> 的读数写到标题、条与计数标签，完成后自行销毁。</summary>
/// <example><code>
/// _bar.Init(7, "装配地图");
/// _bar.Update(3, "构建地形块");
/// _bar.Complete();
/// </code></example>
public partial class LoadProgressBar : VBoxContainer
{
	[Export] public NodePath TitlePath { get; set; }
	[Export] public NodePath BarPath { get; set; }
	[Export] public NodePath StagePath { get; set; }
	[Export] public NodePath PercentPath { get; set; }
	[Export] public bool ShowPercentage { get; set; }

	readonly LoadProgressController _controller = new();

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
		_bar.ShowPercentage = ShowPercentage;
		_percent.Visible = !ShowPercentage;
	}

	/// <summary>初始化：总共 <paramref name="total"/> 项，计数归零并显示。</summary>
	public void Init(int total, string title)
	{
		_controller.Init(total, title);
		_bar.Indeterminate = false;
		Visible = true;
		Render();
	}

	/// <summary>更新当前准备进行的项序号与名称；百分比由组件按总数算。</summary>
	public void Update(int step, string stage, bool indeterminate = false)
	{
		_controller.Update(step, stage);
		if (!_controller.IsActive) return;
		if (_bar.Indeterminate != indeterminate) _bar.Indeterminate = indeterminate;
		Render();
	}

	/// <summary>置满并销毁自身。</summary>
	public void Complete()
	{
		if (!_controller.IsActive) return;
		_controller.Complete();
		QueueFree();
	}

	void Render()
	{
		_title.Text = _controller.Title;
		_bar.MaxValue = _controller.Total;
		_bar.Value = _controller.Step;
		_stage.Text = _controller.Stage;
		_percent.Text = $"{_controller.Step}/{_controller.Total}";
	}
}
