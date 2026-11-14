using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using World.Data;

namespace World.Data.Generation;

public enum SeaLevelPreset { Low, Standard, High }
public enum TerrainReliefPreset { Gentle, Standard, Rugged }
public enum TemperaturePreset { Cold, Standard, Hot }
public enum SeasonalityPreset { Weak, Standard, Strong }
public enum RainfallPreset { Dry, Standard, Wet }

/// <summary>用户界面可编辑的地图生成设置，不复制全部底层模拟参数。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GenerationConfig
{
	[JsonRequired] public int Seed { get; set; } = 42;
	[JsonRequired] public ContinentLayout Layout { get; set; } = ContinentLayout.Auto;
	[JsonRequired] public SeaLevelPreset SeaLevel { get; set; } = SeaLevelPreset.Standard;
	[JsonRequired] public int PlateCount { get; set; } = 8;
	[JsonRequired] public TerrainReliefPreset Relief { get; set; } = TerrainReliefPreset.Standard;
	[JsonRequired] public TemperaturePreset Temperature { get; set; } = TemperaturePreset.Standard;
	[JsonRequired] public SeasonalityPreset Seasonality { get; set; } = SeasonalityPreset.Standard;
	[JsonRequired] public RainfallPreset Rainfall { get; set; } = RainfallPreset.Standard;
	// Older trial profiles may still carry this field.
	[Obsolete("守恒风已默认启用。")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool UseConservativeLowWind { get => false; set { } }

	public const int MinimumPlateCount = 4;
	public const int MaximumPlateCount = 24;

	/// <summary>校验用户配置的结构约束；不执行底层参数映射或生成逻辑。</summary>
	public void Validate()
	{
		RequireDefined(Layout, nameof(Layout));
		RequireDefined(SeaLevel, nameof(SeaLevel));
		RequireDefined(Relief, nameof(Relief));
		RequireDefined(Temperature, nameof(Temperature));
		RequireDefined(Seasonality, nameof(Seasonality));
		RequireDefined(Rainfall, nameof(Rainfall));
		if (PlateCount < MinimumPlateCount || PlateCount > MaximumPlateCount)
			throw new ArgumentOutOfRangeException(nameof(PlateCount), PlateCount,
				$"板块数量必须在 {MinimumPlateCount} 到 {MaximumPlateCount} 之间。");
	}

	static void RequireDefined<TEnum>(TEnum value, string name) where TEnum : struct, Enum
	{
		if (!Enum.IsDefined(value))
			throw new ArgumentOutOfRangeException(name, value, $"{name} 不是支持的选项。");
	}
}

/// <summary>带名称的长期配置档案；配置值嵌入档案，副本隔离由使用方负责。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GenerationProfile
{
	public const int MaximumNameLength = 40;

	[JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
	[JsonRequired] public string Name { get; set; } = "";
	[JsonRequired] public DateTimeOffset CreatedUtc { get; set; }
	[JsonRequired] public DateTimeOffset UpdatedUtc { get; set; }
	[JsonRequired] public GenerationConfig Config { get; set; } = new();

	public void Validate()
	{
		if (Id == Guid.Empty) throw new ArgumentException("配置档案 ID 不能为空。", nameof(Id));
		string normalizedName = Name?.Trim();
		if (string.IsNullOrEmpty(normalizedName) || normalizedName.Length > MaximumNameLength)
			throw new ArgumentException($"配置名称去除首尾空白后必须为 1 到 {MaximumNameLength} 个字符。", nameof(Name));
		if (CreatedUtc.Offset != TimeSpan.Zero || UpdatedUtc.Offset != TimeSpan.Zero)
			throw new ArgumentException("配置档案时间必须使用 UTC。", nameof(CreatedUtc));
		if (UpdatedUtc < CreatedUtc)
			throw new ArgumentException("更新时间不能早于创建时间。", nameof(UpdatedUtc));
		if (Config == null) throw new ArgumentException("配置内容不能为空。", nameof(Config));
		Config.Validate();
	}
}

/// <summary>版本化的命名配置集合；仅定义数据形状与集合不变量。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GenerationProfileCatalog
{
	public const int CurrentSchemaVersion = 1;
	[JsonRequired] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
	[JsonRequired] public List<GenerationProfile> Profiles { get; set; } = new();

	public void Validate()
	{
		if (SchemaVersion != CurrentSchemaVersion)
			throw new ArgumentOutOfRangeException(nameof(SchemaVersion), SchemaVersion,
				$"不支持配置目录版本 {SchemaVersion}，当前版本为 {CurrentSchemaVersion}。");
		if (Profiles == null) throw new ArgumentException("配置档案列表不能为空。", nameof(Profiles));

		var ids = new HashSet<Guid>();
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var profile in Profiles)
		{
			if (profile == null) throw new ArgumentException("配置档案列表不能包含空项。", nameof(Profiles));
			profile.Validate();
			if (!ids.Add(profile.Id)) throw new ArgumentException($"配置档案 ID 重复：{profile.Id}。", nameof(Profiles));
			if (!names.Add(profile.Name.Trim())) throw new ArgumentException($"配置名称重复：{profile.Name.Trim()}。", nameof(Profiles));
		}
	}
}
