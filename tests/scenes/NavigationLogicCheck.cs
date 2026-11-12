using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using World.Data.Generation;
using World.Params.Generation;
using World.Scene;

namespace World.Tests;

internal static class NavigationLogicCheck
{
	sealed class MemoryStore : IGenerationProfileStore
	{
		public int Loads;
		public int Saves;
		public GenerationProfileCatalog Load() { Loads++; return new(); }
		public void Save(GenerationProfileCatalog catalog) { catalog.Validate(); Saves++; }
	}

	sealed class Host : IPageHost, IDisposable
	{
		readonly List<Node> _pages = new();
		public Node CurrentScreen { get; private set; }
		public int Mounts { get; private set; }
		public LoadOperation TransferredOperation { get; private set; }

		public void MountPage(Node page, LoadOperation transferredOperation = null)
		{
			_pages.Add(page);
			CurrentScreen = page;
			TransferredOperation = transferredOperation;
			Mounts++;
		}

		public void Dispose()
		{
			foreach (var page in _pages)
				if (GodotObject.IsInstanceValid(page)) page.Free();
		}
	}

	sealed class Notice : INoticeSink
	{
		public int Calls;
		public void Notify(string message, NoticeKind kind = NoticeKind.Info) { Calls++; }
	}

	static int _assertions;

	internal static void Run()
	{
		_assertions = 0;
		CheckRegistry();
		CheckNavigatorFailures();
		CheckSessionLifetime();
		CheckNullContracts();
		CheckRealFactory();
		GD.Print($"[NAV-CHECK] PASS {_assertions} assertions");
	}

	static void CheckRegistry()
	{
		var registry = new PageRegistry();
		Expect<ArgumentNullException>(() => registry.Register<MainMenuRequest>(null), "Null registration");
		Expect<ArgumentException>(() => registry.Register<PageRequest>(_ => null), "Abstract registration");
		var pages = new List<Node>();
		MainMenuRequest received = null;
		registry.Register<MainMenuRequest>(request =>
		{
			received = request;
			var page = new Node { Name = "Menu" };
			pages.Add(page);
			return page;
		});
		registry.Register<MapLibraryRequest>(_ =>
		{
			var page = new Node { Name = "Library" };
			pages.Add(page);
			return page;
		});
		registry.Register<GenerationConfigRequest>(_ => null);
		Expect<InvalidOperationException>(() => registry.Register<MainMenuRequest>(_ => null), "Duplicate registration");
		Expect<InvalidOperationException>(() => registry.Create(new MainMenuRequest()), "Creation before Freeze");
		registry.Freeze();
		registry.Freeze();
		Expect<InvalidOperationException>(() => registry.Register<WorldGenerationRequest>(_ => null), "Frozen registration");
		Expect<ArgumentNullException>(() => registry.Create<PageRequest>(null), "Null registry request");
		Expect<InvalidOperationException>(() => registry.Create(new WorldGenerationRequest(new GenerationConfig(), _ => { })),
			"Missing exact request registration");
		try
		{
			var request = new MainMenuRequest();
			PageRequest baseRequest = request;
			Node first = registry.Create<PageRequest>(baseRequest);
			Require(first.Name == "Menu" && ReferenceEquals(received, request),
				"Base-generic dispatch must use the runtime request and pass the original instance.");
			Node second = registry.Create(request);
			Require(!ReferenceEquals(first, second), "Each request must create a fresh page.");
			Require(registry.Create<PageRequest>(new MapLibraryRequest()).Name == "Library",
				"Runtime request types must select distinct factories.");
			Require(registry.Create(new GenerationConfigRequest()) == null, "A null factory result must remain null.");
		}
		finally
		{
			foreach (var page in pages) page.Free();
		}
	}

	static void CheckNavigatorFailures()
	{
		using var host = new Host();
		var failure = new FormatException("Expected factory failure");
		PageRegistry captured = null;
		var navigator = new AppNavigator(host, (registry, _) =>
		{
			captured = registry;
			registry.Register<MainMenuRequest>(_ => new Node());
			registry.Register<MapLibraryRequest>(_ => null);
			registry.Register<GenerationConfigRequest>(_ => throw failure);
		}, () => throw new InvalidOperationException("An unrelated route requested a session."));
		Expect<InvalidOperationException>(() => captured.Register<WorldGenerationRequest>(_ => null),
			"Navigator constructor must freeze registration");
		navigator.Navigate<PageRequest>(new MainMenuRequest());
		Node original = host.CurrentScreen;
		Require(original != null && host.Mounts == 1 && host.TransferredOperation == null,
			"Successful base-generic navigation must mount exactly once without an operation.");
		navigator.Navigate(new MapLibraryRequest());
		Require(ReferenceEquals(host.CurrentScreen, original) && host.Mounts == 1,
			"Null creation must preserve the mounted page.");
		var thrown = Expect<FormatException>(() => navigator.Navigate(new GenerationConfigRequest()), "Factory exception");
		Require(ReferenceEquals(thrown, failure), "Factory exception identity must be preserved.");
		Require(ReferenceEquals(host.CurrentScreen, original) && host.Mounts == 1,
			"A factory exception must not replace the mounted page.");
		Expect<InvalidOperationException>(() => navigator.Navigate(new WorldGenerationRequest(new GenerationConfig(), _ => { })),
			"Missing navigator route");
		Expect<ArgumentNullException>(() => navigator.Navigate<PageRequest>(null), "Null navigation request");
		Require(ReferenceEquals(host.CurrentScreen, original) && host.Mounts == 1,
			"Rejected requests must preserve the mounted page.");
		navigator.Navigate(new MainMenuRequest());
		Require(host.Mounts == 2 && !ReferenceEquals(host.CurrentScreen, original),
			"Repeated navigation must mount a fresh page.");
	}

	static void CheckSessionLifetime()
	{
		using var host = new Host();
		var store = new MemoryStore();
		int creations = 0;
		var visits = new List<GenerationConfigSession>();
		var navigator = new AppNavigator(host, (registry, getSession) =>
		{
			registry.Register<MainMenuRequest>(_ => new Node());
			registry.Register<GenerationConfigRequest>(_ =>
			{
				visits.Add(getSession());
				return new Node();
			});
		}, () => { creations++; return new GenerationConfigSession(store); });
		Require(creations == 0 && store.Loads == 0, "Navigator construction must not load a configuration session.");
		navigator.Navigate(new MainMenuRequest());
		Require(creations == 0 && store.Loads == 0, "Non-config navigation must not load a configuration session.");
		navigator.Navigate(new GenerationConfigRequest());
		Node firstPage = host.CurrentScreen;
		visits[0].Edit(new GenerationConfig { Seed = 9876 });
		navigator.Navigate(new MainMenuRequest());
		navigator.Navigate(new GenerationConfigRequest());
		Require(creations == 1 && store.Loads == 1 && store.Saves == 0,
			"Config revisits must reuse one in-memory session without persistence writes.");
		Require(visits.Count == 2 && ReferenceEquals(visits[0], visits[1]) && visits[1].Draft.Seed == 9876,
			"The same session and its draft must survive leaving the configuration page.");
		Require(!ReferenceEquals(firstPage, host.CurrentScreen), "Session reuse must not reuse a page instance.");

		using var nullHost = new Host();
		var nullSessionNavigator = new AppNavigator(nullHost, (registry, getSession) =>
			registry.Register<GenerationConfigRequest>(_ => { getSession(); return new Node(); }), () => null);
		Expect<InvalidOperationException>(() => nullSessionNavigator.Navigate(new GenerationConfigRequest()),
			"Null session factory result");
		Require(nullHost.Mounts == 0, "An invalid session must not mount a page.");
	}

	static void CheckNullContracts()
	{
		var config = new GenerationConfig();
		Action<Exception> onFailure = _ => { };
		Expect<ArgumentNullException>(() => new WorldGenerationRequest(null, onFailure), "Null generation config");
		Expect<ArgumentNullException>(() => new WorldGenerationRequest(config, null), "Null generation failure callback");
		Expect<ArgumentNullException>(() => new MapBrowserRequest(null), "Null browser source");
		var request = new WorldGenerationRequest(config, onFailure);
		Require(ReferenceEquals(request.Config, config) && ReferenceEquals(request.OnFailure, onFailure),
			"Generation requests must retain their supplied payload.");
		using var host = new Host();
		Action<PageRegistry, Func<GenerationConfigSession>> register = (_, _) => { };
		Func<GenerationConfigSession> create = () => new GenerationConfigSession(new MemoryStore());
		Expect<ArgumentNullException>(() => new AppNavigator(null, register, create), "Null page host");
		Expect<ArgumentNullException>(() => new AppNavigator(host, null, create), "Null registration callback");
		Expect<ArgumentNullException>(() => new AppNavigator(host, register, null), "Null session factory");
		Expect<ArgumentNullException>(() => new AppPageFactory(null), "Null notice sink");
	}

	static void CheckRealFactory()
	{
		var notice = new Notice();
		var factory = new AppPageFactory(notice);
		Expect<ArgumentNullException>(() => factory.RegisterPages(null, () => null), "Null factory registry");
		Expect<ArgumentNullException>(() => factory.RegisterPages(new PageRegistry(), null), "Null factory session getter");
		var registry = new PageRegistry();
		var session = new GenerationConfigSession(new MemoryStore());
		int sessionReads = 0;
		factory.RegisterPages(registry, () => { sessionReads++; return session; });
		Require(sessionReads == 0, "Real route registration must not eagerly request a session.");
		Expect<InvalidOperationException>(() => registry.Register<MainMenuRequest>(_ => null), "Real menu registration");
		Expect<InvalidOperationException>(() => registry.Register<MapLibraryRequest>(_ => null), "Real library registration");
		Expect<InvalidOperationException>(() => registry.Register<GenerationConfigRequest>(_ => null), "Real config registration");
		Expect<InvalidOperationException>(() => registry.Register<WorldGenerationRequest>(_ => null), "Real generation registration");
		Expect<InvalidOperationException>(() => registry.Register<MapBrowserRequest>(_ => null), "Real browser registration");
		registry.Freeze();
		Node page = null;
		try
		{
			page = registry.Create<PageRequest>(new GenerationConfigRequest());
			Require(page is MapGenerationPage && !page.IsInsideTree(), "Real config route must create the correct off-tree page.");
			// The page intentionally exposes no session getter; inspect only its injected dependency.
			var sessionField = typeof(MapGenerationPage).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
			Require(sessionField != null && ReferenceEquals(sessionField.GetValue(page), session),
				"The real config factory must inject the supplied session before mounting.");
			Require(sessionReads == 1 && notice.Calls == 0, "Real config creation must request one session without error notices.");
		}
		finally { page?.Free(); }
	}

	static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException("[NAV-CHECK] " + message);
		_assertions++;
	}

	static TException Expect<TException>(Action action, string scenario) where TException : Exception
	{
		try { action(); }
		catch (Exception error)
		{
			if (error.GetType() != typeof(TException))
				throw new InvalidOperationException($"[NAV-CHECK] {scenario}: expected {typeof(TException).Name}, got {error.GetType().Name}.", error);
			_assertions++;
			return (TException)error;
		}
		throw new InvalidOperationException($"[NAV-CHECK] {scenario}: expected {typeof(TException).Name}, but no exception was thrown.");
	}
}
