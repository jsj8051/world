using Godot;
using System.Collections.Generic;

namespace World.UI;

/// <summary>格信息面板组件（2026-09-01 接入；点击地图格显示该格 + 当前图层信息）。
/// 四层模型：表现+容器全部在 scenes/ui/TileInfoPanel.tscn——【固定左下角位置 + 固定 260×168 尺寸】
/// （用户拍板 09-01：位置/大小场景钉死、代码只操控内容，消除动态定位的显示跳变；
/// 行数少量超限时由固定矩形裁切——后续需更多行再引入滚动容器）。
/// 本组件只做逻辑层：ShowAt 清空重建行 + 显示；HidePanel 隐藏。数据由 MapViewer 拾取后下行
/// （通用行 + 策略 TileInfo 只读派生），零游戏状态写入、零坐标计算。</summary>
public partial class TileInfoPanel : PanelContainer
{
    private VBoxContainer _body;   // 行容器（%Body，场景预置）

    public override void _Ready()
    {
        _body = GetNode<VBoxContainer>("%Body");
    }

    /// <summary>显示：清空旧行 → 重建数据行（位置/尺寸由场景固定，无跳变）。
    /// rows：信息行（"值"或"标签：值"），首行建议为格号/图层。</summary>
    public void ShowAt(IReadOnlyList<string> rows)
    {
        // 清空旧行（数据驱动重建；行数少，QueueFree 可接受）
        foreach (Node child in _body.GetChildren())
            child.QueueFree();
        foreach (var text in rows)
        {
            var lab = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
            lab.AddThemeFontSizeOverride("font_size", 13);
            _body.AddChild(lab);
        }
        Visible = true;
    }

    /// <summary>隐藏（点击空白 / 开关）。</summary>
    public void HidePanel() => Visible = false;
}
