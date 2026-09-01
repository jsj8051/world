using Godot;
using System.Collections.Generic;
using World.MapView;

namespace World.UI;

/// <summary>格信息面板组件（2026-09-01 接入；点击地图格显示该格 + 当前图层信息）。
/// 四层模型：表现+容器全部在 scenes/ui/TileInfoPanel.tscn——【固定左下角位置 + 固定 260×168 尺寸】
/// （用户拍板 09-01：位置/大小场景钉死、代码只操控内容，消除动态定位的显示跳变；
/// 行数少量超限时由固定矩形裁切——后续需更多行再引入滚动容器）。
/// 本组件只做逻辑层：ShowAt 渲染结构化条目（TileInfoEntry：标签/值/色块——有色块时行前加
/// "标签：值"颜色块，Data 只读派生零写入）；HidePanel 隐藏。零坐标计算。</summary>
public partial class TileInfoPanel : PanelContainer
{
    private VBoxContainer _body;   // 行容器（%Body，场景预置）

    public override void _Ready()
    {
        _body = GetNode<VBoxContainer>("%Body");
    }

    /// <summary>显示：清空旧行 → 渲染条目行（色块 + "标签：值"；位置/尺寸由场景固定，无跳变）。</summary>
    public void ShowAt(IReadOnlyList<TileInfoEntry> entries)
    {
        // 清空旧行（数据驱动重建；行数少，QueueFree 可接受）
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

    /// <summary>隐藏（点击空白 / 开关）。</summary>
    public void HidePanel() => Visible = false;
}
