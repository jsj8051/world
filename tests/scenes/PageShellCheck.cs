using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Godot;
using World.Application.Components;
using World.Application.Generation;
using World.Application.Loading;
using World.Application.Pages;
using World.Application.Storage;
using World.Data;
using World.Data.Generation;
using World.Data.Maps;
using World.Logic;
using World.Scene;
using World.UI.Pages;

namespace World.Tests;

/// <summary>
/// 组合根行为检查：动作端口 → 创建 → 挂载 的链路，以及会话复用与生成失败退回。
/// 使用模拟宿主与真实工厂（真实场景、未入树），不依赖截图或人工目视结果。
/// </summary>
internal static class PageShellCheck
{
	sealed class MemoryStore : IFileStorage<GenerationProfileCatalog>
	{
		public int Loads;
		public int Saves;
		public GenerationProfileCatalog Import(string path, IFileImportObserver observer = null, CancellationToken cancellation = default)
		{ Loads++; return new(); }
		public void Export(string path, GenerationProfileCatalog catalog) { catalog.Validate(); Saves++; }
	}

	sealed class Host : IPageHost, IDisposable
	{
		readonly List<Node> _pages = new();
		public Node CurrentScreen { get; private set; }
		public int Mounts { get; private set; }
		public LoadOperation TransferredOperation { get; private set; }

		public void Defer(Action action) => action();

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
		public string Last;
		public NoticeKind LastKind;
		public void Notify(string message, NoticeKind kind = NoticeKind.Info) { Calls++; Last = message; LastKind = kind; }
	}

	sealed class MapFiles : IGeographyMapRepository
	{
		public string Directory => "/maps";
		public string Save(GeographySnapshot snapshot)
			=> throw new NotSupportedException("Factory checks do not save maps.");
		public GeographySnapshot Load(string path, IFileImportObserver observer = null, CancellationToken cancellation = default)
			=> throw new NotSupportedException("Factory checks do not read maps.");
		public IReadOnlyList<MapPackageCandidate> List()
			=> Array.Empty<MapPackageCandidate>();
	}

	sealed class Opener : IMapOpener
	{
		public readonly List<string> Paths = new();
		public void OpenMap(string path) => Paths.Add(path);
	}

	sealed class WorldGenerator : IWorldGenerator
	{
		public readonly List<WorldSpec> Specs = new();
		public void StartGeneration(WorldSpec spec, WorldGenerationOptions options) => Specs.Add(spec);
	}

	static int _assertions;

	internal static void Run()
	{
		_assertions = 0;
		CheckMainMenuController();
		CheckShellRoutes();
		CheckSessionReuse();
		CheckGenerationFailure();
		CheckNullContracts();
		CheckFactoryInjection();
		GD.Print($"[SHELL-CHECK] PASS {_assertions} assertions");
	}

	static AppPageFactory CreateFactory(INoticeSink notice, Action quit = null)
		=> new(notice, new MapFiles(), quit ?? (() => { }));

	static AppShell CreateShell(IPageHost host, AppPageFactory factory, IMapOpener opener,
		INoticeSink notice, Func<GenerationConfigSession> session, IWorldGenerator worldGenerator = null)
		=> new(host, factory, opener, worldGenerator ?? new WorldGenerator(), notice, session);

	static void CheckMainMenuController()
	{
		var actions = new MenuActions();
		int quitCalls = 0;
		var controller = new MainMenuController(actions, () => quitCalls++);
		controller.OpenGenerationConfig();
		Require(actions.OpenedConfig == 1, "Geography action must reach the host once.");
		controller.OpenMapLibrary();
		Require(actions.OpenedLibrary == 1, "Library action must reach the host once.");
		controller.QuitApplication();
		Require(quitCalls == 1, "Quit action must invoke its host callback once.");

		Expect<ArgumentNullException>(() => new MainMenuController(null, () => { }), "Null menu actions");
		Expect<ArgumentNullException>(() => new MainMenuController(actions, null), "Null quit callback");
	}

	sealed class MenuActions : IMainMenuActions
	{
		public int OpenedConfig;
		public int OpenedLibrary;
		public void OpenGenerationConfig() => OpenedConfig++;
		public void OpenMapLibrary() => OpenedLibrary++;
	}

	sealed class ConfigActions : IGenerationConfigPageActions
	{
		public void BackToMainMenu() { }
		public void StartGeneration(GenerationConfig config) { }
	}

	static void CheckShellRoutes()
	{
		using var host = new Host();
		var notice = new Notice();
		var opener = new Opener();
		var shell = CreateShell(host, CreateFactory(notice), opener, notice,
			() => new GenerationConfigSession(new MemoryStore(), "profiles.json"));

		shell.Start();
		Require(host.CurrentScreen is MainMenuView, "Start must mount the main menu.");

		var firstMenu = host.CurrentScreen;
		((IMainMenuActions)shell).OpenGenerationConfig();
		Require(host.CurrentScreen is GenerationConfigView, "The geography action must mount the config page.");

		((IGenerationConfigPageActions)shell).BackToMainMenu();
		Require(host.CurrentScreen is MainMenuView && !ReferenceEquals(host.CurrentScreen, firstMenu),
			"Returning to the menu must mount a fresh menu page.");

		((IMainMenuActions)shell).OpenMapLibrary();
		Require(host.CurrentScreen is MapLibraryView, "The library action must mount the map library.");
		var mountedLibrary = host.Mounts;

		((IMapCatalogActions)shell).LoadMap("some/map.wmap");
		Require(opener.Paths.Count == 1 && opener.Paths[0] == "some/map.wmap",
			"Loading a map must reach the opener without changing pages.");
		Require(host.Mounts == mountedLibrary, "Loading a map must not replace the library page.");

		((IMapBrowserPageActions)shell).BackToMapLibrary();
		Require(host.CurrentScreen is MapLibraryView && host.Mounts == mountedLibrary + 1,
			"Leaving the browser must mount a fresh library page.");
	}

	static void CheckSessionReuse()
	{
		using var host = new Host();
		var notice = new Notice();
		var store = new MemoryStore();
		GenerationConfigSession session = null;
		int creations = 0;
		var shell = CreateShell(host, CreateFactory(notice), new Opener(), notice, () =>
		{
			creations++;
			return session ??= new GenerationConfigSession(store, "profiles.json");
		});

		Require(creations == 0 && store.Loads == 0, "Shell construction must not load a configuration session.");
		shell.Start();
		Require(creations == 0 && store.Loads == 0, "The menu must not load a configuration session.");

		((IMainMenuActions)shell).OpenGenerationConfig();
		Node firstPage = host.CurrentScreen;
		Require(creations == 1 && store.Loads == 1, "The first config visit must create and load one session.");
		session.Edit(new GenerationConfig { Seed = 9876 });

		((IGenerationConfigPageActions)shell).BackToMainMenu();
		((IMainMenuActions)shell).OpenGenerationConfig();
		Require(creations == 1 && store.Loads == 1 && store.Saves == 0,
			"Config revisits must reuse one in-memory session without persistence writes.");
		Require(session.Draft.Seed == 9876, "The draft must survive leaving the configuration page.");
		Require(!ReferenceEquals(firstPage, host.CurrentScreen), "Session reuse must not reuse a page instance.");

		using var nullHost = new Host();
		var nullShell = CreateShell(nullHost, CreateFactory(notice), new Opener(), notice, () => null);
		Expect<InvalidOperationException>(() => ((IMainMenuActions)nullShell).OpenGenerationConfig(),
			"Null session factory result");
		Require(nullHost.Mounts == 0, "An invalid session must not mount a page.");
	}

	static void CheckGenerationFailure()
	{
		using var host = new Host();
		var notice = new Notice();
		var shell = CreateShell(host, CreateFactory(notice), new Opener(), notice,
			() => new GenerationConfigSession(new MemoryStore(), "profiles.json"));

		shell.Start();
		shell.HandleGenerationFailure(new InvalidOperationException("late failure"));
		Require(host.CurrentScreen is MainMenuView && notice.Calls == 0,
			"A late failure must not interrupt the page the user already opened.");

		((IMainMenuActions)shell).OpenGenerationConfig();
		var mounts = host.Mounts;

		shell.HandleGenerationFailure(new InvalidOperationException("generation broke"));
		Require(host.CurrentScreen is GenerationConfigView && host.Mounts == mounts,
			"A generation failure must keep the user on the configuration page.");
		Require(notice.Calls == 1 && notice.LastKind == NoticeKind.Error
			&& notice.Last.Contains("generation broke"), "A generation failure must report exactly one error notice.");

		shell.HandleGenerationFailure(new InvalidOperationException("second failure"));
		Require(notice.Calls == 2, "Each failure on the configuration page must be reported once.");
	}

	static void CheckNullContracts()
	{
		using var host = new Host();
		var notice = new Notice();
		var factory = CreateFactory(notice);
		var opener = new Opener();
		var generator = new WorldGenerator();
		Func<GenerationConfigSession> session = () => null;
		Expect<ArgumentNullException>(() => new AppShell(null, factory, opener, generator, notice, session), "Null page host");
		Expect<ArgumentNullException>(() => new AppShell(host, null, opener, generator, notice, session), "Null page factory");
		Expect<ArgumentNullException>(() => new AppShell(host, factory, null, generator, notice, session), "Null map opener");
		Expect<ArgumentNullException>(() => new AppShell(host, factory, opener, null, notice, session), "Null world generator");
		Expect<ArgumentNullException>(() => new AppShell(host, factory, opener, generator, null, session), "Null notice sink");
		Expect<ArgumentNullException>(() => new AppShell(host, factory, opener, generator, notice, null), "Null session factory");
		Expect<ArgumentNullException>(() => new AppPageFactory(null, new MapFiles(), () => { }), "Null factory notice");
		Expect<ArgumentNullException>(() => new AppPageFactory(notice, null, () => { }), "Null factory repository");
		Expect<ArgumentNullException>(() => new AppPageFactory(notice, new MapFiles(), null), "Null factory quit");
	}

	static void CheckFactoryInjection()
	{
		var notice = new Notice();
		var session = new GenerationConfigSession(new MemoryStore(), "profiles.json");
		Node page = null;
		try
		{
			page = CreateFactory(notice).CreateGenerationConfig(new ConfigActions(), session);
			Require(page is GenerationConfigView && !page.IsInsideTree(),
				"Real config creation must return the correct off-tree page.");
			// Inspect injected dependencies without exposing them on the production view API.
			var controllerField = typeof(GenerationConfigView).GetField("_controller", BindingFlags.Instance | BindingFlags.NonPublic);
			var controller = controllerField?.GetValue(page) as GenerationConfigController;
			Require(controller != null, "The real config factory must inject a controller before mounting.");
			session.Edit(new GenerationConfig { Seed = 7654 });
			Require(controller.Draft.Seed == 7654, "The controller must use the supplied shared session.");
			Require(notice.Calls == 0, "Real config creation must not raise error notices.");
		}
		finally { page?.Free(); }
	}

	static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException("[SHELL-CHECK] " + message);
		_assertions++;
	}

	static TException Expect<TException>(Action action, string scenario) where TException : Exception
	{
		try { action(); }
		catch (Exception error)
		{
			if (error.GetType() != typeof(TException))
				throw new InvalidOperationException($"[SHELL-CHECK] {scenario}: expected {typeof(TException).Name}, got {error.GetType().Name}.", error);
			_assertions++;
			return (TException)error;
		}
		throw new InvalidOperationException($"[SHELL-CHECK] {scenario}: expected {typeof(TException).Name}, but no exception was thrown.");
	}
}
