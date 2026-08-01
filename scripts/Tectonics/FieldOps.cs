using Godot;
using System;

namespace World.Tectonics
{
    /// <summary>
    /// 球面标量/向量场运算（tectonics.js Fields/ 的 C# 移植，2026-08-02）。
    ///
    /// JS 用 SoA TypedArray + 命名空间函数（ScalarField/VectorField/BinaryMorphology），
    /// C# 直接静态方法操作 float[]/Vector3[]。网格邻居来自 SphereGrid。
    ///
    /// 对应 JS：
    ///   precompiled/rasters/fields/ScalarField.js
    ///   precompiled/rasters/fields/VectorField.js
    ///   precompiled/rasters/morphology/BinaryMorphology.js
    ///   precompiled/rasters/interpolation/Float32RasterInterpolation.js
    /// </summary>
    public static class FieldOps
    {
        // ── 标量场基础运算 ──

        public static void MultScalar(float[] f, float s, float[] result)
        {
            for (int i = 0; i < f.Length; i++) result[i] = f[i] * s;
        }

        public static void AddScalar(float[] f, float s, float[] result)
        {
            for (int i = 0; i < f.Length; i++) result[i] = f[i] + s;
        }

        public static void SubScalar(float[] f, float s, float[] result)
        {
            for (int i = 0; i < f.Length; i++) result[i] = f[i] - s;
        }

        public static void AddField(float[] a, float[] b, float[] result)
        {
            for (int i = 0; i < a.Length; i++) result[i] = a[i] + b[i];
        }

        public static void MultField(float[] a, float[] b, float[] result)
        {
            for (int i = 0; i < a.Length; i++) result[i] = a[i] * b[i];
        }

        public static void MaxScalar(float[] f, float s, float[] result)
        {
            for (int i = 0; i < f.Length; i++) result[i] = Mathf.Max(f[i], s);
        }

        public static void MinScalar(float[] f, float s, float[] result)
        {
            for (int i = 0; i < f.Length; i++) result[i] = Mathf.Min(f[i], s);
        }

        public static void Clamp(float[] f, float lo, float hi, float[] result)
        {
            for (int i = 0; i < f.Length; i++) result[i] = Mathf.Clamp(f[i], lo, hi);
        }

        /// <summary>f &gt; s → 1，否则 0（byte 场）。对应 ScalarField.gt_scalar。</summary>
        public static byte[] GtScalar(float[] f, float s)
        {
            var r = new byte[f.Length];
            for (int i = 0; i < f.Length; i++) r[i] = f[i] > s ? (byte)1 : (byte)0;
            return r;
        }

        /// <summary>f == s → 1，否则 0。对应 Uint8Field.eq_scalar。</summary>
        public static byte[] EqScalar(float[] f, float s)
        {
            var r = new byte[f.Length];
            for (int i = 0; i < f.Length; i++) r[i] = Mathf.Abs(f[i] - s) < 1e-6f ? (byte)1 : (byte)0;
            return r;
        }

        public static byte[] EqScalar(byte[] f, byte s)
        {
            var r = new byte[f.Length];
            for (int i = 0; i < f.Length; i++) r[i] = f[i] == s ? (byte)1 : (byte)0;
            return r;
        }

        public static byte[] NeScalar(byte[] f, byte s)
        {
            var r = new byte[f.Length];
            for (int i = 0; i < f.Length; i++) r[i] = f[i] != s ? (byte)1 : (byte)0;
            return r;
        }

        // ── 形态学（BinaryMorphology，byte mask 0/1）──

        /// <summary>腐蚀 k 次：值为 1 且所有邻居为 1 才保留。对应 erosion。</summary>
        public static byte[] Erode(SphereGrid grid, byte[] mask, int k)
        {
            var cur = (byte[])mask.Clone();
            var tmp = new byte[mask.Length];
            for (int pass = 0; pass < k; pass++)
            {
                for (int i = 0; i < cur.Length; i++)
                {
                    if (cur[i] == 0) { tmp[i] = 0; continue; }
                    bool all = true;
                    foreach (int nb in grid.Neighbors[i])
                        if (cur[nb] == 0) { all = false; break; }
                    tmp[i] = all ? (byte)1 : (byte)0;
                }
                (cur, tmp) = (tmp, cur);
            }
            return cur;
        }

        /// <summary>膨胀 k 次：值为 1 或任一邻居为 1 保留。对应 dilation。</summary>
        public static byte[] Dilate(SphereGrid grid, byte[] mask, int k)
        {
            var cur = (byte[])mask.Clone();
            var tmp = new byte[mask.Length];
            for (int pass = 0; pass < k; pass++)
            {
                for (int i = 0; i < cur.Length; i++)
                {
                    if (cur[i] == 1) { tmp[i] = 1; continue; }
                    bool any = false;
                    foreach (int nb in grid.Neighbors[i])
                        if (cur[nb] == 1) { any = true; break; }
                    tmp[i] = any ? (byte)1 : (byte)0;
                }
                (cur, tmp) = (tmp, cur);
            }
            return cur;
        }

        /// <summary>mask 的边界外扩 1 层（margin）：mask 为 0 但邻居为 1。对应 margin。</summary>
        public static byte[] Margin(SphereGrid grid, byte[] mask, int k)
        {
            var cur = (byte[])mask.Clone();
            var tmp = new byte[mask.Length];
            for (int pass = 0; pass < k; pass++)
            {
                for (int i = 0; i < cur.Length; i++)
                {
                    if (cur[i] == 1) { tmp[i] = 1; continue; }
                    bool any = false;
                    foreach (int nb in grid.Neighbors[i])
                        if (cur[nb] == 1) { any = true; break; }
                    tmp[i] = any ? (byte)1 : (byte)0;
                }
                (cur, tmp) = (tmp, cur);
            }
            // margin = 膨胀 - 原 mask
            for (int i = 0; i < cur.Length; i++)
                cur[i] = (byte)(cur[i] & (mask[i] == 0 ? 1 : 0));
            return cur;
        }

        // ── 插值（Float32RasterInterpolation）──

        /// <summary>分段线性插值：breaks 升序，values 对应值。对应 lerp。</summary>
        public static float Lerp(float[] breaks, float[] values, float x)
        {
            if (x <= breaks[0]) return values[0];
            int n = breaks.Length;
            if (x >= breaks[n - 1]) return values[n - 1];
            for (int i = 1; i < n; i++)
            {
                if (x <= breaks[i])
                {
                    float t = (x - breaks[i - 1]) / (breaks[i] - breaks[i - 1]);
                    return values[i - 1] + (values[i] - values[i - 1]) * t;
                }
            }
            return values[n - 1];
        }

        /// <summary>linearstep(a, b, x)：a→0，b→1。对应 linearstep。</summary>
        public static float Linearstep(float a, float b, float x)
        {
            return Mathf.Clamp((x - a) / (b - a), 0f, 1f);
        }

        // ── 向量场运算（VectorField 子集）──

        /// <summary>叉积向量场：cross(a, b) → result（逐顶点）。</summary>
        public static void CrossField(Vector3[] a, Vector3[] b, Vector3[] result)
        {
            for (int i = 0; i < a.Length; i++)
                result[i] = a[i].Cross(b[i]);
        }

        /// <summary>向量叉积与单位向量：每顶点 v × r（r 为单位球位置）。</summary>
        public static void CrossVectorField(Vector3[] v, Vector3[] r, Vector3[] result)
        {
            for (int i = 0; i < v.Length; i++)
                result[i] = v[i].Cross(r[i]);
        }

        /// <summary>逐顶点归一化。</summary>
        public static void Normalize(Vector3[] v, Vector3[] result)
        {
            for (int i = 0; i < v.Length; i++)
                result[i] = v[i].Normalized();
        }

        /// <summary>逐顶点点积场：dot(a,b) → float[]。</summary>
        public static void DotField(Vector3[] a, Vector3[] b, float[] result)
        {
            for (int i = 0; i < a.Length; i++)
                result[i] = a[i].Dot(b[i]);
        }

        /// <summary>标量场梯度（球面顶点邻域）：对应 ScalarField.gradient。
        /// 用邻居差值的加权平均近似切平面梯度。简化：均值方向。</summary>
        public static void Gradient(SphereGrid grid, float[] f, Vector3[] result)
        {
            for (int i = 0; i < f.Length; i++)
            {
                Vector3 g = Vector3.Zero;
                foreach (int nb in grid.Neighbors[i])
                {
                    Vector3 d = grid.Vertices[nb] - grid.Vertices[i];
                    g += d * (f[nb] - f[i]);
                }
                result[i] = g;
            }
        }

        /// <summary>
        /// 拉普拉斯扩散（对应 ScalarField.diffusion_by_constant）：
        /// result[i] = f[i] + k × (邻居均值 - f[i])。迭代 N 次 = 高斯平滑，
        /// 用于把局部源（如 buoyancy）扩散成全场连续场（流体压力）。
        /// </summary>
        public static float[] Diffuse(SphereGrid grid, float[] f, float k, int iterations)
        {
            int n = f.Length;
            var cur = (float[])f.Clone();
            var tmp = new float[n];
            for (int iter = 0; iter < iterations; iter++)
            {
                for (int i = 0; i < n; i++)
                {
                    float sum = 0;
                    var nbs = grid.Neighbors[i];
                    for (int kk = 0; kk < nbs.Length; kk++) sum += cur[nbs[kk]];
                    float avg = sum / nbs.Length;
                    tmp[i] = cur[i] + k * (avg - cur[i]);
                }
                (cur, tmp) = (tmp, cur);
            }
            return cur;
        }

        // ── 统计 ──

        public static float Min(float[] f)
        {
            float m = float.MaxValue;
            foreach (var v in f) if (v < m) m = v;
            return m;
        }

        public static float Max(float[] f)
        {
            float m = float.MinValue;
            foreach (var v in f) if (v > m) m = v;
            return m;
        }

        public static float Average(float[] f)
        {
            double s = 0;
            foreach (var v in f) s += v;
            return (float)(s / f.Length);
        }

        public static float Sum(float[] f)
        {
            double s = 0;
            foreach (var v in f) s += v;
            return (float)s;
        }

        /// <summary>加权平均向量（mass 为权重）。对应 VectorDataset.weighted_average。</summary>
        public static Vector3 WeightedAverage(Vector3[] positions, float[] weights)
        {
            Vector3 sum = Vector3.Zero;
            double wsum = 0;
            for (int i = 0; i < positions.Length; i++)
            {
                sum += positions[i] * weights[i];
                wsum += weights[i];
            }
            return wsum > 0 ? sum / (float)wsum : Vector3.Zero;
        }
    }
}
