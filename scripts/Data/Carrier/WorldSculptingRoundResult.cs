namespace World.Data;

/// <summary>完整世界雕刻轮的未提交结果。Source 必须仍是当前已发布状态才能提交。</summary>
public sealed record WorldSculptingRoundResult<TWorldState, TModuleOutput>(
	WorldSculptingRound Round,
	TWorldState Source,
	TWorldState Candidate) where TWorldState : class where TModuleOutput : class;
