namespace World.Data;

/// <summary>演化边界类型；<see cref="Inactive"/> 与低速走滑分开，避免完全静止被当作断层。</summary>
public enum EvolutionMotionKind : byte
{
	Inactive,
	Convergent,
	Divergent,
	Transform,
}

/// <summary>汇聚边的进一步分类；<see cref="None"/> 覆盖张裂、走滑与不活动。</summary>
public enum ConvergenceKind : byte
{
	None,
	Collision,
	Subduction,
}

/// <summary>演化会话的播放、跳转与故障状态。</summary>
public enum EvolutionControlStatus : byte
{
	Uninitialized,
	Paused,
	Playing,
	Seeking,
	Faulted,
}

/// <summary>结构化演化问题的稳定分类。</summary>
public enum EvolutionProblemCode
{
	InvalidParameter,
	InvalidTransition,
	InvalidGeometry,
	InvalidCoverage,
	InvalidReference,
	NonFiniteValue,
	BudgetExceeded,
	InsufficientMantleMaterial,
	CapacityExceeded,
	TimeOverflow,
	UnsupportedModelVersion,
}
