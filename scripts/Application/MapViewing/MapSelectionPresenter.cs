using System;
using World.Data;
using World.Render;

namespace World.Application.MapViewing;

/// <summary>Tracks selection and mode changes without owning controls or world state.</summary>
internal sealed class MapSelectionPresenter : IDisposable
{
	readonly ICellSelectionSource _selection;
	readonly MapModeController _modes;
	readonly Func<PickedCell?, MapMode, MapSelectionContent> _describe;
	PickedCell? _picked;
	bool _disposed;

	internal MapSelectionContent Content { get; private set; }
	internal event Action Changed;

	internal MapSelectionPresenter(ICellSelectionSource selection, MapModeController modes,
		Func<PickedCell?, MapMode, MapSelectionContent> describe)
	{
		_selection = selection ?? throw new ArgumentNullException(nameof(selection));
		_modes = modes ?? throw new ArgumentNullException(nameof(modes));
		_describe = describe ?? throw new ArgumentNullException(nameof(describe));
		_selection.CellPicked += OnCellPicked;
		_modes.Changed += Refresh;
	}

	void OnCellPicked(PickedCell? picked)
	{
		_picked = picked;
		Refresh();
	}

	void Refresh()
	{
		Content = _describe(_picked, _modes.Current);
		Changed?.Invoke();
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_selection.CellPicked -= OnCellPicked;
		_modes.Changed -= Refresh;
		_picked = null;
		Content = null;
		Changed = null;
	}
}
