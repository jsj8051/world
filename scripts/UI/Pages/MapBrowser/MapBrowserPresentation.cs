using System;
using Godot;
using World.Application.Pages;
using World.Application.Export;
using World.Infrastructure.Export;
using World.Logic;
using World.Render;
using World.UI.Components;

namespace World.UI.Pages;

/// <summary>浏览页渲染端口的 Godot 适配器，在主线程装配展示与页面 UI。</summary>
internal sealed class MapBrowserPresentation : IMapBrowserPresentation, IDisposable
{
	readonly WorldPresentation _presentation;
	readonly CellInfoCard _cellInfoCard;
	readonly MapDock _dock;
	readonly CanvasLayer _panels;
	MapDockPresenter _dockPresenter;
	TerrainMapExport _mapExport;
	bool _active;

	internal MapBrowserPresentation(WorldPresentation presentation,
		CellInfoCard cellInfoCard, MapDock dock, CanvasLayer panels)
	{
		_presentation = presentation;
		_cellInfoCard = cellInfoCard;
		_dock = dock;
		_panels = panels;
		Stop();
	}

	public void Begin(ImportedWorldFactsSource source)
	{
		_presentation.InitializeIncremental(source, WorldGenMapModes.CreateAll(source), WorldMapLayers.CreateNatural());
		_dockPresenter?.Dispose();
		_dockPresenter = new MapDockPresenter(_dock, _presentation.Modes);
		_mapExport = new TerrainMapExport(source, _presentation.Modes.Modes, new PngMapImageWriter());
		_cellInfoCard.Bind(_presentation.Selection, source, _presentation.Modes);
	}

	public bool StepBuild(double budgetMs) => _presentation.StepBuild(budgetMs);

	internal void Show()
	{
		_active = true;
		_presentation.Visible = true;
		_panels.Visible = true;
		_presentation.SetInteractionEnabled(true);
	}

	internal bool HandleInput(InputEvent input)
	{
		if (!_active || _mapExport == null || _presentation.SwapActive || !MapExportShortcut.Matches(input)) return false;
		try { GD.Print($"[MAP] 地图图片已导出：{_mapExport.Export()}"); }
		catch (Exception error) { GD.PushError($"[MAP] 导出失败：{error.Message}"); }
		return true;
	}

	public int BuildTotal => _presentation.BuildTotal;
	public int BuildStep => _presentation.BuildStep;
	public string BuildStage => _presentation.BuildStage;
	public void Tick(double delta) => _presentation.Tick(delta);

	public void Stop()
	{
		_active = false;
		_presentation.Visible = false;
		_panels.Visible = false;
		_presentation.SetInteractionEnabled(false);
	}

	public void Dispose()
	{
		_dockPresenter?.Dispose();
		_dockPresenter = null;
		_mapExport = null;
	}
}
