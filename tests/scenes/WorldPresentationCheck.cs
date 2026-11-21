using System;
using Godot;
using World.Application.Geography;
using World.Application.MapViewing;
using World.UI.Components;
using World.Data;
using World.Logic;
using World.Render;

namespace World.Tests;

/// <summary>无界面验证展示装配、显式图层和交互门禁。</summary>
public partial class WorldPresentationCheck : Node
{
	public override void _Ready()
	{
		try
		{
			var source = new WorldGenSimulation(1, PlanetGeometry.ProductionRadius);
			var result = new World.Application.Loading.WorldGenerationJob().Run(source, new WorldSpec(), 1,
				System.Threading.CancellationToken.None);
			source.Publish(result.State);
			var presentation = GD.Load<PackedScene>("res://scenes/render/WorldPresentation.tscn").Instantiate<WorldPresentation>();
			AddChild(presentation);
			presentation.Initialize(source, WorldGenMapModes.CreateAll(source), null);
			Require(presentation.BuildProgress == 1f, "A presentation without UI or layers must finish building.");
			Require(presentation.Selection.InputEnabled, "Picking must be enabled after building.");
			Require(presentation.GetNode<WorldView>("WorldView").GetChildCount() == 1,
				"A bare world view must not create domain overlays.");
			presentation.BeginSwap();
			Require(!presentation.Selection.InputEnabled, "Swapping must disable picking.");
			presentation.EndSwap();
			Require(presentation.Selection.InputEnabled, "Ending a swap must restore picking.");
			presentation.SetInteractionEnabled(false);
			presentation.BeginSwap();
			presentation.EndSwap();
			Require(!presentation.Selection.InputEnabled, "Ending a swap must not enable a hidden page.");
			presentation.Modes.SelectMode(1);
			presentation.RebakeView();
			presentation.Tick(0);
			foreach (var layer in WorldMapLayers.CreateNatural()) presentation.AddLayer(layer);
			Require(presentation.GetNode<WorldView>("WorldView").GetChildCount() == 5,
				"Only explicitly injected layers should be attached to the view.");
			presentation.Free();
			CheckSharedViewer(source);
			GD.Print("[PRESENTATION-CHECK] passed: no dock, explicit overlays, mode change, rebake, input gates, disposal");
			GetTree().Quit(0);
		}
		catch (Exception error)
		{
			GD.PushError($"[PRESENTATION-CHECK] {error}");
			GetTree().Quit(1);
		}
	}

	sealed class UnreadySource : IWorldFactsSource
	{
		public WorldFacts Facts => null;
	}

	sealed class DisposableLayer : IMapLayer, IDisposable
	{
		internal int Disposals;
		public void SetContext(WorldFacts facts, MapMode mode) { }
		public void UpdateCamera(Camera3D camera) { }
		public void Dispose() => Disposals++;
	}

	void CheckSharedViewer(IWorldFactsSource source)
	{
		var viewerScene = GD.Load<PackedScene>("res://scenes/components/MapViewer.tscn");
		var viewer = viewerScene.Instantiate<MapViewer>();
		AddChild(viewer);
		var content = GeographyMapViewerContent.Create(source);
		viewer.Configure(content);
		viewer.SetActive(true);
		while (!viewer.Presentation.StepBuild(1000)) { }
		Require(viewer.Presentation.Selection.InputEnabled, "A shared viewer must enable picking after building.");
		Require(viewer.GetNode("Presentation/WorldView").GetChildCount() == 5, "Geography must explicitly supply natural layers.");
		bool rejected = false;
		try { viewer.Configure(content); }
		catch (InvalidOperationException) { rejected = true; }
		Require(rejected, "A viewer must reject repeated configuration instead of mixing sources.");
		viewer.SetActive(false);
		Require(!viewer.Presentation.Selection.InputEnabled && !viewer.GetNode<CanvasLayer>("Panels").Visible,
			"Hiding a viewer must hide panels and disable input together.");
		viewer.ReleaseBindings();
		viewer.ReleaseBindings();
		rejected = false;
		try { viewer.SetActive(true); }
		catch (InvalidOperationException) { rejected = true; }
		Require(rejected, "A released viewer must not reactivate stale bindings.");
		viewer.Presentation.Modes.SelectMode(1);
		viewer.Free();

		var custom = viewerScene.Instantiate<MapViewer>();
		AddChild(custom);
		var ownedLayer = new DisposableLayer();
		custom.Configure(new MapViewerContent(source, new MapMode[] { new TerrainMode(source) },
			new IMapLayer[] { ownedLayer }, (_, _) => null));
		while (!custom.Presentation.StepBuild(1000)) { }
		Require(custom.GetNode("Presentation/WorldView").GetChildCount() == 1,
			"The shared viewer must not create geography layers when another composition omits them.");
		custom.Free();
		Require(ownedLayer.Disposals == 1, "The view must release owned non-node layers exactly once.");

		var failed = viewerScene.Instantiate<MapViewer>();
		AddChild(failed);
		var unattached = new DisposableLayer();
		rejected = false;
		try
		{
			failed.Configure(new MapViewerContent(new UnreadySource(), new MapMode[] { new TerrainMode(source) },
				new IMapLayer[] { unattached }, (_, _) => null));
		}
		catch (InvalidOperationException) { rejected = true; }
		Require(rejected && unattached.Disposals == 1, "Failed assembly must release unclaimed non-node layers.");
		failed.Free();
		Require(unattached.Disposals == 1, "Failed assembly must not release a layer twice.");
	}

	static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
