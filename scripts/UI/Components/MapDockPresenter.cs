using System;
using System.Linq;
using World.Render;

namespace World.UI.Components;

/// <summary>将地图模式状态绑定到地图坞；页面退出时释放绑定。</summary>
public sealed class MapDockPresenter : IDisposable
{
	readonly MapDock _dock;
	readonly MapModeController _modes;
	bool _disposed;
	string[] _parameterOptions;
	string[] _variantOptions;
	int _modeIndex = -1;

	public MapDockPresenter(MapDock dock, MapModeController modes)
	{
		_dock = dock ?? throw new ArgumentNullException(nameof(dock));
		_modes = modes ?? throw new ArgumentNullException(nameof(modes));
		_dock.BindModes(_modes.Modes.Select(mode => mode.Name).ToArray());
		_dock.ModeSelected += SelectMode;
		_dock.ParameterSelected += SelectParameter;
		_dock.VariantSelected += SelectVariant;
		_modes.Changed += Refresh;
		Refresh();
	}

	void SelectMode(int index) => _modes.SelectMode(index);
	void SelectParameter(int index) => _modes.SelectParameter(index);
	void SelectVariant(int index) => _modes.SelectVariant(index);

	void Refresh()
	{
		var mode = _modes.Current;
		var parameters = mode.ParameterOptions?.ToArray();
		var variants = mode.VariantOptions?.ToArray();
		bool modeChanged = _modeIndex != _modes.CurrentIndex;
		_dock.SetMode(_modes.CurrentIndex);
		if (modeChanged || !SameOptions(_variantOptions, variants))
			_dock.SetVariantOptions(variants, mode.VariantIndex);
		else _dock.SetVariant(mode.VariantIndex);
		if (modeChanged || !SameOptions(_parameterOptions, parameters))
			_dock.SetParameterOptions(parameters, mode.ParameterIndex);
		else _dock.SetParameter(mode.ParameterIndex);
		_parameterOptions = parameters;
		_variantOptions = variants;
		_modeIndex = _modes.CurrentIndex;
		_dock.SetLegend(mode.ScaleCaption);
	}

	static bool SameOptions(string[] left, string[] right)
		=> ReferenceEquals(left, right) || (left != null && right != null && left.SequenceEqual(right));

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_modes.Changed -= Refresh;
		_dock.ModeSelected -= SelectMode;
		_dock.ParameterSelected -= SelectParameter;
		_dock.VariantSelected -= SelectVariant;
	}
}
