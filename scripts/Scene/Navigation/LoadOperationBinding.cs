using System;
using System.Threading;

namespace World.Scene;

internal readonly struct LoadCommitKey
{
	internal LoadCommitKey(Guid operationId, Guid ownerPageId)
	{
		if (operationId == Guid.Empty) throw new ArgumentException("操作标识不能为空。", nameof(operationId));
		if (ownerPageId == Guid.Empty) throw new ArgumentException("页面标识不能为空。", nameof(ownerPageId));
		OperationId = operationId;
		OwnerPageId = ownerPageId;
	}

	internal Guid OperationId { get; }
	internal Guid OwnerPageId { get; }
}

internal sealed class LoadOperationBinding
{
	internal LoadOperationBinding(LoadOperation operation, Guid ownerPageId, CancellationToken ownerLifetime)
	{
		Operation = operation ?? throw new ArgumentNullException(nameof(operation));
		Key = new LoadCommitKey(operation.Id, ownerPageId);
		OwnerLifetime = ownerLifetime;
	}

	internal LoadOperation Operation { get; }
	internal LoadCommitKey Key { get; }
	internal CancellationToken OwnerLifetime { get; }
	internal LoadFlow Flow => Operation.Flow;
}

internal sealed class LoadWorkContext : IDisposable
{
	readonly CancellationTokenSource _linkedCancellation;

	internal LoadWorkContext(LoadOperationBinding binding)
	{
		if (binding == null) throw new ArgumentNullException(nameof(binding));
		Key = binding.Key;
		Flow = binding.Operation.Flow;
		_linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			binding.Operation.Token, binding.OwnerLifetime);
		Token = _linkedCancellation.Token;
	}

	internal LoadCommitKey Key { get; }
	internal LoadFlow Flow { get; }
	internal CancellationToken Token { get; }

	public void Dispose() => _linkedCancellation.Dispose();
}
