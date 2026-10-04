using Godot;
using System;
using World.Render;                // BallView（档位名单一事实源）

namespace World.Render.UI;

// 格子信息卡 · 组件场景（决策 08 §4.4 表现层保留资产 · 从 `NoiseCellPanel` 迁入改名）：
//   点选格子的只读信息卡（格 id/经纬/海拔/档位/海陆），未选中时隐藏。
// **纯哑组件**：数据由宿主喂（ShowCell），不感知星球与相机，不认识任何生成类型。
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

	/// <summary>显示一个格子的信息。</summary>
	public void ShowCell(ulong id, float latDeg, float lngDeg, float elevM)
	{
		string lat = $"{MathF.Abs(latDeg):F1}°{(latDeg >= 0f ? "N" : "S")}";
		string lng = $"{MathF.Abs(lngDeg):F1}°{(lngDeg >= 0f ? "E" : "W")}";
		string elev = $"{(elevM >= 0f ? "+" : "")}{elevM:F0} m";
		_body.Text = $"格 id   {id:X}\n" +
					 $"经纬    {lat}, {lng}\n" +
					 $"海拔    {elev}（{BallView.BandName(elevM)}）\n" +
					 $"海陆    {(elevM >= 0f ? "陆地" : "海洋")}";
		Visible = true;
	}

	/// <summary>取消选中（隐藏卡片）。</summary>
	public void Clear() => Visible = false;
}
