using Godot;
using System;
using World.Data;      // PickedCell（拾取结果：纯数据载体——2026-10-11 由 World.Render 迁入数据层）

namespace World.UI;

// 格子信息卡（决策 08 §4.4 表现层保留资产）：点选一格的只读信息卡，未选中时隐藏。
// **纯哑组件**：数据由宿主喂（ShowCell 吃一份 PickedCell），不感知星球与相机，不认识任何生成类型。
// ★它只 import `World.Data`：**界面层不引用表现层**（2026-10-11 收口；原 PickedCell 住 World.Render）。
// 样式为羊皮纸底 + 深金标题（全局主题 ui_medieval 的深棕字给浅底设计）。
public partial class CellInfoCard : PanelContainer
{
	Label _body;

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
		_body = new Label { Text = "" };
		box.AddChild(_body);
		Visible = false;
	}

	/// <summary>
	/// 显示一格。数据是**自足的一份**（<see cref="PickedCell"/> = 拾取结果本身），
	/// 故本卡不再"自己推导事实"（海陆取 `IsLand` 字段，不取海拔正负号）——
	/// 与地图坞同源（都以世界权威字段为准），不会静默分叉。
	/// 档位名 / 区域名由**宿主喂入**：本卡不依赖任何色带或区域类型。
	/// </summary>
	public void ShowCell(in PickedCell cell, string bandName, string regionName)
	{
		string lat = $"{Math.Abs(cell.LatDeg):F1}°{(cell.LatDeg >= 0f ? "N" : "S")}";
		string lng = $"{Math.Abs(cell.LngDeg):F1}°{(cell.LngDeg >= 0f ? "E" : "W")}";
		string elev = $"{(cell.ElevM >= 0f ? "+" : "")}{cell.ElevM:F0} m";
		_body.Text = $"格 id   {cell.CellId:X}\n" +
					 $"经纬    {lat}, {lng}\n" +
					 $"海拔    {elev}（{bandName}）\n" +
					 $"海陆    {(cell.IsLand ? "陆地" : "海洋")}（陆占比 {cell.LandFraction:P0}）\n" +
					 $"区域    {regionName}\n" +
					 $"离海    {cell.DistToCoast} 格      离岸 {cell.DistToLand} 格";
		Visible = true;
	}

	/// <summary>取消选中（隐藏卡片）。</summary>
	public void Clear() => Visible = false;
}
