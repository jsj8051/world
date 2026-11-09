using System;
using World.Data;

namespace World.Logic;

/// <summary>构造候选步未能通过校验，上一提交帧仍然有效。</summary>
public sealed class EvolutionStepException : Exception
{
	public EvolutionProblemCode Code { get; }

	public EvolutionStepException(EvolutionProblemCode code, string message, Exception inner = null)
		: base(message, inner) => Code = code;
}
