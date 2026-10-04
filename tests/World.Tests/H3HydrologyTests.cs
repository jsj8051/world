using System;
using System.Collections.Generic;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · H3 水文护栏（决策原典 §七：H3 邻接图上 find lowest neighbor + flow accumulation）：
///   · 流向不变量：陆格只走**严格下降**边（⇒ 无环）；平局/无更低 = 出口（内流洼地）；
///   · 累积精确不变量：FlowAccum[i] = 1 + Σ FlowAccum[上游陆格]，海格恒 1、不接收；
///   · 单峰岛：链全部终止于海；平原（全同高）全 sink；确定性逐位同。
/// 人造地形直接构造海拔数组（不依赖噪声场）；纪律（同 NoiseTerrainTests）：
///   只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3HydrologyTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
	static Ball Ball => SharedBall.Value;

	/// <summary>径向单峰岛：以格 0 方向为峰，角距离线性降到反半球海面以下。</summary>
	static float[] ConeIsland()
	{
		var dirs = Ball.CellDirs;
		var peak = dirs[0];
		var elev = new float[dirs.Length];
		for (int i = 0; i < dirs.Length; i++)
		{
			float angle = MathF.Acos(Math.Clamp(dirs[i].Dot(peak), -1f, 1f));
			elev[i] = 1200f * (1f - angle / (MathF.PI / 2f));   // 90° 内出露为岛，其余为海
		}
		return elev;
	}

	[Test]
	public void FlowGoesStrictlyDownhill()
	{
		var h = new H3Hydrology();
		h.Generate(Ball, ConeIsland());
		var elev = ConeIsland();
		for (int i = 0; i < elev.Length; i++)
		{
			if (h.IsOcean[i] || h.Downstream[i] < 0) continue;
			Assert.That(elev[h.Downstream[i]], Is.LessThan(elev[i]),
				$"格 {i}：流向必须严格降高（保证无环的地基）");
		}
	}

	[Test]
	public void DownstreamGraph_IsAcyclic_AllChainsReachOceanOrSink()
	{
		var h = new H3Hydrology();
		h.Generate(Ball, ConeIsland());
		int n = h.Downstream.Length;
		// 三色标记沿链走：0 未访 / 1 在栈 / 2 已证无环——回遇在栈 = 有环
		var color = new byte[n];
		var path = new List<int>(n);
		for (int s = 0; s < n; s++)
		{
			int i = s;
			path.Clear();
			while (i >= 0 && color[i] == 0)
			{
				color[i] = 1;
				path.Add(i);
				i = h.Downstream[i];
			}
			if (i >= 0 && color[i] == 1)
				Assert.Fail($"格 {s} 出发沿流向回到在栈格 {i}——流向图存在环");
			foreach (int p in path) color[p] = 2;
		}
	}

	[Test]
	public void FlowAccum_ExactRecurrence_OceanNeverReceives()
	{
		var h = new H3Hydrology();
		var elev = ConeIsland();
		h.Generate(Ball, elev);

		// 上游表：上游[j] = 所有 Downstream==j 的格
		int n = elev.Length;
		var upstream = new List<int>[n];
		for (int i = 0; i < n; i++) upstream[i] = new List<int>();
		for (int i = 0; i < n; i++)
			if (h.Downstream[i] >= 0)
				upstream[h.Downstream[i]].Add(i);

		for (int i = 0; i < n; i++)
		{
			if (h.IsOcean[i])
			{
				Assert.That(h.FlowAccum[i], Is.EqualTo(1), $"海格 {i} 累积恒 1（雨落海上不入河流账）");
				continue;
			}
			int expect = 1;
			foreach (int u in upstream[i]) expect += h.FlowAccum[u];
			Assert.That(h.FlowAccum[i], Is.EqualTo(expect),
				$"格 {i}：累积必须精确等于 1 + Σ上游（含自身雨量）");
		}
	}

	[Test]
	public void ConeIsland_PeakDrainsToOcean_NoLandSink()
	{
		var h = new H3Hydrology();
		h.Generate(Ball, ConeIsland());
		Assert.That(h.SinkCount, Is.EqualTo(0),
			"径向单峰岛无内流洼地（每陆格都有严格更低邻居或临海）");
		int peak = 0;
		for (int i = 1; i < Ball.CellDirs.Length; i++)
			if (ConeIsland()[i] > ConeIsland()[peak]) peak = i;
		// 峰是全图最高 ⇒ 无上游贡献于它之上，但峰自身链必须到海
		int i2 = peak, hops = 0;
		while (h.Downstream[i2] >= 0)
		{
			i2 = h.Downstream[i2];
			if (++hops > Ball.CellDirs.Length) Assert.Fail("峰的下游链超长——疑似环");
		}
		Assert.That(h.IsOcean[i2], Is.True, "峰的径流链必须终止于海");
	}

	[Test]
	public void FlatPlateau_AllCellsAreSinks()
	{
		var elev = new float[Ball.CellDirs.Length];
		Array.Fill(elev, 500f);   // 全同高、全在海上（等高不走边）
		var h = new H3Hydrology();
		h.Generate(Ball, elev);
		Assert.That(h.SinkCount, Is.EqualTo(elev.Length), "平原无严格下降边 ⇒ 全部为内流 sink（框架期语义）");
		foreach (int d in h.Downstream) Assert.That(d, Is.EqualTo(-1));
	}

	[Test]
	public void SameInput_GeneratesBitwiseIdentical()
	{
		var a = new H3Hydrology();
		var b = new H3Hydrology();
		a.Generate(Ball, ConeIsland());
		b.Generate(Ball, ConeIsland());
		CollectionAssert.AreEqual(a.Downstream, b.Downstream, "同输入流向须逐位同");
		CollectionAssert.AreEqual(a.FlowAccum, b.FlowAccum, "同输入累积须逐位同");
	}
}
