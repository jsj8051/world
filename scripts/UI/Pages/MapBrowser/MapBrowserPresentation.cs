using System;
using Godot;
using World.Application.Pages;
using World.Application.Export;
using World.Application.Geography;
using World.Infrastructure.Export;
using World.Logic;
using World.UI.Components;

namespace World.UI.Pages;

/// <summary>Adapts browser loading to the shared viewer; export remains a page command.</summary>
internal sealed class MapBrowserPresentation : IMapBrowserPresentation, IDisposable
{
	readonly MapViewer _viewer;
	TerrainMapExport _mapExport;
	bool _active;

	internal MapBrowserPresentation(MapViewer viewer)
	{
		_viewer = viewer;
		Stop();
	}

	public void Begin(ImportedWorldFactsSource source)
	{
		_viewer.Configure(GeographyMapViewerContent.Create(source));
		_mapExport = new TerrainMapExport(source, _viewer.Presentation.Modes.Modes, new PngMapImageWriter());
	}

	public bool StepBuild(double budgetMs) => _viewer.Presentation.StepBuild(budgetMs);

	internal void Show()
	{
		_active = true;
		_viewer.SetActive(true);
	}

	internal bool HandleInput(InputEvent input)
	{
		if (!_active || _mapExport == null || _viewer.Presentation.SwapActive || !MapExportShortcut.Matches(input)) return false;
		try { GD.Print($"[MAP] 地图图片已导出：{_mapExport.Export()}"); }
		catch (Exception error) { GD.PushError($"[MAP] 导出失败：{error.Message}"); }
		return true;
	}

	public int BuildTotal => _viewer.Presentation.BuildTotal;
	public int BuildStep => _viewer.Presentation.BuildStep;
	public string BuildStage => _viewer.Presentation.BuildStage;
	public void Tick(double delta) => _viewer.Presentation.Tick(delta);

	public void Stop()
	{
		_active = false;
		_viewer.SetActive(false);
	}

	public void Dispose()
	{
		_active = false;
		_viewer.ReleaseBindings();
		_mapExport = null;
	}
}
