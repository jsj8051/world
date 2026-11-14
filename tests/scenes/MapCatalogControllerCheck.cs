using System;
using System.Collections.Generic;
using World.Application.Components;
using World.Application.Storage;
using World.Data.Maps;

namespace World.Tests;

/// <summary>地图目录控制器的纯逻辑检查：存档列表、选择、加载与宿主动作。</summary>
internal static class MapCatalogControllerCheck
{
	sealed class Repository : IGeographyMapRepository
	{
		public readonly List<MapPackageCandidate> Candidates = new();
		public bool ThrowOnList;

		public string Directory => "/maps";

		public string Save(GeographySnapshot snapshot) => throw new NotSupportedException("Not used.");

		public GeographySnapshot Load(string path, IFileImportObserver observer = null,
			System.Threading.CancellationToken cancellation = default)
			=> throw new NotSupportedException("Not used.");

		public IReadOnlyList<MapPackageCandidate> List()
		{
			if (ThrowOnList) throw new InvalidOperationException("list failed");
			return Candidates;
		}
	}

	sealed class Actions : IMapCatalogActions
	{
		public int Backs;
		public string Loaded;

		public void BackToMainMenu() => Backs++;
		public void LoadMap(string path) => Loaded = path;
	}

	static MapProbeResult Readable(string title) => new(MapProbeStatus.HeaderReadable, Guid.NewGuid(),
		title, GeographyManifest.CurrentFormat, GeographyManifest.CurrentSchemaVersion, null);

	static MapProbeResult Invalid() => new(MapProbeStatus.Invalid, null, null, null, null,
		new MapIssue(MapIssueCode.IoFailure, "package", null, "file is broken"));

	internal static int Run()
	{
		int assertions = 0;
		void Require(bool condition, string message)
		{
			if (!condition) throw new InvalidOperationException("[MAP-CATALOG-CONTROLLER] " + message);
			assertions++;
		}

		var repository = new Repository();
		repository.Candidates.Add(new MapPackageCandidate("/maps/alpha.wmap", Readable("Alpha")));
		repository.Candidates.Add(new MapPackageCandidate("/maps/beta.wmap", Invalid()));
		repository.Candidates.Add(new MapPackageCandidate("/maps/gamma.wmap",
			new MapProbeResult(MapProbeStatus.Unsupported, null, null, null, null,
				new MapIssue(MapIssueCode.UnsupportedVersion, "manifest.schemaVersion", null, "version 2"))));
		var actions = new Actions();
		var controller = new MapCatalogController(repository, actions);
		int catalogChanges = 0, stateChanges = 0;
		controller.CatalogChanged += () => catalogChanges++;
		controller.StateChanged += () => stateChanges++;

		controller.Refresh();
		Require(controller.Entries.Count == 3 && catalogChanges == 1 && stateChanges == 1,
			"Refresh must load every saved map and notify the view.");
		Require(controller.SelectedIndex == -1 && !controller.CanLoad && !controller.StatusVisible,
			"A non-empty library must start without a selection or status message.");
		Require(controller.DetailText.Contains("从左侧选择"), "No selection must show the empty detail prompt.");
		Require(controller.Entries[0].DisplayName.Contains("Alpha") && controller.Entries[0].Title == "Alpha",
			"The readable entry must expose its probe title and a display line.");
		Require(controller.Entries[1].Status == MapProbeStatus.Invalid
			&& controller.Entries[1].Detail.Contains("损坏"), "Invalid packages must be marked in the detail.");

		controller.Select(0);
		Require(controller.SelectedIndex == 0 && controller.CanLoad
			&& controller.DetailText.Contains("/maps/alpha.wmap") && controller.DetailText.Contains("可识别"),
			"Selecting a readable package must enable loading and show its path.");
		controller.LoadSelected();
		Require(actions.Loaded == "/maps/alpha.wmap", "Loading the selected package must reach the host.");
		Require(controller.StatusText.Length > 0 && !controller.StatusIsError,
			"Loading must show an in-progress status.");

		actions.Loaded = null;
		controller.Select(1);
		Require(!controller.CanLoad, "Invalid packages must not be loadable.");
		controller.LoadSelected();
		Require(actions.Loaded == null, "Invalid selection must not reach the host.");
		controller.Select(99);
		Require(controller.SelectedIndex == -1 && !controller.CanLoad,
			"An out-of-range selection must clear the selected row.");

		controller.BackToMainMenu();
		Require(actions.Backs == 1, "Back must reach the host.");

		var empty = new Repository();
		var emptyController = new MapCatalogController(empty, actions);
		emptyController.Refresh();
		Require(emptyController.Entries.Count == 0 && emptyController.StatusVisible
			&& emptyController.StatusText.Contains("地图库为空"), "An empty library must explain how to save a map.");

		empty.ThrowOnList = true;
		emptyController.Refresh();
		Require(emptyController.Entries.Count == 0 && emptyController.StatusIsError
			&& emptyController.StatusText.Contains("读取地图库失败"), "Repository failures must become error status.");
		empty.ThrowOnList = false;

		var noHost = new MapCatalogController(repository);
		noHost.Refresh();
		noHost.Select(0);
		noHost.LoadSelected();
		Require(noHost.StatusIsError && noHost.StatusText.Contains("独立运行"),
			"Missing host capability must be reported instead of throwing.");
		noHost.BackToMainMenu();
		Require(noHost.StatusIsError, "Back without the navigation host must be reported.");

		try { _ = new MapCatalogController(null); throw new InvalidOperationException("Expected null repository rejection."); }
		catch (ArgumentNullException) { assertions++; }

		return assertions;
	}
}
