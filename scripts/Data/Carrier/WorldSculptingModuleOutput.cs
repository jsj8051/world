using System;

namespace World.Data;

/// <summary>领域模块输出的共同身份；具体模块用派生载体表达自己的数据，不传整份候选世界。</summary>
public abstract class WorldSculptingModuleOutput
{
	public WorldSculptingState Source { get; }
	public WorldSculptingRound Round { get; }

	protected WorldSculptingModuleOutput(WorldSculptingState source, WorldSculptingRound round)
	{
		Source = source ?? throw new ArgumentNullException(nameof(source));
		if (round.Index < 1) throw new ArgumentOutOfRangeException(nameof(round));
		Round = round;
	}
}
