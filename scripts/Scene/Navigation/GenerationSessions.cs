using Godot;
using World.Params.Generation;

namespace World.Scene;

/// <summary>
/// 生成配置会话的出厂入口：档案文件位置与存储实现留在这里，
/// 让导航管理器不认识 <c>user://</c> 约定与具体存储。
/// </summary>
internal static class GenerationSessions
{
	const string ProfilesPath = "user://worldgen/profiles.json";

	internal static GenerationConfigSession CreateDefault()
		=> new(new FileGenerationProfileStore(ProjectSettings.GlobalizePath(ProfilesPath)));
}
