using System;

namespace World.Scene;

internal static class LoadCommitGuard
{
	internal static bool IsCurrent(LoadCommitKey candidate, LoadOperationBinding current, Guid currentPageId)
		=> current != null && candidate.OperationId != Guid.Empty && candidate.OwnerPageId != Guid.Empty
			&& candidate.OperationId == current.Operation.Id
			&& candidate.OperationId == current.Key.OperationId
			&& candidate.OwnerPageId == current.Key.OwnerPageId
			&& candidate.OwnerPageId == currentPageId
			&& !current.OwnerLifetime.IsCancellationRequested
			&& !current.Operation.Token.IsCancellationRequested
			&& current.Operation.State != LoadState.Cancelled;

	internal static bool CanCommit(LoadCommitKey candidate, LoadOperationBinding current, Guid currentPageId)
		=> IsCurrent(candidate, current, currentPageId) && current.Operation.State == LoadState.Running;
}
