using System;
using System.Threading;
using World.Application.Pages;

namespace World.Tests;

internal static class LoadStructureModelTests
{
	public static void PageContextsHaveUniqueIds()
	{
		using var lifetime = new CancellationTokenSource();
		var first = new PageContext(lifetime.Token);
		var second = new PageContext(CancellationToken.None);
		Program.True(first.PageId != Guid.Empty && second.PageId != Guid.Empty && first.PageId != second.PageId,
			"Page contexts must have unique nonempty identities.");
		Program.True(first.Lifetime == lifetime.Token, "Page context must keep the host lifetime.");
	}
}
