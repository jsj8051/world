using System;
using Godot;
using World.Application.Components;
using World.Data;
using World.Data.Generation;

namespace World.UI.Components;

/// <summary>
/// 生成配置编辑器：绑定四个页签的控件，把表单状态与配置值的转换、校验和加载期抑制
/// 交给 <see cref="GenerationConfigEditorController"/>。
/// </summary>
public partial class GenerationConfigEditor : TabContainer
{
	readonly GenerationConfigEditorController _controller = new();

	SpinBox _seed;
	SpinBox _plates;
	OptionButton _layout;
	OptionButton _sea;
	OptionButton _relief;
	OptionButton _temperature;
	OptionButton _season;
	OptionButton _rain;

	/// <summary>用户改动任一控件（回填期间不触发）。</summary>
	public event Action Changed;

	public override void _Ready()
	{
		for (int i = 0; i < GenerationConfigEditorController.TabTitles.Length; i++)
			SetTabTitle(i, GenerationConfigEditorController.TabTitles[i]);
		_seed = GetNode<SpinBox>("%Seed");
		_plates = GetNode<SpinBox>("%PlateCount");
		_layout = GetNode<OptionButton>("%Layout");
		_sea = GetNode<OptionButton>("%SeaLevel");
		_relief = GetNode<OptionButton>("%Relief");
		_temperature = GetNode<OptionButton>("%Temperature");
		_season = GetNode<OptionButton>("%Seasonality");
		_rain = GetNode<OptionButton>("%Rainfall");
		AddOptions(_layout, GenerationConfigEditorController.LayoutLabels);
		AddOptions(_sea, GenerationConfigEditorController.SeaLevelLabels);
		AddOptions(_relief, GenerationConfigEditorController.ReliefLabels);
		AddOptions(_temperature, GenerationConfigEditorController.TemperatureLabels);
		AddOptions(_season, GenerationConfigEditorController.SeasonalityLabels);
		AddOptions(_rain, GenerationConfigEditorController.RainfallLabels);
		_seed.ValueChanged += _ => _controller.NotifyUserEdit();
		_plates.ValueChanged += _ => _controller.NotifyUserEdit();
		foreach (var option in new[] { _layout, _sea, _relief, _temperature, _season, _rain })
			option.ItemSelected += _ => _controller.NotifyUserEdit();
		GetNode<Button>("%RandomSeed").Pressed += () => _seed.Value = GenerationConfigEditorController.RandomSeed();
		_controller.Changed += () => Changed?.Invoke();
	}

	static void AddOptions(OptionButton control, string[] labels)
	{
		for (int i = 0; i < labels.Length; i++) control.AddItem(labels[i], i);
	}

	/// <summary>读取控件当前值；非法组合抛出异常，由宿主转成错误提示。</summary>
	public GenerationConfig ReadConfig() => _controller.Read(new GenerationConfigFormState(
		(int)_seed.Value, (int)_plates.Value, _layout.GetSelectedId(), _sea.GetSelectedId(),
		_relief.GetSelectedId(), _temperature.GetSelectedId(), _season.GetSelectedId(), _rain.GetSelectedId()));

	/// <summary>回填控件；回填期间不触发 <see cref="Changed"/>。</summary>
	public void ShowConfig(GenerationConfig config)
	{
		GenerationConfigFormState state = _controller.Write(config);
		_controller.BeginLoad();
		try
		{
			_seed.Value = state.Seed;
			_plates.Value = state.PlateCount;
			_layout.Select(state.Layout);
			_sea.Select(state.SeaLevel);
			_relief.Select(state.Relief);
			_temperature.Select(state.Temperature);
			_season.Select(state.Seasonality);
			_rain.Select(state.Rainfall);
		}
		finally
		{
			_controller.EndLoad();
		}
	}
}
