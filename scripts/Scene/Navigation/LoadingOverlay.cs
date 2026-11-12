using System;
using Godot;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 一次加载的进度层：订阅后台快照，仅在主线程刷新视图；终态时自隐藏并通知外壳销毁本实例。
/// </summary>
public partial class LoadingOverlay : CanvasLayer
{
	[Export] public NodePath ViewPath { get; set; }
	[Export] public NodePath DimPath { get; set; }

	/// <summary>一次加载走到终态、视图已隐藏；外壳据此释放本实例。</summary>
	internal event Action Finished;

	LoadProgressView _view;
	Control _dim;
	LoadProgressMailbox _mailbox;
	LoadProgressSubscription _subscription;
	LoadProgressSnapshot? _latest;
	double _sinceSnapshot;
	double _idle;

	public override void _Ready()
	{
		_view = this.Require<LoadProgressView>(ViewPath, nameof(ViewPath));
		_dim = this.Require<Control>(DimPath, nameof(DimPath));
		Visible = false;
	}

	/// <summary>主线程绑定进度观察者；后台通知只写入纯 .NET 邮箱。</summary>
	internal void Bind(LoadOperation operation)
	{
		if (operation == null) throw new ArgumentNullException(nameof(operation));
		Unbind();
		_mailbox = new LoadProgressMailbox(operation.Id);
		_subscription = operation.Subscribe(_mailbox);
		_idle = 0;
		_sinceSnapshot = 0;
		Visible = true;
	}

	void Unbind()
	{
		_mailbox?.Dispose();
		_mailbox = null;
		_subscription?.Dispose();
		_subscription = null;
		_latest = null;
		_idle = 0;
		Visible = false;
	}

	public override void _Process(double delta)
	{
		if (_mailbox == null) return;
		if (_mailbox.TryTakeLatest(out LoadProgressSnapshot snapshot))
		{
			_latest = snapshot;
			_sinceSnapshot = 0;
			_idle = 0;
			if (snapshot.State != LoadState.Running)
			{
				Unbind();
				Finished?.Invoke();
				return;
			}
			_view.Refresh(snapshot, snapshot.Elapsed);
			return;
		}

		if (_latest is not LoadProgressSnapshot latest) return;
		_idle += delta;
		_sinceSnapshot += delta;
		if (_idle < 0.1) return;
		_idle = 0;
		_view.Refresh(latest, latest.Elapsed + TimeSpan.FromSeconds(_sinceSnapshot));
	}

	public override void _ExitTree() => Unbind();
}
