using System;
using Godot;
using World.Data;
using World.Utils;

namespace World.Render;

/// <summary>Handles pointer selection and highlight, publishing snapshots independently of UI.</summary>
public partial class CellSelectionController : Node, ICellSelectionSource
{
	WorldView _view;
	WorldPicker _picker;
	Camera3D _camera;
	PointerGesture _gesture = new();
	ulong? _selectedCellId;
	bool _inputEnabled = true;

	public event Action<PickedCell?> CellPicked;
	public bool InputEnabled
	{
		get => _inputEnabled;
		set
		{
			_inputEnabled = value;
			_gesture = new PointerGesture();
		}
	}

	/// <summary>Binds a built or building view and resets the previous selection.</summary>
	public void Bind(WorldView view)
	{
		if (view == null) throw new ArgumentNullException(nameof(view));
		var picker = view.CreatePicker(_camera);
		_view?.SetHighlight(null);
		_view = view;
		_picker = picker;
		_selectedCellId = null;
		_gesture = new PointerGesture();
	}

	public void UpdateCamera(Camera3D camera)
	{
		_camera = camera;
		_picker?.SetCamera(camera);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!InputEnabled || _picker == null) return;
		if (_gesture.Feed(@event, out var at) != PointerIntent.Click) return;
		if (_picker.TryPick(at, out var cell))
		{
			_selectedCellId = cell.CellId;
			_view.SetHighlight(cell.CellId);
			CellPicked?.Invoke(cell);
		}
		else ClearSelection();
	}

	public void RefreshSelection()
	{
		if (_selectedCellId is not ulong id) return;
		if (_picker != null && _picker.TryReadCell(id, out var cell))
		{
			_view.SetHighlight(id);
			CellPicked?.Invoke(cell);
		}
		else ClearSelection();
	}

	void ClearSelection()
	{
		_selectedCellId = null;
		_view?.SetHighlight(null);
		CellPicked?.Invoke(null);
	}
}
