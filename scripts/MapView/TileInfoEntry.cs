using Godot;

namespace World.MapView;

/// <summary>格信息面板条目（2026-09-01 数据层结构化）：标签 + 值 + 可选色块。
/// 策略 TileInfo 返回结构化条目（只填数据，不拼显示文本），面板负责渲染成"标签：值"行
/// （含色块时行前显示该格颜色）——数据与显示解耦，测试可断言结构；swatch 常填
/// ColorOf(ctx, tile)（该格当前图层颜色）。</summary>
public readonly record struct TileInfoEntry(string Label, string Value, Color? Swatch = null);
