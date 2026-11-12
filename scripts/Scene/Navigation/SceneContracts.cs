using System;
using World.Data.Maps;
using World.Logic;

namespace World.Scene.Navigation;

internal enum ScreenKind { MainMenu, Geography, MapLibrary, MapBrowser }

internal abstract class ScreenRequest
{
	public ScreenKind Kind { get; }
	private ScreenRequest(ScreenKind kind) => Kind = kind;

	internal sealed class MainMenuRequest : ScreenRequest
	{
		public MainMenuRequest() : base(ScreenKind.MainMenu) { }
	}

	internal sealed class GeographyRequest : ScreenRequest
	{
		public const string EditorParameterResource = "res://res/params/world_params.json";
		public string ParameterResourcePath { get; }

		public GeographyRequest(string parameterResourcePath) : base(ScreenKind.Geography)
		{
			MapStructure.Require(!string.IsNullOrWhiteSpace(parameterResourcePath)
				&& parameterResourcePath.StartsWith("res://", StringComparison.Ordinal)
				&& !parameterResourcePath.Contains('\\') && !parameterResourcePath.Contains("..", StringComparison.Ordinal),
				"scene.geography.parameters", "An explicit project resource path is required.");
			ParameterResourcePath = parameterResourcePath;
		}
	}

	internal sealed class MapLibraryRequest : ScreenRequest
	{
		public MapLibraryRequest() : base(ScreenKind.MapLibrary) { }
	}

	internal sealed class MapBrowseRequest : ScreenRequest
	{
		public ImportedWorldFactsSource Source { get; }
		public GeographyManifest Summary => Source.Snapshot.Manifest;

		public MapBrowseRequest(ImportedWorldFactsSource source) : base(ScreenKind.MapBrowser)
			=> Source = MapStructure.Present(source, "scene.mapBrowser.source");
	}
}

internal static class SceneContractRules
{
	public static bool CanNavigate(ScreenKind from, ScreenKind to) => (from, to) switch
	{
		(ScreenKind.MainMenu, ScreenKind.Geography or ScreenKind.MapLibrary) => true,
		(ScreenKind.Geography or ScreenKind.MapLibrary, ScreenKind.MainMenu) => true,
		(ScreenKind.MapLibrary, ScreenKind.MapBrowser) => true,
		(ScreenKind.MapBrowser, ScreenKind.MapLibrary) => true,
		_ => false,
	};
}