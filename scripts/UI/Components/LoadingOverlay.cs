using System;
using Godot;
using World.Application.Loading;
using World.Utils;

namespace World.UI.Components;

/// <summary>
/// 一次加载的进度层：订阅后台快照，仅在主线程刷新视图。
/// 生命周期归外壳（导航层的 AppLoadCoordinator），不自释放。
/// </summary>
public partial class LoadingOverlay : CanvasLayer
{
	[Export] public NodePath ViewPath { get; set; }
	[Export] public NodePath DimPath { get; set; }

	readonly LoadProgressPresenter _presenter = new();
	LoadProgressView _view;

	public override void _Ready()
	{
		_view = this.Require<LoadProgressView>(ViewPath, nameof(ViewPath));
		// Dim 只要求场景里存在（全屏遮挡 + 拦截输入），这里不做逻辑引用。
		this.Require<Control>(DimPath, nameof(DimPath));
		Visible = false;
	}

	/// <summary>主线程绑定进度观察者；后台通知只写入纯 .NET 邮箱。</summary>
	internal void Bind(LoadOperation operation)
	{
		if (operation == null) throw new ArgumentNullException(nameof(operation));
		_presenter.Bind(operation, _view);
		Visible = true;
	}

	void Unbind()
	{
		_presenter.Detach();
		Visible = false;
	}

	public override void _Process(double delta) => _presenter.Tick(delta);

	public override void _ExitTree() => Unbind();
}
