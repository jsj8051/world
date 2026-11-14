using Godot;
using World.Application.Components;
using World.Data; // PickedCell（拾取结果：纯数据载体）

namespace World.UI.Components;

// 格子信息卡（决策 08 §4.4 表现层保留资产）：点选一格的只读信息卡，未选中时隐藏。
// **纯哑组件**：数据由宿主喂（ShowCell 吃一份 PickedCell），不感知星球与相机，不认识任何生成类型；
// 文本格式化交给 CellInfoCardFormatter。样式为羊皮纸底 + 深金标题（全局主题 ui_medieval 的深棕字给浅底设计）。
public partial class CellInfoCard : PanelContainer
{
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

	/// <summary>显示一格（不带地壳详情）。</summary>
	public void ShowCell(in PickedCell cell, string bandName)
		=> ShowCell(cell, bandName, null);

	/// <summary>显示一格；地壳段与边界列表由 <see cref="CellInfoCardFormatter"/> 生成。</summary>
	internal void ShowCell(in PickedCell cell, string bandName, CrustCellDetails crust)
	{
		CellInfoCardContent content = CellInfoCardFormatter.Describe(cell, bandName, crust);
		if (_cellId != cell.CellId || crust == null) _boundaryToggle.SetPressedNoSignal(false);
		_cellId = cell.CellId;
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
