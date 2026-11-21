using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using World.Data;
using World.Logic;
using World.Utils;
using World.Utils.H3;
using SculptingData = World.Data.WorldSculptingRoundData<World.Data.WorldSculptingState, World.Data.WorldSculptingModuleOutput>;

namespace World.Tests;

static class SculptingPlateMovementTests
{
	static readonly SpherePointD X = new(1, 0, 0);
	static readonly SpherePointD Y = new(0, 1, 0);
	static readonly SpherePointD Z = new(0, 0, 1);

	public static void StateOwnsBuffersAndRejectsInvalidData()
	{
		ulong[] ids = CellIds();
		int[] owners = { 0, 1 };
		float[] heights = { 1200, -4000 };
		CrustType[] crust = { CrustType.Continental, CrustType.Oceanic };
		int[] provinceIds = { 0, -1 };
		CrustProvince[] provinces = { new(0, CrustType.Continental, 0, 0, 1) };
		RotationD[] sampled = { RotationD.Identity, RotationD.Identity };
		ulong firstCellId = ids[0];
		var surface = new SculptingSurfaceState(0, ids, owners, heights, crust, provinceIds, provinces, sampled, 0);
		SculptingPlateState[] plates = Plates();
		var state = new WorldSculptingState(Guid.NewGuid(), 0, 2, plates, surface);
		ids[0] = 9;
		owners[0] = 1;
		heights[0] = -100;
		crust[0] = CrustType.Oceanic;
		provinceIds[0] = -1;
		sampled[0] = RotationOps.FromAxisAngle(Y, 1);
		plates[0] = null;
		Program.True(state.Surface.CellIds[0] == firstCellId && state.Surface.PlateOfCell[0] == 0
			&& state.Surface.HeightM[0] == 1200 && state.Surface.CrustTypes[0] == CrustType.Continental
			&& state.Surface.CrustProvinceIds[0] == 0 && state.Surface.CrustProvinces[0].CellCount == 1
			&& ReferenceEquals(state.Surface.SampledPlateOrientations[0], RotationD.Identity)
			&& state.Plates[0].PlateId == 0, "真实状态必须冻结所有外部可变缓冲");
		bool readOnly = false;
		try { ((IList<float>)surface.HeightM)[0] = 999; }
		catch (NotSupportedException) { readOnly = true; }
		Program.True(readOnly, "不能通过 IReadOnlyList 转型修改地表");

		Reject(() => new SculptingSurfaceState(0, new[] { firstCellId, firstCellId }, new[] { 0, 0 },
			new float[] { 0, 0 }, new[] { CrustType.Oceanic, CrustType.Oceanic }, new[] { -1, -1 },
			Array.Empty<CrustProvince>(), sampled, 0));
		Reject(() => new SculptingSurfaceState(1, new[] { firstCellId }, new[] { 0 },
			new float[] { 0 }, new[] { CrustType.Oceanic }, new[] { -1 }, Array.Empty<CrustProvince>(), sampled, 0));
		Reject(() => new SculptingSurfaceState(0, new ulong[] { 1 }, new[] { 0 },
			new float[] { 0 }, new[] { CrustType.Oceanic }, new[] { -1 }, Array.Empty<CrustProvince>(), sampled, 0));
		Reject(() => new SculptingSurfaceState(0, new[] { firstCellId }, new[] { 2 },
			new float[] { 0 }, new[] { CrustType.Oceanic }, new[] { -1 }, Array.Empty<CrustProvince>(), sampled, 0));
		Reject(() => new SculptingSurfaceState(0, new[] { firstCellId }, new[] { 0 },
			new[] { float.NaN }, new[] { CrustType.Oceanic }, new[] { -1 }, Array.Empty<CrustProvince>(), sampled, 0));
		Reject(() => new WorldSculptingState(Guid.Empty, 0, 2, Plates(), surface));
		Reject(() => new WorldSculptingState(Guid.NewGuid(), 0, double.NaN, Plates(), surface));
		Reject(() => new WorldSculptingState(Guid.NewGuid(), 0, 2, Plates().Reverse(), surface));
	}

	public static void ExplicitRotationsHandleZeroLargeAndNegativeAngles()
	{
		WorldSculptingState state = State();
		WorldSculptingState stationary = Session(state, 0, 0).PrepareNext().Candidate;
		Program.True(ReferenceEquals(stationary.Plates[0].Orientation, state.Plates[0].Orientation),
			"零旋转应保留原姿态，不引入伪变形");
		WorldSculptingState moved = Session(state, 4 * Math.PI + Math.PI / 2, -Math.PI / 2).PrepareNext().Candidate;
		Near(RotationOps.Apply(moved.Plates[0].Orientation, X), new SpherePointD(0, 0, -1));
		Near(RotationOps.Apply(moved.Plates[1].Orientation, X), Z);
		Program.True(ReferenceEquals(moved.Surface, state.Surface), "只移动板块姿态，不伪造地表搬运");
		Near(RotationOps.Apply(state.Surface.SampledPlateOrientations[0], X), X);
		Near(RotationOps.Apply(state.Plates[0].Orientation, X), X);
		Reject(() => new PlateRotation(0, Y, double.PositiveInfinity));
	}

	public static void CommittedRoundsAccumulateWorldAxisRotations()
	{
		WorldSculptingState initial = State();
		var session = Session(initial, Math.PI / 2, 0);
		var first = session.PrepareNext();
		Program.True(ReferenceEquals(session.Current, initial), "准备第一轮不得改变已发布真实状态");
		session.Commit(first);
		var second = session.PrepareNext();
		Program.True(ReferenceEquals(second.Source, first.Candidate) && second.Round.Index == 2,
			"第二轮必须从第一轮已提交真实状态开始");
		session.Commit(second);
		Near(RotationOps.Apply(session.Current.Plates[0].Orientation, X), new SpherePointD(-1, 0, 0));
		Program.True(session.Current.WorldId == initial.WorldId && session.CompletedRounds == 2
			&& ReferenceEquals(session.Current.Surface, initial.Surface), "世界身份与旧地表采样必须保留");

		RotationD before = RotationOps.FromAxisAngle(X, Math.PI / 2);
		RotationD after = RotationOps.FromAxisAngle(Y, Math.PI / 2);
		Near(RotationOps.Apply(RotationOps.Compose(after, before), Y), X);
		RotationD accumulated = RotationD.Identity;
		for (int i = 0; i < 10000; i++) accumulated = RotationOps.Compose(after, accumulated);
		accumulated.Validate();
		Near(RotationOps.Apply(accumulated, X), X);
	}

	public static void MovementRejectsBrokenPlansAndCancellation()
	{
		WorldSculptingState source = State();
		Reject(() => new PlateMovementModule(new[] { new PlateRotation(1, Y, 1) }));
		Reject(() => new PlateMovementModule(new[] { new PlateRotation(0, Y, 1), new PlateRotation(0, Y, 1) }));
		var incomplete = new PlateMovementModule(new[] { new PlateRotation(0, Y, 1) });
		Reject(() => PrepareMovement(source, incomplete));
		var plans = new[] { new PlateRotation(0, Y, Math.PI / 2), new PlateRotation(1, Y, 0) };
		var module = new PlateMovementModule(plans);
		plans[0] = new PlateRotation(0, Y, 0);
		var result = PrepareMovement(source, module).Data.Read<PlateMovementResult>(PlateMovementModule.ModuleId);
		Near(RotationOps.Apply(result.Plates[0].Orientation, X), new SpherePointD(0, 0, -1));
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var session = Session(source, 1, 1);
		bool canceled = false;
		try { session.PrepareNext(cancellation.Token); }
		catch (OperationCanceledException) { canceled = true; }
		Program.True(canceled && ReferenceEquals(session.Current, source) && session.CompletedRounds == 0,
			"取消真实板块移动不能发布候选");
		var foreign = new SculptingData(State(), new WorldSculptingRound(1));
		foreign.BeginModule(PlateMovementModule.ModuleId, Array.Empty<string>());
		foreign.Write(result);
		foreign.CloseModule();
		Reject(() => new PlateMovementRoundComposer().Compose(foreign, CancellationToken.None));
	}

	static WorldSculptingRoundResult<WorldSculptingState, WorldSculptingModuleOutput> PrepareMovement(
		WorldSculptingState source, PlateMovementModule module)
		=> new WorldSculptingPipeline<WorldSculptingState, WorldSculptingModuleOutput>(
			new[] { module }, new PlateMovementRoundComposer()).Prepare(source, 1);

	public static void GeneratedWorldInitializesRealState()
	{
		var simulation = new WorldGenSimulation(1, 2);
		LegacyWorldGeneration.RunStatic(simulation, new WorldSpec());
		WorldFacts facts = simulation.Facts;
		Guid id = Guid.NewGuid();
		WorldSculptingState state = WorldSculptingStateFactory.FromFacts(facts, id);
		Program.True(state.WorldId == id && state.Surface.CellCount == facts.Ball.CellIds.Length
			&& state.Surface.HeightM.SequenceEqual(facts.HeightM)
			&& state.Surface.PlateOfCell.SequenceEqual(facts.PlateOfCell), "从真实生成世界复制地表和板块");
		float savedHeight = state.Surface.HeightM[0];
		facts.HeightM[0] += 99;
		facts.PlateOfCell[0] = -1;
		Program.True(state.Surface.HeightM[0] == savedHeight && state.Surface.PlateOfCell[0] >= 0,
			"现有世界暴露的可变数组不能污染雕刻快照");
		var plans = state.Plates.Select(plate => new PlateRotation(plate.PlateId, Y, Math.PI));
		var pipeline = new WorldSculptingPipeline<WorldSculptingState, WorldSculptingModuleOutput>(
			new[] { new PlateMovementModule(plans) }, new PlateMovementRoundComposer());
		var session = new WorldSculptingSession<WorldSculptingState, WorldSculptingModuleOutput>(state, pipeline);
		session.Commit(session.PrepareNext());
		Program.True(session.CompletedRounds == 1 && ReferenceEquals(session.Current.Surface, state.Surface),
			"真实生成世界能完成一轮板块移动但不声称地表已搬运");
	}

	static ulong[] CellIds() => H3.GetRes0Cells().Take(2).ToArray();

	static SculptingPlateState[] Plates() => new[]
	{
		new SculptingPlateState(0, 3, X, RotationD.Identity),
		new SculptingPlateState(1, 3.1, Z, RotationD.Identity),
	};

	static WorldSculptingState State()
	{
		var surface = new SculptingSurfaceState(0, CellIds(), new[] { 0, 1 },
			new float[] { 1200, -4000 }, new[] { CrustType.Continental, CrustType.Oceanic },
			new[] { 0, -1 }, new[] { new CrustProvince(0, CrustType.Continental, 0, 0, 1) },
			new[] { RotationD.Identity, RotationD.Identity }, 0);
		return new WorldSculptingState(Guid.NewGuid(), 0, 2, Plates(), surface);
	}

	static WorldSculptingSession<WorldSculptingState, WorldSculptingModuleOutput> Session(
		WorldSculptingState state, double firstAngle, double secondAngle)
	{
		var module = new PlateMovementModule(new[] { new PlateRotation(0, Y, firstAngle), new PlateRotation(1, Y, secondAngle) });
		var pipeline = new WorldSculptingPipeline<WorldSculptingState, WorldSculptingModuleOutput>(
			new[] { module }, new PlateMovementRoundComposer());
		return new WorldSculptingSession<WorldSculptingState, WorldSculptingModuleOutput>(state, pipeline);
	}

	static void Near(SpherePointD actual, SpherePointD expected)
		=> Program.True(Math.Abs(actual.X - expected.X) < 1e-10 && Math.Abs(actual.Y - expected.Y) < 1e-10
			&& Math.Abs(actual.Z - expected.Z) < 1e-10, "球面旋转方向必须符合右手系和世界轴左乘约定");

	static void Reject(Action action)
	{
		bool rejected = false;
		try { action(); }
		catch (ArgumentException) { rejected = true; }
		Program.True(rejected, "非法真实状态、旋转计划或外来输出必须拒绝");
	}
}
