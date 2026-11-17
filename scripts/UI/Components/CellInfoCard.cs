using System;
using Godot;
using World.Application.Components;
using World.Data;
using World.Logic;
using World.Render;

namespace World.UI.Components;

/// <summary>随地图选中格显示或刷新摘要与地壳详情，取消选中时隐藏。</summary>
public partial class CellInfoCard : PanelContainer
{
	ICellSelectionSource _selectionSource;
	IWorldFactsSource _factsSource;
	Label _body;
	Label _crust;
	Button _boundaryToggle;
	Label _boundaries;
	ulong? _cellId;

	public override void _Ready()
	{
		var sb = new StyleBoxFlat
		{
			BgColor = new Color(0.909f, 0.862f, 0.729f),
			BorderColor = new Color(0.541f, 0.427f, 0.227f),
		};
		sb.SetBorderWidthAll(1);
		sb.SetCornerRadiusAll(6);
		sb.ContentMarginLeft = 12; sb.ContentMarginRight = 12;
		sb.ContentMarginTop = 8; sb.ContentMarginBottom = 10;
		AddThemeStyleboxOverride("panel", sb);

		var box = new VBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
		AddChild(box);
		var title = new Label { Text = "格子信息" };
		title.AddThemeColorOverride("font_color", new Color(0.45f, 0.32f, 0.08f));
		box.AddChild(title);
		_body = new Label { Name = "CellSummary" };
		box.AddChild(_body);
		_crust = new Label { Name = "CrustSummary" };
		box.AddChild(_crust);
		_boundaryToggle = new Button { Name = "ToggleBoundaries", ToggleMode = true };
		_boundaryToggle.Toggled += expanded => _boundaries.Visible = expanded;
		box.AddChild(_boundaryToggle);
		_boundaries = new Label { Name = "CrustBoundaries" };
		box.AddChild(_boundaries);
		Visible = false;
	}

	/// <summary>卡片就绪后、启用地图交互前绑定。事实源与选中格应来自同一张地图。</summary>
	public void Bind(ICellSelectionSource selectionSource, IWorldFactsSource factsSource = null)
	{
		ArgumentNullException.ThrowIfNull(selectionSource);
		if (!IsInsideTree() || _body == null)
			throw new InvalidOperationException("格子信息卡必须在场景树中就绪后绑定。");
		if (ReferenceEquals(_selectionSource, selectionSource) && ReferenceEquals(_factsSource, factsSource)) return;

		Unbind();
		_selectionSource = selectionSource;
		_factsSource = factsSource;
		_selectionSource.CellPicked += OnCellPicked;
	}

	public override void _ExitTree() => Unbind();

	void Unbind()
	{
		if (_selectionSource != null) _selectionSource.CellPicked -= OnCellPicked;
		_selectionSource = null;
		_factsSource = null;
		Clear();
	}

	void OnCellPicked(PickedCell? picked)
	{
		CellInfoCardContent content = CellInfoCardController.DescribeSelection(
			picked, _selectionSource.ShowCrustDetails, _factsSource);
		if (content == null) Clear();
		else Present(content);
	}

	/// <summary>显示一格（不带地壳详情）。</summary>
	public void ShowCell(in PickedCell cell, string bandName)
		=> Present(CellInfoCardFormatter.Describe(cell, bandName, null));

	void Present(CellInfoCardContent content)
	{
		if (_cellId != content.CellId || !content.ShowCrust) _boundaryToggle.SetPressedNoSignal(false);
		_cellId = content.CellId;
		_body.Text = content.Summary;
		_crust.Text = content.Crust;
		_crust.Visible = content.ShowCrust;
		_boundaryToggle.Text = content.BoundaryToggleText;
		_boundaryToggle.Visible = content.ShowBoundaryToggle;
		_boundaries.Text = content.Boundaries;
		_boundaries.Visible = content.ShowBoundaryToggle && _boundaryToggle.ButtonPressed;
		Visible = true;
	}

	/// <summary>取消选中（隐藏卡片）。</summary>
	public void Clear()
	{
		_cellId = null;
		Visible = false;
	}
}
