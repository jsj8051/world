using System;
using Godot;
using World.Logic;

namespace World.Tests;

/// <summary>地形阶段的地表口径：自由板偏置（离洋壳越远越高，海岸带不加，洋壳格恒 0）。</summary>
static class TerrainTests
{
	/// <summary>10° 弧长（km）——单测里用固定格距，不依赖 H3。</summary>
	static float EdgeKm => 6371f * 10f * MathF.PI / 180f;

	static Vector3 At(float deg)
	{
		float r = deg * MathF.PI / 180f;
		return new Vector3(MathF.Cos(r), 0f, MathF.Sin(r));
	}

	/// <summary>一条经线上的 5 格：0、1 是洋壳，2、3、4 是陆壳。</summary>
	static readonly Vector3[] Line = { At(0f), At(10f), At(20f), At(30f), At(40f) };
	static readonly int[][] LineNeighbors = { new[] { 1 }, new[] { 0, 2 }, new[] { 1, 3 }, new[] { 2, 4 }, new[] { 3 } };
	static readonly bool[] CoastalLine = { false, false, true, true, true };

	/// <summary>洋壳格不加，陆壳格随离洋壳距离单调递增，斜坡尽头取满值。</summary>
	public static void FreeboardRisesInland()
	{
		const float span = 3000f, liftM = 12f;
		float[] lift = FreeboardOps.Build(Line, LineNeighbors, CoastalLine, span, liftM);

		Program.True(lift[0] == 0f && lift[1] == 0f, "洋壳格不该有偏置");
		Program.True(lift[2] > 0f && lift[2] < lift[3], $"偏置应随离洋壳距离递增，实得 {lift[2]} / {lift[3]}");
		Program.True(lift[3] < lift[4], $"偏置应随离洋壳距离递增，实得 {lift[3]} / {lift[4]}");
		Program.Near(lift[4], liftM, 1e-4, "斜坡尽头取满值");

		float u = EdgeKm / span;
		Program.Near(lift[2], liftM * u * u * (3f - 2f * u), 0.01, "斜坡内的 smoothstep 口径");
	}

	/// <summary>偏置量或斜坡长为 0 ⇒ 全 0（关断）；全陆世界没有海岸 ⇒ 也全 0。</summary>
	public static void FreeboardGoesSilent()
	{
		foreach (float[] off in new[]
		{
			FreeboardOps.Build(Line, LineNeighbors, CoastalLine, 3000f, 0f),
			FreeboardOps.Build(Line, LineNeighbors, CoastalLine, 0f, 12f),
			FreeboardOps.Build(Line, LineNeighbors, new[] { true, true, true, true, true }, 3000f, 12f),
		})
		{
			foreach (float v in off)
				Program.True(v == 0f, $"关断时应全 0，实得 {v}");
		}
	}

	/// <summary>陆壳掩码长度不符 ⇒ 抛（不做静默截断）。</summary>
	public static void FreeboardRejectsWrongMaskLength()
	{
		Program.Throws(() => FreeboardOps.Build(Line, LineNeighbors, new[] { true, false }, 3000f, 12f),
			"陆壳掩码长度不符");
	}
}
