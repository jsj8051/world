using System;
using System.Collections.Generic;

namespace World.Render;

/// <summary>Owns the registered map modes and publishes changes without knowing any view or UI.</summary>
public sealed class MapModeController
{
	public IReadOnlyList<MapMode> Modes { get; }
	public int CurrentIndex { get; private set; }
	public MapMode Current => Modes[CurrentIndex];
	public event Action Changed;

	public MapModeController(IReadOnlyList<MapMode> modes)
	{
		if (modes == null) throw new ArgumentNullException(nameof(modes));
		if (modes.Count == 0) throw new ArgumentException("At least one map mode is required.", nameof(modes));
		var registered = new MapMode[modes.Count];
		for (int i = 0; i < modes.Count; i++)
			registered[i] = modes[i] ?? throw new ArgumentException("Map modes cannot contain null.", nameof(modes));
		Modes = Array.AsReadOnly(registered);
	}

	public void SelectMode(int index)
	{
		if (index < 0 || index >= Modes.Count || index == CurrentIndex) return;
		CurrentIndex = index;
		Changed?.Invoke();
	}

	public void SelectParameter(int index)
	{
		var options = Current.ParameterOptions;
		if (options == null || index < 0 || index >= options.Count || index == Current.ParameterIndex) return;
		Current.SetParameter(index);
		Changed?.Invoke();
	}

	public void SelectVariant(int index)
	{
		var options = Current.VariantOptions;
		if (options == null || index < 0 || index >= options.Count || index == Current.VariantIndex) return;
		Current.SetVariant(index);
		Changed?.Invoke();
	}
}
