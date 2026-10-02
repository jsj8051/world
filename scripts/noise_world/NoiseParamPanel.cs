using Godot;
using System;
using System.Reflection;
using World.Utils;              // DeterministicRandom（种子重掷）

namespace World.NoiseWorld;

// 通用噪声调参面板 · 组件场景：把任意 TerrainNoiseParams 反射成调参行（[NoiseParam] 特性驱动，
// 手写数十个控件必错），附图层开关 + 重掷种子。**不依赖具体星球**——Bind 任何 NoiseTerrain 即用；
// 参数就地改进 terrain.Params 并置脏，宿主轮询 terrain.Dirty 防抖重算（组装层职责，不在面板内）。
// 样式：羊皮纸底 + 深金分组标题（全局主题 ui_medieval 的深棕字是给浅底设计的，黑底会糊）。
public partial class NoiseParamPanel : PanelContainer
{
	NoiseTerrain _terrain;
	Container _rows;       // 参数行挂载点
	Label _seedLabel;

	/// <summary>绑定噪声地形并生成面板行（可在入树前调用；换绑 = 清空重建）。</summary>
	public void Bind(NoiseTerrain terrain)
	{
		_terrain = terrain;
		foreach (var child in GetChildren()) child.QueueFree();
		Build();
	}

	void Build()
	{
		// 羊皮纸底（与 ui_medieval 同源色板）：主题深棕字/米黄 SpinBox 直接可用
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

		var box = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
		AddChild(box);
		_rows = box;

		AddHeader("噪声地形 · P2（噪声全栈 L0-L5）");
		BuildParamRows(_terrain.Params);
		AddHeader("图层（预览组合：关掉的层不参与合成）");
		for (int i = 0; i < NoiseTerrain.LayerCount; i++)
			AddLayerToggle(i);
		AddButton("重掷种子", () =>
		{
			_terrain.Params.Seed = new DeterministicRandom(_terrain.Params.Seed).Next();
			_terrain.NotifyParamsChanged();
			_seedLabel.Text = $"seed = {_terrain.Params.Seed}";
		});
		_seedLabel = new Label { Text = $"seed = {_terrain.Params.Seed}" };
		_rows.AddChild(_seedLabel);
	}

	// 反射行：带 [NoiseParam] 的字段按声明顺序生成 SpinBox 行；分组标题在切换处插入。
	// （MetadataToken 排序 = 声明序的稳定口径，GetFields 本身不保证顺序。）
	void BuildParamRows(object parameters)
	{
		var fields = parameters.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
		Array.Sort(fields, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
		string group = null;
		foreach (var f in fields)
		{
			var attr = f.GetCustomAttribute<NoiseParamAttribute>();
			if (attr == null || attr.SkipPanel) continue;
			if (attr.Group != group) { AddHeader(attr.Group); group = attr.Group; }
			AddParamRow(parameters, f, attr);
		}
	}

	void AddParamRow(object parameters, FieldInfo field, NoiseParamAttribute attr)
	{
		bool isInt = field.FieldType == typeof(int);
		float max = isInt ? MathF.Max(attr.Max, attr.Min + 1f) : MathF.Max(attr.Max, attr.Min + attr.Step);
		var row = new HBoxContainer();
		var lab = new Label { Text = attr.Label, CustomMinimumSize = new Vector2(110, 0), TooltipText = $"{attr.Min}–{attr.Max}" };
		var spin = new SpinBox { MinValue = attr.Min, MaxValue = max, Step = attr.Step, CustomMinimumSize = new Vector2(120, 0) };
		spin.Value = Convert.ToSingle(field.GetValue(parameters));
		spin.ValueChanged += v =>
		{
			field.SetValue(parameters, isInt ? (int)Math.Round(v) : (float)v);
			_terrain.NotifyParamsChanged();   // 就地改参通知；重算统一走宿主防抖 + Generate（段缓存跳过未变段）
		};
		row.AddChild(lab); row.AddChild(spin);
		_rows.AddChild(row);
	}

	void AddLayerToggle(int layerIdx)
	{
		var cb = new CheckButton
		{
			Text = NoiseTerrain.LayerName(layerIdx),
			ButtonPressed = _terrain.IsLayerEnabled(layerIdx),
		};
		cb.Toggled += on => _terrain.SetLayerEnabled(layerIdx, on);
		_rows.AddChild(cb);
	}

	void AddHeader(string t)
	{
		var lab = new Label { Text = t };
		lab.AddThemeColorOverride("font_color", new Color(0.45f, 0.32f, 0.08f));   // 深金分组标题（羊皮纸上）
		_rows.AddChild(lab);
	}

	void AddButton(string name, Action on)
	{
		var b = new Button { Text = name };
		b.Pressed += () => on();
		_rows.AddChild(b);
	}
}
