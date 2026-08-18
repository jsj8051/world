using Godot;

namespace World.MapView;

/// <summary>图例条目构建器（2026-08-21 策略模式重构 M2）：策略的 BuildLegend 经此添加条目；
/// 内部操作图例面板的滚动区（box）与常驻底部说明区（footer）。
/// 面板容器/标题/高度自适应仍由 MapViewer（UI 框架）负责。</summary>
public sealed class LegendBuilder
{
    private readonly VBoxContainer _box;
    private readonly VBoxContainer _footer;

    public LegendBuilder(VBoxContainer box, VBoxContainer footer)
    {
        _box = box;
        _footer = footer;
    }

    /// <summary>图例条目：色块 + 文字（横向）。</summary>
    public void Row(Color c, string text)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var swatch = new ColorRect
        {
            Color = c,
            CustomMinimumSize = new Vector2(16, 16),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        row.AddChild(swatch);
        var lab = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center };
        lab.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(lab);
        _box.AddChild(row);
    }

    /// <summary>图例条目：渐变色带 + 两端标注。</summary>
    public void Gradient(Color[] stops, string low, string high)
    {
        // Offsets 动态生成（段数任意；均匀分布）
        var offs = new float[stops.Length];
        for (int i = 0; i < stops.Length; i++)
            offs[i] = stops.Length > 1 ? i / (float)(stops.Length - 1) : 0f;
        var bar = new GradientTexture2D
        {
            Gradient = new Gradient { Offsets = offs, Colors = stops },
            Fill = GradientTexture2D.FillEnum.Linear,
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(1, 0),
            Width = 180,
            Height = 14,
        };
        var tr = new TextureRect
        {
            Texture = bar,
            CustomMinimumSize = new Vector2(180, 14),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        _box.AddChild(tr);
        var labels = new HBoxContainer();
        var lo = new Label { Text = low }; lo.AddThemeFontSizeOverride("font_size", 12);
        var hi = new Label { Text = high, HorizontalAlignment = HorizontalAlignment.Right, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hi.AddThemeFontSizeOverride("font_size", 12);
        labels.AddChild(lo);
        labels.AddChild(hi);
        _box.AddChild(labels);
    }

    /// <summary>图例条目：纯说明文字（小字号、浅灰）——输出到滚动区外的常驻底部区
    /// （2026-08-17 用户拍板：说明文字固定显示，不随条目滚动）。</summary>
    public void Text(string text)
    {
        if (_footer == null) return;
        var lab = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lab.AddThemeFontSizeOverride("font_size", 12);
        lab.AddThemeColorOverride("font_color", new Color(0.75f, 0.78f, 0.85f));
        _footer.AddChild(lab);
    }

    /// <summary>图例动态条目：统计数组中出现过的 key，按覆盖格数降序显示前 12 个（超出滚动查看）。</summary>
    public void Dynamic(int[] tileKeys, System.Func<int, Color> colorOf, string kind)
    {
        if (tileKeys == null)
        {
            Text($"（{kind}数据未加载）");
            return;
        }
        var counts = new System.Collections.Generic.Dictionary<int, int>();
        for (int i = 0; i < tileKeys.Length; i++)
        {
            int k = tileKeys[i];
            if (k == 0) continue;
            counts[k] = counts.TryGetValue(k, out int v) ? v + 1 : 1;
        }
        if (counts.Count == 0)
        {
            Text("（无）");
            return;
        }
        var sorted = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, int>>(counts);
        sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
        int shown = Mathf.Min(12, sorted.Count);
        for (int i = 0; i < shown; i++)
            Row(colorOf(sorted[i].Key), $"{kind} {sorted[i].Key}（{sorted[i].Value}格）");
        if (sorted.Count > shown)
            Text($"…共 {sorted.Count} 个{kind}（滚动查看）");
    }
}
