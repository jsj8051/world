using System.Collections.Generic;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>
/// 可注册的独立雕刻模块。依赖闭包决定本模块可读取哪些同轮模块输出；初始世界状态只读。
/// ID 与事实 ID 使用稳定小写标识，事实声明由管线校验。
/// </summary>
public interface IWorldSculptingModule<TWorldState, TModuleOutput>
	where TWorldState : class
	where TModuleOutput : class
{
	string Id { get; }
	IReadOnlyCollection<string> Dependencies { get; }
	IReadOnlyCollection<string> RequiredFacts { get; }
	IReadOnlyCollection<string> ProducedFacts { get; }

	TModuleOutput Execute(TWorldState source,
		IReadOnlyDictionary<string, TModuleOutput> dependencyOutputs,
		WorldSculptingRound round,
		CancellationToken cancellation);
}
