using System;
using System.Collections.Generic;
using System.Linq;
using World.Utils;
using World.Utils.H3;
using Godot;

namespace World.NewHexWorld
{
	// H3 球面网格数据层（纯数据可单测）：res 网格静态拓扑与几何，一次构建永久只读。
	// 内容模拟（H3Plate）持只读引用：场数组按下标与 CellIds 对齐。
	public class Ball
	{
		// ── 字段（全部 private，构建期写入，之后只读）──
		ulong[] _cellIds;                       // 全部格子 id（面，长度 N = 2+120·7^res）
		ulong[] _vertexIds;                     // 全部唯一顶点 id（角，长度 2N−4，排序 → 确定性）
		Vector3[] _vertexPositions;             // 顶点坐标（与 _vertexIds 一一对应）
		Vector3[] _cellCenters;                 // 格心坐标（与 _cellIds 一一对应）
		int[][] _cellNeighbors;                 // 邻接表：每格一串邻居下标（六边形 6 / 五边形 5）
		Dictionary<ulong, int> _vertexIdToIndex;   // 顶点 id → 顶点数组下标
		Dictionary<ulong, int> _cellIdToIndex;     // 格 id → 格数组下标

		// ── public 只读访问面 ──
		public ulong[] CellIds => _cellIds;
		public ulong[] VertexIds => _vertexIds;
		public Vector3[] VertexPositions => _vertexPositions;
		public Vector3[] CellCenters => _cellCenters;
		public int[][] CellNeighbors => _cellNeighbors;
		public int Res { get; }   // 分辨率档（拾取 LatLngToCell / 重染用，构造即定）
		public float Radius { get; }   // 球半径（格心/顶点坐标同尺度；边界链浮起等派生几何用）

		public Ball(int res, float radius)
		{
			Res = res;
			Radius = radius;
			BuildCellIds(res);
			BuildVertexIds();
			BuildVertexPositions(radius);
			BuildCellCenters(radius);
			BuildVertexIdToIndex();
			BuildCellIdToIndex();
			BuildNeighbors();      // 依赖格 id → 下标字典，必须排在最后
		}

		// 全球取格：122 个基底格各自的子孙拼接（总数 = GetNumCells，H3ApiTests 已断言）。
		void BuildCellIds(int res) =>
			_cellIds = H3.GetRes0Cells().SelectMany(b => H3.CellToChildren(b, res)).ToArray();

		// 全唯一顶点：所有格角点去重（HashSet 语义）后排序 → id 表可复现（确定性网格纪律）。
		void BuildVertexIds() =>
			_vertexIds = _cellIds.SelectMany(H3.CellToVertexes).Distinct().OrderBy(x => x).ToArray();

		// 顶点坐标：vertex id → 经纬度 → ECEF × radius。
		void BuildVertexPositions(float radius) =>
			_vertexPositions = _vertexIds
				.Select(v => CoordUtil.LatLngToSphere(H3.VertexToLatLng(v), radius)).ToArray();

		// 格心坐标：cell id → 经纬度 → ECEF × radius（内容模拟的 Voronoi 距离 / 板块旋转都查它）。
		void BuildCellCenters(float radius) =>
			_cellCenters = _cellIds
				.Select(c => CoordUtil.LatLngToSphere(H3.CellToLatLng(c), radius)).ToArray();

		// 顶点 id → 顶点下标映射（BallMesh 画格子取角点坐标用）。
		void BuildVertexIdToIndex() =>
			_vertexIdToIndex = _vertexIds.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);

		// 格 id → 格下标映射（邻接构建 + 显示层按 cell id 查内容场用）。
		void BuildCellIdToIndex() =>
			_cellIdToIndex = _cellIds.Select((id, i) => (id, i)).ToDictionary(t => t.id, t => t.i);

		// 邻接表构建：逐格 GridDisk(k=1)（含自身）→ 剔自身 → 按 id 排序（H3 不保证输出顺序，
		// 排序保证确定性）→ 字典翻译成下标。这张表是内容模拟一切邻域运算的地基。
		void BuildNeighbors()
		{
			_cellNeighbors = new int[_cellIds.Length][];
			for (int i = 0; i < _cellIds.Length; i++)
			{
				ulong cell = _cellIds[i];
				_cellNeighbors[i] = H3.GridDisk(cell, 1)
					.Where(n => n != cell)
					.OrderBy(n => n)
					.Select(n => _cellIdToIndex[n])
					.ToArray();
			}
		}

		// 顶点 id → 顶点数组下标（查不到即抛：id 域错误要当场暴露，不静默）。
		public int VertexIndexOf(ulong vertexId)
		{
			if (_vertexIdToIndex.TryGetValue(vertexId, out int idx)) return idx;
			throw new InvalidOperationException($"顶点 {H3.H3ToString(vertexId)} 不在共享顶点表");
		}

		// 格 id → 格数组下标（显示层按 cell id 查内容场用；查不到即抛）。
		public int CellIndexOf(ulong cellId)
		{
			if (_cellIdToIndex.TryGetValue(cellId, out int idx)) return idx;
			throw new InvalidOperationException($"格子 {H3.H3ToString(cellId)} 不在格子表");
		}
	}
}
