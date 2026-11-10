using System;
using World.Data;
using World.Data.Generation;

namespace World.Params.Generation;

/// <summary>从稳定默认参数创建独立世界定义；不读取旧用户参数、不触发写盘。</summary>
public static class GenerationConfigMapper
{
	public static WorldSpec CreateWorldSpec(GenerationConfig config, WorldSpec defaults)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(defaults);
		config.Validate();
		if (defaults.Plate == null || defaults.Terrain == null || defaults.Climate == null || defaults.Evolution == null)
			throw new ArgumentException("默认世界参数必须包含全部配置段。", nameof(defaults));
		var result = new WorldSpec();
		defaults.CopyTo(result);
		result.Seed = config.Seed;
		result.Layout = config.Layout;
		result.Plate.PlateCount = config.PlateCount;
		result.Terrain.SeaLevelM = config.SeaLevel switch
		{
			SeaLevelPreset.Low => -100f, SeaLevelPreset.Standard => 0f, SeaLevelPreset.High => 100f,
			_ => throw new ArgumentOutOfRangeException(nameof(config))
		};
		result.Terrain.VerticalScale = config.Relief switch
		{
			TerrainReliefPreset.Gentle => 14f, TerrainReliefPreset.Standard => 20f, TerrainReliefPreset.Rugged => 26f,
			_ => throw new ArgumentOutOfRangeException(nameof(config))
		};
		result.Climate.Insolation = config.Temperature switch
		{
			TemperaturePreset.Cold => .9f, TemperaturePreset.Standard => 1f, TemperaturePreset.Hot => 1.1f,
			_ => throw new ArgumentOutOfRangeException(nameof(config))
		};
		result.Climate.SeasonalBaseC = config.Seasonality switch
		{
			SeasonalityPreset.Weak => 12.5f, SeasonalityPreset.Standard => 25f, SeasonalityPreset.Strong => 37.5f,
			_ => throw new ArgumentOutOfRangeException(nameof(config))
		};
		result.Climate.PrecipBaseMmPerMonth = config.Rainfall switch
		{
			RainfallPreset.Dry => 20f, RainfallPreset.Standard => 30f, RainfallPreset.Wet => 40f,
			_ => throw new ArgumentOutOfRangeException(nameof(config))
		};
		return result;
	}
}
