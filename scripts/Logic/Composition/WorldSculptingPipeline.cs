using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>
/// 注册雕刻模块并按显式依赖关系稳定排序。模块只读取初始世界和依赖祖先的输出；
/// composer 汇总全体输出，新增模块无需修改编排器。
/// </summary>
public sealed class WorldSculptingPipeline<TWorldState, TModuleOutput>
	where TWorldState : class
	where TModuleOutput : class
{
	sealed record Registration(
		IWorldSculptingModule<TWorldState, TModuleOutput> Module,
		string Id,
		string[] Dependencies,
		string[] RequiredFacts,
		string[] ProducedFacts);

	readonly Registration[] _modules;
	readonly Dictionary<string, string[]> _dependencyClosure;
	readonly IWorldSculptingRoundComposer<TWorldState, TModuleOutput> _composer;

	/// <param name="modules">本次世界配置启用的模块集合。</param>
	/// <param name="composer">将模块输出组合为完整世界状态的领域合并器。</param>
	/// <param name="initialFacts">初始世界已经具备、可供模块依赖的事实 ID。</param>
	public WorldSculptingPipeline(IEnumerable<IWorldSculptingModule<TWorldState, TModuleOutput>> modules,
		IWorldSculptingRoundComposer<TWorldState, TModuleOutput> composer,
		IEnumerable<string> initialFacts = null)
	{
		if (modules == null) throw new ArgumentNullException(nameof(modules));
		_composer = composer ?? throw new ArgumentNullException(nameof(composer));
		var initial = ReadIdentifierSet(initialFacts ?? Array.Empty<string>(), "initialFacts");
		var registrations = new Dictionary<string, Registration>(StringComparer.Ordinal);
		foreach (IWorldSculptingModule<TWorldState, TModuleOutput> module in modules)
		{
			if (module == null) throw new ArgumentException("雕刻模块注册项不能为空", nameof(modules));
			string id = ReadIdentifier(module.Id, $"{module.GetType().Name}.Id");
			var registration = new Registration(module, id,
				ReadIdentifierArray(module.Dependencies, $"{id}.Dependencies"),
				ReadIdentifierArray(module.RequiredFacts, $"{id}.RequiredFacts"),
				ReadIdentifierArray(module.ProducedFacts, $"{id}.ProducedFacts"));
			if (!registrations.TryAdd(id, registration))
				throw new ArgumentException($"雕刻模块 ID 重复：{id}", nameof(modules));
		}

		if (registrations.Count == 0)
			throw new ArgumentException("至少需要注册一个雕刻模块", nameof(modules));

		_modules = SortAndValidate(registrations, initial, nameof(modules), out _dependencyClosure);
	}

	public WorldSculptingRoundResult<TWorldState, TModuleOutput> Prepare(TWorldState source,
		long nextRoundIndex, CancellationToken cancellation = default)
		=> Prepare(source, nextRoundIndex, cancellation, null);

	internal WorldSculptingRoundResult<TWorldState, TModuleOutput> Prepare(TWorldState source,
		long nextRoundIndex, CancellationToken cancellation, Action<string> onModuleStarted)
	{
		var data = new WorldSculptingRoundData<TWorldState, TModuleOutput>(source, new WorldSculptingRound(nextRoundIndex));
		foreach (Registration registration in _modules)
		{
			cancellation.ThrowIfCancellationRequested();
			onModuleStarted?.Invoke(registration.Id);
			data.BeginModule(registration.Id, _dependencyClosure[registration.Id]);
			try { registration.Module.Execute(data, cancellation); }
			finally { data.CloseModule(); }
			data.RequireOutput(registration.Id);
		}

		cancellation.ThrowIfCancellationRequested();
		TWorldState candidate = _composer.Compose(data, cancellation)
			?? throw new InvalidOperationException("雕刻轮合并器未返回完整候选世界");
		cancellation.ThrowIfCancellationRequested();
		return new WorldSculptingRoundResult<TWorldState, TModuleOutput>(data, candidate);
	}

	static Registration[] SortAndValidate(Dictionary<string, Registration> registrations,
		HashSet<string> initialFacts, string parameterName,
		out Dictionary<string, string[]> dependencyClosure)
	{
		var indegree = new Dictionary<string, int>(StringComparer.Ordinal);
		var dependents = registrations.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
		foreach (Registration registration in registrations.Values)
		{
			indegree.Add(registration.Id, registration.Dependencies.Length);
			foreach (string dependency in registration.Dependencies)
			{
				if (dependency == registration.Id)
					throw new ArgumentException($"模块 {registration.Id} 不能依赖自身", parameterName);
				if (!registrations.ContainsKey(dependency))
					throw new ArgumentException($"模块 {registration.Id} 依赖未注册模块 {dependency}", parameterName);
				dependents[dependency].Add(registration.Id);
			}
		}

		var ready = new SortedSet<string>(StringComparer.Ordinal);
		foreach (KeyValuePair<string, int> entry in indegree)
			if (entry.Value == 0) ready.Add(entry.Key);

		var sorted = new List<Registration>(registrations.Count);
		while (ready.Count > 0)
		{
			string id = ready.Min;
			ready.Remove(id);
			Registration registration = registrations[id];
			sorted.Add(registration);
			foreach (string dependent in dependents[id])
			{
				int remaining = --indegree[dependent];
				if (remaining == 0) ready.Add(dependent);
			}
		}

		if (sorted.Count != registrations.Count)
		{
			string cycleMembers = string.Join(", ", indegree.Where(entry => entry.Value > 0)
				.Select(entry => entry.Key).OrderBy(id => id, StringComparer.Ordinal));
			throw new ArgumentException($"雕刻模块依赖存在循环：{cycleMembers}", parameterName);
		}

		var factProducers = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (string fact in initialFacts) factProducers.Add(fact, null);
		foreach (Registration registration in sorted)
			foreach (string fact in registration.ProducedFacts)
				if (!factProducers.TryAdd(fact, registration.Id))
					throw new ArgumentException($"事实 {fact} 被重复产出或覆盖", parameterName);

		dependencyClosure = new Dictionary<string, string[]>(StringComparer.Ordinal);
		var ancestorsByModule = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
		foreach (Registration registration in sorted)
		{
			var ancestors = new HashSet<string>(StringComparer.Ordinal);
			foreach (string dependency in registration.Dependencies)
			{
				ancestors.Add(dependency);
				ancestors.UnionWith(ancestorsByModule[dependency]);
			}
			ancestorsByModule.Add(registration.Id, ancestors);
			dependencyClosure.Add(registration.Id, ancestors.OrderBy(id => id, StringComparer.Ordinal).ToArray());

			foreach (string fact in registration.RequiredFacts)
			{
				if (!factProducers.TryGetValue(fact, out string producer))
					throw new ArgumentException($"模块 {registration.Id} 需要未提供的事实 {fact}", parameterName);
				if (producer != null && !ancestors.Contains(producer))
					throw new ArgumentException(
						$"模块 {registration.Id} 需要事实 {fact}，但未声明依赖其生产模块 {producer}", parameterName);
			}
		}

		return sorted.ToArray();
	}

	static HashSet<string> ReadIdentifierSet(IEnumerable<string> values, string parameterName)
	{
		if (values == null) throw new ArgumentException("标识集合不能为空", parameterName);
		var result = new HashSet<string>(StringComparer.Ordinal);
		foreach (string value in values)
		{
			string id = ReadIdentifier(value, parameterName);
			if (!result.Add(id)) throw new ArgumentException($"标识重复：{id}", parameterName);
		}
		return result;
	}

	static string[] ReadIdentifierArray(IReadOnlyCollection<string> values, string parameterName)
		=> ReadIdentifierSet(values, parameterName).ToArray();

	static string ReadIdentifier(string value, string parameterName)
	{
		if (string.IsNullOrEmpty(value) || value[0] is < 'a' or > 'z')
			throw new ArgumentException($"标识必须以小写字母开头：{value ?? "<null>"}", parameterName);
		bool separator = false;
		for (int i = 1; i < value.Length; i++)
		{
			char character = value[i];
			bool valid = character is >= 'a' and <= 'z' or >= '0' and <= '9';
			if (valid) { separator = false; continue; }
			if (character is '.' or '-' or '_')
			{
				if (separator || i == value.Length - 1)
					throw new ArgumentException($"标识格式非法：{value}", parameterName);
				separator = true;
				continue;
			}
			throw new ArgumentException($"标识格式非法：{value}", parameterName);
		}
		return value;
	}
}
