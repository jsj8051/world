using Godot;
using World.Application.Components;
using World.Utils;

namespace World.UI.Components;

/// <summary>进度条组件视图：把 <see cref="LoadProgressController"/> 的读数写到标题、条与计数标签，完成后自行销毁。</summary>
/// <example><code>
/// _bar.Init(7, "装配地图");
/// _bar.Update(3, 7, "构建地形块");
/// _bar.Complete();
/// </code></example>
public partial class LoadProgressBar : VBoxContainer
{
	[Export] public NodePath TitlePath { get; set; }
	[Export] public NodePath BarPath { get; set; }
	[Export] public NodePath StagePath { get; set; }
	[Export] public NodePath PercentPath { get; set; }

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
	}

	/// <summary>初始化：总共 <paramref name="total"/> 项，计数归零。</summary>
	public void Init(int total, string title)
	{
		_controller.Init(total, title);
		Render();
	}

	/// <summary>更新到 <paramref name="done"/>/<paramref name="total"/> 与阶段文案。</summary>
	public void Update(int done, int total, string stage)
	{
		_controller.Update(done, total, stage);
		if (_controller.IsActive) Render();
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
		_bar.Value = _controller.Done;
		_stage.Text = _controller.Stage;
		_percent.Text = $"{_controller.Done}/{_controller.Total}";
	}
}
