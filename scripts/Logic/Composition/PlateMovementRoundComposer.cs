using System;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>仅板块移动的轮次合并器；显式保留旧地表及其采样姿态，不冒充完整地表/气候生成。</summary>
public sealed class PlateMovementRoundComposer : IWorldSculptingRoundComposer<WorldSculptingState, WorldSculptingModuleOutput>
{
	/// <summary>仅接受本来源世界的板块移动输出；其他模块需要配套扩展领域合并器。</summary>
	public WorldSculptingState Compose(WorldSculptingRoundData<WorldSculptingState, WorldSculptingModuleOutput> data,
		CancellationToken cancellation)
	{
		cancellation.ThrowIfCancellationRequested();
		var source = data.Source;
		var movement = data.Read<PlateMovementResult>(PlateMovementModule.ModuleId);
		if (data.ReadableModuleIds.Count != 1 || !ReferenceEquals(movement.Source, source) || movement.Round != data.Round)
			throw new ArgumentException("板块移动轮必须提供本世界唯一的板块移动结果", nameof(data));
		return new WorldSculptingState(source.WorldId, source.H3Resolution, source.Radius, movement.Plates,
			source.Surface, source.PlateElevations);
	}
}
