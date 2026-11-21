using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using World.Data;
using World.H3Grid;
using World.Logic;
using World.Utils;
using H3 = World.Utils.H3.H3;
using SculptingData = World.Data.WorldSculptingRoundData<World.Data.WorldSculptingState, World.Data.WorldSculptingModuleOutput>;

namespace World.Tests;

internal static class SculptingPlateCollisionTests
{
	internal static void CollisionCanConsumeEntirePlate()
	{
		var ball = new Ball(0, 2f);
		int island = Array.FindIndex(ball.CellDirs, point => point.X > .9f && Math.Abs(point.Y) < .2f);
		int[] owners = ball.CellDirs.Select((_, cell) => cell == island ? 0 : 1).ToArray();
		var state = OceanWorld(ball, owners, new[] { new SpherePointD(1, 0, 0), new SpherePointD(-1, 0, 0) });
		var north = new SpherePointD(0, 1, 0);
		var movement = new PlateMovementModule(new[] { new PlateRotation(0, north, .85), new PlateRotation(1, north, 0) });
		var data = RunTransport(state, movement, new SculptingSurfaceTransportModule(ball, 1500), 1);
		var result = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		Program.True(!result.SampledCells.Contains(island)
			&& result.Plates[0].IsExtinct && result.Surface.PlateOfCell.All(owner => owner == 1),
			"最后一个源格被实际碰撞消耗后，板块当轮消亡");
	}

	internal static void SharedRotationHasNoCollision()
	{
		var ball = new Ball(1, 2f);
		int[] owners = ball.CellDirs.Select(direction => direction.Y > .40f ? 0 : 1).ToArray();
		var state = OceanWorld(ball, owners, new[] { new SpherePointD(0, 1, 0), new SpherePointD(0, -1, 0) });
		var movement = new PlateMovementModule(new[]
		{
			new PlateRotation(0, new SpherePointD(0, 1, 0), .1),
			new PlateRotation(1, new SpherePointD(0, 1, 0), .1),
		});
		var data = RunTransport(state, movement, new SculptingSurfaceTransportModule(ball, 1500), 2);
		var result = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		Program.True(result.Coverage.Collisions.All(impact => impact == null) && result.Plates.All(plate => !plate.IsExtinct)
			&& result.Contacts.All(contact => contact == SculptingCrustContact.None),
			"共同旋转保持板块领地，H3 落格竞争不制造碰撞");
	}

	internal static void CompatibleContactMergesPlateTerritories()
	{
		var ball = new Ball(2, 2f);
		int[] owners = ball.CellDirs.Select(point => point.X > .86f ? 0 : 1).ToArray();
		float[] heights = owners.Select(owner => owner == 0 ? -550f : -1300f).ToArray();
		var state = OceanWorld(ball, owners, new[] { new SpherePointD(1, 0, 0), new SpherePointD(-1, 0, 0) }, heights);
		var axis = new SpherePointD(0, 1, 0);
		var movement = new PlateMovementModule(new[] { new PlateRotation(0, axis, .05), new PlateRotation(1, axis, .02) });
		var data = RunTransport(state, movement, new SculptingSurfaceTransportModule(ball, 1500), 1);
		var result = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		Program.True(result.Coverage.Collisions.Any(impact => impact != null) && result.ContactOwners.Contains(0) && result.Plates[0].IsExtinct
			&& result.Surface.PlateOfCell.All(owner => owner == 1), "相近运动且广泛碰撞时合并两个仍有地表的板块");
		Program.True(result.Plates[1].RotationOverride is { AngleRadians: > .02 and < .05 },
			"合并后按面积合成下一轮运动，同时保留本轮接触记录");
		var merged = SculptingElevationWriteback.Apply(ball, result.SourceElevations, result.Surface, result.Surface,
			result.Plates, result.ContactOwners, result.SampledCells, result.MissingLocalElevations, CancellationToken.None);
		int interior = Array.FindIndex(ball.CellDirs, point => point.X > .99f);
		Program.True(merged.TryGet(1, interior, CrustType.Oceanic, out float inherited) && inherited > -560
			&& !merged.TryGet(0, interior, CrustType.Oceanic, out _)
			&& result.SourceElevations.TryGet(0, interior, CrustType.Oceanic, out float original) && original == -550,
			"合并把较浅的地形写进接收板块，释放消亡板块的高程并保留旧快照");
	}

	internal static void OversizedPlateSplitsAndFragmentsMoveApart()
	{
		var ball = new Ball(2, 2f);
		int[] owners = ball.CellDirs.Select(direction => direction.X > .15f ? 0
			: direction.Y > .3f ? 1 : direction.Y < -.3f ? 2 : 3).ToArray();
		var state = OceanWorld(ball, owners, new[]
		{
			new SpherePointD(1, 0, 0), new SpherePointD(0, 1, 0), new SpherePointD(0, -1, 0), new SpherePointD(-1, 0, 0),
		});
		var movement = new PlateMovementModule(state.Plates.Select(plate =>
			new PlateRotation(plate.PlateId, new SpherePointD(0, 1, 0), 0)));
		var transport = new SculptingSurfaceTransportModule(ball, 1500, new PlateRift(ball));
		var first = RunTransport(state, movement, transport, 1);
		var divided = first.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		Program.True(divided.Split is { ParentId: 0, ChildId: 4 } && divided.Plates.Count == 5, "过大的活动板块应裂成有各自运动指令的两块");
		int parentCount = divided.Surface.PlateOfCell.Count(id => id == 0);
		int childCount = divided.Surface.PlateOfCell.Count(id => id == 4);
		Program.True(parentCount + childCount == owners.Count(id => id == 0) && Math.Abs(parentCount - childCount) <= .15 * (parentCount + childCount)
			&& divided.Surface.HeightM.SequenceEqual(state.Surface.HeightM), "裂解保留原来的地表，只沿裂带拆分归属");

		var terrain = new SculptingTerrainModule(ball, new TerrainSpec { VerticalScale = 1 });
		first.BeginModule(SculptingTerrainModule.ModuleId, new[] { PlateMovementModule.ModuleId, SculptingSurfaceTransportModule.ModuleId });
		terrain.Execute(first, CancellationToken.None);
		first.CloseModule();
		var shaped = first.Read<SculptingTerrainModule.Result>(SculptingTerrainModule.ModuleId);
		var riftEdges = shaped.Plate.Boundaries.Edges.Select((edge, index) => (edge, index))
			.Where(entry => (entry.edge.PlateA == 0 && entry.edge.PlateB == 4)
				|| (entry.edge.PlateA == 4 && entry.edge.PlateB == 0)).ToArray();
		Program.True(riftEdges.Length > 0
			&& riftEdges.All(entry => shaped.Plate.Boundary.EdgeMotion[entry.index] == 0)
			&& shaped.Surface.HeightM.SequenceEqual(divided.Surface.HeightM),
			"分家当轮保留接缝，等到下一轮实际拉开才刻张裂地形");

		var nextState = new WorldSculptingGeneration.Composer(ball).Compose(first, CancellationToken.None);
		int transferred = Enumerable.Range(0, owners.Length).First(cell => shaped.Surface.PlateOfCell[cell] == 4);
		Program.True(nextState.PlateElevations.TryGet(4, transferred, CrustType.Oceanic, out float childHeight)
			&& childHeight == shaped.Surface.HeightM[transferred]
			&& !nextState.PlateElevations.TryGet(0, transferred, CrustType.Oceanic, out _),
			"裂解沿同一世界位置转移持久高程");
		var next = RunTransport(nextState, movement, transport, 2);
		var moved = next.Read<PlateMovementResult>(PlateMovementModule.ModuleId);
		var remapped = next.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		Program.True(SeedDot(moved.Plates[0], moved.Plates[4]) < SeedDot(divided.Plates[0], divided.Plates[4])
			&& !remapped.Surface.PlateOfCell.SequenceEqual(divided.Surface.PlateOfCell),
			"下轮两个碎片必须真正旋转分开，并重新搬运逐格归属");
	}

	internal static void RiftFollowsContinuousLowland()
	{
		var ball = new Ball(2, 2f);
		int[] owners = ball.CellDirs.Select(point => point.X > .15f ? 0
			: point.Y > .3f ? 1 : point.Y < -.3f ? 2 : 3).ToArray();
		var seeds = new[] { new SpherePointD(1, 0, 0), new SpherePointD(0, 1, 0),
			new SpherePointD(0, -1, 0), new SpherePointD(-1, 0, 0) };
		float[] heights = ball.CellDirs.Select(point =>
		{
			float valley = .13f + .23f * MathF.Sin(3.4f * point.Z);
			return -900 + 5000 * Math.Clamp(MathF.Abs(point.Y - valley) / .38f, 0, 1);
		}).ToArray();
		var flat = SplitStationary(ball, OceanWorld(ball, owners, seeds));
		var lowland = SplitStationary(ball, OceanWorld(ball, owners, seeds, heights));
		Program.True(flat.Split is { ParentId: 0, ChildId: 4 } && lowland.Split is { ParentId: 0, ChildId: 4 }
			&& lowland.Surface.HeightM.SequenceEqual(heights), "按当轮地表海拔选择裂解线，保留原高度");
		var flatEdges = RiftEdges(ball, flat, 0, 4);
		var lowlandEdges = RiftEdges(ball, lowland, 0, 4);
		double MeanHeight(IEnumerable<(int First, int Second)> edges)
			=> edges.Average(edge => ((double)heights[edge.First] + heights[edge.Second]) / 2);
		Program.True(MeanHeight(lowlandEdges) < MeanHeight(flatEdges) - 1000,
			"连续裂带应沿弯曲低谷绕过高地");
		CheckSeam(ball, lowlandEdges, endpoints: 2);
	}

	internal static void GlobalPlateSplitsAlongClosedSeam()
	{
		var ball = new Ball(1, 2f);
		var state = OceanWorld(ball, new int[ball.CellIds.Length], new[] { new SpherePointD(1, 0, 0) });
		var result = SplitStationary(ball, state);
		Program.True(result.Split is { ParentId: 0, ChildId: 1 }, "无外边界的全球板块也能沿闭合裂带拆分");
		CheckSeam(ball, RiftEdges(ball, result, 0, 1), endpoints: 0);
	}

	static SculptingSurfaceTransportModule.Result SplitStationary(Ball ball, WorldSculptingState state)
	{
		var movement = new PlateMovementModule(state.Plates.Select(plate =>
			new PlateRotation(plate.PlateId, new SpherePointD(0, 1, 0), 0)));
		return RunTransport(state, movement, new SculptingSurfaceTransportModule(ball, 1500, new PlateRift(ball)), 1)
			.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
	}

	static (int First, int Second)[] RiftEdges(Ball ball, SculptingSurfaceTransportModule.Result result, int parent, int child)
	{
		var owners = result.Surface.PlateOfCell;
		return Enumerable.Range(0, owners.Count).Where(cell => owners[cell] == parent)
			.SelectMany(cell => ball.CellNeighbors[cell].Where(neighbor => owners[neighbor] == child)
				.Select(neighbor => (cell, neighbor))).ToArray();
	}

	static void CheckSeam(Ball ball, (int First, int Second)[] edges, int endpoints)
	{
		var graph = new Dictionary<ulong, List<ulong>>();
		foreach (var edge in edges)
		{
			var corners = H3.CellToVertexes(ball.CellIds[edge.First]).Intersect(H3.CellToVertexes(ball.CellIds[edge.Second])).ToArray();
			for (int i = 0; i < 2; i++)
			{
				if (!graph.TryGetValue(corners[i], out var neighbors)) graph.Add(corners[i], neighbors = new List<ulong>());
				neighbors.Add(corners[1 - i]);
			}
		}
		Program.True(graph.Count > 0 && graph.Values.All(neighbors => neighbors.Count <= 2)
			&& graph.Values.Count(neighbors => neighbors.Count == 1) == endpoints, "裂带端点数量正确且没有分叉");
		var visited = new HashSet<ulong>();
		var queue = new Queue<ulong>();
		queue.Enqueue(graph.Keys.First());
		while (queue.TryDequeue(out ulong corner))
		{
			if (!visited.Add(corner)) continue;
			foreach (ulong neighbor in graph[corner]) queue.Enqueue(neighbor);
		}
		Program.True(visited.Count == graph.Count, "所有裂解格边连为同一条线");
	}

	static double SeedDot(SculptingPlateState a, SculptingPlateState b)
	{
		var first = RotationOps.Apply(a.Orientation, a.ReferenceSeed);
		var second = RotationOps.Apply(b.Orientation, b.ReferenceSeed);
		return first.X * second.X + first.Y * second.Y + first.Z * second.Z;
	}

	static WorldSculptingState OceanWorld(Ball ball, int[] owners, SpherePointD[] seeds, float[] heights = null)
	{
		var plates = seeds.Select((seed, id) => new SculptingPlateState(id, 3, seed, RotationD.Identity)).ToArray();
		var surface = new SculptingSurfaceState(ball.Res, ball.CellIds, owners, heights ?? Enumerable.Repeat(-2600f, owners.Length),
			Enumerable.Repeat(CrustType.Oceanic, owners.Length), Enumerable.Repeat(-1, owners.Length),
			Array.Empty<CrustProvince>(), plates.Select(plate => plate.Orientation), 0);
		return new WorldSculptingState(Guid.NewGuid(), ball.Res, ball.Radius, plates, surface);
	}

	static SculptingData RunTransport(WorldSculptingState state, PlateMovementModule movement,
		SculptingSurfaceTransportModule transport, int round)
	{
		var data = new SculptingData(state, new WorldSculptingRound(round));
		data.BeginModule(PlateMovementModule.ModuleId, Array.Empty<string>());
		movement.Execute(data, CancellationToken.None);
		data.CloseModule();
		data.BeginModule(SculptingSurfaceTransportModule.ModuleId, new[] { PlateMovementModule.ModuleId });
		transport.Execute(data, CancellationToken.None);
		data.CloseModule();
		return data;
	}
}
