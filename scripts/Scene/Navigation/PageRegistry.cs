using System;
using System.Collections.Generic;
using Godot;

namespace World.Scene;

internal sealed class PageRegistry
{
	readonly Dictionary<Type, Func<PageRequest, Node>> _factories = new();
	bool _frozen;

	public void Register<TRequest>(Func<TRequest, Node> factory) where TRequest : PageRequest
	{
		ArgumentNullException.ThrowIfNull(factory);
		if (_frozen) throw new InvalidOperationException("Page registration is already frozen.");
		if (typeof(TRequest).IsAbstract)
			throw new ArgumentException("Only concrete page requests can be registered.", nameof(factory));
		if (!_factories.TryAdd(typeof(TRequest), request => factory((TRequest)request)))
			throw new InvalidOperationException($"Page request is already registered: {typeof(TRequest).Name}.");
	}

	public void Freeze() => _frozen = true;

	public Node Create<TRequest>(TRequest request) where TRequest : PageRequest
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!_frozen) throw new InvalidOperationException("Page registration must be frozen before navigation.");
		if (!_factories.TryGetValue(request.GetType(), out var factory))
			throw new InvalidOperationException($"Page request is not registered: {request.GetType().Name}.");
		return factory(request);
	}
}
