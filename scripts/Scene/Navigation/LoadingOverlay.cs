using Godot;
using World.Utils;

namespace World.Scene;

/// <summary>页面加载常驻层：Modal 遮罩，轮询绑定操作的快照并刷新视图。</summary>
public partial class LoadingOverlay : CanvasLayer
{
	[Export] public NodePath ViewPath { get; set; }
	[Export] public NodePath DimPath { get; set; }

	LoadProgressView _view;
	Control _dim;
	LoadOperation _operation;
	long _version = -1;
	double _idle;

	public override void _Ready()
	{
		_view = this.Require<LoadProgressView>(ViewPath, nameof(ViewPath));
		_dim = this.Require<Control>(DimPath, nameof(DimPath));
		Visible = false;
	}

	internal void Bind(LoadOperation operation)
	{
		_operation = operation;
		_version = -1;
		_idle = 0;
		Visible = true;
	}

	internal void Unbind()
	{
		_operation = null;
		Visible = false;
	}

	public override void _Process(double delta)
	{
		if (_operation == null) return;
		LoadProgressSnapshot snapshot = _operation.Snapshot();
		if (snapshot.State != LoadState.Running)
		{
			Unbind();
			return;
		}

		// 即使阶段内没有新的进度上报，也要至少每 100 ms 刷新一次已用时间。
		_idle += delta;
		if (snapshot.Version == _version && _idle < 0.1) return;
		_idle = 0;
		_version = snapshot.Version;
		_view.Refresh(snapshot);
	}
}
