using System;
using System.Diagnostics;

namespace World.Scene;

internal interface ILoadProgressObserver
{
	// Runs on the reporting thread; observers must not access Godot nodes here.
	void OnProgress(LoadProgressSnapshot snapshot);
}

internal sealed class LoadProgressSubscription : IDisposable
{
	readonly object _gate = new();
	ILoadProgressObserver _observer;
	Action<Guid> _unsubscribe;
	LoadProgressSnapshot? _pending;
	long _deliveredVersion = -1;
	bool _delivering;
	bool _disposed;

	internal LoadProgressSubscription(Guid operationId, Guid subscriptionId)
	{
		if (operationId == Guid.Empty) throw new ArgumentException("Operation identity is required.", nameof(operationId));
		if (subscriptionId == Guid.Empty) throw new ArgumentException("Subscription identity is required.", nameof(subscriptionId));
		OperationId = operationId;
		SubscriptionId = subscriptionId;
	}

	internal LoadProgressSubscription(Guid operationId, Guid subscriptionId,
		ILoadProgressObserver observer, Action<Guid> unsubscribe) : this(operationId, subscriptionId)
	{
		_observer = observer ?? throw new ArgumentNullException(nameof(observer));
		_unsubscribe = unsubscribe ?? throw new ArgumentNullException(nameof(unsubscribe));
	}

	internal Guid OperationId { get; }
	internal Guid SubscriptionId { get; }
	internal bool IsDisposed { get { lock (_gate) return _disposed; } }

	internal void Deliver(LoadProgressSnapshot snapshot)
	{
		lock (_gate)
		{
			if (_disposed || snapshot.Id != OperationId || snapshot.Version <= _deliveredVersion
				|| (_pending.HasValue && snapshot.Version <= _pending.Value.Version)) return;
			_pending = snapshot;
			if (_delivering) return;
			_delivering = true;
		}

		// Serialize callbacks without holding any lock across observer code. Reentrant
		// reports replace the pending snapshot instead of recursively invoking observers.
		while (true)
		{
			ILoadProgressObserver observer;
			LoadProgressSnapshot next;
			lock (_gate)
			{
				if (_disposed || !_pending.HasValue)
				{
					_delivering = false;
					return;
				}
				next = _pending.Value;
				_pending = null;
				_deliveredVersion = next.Version;
				observer = _observer;
			}
			try { observer?.OnProgress(next); }
			catch (Exception error) { Trace.TraceError("Load progress observer failed: {0}", error); }
		}
	}

	public void Dispose()
	{
		Action<Guid> unsubscribe;
		lock (_gate)
		{
			if (_disposed) return;
			_disposed = true;
			_pending = null;
			_observer = null;
			unsubscribe = _unsubscribe;
			_unsubscribe = null;
		}
		unsubscribe?.Invoke(SubscriptionId);
	}
}

internal sealed class LoadProgressMailbox : ILoadProgressObserver, IDisposable
{
	readonly object _gate = new();
	LoadProgressSnapshot? _latest;
	long _publishedVersion = -1;
	long _consumedVersion = -1;
	bool _closed;

	internal LoadProgressMailbox(Guid operationId)
	{
		if (operationId == Guid.Empty) throw new ArgumentException("Operation identity is required.", nameof(operationId));
		OperationId = operationId;
	}

	internal Guid OperationId { get; }
	internal LoadProgressSnapshot? Latest { get { lock (_gate) return _latest; } }
	internal long PublishedVersion { get { lock (_gate) return _publishedVersion; } }
	internal long ConsumedVersion { get { lock (_gate) return _consumedVersion; } }
	internal bool Closed { get { lock (_gate) return _closed; } }

	public void OnProgress(LoadProgressSnapshot snapshot)
	{
		lock (_gate)
		{
			if (_closed || snapshot.Id != OperationId || snapshot.Version <= _publishedVersion
				|| (_latest.HasValue && _latest.Value.State != LoadState.Running)) return;
			_latest = snapshot;
			_publishedVersion = snapshot.Version;
		}
	}

	internal bool TryTakeLatest(out LoadProgressSnapshot snapshot)
	{
		lock (_gate)
		{
			if (_closed || !_latest.HasValue || _publishedVersion <= _consumedVersion)
			{
				snapshot = default;
				return false;
			}
			snapshot = _latest.Value;
			_consumedVersion = snapshot.Version;
			return true;
		}
	}

	public void Dispose()
	{
		lock (_gate)
		{
			_closed = true;
			_latest = null;
		}
	}
}
