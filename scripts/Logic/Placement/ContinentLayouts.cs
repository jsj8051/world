using System;
using World.Data;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 陆块布局档：把 <see cref="WorldSpec"/> 的四档权重按种子抽成一档，并给出
/// <see cref="PlateManager.AssignCrustTypes"/> 用的陆壳块数增量。
/// 判"隔开"用 <see cref="PlateManager.PlateAdjacency"/> 而非角度阈值：
/// 8 块时每块近半球，种子点隔 70° 照样接壤。
/// 定档走独立 rng 流，不消耗其他序列。
/// </summary>
public static class ContinentLayouts
{
	/// <summary>多大陆样比基准多选几块陆壳（实现策略，不进 spec）。</summary>
	public const int ManyExtraPlates = 1;

	/// <summary>群岛样比基准多选几块陆壳（实现策略，不进 spec；真正的"碎"由 ⓪ 给出的基线压低、
	/// ① 阶段应用负责）。</summary>
	public const int ArchipelagoExtraPlates = 3;

	/// <summary>定档：<see cref="WorldSpec.Layout"/> 非 <see cref="ContinentLayout.Auto"/> 时直接用它；
	/// 是 Auto 则按四档权重抽（权重全零 ⇒ 兜底地球样）。</summary>
	/// <param name="spec">世界定义（种子 + 布局档 + 四档权重）。</param>
	public static ContinentLayout Resolve(WorldSpec spec)
	{
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (spec.Layout != ContinentLayout.Auto) return spec.Layout;

		float earth = MathF.Max(0f, spec.EarthlikeWeight);
		float pangea = MathF.Max(0f, spec.PangeaWeight);
		float archipelago = MathF.Max(0f, spec.ArchipelagoWeight);
		float many = MathF.Max(0f, spec.ManyContinentsWeight);
		float total = earth + pangea + archipelago + many;
		if (!(total > 0f)) return ContinentLayout.Earthlike;

		var rnd = new DeterministicRandom(SeedDerivation.Derive(spec.Seed, SeedDerivation.World_Layout));
		float u = (float)rnd.NextDouble() * total;
		if ((u -= earth) < 0f) return ContinentLayout.Earthlike;
		if ((u -= pangea) < 0f) return ContinentLayout.Pangea;
		if ((u -= archipelago) < 0f) return ContinentLayout.Archipelago;
		return ContinentLayout.ManyContinents;
	}
}
