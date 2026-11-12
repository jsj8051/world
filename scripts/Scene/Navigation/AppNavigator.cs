using System;
using World.Params.Generation;

namespace World.Scene;

internal sealed class AppNavigator : IPageNavigator
{
	readonly IPageHost _host;
	readonly PageRegistry _registry = new();
	readonly Func<GenerationConfigSession> _createSession;
	GenerationConfigSession _generationConfig;

	public AppNavigator(IPageHost host,
		Action<PageRegistry, Func<GenerationConfigSession>> registerPages,
		Func<GenerationConfigSession> createSession)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
		ArgumentNullException.ThrowIfNull(registerPages);
		_createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
		registerPages(_registry, GetGenerationConfig);
		_registry.Freeze();
	}

	GenerationConfigSession GetGenerationConfig()
		=> _generationConfig ??= _createSession()
			?? throw new InvalidOperationException("The generation session factory returned null.");

	public void Navigate<TRequest>(TRequest request) where TRequest : PageRequest
	{
		var page = _registry.Create(request);
		var operation = (request as MapBrowserRequest)?.Operation;
		if (page == null)
		{
			operation?.Fail(new LoadFailure("LoadError", "画面装配", "地图浏览场景缺失。"));
			return;
		}
		_host.MountPage(page, operation);
	}
}
