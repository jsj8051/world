using System;
using World.Application.Components;
using World.Application.MapViewing;
using World.Logic;
using World.Render;

namespace World.Application.Geography;

internal static class GeographyMapViewerContent
{
	internal static MapViewerContent Create(IWorldFactsSource source)
	{
		ArgumentNullException.ThrowIfNull(source);
		return new MapViewerContent(source, WorldGenMapModes.CreateAll(source), WorldMapLayers.CreateNatural(),
			(picked, mode) => CellInfoCardController.DescribeSelection(picked, mode is CrustMode, source));
	}
}
