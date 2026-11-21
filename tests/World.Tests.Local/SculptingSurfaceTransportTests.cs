using System;
using System.Linq;
using System.Threading;
using Godot;
using World.Data;
using World.H3Grid;
using World.Logic;
using World.Utils;
using World.Utils.H3;
using SculptingData = World.Data.WorldSculptingRoundData<World.Data.WorldSculptingState, World.Data.WorldSculptingModuleOutput>;

namespace World.Tests;

static class SculptingSurfaceTransportTests
{
	internal static void RotationMovesSurfaceAndResolvesOverlapAndGaps()
	{
		var ball = new Ball(0, 2f);
		var north = new SpherePointD(0, 1, 0);
		var plates = new[]
		{
			new SculptingPlateState(0, 3.2, new SpherePointD(1, 0, 0), RotationD.Identity),
			new SculptingPlateState(1, 3.0, new SpherePointD(-1, 0, 0), RotationD.Identity),
		};
		int[] owners = ball.CellDirs.Select(direction => direction.X > 0 ? 0 : 1).ToArray();
		float[] heights = owners.Select((owner, cell) => owner == 0 ? 100f + cell : -50f).ToArray();
		CrustType[] types = owners.Select(owner => owner == 0 ? CrustType.Continental : CrustType.Oceanic).ToArray();
		int[] provinces = owners.Select(owner => owner == 0 ? 0 : -1).ToArray();
		var sourceSurface = new SculptingSurfaceState(ball.Res, ball.CellIds, owners, heights, types, provinces,
			new[] { new CrustProvince(0, CrustType.Continental, Array.IndexOf(owners, 0), 0, owners.Count(owner => owner == 0)) },
			plates.Select(plate => plate.Orientation), 0);
		var source = new WorldSculptingState(Guid.NewGuid(), ball.Res, ball.Radius, plates, sourceSurface);
		var plan = new[]
		{
			new PlateRotation(0, north, .65),
			new PlateRotation(1, north, 0),
		};
		var data = Run(ball, source, plan);
		var poses = data.Read<PlateMovementResult>(PlateMovementModule.ModuleId);
		var result = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		var repeat = Run(ball, source, plan).Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		var surface = result.Surface;
		Program.True(ball.CellIds.SequenceEqual(surface.CellIds) && surface.HeightM.SequenceEqual(repeat.Surface.HeightM)
			&& result.Contacts.SequenceEqual(repeat.Contacts),
			"搬运保留 H3 格序且结果可复现");
		Program.True(surface.HeightM.Any(height => height > 100f && height != MathF.Truncate(height)),
			"搬运后的亚格位置用同板块邻格混合高程，避免重复最近格复制");
		Program.True(surface.CrustProvinceIds.Count(id => id == 0) == surface.CrustProvinces[0].CellCount
			&& surface.CrustProvinceIds[surface.CrustProvinces[0].SeedCell] == 0,
			"省的格数和种子跟随搬运");
		Program.True(ReferenceEquals(surface.SampledPlateOrientations[0], poses.Plates[0].Orientation)
			&& source.Surface.HeightM.SequenceEqual(heights), "本轮更新采样姿态，保留来源快照");
		var relief = data.Read<SculptingTerrainModule.Result>(SculptingTerrainModule.ModuleId).Surface;
		var overlap = Enumerable.Range(0, surface.CellCount)
			.Where(cell => result.Contacts[cell] == SculptingCrustContact.ContinentalMargin).ToArray();
		Program.True(overlap.Length > 0 && overlap.All(cell => surface.CrustTypes[cell] == CrustType.Continental
			&& relief.HeightM[cell] > surface.HeightM[cell]), "较密的陆壳在陆洋碰撞时也应保留并隆起");
		CheckSampling(ball, result, source.Surface);
		var sampled = result.SampledCells.Where(sample => sample >= 0).ToArray();
		Program.True(sampled.Length > sampled.Distinct().Count(), "反向采样允许复用旧格，无需一对一分配");

		var common = Run(ball, source, new[] { new PlateRotation(0, north, .5), new PlateRotation(1, north, .5) });
		var commonTransport = common.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		var commonTerrain = common.Read<SculptingTerrainModule.Result>(SculptingTerrainModule.ModuleId);
		Program.True(commonTransport.Contacts.All(contact => contact == SculptingCrustContact.None)
			&& commonTransport.Surface.HeightM.SequenceEqual(commonTerrain.Surface.HeightM),
			"所有板块共同转动时，搬运不制造分离或碰撞地形");
		CheckSampling(ball, commonTransport, source.Surface);
	}

	static void CheckSampling(Ball ball, SculptingSurfaceTransportModule.Result transport, SculptingSurfaceState initial)
	{
		for (int target = 0; target < initial.CellCount; target++)
		{
			int sample = transport.SampledCells[target];
			Program.True(sample < 0
				? transport.Coverage.Openings[target].HasValue
					&& transport.Contacts[target] is SculptingCrustContact.OceanSpreading or SculptingCrustContact.ContinentalStretch
				: transport.Surface.PlateOfCell[target] == initial.PlateOfCell[sample]
					&& transport.Surface.CrustTypes[target] == initial.CrustTypes[sample],
				"目标格从旋转后的地表采样，只有张裂空缺才能生成新地表");
			if (sample < 0) continue;
			float height = initial.HeightM[sample];
			var stencil = ball.CellNeighbors[sample].Where(neighbor =>
				initial.PlateOfCell[neighbor] == initial.PlateOfCell[sample]
				&& initial.CrustTypes[neighbor] == initial.CrustTypes[sample]
				&& (initial.HeightM[neighbor] > initial.SeaLevelM) == (height > initial.SeaLevelM))
				.Select(neighbor => initial.HeightM[neighbor]).Append(height);
			float actual = transport.Surface.HeightM[target];
			Program.True(actual >= stencil.Min() && actual <= stencil.Max(),
				"重建高程限制在同板块、同壳型、同海陆的来源邻域内");
		}
	}

	internal static void RigidRotationPreservesRidgeRelief()
	{
		var ball = new Ball(3, 2f);
		var center = new Vector3(.71f, -.4f, .58f).Normalized();
		var along = center.Cross(Vector3.Up).Normalized();
		var across = center.Cross(along).Normalized();
		const double ridgeWidthKm = 100 * 2.6457513110645907;
		const float baseHeightM = 100;
		var plates = new[] { new SculptingPlateState(0, 3, new SpherePointD(1, 0, 0), RotationD.Identity) };
		int count = ball.CellIds.Length;
		var surface = new SculptingSurfaceState(ball.Res, ball.CellIds, new int[count],
			ball.CellDirs.Select(dir => baseHeightM + (float)RidgeHeight(dir)),
			Enumerable.Repeat(CrustType.Continental, count), new int[count],
			new[] { new CrustProvince(0, CrustType.Continental, 0, 0, count) },
			plates.Select(plate => plate.Orientation), 0);
		var id = Guid.NewGuid();
		var initial = new WorldSculptingState(id, ball.Res, ball.Radius, plates, surface);
		var state = initial;
		double axisLength = Math.Sqrt(14);
		var axis = new SpherePointD(1 / axisLength, 2 / axisLength, 3 / axisLength);
		var rotations = new[] { new PlateRotation(0, axis, .055) };
		for (int round = 1; round <= 20; round++)
		{
			var data = RunTransport(ball, state, rotations, round);
			var moved = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
			Program.True(moved.Contacts.All(contact => contact == SculptingCrustContact.None), "单板刚体旋转没有构造接触");
			var next = CommitTransport(ball, state, moved);
			Program.True(ReferenceEquals(next.PlateElevations, moved.SourceElevations),
				"纯旋转复用整份板块高程");
			state = next;
		}

		var direct = ReadTransport(RunTransport(ball, initial, new[] { new PlateRotation(0, axis, 20 * .055) }, 1));
		Program.True(state.Surface.HeightM.Zip(direct.Surface.HeightM,
			(repeated, once) => Math.Abs(repeated - once)).Max() < .002,
			"纯旋转多轮与按最终姿态直接采样一致");

		var inverse = RotationOps.Inverse(state.Plates[0].Orientation);
		double actualPeak = 0, exactPeak = 0;
		double actualArea = 0, exactArea = 0, actualFirstMoment = 0, actualSecondMoment = 0;
		for (int cell = 0; cell < count; cell++)
		{
			Vector3 direction = ball.CellDirs[cell];
			var (x, y, z) = RotationOps.Apply(inverse, direction.X, direction.Y, direction.Z);
			var local = new Vector3((float)x, (float)y, (float)z);
			double actual = state.Surface.HeightM[cell] - baseHeightM;
			double exact = RidgeHeight(local);
			double area = H3.CellAreaKm2(ball.CellIds[cell]);
			double offset = Coordinate(local, across);
			double weight = actual * area;
			actualPeak = Math.Max(actualPeak, actual);
			exactPeak = Math.Max(exactPeak, exact);
			actualArea += weight;
			exactArea += exact * area;
			actualFirstMoment += weight * offset;
			actualSecondMoment += weight * offset * offset;
		}
		double centroid = actualFirstMoment / actualArea;
		double width = 2.35482 * Math.Sqrt(actualSecondMoment / actualArea - centroid * centroid);
		Program.Near(actualPeak / exactPeak, 1, .22, "20 轮搬运后窄山脊保留峰高");
		Program.Near(width / ridgeWidthKm, 1, .35, "20 轮搬运后窄山脊保持横向尺度");
		Program.Near(actualArea / exactArea, 1, .15, "20 轮搬运后相对基准面的面积加权高程漂移有限");

		double Coordinate(Vector3 direction, Vector3 basis)
			=> SphericalFbmNoise.WorldRadiusKm * Math.Atan2(direction.Dot(basis), direction.Dot(center));

		double RidgeHeight(Vector3 direction)
		{
			double transverse = Coordinate(direction, across) / ridgeWidthKm;
			double longitudinal = Coordinate(direction, along) / 1100;
			return 2500 * Math.Exp(-4 * Math.Log(2) * (transverse * transverse + longitudinal * longitudinal));
		}
	}

	internal static void ShapedElevationsFollowPlateWithoutApplyingTwice()
	{
		var ball = new Ball(2, 2f);
		int count = ball.CellIds.Length;
		const float baseHeight = 100;
		const float uplift = 300;
		var axis = new SpherePointD(0, 1, 0);
		var plates = new[] { new SculptingPlateState(0, 3, new SpherePointD(1, 0, 0), RotationD.Identity) };
		var surface = new SculptingSurfaceState(ball.Res, ball.CellIds, new int[count],
			Enumerable.Repeat(baseHeight, count), Enumerable.Repeat(CrustType.Continental, count), new int[count],
			new[] { new CrustProvince(0, CrustType.Continental, 0, 0, count) },
			plates.Select(plate => plate.Orientation), 0);
		var initial = new WorldSculptingState(Guid.NewGuid(), ball.Res, ball.Radius, plates, surface);
		var rotations = new[] { new PlateRotation(0, axis, .31) };
		var transported = ReadTransport(RunTransport(ball, initial, rotations, 1));
		var shaped = transported.Surface.WithHeights(ball.CellDirs.Select(dir => dir.X > .78f
			? baseHeight + uplift : baseHeight).ToArray());
		var edited = CommitTransport(ball, initial, transported, shaped);
		var repeated = CommitTransport(ball, initial, transported, shaped);

		double worldIntegral = 0, plateIntegral = 0;
		for (int cell = 0; cell < count; cell++)
		{
			double area = H3.CellAreaKm2(ball.CellIds[cell]);
			worldIntegral += (shaped.HeightM[cell] - baseHeight) * area;
			Program.True(edited.PlateElevations.TryGet(0, cell, CrustType.Continental, out float height),
				"抬升后保留板块的完整高程");
			Program.True(repeated.PlateElevations.TryGet(0, cell, CrustType.Continental, out float repeatHeight)
				&& repeatHeight == height, "重试同一轮时按固定次序累加高程");
			plateIntegral += (height - baseHeight) * area;
			Program.True(transported.SourceElevations.TryGet(0, cell, CrustType.Continental, out float before)
				&& before == baseHeight, "回写不能污染来源状态");
		}
		Program.Near(plateIntegral / worldIntegral, 1, 1e-6, "跨坐标回写保持面积加权抬升量");

		const float nextAngle = 1.1f;
		var moveAgain = new[] { new PlateRotation(0, axis, nextAngle) };
		var second = ReadTransport(RunTransport(ball, edited, moveAgain, 2));
		var secondState = CommitTransport(ball, edited, second);
		Program.True(ReferenceEquals(edited.PlateElevations, secondState.PlateElevations), "抬升只写入发生改动的一轮");
		Vector3 movedCenter = new(MathF.Cos(nextAngle), 0, -MathF.Sin(nextAngle));
		int peakCell = BallGeoIndex.CellAt(ball, movedCenter);
		int oldCenter = BallGeoIndex.CellAt(ball, Vector3.Right);
		Program.Near(second.Surface.HeightM[peakCell], baseHeight + uplift, 1, "山脊随板块转过第二轮");
		Program.Near(second.Surface.HeightM[oldCenter], baseHeight, 1, "世界原位置不残留抬升");
		Program.True(second.Surface.HeightM.Max() <= baseHeight + uplift + .001f, "连续搬运不会重复叠加抬升");
	}

	static SculptingSurfaceTransportModule.Result ReadTransport(SculptingData data)
		=> data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);

	static WorldSculptingState CommitTransport(Ball ball, WorldSculptingState source,
		SculptingSurfaceTransportModule.Result transport, SculptingSurfaceState edited = null)
	{
		var surface = edited ?? transport.Surface;
		var elevations = SculptingElevationWriteback.Apply(ball, transport.SourceElevations, transport.Surface,
			surface, transport.Plates, transport.ContactOwners, transport.SampledCells, transport.MissingLocalElevations,
			CancellationToken.None);
		return new WorldSculptingState(source.WorldId, ball.Res, ball.Radius, transport.Plates, surface, elevations);
	}

	internal static void ContinentalOverlapKeepsBothSides()
	{
		var ball = new Ball(2, 2f);
		var axis = new SpherePointD(0, 1, 0);
		int[] owners = ball.CellDirs.Select(direction => direction.X > 0 ? 0 : 1).ToArray();
		var plates = new[]
		{
			new SculptingPlateState(0, 3, new SpherePointD(1, 0, 0), RotationD.Identity),
			new SculptingPlateState(1, 3, new SpherePointD(-1, 0, 0), RotationD.Identity),
		};
		var heights = owners.Select(owner => owner == 0 ? 600f : 900f).ToArray();
		var surface = new SculptingSurfaceState(ball.Res, ball.CellIds, owners, heights,
			Enumerable.Repeat(CrustType.Continental, owners.Length), new int[owners.Length],
			new[] { new CrustProvince(0, CrustType.Continental, 0, owners[0], owners.Length) },
			plates.Select(plate => plate.Orientation), 0);
		var source = new WorldSculptingState(Guid.NewGuid(), ball.Res, ball.Radius, plates, surface);
		var data = Run(ball, source, new[] { new PlateRotation(0, axis, .25), new PlateRotation(1, axis, -.25) });
		var moved = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		var terrain = data.Read<SculptingTerrainModule.Result>(SculptingTerrainModule.ModuleId);
		int[] collisions = Enumerable.Range(0, owners.Length)
			.Where(cell => moved.Contacts[cell] == SculptingCrustContact.ContinentalCollision).ToArray();
		Program.True(collisions.Select(cell => moved.ContactOwners[cell]).Distinct().Count() == 2,
			"陆陆碰撞在重叠区域两侧各保留一块板，形成缝合线");
		Program.True(collisions.All(cell => moved.SampledCells[cell] >= 0)
			&& collisions.Any(cell => terrain.Surface.HeightM[cell] > moved.Surface.HeightM[cell])
			&& moved.Surface.CrustTypes.All(type => type == CrustType.Continental),
			"沿大陆缝合线抬升保留下来的地表");
	}

	internal static void DivergenceRespectsCrustAndElevation()
	{
		var ball = new Ball(2, 2f);
		var axis = new SpherePointD(0, 1, 0);
		int[] owners = ball.CellDirs.Select(direction => direction.X > 0 ? 0 : 1).ToArray();
		var plates = new[]
		{
			new SculptingPlateState(0, 3, new SpherePointD(1, 0, 0), RotationD.Identity),
			new SculptingPlateState(1, 3, new SpherePointD(-1, 0, 0), RotationD.Identity),
		};
		var plan = new[] { new PlateRotation(0, axis, .2), new PlateRotation(1, axis, -.2) };

		SculptingData Open(CrustType type, float height)
		{
			bool continental = type == CrustType.Continental;
			var provinces = continental ? new[] { new CrustProvince(0, type, 0, owners[0], owners.Length) }
				: Array.Empty<CrustProvince>();
			var surface = new SculptingSurfaceState(ball.Res, ball.CellIds, owners,
				Enumerable.Repeat(height, owners.Length), Enumerable.Repeat(type, owners.Length),
				Enumerable.Repeat(continental ? 0 : -1, owners.Length), provinces,
				plates.Select(plate => plate.Orientation), 0);
			var source = new WorldSculptingState(Guid.NewGuid(), ball.Res, ball.Radius, plates, surface);
			return Run(ball, source, plan);
		}

		var land = Open(CrustType.Continental, 600);
		var landTransport = land.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		var landTerrain = land.Read<SculptingTerrainModule.Result>(SculptingTerrainModule.ModuleId);
		var landRifts = Enumerable.Range(0, owners.Length)
			.Where(cell => landTransport.Contacts[cell] == SculptingCrustContact.ContinentalStretch).ToArray();
		Program.True(landRifts.Length > 0 && landTransport.Surface.CrustTypes.All(type => type == CrustType.Continental)
			&& landRifts.All(cell => landTerrain.Surface.HeightM[cell] > 0)
			&& landRifts.Any(cell => landTerrain.Surface.HeightM[cell] < landTransport.Surface.HeightM[cell]),
			"露出的陆壳沿真正的张开带下切成裂谷，保留陆壳与高程连续性");

		CheckNewSeafloor(Open(CrustType.Oceanic, -600));
		CheckNewSeafloor(Open(CrustType.Continental, -600));

		void CheckNewSeafloor(SculptingData data)
		{
			var moved = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
			var terrain = data.Read<SculptingTerrainModule.Result>(SculptingTerrainModule.ModuleId);
			var rifts = Enumerable.Range(0, owners.Length)
				.Where(cell => moved.Contacts[cell] == SculptingCrustContact.OceanSpreading).ToArray();
			Program.True(rifts.Length > 0 && rifts.All(cell => moved.Surface.CrustTypes[cell] == CrustType.Oceanic
				&& moved.Surface.CrustProvinceIds[cell] == -1 && moved.Surface.HeightM[cell] is >= -600 and <= -200
				&& terrain.Surface.HeightM[cell] < 0),
				"洋底扩张和宽海盆的新洋壳在两侧海床与配置深度间过渡");
			Program.True(rifts.Any(cell => moved.Surface.HeightM[cell] < -200), "新洋壳边缘仍接回已有海床");
		}
	}

	static SculptingData Run(Ball ball, WorldSculptingState source, PlateRotation[] rotations)
	{
		var data = RunTransport(ball, source, rotations, 1);
		var terrain = new SculptingTerrainModule(ball, new TerrainSpec { VerticalScale = 1 });
		data.BeginModule(SculptingTerrainModule.ModuleId, new[] { PlateMovementModule.ModuleId, SculptingSurfaceTransportModule.ModuleId });
		terrain.Execute(data, CancellationToken.None);
		data.CloseModule();
		return data;
	}

	static SculptingData RunTransport(Ball ball, WorldSculptingState source, PlateRotation[] rotations, int round)
	{
		var movement = new PlateMovementModule(rotations);
		var transport = new SculptingSurfaceTransportModule(ball, 200);
		var data = new SculptingData(source, new WorldSculptingRound(round));
		data.BeginModule(PlateMovementModule.ModuleId, Array.Empty<string>());
		movement.Execute(data, CancellationToken.None);
		data.CloseModule();
		data.BeginModule(SculptingSurfaceTransportModule.ModuleId, new[] { PlateMovementModule.ModuleId });
		transport.Execute(data, CancellationToken.None);
		data.CloseModule();
		return data;
	}
}
