using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace World.Data.Civilization;

/// <summary>带名称和元数据的文明配置档案。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CivilizationProfile
{
	public const int MaximumNameLength = 40;

	[JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
	[JsonRequired] public string Name { get; set; } = "";
	[JsonRequired] public DateTimeOffset CreatedUtc { get; set; }
	[JsonRequired] public DateTimeOffset UpdatedUtc { get; set; }
	[JsonRequired] public CivilizationConfig Config { get; set; } = new();

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

/// <summary>版本化的文明命名配置目录。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CivilizationProfileCatalog
{
	public const int CurrentSchemaVersion = 1;
	[JsonRequired] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
	[JsonRequired] public List<CivilizationProfile> Profiles { get; set; } = new();

	public void Validate()
	{
		if (SchemaVersion != CurrentSchemaVersion)
			throw new ArgumentOutOfRangeException(nameof(SchemaVersion), SchemaVersion,
				$"不支持文明配置目录版本 {SchemaVersion}，当前版本为 {CurrentSchemaVersion}。");
		if (Profiles == null) throw new ArgumentException("配置档案列表不能为空。", nameof(Profiles));
		var ids = new HashSet<Guid>();
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (CivilizationProfile profile in Profiles)
		{
			if (profile == null) throw new ArgumentException("配置档案列表不能包含空项。", nameof(Profiles));
			profile.Validate();
			if (!ids.Add(profile.Id)) throw new ArgumentException($"配置档案 ID 重复：{profile.Id}。", nameof(Profiles));
			if (!names.Add(profile.Name.Trim())) throw new ArgumentException($"配置名称重复：{profile.Name.Trim()}。", nameof(Profiles));
		}
	}
}
