using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>领域合并器：把各模块输出汇总为完整的下一轮候选世界。</summary>
public interface IWorldSculptingRoundComposer<TWorldState, TModuleOutput>
	where TWorldState : class
	where TModuleOutput : class
{
	TWorldState Compose(WorldSculptingRoundData<TWorldState, TModuleOutput> data, CancellationToken cancellation);
}
