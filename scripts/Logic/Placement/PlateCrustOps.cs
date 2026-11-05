using System;
using System.Collections.Generic;
using World.Data;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 派定壳型：每块按「格数 × 随机抖动」打分，按布局档挑出陆壳板块，其余洋壳。
/// 陆壳块数 = clamp(round(板块数 × <c>ContinentalRatio</c>), 1, 板块数)，至少一块，否则全球是海。
/// 抖动 ∈ [0.5,1.5) ⇒ 大板块更易当陆壳，但不是必然（面积重尾，实际接近按面积取前几大）。
/// 各档只改排列（多大陆 / 群岛档额外多选几块），判据一律是板块邻接：Earthlike / ManyContinents
/// 优先取不与已选相邻的板块（之间必夹洋壳 ⇒ 大陆被大洋隔开；容不下时按分补齐）；
/// Pangea 只取与已选相邻者（陆壳连成一片）；Archipelago 块数最多、不做邻接约束
/// （"碎"由 ⓪ 的基线压低、① 应用负责）。
/// </summary>
public static class PlateCrustOps
{
	/// <summary>按布局档给 <paramref name="plates"/> 写壳型（就地改 <see cref="Plate.Crust"/>）。</summary>
	/// <param name="seed">世界种子（走 <c>Plate_Crust</c> 流）。</param>
	/// <param name="spec">板块配置（陆壳占比）。</param>
	/// <param name="layout">陆块布局档（<see cref="ContinentLayout.Auto"/> 须由调用方先定档）。</param>
	/// <param name="plates">板块表（长度 = 板块数；本方法只改壳型）。</param>
	/// <param name="sizeOfPlate">逐块格数（打分用）。</param>
	/// <param name="adjacency">板块邻接矩阵（判"陆壳是否挨着"用）。</param>
	public static void Assign(int seed, PlateSpec spec, ContinentLayout layout,
		Plate[] plates, int[] sizeOfPlate, bool[,] adjacency)
	{
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		if (sizeOfPlate == null) throw new ArgumentNullException(nameof(sizeOfPlate));
		if (adjacency == null) throw new ArgumentNullException(nameof(adjacency));
		int p = plates.Length;
		if (p < 1) throw new InvalidOperationException("板块表为空：先 PlateSeedOps.Build");
		if (sizeOfPlate.Length != p)
			throw new InvalidOperationException("缺各板块格数：先 PlateFloodOps.Grow");
		if (adjacency.GetLength(0) != p)
			throw new InvalidOperationException("缺板块邻接：先 PlateFloodOps.Grow");

		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Plate_Crust));
		int baseWant = Math.Clamp((int)MathF.Round(p * spec.ContinentalRatio), 1, p);

		int want;
		switch (layout)
		{
			case ContinentLayout.ManyContinents:
				want = Math.Min(p, baseWant + ContinentLayouts.ManyExtraPlates);
				break;
			case ContinentLayout.Archipelago:
				want = Math.Min(p, baseWant + ContinentLayouts.ArchipelagoExtraPlates);
				break;
			default:                                   // Earthlike / Pangea（Auto 已在外层定档）
				want = baseWant;
				break;
		}

		var score = new float[p];
		for (int k = 0; k < p; k++)
			score[k] = sizeOfPlate[k] * (0.5f + (float)rnd.NextDouble());

		// 候选顺序：分高者在前；同分按板块号 ⇒ 与线程 / 调用次序无关的全序
		var order = new int[p];
		for (int k = 0; k < p; k++) order[k] = k;
		Array.Sort(order, (a, b) => score[a] != score[b] ? score[b].CompareTo(score[a]) : a.CompareTo(b));

		var chosen = new bool[p];
		var picked = new List<int>(want);
		switch (layout)
		{
			case ContinentLayout.Pangea:      PickContiguous(order, chosen, picked, want, adjacency); break;
			case ContinentLayout.Archipelago: PickByScore(order, chosen, picked, want); break;
			default:                          PickSplit(order, chosen, picked, want, adjacency); break;
		}

		for (int k = 0; k < p; k++) plates[k].Crust = CrustType.Oceanic;
		foreach (int k in picked) plates[k].Crust = CrustType.Continental;
	}

	/// <summary>连片挑选：第一块取分最高者，之后每轮只从与已选板块相邻的候选里取分最高者
	/// ⇒ 选出的陆壳板块彼此连通。邻接图不连通时兜底取分最高者（否则块数不达标）。</summary>
	static void PickContiguous(int[] order, bool[] chosen, List<int> picked, int want, bool[,] adjacency)
	{
		while (picked.Count < want)
		{
			int best = -1;
			foreach (int c in order)              // order 已按分降序 ⇒ 第一个命中即最高
			{
				if (chosen[c]) continue;
				if (picked.Count > 0 && !TouchesChosen(c, picked, adjacency)) continue;
				best = c;
				break;
			}
			if (best < 0)                          // 兜底：邻接图上够不到那么远
			{
				foreach (int c in order)
					if (!chosen[c]) { best = c; break; }
			}
			if (best < 0) break;
			chosen[best] = true;
			picked.Add(best);
		}
	}

	/// <summary>分开挑选：先按分降序取不与已选陆壳相邻的板块；邻接图容不下时再按分补齐
	/// ⇒ 少数几块会连在一起，像欧亚与非洲。块数一定达标，优先保分散。</summary>
	static void PickSplit(int[] order, bool[] chosen, List<int> picked, int want, bool[,] adjacency)
	{
		foreach (int c in order)
		{
			if (picked.Count >= want) break;
			if (chosen[c] || TouchesChosen(c, picked, adjacency)) continue;
			chosen[c] = true;
			picked.Add(c);
		}
		foreach (int c in order)
		{
			if (picked.Count >= want) break;
			if (chosen[c]) continue;
			chosen[c] = true;
			picked.Add(c);
		}
	}

	/// <summary>只看分数：按分降序取满（不做邻接约束）。</summary>
	static void PickByScore(int[] order, bool[] chosen, List<int> picked, int want)
	{
		foreach (int c in order)
		{
			if (picked.Count >= want) break;
			if (chosen[c]) continue;
			chosen[c] = true;
			picked.Add(c);
		}
	}

	/// <summary>该板块是否与任一块已选陆壳相邻。</summary>
	static bool TouchesChosen(int candidate, List<int> picked, bool[,] adjacency)
	{
		foreach (int q in picked)
			if (adjacency[candidate, q]) return true;
		return false;
	}
}
