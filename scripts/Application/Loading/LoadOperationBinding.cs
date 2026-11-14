using System;
using System.Threading;
using World.Application.Pages;

namespace World.Application.Loading;

/// <summary>
/// 一次加载操作与"谁有权提交"的绑定：操作、所属页面身份、页面生命周期。
/// 页面身份与生命周期都取自挂载时的 <see cref="PageContext"/>，转交时由协调器重建。
/// </summary>
internal sealed class LoadOperationBinding
{
	internal LoadOperationBinding(LoadOperation operation, Guid ownerPageId, CancellationToken ownerLifetime)
	{
		Operation = operation ?? throw new ArgumentNullException(nameof(operation));
		if (ownerPageId == Guid.Empty) throw new ArgumentException("页面标识不能为空。", nameof(ownerPageId));
		OwnerPageId = ownerPageId;
		OwnerLifetime = ownerLifetime;
	}

	internal LoadOperation Operation { get; }
	internal Guid OwnerPageId { get; }
	internal CancellationToken OwnerLifetime { get; }
}

/// <summary>工作线程上下文：把操作令牌与页面生命周期合成一个可取消令牌。</summary>
internal sealed class LoadWorkContext : IDisposable
{
	readonly CancellationTokenSource _linkedCancellation;

	internal LoadWorkContext(LoadOperationBinding binding)
	{
		if (binding == null) throw new ArgumentNullException(nameof(binding));
		_linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			binding.Operation.Token, binding.OwnerLifetime);
		Token = _linkedCancellation.Token;
	}

	internal CancellationToken Token { get; }

	public void Dispose() => _linkedCancellation.Dispose();
}

/// <summary>
/// 提交门：操作未终态、令牌未取消、绑定仍属于当前页面时才允许提交。
/// 页面自己持有 binding 时页面标识恒等，真正的判别发生在"当前挂载页"与绑定页不同的场合（浏览转交）。
/// </summary>
internal static class LoadCommitGuard
{
	internal static bool IsCurrent(LoadOperationBinding binding, Guid currentPageId)
		=> binding != null
			&& binding.OwnerPageId == currentPageId
			&& !binding.OwnerLifetime.IsCancellationRequested
			&& !binding.Operation.Token.IsCancellationRequested
			&& binding.Operation.State != LoadState.Cancelled;

	internal static bool CanCommit(LoadOperationBinding binding, Guid currentPageId)
		=> IsCurrent(binding, currentPageId) && binding.Operation.State == LoadState.Running;
}
