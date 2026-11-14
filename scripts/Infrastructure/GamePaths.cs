using System;
using System.IO;
using Godot;

namespace World.Infrastructure;

/// <summary>游戏目录下的固定路径；编辑器取项目目录，导出后取可执行文件所在目录。</summary>
internal static class GamePaths
{
	internal static string GameDirectory => ResolveGameDirectory();

	internal static string MapDirectory => Path.Combine(GameDirectory, "userdata", "saves");

	internal static string ConfigDirectory => Path.Combine(GameDirectory, "userdata", "configs");

	internal static string GenerationProfilesPath => Path.Combine(ConfigDirectory, "generation_profiles.json");

	static string ResolveGameDirectory()
	{
		string project = ProjectSettings.GlobalizePath("res://");
		if (!string.IsNullOrEmpty(project) && File.Exists(Path.Combine(project, "project.godot")))
			return project;
		string executable = OS.GetExecutablePath();
		string directory = string.IsNullOrEmpty(executable) ? null : Path.GetDirectoryName(executable);
		return string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory;
	}
}
