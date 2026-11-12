using System;
using World.Data.Generation;
using World.Logic;

namespace World.Scene;

internal abstract class PageRequest
{
	private protected PageRequest() { }
}

internal sealed class MainMenuRequest : PageRequest { }

internal sealed class GenerationConfigRequest : PageRequest { }

internal sealed class MapLibraryRequest : PageRequest { }

internal sealed class WorldGenerationRequest : PageRequest
{
	public WorldGenerationRequest(GenerationConfig config, Action<Exception> onFailure)
	{
		Config = config ?? throw new ArgumentNullException(nameof(config));
		OnFailure = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
	}

	public GenerationConfig Config { get; }
	public Action<Exception> OnFailure { get; }
}

internal sealed class MapBrowserRequest : PageRequest
{
	public MapBrowserRequest(ImportedWorldFactsSource source, LoadOperation operation = null)
	{
		Source = source ?? throw new ArgumentNullException(nameof(source));
		Operation = operation;
	}

	public ImportedWorldFactsSource Source { get; }
	public LoadOperation Operation { get; }
}
