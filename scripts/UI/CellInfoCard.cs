using Godot;
using System;
using System.Text;
using World.Data;      // PickedCell（拾取结果：纯数据载体——2026-10-11 由 World.Render 迁入数据层）

namespace World.UI;

// 格子信息卡（决策 08 §4.4 表现层保留资产）：点选一格的只读信息卡，未选中时隐藏。
// **纯哑组件**：数据由宿主喂（ShowCell 吃一份 PickedCell），不感知星球与相机，不认识任何生成类型。
// ★它只 import `World.Data`：**界面层不引用表现层**（2026-10-11 收口；原 PickedCell 住 World.Render）。
// 样式为羊皮纸底 + 深金标题（全局主题 ui_medieval 的深棕字给浅底设计）。
public partial class CellInfoCard : PanelContainer
{
	Label _body;
	Label _crust;
	Button _boundaryToggle;
	Label _boundaries;
	ulong? _cellId;
	static readonly string[] Headings = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };

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

	/// <summary>
	/// 显示一格。数据是**自足的一份**（<see cref="PickedCell"/> = 拾取结果本身），
	/// 故本卡不再"自己推导事实"（海陆取 `IsLand` 字段，不取海拔正负号）——
	/// 与地图坞同源（都以世界权威字段为准），不会静默分叉。
	/// 档位名由**宿主喂入**：本卡不依赖任何色带。
	/// </summary>
	public void ShowCell(in PickedCell cell, string bandName)
		=> ShowCell(cell, bandName, null);

	internal void ShowCell(in PickedCell cell, string bandName, CrustCellDetails crust)
	{
		if (_cellId != cell.CellId || crust == null) _boundaryToggle.SetPressedNoSignal(false);
		_cellId = cell.CellId;
		string lat = $"{Math.Abs(cell.LatDeg):F1}°{(cell.LatDeg >= 0f ? "N" : "S")}";
		string lng = $"{Math.Abs(cell.LngDeg):F1}°{(cell.LngDeg >= 0f ? "E" : "W")}";
		string elev = $"{(cell.ElevM >= 0f ? "+" : "")}{cell.ElevM:F0} m";
		_body.Text = $"格 id   {cell.CellId:X}\n" +
					 $"经纬    {lat}, {lng}\n" +
					 $"海拔    {elev}（{bandName}）\n" +
					 $"海陆    {(cell.IsLand ? "陆地" : "海洋")}（陆占比 {cell.LandFraction:P0}）\n" +
					 $"离海    {cell.DistToCoast} 格      离岸 {cell.DistToLand} 格";
		_crust.Visible = crust != null;
		_boundaryToggle.Visible = crust?.Boundaries.Length > 1;
		_boundaries.Visible = _boundaryToggle.Visible && _boundaryToggle.ButtonPressed;
		if (crust != null) ShowCrust(crust);
		Visible = true;
	}

	void ShowCrust(CrustCellDetails crust)
	{
		var text = new StringBuilder();
		if (crust.TimeMa is double time) text.AppendLine($"演化年代    {time:0.00####} Ma");
		text.Append($"表层板块 {crust.PlateId}  ·  表层{(crust.Type == CrustType.Continental ? "陆壳" : "洋壳")}");
		if (crust.OceanicCoverage is double coverage) text.Append($"  ·  格内洋壳占比 {coverage:P0}");
		if (crust.OceanAgeMa is double age) text.AppendLine().Append($"洋壳平均年龄    {age:F1} Ma");
		string direction = crust.HeadingDeg is double heading
			? $"{Headings[(int)((heading + 22.5) / 45) % Headings.Length]}（{heading:F0}°）" : "近乎静止";
		text.AppendLine().Append($"板块运动    {direction}");
		if (crust.SpeedKmPerMa is double speed) text.Append($"  {Rate(speed)} cm/年");
		if (crust.MaterialHeightM is float material && crust.ReliefM is double relief)
			text.AppendLine().Append($"材料海拔    {material:+0;-0;0} m\n剖面净修正  {relief:+0;-0;0} m");

		var edges = crust.Boundaries;
		text.AppendLine();
		if (edges.Length == 0) text.Append("无直接板块边界");
		else text.Append("边界摘要    ").Append(BoundaryText(edges[0]));
		_crust.Text = text.ToString();

		if (edges.Length > 1)
		{
			_boundaryToggle.Text = $"全部相邻边界（{edges.Length}）";
			var list = new StringBuilder();
			for (int i = 0; i < edges.Length; i++)
			{
				if (i > 0) list.AppendLine();
				list.Append(BoundaryText(edges[i]));
			}
			_boundaries.Text = list.ToString();
		}
	}

	static string BoundaryText(CrustCellBoundary edge)
	{
		string motion = edge.Motion switch
		{
			BoundaryKind.Convergent => edge.Convergence == ConvergenceKind.Subduction ? "俯冲" : "陆陆碰撞",
			BoundaryKind.Divergent => "张裂",
			BoundaryKind.Transform => "走滑",
			_ => "不活动",
		};
		string side = edge.Role switch
		{
			BoundarySideRole.Overriding => "（本侧上盘）",
			BoundarySideRole.Underthrusting => "（本侧下盘）",
			_ => "",
		};
		string text = $"邻接板块 {edge.NeighborPlateId}  ·  {motion}{side}";
		if (edge.NormalKmPerMa is double normal && edge.ShearKmPerMa is double shear)
		{
			string sense = normal < 0 ? "闭合" : "张开";
			text += $"\n    法向{sense} {Rate(normal)}  ·  走滑 {Rate(shear)} cm/年";
		}
		return text;
	}

	static string Rate(double kmPerMa) => (Math.Abs(kmPerMa) * 0.1).ToString("F2");

	/// <summary>取消选中（隐藏卡片）。</summary>
	public void Clear()
	{
		_cellId = null;
		Visible = false;
	}
}
