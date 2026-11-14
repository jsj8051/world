using System;
using World.Application.Loading;

namespace World.UI.Components;

/// <summary>
/// 进度视图的观察端：持有邮箱与订阅，把后台快照按 0.1 秒节流交给 <see cref="LoadProgressView"/>。
/// 只在主线程调用；不决定进度层自身的显隐（那是各宿主的事）。
/// </summary>
internal sealed class LoadProgressPresenter
{
	const double RefreshIntervalSeconds = 0.1;

	LoadProgressView _view;
	LoadProgressMailbox _mailbox;
	LoadProgressSubscription _subscription;
	LoadProgressSnapshot? _latest;
	double _idle;
	double _sinceSnapshot;

	/// <summary>是否已绑定操作。</summary>
	internal bool IsBound => _mailbox != null;

	/// <summary>最近一次收到的快照；未收到任何快照时为空。</summary>
	internal LoadProgressSnapshot? Latest => _latest;

	/// <summary>绑定一次操作到视图；已有绑定时先解绑，并立即呈现当前快照。</summary>
	internal void Bind(LoadOperation operation, LoadProgressView view)
	{
		if (operation == null) throw new ArgumentNullException(nameof(operation));
		if (view == null) throw new ArgumentNullException(nameof(view));
		Detach();
		_view = view;
		_mailbox = new LoadProgressMailbox(operation.Id);
		_subscription = operation.Subscribe(_mailbox);
		Tick(0);
	}

	/// <summary>停止观察；之后 <see cref="Tick"/> 不再刷新视图。</summary>
	internal void Detach()
	{
		_mailbox?.Dispose();
		_mailbox = null;
		_subscription?.Dispose();
		_subscription = null;
		_latest = null;
		_idle = 0;
		_sinceSnapshot = 0;
	}

	/// <summary>主线程每帧调用：新快照立即刷新，等待期间用本地时钟补足已用时间。</summary>
	internal void Tick(double delta)
	{
		if (_mailbox == null || _view == null) return;
		if (_mailbox.TryTakeLatest(out LoadProgressSnapshot snapshot))
		{
			_latest = snapshot;
			_idle = 0;
			_sinceSnapshot = 0;
			_view.Refresh(snapshot, snapshot.Elapsed);
			return;
		}

		if (_latest is not LoadProgressSnapshot latest || latest.State != LoadState.Running) return;
		_idle += delta;
		_sinceSnapshot += delta;
		if (_idle < RefreshIntervalSeconds) return;
		_idle = 0;
		_view.Refresh(latest, latest.Elapsed + TimeSpan.FromSeconds(_sinceSnapshot));
	}
}
