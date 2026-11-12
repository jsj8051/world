using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.Data;
using World.Logic;

namespace World.Tests;

/// <summary>
/// 第 5 步演化逻辑测试：一次细分的二十面体球（80 个三角格，完整覆盖球面），验证零步、
/// 搬运、俯冲/扩张、账本闭合、确定性与控制状态。纯托管，不碰 H3 原生库。
/// </summary>
static class EvolutionLogicTests
{
	public static void ZeroStepConservesVolume()
	{
		EvolutionSimulation sim = BuildSim(0, 0, out var simFrame);
		double before = simFrame.Budget.InitialCrustVolume;
		simFrame = sim.CalculateToStep(simFrame, simFrame.Clock.StepIndex + 1);
		Program.Near(simFrame.Budget.CurrentCrustVolume, before, 1e-6 * before, "静止一步地壳体积");
		Program.True(Math.Abs(simFrame.Budget.CumulativeResidual) < 10, "静止一步累计残差应接近 0");
		Program.True(simFrame.Budget.TotalNewOceanic == 0, "静止一步不应产生新洋壳");
		Program.True(simFrame.Budget.TotalSubducted == 0, "静止一步不应发生俯冲");
	}

	public static void OceanAgeAdvancesWithTime()
	{
		EvolutionSimulation sim = BuildSim(0, 0, out var simFrame);
		int oceanCell = FirstOceanCell(simFrame);
		double? before = simFrame.Projection.OceanAgeMa[oceanCell];
		simFrame = sim.CalculateToStep(simFrame, simFrame.Clock.StepIndex + 1);
		double? after = simFrame.Projection.OceanAgeMa[oceanCell];
		Program.True(before != null && after != null, "洋壳格应有海底年龄");
		Program.Near(after!.Value - before!.Value, 5, 1e-6, "海底年龄应随 5 Ma 步长增加");
	}

	public static void PlatesRotateAndMixMaterial()
	{
		EvolutionSimulation sim = BuildSim(-1.5, 1.5, out var simFrame);
		double before = simFrame.Budget.InitialCrustVolume;
		simFrame = sim.CalculateToStep(simFrame, simFrame.Clock.StepIndex + 1);
		Program.True(simFrame.Material.OccupiedSlotCount > 80, "跨格搬运后应有混合格");
		Program.True(simFrame.Material.OccupiedSlotCount <= sim.Grid.CellCount * simFrame.Motions.Count * 2, "材料槽数量有固定上限");
		Program.True(Math.Abs(simFrame.Budget.CurrentCrustVolume - before) < 0.01 * before, "搬运后体积近似守恒");
		Program.True(simFrame.Motions[0].Orientation.W < 1, "板块姿态应随步进改变");
		Program.True(simFrame.Motions[0].Orientation.W < 1, "提交帧应包含本步姿态");
	}

	public static void CommonRotationDoesNotCreateTectonics()
	{
		EvolutionSimulation sim = BuildSim(1.5, 1.5, out var simFrame);
		double original = simFrame.Material.VolumeKm3;
		simFrame = sim.CalculateToStep(simFrame, 10);
		Program.Near(simFrame.Material.VolumeKm3, original, original * 1e-12, "刚体共转体积");
		Program.True(simFrame.Projection.Boundary.MotionKinds.All(k => k == EvolutionMotionKind.Inactive),
			"同速共转的相对法向和切向速度均为零");
		Program.True(simFrame.Budget.TotalSubducted == 0 && simFrame.Budget.TotalNewOceanic == 0
			&& simFrame.Budget.TotalVolcanic == 0, "刚体共转不能触发构造过程");
	}

	public static void BoundaryFluxFollowsNormalMotion()
	{
		Check(150, 150, 0, 0);
		Check(150, 50, 0, 10);
		Check(-50, -150, 10, 0);
		Check(150, -50, 5, 15);
		Check(-150, 50, -15, -5);

		static void Check(double rateA, double rateB, double expectedA, double expectedB)
		{
			var (areaA, areaB) = CrustProcesses.SplitBoundaryArea(rateA, rateB, 0.1);
			Program.Near(areaA, expectedA, 1e-12, "A 侧构造面积");
			Program.Near(areaB, expectedB, 1e-12, "B 侧构造面积");
			Program.Near(areaA + areaB, (rateA - rateB) * 0.1, 1e-12, "每条边的相对法向通量只结算一次");
		}
	}

	public static void ViscousContactBalancesForces()
	{
		Check(3, 3, 1, 3, 3, 3);
		Check(3, 1, 1, 7d / 3, 5d / 3, 2);
		Check(3, 1, 0.25, 29d / 15, 19d / 15, 1.4);
		var outward = new ViscousContactSolver.Velocity(1, 0, 0);
		var vertical = new ViscousContactSolver.Velocity(0, 0, 1);
		var separating = ViscousContactSolver.Solve(
			new[] { new ViscousContactSolver.Node(vertical, 1, outward * -1), new ViscousContactSolver.Node(vertical, 1, outward) },
			new[] { new ViscousContactSolver.Link(0, 1, 1, outward, 1) });
		Program.Near(separating[0].X, -1, 1e-8, "两侧受向外驱动力时接触允许张开");
		Program.Near(separating[1].X, 1, 1e-8, "接触解除后不产生拉力");

		static void Check(double farA, double farB, double resistanceA, double expectedA, double expectedB, double expectedContact)
		{
			var radial = new ViscousContactSolver.Velocity(0, 0, 1);
			var normal = new ViscousContactSolver.Velocity(1, 0, 0);
			double halfA = 2 * resistanceA;
			const double halfB = 2;
			var nodes = new[] { new ViscousContactSolver.Node(radial, resistanceA), new ViscousContactSolver.Node(radial, 1) };
			var link = new ViscousContactSolver.Link(0, 1, halfA * halfB / (halfA + halfB), normal, farA - farB);
			var velocity = ViscousContactSolver.Solve(nodes, new[] { link });
			double a = farA + velocity[0].X, b = farB + velocity[1].X;
			Program.Near(a, expectedA, 1e-8, "A 侧格心速度");
			Program.Near(b, expectedB, 1e-8, "B 侧格心速度");
			double contact = (halfA * a + halfB * b) / (halfA + halfB);
			Program.Near(contact, expectedContact, 1e-8, "界面速度");
			Program.Near(resistanceA * (farA - a), b - farB, 1e-8, "碰撞两侧接触力平衡");
		}
	}

	public static void CollisionOnlyUsesSolvedCompression()
	{
		var world = World(1.5, -1.5, out var grid, out var owners, out _, out _, out var plates);
		EvolutionSimulation.Create(world, grid, owners, AllContinental(owners.Length), new float[owners.Length], plates,
			out var frame);
		const double deltaMa = 0.01;
		var projection = frame.Projection;
		var solved = ContinentalCollisionSolver.Solve(grid, frame.Material, projection, frame.Motions, world.Evolution, default);
		foreach (var face in solved.Faces.Values.Where(face => face.PlateB >= 0))
			Program.True(face.RateA <= face.RateB + 1e-10, "碰撞界面两侧不能继续相互穿透");
		var (cell, motion) = solved.Cells.MaxBy(pair => pair.Value.ConvergenceKm2PerMa);
		double capacity = grid.AreasKm2[cell];
		double rawArea = capacity * 1.1;
		double collisionArea = motion.ConvergenceKm2PerMa * deltaMa;
		Program.True(collisionArea > 0 && collisionArea < rawArea - capacity, "测试须含额外的非构造覆盖误差");
		var row = new CrustRowBuffer(4);
		int first = (int)CrustType.Continental;
		int second = 2 + first;
		row.Set(first, CrustSlot.Create(rawArea / 2, 35, 0, null, 0));
		row.Set(second, CrustSlot.Create(rawArea / 2, 35, 0, null, 0));
		var processes = new CrustProcesses(grid, world.Evolution, projection, frame.Motions, deltaMa, deltaMa,
			new double[grid.CellCount], solved);
		processes.SettleCell(cell, row);
		var a = row.Slots[first];
		var b = row.Slots[second];
		Program.Near(a.CollisionVolume + b.CollisionVolume, 35 * collisionArea, capacity * 1e-10,
			"整格覆盖误差不能计入碰撞增厚");
		Program.Near(a.Area + b.Area, capacity, capacity * 1e-12, "覆盖闭合");
		Program.Near(a.Volume + b.Volume, 35 * rawArea, capacity * 1e-12, "缩短与覆盖修正均保存体积");
	}

	public static void MultiSubstepTransportKeepsSourceFrame()
	{
		var sim = BuildSim(1.5, 1.5, out var simFrame);
		EvolutionCrust source = simFrame.Material;
		var advection = new CrustAdvection(sim.Grid, simFrame.Motions, 100);
		CrustRows slots = advection.Move(source);
		var moved = new EvolutionCrust(sim.Grid.AreasKm2, source.PlateCount, slots, 100, 1e-8);

		Program.True(moved.OccupiedSlotCount > sim.Grid.CellCount, "跨多格共转后应有混合材料");
		Program.True(source.OccupiedSlotCount == sim.Grid.CellCount, "工作缓冲不能修改来源帧");
		Program.Near(moved.VolumeKm3, source.VolumeKm3, source.VolumeKm3 * 1e-12, "多子步输运体积");
	}

	public static void TransportSupportsPlateIdsAboveFirstMaskWord()
	{
		EvolutionSimulation sim = BuildSim(1.5, 1.5, out var simFrame);
		const int plateCount = 65;
		var motions = new PlateMotion[plateCount];
		for (int i = 0; i < plateCount; i++)
			motions[i] = new PlateMotion(i, new SphereVectorD(0, 0, 0), RotationD.Identity);
		motions[0] = simFrame.Motions[0];
		motions[64] = new PlateMotion(64, simFrame.Motions[1].OmegaRadPerMa, RotationD.Identity);

		var slots = new CrustSlot[sim.Grid.CellCount * plateCount * 2];
		for (int cell = 0; cell < sim.Grid.CellCount; cell++)
			foreach (CrustType type in new[] { CrustType.Oceanic, CrustType.Continental })
			{
				slots[EvolutionCrust.Index(cell, 0, type, plateCount)] = simFrame.Material.Get(cell, 0, type);
				slots[EvolutionCrust.Index(cell, 64, type, plateCount)] = simFrame.Material.Get(cell, 1, type);
			}
		var expanded = new EvolutionCrust(sim.Grid.AreasKm2, plateCount, slots, 0, 1e-8);
		Program.True(expanded.Rows.AllocatedEntries < slots.LongLength / 2,
			"空槽数量不得决定材料存储容量");
		Program.True(expanded.Get(0, 63, CrustType.Oceanic).Area == 0,
			"读取缺席的材料槽应返回空材料");
		CrustRows expected = new CrustAdvection(sim.Grid, simFrame.Motions, 5).Move(simFrame.Material);
		CrustRows actual = new CrustAdvection(sim.Grid, motions, 5).Move(expanded);
		for (int cell = 0; cell < sim.Grid.CellCount; cell++)
			foreach (CrustType type in new[] { CrustType.Oceanic, CrustType.Continental })
			{
				CrustSlot lower = expected.Get(cell, (int)type);
				CrustSlot higher = expected.Get(cell, 2 + (int)type);
				Program.Near(actual.Get(cell, (int)type).Area, lower.Area, 0, "低板块号输运");
				Program.Near(actual.Get(cell, 128 + (int)type).Volume, higher.Volume, 0, "高板块号输运");
			}
	}

	public static void TransportRoundsUnderflowingSlotAsOneContribution()
	{
		var source = CrustSlot.Create(double.Epsilon, 35, 500, null, 0);
		var destination = new CrustSlot();
		destination.Add(source, 0.25);
		destination.Validate(false, 0);
		Program.True(destination.Area == 0 && destination.Volume == 0,
			"面积下溢时不能留下没有占地的极小体积");
	}

	public static void LimitedTransportKeepsContinentalTracersBoundedAndConservative()
	{
		var (grid, _) = IcoGrid();
		var motions = new[] { new PlateMotion(0, new SphereVectorD(0.02, -0.03, 0.06), RotationD.Identity) };
		const double stepMa = 1;
		int key = (int)CrustType.Continental;
		var sourceRows = CrustRows.Build(grid.CellCount, 2, null, (cell, row) =>
		{
			SpherePointD center = grid.Centers[cell];
			var slot = CrustSlot.Create(grid.AreasKm2[cell], 34 + 2 * center.Y, 400 + 150 * center.Z, null, 0);
			slot.Volume += 3 * center.X * slot.Area;
			slot.CollisionVolume = Math.Max(0, 2 * center.X) * slot.Area;
			row.Set(key, slot);
		});
		var material = new EvolutionCrust(grid.AreasKm2, 1, sourceRows, 0, 1e-8);
		var moved = new CrustAdvection(grid, motions, stepMa).Move(material);
		var fluxes = new CrustTransportFluxes(grid, motions, stepMa);
		Program.True(fluxes.StepCount == 1, "局部限幅测试使用一个输运子步");
		Check(slot => slot.ReferenceThicknessArea, "参考体积");
		Check(slot => slot.Volume - slot.ReferenceThicknessArea, "厚度异常体积", expectCorrection: true);
		Check(slot => slot.CollisionVolume, "碰撞增厚体积");
		Check(slot => slot.ReferenceHeightArea, "面积加权参考高程");

		void Check(Func<CrustSlot, double> amount, string name, bool expectCorrection = false)
		{
			double initialTotal = 0, finalTotal = 0;
			bool corrected = false;
			for (int cell = 0; cell < grid.CellCount; cell++)
			{
				var old = sourceRows.Get(cell, key);
				var next = moved.Get(cell, key);
				initialTotal += amount(old);
				finalTotal += amount(next);
				Program.Near(next.Area, old.Area, old.Area * 1e-12, "限幅不改变跨格面积");
				double min = amount(old) / old.Area, max = min;
				foreach (int neighbor in grid.Neighbors[cell])
				{
					var other = sourceRows.Get(neighbor, key);
					double mean = amount(other) / other.Area;
					min = Math.Min(min, mean);
					max = Math.Max(max, mean);
				}
				double actual = amount(next) / next.Area;
				Program.True(actual >= min - 1e-10 && actual <= max + 1e-10, $"{name} 不应产生局部过冲");
				double firstOrder = amount(old) * fluxes.RetainedFraction(cell, 0);
				foreach (int face in fluxes.FacesOfCell(cell))
				{
					var edge = grid.Faces[face];
					bool isA = cell == edge.A;
					double incoming = fluxes.SignedFraction(face, 0) * (isA ? -1 : 1);
					if (incoming > 0) firstOrder += amount(sourceRows.Get(isA ? edge.B : edge.A, key)) * incoming;
				}
				corrected |= Math.Abs(firstOrder / next.Area - actual) > 1e-7;
			}
			Program.Near(finalTotal, initialTotal, grid.AreasKm2.Sum() * 1e-10, $"{name} 守恒");
			Program.True(!expectCorrection || corrected, $"{name} 应有实际通量修正");
		}
	}

	public static void ContinentalRiftThinsBeforeMakingOcean()
	{
		WorldSpec world = World(-1.5, 1.5, out EvolutionGrid grid, out int[] owners,
			out _, out _, out Plate[] plates);
		CrustField land = AllContinental(owners.Length);
		var heights = new float[owners.Length];
		var sim = EvolutionSimulation.Create(world, grid, owners, land, heights, plates, out var simFrame);
		simFrame = sim.CalculateToStep(simFrame, simFrame.Clock.StepIndex + 1);
		Program.True(simFrame.Budget.TotalNewOceanic == 0, "未破裂的陆壳不能补洋壳");
		Program.True(Enumerable.Range(0, owners.Length).Any(cell =>
			simFrame.Material.ExtensionStrain(cell, owners[cell]) > 0 &&
			simFrame.Material.ThicknessKm(cell, owners[cell], CrustType.Continental) < 35),
			"陆壳伸展须记录应变并减薄");
		CheckCellCenterRelief(sim.Grid, simFrame);

		var fineGrid = new EvolutionGrid(grid.Cells, grid.Neighbors, grid.Centers, 150, grid.Resolution);
		var fineSim = EvolutionSimulation.Create(world, fineGrid, owners, land, heights, plates, out var fineZero);
		var rift = fineSim.CalculateToStep(fineZero, 1).Projection;
		Program.True(Enumerable.Range(0, fineGrid.CellCount).Any(cell => rift.HeightM[cell] < rift.MaterialHeightM[cell]),
			"陆陆张裂应在最终格心高程生成裂谷沉降");

		world.Evolution.RiftBreakupStrain = 0.0001;
		var earlierBreakup = EvolutionSimulation.Create(world, grid, owners, land, heights, plates, out var earlierBreakupFrame);
		earlierBreakupFrame = earlierBreakup.CalculateToStep(earlierBreakupFrame, 2);
		Program.True(earlierBreakupFrame.Budget.TotalNewOceanic > 0, "跨过破裂阈值才生成新洋壳");
		Program.Near(sim.Definition.CreateSpecCopy().Evolution.RiftBreakupStrain, 1, 0, "活动演化参数冻结");
	}

	public static void ContinentalCollisionThickensCrust()
	{
		WorldSpec world = World(1.5, -1.5, out EvolutionGrid grid, out int[] owners,
			out _, out _, out Plate[] plates);
		var sim = EvolutionSimulation.Create(world, grid, owners, AllContinental(owners.Length),
			new float[owners.Length], plates, out var simFrame);
		simFrame = sim.CalculateToStep(simFrame, simFrame.Clock.StepIndex + 1);
		Program.True(simFrame.Projection.Boundary.ConvergenceKinds.Any(kind => kind == ConvergenceKind.Collision),
			"陆陆汇聚应分类为碰撞");
		Program.True(simFrame.Budget.TotalSubducted == 0, "陆陆碰撞体积留在地壳");
		Program.True(Enumerable.Range(0, owners.Length).Any(cell =>
			simFrame.Material.ThicknessKm(cell, owners[cell], CrustType.Continental) > 35.1),
			"汇聚陆壳在固定格内增厚");
		Program.True(simFrame.Projection.HeightM.Max() > 20, "增厚产生均衡抬升");
	}

	public static void CollisionDeformsContinentalCellsAwayFromTheContact()
	{
		var world = World(1.5, -1.5, out var coarse, out var owners, out _, out _, out var plates);
		world.Evolution.RiftReliefScaleM = 0;
		var grid = new EvolutionGrid(coarse.Cells, coarse.Neighbors, coarse.Centers, 150, coarse.Resolution);
		var sim = EvolutionSimulation.Create(world, grid, owners, AllContinental(owners.Length),
			new float[owners.Length], plates, out var zero);
		var frame = sim.CalculateToStep(zero, 1);
		var projection = frame.Projection;
		var contacts = zero.Projection.PlateBoundaries.Edges
			.Where((_, i) => zero.Projection.Boundary.ConvergenceKinds[i] == ConvergenceKind.Collision)
			.SelectMany(edge => new[] { edge.CellA, edge.CellB }).ToHashSet();
		bool raisedInside = false;
		for (int cell = 0; cell < grid.CellCount; cell++)
		{
			Program.Near(projection.HeightM[cell], projection.MaterialHeightM[cell], 0, "碰撞高程直接来自材料增厚");
			raisedInside |= !contacts.Contains(cell) && frame.Material.Get(cell, owners[cell], CrustType.Continental).CollisionVolume > 0;
		}
		Program.True(raisedInside, "碰撞缩短应向板内传播并增厚陆壳");
		CheckCellCenterRelief(grid, frame);
	}

	public static void SubductionOverflowKeepsContinentalArea()
	{
		var world = World(1.5, -1.5, out var grid, out var owners, out var crust, out var heights, out var plates);
		EvolutionSimulation.Create(world, grid, owners, crust, heights, plates, out var frame);
		var projection = frame.Projection;
		int cell = Enumerable.Range(0, grid.CellCount).First(i =>
		{
			int edge = Dominant(i);
			return edge >= 0 && projection.Boundary.UnderthrustingPlateIds[edge] >= 0
				&& grid.Neighbors[i].Any(j => Dominant(j) >= 0 &&
					projection.Boundary.UnderthrustingPlateIds[Dominant(j)] == projection.Boundary.UnderthrustingPlateIds[edge]);
		});
		int under = projection.Boundary.UnderthrustingPlateIds[Dominant(cell)];
		int over = 1 - under;
		int donor = grid.Neighbors[cell].First(j => Dominant(j) >= 0
			&& projection.Boundary.UnderthrustingPlateIds[Dominant(j)] == under);
		double capacity = grid.AreasKm2[cell];
		world.Evolution.ArcVolcanicRateKmPerMa = 0;

		foreach (bool canBorrowSlab in new[] { true, false })
		{
			var process = new CrustProcesses(grid, world.Evolution, projection, frame.Motions, 1, 100, new double[grid.CellCount]);
			var raw = CrustRows.Build(grid.CellCount, 4, null, (i, row) =>
			{
				if (i == cell)
				{
					row.Set(2 * over + (int)CrustType.Continental, CrustSlot.Create(1.1 * capacity, 35, 500, null, 0));
					row.Set(2 * under + (int)CrustType.Oceanic, CrustSlot.Create(0.05 * capacity, 7, -3000, 0, -1));
					process.SettleCell(i, row);
				}
				else
				{
					int plate = i == donor && canBorrowSlab ? under : over;
					row.Set(2 * plate + (int)CrustType.Oceanic, CrustSlot.Create(grid.AreasKm2[i], 7, -3000, 0, -1));
				}
			});
			var settled = process.ResolveOverflow(raw, default);
			var flows = process.CollectFlows();
			var material = new EvolutionCrust(grid.AreasKm2, 2, settled, 1, 1e-8);
			double land = Enumerable.Range(0, grid.CellCount).Sum(i => material.AreaKm2(i, over, CrustType.Continental));
			Program.Near(land, 1.1 * capacity, capacity * 1e-10, "俯冲余量不应扣减陆壳占地");
			double expectedSubducted = (canBorrowSlab ? 0.15 : 0.05) * capacity * 7;
			Program.Near(flows.Subducted, expectedSubducted, capacity * 1e-10, "只把实际移除的下盘洋壳记入俯冲");
			double totalBefore = 1.1 * capacity * 35 + (grid.AreasKm2.Sum() - 0.95 * capacity) * 7;
			Program.Near(material.VolumeKm3 + flows.Subducted, totalBefore, totalBefore * 1e-10,
				"跨格结算应保存原有材料体积与俯冲入池量之和");
			Program.True(material.AreaKm2(cell, over, CrustType.Continental) < land,
				"本格放不下的陆壳应重新分配到邻格");
		}

		var onlyLower = new CrustRowBuffer(4);
		onlyLower.Set(2 * under + (int)CrustType.Oceanic, CrustSlot.Create(1.1 * capacity, 7, -3000, 0, -1));
		var noContact = new CrustProcesses(grid, world.Evolution, projection, frame.Motions, 1, 100,
			new double[grid.CellCount]);
		noContact.SettleCell(cell, onlyLower);
		Program.Near(noContact.CollectFlows().Subducted, 0, 0, "只有下盘材料的覆盖误差不能触发俯冲");

		int Dominant(int i) => projection.PlateBoundaries.EdgesOfCell(i).ToArray()
			.Where(edge => projection.Boundary.MotionKinds[edge] is EvolutionMotionKind.Convergent or EvolutionMotionKind.Divergent)
			.OrderByDescending(edge => Math.Abs(projection.Boundary.NormalSpeedKmPerMa[edge]))
			.DefaultIfEmpty(-1).First();
	}

	public static void MixedCoastKeepsItsEmergentArea()
	{
		var coast = CrustProjectionBuilder.ClassifySurface(30, 150, 70, -3000, 100);
		Program.Near(coast.EmergentFraction, 0.3, 1e-7, "深海不应抹掉同格的露出陆壳");
		Program.True(coast.LandHeightM == 150 && coast.WaterHeightM == -3000, "海岸两侧各用自己的海拔");
		var island = CrustProjectionBuilder.ClassifySurface(0, -500, 100, 150, 100);
		Program.True(island.EmergentFraction == 1, "高于海面的洋壳也计入陆地");
	}

	public static void MixedCoverageCorrectionKeepsContinentalArea()
	{
		var sim = BuildSim(0, 0, out var frame);
		var grid = sim.Grid;
		var spec = sim.Definition.CreateSpecCopy().Evolution;
		double capacity = grid.AreasKm2[0];
		foreach (double ocean in new[] { 0.4, 0.6 })
		{
			var process = new CrustProcesses(grid, spec, frame.Projection, frame.Motions, 1, 1, new double[grid.CellCount]);
			var row = new CrustRowBuffer(4);
			int landKey = (int)CrustType.Continental;
			int oceanKey = (int)CrustType.Oceanic;
			row.Set(landKey, CrustSlot.Create(0.5 * capacity, 35, 500, null, 0));
			row.Set(oceanKey, CrustSlot.Create(ocean * capacity, 7, -3000, 0, -1));
			process.SettleCell(0, row);
			Program.Near(row.Slots[landKey].Area, 0.5 * capacity, capacity * 1e-10,
				"普通覆盖修正不应让混合格的陆壳占地漂移");
			Program.Near(row.Slots[oceanKey].Area, 0.5 * capacity, capacity * 1e-10, "格内覆盖应恰好闭合");
			Program.Near(row.Slots[oceanKey].Volume, ocean * capacity * 7, capacity * 1e-10,
				"覆盖修正不应吞掉洋壳体积");
		}
	}

	public static void InsufficientMantleKeepsPreviousFrame()
	{
		WorldSpec world = World(-1.5, 1.5, out EvolutionGrid grid, out int[] owners,
			out CrustField crust, out float[] heights, out Plate[] plates);
		world.Evolution.InitialMantleVolumeKm3 = 0;
		world.Evolution.RidgeVolcanicRateKmPerMa = 1_000;
		var sim = EvolutionSimulation.Create(world, grid, owners, crust, heights, plates, out var initial);
		try
		{
			sim.CalculateToStep(initial, 1);
			throw new Exception("释放需求超额时应失败");
		}
		catch (EvolutionStepException failure)
		{
			Program.True(failure.Code == EvolutionProblemCode.InsufficientMantleMaterial,
				"应保留库存不足的结构化原因");
			Program.True(initial.Clock.StepIndex == 0 && initial.Budget.TotalNewOceanic == 0,
				"失败不能修改来源帧或累计流量");
		}
	}

	public static void ConvergenceSubductsOceanicCrust()
	{
		WorldSpec world = World(1.5, -1.5, out EvolutionGrid grid, out int[] owners,
			out CrustField crust, out float[] heights, out Plate[] plates);
		// 测试球只有 80 格，放宽海沟剖面使下沉覆盖至少一个格心。
		world.Evolution.TrenchReachKm = 2000;
		// 给洋壳表面同样的露出高度，检验下盘能跌破海平面。
		Array.Fill(heights, 500f);
		var sim = EvolutionSimulation.Create(world, grid, owners, crust, heights, plates, out var simFrame);
		double mantleBefore = simFrame.Mantle.StoredVolumeKm3;
		simFrame = sim.CalculateToStep(simFrame, simFrame.Clock.StepIndex + 1);
		Program.True(simFrame.Budget.TotalSubducted > 1, $"汇聚应发生俯冲，实得 {simFrame.Budget.TotalSubducted:G6} km³");
		Program.True(simFrame.Budget.TotalSubducted > simFrame.Budget.TotalVolcanic,
			"俯冲入池量应大于火山释放量");
		Program.True(simFrame.Budget.TotalVolcanic > 0, "俯冲弧应产生火山增生");
		Program.True(simFrame.Mantle.StoredVolumeKm3 >= 0, "地幔库存不得为负");
		Program.True(mantleBefore > 0 && simFrame.Mantle.StoredVolumeKm3 != mantleBefore, "库存应随物质转移变化");

		Program.True(simFrame.Projection.PlateBoundaries.Edges.Select((edge, i) => (edge, i)).Any(pair =>
		{
			int under = simFrame.Projection.Boundary.UnderthrustingPlateIds[pair.i];
			if (under < 0) return false;
			EvolutionFace face = sim.Grid.Faces.First(f => f.A == pair.edge.CellA && f.B == pair.edge.CellB);
			SpherePointD mid = Unit(face.Start.X + face.End.X, face.Start.Y + face.End.Y, face.Start.Z + face.End.Z);
			int lowerCell = pair.edge.PlateA == under ? face.A : face.B;
			return simFrame.Projection.Relief.At(lowerCell, mid, under) < -1;
		}), "俯冲带应在下盘一侧形成海沟");
		Program.True(!simFrame.Projection.MaterialHeightM.SequenceEqual(simFrame.Projection.HeightM),
			"海沟剖面应压低至少一个格心");
		Program.True(simFrame.Projection.PlateBoundaries.Edges.Select((edge, i) => (edge, i)).Any(pair =>
		{
			int under = simFrame.Projection.Boundary.UnderthrustingPlateIds[pair.i];
			int cell = pair.edge.PlateA == under ? pair.edge.CellA : pair.edge.CellB;
			return under >= 0 && simFrame.Projection.Crust.Type[cell] == CrustType.Oceanic
				&& simFrame.Projection.MaterialHeightM[cell] > 0 && simFrame.Projection.HeightM[cell] < 0;
		}), "俯冲下盘原本露出水面的洋壳应可没入海平面");
		CheckCellCenterRelief(sim.Grid, simFrame);
	}

	static void CheckCellCenterRelief(EvolutionGrid grid, EvolutionFrame simFrame)
	{
		CrustProjection projection = simFrame.Projection;
		for (int cell = 0; cell < grid.CellCount; cell++)
		{
			float sampled = projection.MaterialHeightM[cell] + projection.Relief.At(
				cell, grid.Centers[cell], projection.PlateOfCell[cell]);
			Program.True(BitConverter.SingleToInt32Bits(projection.HeightM[cell]) == BitConverter.SingleToInt32Bits(sampled),
				$"格 {cell} 的批量剖面与逐点剖面采样不一致");
		}
	}

	public static void DivergenceCreatesNewOceanicCrust()
	{
		WorldSpec world = World(-1.5, 1.5, out EvolutionGrid grid, out int[] owners,
			out CrustField crust, out float[] heights, out Plate[] plates);
		// 放宽 80 格测试球的洋脊和海沟，让格心实际采样两种剖面。
		world.Evolution.OceanRidgeReachKm = 2800;
		world.Evolution.TrenchReachKm = 2000;
		for (int cell = 0; cell < heights.Length; cell++)
			if (crust.Type[cell] == CrustType.Oceanic) heights[cell] = -7500;
		var sim = EvolutionSimulation.Create(world, grid, owners, crust, heights, plates, out var simFrame);
		double mantleBefore = simFrame.Mantle.StoredVolumeKm3;
		simFrame = sim.CalculateToStep(simFrame, simFrame.Clock.StepIndex + 1);
		Program.True(simFrame.Budget.TotalNewOceanic > 1, $"张裂应生成新洋壳，实得 {simFrame.Budget.TotalNewOceanic:G6} km³");
		Program.Near(simFrame.Mantle.StoredVolumeKm3, mantleBefore + simFrame.Budget.StepSubducted
			- simFrame.Budget.StepNewOceanic - simFrame.Budget.StepVolcanic, 1e-6, "新洋壳应从地幔池释放");
		Program.True(simFrame.Projection.OceanAgeMa.Any(age => age < 85),
			"新洋壳应拉低格内的体积加权年龄");
		CrustProjection projection = simFrame.Projection;
		var ridgeCells = Enumerable.Range(0, grid.CellCount)
			.Where(cell => projection.Crust.Type[cell] == CrustType.Oceanic)
			.Where(cell => DominantNormalSpeed(projection, cell) > 0)
			.ToArray();
		Program.True(ridgeCells.Any(cell => projection.HeightM[cell] > projection.MaterialHeightM[cell]),
			"洋壳张裂应在轴部形成正的洋脊地形");
		Program.True(ridgeCells.All(cell => projection.HeightM[cell] >= projection.MaterialHeightM[cell]),
			"附近的俯冲海沟不能压低以张裂为主的洋壳边界格");
		CheckCellCenterRelief(sim.Grid, simFrame);

		static double DominantNormalSpeed(CrustProjection projection, int cell)
		{
			double dominant = 0;
			foreach (int i in projection.PlateBoundaries.EdgesOfCell(cell))
			{
				double speed = projection.Boundary.NormalSpeedKmPerMa[i];
				if (projection.Boundary.MotionKinds[i] is EvolutionMotionKind.Convergent or EvolutionMotionKind.Divergent
					&& Math.Abs(speed) > Math.Abs(dominant))
					dominant = speed;
			}
			return dominant;
		}
	}

	public static void BoundaryFieldClassifiesSubduction()
	{
		EvolutionSimulation sim = BuildSim(1.5, -1.5, out var simFrame);
		simFrame = sim.CalculateToStep(simFrame, simFrame.Clock.StepIndex + 1);
		bool anySubduction = simFrame.Projection.Boundary.ConvergenceKinds
			.Any(k => k == ConvergenceKind.Subduction);
		Program.True(anySubduction, "汇聚边界应被分类为俯冲");
	}

	public static void TrenchOnlyLowersTheSubductingOceanicSlot()
	{
		var sim = PairedWorld(continentalPlate: true, out var zero);
		var grid = sim.Grid;
		var spec = zero.Definition.CreateSpecCopy().Evolution;
		spec.TrenchReachKm = 350;
		spec.CoolingDepthMPerSqrtMa = 0;
		spec.RiftReliefScaleM = 0;
		var initial = zero.Projection;
		int edgeIndex = Enumerable.Range(0, initial.PlateBoundaries.Edges.Length)
			.Where(i => initial.Boundary.UnderthrustingPlateIds[i] == 1)
			.MinBy(i => initial.Boundary.NormalSpeedKmPerMa[i]);
		var edge = initial.PlateBoundaries.Edges[edgeIndex];
		int mixed = edge.PlateA == 1 ? edge.CellA : edge.CellB;
		int upper = edge.PlateA == 0 ? edge.CellA : edge.CellB;
		int pureLand = grid.Neighbors[mixed].First(i => initial.PlateOfCell[i] == 1);
		var rows = CrustRows.Build(grid.CellCount, 4, zero.Material.Rows, (cell, row) =>
		{
			double area = grid.AreasKm2[cell];
			if (cell == mixed)
			{
				row.Set((int)CrustType.Continental, CrustSlot.Create(0.30 * area, 35, 1800, null, 0));
				row.Set((int)CrustType.Oceanic, CrustSlot.Create(0.10 * area, 7, 650, 0, -1));
				row.Set(2 + (int)CrustType.Continental, CrustSlot.Create(0.29 * area, 35, 1800, null, 0));
				row.Set(2 + (int)CrustType.Oceanic, CrustSlot.Create(0.31 * area, 7, 700, 0, -1));
			}
			else if (cell == upper)
			{
				row.Set((int)CrustType.Continental, CrustSlot.Create(0.75 * area, 35, 1800, null, 0));
				row.Set(2 + (int)CrustType.Oceanic, CrustSlot.Create(0.25 * area, 7, 700, 0, -1));
			}
			else if (cell == pureLand)
				row.Set(2 + (int)CrustType.Continental, CrustSlot.Create(area, 35, 1800, null, 0));
			else
				foreach (ref readonly var entry in zero.Material.Rows.Row(cell)) row.Set(entry.Key, entry.Slot);
		});
		var material = new EvolutionCrust(grid.AreasKm2, 2, rows, 30, spec.CoverageRelativeTolerance);
		var p = CrustProjectionBuilder.Build(grid, new EvolutionReliefGeometry(grid, spec), spec, 1, 30, material,
			zero.Motions, sim.Plates.ToArray(), initial.Crust.Provinces, new double[grid.CellCount], 0);
		Program.True(p.SurfaceCrustType[mixed] == CrustType.Oceanic && p.OceanicCoverage[mixed] < 0.5,
			"混合格复现主导板块以洋壳为主、整格以陆壳为主的情况");
		Program.Near(p.SurfaceCells[mixed].EmergentFraction, 0.69, 1e-6, "陆壳和上盘洋壳保留露出面积");
		Program.Near(p.SurfaceCells[mixed].LandHeightM, (0.59 * 1800 + 0.10 * 650) / 0.69, 1e-3,
			"陆壳和上盘洋壳海拔各自保留");
		Program.True(p.SurfaceCells[mixed].WaterHeightM < 0, "同格的下盘洋壳仍能沉入海平面");
		Program.Near(p.HeightM[mixed] - p.MaterialHeightM[mixed],
			0.31 * (p.SurfaceCells[mixed].WaterHeightM - 700), 1e-3, "格心只按下盘洋壳占地计入海沟");
		Program.Near(p.HeightM[pureLand], 1800, 0, "下盘板块邻近的纯陆壳格保持材料高程");
		Program.Near(p.SurfaceCells[upper].LandHeightM, 1800, 0, "上盘格的陆壳保持材料高程");
		Program.True(p.SurfaceCells[upper].WaterHeightM < 0, "混在上盘格里的下盘洋壳仍受海沟沉降");
		Program.Near(p.Relief.At(mixed, grid.Centers[mixed], 1, fine: true, sampledType: CrustType.Continental),
			0, 0, "逐点采样陆壳时不加入海沟");
	}

	public static void SubductionFormsAnUpperArc(bool continentalUpper)
	{
		var sim = PairedWorld(continentalUpper, out var zero);
		var frame = sim.CalculateToStep(zero, 1);
		var p = frame.Projection;
		int[] edges = Enumerable.Range(0, p.PlateBoundaries.Edges.Length)
			.Where(i => p.Boundary.MotionKinds[i] == EvolutionMotionKind.Convergent).ToArray();
		Program.True(edges.Length > 0 && edges.All(i => p.Boundary.ConvergenceKinds[i] == ConvergenceKind.Subduction
			&& p.Boundary.UnderthrustingPlateIds[i] == 1), "汇聚边应让洋壳板块 1 下沉");
		Program.True(frame.Budget.StepSubducted > 0 && frame.Budget.StepVolcanic > 0,
			"俯冲应消耗洋壳并向上盘增生火山体积");
		double integral = 0, total = 0;
		bool arc = false, trench = false;
		for (int cell = 0; cell < sim.Grid.CellCount; cell++)
		{
			int plate = p.SurfacePlateOfCell[cell];
			double area = sim.Grid.AreasKm2[cell];
			double redistributed = p.Relief.RedistributionDeltaAt(cell);
			total += area;
			integral += redistributed * area;
			if (plate == 0 && redistributed > 0.01 && p.HeightM[cell] > p.MaterialHeightM[cell]) arc = true;
			if (plate == 1 && p.HeightM[cell] < p.MaterialHeightM[cell]) trench = true;
			if (plate == 1) Program.True(redistributed == 0, "火山弧抬升只分配到上盘");
		}
		Program.True(arc && trench, "最终格心高程应同时出现上盘火山弧与下盘海沟");
		Program.Near(integral / total, 0, 1e-9, "火山弧不能重复计算增生抬升");
		Program.Near(frame.Budget.CumulativeResidual, 0, 1e-4, "火山弧与俯冲物质收支");
		CheckCellCenterRelief(sim.Grid, frame);
	}

	public static void DivergenceFollowsCrustOnEachSide(bool continentalSide)
	{
		var sim = PairedWorld(continentalSide, out var zero);
		var frame = sim.CalculateToStep(zero, 1);
		var p = frame.Projection;
		Program.True(frame.Budget.StepNewOceanic > 0, "洋侧张裂必须生成新洋壳");
		Program.True(p.OceanAgeMa.Any(age => age < zero.Definition.CreateSpecCopy().Evolution.InitialOceanAgeMa),
			"张裂洋壳应有年轻材料");
		bool ridge = false;
		for (int i = 0; i < p.PlateBoundaries.Edges.Length; i++)
		{
			if (p.Boundary.MotionKinds[i] != EvolutionMotionKind.Divergent) continue;
			var edge = p.PlateBoundaries.Edges[i];
			foreach (int cell in new[] { edge.CellA, edge.CellB })
				if (p.SurfaceCrustType[cell] == CrustType.Oceanic && p.HeightM[cell] > p.MaterialHeightM[cell])
					ridge = true;
		}
		Program.True(ridge, "最终高程应包含张裂洋侧的洋脊");
		if (continentalSide)
			Program.True(Enumerable.Range(0, sim.Grid.CellCount).All(cell => frame.Material.ExtensionStrain(cell, 0) == 0),
				"陆洋张裂的陆侧保持被动陆缘，不走陆陆裂解流程");
		Program.Near(frame.Budget.CumulativeResidual, 0, 1e-4, "张裂的物质收支");
	}

	public static void SurfaceCrustFollowsItsPlateInAMixedCell()
	{
		var sim = PairedWorld(true, out var zero);
		var grid = sim.Grid;
		var p = zero.Projection;
		int cell = Enumerable.Range(0, p.PlateBoundaries.Edges.Length)
			.Where(i => p.Boundary.ConvergenceKinds[i] == ConvergenceKind.Subduction)
			.Select(i => p.PlateBoundaries.Edges[i])
			.Select(edge => edge.PlateA == 0 ? edge.CellA : edge.CellB).First();
		double area = grid.AreasKm2[cell];
		var rows = CrustRows.Build(grid.CellCount, 4, null, (i, row) =>
		{
			if (i == cell)
			{
				row.Set((int)CrustType.Continental, CrustSlot.Create(0.45 * area, 35, 500, null, 0));
				row.Set((int)CrustType.Oceanic, CrustSlot.Create(0.10 * area, 7, -5000, -85, -1));
				row.Set(2 + (int)CrustType.Oceanic, CrustSlot.Create(0.45 * area, 7, -5000, -85, -1));
			}
			else foreach (ref readonly CrustEntry entry in zero.Material.Rows.Row(i)) row.Set(entry.Key, entry.Slot);
		});
		var material = new EvolutionCrust(grid.AreasKm2, 2, rows, 0, 1e-8);
		var spec = zero.Definition.CreateSpecCopy();
		var projected = CrustProjectionBuilder.Build(grid, new EvolutionReliefGeometry(grid, spec.Evolution),
			spec.Evolution, 0, 0, material, zero.Motions, sim.Plates.ToArray(), p.Crust.Provinces, new double[grid.CellCount], 0);
		Program.True(projected.Crust.Type[cell] == CrustType.Oceanic && projected.SurfacePlateOfCell[cell] == 0
			&& projected.SurfaceCrustType[cell] == CrustType.Continental,
			"上盘陆壳应保持表层陆壳，整格材料仍可以洋壳为多数");
		Program.Near(projected.OceanicCoverage[cell], 0.55, 1e-9, "保留整格洋壳比例");
	}

	static EvolutionSimulation PairedWorld(bool continentalPlate, out EvolutionFrame zero)
	{
		var world = World(1.5, -1.5, out var coarse, out var owners, out _, out _, out var plates);
		var grid = new EvolutionGrid(coarse.Cells, coarse.Neighbors, coarse.Centers, 400, coarse.Resolution);
		var types = owners.Select(id => continentalPlate && id == 0 ? CrustType.Continental : CrustType.Oceanic).ToArray();
		var provinces = continentalPlate
			? new[] { new CrustProvince(0, CrustType.Continental, Array.IndexOf(owners, 0), 0, owners.Count(id => id == 0)) }
			: Array.Empty<CrustProvince>();
		var crust = new CrustField(types, types.Select(t => t == CrustType.Continental ? 0 : -1).ToArray(), provinces);
		var heights = types.Select(t => t == CrustType.Continental ? 500f : -5000f).ToArray();
		world.Evolution.RidgeVolcanicRateKmPerMa = 0;
		return EvolutionSimulation.Create(world, grid, owners, crust, heights, plates, out zero);
	}

	public static void RepeatedRunIsDeterministic()
	{
		EvolutionSimulation a = BuildSim(1.5, -1.5, out var aFrame);
		EvolutionSimulation b = BuildSim(1.5, -1.5, out var bFrame);
		aFrame = a.CalculateToStep(aFrame, aFrame.Clock.StepIndex + 1);
		bFrame = b.CalculateToStep(bFrame, bFrame.Clock.StepIndex + 1);
		Program.Near(aFrame.Budget.CurrentCrustVolume, bFrame.Budget.CurrentCrustVolume, 1e-9, "两次独立运行的体积");
		Program.Near(aFrame.Mantle.StoredVolumeKm3, bFrame.Mantle.StoredVolumeKm3, 1e-9, "两次独立运行的库存");
		for (int i = 0; i < aFrame.Projection.HeightM.Count; i++)
			Program.Near(aFrame.Projection.HeightM[i], bFrame.Projection.HeightM[i], 1e-9, $"格 {i} 高程");
	}

	public static void TwoStepsEqualSeekToStepTwo()
	{
		EvolutionSimulation sequential = BuildSim(1.5, -1.5, out var sequentialFrame);
		sequentialFrame = sequential.CalculateToStep(sequentialFrame, sequentialFrame.Clock.StepIndex + 1);
		sequentialFrame = sequential.CalculateToStep(sequentialFrame, sequentialFrame.Clock.StepIndex + 1);

		EvolutionSimulation seek = BuildSim(1.5, -1.5, out var seekFrame);
		seekFrame = seek.CalculateToStep(seekFrame, 2);

		Program.True(seekFrame.Clock.StepIndex == 2, "跳转后应到第 2 步");
		Program.Near(seekFrame.Budget.CurrentCrustVolume, sequentialFrame.Budget.CurrentCrustVolume, 1e-9, "分步与跳转体积");
		Program.Near(seekFrame.Mantle.StoredVolumeKm3, sequentialFrame.Mantle.StoredVolumeKm3, 1e-9, "分步与跳转库存");
		Program.True(seekFrame.Projection.HeightM.SequenceEqual(sequentialFrame.Projection.HeightM),
			"延迟投影的最终高程必须与逐步出图一致");
		Program.True(seekFrame.Projection.PlateOfCell.SequenceEqual(sequentialFrame.Projection.PlateOfCell),
			"延迟投影的最终板块归属必须与逐步出图一致");
	}

	public static void CandidateCancellationKeepsSourceFrame()
	{
		var sim = BuildSim(0, 0, out var initial);
		using var cancel = new System.Threading.CancellationTokenSource();
		cancel.Cancel();
		try
		{
			sim.CalculateToStep(initial, 2, cancel.Token);
			throw new Exception("取消的计算应退出");
		}
		catch (OperationCanceledException)
		{
			Program.True(initial.Clock.StepIndex == 0, "取消后保留来源帧");
		}
		var frame = sim.CalculateToStep(initial, 1);
		Program.True(frame.Clock.StepIndex == 1 && initial.Clock.StepIndex == 0, "原帧可用于重新计算");
		Program.Throws(() => sim.CalculateToStep(frame, 0), "不支持向后跳转");
		var another = BuildSim(0, 0, out _);
		Program.Throws(() => another.CalculateToStep(frame, 2), "来源帧须与求解器的定义匹配");
	}

	public static void OversizedStepSplitsIntoTectonicSubsteps()
	{
		WorldSpec world = World(1.5, -1.5, out EvolutionGrid grid, out int[] owners,
			out CrustField crust, out float[] heights, out Plate[] plateTable);
		long safeYears = (long)(grid.MinNeighborDistanceKm * world.Evolution.MaxDisplacementFraction * 1_000_000d
			/ (1.5 * world.Evolution.LegacyOmegaScaleRadPerMa * grid.RadiusKm));
		long smallStepYears = safeYears * 3 / 4;
		world.Evolution.StepYears = 2 * smallStepYears;
		var coarse = EvolutionSimulation.Create(world, grid, owners, crust, heights, plateTable, out var coarseFrame);
		world.Evolution.StepYears = smallStepYears;
		var fine = EvolutionSimulation.Create(world, grid, owners, crust, heights, plateTable, out var fineFrame);
		Program.True(coarse.TectonicSubstepCount == 2 && fine.TectonicSubstepCount == 1, "长步应自动拆成两个安全子步");

		coarseFrame = coarse.CalculateToStep(coarseFrame, coarseFrame.Clock.StepIndex + 1);
		fineFrame = fine.CalculateToStep(fineFrame, fineFrame.Clock.StepIndex + 1);
		EvolutionBudget first = fineFrame.Budget;
		fineFrame = fine.CalculateToStep(fineFrame, fineFrame.Clock.StepIndex + 1);
		Program.True(coarseFrame.Clock.StepIndex == 1 && coarseFrame.Clock.TimeMa == fineFrame.Clock.TimeMa,
			"两个构造子步只能提交一个外部帧");
		Program.True(coarseFrame.Projection.HeightM.SequenceEqual(fineFrame.Projection.HeightM)
			&& coarseFrame.Projection.PlateOfCell.SequenceEqual(fineFrame.Projection.PlateOfCell),
			"拆步后的海拔和归属须与逐小步结算一致");
		Program.True(coarseFrame.Mantle.StoredVolumeKm3 == fineFrame.Mantle.StoredVolumeKm3
			&& coarseFrame.Budget.StepSubducted == first.StepSubducted + fineFrame.Budget.StepSubducted
			&& coarseFrame.Budget.StepNewOceanic == first.StepNewOceanic + fineFrame.Budget.StepNewOceanic
			&& coarseFrame.Budget.StepVolcanic == first.StepVolcanic + fineFrame.Budget.StepVolcanic,
			"提交帧应汇总所有构造子步的材料收支");
	}

	// ── 夹具：一次细分的二十面体球（80 三角格）──

	static EvolutionSimulation BuildSim(double omega0X, double omega1X, out EvolutionFrame frame)
	{
		WorldSpec world = World(omega0X, omega1X, out EvolutionGrid grid, out int[] plateOfCell,
			out CrustField crust, out float[] heights, out Plate[] plates);
		return EvolutionSimulation.Create(world, grid, plateOfCell, crust, heights, plates, out frame);
	}

	static WorldSpec World(double omega0X, double omega1X, out EvolutionGrid grid,
		out int[] plateOfCell, out CrustField crust, out float[] heights, out Plate[] plates)
	{
		(grid, plateOfCell) = IcoGrid();
		crust = IcoCrust(plateOfCell);
		heights = new float[plateOfCell.Length];
		for (int i = 0; i < heights.Length; i++)
			heights[i] = crust.Type[i] == CrustType.Continental ? 500 : -3000;

		plates = new[]
		{
			new Plate { PlateId = 0, Omega = new Vector3((float)omega0X, 0, 0), Density = 2800 },
			new Plate { PlateId = 1, Omega = new Vector3((float)omega1X, 0, 0), Density = 2900 },
		};
		var world = new WorldSpec();
		world.Plate.PlateCount = 2;
		world.Evolution.StepYears = 5_000_000;
		world.Evolution.InitialMantleVolumeKm3 = 1e10;
		return world;
	}

	static int FirstOceanCell(EvolutionFrame simFrame)
	{
		for (int i = 0; i < simFrame.Projection.Crust.Type.Length; i++)
			if (simFrame.Projection.Crust.Type[i] == CrustType.Oceanic) return i;
		throw new Exception("没有洋壳格");
	}

	static (EvolutionGrid grid, int[] plateOfCell) IcoGrid()
	{
		double phi = (1 + Math.Sqrt(5)) / 2;
		var baseVertices = new[]
		{
			(-1d, phi, 0d), (1d, phi, 0d), (-1d, -phi, 0d), (1d, -phi, 0d),
			(0d, -1d, phi), (0d, 1d, phi), (0d, -1d, -phi), (0d, 1d, -phi),
			(phi, 0d, -1d), (phi, 0d, 1d), (-phi, 0d, -1d), (-phi, 0d, 1d),
		};
		var vertices = new List<SpherePointD>();
		foreach (var v in baseVertices) vertices.Add(Unit(v.Item1, v.Item2, v.Item3));

		var baseFaces = new[]
		{
			(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
			(1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
			(3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
			(4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
		};

		var midpointCache = new Dictionary<(int, int), int>();
		var triangles = new List<(int A, int B, int C)>();
		int Midpoint(int a, int b)
		{
			(int, int) key = a < b ? (a, b) : (b, a);
			if (midpointCache.TryGetValue(key, out int found)) return found;
			SpherePointD p = Unit(vertices[a].X + vertices[b].X, vertices[a].Y + vertices[b].Y,
				vertices[a].Z + vertices[b].Z);
			vertices.Add(p);
			int index = vertices.Count - 1;
			midpointCache[key] = index;
			return index;
		}

		foreach (var face in baseFaces)
		{
			int ab = Midpoint(face.Item1, face.Item2);
			int bc = Midpoint(face.Item2, face.Item3);
			int ca = Midpoint(face.Item3, face.Item1);
			triangles.Add((face.Item1, ab, ca));
			triangles.Add((face.Item2, bc, ab));
			triangles.Add((face.Item3, ca, bc));
			triangles.Add((ab, bc, ca));
		}

		var cells = new SpherePointD[triangles.Count][];
		var centers = new SpherePointD[triangles.Count];
		for (int i = 0; i < triangles.Count; i++)
		{
			cells[i] = new[]
			{
				vertices[triangles[i].A], vertices[triangles[i].B], vertices[triangles[i].C],
			};
			SpherePointD a = vertices[triangles[i].A], b = vertices[triangles[i].B], c = vertices[triangles[i].C];
			centers[i] = Unit(a.X + b.X + c.X, a.Y + b.Y + c.Y, a.Z + b.Z + c.Z);
		}

		var edgeMap = new Dictionary<(int, int), List<int>>();
		for (int i = 0; i < triangles.Count; i++)
		{
			(int A, int B, int C) = triangles[i];
			AddEdge(edgeMap, A, B, i);
			AddEdge(edgeMap, B, C, i);
			AddEdge(edgeMap, C, A, i);
		}
		var neighbors = new int[triangles.Count][];
		var buckets = new List<int>[triangles.Count];
		for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<int>();
		foreach (var pair in edgeMap)
		{
			if (pair.Value.Count != 2)
				throw new Exception($"细分网格边 {pair.Key} 被 {pair.Value.Count} 个三角共享");
			buckets[pair.Value[0]].Add(pair.Value[1]);
			buckets[pair.Value[1]].Add(pair.Value[0]);
		}
		for (int i = 0; i < neighbors.Length; i++) neighbors[i] = buckets[i].ToArray();

		var grid = new EvolutionGrid(cells, neighbors, centers, 6371, 1);
		var plateOfCell = new int[cells.Length];
		for (int i = 0; i < plateOfCell.Length; i++)
			plateOfCell[i] = centers[i].Z >= 0 ? 0 : 1;
		return (grid, plateOfCell);
	}

	static void AddEdge(Dictionary<(int, int), List<int>> map, int a, int b, int triangle)
	{
		var key = a < b ? (a, b) : (b, a);
		if (!map.TryGetValue(key, out List<int> list))
		{
			list = new List<int>();
			map[key] = list;
		}
		list.Add(triangle);
	}

	static CrustField IcoCrust(int[] plateOfCell)
	{
		var types = new CrustType[plateOfCell.Length];
		var province = new int[plateOfCell.Length];
		int continental = 0;
		for (int i = 0; i < types.Length; i++)
		{
			if (plateOfCell[i] == 0)
			{
				types[i] = CrustType.Continental;
				province[i] = 0;
				continental++;
			}
			else
			{
				types[i] = CrustType.Oceanic;
				province[i] = -1;
			}
		}
		var provinces = new[] { new CrustProvince(0, CrustType.Continental, 0, 0, continental) };
		return new CrustField(types, province, provinces);
	}

	static CrustField AllContinental(int count) => new(
		Enumerable.Repeat(CrustType.Continental, count).ToArray(), new int[count],
		new[] { new CrustProvince(0, CrustType.Continental, 0, 0, count) });

	static SpherePointD Unit(double x, double y, double z)
	{
		double length = Math.Sqrt(x * x + y * y + z * z);
		return new SpherePointD(x / length, y / length, z / length);
	}
}
