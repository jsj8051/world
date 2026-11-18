using World.Application.Configuration;
using World.Data.Generation;

namespace World.Application.Generation;

/// <summary>地理生成配置的领域策略：隔离默认值、字段复制、校验与相等规则。</summary>
internal sealed class GenerationConfigStrategy : IConfigurationStrategy<GenerationConfig>
{
	internal static readonly GenerationConfigStrategy Instance = new();

	GenerationConfigStrategy() { }

	public GenerationConfig CreateDefault() => new();

	public GenerationConfig Copy(GenerationConfig config) => new()
	{
		Seed = config.Seed,
		Layout = config.Layout,
		SeaLevel = config.SeaLevel,
		PlateCount = config.PlateCount,
		Relief = config.Relief,
		Temperature = config.Temperature,
		Seasonality = config.Seasonality,
		Rainfall = config.Rainfall,
	};

	public void Validate(GenerationConfig config) => config.Validate();

	public bool AreEqual(GenerationConfig left, GenerationConfig right) =>
		left.Seed == right.Seed && left.Layout == right.Layout && left.SeaLevel == right.SeaLevel &&
		left.PlateCount == right.PlateCount && left.Relief == right.Relief &&
		left.Temperature == right.Temperature && left.Seasonality == right.Seasonality &&
		left.Rainfall == right.Rainfall;
}
