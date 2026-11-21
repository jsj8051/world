namespace World.Data;

/// <summary>完整世界雕刻轮的未提交结果。Source 必须仍是当前已发布状态才能提交。</summary>
public sealed class WorldSculptingRoundResult<TWorldState, TModuleOutput>
	where TWorldState : class where TModuleOutput : class
{
	public WorldSculptingRound Round => Data.Round;
	public TWorldState Source => Data.Source;
	public TWorldState Candidate { get; }
	internal WorldSculptingRoundData<TWorldState, TModuleOutput> Data { get; }

	internal WorldSculptingRoundResult(WorldSculptingRoundData<TWorldState, TModuleOutput> data, TWorldState candidate)
	{
		Data = data;
		Candidate = candidate;
	}
}
