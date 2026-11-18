using System;
using Godot;
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
			source.Run(new WorldSpec());
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
			GD.Print("[PRESENTATION-CHECK] passed: no dock, explicit overlays, mode change, rebake, input gates, disposal");
			GetTree().Quit(0);
		}
		catch (Exception error)
		{
			GD.PushError($"[PRESENTATION-CHECK] {error}");
			GetTree().Quit(1);
		}
	}

	static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}
}
