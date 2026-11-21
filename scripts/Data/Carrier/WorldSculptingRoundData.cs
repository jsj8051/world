using System;
using System.Collections.Generic;
using System.Linq;

namespace World.Data;

/// <summary>同一轮所有模块共用的数据入口，持有上轮快照和本轮候选结果。</summary>
public sealed class WorldSculptingRoundData<TWorldState, TModuleOutput>
	where TWorldState : class where TModuleOutput : class
{
	readonly Dictionary<string, TModuleOutput> _outputs = new(StringComparer.Ordinal);
	string _writingModule;
	string[] _allowedReads;

	public TWorldState Source { get; }
	public WorldSculptingRound Round { get; }

	internal WorldSculptingRoundData(TWorldState source, WorldSculptingRound round)
	{
		Source = source ?? throw new ArgumentNullException(nameof(source));
		Round = round;
	}

	public IReadOnlyList<string> ReadableModuleIds => _allowedReads == null
		? _outputs.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray()
		: (string[])_allowedReads.Clone();

	public TOutput Read<TOutput>(string moduleId) where TOutput : TModuleOutput
	{
		if (_allowedReads != null && Array.IndexOf(_allowedReads, moduleId) < 0)
			throw new InvalidOperationException($"雕刻模块 {_writingModule} 未声明对 {moduleId} 的依赖");
		if (!_outputs.TryGetValue(moduleId, out var value) || value is not TOutput typed)
			throw new InvalidOperationException($"雕刻模块 {moduleId} 缺少 {typeof(TOutput).Name} 输出");
		return typed;
	}

	public void Write(TModuleOutput output)
	{
		ArgumentNullException.ThrowIfNull(output);
		if (_writingModule == null)
			throw new InvalidOperationException("只有正在运行的雕刻模块可以写入数据");
		if (!_outputs.TryAdd(_writingModule, output))
			throw new InvalidOperationException($"雕刻模块 {_writingModule} 已写入输出");
	}

	internal void BeginModule(string moduleId, string[] allowedReads)
	{
		_writingModule = moduleId;
		_allowedReads = allowedReads;
	}

	internal void CloseModule()
	{
		_writingModule = null;
		_allowedReads = null;
	}

	internal void RequireOutput(string moduleId)
	{
		if (!_outputs.ContainsKey(moduleId))
			throw new InvalidOperationException($"雕刻模块 {moduleId} 未写入候选输出");
	}
}
