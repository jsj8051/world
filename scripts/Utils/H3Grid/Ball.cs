using System;
using System.Collections.Generic;
using System.Linq;
using World.Utils;
using World.Utils.H3;
using Godot;

// H3 球面网格数据层（纯数据可单测）：res 网格静态拓扑与几何，一次构建永久只读。
// 消费方（世界生成 / 表现层）持只读引用：场数组按下标与 CellIds 对齐。
//
// ★命名沿革：
//   2026-10-06 E 步：`new_HexWorld/Ball` → `Spatial/Ball`，ns `World.NewHexWorld` → `World.Spatial`；
//   2026-10-09：`Spatial/Ball` → `H3Grid/`，ns `World.Spatial` → **`World.H3Grid`**。
//     改名理由：`World.Spatial` 与另两个同族名易混——`SpatialScale`（尺度口径，`World.Logic`）、
//     `FinalSpatialIndex`（空间查询索引，`World.Logic`）；而本命名空间的真实身份是
//     **H3 球面网格本体**（`Ball` 也不是"球"）。新名与 `Utils/H3`（`World.Utils.H3`）呼应。
namespace World.H3Grid
{
	public class Ball
	{
		// ── 字段（全部 private，构建期写入，之后只读）──
		ulong[] _cellIds;                       // 全部格子 id（面，长度 N = 2+120·7^res）
		BallVertices _vertices;                 // 顶点侧（角）：id 表 + 坐标 + 反查（2026-10-11 内聚成对象）
		Vector3[] _cellCenters;                 // 格心坐标（与 _cellIds 一一对应）
		Vector3[] _cellDirs;                    // 格心单位方向（= 格心归一化；构造期算一次共享，见 BuildCellDirs）
		int[][] _cellNeighbors;                 // 邻接表：每格一串邻居下标（六边形 6 / 五边形 5）
		Dictionary<ulong, int> _cellIdToIndex;     // 格 id → 格数组下标

		// ── public 只读访问面 ──
		public ulong[] CellIds => _cellIds;
		/// <summary>顶点侧（角）：唯一顶点表 / 坐标 / id 反查。
		/// ★生产侧消费者**只有 `H3TerrainSampler`**（渲染取角点走 `CellQuery` 现算，不用本表）。</summary>
		public BallVertices Vertices => _vertices;
		public Vector3[] CellCenters => _cellCenters;
		public Vector3[] CellDirs => _cellDirs;
		public int[][] CellNeighbors => _cellNeighbors;
		public BallGeoIndex Geo { get; }   // 逐格经纬度索引（CellDirs 的纯函数缓存，同生共死）
		public int Res { get; }   // 分辨率档（拾取 LatLngToCell / 重染用，构造即定）
		public float Radius { get; }   // 球半径（格心/顶点坐标同尺度；边界链浮起等派生几何用）

		public Ball(int res, float radius)
		{
			Res = res;
			Radius = radius;
			BuildCellIds(res);
			BuildVertexIds(radius);
			BuildCellCenters(radius);
			BuildCellDirs();
			BuildCellIdToIndex();
			BuildNeighbors();      // 依赖格 id → 下标字典，必须排在最后
			Geo = new BallGeoIndex(this);   // 依赖 CellDirs，排在它之后
		}

		// 全球取格：122 个基底格各自的子孙拼接（总数 = GetNumCells，H3ApiTests 已断言）。
		void BuildCellIds(int res) =>
			_cellIds = H3.GetRes0Cells().SelectMany(b => H3.CellToChildren(b, res)).ToArray();

		// 顶点侧（角）一次性内聚构建：全唯一顶点去重 + 排序（→ 确定性）+ 坐标 + 反查
		// （三项同生共死，收进 `BallVertices`；原先是三个字段 + 三个分别的 Build 方法）。
		void BuildVertexIds(float radius) =>
			_vertices = new BallVertices(
				_cellIds.SelectMany(H3.CellToVertexes).Distinct().OrderBy(x => x).ToArray(), radius);

		// 格心坐标：cell id → 经纬度 → ECEF × radius（内容模拟的 Voronoi 距离 / 板块旋转都查它）。
		void BuildCellCenters(float radius) =>
			_cellCenters = _cellIds
				.Select(c => CoordUtil.LatLngToSphere(H3.CellToLatLng(c), radius)).ToArray();

		// 格心单位方向：运动学边界法线 / 平流流向 / 降水纬度带等热路径每格都要"格心归一化"，
		// 而半径是常量 ⇒ 方向永不变化，构造期归一一次全场共享。res5 下每步可省 ~2×10⁷ 次
		// sqrt+除法（原先边界法线一处就对每个邻居重复归一化同一格心）。
		void BuildCellDirs()
		{
			_cellDirs = new Vector3[_cellCenters.Length];
			for (int i = 0; i < _cellCenters.Length; i++) _cellDirs[i] = _cellCenters[i].Normalized();
		}

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

		// 顶点 id → 顶点数组下标（★转调 `Vertices`：顶点侧的存取与反查全归那一处；
		// 保留本口是为了"消费方仍只认 Ball"——想解耦才需要动持有关系，那不在本次范围）。
		public int VertexIndexOf(ulong vertexId) => _vertices.IndexOf(vertexId);

		// 格 id → 格数组下标（显示层按 cell id 查内容场用；查不到即抛）。
		public int CellIndexOf(ulong cellId)
		{
			if (_cellIdToIndex.TryGetValue(cellId, out int idx)) return idx;
			throw new InvalidOperationException($"格子 {H3.H3ToString(cellId)} 不在格子表");
		}
	}
}
