// H3Native.cs —— Uber H3（v4.5.0，原生 C 库）的 P/Invoke 声明层。
//
// 职责：只做"函数签名 ↔ 原生符号"的最小映射 + 原生库加载解析，不夹带任何逻辑。
// 托管门面（对用户友好的 API、参数校验、错误转异常）在 H3.cs。
//
// 原生库位置： native/h3/h3.dll（MinGW 编译的 Release 版，运行时仅依赖 KERNEL32 + UCRT，
// 无 libgcc/winpthread 依赖，Win10+ 直接可加载）。重编译步骤见 native/h3/rebuild.sh。
//
// 数据结构（与 h3api.h 完全一致）：
//   H3Index   —— uint64，六边形格子全局索引（含五边形）。H3.Null = 0 表示无效。
//   LatLng    —— { double lat; double lng; }，单位是【弧度】（h3api.h 定义为 radians）。
//   CellBoundary —— 格子边界，固定内联数组（v4 起 verts[10]，非指针），数组元素在
//               h3api.h 里定义了 `verts[MAX_CELL_BNDRY_VERTS]`（10 = 五边形 5 + 5 边穿越）。
//
// 调用约定 cdecl（H3 是 C 库默认）。
//
// 原文注释头（Apache-2.0，Uber）写在 native/h3/h3api.h，这里中文注释只补"我们怎么接的"。

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace World.Utils.H3
{
    /// <summary>H3 原生层：DllImport 声明 + 库定位解析器。internal，勿直接从业务代码使用。</summary>
    internal static class H3Native
    {
        /// <summary>DllImport 用的库名（.NET 在 Windows 上会自动补 .dll）。</summary>
        private const string Lib = "h3";

        /// <summary>
        /// 静态构造：在程序集上注册自定义解析器，处理 Godot/单测两种宿主下原生库的查找。
        /// static 构造先于任何 P/Invoke 调用执行，保证首次调用前解析器已就位。
        /// </summary>
        static H3Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(H3Native).Assembly, Resolve);
        }

        /// <summary>
        /// h3.dll 查找顺序（返回找到的库句柄，找不到返回 Zero 让运行时走默认探测）：
        ///   1) 调用方程序集输出目录（Godot 运行 = .godot/mono/temp/bin/Debug；单测 = tests/bin）。
        ///      —— world.csproj 的 None+CopyToOutputDirectory 已把 native/h3/h3.dll 拷贝过去。
        ///   2) Godot res:// 根目录的 absolute 路径（编辑器直跑且输出未及拷贝时的兜底）。
        ///   3) 环境变量 H3_NATIVE_PATH 显式指定的完整路径（测试/特殊部署的逃生门）。
        /// </summary>
        private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly,
                                      DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, Lib, StringComparison.Ordinal))
                return IntPtr.Zero;

            // 1) 程序集输出目录（Godot 运行 = .godot/mono/temp/bin/Debug；单测 = tests/bin）
            string baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(baseDir))
            {
                IntPtr h = TryLoad(Path.Combine(baseDir, Lib + ".dll"));
                if (h != IntPtr.Zero) return h;
            }

            // 2) Godot res:// 根目录（GlobalizePath 在非 Godot 宿主下会抛，用 try/catch 守住）
            try
            {
                string resRoot = godotProjectRoot();
                if (resRoot != null)
                {
                    IntPtr h = TryLoad(Path.Combine(resRoot, "native", "h3", Lib + ".dll"));
                    if (h != IntPtr.Zero) return h;
                }
            }
            catch
            {
                // 非 Godot 宿主（单测控制台）：忽略 res:// 分支
            }

            // 3) 环境变量显式指定（逃生门）
            string env = Environment.GetEnvironmentVariable("H3_NATIVE_PATH");
            if (!string.IsNullOrEmpty(env))
            {
                IntPtr h = TryLoad(env);
                if (h != IntPtr.Zero) return h;
            }

            return IntPtr.Zero;
        }

        private static IntPtr TryLoad(string fullPath)
        {
            return File.Exists(fullPath) ? NativeLibrary.Load(fullPath) : IntPtr.Zero;
        }

        /// <summary>仅在 Godot 宿主下可用；取 res:// 在磁盘上的绝对路径。</summary>
        private static string godotProjectRoot()
        {
            // 用到 Godot API —— 惰性引用，确保只有真的在 Godot 里跑才触发
            return Godot.ProjectSettings.GlobalizePath("res://");
        }

        // ============ 数据类型（与 h3api.h 对齐） ============

        /// <summary>
        /// 单个格子边界：顶点数 + 固定 10 个顶点（LatLng 8 字节对齐，首字段 int 后有 4 字节填充，
        /// 整体 168 字节，与 C 端 CellBoundary 布局逐字节一致——blittable，按值传引用零拷贝）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct CellBoundary
        {
            public int NumVerts;
            public LatLng V0;
            public LatLng V1;
            public LatLng V2;
            public LatLng V3;
            public LatLng V4;
            public LatLng V5;
            public LatLng V6;
            public LatLng V7;
            public LatLng V8;
            public LatLng V9;

            /// <summary>取出前 NumVerts 个顶点（边界顶点按逆时针顺序）。</summary>
            public LatLng[] TakeVerts()
            {
                int n = Math.Clamp(NumVerts, 0, 10);
                var arr = new LatLng[n];
                var all = new[] { V0, V1, V2, V3, V4, V5, V6, V7, V8, V9 };
                for (int i = 0; i < n; i++) arr[i] = all[i];
                return arr;
            }
        }

        // ============ 错误码（h3api.h H3ErrorCodes） ============

        /// <summary>操作成功。</summary>
        internal const uint ESuccess = 0;

        // ============ 原始函数声明（只做签名映射） ============

        // 经纬度 → 所在格子；坐标单位弧度，res 0–15
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint latLngToCell(ref LatLng g, int res, out ulong h3);

        // 格子 → 中心经纬度（弧度）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint cellToLatLng(ulong h3, out LatLng g);

        // 格子 → 边界顶点（逆时针，弧度）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint cellToBoundary(ulong h3, out CellBoundary boundary);

        // k-ring 理论最大格子数（含五边形畸变），用于为 out 数组分配合适大小
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint maxGridDiskSize(int k, out long outSize);

        // 安全 k-ring（自动绕过五边形；结果可能少于理论最大值，尾部以 0 填充）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint gridDisk(ulong origin, int k, [In, Out] ulong[] outCells);

        // 不安全 k-ring（假设无五边形；遇到五边形畸变返回 E_PENTAGON）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint gridDiskUnsafe(ulong origin, int k, [In, Out] ulong[] outCells);

        // 空心环（只有距离恰好为 k 的那一圈）的理论最大值
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint maxGridRingSize(int k, out long outSize);

        // 空心环（只有距离恰好为 k 的那一圈）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint gridRingUnsafe(ulong origin, int k, [In, Out] ulong[] outCells);

        // 两格距离（单位 = 步数）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint gridDistance(ulong origin, ulong h3, out long distance);

        // 连线含两端点；size 含端点
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint gridPathCellsSize(ulong start, ulong end, out long size);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint gridPathCells(ulong start, ulong end, [In, Out] ulong[] outCells);

        // 父格（parentRes ≤ 当前 res）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint cellToParent(ulong h3, int parentRes, out ulong parent);

        // 子格数量 / 子格数组（childRes ≥ 当前 res）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint cellToChildrenSize(ulong h3, int childRes, out long outSize);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint cellToChildren(ulong h3, int childRes, [In, Out] ulong[] children);

        // 中心子格（保证是父格的正中心那个孩子）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint cellToCenterChild(ulong h3, int childRes, out ulong child);

        // 压缩：把能合并到同一父格的完整子格集压成一个父格。out 长度 = 输入长度，多余槽位填 0
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint compactCells([In] ulong[] h3Set, [In, Out] ulong[] compactedSet, long numHexes);

        // 解压数量 / 解压数组
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint uncompactCellsSize([In] ulong[] compactedSet, long numCompacted, int res, out long outSize);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint uncompactCells([In] ulong[] compactedSet, long numCompacted,
                                                   [In, Out] ulong[] outSet, long numOut, int res);

        // 纯查询（返回 int，非错误码）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int getResolution(ulong h3);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int getBaseCellNumber(ulong h3);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int isValidCell(ulong h3);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int isPentagon(ulong h3);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int isResClassIII(ulong h3);

        // 邻居判断（int out：1=邻接 0=不邻接）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint areNeighborCells(ulong origin, ulong destination, out int outIsNeighbor);

        // 字符串 ↔ 索引
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern uint stringToH3([MarshalAs(UnmanagedType.LPStr)] string str, out ulong h3);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]  // char* 缓冲区，传入 StringBuilder
        internal static extern uint h3ToString(ulong h3, StringBuilder str, nuint size);

        // 全局统计与枚举
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint getNumCells(int res, out long outCount);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int res0CellCount();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint getRes0Cells([In, Out] ulong[] outCells);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pentagonCount();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint getPentagons(int res, [In, Out] ulong[] outCells);

        // 面积 / 边长（含五边形时需按格子精确算）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint cellAreaKm2(ulong h3, out double area);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint getHexagonAreaAvgKm2(int res, out double area);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint getHexagonEdgeLengthAvgKm(int res, out double length);

        // 顶点 API（返回顶点索引，多格共享同一顶点 id → 便于无缝拼接渲染）
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint cellToVertexes(ulong origin, [In, Out] ulong[] vertexes);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint vertexToLatLng(ulong vertex, out LatLng point);

        // 错误描述
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr describeH3Error(uint err);
    }
}
