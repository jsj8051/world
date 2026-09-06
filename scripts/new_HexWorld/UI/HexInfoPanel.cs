using Godot;
using System.Collections.Generic;
using World.MapView;   // TileInfoEntry（纯数据结构：标签/值/色块，跨世界复用）

namespace World.NewHexWorld.UI
{

	// 格信息面板（自旧 TileInfoPanel 迁移，独立演进）：位置/尺寸场景钉死，ShowAt 渲染结构化条目。
	public partial class HexInfoPanel : PanelContainer
	{
		private VBoxContainer _body;   // 行容器（%Body，场景预置）

		public override void _Ready()
		{
			_body = GetNode<VBoxContainer>("%Body");
		}

		// 显示：清空旧行 → 渲染条目行（色块 + "标签：值"）。数据驱动重建，行数少 QueueFree 可接受。
		public void ShowAt(IReadOnlyList<TileInfoEntry> entries)
		{
			foreach (Node child in _body.GetChildren())
				child.QueueFree();
			foreach (var e in entries)
			{
				var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
				row.AddThemeConstantOverride("separation", 6);
				if (e.Swatch is Color s)
				{
					var sw = new ColorRect
					{
						Color = s,
						CustomMinimumSize = new Vector2(14, 14),
						SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
					};
					row.AddChild(sw);
				}
				var lab = new Label { Text = $"{e.Label}：{e.Value}", MouseFilter = MouseFilterEnum.Ignore };
				lab.AddThemeFontSizeOverride("font_size", 13);
				row.AddChild(lab);
				_body.AddChild(row);
			}
			Visible = true;
		}

		// 隐藏（点击空白处）。
		public void HidePanel() => Visible = false;
	}
}
