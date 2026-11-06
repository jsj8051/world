using System;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 板块边界的逐边快照：由几何（<see cref="PlateBoundarySet"/>）+ 板块表 + 逐格壳型算出的
/// 运动 / 类型 / 两侧角色。⓪ 阶段末算一次，① 只读。
/// 所有场都是 <see cref="Build"/> 的纯函数产物（无随机），可随时按新参数重建。
/// </summary>
public sealed class PlateBoundaryFields
{
	/// <summary>空场（未建边界时的占位；参与长度校验，不参与计算）。</summary>
	public static readonly PlateBoundaryFields Empty = new(
		Array.Empty<float>(), Array.Empty<BoundaryKind>(),
		Array.Empty<BoundarySideRole>(), Array.Empty<BoundarySideRole>());

	/// <summary>逐边法向相对速率（按边的规范方向 CellA→CellB 计算）。</summary>
	public float[] EdgeMotion { get; }
	/// <summary>逐边类型（汇聚 / 张裂 / 走滑，阈值与山带共用 <see cref="PlateBoundaryOps.ActiveMotion"/>）。</summary>
	public BoundaryKind[] EdgeKind { get; }
	/// <summary>逐边 A 侧角色（CellA 所在侧；非汇聚 = None）。</summary>
	public BoundarySideRole[] EdgeRoleA { get; }
	/// <summary>逐边 B 侧角色（CellB 所在侧）。</summary>
	public BoundarySideRole[] EdgeRoleB { get; }

	PlateBoundaryFields(float[] edgeMotion, BoundaryKind[] edgeKind, BoundarySideRole[] edgeRoleA,
		BoundarySideRole[] edgeRoleB)
	{
		EdgeMotion = edgeMotion;
		EdgeKind = edgeKind;
		EdgeRoleA = edgeRoleA;
		EdgeRoleB = edgeRoleB;
	}

	/// <summary>由边界集、板块表与逐格壳型算逐边事实。入参须同属一次板块划分。</summary>
	public static PlateBoundaryFields Build(Ball ball, PlateBoundarySet boundaries,
		CrustField crust, Plate[] plates)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (boundaries == null) throw new ArgumentNullException(nameof(boundaries));
		if (crust == null) throw new ArgumentNullException(nameof(crust));
		if (plates == null) throw new ArgumentNullException(nameof(plates));

		int n = ball.CellDirs.Length;
		if (boundaries.CellEdgeStart.Length != n + 1)
			throw new ArgumentException("边界集与球壳格数不一致", nameof(boundaries));

		int edgeCount = boundaries.Edges.Length;
		var edgeMotion = new float[edgeCount];
		var edgeKind = new BoundaryKind[edgeCount];
		var edgeRoleA = new BoundarySideRole[edgeCount];
		var edgeRoleB = new BoundarySideRole[edgeCount];
		var dirs = ball.CellDirs;
		Parallel.For(0, edgeCount, k =>
		{
			PlateBoundaryEdge edge = boundaries.Edges[k];
			float motion = PlateBoundaryOps.MotionOfEdge(dirs[edge.CellA], dirs[edge.CellB],
				plates[edge.PlateA], plates[edge.PlateB]);
			BoundaryKind kind = PlateBoundaryOps.KindOf(motion);
			bool densityRides = PlateBoundaryOps.Overrides(edge.PlateA, edge.PlateB, plates);
			bool aRides = PlateBoundaryOps.Rides(crust.Type[edge.CellA], crust.Type[edge.CellB], densityRides);
			bool bRides = PlateBoundaryOps.Rides(crust.Type[edge.CellB], crust.Type[edge.CellA], !densityRides);
			edgeMotion[k] = motion;
			edgeKind[k] = kind;
			edgeRoleA[k] = kind == BoundaryKind.Convergent
				? (aRides ? BoundarySideRole.Overriding : BoundarySideRole.Underthrusting)
				: BoundarySideRole.None;
			edgeRoleB[k] = kind == BoundaryKind.Convergent
				? (bRides ? BoundarySideRole.Overriding : BoundarySideRole.Underthrusting)
				: BoundarySideRole.None;
		});

		return new PlateBoundaryFields(edgeMotion, edgeKind, edgeRoleA, edgeRoleB);
	}
}
