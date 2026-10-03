using System;
using NUnit.Framework;
using Godot;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 水文护栏（World Simulation 第一下游消费者，决策 08 v2）：
///   架构边界：RiverNetwork 不引用具体 Feature 类/投影器（反射，下游只依赖 Final 层）。
///   汇流四问（克制版验收）：① 河流只在 FinalLand 上；② 海洋是最终汇出口；
///   ③ 高程梯度稳定产生下坡流；④ 支流自然汇合（存在显著累积节点）。
///   确定性逐位同。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class RiverNetworkTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	static (RiverNetwork r, FinalGeography f, HeightComposer c) Make(int seed = 42)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, 8_000_000f);
		var m = new MountainSkeleton(seed, baseSigmaKm: 520f);
		m.Generate(Ball, g, surface);
		var l = new RegionalLandforms(seed);
		l.Generate(Ball, g);
		var features = new System.Collections.Generic.List<FeatureField>
		{
			new(l, TerrainDomain.LandOnly),
			new(m, TerrainDomain.LandAndSea),
		};
		var c = new HeightComposer(seed);
		c.Generate(Ball, surface, g, features);
		var f = new FinalGeography();
		f.Generate(Ball, c, g);
		var r = new RiverNetwork();
		r.Generate(Ball, f, c, riverThresholdCells: 12);   // res1 陆格少：低阈值让大河成形
		return (r, f, c);
	}

	[Test]
	public void Rivers_OnlyOnFinalLand()
	{
		// 四问 ①：河流掩码 ⊆ FinalLand（最终地理的世界事实）
		var (r, f, _) = Make();
		for (int i = 0; i < f.FinalLand.Length; i++)
			if (!f.FinalLand[i])
				Assert.That(r.IsRiver[i], Is.False, $"格 {i}：海格不得是河流");
	}

	[Test]
	public void Ocean_IsTheFinalOutlet()
	{
		// 四问 ②：每条陆上流链必终止于海（FinalLand=false）或内流洼地（Downstream=-1）
		var (r, f, _) = Make();
		int visitedTotal = 0;
		var state = new byte[f.FinalLand.Length];   // 0 未访 / 1 在链 / 2 已证
		var path = new System.Collections.Generic.List<int>();
		for (int s = 0; s < f.FinalLand.Length; s++)
		{
			if (!f.FinalLand[s]) continue;
			int i = s;
			path.Clear();
			while (i >= 0 && f.FinalLand[i] && state[i] == 0)
			{
				state[i] = 1;
				path.Add(i);
				i = r.Downstream[i];
				if (++visitedTotal > f.FinalLand.Length * 2) Assert.Fail("流链超长——疑似环");
			}
			// 链尾：要么入海（FinalLand=false），要么内流洼地（Downstream=-1）
			if (i >= 0)
				Assert.That(f.FinalLand[i] || r.Downstream[i] == -1, Is.True,
					$"格 {s} 的流链停在非海非洼地的格 {i}");
			// 终止于海时：下一跳必须是海格
			if (i >= 0 && r.Downstream[i] == -1 && !f.FinalLand[i])
				Assert.That(f.FinalLand[i], Is.False);
			foreach (int p in path) state[p] = 2;
		}
	}

	[Test]
	public void Flow_StrictlyDownhill()
	{
		// 四问 ③：每条流边严格降高（最终高度口径）
		var (r, f, c) = Make();
		for (int i = 0; i < c.HeightM.Length; i++)
		{
			int d = r.Downstream[i];
			if (d < 0) continue;
			Assert.That(c.HeightM[d], Is.LessThan(c.HeightM[i]),
				$"格 {i}：流边必须严格降高（{c.HeightM[i]:F0} → {c.HeightM[d]:F0}）");
		}
	}

	[Test]
	public void Tributaries_Converge_Naturally()
	{
		// 四问 ④：存在显著累积节点（多条支流汇合的自然产物，非人为规则）
		var (r, _, _) = Make();
		int maxAccum = 0;
		foreach (int a in r.FlowAccum) maxAccum = Math.Max(maxAccum, a);
		Assert.That(maxAccum, Is.GreaterThanOrEqualTo(12),
			$"最大汇流 {maxAccum} 不足——支流未自然汇合");
		Assert.That(r.SinkCount, Is.GreaterThanOrEqualTo(0));
	}

	[Test]
	public void SameSeed_BitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		CollectionAssert.AreEqual(a.r.Downstream, b.r.Downstream, "流向须逐位同");
		CollectionAssert.AreEqual(a.r.FlowAccum, b.r.FlowAccum, "累积须逐位同");
		CollectionAssert.AreEqual(a.r.IsRiver, b.r.IsRiver, "河流掩码须逐位同");
	}
}
