using System;
using System.Collections.Generic;
using Godot;
using World.Application.MapViewing;
using World.Data;
using World.Logic;
using World.Render;

namespace World.Tests;

internal static class MapViewerTests
{
	sealed class SelectionSource : ICellSelectionSource
	{
		Action<PickedCell?> _picked;
		internal int Subscribers { get; private set; }
		public event Action<PickedCell?> CellPicked
		{
			add { _picked += value; Subscribers++; }
			remove { _picked -= value; Subscribers--; }
		}
		internal void Select(PickedCell? cell) => _picked?.Invoke(cell);
	}

	sealed class Mode : MapMode
	{
		public override string Name => "Custom";
		public override Color CellColorAt(int i) => default;
	}

	sealed class FactsSource : IWorldFactsSource
	{
		public WorldFacts Facts => null;
	}

	internal static void SelectionUsesInjectedContentAndReleasesSubscriptions()
	{
		var selection = new SelectionSource();
		var first = new Mode();
		var second = new Mode();
		var modes = new MapModeController(new MapMode[] { first, second });
		int descriptions = 0;
		MapMode describedMode = null;
		var presenter = new MapSelectionPresenter(selection, modes, (picked, mode) =>
		{
			descriptions++;
			describedMode = mode;
			return picked is PickedCell cell ? new MapSelectionContent(cell.CellId, "Custom", "", "", "") : null;
		});
		Program.True(selection.Subscribers == 1 && presenter.Content == null, "Selection binding must start empty.");
		selection.Select(new PickedCell { CellId = 42 });
		Program.True(presenter.Content?.Summary == "Custom" && ReferenceEquals(describedMode, first),
			"The viewer must use injected content, not geography formatting.");
		modes.SelectMode(1);
		Program.True(ReferenceEquals(describedMode, second), "Mode changes must refresh the current selection.");
		selection.Select(null);
		Program.True(presenter.Content == null, "Clearing selection must clear content.");
		presenter.Dispose();
		presenter.Dispose();
		int before = descriptions;
		selection.Select(new PickedCell { CellId = 99 });
		modes.SelectMode(0);
		Program.True(selection.Subscribers == 0 && descriptions == before && presenter.Content == null,
			"Disposal must detach both subscriptions and remain idempotent.");
	}

	internal static void CompositionSnapshotsRegistrationsAndRejectsInvalidInputs()
	{
		var modes = new List<MapMode> { new Mode() };
		var layers = new List<IMapLayer>();
		var source = new FactsSource();
		var content = new MapViewerContent(source, modes, layers, (_, _) => null);
		modes.Clear();
		layers.Add(null);
		Program.True(content.Modes.Count == 1 && content.Layers.Count == 0 && ReferenceEquals(content.Source, source),
			"Composition must snapshot registrations but retain the source identity.");
		Program.Throws(() => new MapViewerContent(source, Array.Empty<MapMode>(), Array.Empty<IMapLayer>(), (_, _) => null),
			"Empty modes must be rejected.");
		Program.Throws(() => new MapViewerContent(source, content.Modes, layers, (_, _) => null),
			"Null layer registrations must be rejected.");
		Program.Throws(() => new MapViewerContent(source, content.Modes, Array.Empty<IMapLayer>(), null),
			"Missing detail providers must be rejected.");
	}
}
