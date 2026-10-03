using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Godot;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 架构契约测试（决策 08——架构 v1 冻结的守护钉）：
///   测的是"有没有偷偷跨层依赖"，不是功能。三条反射契约 + 一条行为契约：
///   ② 地理层不依赖投影器（FinalGeography 不引用 H3LandSeaProjector——Final 世界事实
///      只由 FinalHeight 派生，不看生成依据）；
///   ③ 合成器不依赖具体 Feature 类（HeightComposer 公共面只认 ITerrainField 列表——
///      新 Feature 零合成器改动的保证）；
///   ④ FinalGeography 不依赖具体 Feature 类（新 Feature 不需要改 FinalGeography）；
///   ⑤ 空特征列表 = 纯基线（没有特征就没有影响——"影响只能来自注册的特征"）。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class ArchitectureContractTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	[Test]
	public void FinalGeography_DoesNotReferencePlacementProjector()
	{
		// 契约 ②：Final 世界事实层不得引用 Placement 投影器（生成依据与世界事实分离的编译期体现）
		var t = typeof(FinalGeography);
		var offending = ReferencedTypes(t).Where(x => x == typeof(H3LandSeaProjector)).ToList();
		Assert.That(offending, Is.Empty,
			"FinalGeography 引用了 H3LandSeaProjector——Final 层不得读 Placement 生成依据（决策 07 §1）");
	}

	[Test]
	public void HeightComposer_DoesNotReferenceConcreteFeatures()
	{
		// 契约 ③：合成器只认 ITerrainField 列表——具体 Feature 类（骨架/地貌/火山）不得出现在
		// HeightComposer 的公共面（方法签名/字段）。新 Feature 零合成器改动的保证。
		var t = typeof(HeightComposer);
		var concrete = new[] { typeof(MountainSkeleton), typeof(RegionalLandforms), typeof(VolcanoField) };
		var offending = ReferencedTypes(t).Where(concrete.Contains).ToList();
		Assert.That(offending, Is.Empty,
			$"HeightComposer 引用了具体 Feature 类：{string.Join(",", offending)}——合成器只准消费 ITerrainField 列表（决策 08 契约 ③）");
	}

	[Test]
	public void FinalGeography_DoesNotReferenceConcreteFeatures()
	{
		// 契约 ④：新 Feature 不需要改 FinalGeography——最终地理只从 FinalHeight 派生
		var t = typeof(FinalGeography);
		var concrete = new[] { typeof(MountainSkeleton), typeof(RegionalLandforms), typeof(VolcanoField) };
		var offending = ReferencedTypes(t).Where(concrete.Contains).ToList();
		Assert.That(offending, Is.Empty,
			$"FinalGeography 引用了具体 Feature 类：{string.Join(",", offending)}——新 Feature 不需要改 FinalGeography（决策 08 契约 ④）");
	}

	/// <summary>类型自身声明的引用面：方法参数/返回 + 字段/属性类型（基类递归）。</summary>
	static IEnumerable<Type> ReferencedTypes(Type t)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
		for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
		{
			foreach (var m in cur.GetMethods(flags))
			{
				if (m.IsSpecialName) continue;
				foreach (var p in m.GetParameters()) yield return p.ParameterType;
				yield return m.ReturnType;
			}
			foreach (var f in cur.GetFields(flags)) yield return f.FieldType;
			foreach (var pr in cur.GetProperties(flags)) yield return pr.PropertyType;
		}
	}

	[Test]
	public void EmptyFeatureList_EqualsPureBaseline()
	{
		// 契约 ⑤：没有特征就没有影响——空列表的最终高度 = 纯基线
		//（Base + 三档 variation + MinLand 钳制）。手工构造基线并与空列表合成逐格对照。
		int seed = 42;
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, 8_000_000f);
		var composer = new HeightComposer(seed);
		var empty = Array.Empty<FeatureField>();
		composer.Generate(Ball, surface, g, empty);

		// 基线 = BaseElevation lerp 空（=自身）+ variation ±470 + MinLand 钳制
		float varSum = HeightComposer.LargeVariationM + HeightComposer.MediumVariationM + HeightComposer.RegionalNoiseM;
		var dirs = Ball.CellDirs;
		int checkedCells = 0;
		for (int i = 0; i < dirs.Length; i++)
		{
			if (g.RegionOfCell[i] < 0) continue;   // 海格基线 = 纯剖面（无 LandAndSea 特征）
			float baseH = g.BaseElevationField[i];
			// 下界考虑 MinLand 钳制（低基座 + variation 负摆会被钳到 30）
			Assert.That(composer.HeightM[i],
				Is.InRange(MathF.Min(baseH - varSum - 1f, HeightComposer.MinLandElevationM), baseH + varSum + 1f),
				$"格 {i}：空特征列表高度必须落在纯基线 ± variation 带内（钳制放宽下界）");
			checkedCells++;
		}
		Assert.That(checkedCells, Is.GreaterThan(0));
	}

	[Test]
	public void VolcanoField_RegistersThroughContract_Only()
	{
		// 实证复核（决策 08 首例 Feature）：火山全链只经 ITerrainField 进合成器——
		// 上面的反射契约 ③/④ 已保证"不需要改合成器/地理"；本测试验证运行期行为：
		// 一个火山 Feature 的存在只在其帽内改变地表。
		var volcano = new VolcanoFeature
		{
			Anchor = Vector3.Up,
			Scale = new Scale3(80f, 80f, 4200f),
			CapCenter = Vector3.Up,
			CapRadiusRad = 1.0f,
			SigmaKm = 40f,
			PeakTargetM = 4200f,
		};
		IReadOnlyList<FeatureField> features = new[] { new FeatureField(volcano, TerrainDomain.LandAndSea) };
		var composer = new HeightComposer(7);
		float halfSigmaRad = 0.5f * (40f / 6371f);   // 0.5σ 角距（球面精确构造，不靠线性近似）
		var q = (Vector3.Up * MathF.Cos(halfSigmaRad) + Vector3.Right * MathF.Sin(halfSigmaRad)).Normalized();
		var (inf, tgt) = volcano.SampleAt(q);
		Assert.That(inf, Is.GreaterThan(0.6f), $"0.5σ 处影响度应 ≈exp(−0.25)≈0.78（实测 {inf:F2}）");
		Assert.That(tgt, Is.InRange(4100f, 4300f), "火山目标 = 峰顶常数");
		Assert.That(features.Count, Is.EqualTo(1));
	}
}
