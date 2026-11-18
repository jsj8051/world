using System.Text.Json.Serialization;

namespace World.Data.Civilization;

/// <summary>文明演化运行配置；当前仅包含可复现的随机种子。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CivilizationConfig
{
	[JsonRequired] public int Seed { get; set; } = 42;

	public void Validate() { }
}
