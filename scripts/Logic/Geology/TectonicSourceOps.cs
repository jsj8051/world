using System;
using System.Collections.Generic;
using World.Data;

namespace World.Logic;

/// <summary>
/// 逐边构造来源分类：每条活动边在**产生构造响应的那一侧**产出一个 <see cref="TectonicSource"/>。
/// 通道由运动类型与**该侧格的壳型**决定；上下盘规则与 <see cref="PlateBoundaryOps.Overrides"/> 相同。
/// </summary>
public static class TectonicSourceOps
{
	/// <summary>顺序 = 边扫描序 × (A 侧, B 侧)，确定性。</summary>
	public static TectonicSource[] Build(PlateBoundarySet boundaries, Plate[] plates,
		CrustField crust, float[] edgeMotion)
	{
		if (boundaries == null) throw new ArgumentNullException(nameof(boundaries));
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		if (crust == null) throw new ArgumentNullException(nameof(crust));
		if (edgeMotion == null) throw new ArgumentNullException(nameof(edgeMotion));

		int edgeCount = boundaries.Edges.Length;
		if (edgeMotion.Length != edgeCount)
			throw new ArgumentException($"逐边运动长度 {edgeMotion.Length} ≠ 边数 {edgeCount}", nameof(edgeMotion));

		var sources = new List<TectonicSource>(edgeCount * 2);
		for (int k = 0; k < edgeCount; k++)
		{
			float motion = edgeMotion[k];
			if (!float.IsFinite(motion))
				throw new ArgumentException($"边 {k} 的运动不是有限值：{motion}", nameof(edgeMotion));

			BoundaryKind kind = PlateBoundaryOps.KindOf(motion);
			if (kind == BoundaryKind.Transform) continue;

			PlateBoundaryEdge edge = boundaries.Edges[k];
			RequireCell(edge.CellA, crust.Type.Length);
			RequireCell(edge.CellB, crust.Type.Length);
			RequirePlate(edge.PlateA, plates.Length, k);
			RequirePlate(edge.PlateB, plates.Length, k);

			bool aRides = PlateBoundaryOps.Overrides(edge.PlateA, edge.PlateB, plates);
			if (TryChannelOf(kind, crust.Type[edge.CellA], crust.Type[edge.CellB], aRides,
				out TectonicChannel channelA))
				sources.Add(new TectonicSource(k, edge.CellA, channelA));
			if (TryChannelOf(kind, crust.Type[edge.CellB], crust.Type[edge.CellA], !aRides,
				out TectonicChannel channelB))
				sources.Add(new TectonicSource(k, edge.CellB, channelB));
		}
		return sources.ToArray();
	}

	/// <summary>
	/// 本侧通道。汇聚按两侧壳型分碰撞 / 弧 / 俯冲；张裂只在两侧都陆壳时成裂谷，
	/// 陆洋张裂的陆侧是被动边缘（不产通道），洋侧成洋中脊。
	/// </summary>
	public static bool TryChannelOf(BoundaryKind kind, CrustType mine, CrustType other,
		bool mineRides, out TectonicChannel channel)
	{
		bool mineContinental = mine == CrustType.Continental;
		bool otherContinental = other == CrustType.Continental;

		switch (kind)
		{
			case BoundaryKind.Divergent:
				if (mineContinental && otherContinental) { channel = TectonicChannel.ContinentalRift; return true; }
				if (!mineContinental) { channel = TectonicChannel.OceanicRidge; return true; }
				channel = default;
				return false;
			case BoundaryKind.Convergent:
				if (mineContinental && otherContinental) { channel = TectonicChannel.ContinentalCollision; return true; }
				if (mineContinental != otherContinental)
				{
					channel = mineContinental ? TectonicChannel.ContinentalArc : TectonicChannel.Subduction;
					return true;
				}
				channel = mineRides ? TectonicChannel.OceanicArc : TectonicChannel.Subduction;
				return true;
			default:
				channel = default;
				return false;
		}
	}

	/// <summary>该格最近源的活动强度 ∈ [0,1]；无源 = 0。</summary>
	public static float StrengthAt(TectonicInfluenceField field, int cell, float[] edgeMotion)
	{
		if (field.SourceIndex.Length == 0) return 0f;
		int source = field.SourceIndex[cell];
		if (source < 0) return 0f;
		float motion = edgeMotion[field.Sources[source].EdgeIndex];
		return MathF.Min(1f, MathF.Abs(motion) / PlateBoundaryOps.MotionScale);
	}

	static void RequireCell(int cell, int count)
	{
		if ((uint)cell >= (uint)count)
			throw new ArgumentOutOfRangeException(nameof(cell), $"格号 {cell} 越界（壳型长度 {count}）");
	}

	static void RequirePlate(int plate, int count, int edgeIndex)
	{
		if ((uint)plate >= (uint)count)
			throw new ArgumentOutOfRangeException(nameof(plate),
				$"边 {edgeIndex} 的板块号 {plate} 越界（板块数 {count}）");
	}
}
