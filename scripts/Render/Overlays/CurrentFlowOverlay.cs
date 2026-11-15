using System;
using Godot;
using System.Collections.Generic;
using World.Logic;
using World.Utils;

namespace World.Render;

/// <summary>
/// 洋流粒子层：把洋流方向场积成静态 ribbon 流线，运行时由 shader 沿 UV.x 播放彗星。
/// 积分、分桶与网格都在 <see cref="FlowParticleOverlay"/>；本类只负责取洋流场、按冷暖配色、
/// 合并同路线光带，以及"只有洋流粒子档才显示"的显隐判断。
///
/// @example
/// <code>
/// var flow = new CurrentFlowOverlay { Name = "CurrentFlow" };
/// AddChild(flow);
/// flow.SetContext(facts, currentMode);
/// </code>
/// </summary>
internal sealed partial class CurrentFlowOverlay : FlowParticleOverlay, IMapLayer
{
	const float MergeRadiusCells = 1.0f;   // 近似同路线的合并半径（格距倍数）
	const float MergeOverlap = 0.55f;      // 重叠超过此占比的整条丢弃

	WorldFacts _layerFacts;
	float[] _warmth;

	protected override int SeedCount => 2600;
	protected override float MinSpeedMul => 0.45f;
	protected override float FilledSpeed => 0.10f;
	protected override float MinSeedSpeed => 0.32f;
	protected override float BaseWidthPx => 2.8f;
	protected override float WidthPerStrengthPx => 1.2f;
	protected override float CycleU => 0.22f;
	protected override float CometRate => 0.55f;
	protected override string LogTag => "[FLOW]";
	protected override float LutDutyBase => 0.46f;
	protected override float LutDutyStep => 0.04f;
	protected override float LutPowerBase => 1.15f;
	protected override float LutPowerStep => 0.15f;
	protected override float LutTailAmplitude => 0.62f;

	public void SetContext(WorldFacts facts, MapMode mode)
	{
		bool show = facts != null && mode is OceanCurrentMode ocean && ocean.ShowParticles;
		if (!ReferenceEquals(_layerFacts, facts))
		{
			if (show)
			{
				BuildFromFacts(facts);
				_layerFacts = facts;
			}
			else if (_layerFacts != null)
			{
				Mesh = null;
				_layerFacts = null;
			}
		}
		Visible = show && Mesh != null;
	}

	/// <summary>按本次发布的世界事实重装流线（旧几何丢弃）。世界事实换了才调一次。</summary>
	void BuildFromFacts(WorldFacts facts)
	{
		PrepareGrid(facts.Ball);
		Land = facts.FinalLand;
		_warmth = facts.OceanCurrentWarmth;

		Mesh = null;
		if (facts.OceanCurrentDirection == null || facts.OceanCurrentDirection.Length != CellDirs.Length)
		{
			Visible = false;
			return;
		}

		var watch = System.Diagnostics.Stopwatch.StartNew();
		BuildField(facts.OceanCurrentDirection, facts.OceanCurrentStrength);
		if (!EnsureMaterial())
		{
			Visible = false;
			return;
		}

		var lines = BuildLines();
		int rawLines = lines.Count;
		lines = MergeLines(lines);
		if (lines.Count == 0)
		{
			Visible = false;
			return;
		}

		int points = 0;
		foreach (var line in lines) points += line.Pts.Length;

		ApplyMesh(BuildMesh(lines));
		Visible = true;
		GD.Print($"{LogTag} raw={rawLines} kept={lines.Count} points={points} build={watch.ElapsedMilliseconds}ms");
	}

	/// <summary>冷暖色带与"冷暖"档同源；弱流去饱和，强流更亮。</summary>
	protected override Color ColorAt(int cell, float strength)
	{
		float warmth = _warmth[Math.Clamp(cell, 0, _warmth.Length - 1)];
		var c = ColorRamp.RampSampleSmooth(OceanCurrentMode.WarmthStops, (warmth + 1f) * 0.5f);
		c = new Color(0.92f, 0.94f, 1f).Lerp(c, 0.45f + 0.55f * strength);
		return c * (0.50f + 0.50f * strength);
	}

	/// <summary>
	/// 合并近似同路线的流线：先按长度降序，再对每条线的采样点查"已被保留线的点是否在
	/// <see cref="MergeRadiusCells"/> 格内"；重叠超过 <see cref="MergeOverlap"/> 的整条丢弃。
	/// 只保留长线即可保住主干；被丢弃的是同一条支流上的重复光带。
	/// </summary>
	List<FlowLine> MergeLines(List<FlowLine> lines)
	{
		if (lines.Count < 2) return lines;

		lines.Sort(static (a, b) => b.Pts.Length.CompareTo(a.Pts.Length));
		var kept = new List<FlowLine>(lines.Count);
		var pointBuckets = new Dictionary<int, List<Vector3>>(4096);
		float cosLimit = Mathf.Cos(GridArc * MergeRadiusCells);

		foreach (var line in lines)
		{
			int near = 0;
			for (int i = 0; i < line.Pts.Length; i++)
				if (HasNearbyPoint(pointBuckets, line.Cells[i], line.Pts[i], cosLimit)) near++;

			if (near > line.Pts.Length * MergeOverlap) continue;

			kept.Add(line);
			for (int i = 0; i < line.Pts.Length; i++)
			{
				int cell = line.Cells[i];
				if (!pointBuckets.TryGetValue(cell, out var bucket))
					pointBuckets[cell] = bucket = new List<Vector3>(4);
				bucket.Add(line.Pts[i]);
			}
		}
		return kept;
	}

	bool HasNearbyPoint(Dictionary<int, List<Vector3>> pointBuckets, int cell, Vector3 p, float cosLimit)
	{
		if (pointBuckets.TryGetValue(cell, out var own) && ContainsNear(own, p, cosLimit)) return true;
		foreach (int nb in Neighbors[cell])
			if (pointBuckets.TryGetValue(nb, out var bucket) && ContainsNear(bucket, p, cosLimit)) return true;
		return false;
	}

	static bool ContainsNear(List<Vector3> points, Vector3 p, float cosLimit)
	{
		foreach (var q in points)
			if (p.Dot(q) > cosLimit) return true;
		return false;
	}
}
