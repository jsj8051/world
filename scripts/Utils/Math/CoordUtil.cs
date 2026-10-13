using System;
using Godot;
using World.Utils.H3;         // LatLng（DeterministicRandom 同在本命名空间 World.Utils）

namespace World.Utils
{
    /// <summary>球面 / 经纬互转与球面采样（通用工具）。约定：极轴 = Y，θ 从北极起算，φ = atan2(Z, X)，LatLng 是弧度。</summary>
    public static class CoordUtil
    {
        /// <summary>经纬 → 球面方向（乘 radius）。</summary>
        public static Vector3 LatLngToSphere(LatLng g, float radius)
        {
            // g.Lat / g.Lng 均为弧度
            double x = Math.Cos(g.Lat) * Math.Cos(g.Lng);
            double y = Math.Sin(g.Lat);                       // 纬度直接决定 Y（北）
            double z = Math.Cos(g.Lat) * Math.Sin(g.Lng);
            return new Vector3((float)x, (float)y, (float)z) * radius;
        }

        /// <summary>球面方向 → 经纬（弧度）。</summary>
        public static LatLng SphereToLatLng(Vector3 dir)
            => new(Math.Asin(Math.Clamp(dir.Y, -1f, 1f)), Math.Atan2(dir.Z, dir.X));

        /// <summary>球面方向 → (θ ∈ [0,π], φ ∈ [0,2π))，弧度。</summary>
        public static (float theta, float phi) ToEuler(Vector3 dir)
        {
            float theta = MathF.Acos(Math.Clamp(dir.Y, -1f, 1f));
            float phi = MathF.Atan2(dir.Z, dir.X);
            if (phi < 0f) phi += MathF.Tau;
            return (theta, phi);
        }

        /// <summary>(θ, φ) → 经纬（弧度）；与 <see cref="ToEuler"/> 互为逆。</summary>
        public static LatLng EulerToLatLng(float theta, float phi)
            => new(Math.PI * 0.5 - theta, phi);

        /// <summary>球面均匀采样（面积元均匀，不两极堆点）。</summary>
        public static Vector3 UniformSphere(DeterministicRandom rnd)
        {
            float z = 2f * (float)rnd.NextDouble() - 1f;
            float a = 2f * MathF.PI * (float)rnd.NextDouble();
            float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            return new Vector3(r * MathF.Cos(a), z, r * MathF.Sin(a));
        }
    }
}
