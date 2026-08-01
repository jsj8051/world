using Godot;
using System;
using System.Collections.Generic;
using World.HexPlanet;

namespace World.Tectonics
{
    /// <summary>
    /// 球面网格（Icosahedron 细分，tectonics.js Grid.js 的 C# 移植，2026-08-02）。
    ///
    /// 提供：
    ///   Vertices —— 单位球顶点（Vector3）
    ///   Neighbors —— 顶点邻接表（O(1) 查邻居）
    ///   NearestId —— Voronoi 最近邻（球面 hash 分桶，替代 VoronoiSphere 空间索引）
    ///
    /// 对应 JS：Grid.js（邻居预计算）+ VoronoiSphere.js（最近邻，简化版）。
    /// 源码参考：docs/tectonics-ref/precompiled/rasters/Grid.js
    /// </summary>
    public class SphereGrid
    {
        public int VertexCount => Vertices.Length;
        public Vector3[] Vertices;          // 单位球顶点
        public int[][] Neighbors;           // 每顶点邻居 id 数组
        public List<(int a, int b, int c)> Faces;

        // ── 球面 hash 桶（最近邻加速）──
        private const int BucketsLat = 16;   // 纬度分桶（-90..90）
        private const int BucketsLon = 32;   // 经度分桶（环形）
        private List<int>[,] _buckets;

        public SphereGrid(int n)
        {
            // 复用项目现有 Icosahedron 细分（n 段切边）。
            // ⚠️ Icosahedron.VertexKey 按 1km 量化——必须传真实半径(km)再归一化，
            //    传 1 会让全部顶点合并（实测 verts=26）。2026-08-02 修复。
            Icosahedron.Subdivide(n, 6330f, out var verts, out var indices);
            Vertices = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++)
                Vertices[i] = verts[i].Normalized();
            Faces = new List<(int, int, int)>();
            for (int i = 0; i < indices.Count; i += 3)
                Faces.Add((indices[i], indices[i + 1], indices[i + 2]));

            BuildNeighbors();
            BuildBuckets();
        }

        /// <summary>从面表构建邻接表（每顶点→相邻顶点数组）。</summary>
        private void BuildNeighbors()
        {
            var lookup = new HashSet<int>[Vertices.Length];
            for (int i = 0; i < lookup.Length; i++) lookup[i] = new HashSet<int>();
            foreach (var f in Faces)
            {
                lookup[f.a].Add(f.b); lookup[f.a].Add(f.c);
                lookup[f.b].Add(f.a); lookup[f.b].Add(f.c);
                lookup[f.c].Add(f.a); lookup[f.c].Add(f.b);
            }
            Neighbors = new int[Vertices.Length][];
            for (int i = 0; i < Vertices.Length; i++)
                Neighbors[i] = new List<int>(lookup[i]).ToArray();
        }

        private void BuildBuckets()
        {
            _buckets = new List<int>[BucketsLat, BucketsLon];
            for (int y = 0; y < BucketsLat; y++)
                for (int x = 0; x < BucketsLon; x++)
                    _buckets[y, x] = new List<int>();

            for (int i = 0; i < Vertices.Length; i++)
            {
                (int by, int bx) = BucketOf(Vertices[i]);
                _buckets[by, bx].Add(i);
            }
        }

        private static (int, int) BucketOf(Vector3 v)
        {
            float lat = Mathf.Asin(Mathf.Clamp(v.Y, -1f, 1f));         // -π/2..π/2
            float lon = Mathf.Atan2(v.Z, v.X);                          // -π..π
            int by = (int)Mathf.Clamp((lat / Mathf.Pi + 0.5f) * BucketsLat, 0, BucketsLat - 1);
            int bx = (int)(((lon / Mathf.Pi + 1f) * 0.5f * BucketsLon) % BucketsLon);
            return (by, bx);
        }

        /// <summary>
        /// 最近邻顶点：球面点 p → 最近网格顶点 id。
        /// 查 3×3 邻桶（经度环形），桶内线性扫描。
        /// 对应 JS：VoronoiSphere.getNearestId。
        /// </summary>
        public int NearestId(Vector3 p)
        {
            (int by, int bx) = BucketOf(p);
            int best = -1;
            float bestD = float.MaxValue;
            for (int dy = -1; dy <= 1; dy++)
            {
                int y = (by + dy + BucketsLat) % BucketsLat;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = (bx + dx + BucketsLon) % BucketsLon;
                    foreach (int id in _buckets[y, x])
                    {
                        float d = (Vertices[id] - p).LengthSquared();
                        if (d < bestD) { bestD = d; best = id; }
                    }
                }
            }
            return best;
        }

        /// <summary>批量最近邻：posField 每项 → 最近顶点 id 写入 result。</summary>
        public void NearestIds(Vector3[] posField, int[] result)
        {
            for (int i = 0; i < posField.Length; i++)
                result[i] = NearestId(posField[i]);
        }

        /// <summary>诊断：顶点数/邻居数/桶分布。</summary>
        public void PrintDiagnostics()
        {
            int minNb = int.MaxValue, maxNb = 0;
            long sumNb = 0;
            foreach (var nb in Neighbors)
            {
                minNb = Math.Min(minNb, nb.Length);
                maxNb = Math.Max(maxNb, nb.Length);
                sumNb += nb.Length;
            }
            GD.Print($"[SphereGrid] verts={Vertices.Length} faces={Faces.Count} " +
                     $"neighbors avg={sumNb / (double)Vertices.Length:F1} min={minNb} max={maxNb}");
        }
    }
}
