// H3.cs —— Uber H3（v4.5.0）托管门面：把 C 库 P/Invoke 收成一套对游戏 C# 友好的静态 API。
//
// 设计要点：
//   * 坐标约定与 h3api.h 完全一致——LatLng 单位是【弧度】。需要度数时自行换算（1° = π/180）。
//   * 与 C API 同名的取单元格 / 邻居 / 层级 / 压缩等方法，只是去掉了错误码返回值：
//     出错即抛 InvalidOperationException（附 H3 的错误描述），正常路径无歧义。
//   * 所有返回数组都已剔除 0 空槽（H3 用 0 表示"不存在"，五边形卷积时可能出现在
//    数组任意位置，不只是尾部）。
//
// 既有的底层声明见 H3Native.cs；原生库本体在 native/h3/h3.dll，重编译见 native/h3/rebuild.sh。

using System;

namespace World.Utils.H3
{
    /// <summary>经纬度坐标，单位【弧度】，与 h3api.h 的 LatLng 一致（lat=纬度, lng=经度）。</summary>
    public readonly record struct LatLng(double Lat, double Lng);

    /// <summary>
    /// Uber H3 六边形层级索引库的托管入口（静态门面）。
    /// H3 把球面（以 icosahedron 为基底）切分成层级分明的六边形网格，分辨率 0–15，
    /// 索引是 64 位无符号整数：低层大格、高层小格，每级 parent 恰好覆盖 7 个 child
    /// （1 个中心 + 6 个环绕；五边形除外，只有 1 中心 + 5 环绕 = 6 个 child）。
    /// </summary>
    public static class H3
    {
        /// <summary>无效索引（"NaN"）：latLngToCell 失败或数组空槽都用它。</summary>
        public const ulong Null = 0UL;

        /// <summary>最小分辨率（最大格子，122 个）。</summary>
        public const int MinRes = 0;

        /// <summary>最大分辨率（最小格子，分辨率 15 的格子边长约 0.5 公里）。</summary>
        public const int MaxRes = 15;

        /// <summary>每级子格数固定的父→子倍数（六边形 1→7；五边形 1→6）。</summary>
        public const int ChildrenPerHex = 7;

        // ================= 经纬度 ↔ 索引 =================

        /// <summary>求包含给定经纬度（弧度）的分辨率 res 格子索引。</summary>
        public static ulong LatLngToCell(LatLng g, int res)
        {
            CheckRes(res);
            uint err = H3Native.latLngToCell(ref g, res, out ulong cell);
            ThrowOnError(err, "LatLngToCell");
            return cell;
        }

        /// <summary>求格子中心点的经纬度（弧度）。</summary>
        public static LatLng CellToLatLng(ulong cell)
        {
            uint err = H3Native.cellToLatLng(cell, out LatLng g);
            ThrowOnError(err, "CellToLatLng");
            return g;
        }

        /// <summary>求格子边界顶点（逆时针，弧度）。六边形 6 个、五边形 5 个。</summary>
        public static LatLng[] CellToBoundary(ulong cell)
        {
            uint err = H3Native.cellToBoundary(cell, out H3Native.CellBoundary b);
            ThrowOnError(err, "CellToBoundary");
            return b.TakeVerts();
        }

        // ================= 邻居 / 距离 / 路径 =================

        /// <summary>k-ring：距离 origin 在 k 步以内的所有格子（含自身；安全版自动绕过五边形畸变）。
        /// 注意：结果顺序无定义（C 端按哈希集合写），且穿越五边形时内部可能有 0 空槽，已整体剔除。</summary>
        public static ulong[] GridDisk(ulong origin, int k)
        {
            CheckRadius(k);
            ulong[] buf = new ulong[MaxGridDiskSize(k)];
            uint err = H3Native.gridDisk(origin, k, buf);
            ThrowOnError(err, "GridDisk");
            return Trim(buf);
        }

        /// <summary>k-ring 不安全版：假设途经全是六边形、无五边形（更快；遇五边形畸变抛错）。</summary>
        public static ulong[] GridDiskUnsafe(ulong origin, int k)
        {
            CheckRadius(k);
            ulong[] buf = new ulong[MaxGridDiskSize(k)];
            uint err = H3Native.gridDiskUnsafe(origin, k, buf);
            ThrowOnError(err, "GridDiskUnsafe");
            return Trim(buf);
        }

        /// <summary>空心环：只取距离 origin 恰好 k 步的那一圈（k=1 即紧邻的 6 个邻居）。</summary>
        public static ulong[] GridRingUnsafe(ulong origin, int k)
        {
            CheckRadius(k);
            if (k == 0) return new[] { origin };
            H3Native.maxGridRingSize(k, out long size);
            ulong[] buf = new ulong[size];
            uint err = H3Native.gridRingUnsafe(origin, k, buf);
            ThrowOnError(err, "GridRingUnsafe");
            return Trim(buf);
        }

        /// <summary>两格之间的网格距离（单位 = 六边形步数，非地理距离）。</summary>
        public static long GridDistance(ulong a, ulong b)
        {
            uint err = H3Native.gridDistance(a, b, out long d);
            ThrowOnError(err, "GridDistance");
            return d;
        }

        /// <summary>两格之间的网格连线（含两端点），可用作插值/寻路骨架。</summary>
        public static ulong[] GridPathCells(ulong start, ulong end)
        {
            uint err1 = H3Native.gridPathCellsSize(start, end, out long size);
            ThrowOnError(err1, "GridPathCellsSize");
            ulong[] buf = new ulong[size];
            uint err2 = H3Native.gridPathCells(start, end, buf);
            ThrowOnError(err2, "GridPathCells");
            return buf;
        }

        // ================= 层级（parent / children） =================

        /// <summary>求 parentRes 级的父格（parentRes 必须 ≤ 当前格子分辨率）。</summary>
        public static ulong CellToParent(ulong cell, int parentRes)
        {
            CheckRes(parentRes);
            uint err = H3Native.cellToParent(cell, parentRes, out ulong parent);
            ThrowOnError(err, "CellToParent");
            return parent;
        }

        /// <summary>求 childRes 级的所有子格（childRes 必须 ≥ 当前分辨率；六边形 7 个、五边形 6 个）。</summary>
        public static ulong[] CellToChildren(ulong cell, int childRes)
        {
            CheckRes(childRes);
            uint err1 = H3Native.cellToChildrenSize(cell, childRes, out long size);
            ThrowOnError(err1, "CellToChildrenSize");
            ulong[] buf = new ulong[size];
            uint err2 = H3Native.cellToChildren(cell, childRes, buf);
            ThrowOnError(err2, "CellToChildren");
            return buf;
        }

        /// <summary>求正中心的那个子格（对 LOD/居中采样很有用）。</summary>
        public static ulong CellToCenterChild(ulong cell, int childRes)
        {
            CheckRes(childRes);
            uint err = H3Native.cellToCenterChild(cell, childRes, out ulong child);
            ThrowOnError(err, "CellToCenterChild");
            return child;
        }

        // ================= 压缩 / 解压 =================

        /// <summary>把整组 child 合并回它们的共同父格（输入来自同一父格时返回单个父格）。已剔除空槽。</summary>
        public static ulong[] CompactCells(ulong[] cells)
        {
            if (cells == null || cells.Length == 0) return Array.Empty<ulong>();
            ulong[] outBuf = new ulong[cells.Length];
            uint err = H3Native.compactCells(cells, outBuf, cells.Length);
            ThrowOnError(err, "CompactCells");
            return Trim(outBuf);
        }

        /// <summary>把压缩后的父格集按 res 展开回完整子格集（与 CompactCells 互逆）。</summary>
        public static ulong[] UncompactCells(ulong[] compacted, int res)
        {
            CheckRes(res);
            if (compacted == null || compacted.Length == 0) return Array.Empty<ulong>();
            uint err1 = H3Native.uncompactCellsSize(compacted, compacted.Length, res, out long size);
            ThrowOnError(err1, "UncompactCellsSize");
            ulong[] outBuf = new ulong[size];
            uint err2 = H3Native.uncompactCells(compacted, compacted.Length, outBuf, size, res);
            ThrowOnError(err2, "UncompactCells");
            return outBuf;
        }

        // ================= 索引解析（元信息） =================

        /// <summary>分辨率（0–15）。</summary>
        public static int GetResolution(ulong cell) => H3Native.getResolution(cell);

        /// <summary>基底格编号（0–121）：20 个面 × 6 + 2 个极格。</summary>
        public static int GetBaseCellNumber(ulong cell) => H3Native.getBaseCellNumber(cell);

        /// <summary>是否为合法格子索引（六边形或五边形）。</summary>
        public static bool IsValidCell(ulong cell) => H3Native.isValidCell(cell) != 0;

        /// <summary>是否为五边形（全球只有 12 个五边形，随分辨率逐级继承）。</summary>
        public static bool IsPentagon(ulong cell) => H3Native.isPentagon(cell) != 0;

        /// <summary>格子的"分类"（Class III 为 true）。决定邻居/镜像方向语义，一般不必直接用到。</summary>
        public static bool IsResClassIII(ulong cell) => H3Native.isResClassIII(cell) != 0;

        /// <summary>两格是否共边（直接相邻）。</summary>
        public static bool AreNeighborCells(ulong a, ulong b)
        {
            uint err = H3Native.areNeighborCells(a, b, out int nb);
            ThrowOnError(err, "AreNeighborCells");
            return nb != 0;
        }

        // ================= 字符串 与 全局统计 =================

        /// <summary>把标准 15 位十六进制字符串解析成索引（如 "8928308280fffff"）。</summary>
        public static ulong StringToH3(string s)
        {
            uint err = H3Native.stringToH3(s, out ulong cell);
            ThrowOnError(err, "StringToH3");
            return cell;
        }

        /// <summary>把索引转成标准十六进制字符串（用于存档/调试）。</summary>
        public static string H3ToString(ulong cell)
        {
            // C 端要求 sz >= 17（16 位十六进制 + 终止符，按完整 uint64 计算）
            var sb = new System.Text.StringBuilder(17);
            uint err = H3Native.h3ToString(cell, sb, (nuint)sb.Capacity);
            ThrowOnError(err, "H3ToString");
            return sb.ToString();
        }

        /// <summary>res分辨率全星球格子总数（= 2 + 120 × 7^res）。</summary>
        public static long GetNumCells(int res)
        {
            CheckRes(res);
            H3Native.getNumCells(res, out long count);
            return count;
        }

        /// <summary>分辨率 0 的 122 个基底格。</summary>
        public static ulong[] GetRes0Cells()
        {
            ulong[] buf = new ulong[H3Native.res0CellCount()];
            uint err = H3Native.getRes0Cells(buf);
            ThrowOnError(err, "GetRes0Cells");
            return buf;
        }

        /// <summary>某分辨率下的 12 个五边形（五边形位置固定、逐级继承）。</summary>
        public static ulong[] GetPentagons(int res)
        {
            CheckRes(res);
            ulong[] buf = new ulong[H3Native.pentagonCount()];
            uint err = H3Native.getPentagons(res, buf);
            ThrowOnError(err, "GetPentagons");
            return buf;
        }

        // ================= 面积 / 边长（分辨率定性用） =================

        /// <summary>某格子的精确面积（平方公里；已区别五边形/六边形）。</summary>
        public static double CellAreaKm2(ulong cell)
        {
            uint err = H3Native.cellAreaKm2(cell, out double area);
            ThrowOnError(err, "CellAreaKm2");
            return area;
        }

        /// <summary>某分辨率六边形的平均面积（平方公里，不含五边形）。</summary>
        public static double GetHexagonAreaAvgKm2(int res)
        {
            CheckRes(res);
            uint err = H3Native.getHexagonAreaAvgKm2(res, out double area);
            ThrowOnError(err, "GetHexagonAreaAvgKm2");
            return area;
        }

        /// <summary>某分辨率六边形的平均边长（公里，不含五边形）。</summary>
        public static double GetHexagonEdgeLengthAvgKm(int res)
        {
            CheckRes(res);
            uint err = H3Native.getHexagonEdgeLengthAvgKm(res, out double len);
            ThrowOnError(err, "GetHexagonEdgeLengthAvgKm");
            return len;
        }

        // ================= 顶点（渲染多格共享无缝边界） =================

        /// <summary>格子所有顶点（六边形 6 个 / 五边形 5 个），每个是一个顶点索引（相邻格共享同一顶点 id）。</summary>
        public static ulong[] CellToVertexes(ulong cell)
        {
            ulong[] buf = new ulong[6];
            uint err = H3Native.cellToVertexes(cell, buf);
            ThrowOnError(err, "CellToVertexes");
            return Trim(buf);
        }

        /// <summary>顶点索引 → 经纬度（弧度）。</summary>
        public static LatLng VertexToLatLng(ulong vertex)
        {
            uint err = H3Native.vertexToLatLng(vertex, out LatLng p);
            ThrowOnError(err, "VertexToLatLng");
            return p;
        }

        // ================= 内部工具 =================

        /// <summary>k-ring 理论最大格子数（含五边形畸变余量）。</summary>
        public static long MaxGridDiskSize(int k)
        {
            uint err = H3Native.maxGridDiskSize(k, out long size);
            ThrowOnError(err, "MaxGridDiskSize");
            return size;
        }

        /// <summary>校验分辨率参数范围，非法直接抛。</summary>
        private static void CheckRes(int res)
        {
            if (res < MinRes || res > MaxRes)
                throw new ArgumentOutOfRangeException(nameof(res), res, $"分辨率必须在 {MinRes}–{MaxRes}");
        }

        /// <summary>校验 k-ring 半径（k=0 只含自身，k 过大导致数组溢出 int64 边界时抛）。</summary>
        private static void CheckRadius(int k)
        {
            if (k < 0) throw new ArgumentOutOfRangeException(nameof(k), k, "k-ring 半径不能为负");
        }

        /// <summary>剔除所有 0 空槽。注意不能只删尾部：gridDisk 穿越五边形时，
        /// 输出数组【任意位置】都可能留 0（C 端按哈希集合写入，顺序不定）——所以整体过滤。</summary>
        private static ulong[] Trim(ulong[] arr)
        {
            int count = 0;
            foreach (ulong v in arr)
                if (v != Null) count++;
            if (count == arr.Length) return arr;

            var trimmed = new ulong[count];
            int idx = 0;
            foreach (ulong v in arr)
                if (v != Null) trimmed[idx++] = v;
            return trimmed;
        }

        /// <summary>统一错误出口：非 E_SUCCESS 即抛异常，附 H3 给出的错误描述。</summary>
        private static void ThrowOnError(uint err, string op)
        {
            if (err == H3Native.ESuccess) return;
            string desc = MarshalErr(err);
            throw new InvalidOperationException($"[H3] {op} 失败（错误码 {err}）：{desc}");
        }

        private static string MarshalErr(uint err)
        {
            try
            {
                IntPtr p = H3Native.describeH3Error(err);
                return p == IntPtr.Zero ? "(无描述)" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(p) ?? "(无描述)";
            }
            catch
            {
                return "(取描述失败)";
            }
        }
    }
}
