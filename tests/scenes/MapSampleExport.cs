using Godot;
using World.UI.Pages;

namespace World.Tests;

/// <summary>
/// 验证用样本导出：无头生成一次世界并导出 <c>.wmap</c>，
/// 打印相对游戏根目录路径供 <c>MapBrowserLoadCheck</c> 使用。
/// 用法：godot --headless --path . res://tests/scenes/MapSampleExport.tscn
/// </summary>
public partial class MapSampleExport : Node
{
	public override void _Ready()
	{
		var packed = ResourceLoader.Load<PackedScene>("res://scenes/pages/GeographyEvolution/GeographyEvolutionPage.tscn");
		if (packed == null)
		{
			GD.PushError("[SAMPLE] GeographyEvolutionPage 场景缺失");
			GetTree().Quit(2);
			return;
		}

		var world = packed.Instantiate<GeographyEvolutionView>();
		AddChild(world);

		string path = world.SaveMap();
		if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(World.Utils.RootRelativePath.Resolve(World.Infrastructure.GamePaths.GameDirectory, path)))
		{
			GD.PushError($"[SAMPLE] 导出失败：{path}");
			GetTree().Quit(1);
			return;
		}

		GD.Print($"[SAMPLE] path={path}");
		GetTree().Quit(0);
	}
}
