using System;

namespace World.Data;

/// <summary>完整世界雕刻轮的序号，不编码地质时间或墙钟时间。</summary>
public readonly record struct WorldSculptingRound
{
	public long Index { get; }

	public WorldSculptingRound(long index)
	{
		if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
		Index = index;
	}
}
