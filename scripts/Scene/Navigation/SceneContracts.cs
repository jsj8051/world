using System;
using System.Threading;
using World.Data.Maps;
using World.Logic;

namespace World.Scene.Navigation;

internal enum ScreenKind { MainMenu, Geography, MapLibrary, MapBrowser }
internal enum SceneInitializationState { Created, NodesReady, Initializing, Active, Failed, Cancelled, Exiting, Disposed }
internal enum MapOperationState { Idle, Running, Cancelling, Succeeded, Failed, Cancelled }

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

internal sealed class OperationContext
{
	public Guid OperationId { get; }
	public Guid OwnerScreenId { get; }
	public MapOperationState State { get; }
	public float? Progress { get; }
	public string StageText { get; }
	public CancellationToken CancellationToken { get; }

	public OperationContext(Guid operationId, Guid ownerScreenId, MapOperationState state,
		float? progress, string stageText, CancellationToken cancellationToken)
	{
		MapStructure.Require(operationId != Guid.Empty && ownerScreenId != Guid.Empty,
			"operation.identity", "Operation and owning screen identities are required.");
		MapStructure.Require(Enum.IsDefined(state), "operation.state", "The operation state is unknown.");
		if (progress.HasValue) MapStructure.Range(progress.Value, 0, 1, "operation.progress");
		MapStructure.Present(stageText, "operation.stageText");
		(OperationId, OwnerScreenId, State, Progress, StageText, CancellationToken) =
			(operationId, ownerScreenId, state, progress, stageText, cancellationToken);
	}

	public bool CanCommit(Guid currentScreenId, Guid currentOperationId, SceneInitializationState ownerState)
		=> OwnerScreenId == currentScreenId && OperationId == currentOperationId
			&& State == MapOperationState.Running && !CancellationToken.IsCancellationRequested
			&& ownerState is SceneInitializationState.Initializing or SceneInitializationState.Active;
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

	public static bool CanInitializeTransition(SceneInitializationState from, SceneInitializationState to)
	{
		if (!Enum.IsDefined(from) || !Enum.IsDefined(to)) return false;
		if (to == SceneInitializationState.Exiting)
			return from is not (SceneInitializationState.Exiting or SceneInitializationState.Disposed);
		return (from, to) switch
		{
			(SceneInitializationState.Created, SceneInitializationState.NodesReady) => true,
			(SceneInitializationState.NodesReady, SceneInitializationState.Initializing) => true,
			(SceneInitializationState.Initializing, SceneInitializationState.Active or SceneInitializationState.Failed or SceneInitializationState.Cancelled) => true,
			(SceneInitializationState.Exiting, SceneInitializationState.Disposed) => true,
			_ => false,
		};
	}

	public static bool CanOperationTransition(MapOperationState from, MapOperationState to) => (from, to) switch
	{
		(MapOperationState.Idle, MapOperationState.Running) => true,
		(MapOperationState.Running, MapOperationState.Cancelling or MapOperationState.Succeeded or MapOperationState.Failed or MapOperationState.Cancelled) => true,
		(MapOperationState.Cancelling, MapOperationState.Cancelled or MapOperationState.Failed) => true,
		_ => false,
	};
}