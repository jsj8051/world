using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using World.Data;
using World.Params;

namespace World.Tests;

/// <summary>
/// 第 4 步结构落地测试：只验证类型、参数兼容与结构校验，不推进演化。
/// 材料槽使用合成面积，不依赖 H3 原生库。
/// </summary>
static class EvolutionStructureTests
{
	// ── 参数 ──

	public static void DefaultsAreValid()
	{
		var spec = new EvolutionSpec();
		spec.Validate();
		Program.True(spec.StepYears == 100_000, $"默认步长应为 100000，实得 {spec.StepYears}");
	}

	public static void RejectsBadStepYears()
	{
		var spec = new EvolutionSpec { StepYears = 0 };
		Program.Throws(() => spec.Validate(), "零步长必须被拒绝");
	}

	public static void RejectsDensityOrder()
	{
		var spec = new EvolutionSpec { OceanicDensityKgM3 = 2700 };
		Program.Throws(() => spec.Validate(), "洋壳密度低于陆壳必须被拒绝");
	}

	public static void JsonSuppliesEvolutionDefaults()
	{
		string root = RepoRoot();
		string json = File.ReadAllText(Path.Combine(root, "res", "params", "world_params.json"));
		WorldSpec spec = WorldParams.ReadJson(json, out var problems);
		Program.True(spec != null, $"参数档应可装配：{string.Join("；", problems)}");
		Program.True(problems.Count == 0, $"参数档不应有问题：{string.Join("；", problems)}");
		Program.True(spec.Evolution.StepYears == 100_000, "参数档 Evolution.StepYears 应为 100000");
		Program.True(spec.Evolution.InitialMantleVolumeKm3 == 10_000_000_000d, "地幔库存默认值不符");
		spec.Evolution.Validate();
	}

	public static void JsonEvolutionMatchesCodeDefaults()
	{
		string root = RepoRoot();
		string json = File.ReadAllText(Path.Combine(root, "res", "params", "world_params.json"));
		WorldSpec spec = WorldParams.ReadJson(json, out _);
		EvolutionSpec expected = new();
		foreach (var p in typeof(EvolutionSpec).GetProperties())
		{
			if (!p.CanRead) continue;
			object fromJson = p.GetValue(spec.Evolution);
			object fromCode = p.GetValue(expected);
			Program.True(Equals(fromJson, fromCode),
				$"Evolution.{p.Name}：参数档 {fromJson} ≠ 代码默认 {fromCode}");
		}
	}

	public static void InvalidJsonEvolutionIsRejectedAtomically()
	{
		const string json = "{ \"Evolution\": { \"StepYears\": -1 } }";
		WorldSpec spec = WorldParams.ReadJson(json, out var problems);
		Program.True(spec == null, "非法 Evolution 段应整层拒绝");
		Program.True(problems.Any(p => p.Contains("Evolution")), "应报告 Evolution 段问题");
	}

	// ── 球面基础类型与材料 ──

	public static void SphereTypesEnforceInvariants()
	{
		Program.Throws(() => new SpherePointD(1, 1, 1), "非单位方向必须被拒绝");
		Program.Throws(() => new RotationD(0, 0, 0, 2), "非单位四元数必须被拒绝");
		new SphereVectorD(0, 0, 0).Validate();
	}

	public static void ZeroStepContinentalSlotIsValid()
	{
		var slot = CrustSlot.Create(100, 35, 600, null, 0);
		var material = new EvolutionCrust(new[] { 100d }, 1, new[] { default, slot }, 0, 1e-8);
		Program.Near(material.ThicknessKm(0, 0, CrustType.Continental), 35, 0, "零步陆壳厚度");
	}

	public static void OldOceanSlotKeepsItsBirthTime()
	{
		var slot = CrustSlot.Create(100, 7, -2500, -80, -1);
		var material = new EvolutionCrust(new[] { 100d }, 1, new[] { slot, default }, 0, 1e-8);
		Program.Near(material.OceanBirthTimeMa(0, 0)!.Value, -80, 0, "初始洋壳出生时间");
	}

	public static void OceanSlotRejectsFutureBirth()
	{
		var slot = CrustSlot.Create(100, 7, -2500, 2, -1);
		Program.Throws(() => new EvolutionCrust(new[] { 100d }, 1, new[] { slot, default }, 0, 1e-8),
			"洋壳出生时间不能晚于当前时间");
	}

	public static void SlotRejectsNegativeThickness()
	{
		var slot = CrustSlot.Create(100, -35, 600, null, 0);
		Program.Throws(() => new EvolutionCrust(new[] { 100d }, 1, new[] { default, slot }, 0, 1e-8),
			"负壳厚必须被拒绝");
	}

	public static void TwoSlotsCoverCellExactly()
	{
		var ocean = CrustSlot.Create(40, 7, -2500, -80, -1);
		var land = CrustSlot.Create(60, 35, 600, null, 0);
		var material = new EvolutionCrust(new[] { 100d }, 1, new[] { ocean, land }, 0, 1e-8);
		Program.True(material.OccupiedSlotCount == 2, "同一格支持两种壳型");
	}

	public static void InvalidCoverageIsRejected()
	{
		foreach (double area in new[] { 50d, 150d })
		{
			var slot = CrustSlot.Create(area, 35, 600, null, 0);
			Program.Throws(() => new EvolutionCrust(new[] { 100d }, 1, new[] { default, slot }, 0, 1e-8),
				"占地不足或超出宿主格都必须被拒绝");
		}
	}

	public static void MantleRejectsNegativeStock()
	{
		Program.Throws(() => new MantleReservoir(-1), "负库存必须被拒绝");
	}

	public static void BudgetAcceptsPoolTransfer()
	{
		var budget = new EvolutionBudget(1_000_000, 500, 1_000_000, 999_100, 500, 1_400,
			1_000, 0, 100, 0, 1_000, 0, 100, 0);
		budget.Validate(0.001, 1e-8);
	}

	public static void BudgetRejectsMantleIdentityMismatch()
	{
		var budget = new EvolutionBudget(1_000_000, 500, 1_000_000, 1_000_000, 500, 1_400_001,
			1_000, 0, 0, 0, 1_000, 0, 0, 0);
		Program.Throws(() => budget.Validate(0.001, 1e-8), "库存恒等式失配必须被拒绝");
	}

	public static void ProjectionRejectsLengthMismatch()
	{
		Program.Throws(() => new CrustProjection(0, 1, new[] { 0 }, CrustOfOne(), new float[2],
			new double?[] { null }, new[] { 0d }, new[] { 0d }, EmptyBoundaries(), EmptyBoundaryField()),
			"逐格数组长度不一致必须被拒绝");
	}

	public static void FrameValidatesAlignedZeroStep()
	{
		EvolutionFrame frame = ZeroStepFrame();
		frame.Validate();
	}

	public static void FrameCanDeferSurfaceDuringSeek()
	{
		EvolutionFrame initial = ZeroStepFrame();
		int builds = 0;
		var projection = new CrustProjection(0, 1, new[] { 0 }, initial.Projection.Crust,
			new[] { 600f }, new double?[] { null }, new[] { 0d }, new[] { 0d },
			EmptyBoundaries(), EmptyBoundaryField(), () =>
			{
				builds++;
				return (new[] { 750f }, null, new[] { new CrustSurfaceCell(1, 750, 0) }, 1,
					new[] { CrustType.Continental });
			});
		var frame = new EvolutionFrame(initial.Definition, initial.Clock, initial.Motions, initial.Material,
			initial.Mantle, projection, initial.Budget);

		frame.Validate(includeSurface: false);
		Program.True(builds == 0, "材料和账本校验不能提前触发地形计算");
		frame.Validate();
		Program.True(projection.HeightM[0] == 750 && builds == 1, "出图只计算一次");
	}

	public static void FrameRejectsMantleMismatch()
	{
		EvolutionFrame good = ZeroStepFrame();
		var bad = new EvolutionFrame(good.Definition, good.Clock, good.Motions, good.Material,
			new MantleReservoir(1e10 + 1), good.Projection, good.Budget);
		Program.Throws(() => bad.Validate(), "帧库存与地幔池不一致必须被拒绝");
	}

	public static void ClockRoundsUpPartialSteps()
	{
		var clock = new EvolutionClock(0, 100_000);
		Program.True(clock.StepsFor(250_000) == 3, "不足一步应向上取整");
		Program.True(clock.StepsFor(0) == 0, "零年应为零步");
	}

	// ── 夹具 ──

	static EvolutionFrame ZeroStepFrame()
	{
		var spec = new WorldSpec();
		spec.Plate.PlateCount = 1;
		spec.Evolution.StepYears = 100_000;

		EvolutionDefinition definition = EvolutionDefinition.Create(spec, 4, 6371, "test-grid");
		var clock = new EvolutionClock(0, 100_000);
		var motions = new[] { new PlateMotion(0, new SphereVectorD(0, 0, 0.01), RotationD.Identity) };


		const double area = 100;
		var material = new EvolutionCrust(new[] { area }, 1,
			new[] { default, CrustSlot.Create(area, 35, 600, null, 0) }, 0, 1e-8);

		CrustField crust = CrustOfOne();
		var projection = new CrustProjection(0, 1, new[] { 0 }, crust, new[] { 600f },
			new double?[] { null }, new[] { 0d }, new[] { 0d }, EmptyBoundaries(), EmptyBoundaryField());

		double volume = material.VolumeKm3;
		EvolutionBudget budget = EvolutionBudget.CreateInitial(volume, 1e10);
		var mantle = new MantleReservoir(1e10);
		return new EvolutionFrame(definition, clock, motions, material, mantle, projection, budget);
	}

	static CrustField CrustOfOne()
	{
		var provinces = new[] { new CrustProvince(0, CrustType.Continental, 0, 0, 1) };
		return new CrustField(new[] { CrustType.Continental }, new[] { 0 }, provinces);
	}

	static PlateBoundarySet EmptyBoundaries()
	{
		var set = new PlateBoundarySet(1, Array.Empty<PlateBoundaryEdge>(),
			new[] { default(PlateBoundaryPair) }, new[] { 0, 0 }, Array.Empty<int>());
		set.Validate(1, new[] { 0 });
		return set;
	}

	static EvolutionBoundaryField EmptyBoundaryField()
		=> new(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<EvolutionMotionKind>(),
			Array.Empty<ConvergenceKind>(), Array.Empty<int>());

	static string RepoRoot()
	{
		var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
		while (dir != null && !File.Exists(Path.Combine(dir.FullName, "world.csproj"))) dir = dir.Parent;
		return dir?.FullName ?? throw new Exception("找不到 world.csproj 所在的项目根");
	}
}
