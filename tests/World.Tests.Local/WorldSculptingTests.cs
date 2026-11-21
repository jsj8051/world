using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using World.Data;
using World.Logic;

namespace World.Tests;

static class WorldSculptingTests
{
	sealed record State(int Value);

	sealed class Module : IWorldSculptingModule<State, State>
	{
		readonly List<string> _calls;
		readonly List<long> _rounds;
		readonly Dictionary<string, string[]> _visibleDependencies;
		readonly bool _fail;

		public string Id { get; }
		public IReadOnlyCollection<string> Dependencies { get; }
		public IReadOnlyCollection<string> RequiredFacts { get; }
		public IReadOnlyCollection<string> ProducedFacts { get; }
		internal Action<WorldSculptingRoundData<State, State>> Inspect { get; set; }

		public Module(string id, List<string> calls, List<long> rounds,
			Dictionary<string, string[]> visibleDependencies,
			string[] dependencies = null, string[] requiredFacts = null,
			string[] producedFacts = null, bool fail = false)
		{
			Id = id;
			_calls = calls;
			_rounds = rounds;
			_visibleDependencies = visibleDependencies;
			Dependencies = dependencies ?? Array.Empty<string>();
			RequiredFacts = requiredFacts ?? Array.Empty<string>();
			ProducedFacts = producedFacts ?? Array.Empty<string>();
			_fail = fail;
		}

		public void Execute(WorldSculptingRoundData<State, State> data, CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();
			_calls.Add(Id);
			_rounds.Add(data.Round.Index);
			Inspect?.Invoke(data);
			if (_visibleDependencies != null)
				_visibleDependencies[Id] = data.ReadableModuleIds.ToArray();
			if (_fail) throw new InvalidOperationException("test failure");
			data.Write(new State(1));
		}
	}

	sealed class Composer : IWorldSculptingRoundComposer<State, State>
	{
		public State Compose(WorldSculptingRoundData<State, State> data, CancellationToken cancellation)
		{
			cancellation.ThrowIfCancellationRequested();
			return new State(data.Source.Value + data.ReadableModuleIds.Sum(id => data.Read<State>(id).Value));
		}
	}

	static Module Create(string id, List<string> calls, List<long> rounds,
		Dictionary<string, string[]> visibleDependencies = null,
		string[] dependencies = null, string[] requiredFacts = null,
		string[] producedFacts = null, bool fail = false)
		=> new(id, calls, rounds, visibleDependencies, dependencies, requiredFacts, producedFacts, fail);

	static List<IWorldSculptingModule<State, State>> WorldModules(List<string> calls, List<long> rounds,
		Dictionary<string, string[]> visibleDependencies = null, string failingId = null)
	{
		return new List<IWorldSculptingModule<State, State>>
		{
			Create("biome.classification", calls, rounds, visibleDependencies,
				new[] { "climate", "hydrology" }, new[] { "climate.temperature", "water.flow" },
				new[] { "biome.class" }, failingId == "biome.classification"),
			Create("hydrology", calls, rounds, visibleDependencies,
				new[] { "climate", "terrain" }, new[] { "climate.rain", "terrain.height" },
				new[] { "water.flow" }, failingId == "hydrology"),
			Create("climate", calls, rounds, visibleDependencies,
				new[] { "terrain" }, new[] { "terrain.height" },
				new[] { "climate.rain", "climate.temperature" }, failingId == "climate"),
			Create("terrain", calls, rounds, visibleDependencies,
				new[] { "surface.transport" }, new[] { "surface.moved" },
				new[] { "terrain.height" }, failingId == "terrain"),
			Create("surface.transport", calls, rounds, visibleDependencies,
				new[] { "plate.movement" }, new[] { "plate.layout" },
				new[] { "surface.moved" }, failingId == "surface.transport"),
			Create("plate.movement", calls, rounds, visibleDependencies, producedFacts: new[] { "plate.layout" },
				fail: failingId == "plate.movement"),
		};
	}

	static WorldSculptingPipeline<State, State> BuildPipeline(
		IEnumerable<IWorldSculptingModule<State, State>> modules, IEnumerable<string> initialFacts = null)
		=> new(modules, new Composer(), initialFacts);

	public static void DependencyGraphSortsAndAllowsExtension()
	{
		var calls = new List<string>();
		var rounds = new List<long>();
		var visible = new Dictionary<string, string[]>();
		List<IWorldSculptingModule<State, State>> modules = WorldModules(calls, rounds, visible);
		modules.Add(Create("vegetation.cover", calls, rounds, visible,
			new[] { "biome.classification", "hydrology" }, new[] { "biome.class", "water.flow" },
			new[] { "vegetation.cover" }));

		var session = new WorldSculptingSession<State, State>(new State(0), BuildPipeline(modules));
		WorldSculptingRoundResult<State, State> candidate = session.PrepareNext();

		Program.True(calls.SequenceEqual(new[]
			{ "plate.movement", "surface.transport", "terrain", "climate", "hydrology", "biome.classification", "vegetation.cover" }),
			"依赖图应稳定排序，新增植被模块无需修改编排器");
		Program.True(rounds.Count == modules.Count && rounds.TrueForAll(index => index == candidate.Round.Index),
			"所有模块必须共享同一个轮次号");
		Program.True(visible["vegetation.cover"].SequenceEqual(new[]
			{ "biome.classification", "climate", "hydrology", "plate.movement", "surface.transport", "terrain" }),
			"模块只能读取其声明依赖的祖先输出，不能隐式读取无关阶段");
		Program.True(session.Current.Value == 0 && session.CompletedRounds == 0,
			"准备候选期间不得改变已发布状态");

		session.Commit(candidate);
		Program.True(session.Current.Value == modules.Count && session.CompletedRounds == 1,
			"composer 汇总完整输出后，session 原子提交候选世界");
	}

	public static void FailureAndCancellationDoNotCommit()
	{
		var calls = new List<string>();
		var rounds = new List<long>();
		var initial = new State(7);
		var session = new WorldSculptingSession<State, State>(initial,
			BuildPipeline(WorldModules(calls, rounds, failingId: "hydrology")));
		bool failed = false;
		try { session.PrepareNext(); }
		catch (InvalidOperationException) { failed = true; }
		Program.True(failed && ReferenceEquals(session.Current, initial) && session.CompletedRounds == 0,
			"模块失败不能提交半轮状态");

		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		bool canceled = false;
		try { session.PrepareNext(cancellation.Token); }
		catch (OperationCanceledException) { canceled = true; }
		Program.True(canceled && ReferenceEquals(session.Current, initial) && session.CompletedRounds == 0,
			"取消不能提交候选状态");
	}

	public static void RegistrationValidationRejectsBrokenGraphs()
	{
		var calls = new List<string>();
		var rounds = new List<long>();
		Check(new[] { Create("same", calls, rounds), Create("same", calls, rounds) },
			"重复模块 ID 必须拒绝");
		Check(new[] { Create("bad..id", calls, rounds) }, "非法模块 ID 必须拒绝");
		Check(new[] { Create("self", calls, rounds, dependencies: new[] { "self" }) }, "模块自依赖必须拒绝");
		Check(new[] { Create("repeated.dependencies", calls, rounds,
			dependencies: new[] { "source", "source" }), Create("source", calls, rounds) }, "重复模块依赖必须拒绝");
		Check(new[] { Create("repeated.facts", calls, rounds,
			producedFacts: new[] { "same.fact", "same.fact" }) }, "重复事实声明必须拒绝");
		Check(new[] { Create("consumer", calls, rounds, dependencies: new[] { "missing" }) },
			"缺失模块依赖必须拒绝");
		Check(new[]
		{
			Create("alpha", calls, rounds, dependencies: new[] { "beta" }),
			Create("beta", calls, rounds, dependencies: new[] { "alpha" }),
		}, "依赖循环必须拒绝");
		Check(new[] { Create("consumer", calls, rounds, requiredFacts: new[] { "unknown.fact" }) },
			"无来源事实必须拒绝");
		Check(new[]
		{
			Create("producer", calls, rounds, producedFacts: new[] { "shared.fact" }),
			Create("consumer", calls, rounds, requiredFacts: new[] { "shared.fact" }),
		}, "未声明依赖的事实读取必须拒绝");
		Check(new[]
		{
			Create("producer", calls, rounds, producedFacts: new[] { "shared.fact" }),
			Create("consumer", calls, rounds, dependencies: new[] { "producer" },
				producedFacts: new[] { "shared.fact" }),
		}, "重复事实生产必须拒绝");
		Check(new[] { Create("consumer", calls, rounds, requiredFacts: new[] { "world.seed" }) },
			"未声明为初始事实的输入必须拒绝");

		var initialPipeline = BuildPipeline(
			new[] { Create("seed.consumer", calls, rounds, requiredFacts: new[] { "world.seed" }) },
			new[] { "world.seed" });
		Program.True(initialPipeline != null, "显式提供的初始事实应满足模块输入契约");

		void Check(IEnumerable<IWorldSculptingModule<State, State>> modules, string message)
		{
			bool rejected = false;
			try { _ = BuildPipeline(modules); }
			catch (ArgumentException) { rejected = true; }
			Program.True(rejected, message);
		}
	}

	public static void PlanIsStableAndSnapshotsRegistration()
	{
		var calls = new List<string>();
		var rounds = new List<long>();
		var visible = new Dictionary<string, string[]>();
		var dependencies = new[] { "alpha" };
		var produced = new[] { "alpha.fact" };
		var required = new[] { "alpha.fact" };
		var modules = new List<IWorldSculptingModule<State, State>>
		{
			Create("zeta", calls, rounds, visible),
			Create("beta", calls, rounds, visible, dependencies, required),
			Create("alpha", calls, rounds, visible, producedFacts: produced),
		};
		var pipeline = BuildPipeline(modules);
		var reversed = BuildPipeline(modules.AsEnumerable().Reverse());
		var seen = new List<WorldSculptingRoundData<State, State>>();
		foreach (Module module in modules)
			module.Inspect = seen.Add;
		((Module)modules[0]).Inspect = data =>
		{
			seen.Add(data);
			Program.Throws(() => data.Read<State>("alpha"), "无关模块不能从共享数据读取先前结果");
		};
		var first = pipeline.Prepare(new State(0), 1);
		Program.True(seen.Count == 3 && seen.All(data => ReferenceEquals(data, first.Data)),
			"同一轮所有模块必须读写同一份共享数据");
		Program.Throws(() => first.Data.Write(new State(99)), "候选准备完成后必须关闭写入");
		Program.True(visible["zeta"].Length == 0
			&& visible["beta"].SequenceEqual(new[] { "alpha" }),
			"后执行的无关模块不得看到先前模块输出，依赖消费者只能看到依赖闭包");
		string[] expected = calls.ToArray();
		calls.Clear();
		var independent = reversed.Prepare(new State(0), 1);
		Program.True(!ReferenceEquals(independent.Data, first.Data), "不同世界的工作数据相互独立");
		Program.True(calls.SequenceEqual(expected) && calls.SequenceEqual(new[] { "alpha", "beta", "zeta" }),
			"注册枚举顺序不影响依赖排序，无依赖模块按 ordinal ID 排序");

		dependencies[0] = "missing";
		produced[0] = "changed.fact";
		required[0] = "unknown.fact";
		modules.Clear();
		calls.Clear();
		pipeline.Prepare(new State(0), 2);
		Program.True(calls.SequenceEqual(expected), "构造后修改注册列表和元数据不得改变执行计划");
	}

	public static void SessionRejectsStaleResult()
	{
		var calls = new List<string>();
		var rounds = new List<long>();
		var session = new WorldSculptingSession<State, State>(new State(0),
			BuildPipeline(WorldModules(calls, rounds)));
		WorldSculptingRoundResult<State, State> old = session.PrepareNext();
		session.Commit(old);
		bool staleRejected = false;
		try { session.Commit(old); }
		catch (InvalidOperationException) { staleRejected = true; }
		Program.True(staleRejected && session.CompletedRounds == 1,
			"同一候选只能提交一次，旧来源候选必须拒绝");
	}
}
