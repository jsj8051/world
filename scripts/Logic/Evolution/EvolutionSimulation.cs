using System;
using System.Collections.Generic;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>
/// 地壳演化求解器：由给定帧计算候选帧。网格、参数和输运系数在创建时固定。
/// </summary>
public sealed class EvolutionSimulation
{
	readonly EvolutionSpec _spec;
	readonly float _seaLevelM;
	readonly Plate[] _plates;
	readonly CrustProvince[] _provinces;
	readonly CrustAdvection _advection;
	readonly EvolutionReliefGeometry _reliefGeometry;
	internal int TectonicSubstepCount { get; }

	/// <summary>固定演化网格。</summary>
	public EvolutionGrid Grid { get; }
	/// <summary>冻结定义（参数副本、网格指纹）。</summary>
	public EvolutionDefinition Definition { get; }
	internal ReadOnlySpan<Plate> Plates => _plates;

	EvolutionSimulation(EvolutionSpec spec, float seaLevelM, EvolutionGrid grid, Plate[] plates,
		CrustProvince[] provinces, EvolutionDefinition definition, CrustAdvection advection,
		EvolutionReliefGeometry reliefGeometry, int tectonicSubstepCount)
	{
		_spec = spec;
		_seaLevelM = seaLevelM;
		_plates = plates;
		_provinces = provinces;
		Grid = grid;
		Definition = definition;
		_advection = advection;
		_reliefGeometry = reliefGeometry;
		TectonicSubstepCount = tectonicSubstepCount;
	}

	/// <summary>
	/// 从零步世界状态建立演化仿真：每格初始化一个材料槽，海底年龄取统一初始值，地幔库存取配置值。
	/// </summary>
	/// <exception cref="ArgumentException">长度或引用不一致，或子步数量超出可表示范围。</exception>
	public static EvolutionSimulation Create(WorldSpec world, EvolutionGrid grid, int[] plateOfCell,
		CrustField crust, float[] heightM, Plate[] plates, out EvolutionFrame initialFrame)
	{
		if (world == null) throw new ArgumentNullException(nameof(world));
		if (world.Evolution == null) throw new ArgumentException("世界定义缺 Evolution 段", nameof(world));
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (crust == null) throw new ArgumentNullException(nameof(crust));
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));
		if (plates == null) throw new ArgumentNullException(nameof(plates));

		var definition = EvolutionDefinition.Create(world, grid.Resolution, grid.RadiusKm, grid.Fingerprint());
		WorldSpec frozen = definition.CreateSpecCopy();
		EvolutionSpec spec = frozen.Evolution;
		spec.Validate();
		int n = grid.CellCount;
		if (plateOfCell.Length != n || crust.Type.Length != n || heightM.Length != n)
			throw new ArgumentException("逐格数组长度与网格不一致");
		if (plates.Length < 1) throw new ArgumentException("板块表为空", nameof(plates));
		if (plates.Length != world.Plate.PlateCount)
			throw new ArgumentException($"板块表 {plates.Length} 与冻结配置 PlateCount {world.Plate.PlateCount} 不一致",
				nameof(plates));

		double stepMa = spec.StepYears / 1_000_000d;
		double maxSpeed = 0;
		foreach (Plate plate in plates)
			maxSpeed = Math.Max(maxSpeed, Math.Sqrt(plate.Omega.X * plate.Omega.X
				+ plate.Omega.Y * plate.Omega.Y + plate.Omega.Z * plate.Omega.Z) * spec.LegacyOmegaScaleRadPerMa);
		double maxDisplacementKm = maxSpeed * stepMa * grid.RadiusKm;
		double limitKm = spec.MaxDisplacementFraction * grid.MinNeighborDistanceKm;
		double requiredSubsteps = maxDisplacementKm <= limitKm ? 1 : Math.Ceiling(maxDisplacementKm / limitKm);
		if (!double.IsFinite(requiredSubsteps) || requiredSubsteps > int.MaxValue)
			throw new ArgumentException("构造子步数量超出可表示范围");
		int tectonicSubstepCount = (int)requiredSubsteps;

		for (int cell = 0; cell < n; cell++)
		{
			int plate = plateOfCell[cell];
			CrustType type = crust.Type[cell];
			if ((uint)plate >= (uint)plates.Length || (uint)type > 1)
				throw new ArgumentException($"格 {cell} 的板块号或壳型非法");
		}
		var slots = CrustRows.Build(n, checked(plates.Length * 2), null, (cell, row) =>
		{
			int plate = plateOfCell[cell];
			CrustType type = crust.Type[cell];
			bool ocean = type == CrustType.Oceanic;
			double thickness = ocean ? spec.InitialOceanicThicknessKm : spec.InitialContinentalThicknessKm;
			var slot = CrustSlot.Create(grid.AreasKm2[cell], thickness, heightM[cell],
				ocean ? -spec.InitialOceanAgeMa : null, crust.ProvinceId[cell]);
			if (ocean) slot.CoolingReferenceArea = slot.Area * Math.Sqrt(Math.Min(spec.InitialOceanAgeMa, spec.MaxCoolingAgeMa));
			row.Set(2 * plate + (int)type, slot);
		});
		var material = new EvolutionCrust(grid.AreasKm2, plates.Length, slots, 0, spec.CoverageRelativeTolerance);
		var motions = new PlateMotion[plates.Length];
		for (int i = 0; i < plates.Length; i++)
		{
			motions[i] = new PlateMotion(i, new SphereVectorD(
				plates[i].Omega.X * spec.LegacyOmegaScaleRadPerMa,
				plates[i].Omega.Y * spec.LegacyOmegaScaleRadPerMa,
				plates[i].Omega.Z * spec.LegacyOmegaScaleRadPerMa), RotationD.Identity);
		}

		plates = (Plate[])plates.Clone();
		var provinces = (CrustProvince[])crust.Provinces.Clone();
		var clock = new EvolutionClock(0, spec.StepYears);
		var mantle = new MantleReservoir(spec.InitialMantleVolumeKm3);
		var reliefGeometry = new EvolutionReliefGeometry(grid, spec);
		CrustProjection projection = CrustProjectionBuilder.Build(grid, reliefGeometry, spec, 0, 0, material, motions,
			plates, provinces, new double[n], frozen.Terrain.SeaLevelM);
		EvolutionBudget budget = EvolutionBudget.CreateInitial(material.VolumeKm3, mantle.StoredVolumeKm3);
		initialFrame = new EvolutionFrame(definition, clock, motions, material, mantle, projection, budget);
		initialFrame.Validate();
		return new EvolutionSimulation(spec, frozen.Terrain.SeaLevelM, grid, plates, provinces, definition,
			new CrustAdvection(grid, motions, stepMa / tectonicSubstepCount), reliefGeometry, tectonicSubstepCount);
	}

	/// <summary>从已完成的帧向前计算。在中间步只生成下一步所需的构造数据。</summary>
	public EvolutionFrame CalculateToStep(EvolutionFrame previous, long targetStep,
		CancellationToken cancellation = default, Action<long, long> onStep = null)
	{
		if (previous == null) throw new ArgumentNullException(nameof(previous));
		if (!ReferenceEquals(previous.Definition, Definition))
			throw new ArgumentException("来源帧与演化定义不一致", nameof(previous));
		if (targetStep < previous.Clock.StepIndex)
			throw new ArgumentOutOfRangeException(nameof(targetStep), "当前版本只支持向前跳转");

		cancellation.ThrowIfCancellationRequested();
		long start = previous.Clock.StepIndex;
		long total = targetStep - start;
		while (previous.Clock.StepIndex < targetStep)
		{
			previous = AdvanceOne(previous, previous.Clock.StepIndex == targetStep - 1, cancellation);
			onStep?.Invoke(previous.Clock.StepIndex - start, total);
		}
		return previous;
	}

	EvolutionFrame AdvanceOne(EvolutionFrame previous, bool includeSurface, CancellationToken cancellation)
	{
		double substepYears = previous.Clock.StepYears / (double)TectonicSubstepCount;
		double deltaMa = (previous.Clock.StepYears / 1_000_000d) / TectonicSubstepCount;
		try
		{
			var clock = new EvolutionClock(checked(previous.Clock.StepIndex + 1), previous.Clock.StepYears);
			var material = previous.Material;
			var projection = previous.Projection;
			var mantle = previous.Mantle;
			var motions = previous.Motions;
			var budget = previous.Budget;
			double subducted = 0, newOceanic = 0, volcanic = 0, areaCorrection = 0;
			for (int substep = 0; substep < TectonicSubstepCount; substep++)
			{
				cancellation.ThrowIfCancellationRequested();
				bool last = substep == TectonicSubstepCount - 1;
				double timeMa = last ? clock.TimeMa
					: (previous.Clock.StepIndex * (double)clock.StepYears + (substep + 1) * substepYears) / 1_000_000d;
				double oldCrust = material.VolumeKm3;
				double oldMantle = mantle.StoredVolumeKm3;
				var rates = new double[Grid.CellCount];
				var processes = new CrustProcesses(Grid, _spec, projection, timeMa, deltaMa, rates);
				CrustRows slots = _advection.Move(material, processes.SettleCell);
				slots = processes.ResolveOverflow(slots, cancellation);
				var flows = processes.CollectFlows();
				double mantleVolume = oldMantle + flows.Subducted - flows.NewOceanic - flows.Volcanic;
				if (mantleVolume < -1e-9)
					throw new EvolutionStepException(EvolutionProblemCode.InsufficientMantleMaterial,
						$"构造子步的洋壳与火山增生超出地幔库存 {-mantleVolume:G6} km³");
				mantle = new MantleReservoir(Math.Max(0, mantleVolume));
				material = new EvolutionCrust(Grid.AreasKm2, _plates.Length, slots, timeMa, _spec.CoverageRelativeTolerance);
				motions = AdvanceOrientations(motions, deltaMa);
				projection = CrustProjectionBuilder.Build(Grid, _reliefGeometry, _spec, clock.StepIndex, timeMa,
					material, motions, _plates, _provinces, rates, _seaLevelM);
				budget = new EvolutionBudget(budget.InitialCrustVolume, budget.InitialMantleVolume,
					oldCrust, material.VolumeKm3, oldMantle, mantle.StoredVolumeKm3,
					flows.Subducted, 0, flows.NewOceanic, flows.Volcanic, budget.TotalSubducted + flows.Subducted,
					budget.TotalReturned, budget.TotalNewOceanic + flows.NewOceanic, budget.TotalVolcanic + flows.Volcanic);
				if (TectonicSubstepCount > 1)
					budget.Validate(_spec.VolumeAbsoluteToleranceKm3, _spec.VolumeRelativeTolerance);

				subducted += flows.Subducted;
				newOceanic += flows.NewOceanic;
				volcanic += flows.Volcanic;
				areaCorrection += flows.AreaCorrection;
			}
			if (TectonicSubstepCount > 1)
			{
				budget = new EvolutionBudget(budget.InitialCrustVolume, budget.InitialMantleVolume,
					previous.Material.VolumeKm3, material.VolumeKm3, previous.Mantle.StoredVolumeKm3, mantle.StoredVolumeKm3,
					subducted, 0, newOceanic, volcanic, budget.TotalSubducted, budget.TotalReturned,
					budget.TotalNewOceanic, budget.TotalVolcanic);
			}
			var frame = new EvolutionFrame(Definition, clock, motions, material, mantle, projection, budget,
				areaCorrection);
			frame.Validate(includeSurface);
			cancellation.ThrowIfCancellationRequested();
			return frame;
		}
		catch (OverflowException exception)
		{
			throw new EvolutionStepException(EvolutionProblemCode.TimeOverflow, exception.Message, exception);
		}
		catch (ArgumentException exception)
		{
			throw new EvolutionStepException(EvolutionProblemCode.InvalidCoverage, exception.Message, exception);
		}
	}

	static IReadOnlyList<PlateMotion> AdvanceOrientations(IReadOnlyList<PlateMotion> motions,
		double deltaMa)
	{
		var next = new PlateMotion[motions.Count];
		for (int i = 0; i < motions.Count; i++)
		{
			PlateMotion motion = motions[i];
			SphereVectorD omega = motion.OmegaRadPerMa;
			double speed = Math.Sqrt(omega.X * omega.X + omega.Y * omega.Y + omega.Z * omega.Z);
			RotationD orientation = motion.Orientation;
			if (speed > 1e-15)
			{
				double angle = speed * deltaMa;
				double ax = omega.X / speed, ay = omega.Y / speed, az = omega.Z / speed;
				double half = angle / 2, sin = Math.Sin(half), cos = Math.Cos(half);
				orientation = Multiply(new RotationD(ax * sin, ay * sin, az * sin, cos), orientation);
			}
			next[i] = new PlateMotion(motion.PlateId, omega, orientation);
		}
		return next;
	}

	static RotationD Multiply(RotationD a, RotationD b) => new(
		a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
		a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
		a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
		a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

}
