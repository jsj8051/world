using System;
using World.Data;
using World.Data.Generation;

namespace World.Application.Components;

/// <summary>生成配置编辑器的表单状态：选项控件用选项下标表示，便于脱离 Godot 读写。</summary>
internal readonly record struct GenerationConfigFormState(
	int Seed, int PlateCount, int Layout, int SeaLevel, int Relief, int Temperature, int Seasonality, int Rainfall);

/// <summary>
/// 生成配置编辑器的逻辑：选项表、表单状态与 <see cref="GenerationConfig"/> 的互相映射、
/// 以及回填控件期间抑制变更通知。不持有 Godot 控件，视图只负责读写控件后转调本类。
/// </summary>
internal sealed class GenerationConfigEditorController
{
	internal static readonly string[] TabTitles = { "基础", "海陆", "地形", "气候" };
	internal static readonly string[] LayoutLabels = { "随机", "地球式", "超级大陆", "群岛", "多大陆" };
	internal static readonly string[] SeaLevelLabels = { "低", "标准", "高" };
	internal static readonly string[] ReliefLabels = { "平缓", "标准", "崎岖" };
	internal static readonly string[] TemperatureLabels = { "寒冷", "标准", "炎热" };
	internal static readonly string[] SeasonalityLabels = { "弱", "标准", "强" };
	internal static readonly string[] RainfallLabels = { "少", "标准", "多" };

	internal bool IsLoading { get; private set; }

	/// <summary>用户改动控件（回填期间不发）。</summary>
	internal event Action Changed;

	internal void BeginLoad() => IsLoading = true;

	internal void EndLoad() => IsLoading = false;

	internal void NotifyUserEdit()
	{
		if (!IsLoading) Changed?.Invoke();
	}

	/// <summary>把表单状态转成配置并校验；非法选择直接抛出，由宿主转成错误提示。</summary>
	internal GenerationConfig Read(GenerationConfigFormState state)
	{
		var config = new GenerationConfig
		{
			Seed = state.Seed,
			PlateCount = state.PlateCount,
			Layout = (ContinentLayout)state.Layout,
			SeaLevel = (SeaLevelPreset)state.SeaLevel,
			Relief = (TerrainReliefPreset)state.Relief,
			Temperature = (TemperaturePreset)state.Temperature,
			Seasonality = (SeasonalityPreset)state.Seasonality,
			Rainfall = (RainfallPreset)state.Rainfall,
		};
		config.Validate();
		return config;
	}

	/// <summary>校验配置并转成表单状态；选项值以枚举本身作为下标。</summary>
	internal GenerationConfigFormState Write(GenerationConfig config)
	{
		if (config == null) throw new ArgumentNullException(nameof(config));
		config.Validate();
		return new GenerationConfigFormState(config.Seed, config.PlateCount, (int)config.Layout,
			(int)config.SeaLevel, (int)config.Relief, (int)config.Temperature,
			(int)config.Seasonality, (int)config.Rainfall);
	}

	internal static int RandomSeed(Random random = null)
		=> (int)(random ?? Random.Shared).NextInt64(int.MinValue, (long)int.MaxValue + 1);
}
