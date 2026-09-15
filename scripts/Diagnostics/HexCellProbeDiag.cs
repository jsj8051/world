using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.NewHexWorld;
using World.NewHexWorld.Planet;
using World.Utils.H3;

namespace World.Diagnostics;

/// <summary>单格探针（治乱判读）：复现同参星球，回答"某格为什么长这样"——
/// 板归属 / 海拔 / 七场厚度 / 年龄 / 距最近板缘的格数 / 沿山脊的厚度剖面 / 生命周期计数。
/// 用法：
///   Godot --headless --path E:/godotGames/world --quit-after 400000 res://scenes/diag/HexCellProbeDiag.tscn
///       -- --cell=83184affffffff [--res=3 --plates=15 --seed=42]
/// 判读口径：距板缘 ≥3 格的长厚带 = 化石缝合带（碰撞在旧边界造山 → 缝合/重启/裂解把边界挪走，
/// 增厚物质随格留在板内）；贴板缘 1–2 格的带 = 终态弧带（H3BoundaryRelief 内陆偏移 2 格）。</summary>
public partial class HexCellProbeDiag : Node
{
	public override void _Ready()
	{
		var args = DiagSceneBase.ParseUserArgs();
		string cellArg = args.GetValueOrDefault("cell", "");
		int res = args.TryGetValue("res", out var r) ? int.Parse(r) : 3;
		int plates = args.TryGetValue("plates", out var p) ? int.Parse(p) : 15;
		int seed = args.TryGetValue("seed", out var s) ? int.Parse(s) : 42;
		bool scan = args.ContainsKey("scan");          // --scan：不查单格，改扫"板内厚壳带"（化石缝合带判读）

		if (cellArg.StartsWith("0x") || cellArg.StartsWith("0X")) cellArg = cellArg[2..];
		// 显示层可能截头（前导 0）也可能截尾（用户复制丢字）——先试补零候选，不行按前缀扫描取首个命中
		var candidates = new List<ulong>();
		if (!scan)
			foreach (string candidate in new[] { cellArg, "0" + cellArg, cellArg + "f" })
			{
				if (candidate.Length > 16) continue;
				candidates.Add(Convert.ToUInt64(candidate, 16));
			}

		var ball = new Ball(res, 1f);
		int index = -1;
		ulong h3 = 0;
		if (!scan)                          // --scan 模式不查单格，走末尾的"板内厚壳带"扫描
			foreach (ulong candidate in candidates)
			{
				for (int i = 0; i < ball.CellIds.Length; i++)
					if (ball.CellIds[i] == candidate) { h3 = candidate; index = i; break; }
				if (index >= 0) break;
			}
		if (index < 0 && !scan)
		{
			string prefix = cellArg.Length > 6 ? cellArg[..6] : cellArg;
			GD.PrintErr($"HexCellProbeDiag: 精确候选不在 res{res} 网格，按前缀 {prefix} 扫描：");
			for (int i = 0; i < ball.CellIds.Length && index < 0; i++)
			{
				string idText = H3.H3ToString(ball.CellIds[i]);
				if (idText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				{
					GD.PrintErr($"  取首个命中 index {i} = {idText}（与你给的串差一个尾部字符）");
					index = i;
				}
			}
			if (index < 0)
			{
				GD.PrintErr($"  前缀也未命中。格表样例（前 5 个 id）：");
				for (int i = 0; i < 5; i++) GD.PrintErr($"  index {i} = {H3.H3ToString(ball.CellIds[i])}");
				GetTree().Quit(1);
				return;
			}
		}
		GD.Print($"=== HexCellProbeDiag res={res} P={plates} seed={seed} N={ball.CellIds.Length} "
			+ $"cell={cellArg}（index {index}）===");

		var manager = new H3PlateManager();
		manager.Init(ball, plates, seed);
		var sim = manager.Simulation;
		var crust = manager.Plate.Crust;
		var fields = sim.Fields;
		var material = new World.Tectonics.MaterialDensity();
		var neighbors = ball.CellNeighbors;
		float sea = sim.SeaLevel;

		GD.Print($"[星球] 海平面={sea:F0}m 缝合={sim.SutureCount} 裂解={sim.SplitCount} "
			+ $"重启={sim.RestartCount}（{sim.LastRestartReason}） 板数={manager.NumPlates}");

		float Thick(int i) => fields.Thickness(i, material);
		string Row(int i) => $"格{i} 板{fields.PlateId[i]} {(fields.IsLand(i) ? "陆" : "洋")} "
			+ $"海拔={crust.Elevation[i]:F0}m 厚={Thick(i) / 1000f:F1}km"
			+ $"（长英质 {(fields.FelsicPlutonic[i] + fields.FelsicVolcanic[i]) / material.FelsicPlutonic / 1000f:F1}"
			+ $"/镁铁质 {(fields.MaficVolcanic[i] + fields.MaficPlutonic[i]) / material.MaficVolcanicMin / 1000f:F1}）"
			+ $" age={fields.Age[i]:F0}My";

		// 距最近板缘的格数（BFS；板缘 = 有异板邻居的格）
		int[] distance = new int[ball.CellIds.Length];
		Array.Fill(distance, -1);
		var queue = new Queue<int>();
		for (int i = 0; i < ball.CellIds.Length; i++)
		{
			if (fields.PlateId[i] < 0) continue;
			foreach (int nb in neighbors[i])
				if (fields.PlateId[nb] != fields.PlateId[i]) { distance[i] = 0; queue.Enqueue(i); break; }
		}
		while (queue.Count > 0)
		{
			int v = queue.Dequeue();
			foreach (int nb in neighbors[v])
				if (distance[nb] < 0 && fields.PlateId[nb] >= 0)
				{
					distance[nb] = distance[v] + 1;
					queue.Enqueue(nb);
				}
		}

		if (scan)
		{
			var interior = new List<int>();
			for (int i = 0; i < ball.CellIds.Length; i++)
				if (distance[i] >= 5 && Thick(i) >= 55000f) interior.Add(i);
			GD.Print($"[扫描] 板内厚壳格（厚≥55km 且 距板缘≥5格 = 化石缝合带判据）：{interior.Count} 格");
			if (interior.Count == 0) { GD.Print("  无——本世界没有板内造山带"); GetTree().Quit(0); return; }
			index = interior.OrderByDescending(i => Thick(i)).First();
			GD.Print($"[扫描] 最厚板内格 index={index} id={H3.H3ToString(ball.CellIds[index])} "
				+ $"距板缘={distance[index]}格——下方剖面即「化石缝合带」实样");
		}

		GD.Print($"[目标] {Row(index)} ｜ 距板缘={distance[index]}格 "
			+ $"板缘分类={manager.KindOfCell(index)} 最近板缘类型={manager.NearestBoundaryKindOfCell(index)}");

		// 沿脊剖面：从目标格沿"相邻厚度更大"的同板格爬到局部最大（最多 24 步），
		// 再从峰值往两侧各延伸 6 格——给出"这条山的厚度地形"。
		var chain = new List<int> { index };
		int current = index;
		for (int step = 0; step < 24; step++)
		{
			int best = -1;
			float bestThick = Thick(current);
			foreach (int nb in neighbors[current])
			{
				if (fields.PlateId[nb] != fields.PlateId[current] || chain.Contains(nb)) continue;
				if (Thick(nb) > bestThick) { bestThick = Thick(nb); best = nb; }
			}
			if (best < 0) break;
			chain.Add(best);
			current = best;
		}
		int peak = chain[^1];
		foreach (int step in Enumerable.Range(1, 6))          // 峰值向一侧延伸（山谷对照）
		{
			int best = -1;
			float bestThick = Thick(peak);
			foreach (int nb in neighbors[peak])
			{
				if (fields.PlateId[nb] != fields.PlateId[current] || chain.Contains(nb)) continue;
				if (Thick(nb) > bestThick) { bestThick = Thick(nb); best = nb; }
			}
			if (best < 0) break;
			chain.Add(best);
		}
		GD.Print($"[沿脊剖面] 峰值壳厚 {Thick(peak) / 1000f:F1}km @ 距板缘 {distance[peak]}格：");
		foreach (int i in chain)
			GD.Print($"  距板缘{distance[i],3}格 ｜ {Row(i)}");

		GetTree().Quit(0);
	}
}
