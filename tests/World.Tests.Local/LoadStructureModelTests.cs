using System;
using System.Threading;
using World.Scene;

namespace World.Tests;

internal static class LoadStructureModelTests
{
	public static void MailboxValidatesIdentityAndInitialState()
	{
		Guid operationId = Guid.NewGuid();
		var mailbox = new LoadProgressMailbox(operationId);
		Program.True(mailbox.OperationId == operationId, "Mailbox identity changed.");
		Program.True(mailbox.Latest == null && !mailbox.Closed, "Mailbox must start open and empty.");
		Program.True(mailbox.PublishedVersion == -1 && mailbox.ConsumedVersion == -1,
			"Mailbox versions must start before the first operation version.");
		Program.Throws(() => new LoadProgressMailbox(Guid.Empty), "Empty mailbox identity was accepted.");
	}

	public static void SubscriptionValidatesIdentityAndInitialState()
	{
		Guid operationId = Guid.NewGuid(), subscriptionId = Guid.NewGuid();
		var subscription = new LoadProgressSubscription(operationId, subscriptionId);
		Program.True(subscription.OperationId == operationId && subscription.SubscriptionId == subscriptionId,
			"Subscription identity changed.");
		Program.True(!subscription.IsDisposed, "Subscription must start active.");
		Program.Throws(() => new LoadProgressSubscription(Guid.Empty, subscriptionId),
			"Empty operation identity was accepted.");
		Program.Throws(() => new LoadProgressSubscription(operationId, Guid.Empty),
			"Empty subscription identity was accepted.");
	}

	public static void BindingLinksOperationAndPageCancellation()
	{
		using var operationCancellation = new CancellationTokenSource();
		using var pageLifetime = new CancellationTokenSource();
		var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "Load map", operationCancellation);
		Guid pageId = Guid.NewGuid();
		var binding = new LoadOperationBinding(operation, pageId, pageLifetime.Token);
		using var work = new LoadWorkContext(binding);
		Program.True(ReferenceEquals(binding.Operation, operation) && binding.OwnerLifetime == pageLifetime.Token,
			"Binding must retain the operation and page lifetime without copying state.");
		Program.True(work.Key.OperationId == operation.Id && work.Key.OwnerPageId == pageId,
			"Work context must retain both identities.");
		Program.True(binding.Flow == operation.Flow && work.Flow == operation.Flow && !work.Token.IsCancellationRequested,
			"Work flow or initial cancellation is incorrect.");

		pageLifetime.Cancel();
		Program.True(work.Token.IsCancellationRequested, "Page cancellation did not reach the work token.");
		Program.True(!operation.Token.IsCancellationRequested, "Page cancellation must not cancel the operation token.");

		using var secondPageLifetime = new CancellationTokenSource();
		var secondBinding = new LoadOperationBinding(operation, Guid.NewGuid(), secondPageLifetime.Token);
		using var secondWork = new LoadWorkContext(secondBinding);
		operation.Cancel();
		Program.True(secondWork.Token.IsCancellationRequested, "Operation cancellation did not reach the work token.");
		Program.True(!secondPageLifetime.IsCancellationRequested, "Operation cancellation must not cancel the page.");
		Program.Throws(() => new LoadCommitKey(Guid.Empty, pageId), "Empty operation key was accepted.");
		Program.Throws(() => new LoadCommitKey(operation.Id, Guid.Empty), "Empty page key was accepted.");
		Program.Throws(() => new LoadOperationBinding(null, pageId, CancellationToken.None), "Null operation was accepted.");
		Program.Throws(() => new LoadOperationBinding(operation, Guid.Empty, CancellationToken.None), "Empty binding owner was accepted.");
		Program.Throws(() => new LoadWorkContext(null), "Null work binding was accepted.");
	}

	public static void PageContextsHaveUniqueIds()
	{
		using var lifetime = new CancellationTokenSource();
		var first = new PageContext(lifetime.Token);
		var second = new PageContext(CancellationToken.None);
		Program.True(first.PageId != Guid.Empty && second.PageId != Guid.Empty && first.PageId != second.PageId,
			"Page contexts must have unique nonempty identities.");
		Program.True(first.IsStandalone && second.IsStandalone, "Standalone status changed.");
		Program.True(first.Lifetime == lifetime.Token && first.Progress == null, "Existing page capabilities changed.");
	}
}
