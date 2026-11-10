using Godot;
using System;
using World.Data.Maps;
using World.Logic;

namespace World.Tests;

/// <summary>
/// 无头导入检查：读取 <c>.wmap</c>、重建世界事实并打印摘要。
/// 用法：godot --headless --path . res://tests/scenes/MapImportCheck.tscn -- &lt;地图路径&gt;
/// </summary>
public partial class MapImportCheck : Node
{
	public override void _Ready()
	{
		string[] args = OS.GetCmdlineUserArgs();
		if (args.Length == 0)
		{
			GD.PushError("[MAP-CHECK] 缺少地图路径");
			GetTree().Quit(2);
			return;
		}

		try
		{
			ulong started = Time.GetTicksMsec();
			GeographySnapshot snapshot = MapPackageReader.Read(args[0]);
			ImportedWorldFactsSource source = ImportedWorldFactsFactory.Build(snapshot);
			WorldFacts facts = source.Facts;
			GD.Print($"[MAP-CHECK] cells={facts.Ball.CellIds.Length} res={facts.Ball.Res} " +
				$"rivers={facts.RiverTopology.RiverCellCount} lakes={facts.Lakes.LakeCount} " +
				$"evolution={(facts.Evolution != null)} timeMa={facts.SourceTimeMa?.ToString("F3") ?? "static"} " +
				$"surface={(facts.Surface.Projected != null)} ms={Time.GetTicksMsec() - started}");
			GetTree().Quit(0);
		}
		catch (Exception e)
		{
			GD.PushError($"[MAP-CHECK] 导入失败：{e.Message}");
			GetTree().Quit(1);
		}
	}
}