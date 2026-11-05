using System;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 板块边界的运动学快照：由几何（<see cref="PlateBoundarySet"/>）+ 板块表算出的逐格 / 逐边场。
/// ⓪ 阶段末算一次，① 只读——① 不再自己跑两遍全局 BFS，也不重复判“是不是边界”。
/// 所有场都是 <see cref="Build"/> 的纯函数产物（无随机），可随时按新参数重建。
/// </summary>
public sealed class PlateBoundaryFields
{
	/// <summary>空场（未建边界时的占位；参与长度校验，不参与计算）。</summary>
	public static readonly PlateBoundaryFields Empty = new(
		Array.Empty<float>(), Array.Empty<BoundaryRole>(), Array.Empty<int>(),
		Array.Empty<float>(), Array.Empty<BoundaryKind>());

	/// <summary>逐格边界法向相对速率（&lt; 0 汇聚 / &gt; 0 张裂；内部格由最近边界扩散而来）。</summary>
	public float[] CellMotion { get; }
	/// <summary>逐格汇聚边界角色（非汇聚侧的格 = <see cref="BoundaryRole.None"/>）。</summary>
	public BoundaryRole[] CellRole { get; }
	/// <summary>逐格到最近板块边界的跳数（边界格 = 0）。</summary>
	public int[] CellHops { get; }
	/// <summary>逐边法向相对速率（按边的规范方向 CellA→CellB 计算）。</summary>
	public float[] EdgeMotion { get; }
	/// <summary>逐边类型（汇聚 / 张裂 / 走滑，阈值与山带共用 <see cref="PlateBoundaryOps.ActiveMotion"/>）。</summary>
	public BoundaryKind[] EdgeKind { get; }

	PlateBoundaryFields(float[] cellMotion, BoundaryRole[] cellRole, int[] cellHops,
		float[] edgeMotion, BoundaryKind[] edgeKind)
	{
		CellMotion = cellMotion;
		CellRole = cellRole;
		CellHops = cellHops;
		EdgeMotion = edgeMotion;
		EdgeKind = edgeKind;
	}

	/// <summary>由边界集与板块表算全部场。入参须同属一次板块划分。</summary>
	public static PlateBoundaryFields Build(Ball ball, PlateBoundarySet boundaries, int[] plateOfCell,
		Plate[] plates)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (boundaries == null) throw new ArgumentNullException(nameof(boundaries));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (plates == null) throw new ArgumentNullException(nameof(plates));

		int n = ball.CellDirs.Length;
		if (boundaries.CellEdgeStart.Length != n + 1)
			throw new ArgumentException("边界集与球壳格数不一致", nameof(boundaries));
		if (plateOfCell.Length != n)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 格数 {n}", nameof(plateOfCell));

		float[] cellMotion = PlateBoundaryOps.Motion(ball, boundaries, plates);
		BoundaryRole[] cellRole = PlateBoundaryOps.Roles(ball, boundaries, plateOfCell, plates);
		int[] cellHops = PlateBoundaryOps.Hops(ball, boundaries);

		int edgeCount = boundaries.Edges.Length;
		var edgeMotion = new float[edgeCount];
		var edgeKind = new BoundaryKind[edgeCount];
		var dirs = ball.CellDirs;
		Parallel.For(0, edgeCount, k =>
		{
			PlateBoundaryEdge edge = boundaries.Edges[k];
			float motion = PlateBoundaryOps.MotionOfEdge(dirs[edge.CellA], dirs[edge.CellB],
				plates[edge.PlateA], plates[edge.PlateB]);
			edgeMotion[k] = motion;
			edgeKind[k] = PlateBoundaryOps.KindOf(motion);
		});

		return new PlateBoundaryFields(cellMotion, cellRole, cellHops, edgeMotion, edgeKind);
	}
}
