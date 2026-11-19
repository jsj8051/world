using System;
using System.Linq;
using Godot;
using World.Application.MapViewing;
using World.Render;
using World.Utils;

namespace World.UI.Components;

/// <summary>
/// Owns one map presentation and its mode/selection bindings. Configure once after entering the tree;
/// the parent page owns simulation, commands and export. Example: viewer.Configure(content).
/// </summary>
public partial class MapViewer : Node3D
{
	WorldPresentation _presentation;
	MapDockPresenter _dockPresenter;
	MapSelectionPresenter _selectionPresenter;
	CellInfoCard _card;
	bool _configured;
	bool _released;

	public WorldPresentation Presentation => _presentation ??= this.Require<WorldPresentation>("Presentation", "Presentation");

	internal void Configure(MapViewerContent content)
	{
		ArgumentNullException.ThrowIfNull(content);
		if (_configured || _released) throw new InvalidOperationException("A map viewer can only be configured once.");
		if (!IsInsideTree()) throw new InvalidOperationException("The map viewer must be in the scene tree.");
		_configured = true;
		Presentation.SetInteractionEnabled(false);
		try { Presentation.InitializeIncremental(content.Source, content.Modes.ToList(), content.Layers); }
		catch
		{
			// Ownership transfers per registered layer; do not dispose layers already owned by WorldView.
			foreach (var layer in content.Layers)
			{
				if (Presentation.OwnsLayer(layer)) continue;
				if (layer is Node node)
				{
					if (node.GetParent() == null) node.Free();
				}
				else if (layer is IDisposable disposable) disposable.Dispose();
			}
			throw;
		}
		_dockPresenter = new MapDockPresenter(this.Require<MapDock>("Panels/MapDock", "MapDock"), Presentation.Modes);
		_selectionPresenter = new MapSelectionPresenter(Presentation.Selection, Presentation.Modes, content.DescribeSelection);
		_card = this.Require<CellInfoCard>("Panels/CellInfoCard", "CellInfoCard");
		_card.Bind(_selectionPresenter);
	}

	internal void SetActive(bool active)
	{
		if (active && _released) throw new InvalidOperationException("A released viewer cannot be activated.");
		Visible = active;
		this.Require<CanvasLayer>("Panels", "Panels").Visible = active;
		Presentation.SetInteractionEnabled(active && !_released);
	}

	internal void ReleaseBindings()
	{
		if (_released) return;
		_released = true;
		SetActive(false);
		_card?.Unbind();
		_selectionPresenter?.Dispose();
		_dockPresenter?.Dispose();
		_selectionPresenter = null;
		_dockPresenter = null;
	}

	public override void _ExitTree() => ReleaseBindings();
}
