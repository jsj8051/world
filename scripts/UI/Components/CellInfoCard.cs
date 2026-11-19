using System;
using Godot;
using World.Application.MapViewing;

namespace World.UI.Components;

/// <summary>Displays stage-provided selection text without querying world facts.</summary>
public partial class CellInfoCard : PanelContainer
{
	MapSelectionPresenter _presenter;
	Label _body;
	Label _details;
	Button _expansionToggle;
	Label _expandedDetails;
	ulong? _cellId;

	public override void _Ready()
	{
		var style = new StyleBoxFlat
		{
			BgColor = new Color(0.909f, 0.862f, 0.729f),
			BorderColor = new Color(0.541f, 0.427f, 0.227f),
		};
		style.SetBorderWidthAll(1);
		style.SetCornerRadiusAll(6);
		style.ContentMarginLeft = 12; style.ContentMarginRight = 12;
		style.ContentMarginTop = 8; style.ContentMarginBottom = 10;
		AddThemeStyleboxOverride("panel", style);

		var box = new VBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
		AddChild(box);
		var title = new Label { Text = "格子信息" };
		title.AddThemeColorOverride("font_color", new Color(0.45f, 0.32f, 0.08f));
		box.AddChild(title);
		_body = new Label { Name = "CellSummary" };
		box.AddChild(_body);
		_details = new Label { Name = "SelectionDetails" };
		box.AddChild(_details);
		_expansionToggle = new Button { Name = "ToggleDetails", ToggleMode = true };
		_expansionToggle.Toggled += expanded => _expandedDetails.Visible = expanded;
		box.AddChild(_expansionToggle);
		_expandedDetails = new Label { Name = "ExpandedDetails" };
		box.AddChild(_expandedDetails);
		Visible = false;
	}

	internal void Bind(MapSelectionPresenter presenter)
	{
		ArgumentNullException.ThrowIfNull(presenter);
		if (_body == null) throw new InvalidOperationException("Selection card must be ready before binding.");
		Unbind();
		_presenter = presenter;
		_presenter.Changed += RefreshContent;
		RefreshContent();
	}

	public override void _ExitTree() => Unbind();

	internal void Unbind()
	{
		if (_presenter != null) _presenter.Changed -= RefreshContent;
		_presenter = null;
		_cellId = null;
		Visible = false;
	}

	void RefreshContent()
	{
		var content = _presenter?.Content;
		if (content == null)
		{
			_cellId = null;
			Visible = false;
			return;
		}
		bool expandable = !string.IsNullOrEmpty(content.ExpandedDetails);
		if (_cellId != content.CellId || !expandable) _expansionToggle.SetPressedNoSignal(false);
		_cellId = content.CellId;
		_body.Text = content.Summary;
		_details.Text = content.Details;
		_details.Visible = !string.IsNullOrEmpty(content.Details);
		_expansionToggle.Text = content.ExpansionCaption;
		_expansionToggle.Visible = expandable;
		_expandedDetails.Text = content.ExpandedDetails;
		_expandedDetails.Visible = expandable && _expansionToggle.ButtonPressed;
		Visible = true;
	}
}
