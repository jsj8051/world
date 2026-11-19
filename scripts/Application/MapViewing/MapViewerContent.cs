using System;
using System.Collections.Generic;
using World.Data;
using World.Logic;
using World.Render;

namespace World.Application.MapViewing;

internal sealed record MapSelectionContent(
	ulong CellId, string Summary, string Details, string ExpansionCaption, string ExpandedDetails);

/// <summary>One viewer's read-only world input and display composition; layer instances belong to that viewer.</summary>
internal sealed class MapViewerContent
{
	internal IWorldFactsSource Source { get; }
	internal IReadOnlyList<MapMode> Modes { get; }
	internal IReadOnlyList<IMapLayer> Layers { get; }
	internal Func<PickedCell?, MapMode, MapSelectionContent> DescribeSelection { get; }

	internal MapViewerContent(IWorldFactsSource source, IReadOnlyList<MapMode> modes,
		IReadOnlyList<IMapLayer> layers, Func<PickedCell?, MapMode, MapSelectionContent> describeSelection)
	{
		Source = source ?? throw new ArgumentNullException(nameof(source));
		Modes = new MapModeController(modes).Modes;
		ArgumentNullException.ThrowIfNull(layers);
		var registered = new IMapLayer[layers.Count];
		for (int i = 0; i < layers.Count; i++)
			registered[i] = layers[i] ?? throw new ArgumentException("Layers cannot contain null.", nameof(layers));
		Layers = Array.AsReadOnly(registered);
		DescribeSelection = describeSelection ?? throw new ArgumentNullException(nameof(describeSelection));
	}
}
