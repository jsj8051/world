using System;
using Godot;
using World.Data;
using World.Data.Generation;

namespace World.UI;

/// <summary>配置编辑器只负责八项控件与值转换，不访问档案、导航或地图生成。</summary>
public partial class GenerationConfigEditor : TabContainer
{
	SpinBox _seed;
	SpinBox _plates;
	OptionButton _layout;
	OptionButton _sea;
	OptionButton _relief;
	OptionButton _temperature;
	OptionButton _season;
	OptionButton _rain;
	bool _loading;
	public event Action Changed;

	public override void _Ready()
	{
		var titles = new[] { "基础", "海陆", "地形", "气候" };
		for (int i = 0; i < titles.Length; i++) SetTabTitle(i, titles[i]);
		_seed = GetNode<SpinBox>("%Seed");
		_plates = GetNode<SpinBox>("%PlateCount");
		_layout = GetNode<OptionButton>("%Layout");
		_sea = GetNode<OptionButton>("%SeaLevel");
		_relief = GetNode<OptionButton>("%Relief");
		_temperature = GetNode<OptionButton>("%Temperature");
		_season = GetNode<OptionButton>("%Seasonality");
		_rain = GetNode<OptionButton>("%Rainfall");
		AddOptions(_layout, "随机", "地球式", "超级大陆", "群岛", "多大陆");
		AddOptions(_sea, "低", "标准", "高");
		AddOptions(_relief, "平缓", "标准", "崎岖");
		AddOptions(_temperature, "寒冷", "标准", "炎热");
		AddOptions(_season, "弱", "标准", "强");
		AddOptions(_rain, "少", "标准", "多");
		_seed.ValueChanged += _ => NotifyChange();
		_plates.ValueChanged += _ => NotifyChange();
		foreach (var option in new[] { _layout, _sea, _relief, _temperature, _season, _rain })
			option.ItemSelected += _ => NotifyChange();
		GetNode<Button>("%RandomSeed").Pressed += () => _seed.Value = Random.Shared.NextInt64(int.MinValue, (long)int.MaxValue + 1);
	}

	static void AddOptions(OptionButton control, params string[] labels)
	{
		for (int i = 0; i < labels.Length; i++) control.AddItem(labels[i], i);
	}

	void NotifyChange()
	{
		if (!_loading) Changed?.Invoke();
	}

	public GenerationConfig ReadConfig()
	{
		var config = new GenerationConfig
		{
			Seed = (int)_seed.Value, PlateCount = (int)_plates.Value,
			Layout = (ContinentLayout)_layout.GetSelectedId(), SeaLevel = (SeaLevelPreset)_sea.GetSelectedId(),
			Relief = (TerrainReliefPreset)_relief.GetSelectedId(), Temperature = (TemperaturePreset)_temperature.GetSelectedId(),
			Seasonality = (SeasonalityPreset)_season.GetSelectedId(), Rainfall = (RainfallPreset)_rain.GetSelectedId()
		};
		config.Validate();
		return config;
	}

	public void ShowConfig(GenerationConfig config)
	{
		config.Validate();
		_loading = true;
		_seed.Value = config.Seed;
		_plates.Value = config.PlateCount;
		_layout.Select((int)config.Layout);
		_sea.Select((int)config.SeaLevel);
		_relief.Select((int)config.Relief);
		_temperature.Select((int)config.Temperature);
		_season.Select((int)config.Seasonality);
		_rain.Select((int)config.Rainfall);
		_loading = false;
	}
}
