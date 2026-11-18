using System;
using World.Application.Configuration;
using World.Data.Civilization;

namespace World.Application.Civilization;

/// <summary>文明配置策略：集中定义默认值、复制、校验与变更比较。</summary>
internal sealed class CivilizationConfigStrategy : IConfigurationStrategy<CivilizationConfig>
{
	internal static readonly CivilizationConfigStrategy Instance = new();

	CivilizationConfigStrategy() { }

	public CivilizationConfig CreateDefault() => new();

	public CivilizationConfig Copy(CivilizationConfig config) => new() { Seed = config.Seed };

	public void Validate(CivilizationConfig config) { }

	public bool AreEqual(CivilizationConfig left, CivilizationConfig right) => left.Seed == right.Seed;

	internal static int RandomSeed(Random random = null)
		=> (int)(random ?? Random.Shared).NextInt64(int.MinValue, (long)int.MaxValue + 1);
}
