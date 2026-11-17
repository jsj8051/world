using Godot;
using World.Application.Pages;
using World.Logic;
using World.Render;
using World.UI.Components;

namespace World.UI.Pages;

/// <summary>浏览页渲染端口的 Godot 适配器，仅在主线程装配和推进画面。</summary>
internal sealed class MapBrowserPresentation : IMapBrowserPresentation
{
	readonly RenderManager _render;
	readonly OrbitalCamera _camera;
	readonly CellInfoCard _cellInfoCard;
	readonly PanelContainer _dock;
	readonly CanvasLayer _panels;

	internal MapBrowserPresentation(RenderManager render, OrbitalCamera camera,
		CellInfoCard cellInfoCard, PanelContainer dock, CanvasLayer panels)
	{
		_render = render;
		_camera = camera;
		_cellInfoCard = cellInfoCard;
		_dock = dock;
		_panels = panels;
		Stop();
	}

	public void Begin(ImportedWorldFactsSource source)
	{
		_render.SetDock(_dock);
		_render.InitializeIncremental(source, WorldGenMapModes.CreateAll(source));
		_camera.SetOrbitRadius(source.Facts.Ball.Radius);
		if (_render.GetLandFocusDirection() is Vector3 direction) _camera.LookAtPoint(direction);
		_cellInfoCard.Bind(_render, source);
	}

	public bool StepBuild(double budgetMs) => _render.StepBuild(budgetMs);

	internal void Show()
	{
		_render.Visible = true;
		_panels.Visible = true;
		_render.SetProcessUnhandledInput(true);
		_camera.SetProcessUnhandledInput(true);
		_camera.SetProcess(true);
	}

	public int BuildTotal => _render.BuildTotal;
	public int BuildStep => _render.BuildStep;
	public string BuildStage => _render.BuildStage;
	public void Tick(double delta) => _render.Tick(delta);

	public void Stop()
	{
		_render.Visible = false;
		_panels.Visible = false;
		_render.SetProcessUnhandledInput(false);
		_camera.SetProcessUnhandledInput(false);
		_camera.SetProcess(false);
	}
}
