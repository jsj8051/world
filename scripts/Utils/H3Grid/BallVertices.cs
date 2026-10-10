using System;
using System.Collections.Generic;
using System.Linq;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils.H3;

namespace World.H3Grid;

/// <summary>
/// **顶点侧**（H3 球壳的"角"）：唯一顶点表 + 球面坐标 + id→下标反查。
///
/// ★2026-10-11 从 `Ball` 内聚出来（用户问"这些字段有必要拆类吗" → 逐个核消费面后的结论）：
///   `Ball` 的成员其实分两类实体——**格**（N 个，被 Logic 十余文件消费）与
///   **顶点**（2N−4 个，生产侧消费者**只有 `H3TerrainSampler` 一个**）。
///   顶点这三项（id 表 / 坐标 / 反查）**同生共死、永远一起用** ⇒ 收成一个对象后：
///   `Ball` 的公共面少 3 项，且"顶点只服务采样器"这件事在类型上可见。
///
/// ⚠️ **不减少耦合**：消费者本来也都经 `Ball` 拿到它们。本类只做内聚，不做解耦——
///   想解耦得动持有关系，那是另一件事。`Ball` 仍是唯一构造者与持有者（同生共死）。
///
/// ★端点数是**派生量**（`2N−4`），由格角点去重而来，故由 <see cref="Ball"/> 构造时灌入，
///   本类不自算（顶点与格的对应关系只能从 H3 角点查得，那是 `Ball` 的活）。
/// </summary>
public sealed class BallVertices
{
	/// <summary>全部唯一顶点 id（升序 —— 排序保证网格可复现，确定性纪律）。</summary>
	public ulong[] Ids { get; }

	/// <summary>顶点球面坐标（与 <see cref="Ids"/> 一一对应；半径 = `Ball.Radius` 同尺度）。</summary>
	public Vector3[] Positions { get; }

	readonly Dictionary<ulong, int> _idToIndex;

	/// <param name="ids">升序的唯一顶点 id（由 `Ball` 去重后灌入）。</param>
	/// <param name="radius">球半径（与格心同尺度）。</param>
	internal BallVertices(ulong[] ids, float radius)
	{
		Ids = ids ?? throw new ArgumentNullException(nameof(ids));
		Positions = ids.Select(v => World.Utils.CoordUtil.LatLngToSphere(H3.VertexToLatLng(v), radius)).ToArray();
		_idToIndex = ids.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);
	}

	/// <summary>顶点数（= <see cref="Ids"/>.Length；便捷口）。</summary>
	public int Count => Ids.Length;

	/// <summary>
	/// 顶点 id → 顶点下标（**查不到即抛**：id 域错误要当场暴露，不静默）。
	/// ★字典而非二分：实测 20 万次查询 &lt;1ms（**查询不是瓶颈**），换二分是纯亏（2026-10-11 量过）。
	/// </summary>
	public int IndexOf(ulong vertexId)
	{
		if (_idToIndex.TryGetValue(vertexId, out int idx)) return idx;
		throw new InvalidOperationException($"顶点 {H3.H3ToString(vertexId)} 不在共享顶点表");
	}

	/// <summary>顶点 id → 球面坐标（查不到即抛）。采样器最常用的组合口。</summary>
	public Vector3 PositionOf(ulong vertexId) => Positions[IndexOf(vertexId)];
}
