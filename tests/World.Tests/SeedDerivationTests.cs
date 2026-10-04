using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 种子派生表护栏（收口 §07 D-6 / T9）：
///   · **唯一性**：表内标签两两不同 ⇒ 任意两个子系统不会拿到同一条随机流
///     （复制粘贴魔数是"只在个别 seed 上显现的伪相关"型 bug，必须编译期钉死）；
///   · **退化解**（永久架构原则）：`Derive` 就是原来的 `seed ^ tag`，不得混入额外混合。
///     因此改表前后同一个 seed 生成的世界逐位相同——本文件不重复钉世界快照，
///     由既有确定性测试（GeologicalRegionsTests / MountainSkeletonTests / …）覆盖。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class SeedDerivationTests
{
	[Test]
	public void Tags_AreUnique()
	{
		var tags = SeedDerivation.AllTags;
		Assert.That(tags.Length, Is.GreaterThanOrEqualTo(13), "派生表条目数应与登记一致（新增子系统在表尾追加）");
		var dup = tags.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
		Assert.That(dup, Is.Empty,
			"种子派生标签不得重复：重复 ⇒ 两个子系统共用同一条随机流（伪相关 bug）");
	}

	[Test]
	public void Derive_IsPureXor_NoExtraMixing()
	{
		// ★退化解：Derive 必须与改造前的 `seed ^ tag` 逐位相同，不得混入 splitmix/乘法等。
		foreach (int seed in new[] { 0, 1, 7, 42, 1000, -13, int.MaxValue })
		{
			Assert.That(SeedDerivation.Derive(seed, 0), Is.EqualTo(seed), "tag=0 必须恒等（未列出的子系统 = 直接用主种子）");
			foreach (int tag in SeedDerivation.AllTags)
			{
				Assert.That(SeedDerivation.Derive(seed, tag), Is.EqualTo(seed ^ tag));
				// 可逆性：施加同一标签两次回到原值 ⇒ 派生不丢信息
				Assert.That(SeedDerivation.Derive(SeedDerivation.Derive(seed, tag), tag), Is.EqualTo(seed));
			}
		}
	}

	[Test]
	public void Derive_GivesDistinctStreamsPerTag()
	{
		int seed = 12345;
		var derived = SeedDerivation.AllTags.Select(t => SeedDerivation.Derive(seed, t)).ToArray();
		Assert.That(derived.Distinct().Count(), Is.EqualTo(derived.Length),
			"同一主种子下，不同标签必须派生出不同的流");
	}

	[Test]
	public void Derive_IsDeterministic()
	{
		for (int i = 0; i < 8; i++)
			Assert.That(SeedDerivation.Derive(2026, SeedDerivation.Regions_Noise),
				Is.EqualTo(2026 ^ SeedDerivation.Regions_Noise));
	}
}
