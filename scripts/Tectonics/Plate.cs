using Godot;
using System.Collections.Generic;
using System.Threading;

namespace World.Tectonics
{
    /// <summary>
    /// 板块（tectonics.js Plate.js 的 C# 移植，2026-08-02）。
    ///
    /// 每块板是刚体：持有自己的 crust（定义在板局部网格上）和
    /// local_to_global 旋转矩阵（欧拉旋转，绕球心）。
    ///
    /// 移动（move）：旋转矩阵累积 → 全局顶点映射到局部坐标 → 找局部最近顶点
    /// （local_ids_of_global_cells），用于把板 crust 合并回全局（merge）。
    ///
    /// 对应 JS 字段：
    ///   mask —— 板覆盖范围（byte[]，局部网格上）
    ///   local_to_global_matrix / global_to_local_matrix —— 旋转矩阵（列主序 9 元素）
    ///   local_ids_of_global_cells —— 每全局顶点 → 本板最近局部顶点 id
    ///   global_ids_of_local_cells —— 每局部顶点 → 全局顶点 id
    ///
    /// 源码参考：docs/tectonics-ref/noncompiled/models/lithosphere/Plate.js
    /// </summary>
    public class Plate
    {
        public int Id;
        public SphereGrid LocalGrid;        // 板局部网格（= 全局网格拓扑，坐标用局部系）
        public Crust Crust;                 // 板地壳（局部网格上）
        public byte[] Mask;                 // 板覆盖 mask（局部网格上，1=属于本板）
        public float[] LocalToGlobal;       // 旋转矩阵 3×3（列主序 9 元素）
        public float[] GlobalToLocal;       // 逆矩阵
        public int[] LocalIdsOfGlobalCells; // 全局顶点 → 本板最近局部顶点 id
        public int[] GlobalIdsOfLocalCells; // 局部顶点 → 全局顶点 id
        public Vector3[] Velocity;          // 每格速度场（局部网格，rad/My）
        public Vector3[] BoundaryNormal;    // mask 边界法线（局部网格）
        public Vector3[] BuoyancyVec;       // 每格浮力（局部网格，N/m³）

        /// <summary>面积（局部网格 mask 覆盖数）。</summary>
        public int TileCount
        {
            get
            {
                int c = 0;
                foreach (var m in Mask) if (m == 1) c++;
                return c;
            }
        }

        public Plate(int id, SphereGrid grid, Crust crust, byte[] mask)
        {
            Id = id;
            LocalGrid = grid;
            Crust = crust;
            Mask = (byte[])mask.Clone();
            LocalToGlobal = MatrixOps.Identity();
            GlobalToLocal = MatrixOps.Identity();
            int n = grid.VertexCount;
            LocalIdsOfGlobalCells = new int[n];
            GlobalIdsOfLocalCells = new int[n];
            Velocity = new Vector3[n];
            BoundaryNormal = new Vector3[n];
            BuoyancyVec = new Vector3[n];
            // 初始：局部=全局，一一对应
            for (int i = 0; i < n; i++)
            {
                LocalIdsOfGlobalCells[i] = i;
                GlobalIdsOfLocalCells[i] = i;
            }
        }

        /// <summary>
        /// 移动板块（真速度模型，M2）：Schellart 浮力驱动。
        /// 对应 JS Plate.move + Tectonophysics：
        ///   1. velocity = guess_plate_velocity(boundary_normal, buoyancy)
        ///   2. rotation = get_plate_rotation_matrix3x3(velocity, com, megayears)
        ///   3. local_to_global = local_to_global × rotation
        ///   4. 重算局部↔全局映射（Voronoi 最近邻）
        /// </summary>
        /// <param name="resyncMappings">⚠️ 2026-08-02 性能优化 A：周期全量重同步。
        ///   板块旋转角随模拟时间累积 → 上一步映射当爬山种子越来越不准 → 候选打满 80
        ///   退化成近似全桶查询（profile：n=64 每 50My Move 涨 3 倍，100-150My 段 13.6s）。
        ///   每 25 步调用一次全桶 NearestId 重建映射 → 中间 24 步种子保持准（~7 次）。</param>
        public void Move(float megayears, SphereGrid globalGrid, MaterialDensity material, float surfaceGravity, bool resyncMappings = false)
        {
            // 1. 速度场：v = boundary_normal × buoyancy × k
            //    buoyancy ≤0（负浮力=下沉的俯冲板片），方向指向板边界外侧
            var mass = Crust.GetTotalMass();
            var thickness = Crust.GetThickness(material);
            var density = Crust.GetDensity(mass, thickness, material.MaficVolcanicMin);
            var buoyancy = Crust.GetBuoyancy(density, material, surfaceGravity);
            for (int i = 0; i < buoyancy.Length; i++)
                BuoyancyVec[i] = LocalGrid.Vertices[i] * buoyancy[i];

            // 边界法线（mask 梯度方向，指向板外）
            Tectonophysics.GetBoundaryNormal(LocalGrid, Mask, BoundaryNormal);

            // v = boundary_normal × (buoyancy × k)
            float k = Tectonophysics.LateralSpeedPerForce(material.MantleViscosity);
            for (int i = 0; i < LocalGrid.VertexCount; i++)
                Velocity[i] = BoundaryNormal[i] * (buoyancy[i] * k);

            // 2. 刚性旋转矩阵（绕质心 + 绕世界中心）
            Vector3 com = GetCenterOfMass(mass);
            float[] rot = Tectonophysics.GetPlateRotationMatrix3x3(LocalGrid, Velocity, com, megayears);

            // 3. 累积旋转
            LocalToGlobal = MatrixOps.MultMatrix(LocalToGlobal, rot);
            GlobalToLocal = MatrixOps.Invert(LocalToGlobal);

            // 4. 重算映射（全局顶点 → 板局部坐标 → 最近局部顶点）
            // ⚠️ 优化（2026-08-02 v2）：种子爬山 + 失败兜底全桶（自适应，不依赖参数）。
            //   v1 用 resync 周期全桶——成本 O(n)×频繁 随 n/速度失效（n=64 后段 Move 44s+）。
            //   现在：正常顶点爬山（~7 次距离），爬山超 64 候选（种子错）→ -1 → 全桶精确纠错。
            //   错误每步纠正不传播 → 不随 n/自转速度/板块数退化。
            int n = globalGrid.VertexCount;
            // ⚠️ 2026-08-03 性能：板内顶点级并行（桶构造时已构建=只读安全）；
            //   _scratch 改 ThreadLocal（共享数组并行竞态）
            System.Threading.Tasks.Parallel.For(0, n, i =>
            {
                Vector3 p = globalGrid.Vertices[i];
                Vector3 localPos = MatrixOps.MultVector(GlobalToLocal, p);
                Vector3 dir = localPos.Normalized();
                int seed = LocalIdsOfGlobalCells[i];   // 上一步的映射（旋转小→仍在附近）
                int r = LocalGrid.NearestIdSeeded(dir, seed, _scratchLocal.Value);
                LocalIdsOfGlobalCells[i] = r >= 0 ? r : LocalGrid.NearestId(dir);   // 兜底全桶纠错
            });
            System.Threading.Tasks.Parallel.For(0, LocalGrid.VertexCount, i =>
            {
                Vector3 localPos = MatrixOps.MultVector(LocalToGlobal, LocalGrid.Vertices[i]);
                Vector3 dir = localPos.Normalized();
                int seed = GlobalIdsOfLocalCells[i];
                int r = globalGrid.NearestIdSeeded(dir, seed, _scratchLocal.Value);
                GlobalIdsOfLocalCells[i] = r >= 0 ? r : globalGrid.NearestId(dir);   // 兜底全桶纠错
            });
        }

        private ThreadLocal<int[]> _scratchLocal = new(() => new int[300]);   // 爬山候选（每线程独立，GC 优化）

        /// <summary>板质心（质量加权，局部坐标，单位向量）。</summary>
        public Vector3 GetCenterOfMass(float[] mass)
        {
            return Tectonophysics.GetPlateCenterOfMass(LocalGrid, mass, Mask);
        }

        /// <summary>把板 crust 重采样到全局顶点（用 local_ids_of_global_cells）。
        /// 对应 JS merge_plates_to_master 里的 resample_crust。</summary>
        public void ResampleCrustToGlobal(Crust target)
        {
            int n = LocalIdsOfGlobalCells.Length;
            var pools = Crust.AllPools();
            var tPools = target.AllPools();
            for (int p = 0; p < 8; p++)
            {
                var src = pools[p];
                var dst = tPools[p];
                for (int i = 0; i < n; i++)
                    dst[i] = src[LocalIdsOfGlobalCells[i]];
            }
        }
    }

    /// <summary>3×3 矩阵运算（tectonics.js Matrix3x3.js 移植，列主序 9 元素）。</summary>
    public static class MatrixOps
    {
        public static float[] Identity()
        {
            return new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
        }

        /// <summary>旋转向量（ω = 轴×角）→ 旋转矩阵。对应 FromRotationVector。</summary>
        public static float[] FromRotationVector(Vector3 w)
        {
            float mag = w.Length();
            if (mag < 1e-12f) return Identity();
            Vector3 axis = w / mag;
            float c = Mathf.Cos(mag), s = Mathf.Sin(mag);
            float v = 1f - c;
            float x = axis.X, y = axis.Y, z = axis.Z;
            float vx = v * x, vy = v * y;
            var m = new float[9];
            m[0] = vx * x + c; m[3] = vx * y + s * z; m[6] = vx * z - s * y;
            m[1] = vx * y - s * z; m[4] = vy * y + c; m[7] = vy * z + s * x;
            m[2] = vx * z + s * y; m[5] = vy * z - s * x; m[8] = v * z * z + c;
            return m;
        }

        /// <summary>矩阵乘法 A×B（列主序）。对应 mult_matrix。</summary>
        public static float[] MultMatrix(float[] A, float[] B)
        {
            var C = new float[9];
            C[0] = A[0] * B[0] + A[3] * B[1] + A[6] * B[2];
            C[3] = A[0] * B[3] + A[3] * B[4] + A[6] * B[5];
            C[6] = A[0] * B[6] + A[3] * B[7] + A[6] * B[8];
            C[1] = A[1] * B[0] + A[4] * B[1] + A[7] * B[2];
            C[4] = A[1] * B[3] + A[4] * B[4] + A[7] * B[5];
            C[7] = A[1] * B[6] + A[4] * B[7] + A[7] * B[8];
            C[2] = A[2] * B[0] + A[5] * B[1] + A[8] * B[2];
            C[5] = A[2] * B[3] + A[5] * B[4] + A[8] * B[5];
            C[8] = A[2] * B[6] + A[5] * B[7] + A[8] * B[8];
            return C;
        }

        /// <summary>矩阵×向量（列主序）。</summary>
        public static Vector3 MultVector(float[] M, Vector3 v)
        {
            return new Vector3(
                M[0] * v.X + M[3] * v.Y + M[6] * v.Z,
                M[1] * v.X + M[4] * v.Y + M[7] * v.Z,
                M[2] * v.X + M[5] * v.Y + M[8] * v.Z);
        }

        /// <summary>3×3 逆矩阵。对应 invert。</summary>
        public static float[] Invert(float[] A)
        {
            float a11 = A[0], a12 = A[3], a13 = A[6];
            float a21 = A[1], a22 = A[4], a23 = A[7];
            float a31 = A[2], a32 = A[5], a33 = A[8];
            float det = a11 * (a22 * a33 - a32 * a23)
                      - a12 * (a21 * a33 - a23 * a31)
                      + a13 * (a21 * a32 - a22 * a31);
            if (Mathf.Abs(det) < 1e-12f) return Identity();
            float inv = 1f / det;
            var B = new float[9];
            B[0] = (a22 * a33 - a32 * a23) * inv;
            B[3] = (a13 * a32 - a12 * a33) * inv;
            B[6] = (a12 * a23 - a13 * a22) * inv;
            B[1] = (a23 * a31 - a21 * a33) * inv;
            B[4] = (a11 * a33 - a13 * a31) * inv;
            B[7] = (a21 * a13 - a11 * a23) * inv;
            B[2] = (a21 * a32 - a31 * a22) * inv;
            B[5] = (a31 * a12 - a11 * a32) * inv;
            B[8] = (a11 * a22 - a21 * a12) * inv;
            return B;
        }
    }
}
