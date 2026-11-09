using System;
using System.Collections.Generic;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 构造影响场：按通道分别做多源 Dijkstra，求逐格到最近边界侧的距离与来源编号。
/// 距离只在**本侧板块且同壳型**内传播 ⇒ 构造带不会漏到对侧，也不会从陆壳爬到同板块的洋壳；
/// 同一通道的多条边界共用一张场，最近来源胜出；不同通道互不影响。
/// </summary>
public static class TectonicInfluenceOps
{
	internal const double WorldSearchRadiusKm = 4000;
	static readonly int ChannelCount = Enum.GetValues<TectonicChannel>().Length;

	/// <param name="edgeMotion">逐边法向相对速率（与 <c>boundaries.Edges</c> 同序）。</param>
	/// <param name="capKm">搜索截断半径（km）：超出即停，够不到的格 <c>SourceIndex = −1</c>、距离 = <paramref name="capKm"/>。</param>
	public static TectonicInfluenceSet Build(Ball ball, PlateBoundarySet boundaries, Plate[] plates,
		CrustField crust, int[] plateOfCell, float[] edgeMotion, double capKm, PlateBoundaryFields classified = null,
		IReadOnlyList<CrustType> surfaceTypes = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (crust == null) throw new ArgumentNullException(nameof(crust));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));

		int n = ball.CellDirs.Length;
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));
		if (crust.Type.Length != n)
			throw new ArgumentException($"壳型长度 {crust.Type.Length} ≠ 格数 {n}", nameof(crust));
		surfaceTypes ??= crust.Type;
		if (surfaceTypes.Count != n)
			throw new ArgumentException($"表层壳型长度 {surfaceTypes.Count} ≠ 格数 {n}", nameof(surfaceTypes));

		TectonicSource[] sources = TectonicSourceOps.Build(boundaries, plates, crust, edgeMotion, classified);

		var perChannel = new List<TectonicSource>[ChannelCount];
		foreach (TectonicSource source in sources)
		{
			int c = (int)source.Channel;
			(perChannel[c] ??= new List<TectonicSource>()).Add(source);
		}

		var fields = new TectonicInfluenceField[ChannelCount];
		for (int c = 0; c < ChannelCount; c++)
		{
			var channel = (TectonicChannel)c;
			List<TectonicSource> list = perChannel[c];
			if (list == null || list.Count == 0)
			{
				fields[c] = TectonicInfluenceField.Empty(channel);
				continue;
			}

			var seeds = new DistanceFieldOps.Seed[list.Count];
			for (int i = 0; i < list.Count; i++)
				seeds[i] = new DistanceFieldOps.Seed(list[i].SideCell, i, 0.0);

			DistanceFieldOps.SeedField field = DistanceFieldOps.BuildSeeds(ball, seeds, capKm,
				(i, j) => plateOfCell[j] == plateOfCell[i] && surfaceTypes[j] == surfaceTypes[i]);
			fields[c] = new TectonicInfluenceField(channel, list.ToArray(), field.DistKm, field.SeedId);
		}

		return new TectonicInfluenceSet(fields);
	}
}
