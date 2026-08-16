using Godot;
using System;
using System.Collections.Generic;
using World.HexPlanet;
using World.MapGen;
using World.Services;

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
        // ⚠️ 2026-08-19：桶数按 n 缩放（对齐 MapArchive/GameGrid 同款修复）——固定 16×32 时
        //   n≥128 每桶 ~320 顶点 → NearestId O(V²) 卡死；目标每桶 ~30 顶点，lat:lon=1:2。
        private int _bucketsLat;
        private int _bucketsLon;
        private List<int>[,] _buckets;

        public SphereGrid(int n)
        {
            // 复用项目现有 Icosahedron 细分（n 段切边）。
            // ⚠️ Icosahedron.VertexKey 按 1km 量化——必须传真实半径(km)再归一化，
            //    传 1 会让全部顶点合并（实测 verts=26）。2026-08-02 修复。
            // ⚠️ 板块模拟内部标度固定 6371km（与存档默认一致；板块输出是 rad 角度格局，
            //    与星球实际半径无关——2026-08-10 统一标度决策）。
            Icosahedron.Subdivide(n, MapArchive.DefaultRadiusKm, out var verts, out var indices);
            Vertices = new Vector3[verts.Count];
            for (int i = 0; i < verts.Count; i++)
                Vertices[i] = verts[i].Normalized();
            Faces = new List<(int, int, int)>();
            for (int i = 0; i < indices.Count; i += 3)
                Faces.Add((indices[i], indices[i + 1], indices[i + 2]));

            // 桶数按顶点数缩放（目标每桶 ~30，lat:lon ≈ 1:2）——n=16→16×32 与原相同，n≥64 自动加密
            int targetPerBucket = 30;
            int totalBuckets = Math.Max(2, Vertices.Length / targetPerBucket);
            _bucketsLat = Mathf.Clamp((int)Mathf.Round(Mathf.Sqrt(totalBuckets / 2f)), 4, 512);
            _bucketsLon = _bucketsLat * 2;

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
            _buckets = new List<int>[_bucketsLat, _bucketsLon];
            for (int y = 0; y < _bucketsLat; y++)
                for (int x = 0; x < _bucketsLon; x++)
                    _buckets[y, x] = new List<int>();

            for (int i = 0; i < Vertices.Length; i++)
            {
                (int by, int bx) = BucketOf(Vertices[i]);
                _buckets[by, bx].Add(i);
            }
        }

        private (int, int) BucketOf(Vector3 v)
        {
            float lat = Mathf.Asin(Mathf.Clamp(v.Y, -1f, 1f));         // -π/2..π/2
            float lon = Mathf.Atan2(v.Z, v.X);                          // -π..π
            int by = (int)Mathf.Clamp((lat / Mathf.Pi + 0.5f) * _bucketsLat, 0, _bucketsLat - 1);
            int bx = (int)(((lon / Mathf.Pi + 1f) * 0.5f * _bucketsLon) % _bucketsLon);
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
                int y = (by + dy + _bucketsLat) % _bucketsLat;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = (bx + dx + _bucketsLon) % _bucketsLon;
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

        /// <summary>
        /// 种子最近邻（爬山）：从 seed 出发，沿"更近的邻居"方向移动直到局部最优。
        /// 适用：查询点 p 靠近 seed（如刚体旋转后上一步的映射），旋转角小于网格间距时
        /// 结果与全桶查询一致（实测 500 次移动后仅 10/10242 差 1 格 = 0.1%，误差在
        /// Merge 重采样时被平滑，物理无影响），但只需 ~7-20 次距离计算（vs 全桶 ~180 次）。
        /// ⚠️ 2026-08-02 v2：扩展超阈值（种子不可靠，如错误种子传播）→ 返回 -1，
        ///   调用方兜底全桶 NearestId 精确纠错。根治"爬山从错误种子出发一直错"的
        ///   累积退化（profile：n=64 后段 Move 44s+，resync 全桶方案成本 O(n) 也失效）。
        /// </summary>
        public int NearestIdSeeded(Vector3 p, int seed, int[] scratch)
        {
            // 正确种子 ~7-20 候选；超 64 = 种子漂移（错误）→ 返回 -1 兜底
            const int MaxCandidates = 64;
            int best = seed;
            float bestD = (Vertices[best] - p).LengthSquared();
            int stackCount = 0;
            scratch[stackCount++] = best;
            // BFS：检查种子及其邻居，若邻居更近则继续扩展（爬山）
            int head = 0;
            while (head < stackCount)
            {
                if (stackCount > MaxCandidates) return -1;   // 种子不可靠 → 调用方全桶
                int id = scratch[head++];
                foreach (int nb in Neighbors[id])
                {
                    float d = (Vertices[nb] - p).LengthSquared();
                    if (d < bestD)
                    {
                        bestD = d;
                        best = nb;
                        // 新邻居入栈（若未在栈中——栈小，线性查重）
                        bool dup = false;
                        for (int k = 0; k < stackCount; k++)
                            if (scratch[k] == nb) { dup = true; break; }
                        if (!dup && stackCount < scratch.Length)
                            scratch[stackCount++] = nb;
                    }
                }
            }
            return best;
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
            LogService.Log("SphereGrid", $"verts={Vertices.Length} faces={Faces.Count} " +
                     $"neighbors avg={sumNb / (double)Vertices.Length:F1} min={minNb} max={maxNb}");
        }
    }
}
