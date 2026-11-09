using System;
using System.Diagnostics;

namespace World.Logic;

/// <summary>可选的世界生成诊断计时。生产运行默认关闭。</summary>
internal static class WorldBuildProfile
{
	static readonly bool Enabled = Environment.GetEnvironmentVariable("WORLD_BUILD_PROFILE") == "1";

	public static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

	public static void Mark(ref long start, string stage)
	{
		if (start == 0) return;
		long now = Stopwatch.GetTimestamp();
		Console.WriteLine(FormattableString.Invariant(
			$"[WORLD-PROFILE] {stage}={Stopwatch.GetElapsedTime(start, now).TotalMilliseconds:F1} ms"));
		start = now;
	}
}
