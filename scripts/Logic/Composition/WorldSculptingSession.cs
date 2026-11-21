using System;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>拥有当前已发布状态；只提交基于当前来源且完整成功的候选轮次。</summary>
public sealed class WorldSculptingSession<TWorldState, TModuleOutput>
	where TWorldState : class
	where TModuleOutput : class
{
	readonly WorldSculptingPipeline<TWorldState, TModuleOutput> _pipeline;

	public TWorldState Current { get; private set; }
	public long CompletedRounds { get; private set; }

	public WorldSculptingSession(TWorldState initial,
		WorldSculptingPipeline<TWorldState, TModuleOutput> pipeline)
	{
		Current = initial ?? throw new ArgumentNullException(nameof(initial));
		_pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
	}

	public WorldSculptingRoundResult<TWorldState, TModuleOutput> PrepareNext(CancellationToken cancellation = default)
		=> _pipeline.Prepare(Current, checked(CompletedRounds + 1), cancellation);

	internal WorldSculptingRoundResult<TWorldState, TModuleOutput> PrepareNext(CancellationToken cancellation,
		Action<string> onModuleStarted)
		=> _pipeline.Prepare(Current, checked(CompletedRounds + 1), cancellation, onModuleStarted);

	public void Commit(WorldSculptingRoundResult<TWorldState, TModuleOutput> result)
	{
		if (result == null) throw new ArgumentNullException(nameof(result));
		if (result.Round.Index != checked(CompletedRounds + 1)
			|| !ReferenceEquals(result.Source, Current)
			|| result.Candidate == null)
			throw new InvalidOperationException("雕刻轮候选结果已过期、顺序错误或不完整");

		Current = result.Candidate;
		CompletedRounds = result.Round.Index;
	}
}
