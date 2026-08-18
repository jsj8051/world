using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.HexPlanet;

namespace World.PlanetLOD
{

    /// <summary>
    /// Builds flat per-tile colored ArrayMeshes from tile subsets.
    /// Rules: vertices split per tile, one flat color per tile, triangle fan with
    /// outward winding, geometry normals from displaced positions (flat facet look).
    ///
    /// 几何/颜色拆分：BuildGeometry 产出顶点/法线/索引（与颜色无关，可缓存）；
    /// BuildColors 按图层/色板重算颜色（几何不变时只需重算颜色 → 图层切换秒级）。
    /// 顶点按 tile 分割不共享 → 所有并行路径无锁无竞态。
    /// </summary>
    public static class ChunkMeshBuilder
    {
        /// <summary>
        /// 几何构建（纯数据，后台线程安全）：顶点/索引/fan 法线。
        /// 结果可缓存——图层切换只需重算颜色。
        /// </summary>
        public static GeometryData BuildGeometry(
            List<HexTile> tiles,
            Func<Vector3, float> elevAt,
            float radiusKm,
            float elevationScaleKm,
            Action<float> progress = null)
        {
            int total = tiles.Count;

            // ── 第一遍：每 tile 顶点数 → 顶点偏移；三角形数 → 索引偏移 ──
            var vertOffsets = new int[total];
            var triOffsets = new int[total];
            int totalVerts = 0;
            int totalTris = 0;
            for (int i = 0; i < total; i++)
            {
                int n = tiles[i].Corners.Length;
                vertOffsets[i] = totalVerts;
                triOffsets[i] = totalTris;
                if (n >= 3)
                {
                    totalVerts += n + 1; // 中心 + n 个角
                    totalTris += n;      // fan：n 个三角形
                }
            }

            var meshVerts = new Vector3[totalVerts];
            var meshIndices = new int[totalTris * 3];

            // ── 第二遍：并行填充每 tile 的顶点/索引 ──
            int done = 0;
            Parallel.For(0, total, i =>
            {
                var tile = tiles[i];
                int n = tile.Corners.Length;
                if (n >= 3)
                {
                    int off = vertOffsets[i];
                    int centerIdx = off;

                    Vector3 centerPos = tile.Center.Normalized() * (radiusKm + elevAt(tile.Center) * elevationScaleKm);
                    meshVerts[centerIdx] = centerPos;

                    for (int k = 0; k < n; k++)
                    {
                        Vector3 p = tile.Corners[k].Normalized() * (radiusKm + elevAt(tile.Corners[k]) * elevationScaleKm);
                        meshVerts[off + 1 + k] = p;
                    }

                    int idxBase = triOffsets[i] * 3;
                    for (int k = 0; k < n; k++)
                    {
                        int a = off + 1 + k;
                        int b = off + 1 + (k + 1) % n;
                        Vector3 va = meshVerts[a];
                        Vector3 vb = meshVerts[b];
                        Vector3 vc = meshVerts[centerIdx];

                        Vector3 normal = (va - vc).Cross(vb - vc);
                        if (normal.Dot(vc) < 0f)
                        {
                            meshIndices[idxBase + k * 3] = centerIdx;
                            meshIndices[idxBase + k * 3 + 1] = b;
                            meshIndices[idxBase + k * 3 + 2] = a;
                        }
                        else
                        {
                            meshIndices[idxBase + k * 3] = centerIdx;
                            meshIndices[idxBase + k * 3 + 1] = a;
                            meshIndices[idxBase + k * 3 + 2] = b;
                        }
                    }
                }

                if (progress != null && Interlocked.Increment(ref done) % 64 == 0)
                    progress(done / (float)total);
            });
            progress?.Invoke(1f);

            // ── 第三遍：法线累积（每 tile 只写自己的顶点段，无竞态）+ 归一化 ──
            var accNormals = new Vector3[totalVerts];
            Parallel.For(0, total, i =>
            {
                int n = tiles[i].Corners.Length;
                if (n < 3)
                    return;
                int idxBase = triOffsets[i] * 3;
                for (int k = 0; k < n; k++)
                {
                    int ia = meshIndices[idxBase + k * 3];
                    int ib = meshIndices[idxBase + k * 3 + 1];
                    int ic = meshIndices[idxBase + k * 3 + 2];
                    Vector3 nrm = (meshVerts[ib] - meshVerts[ia]).Cross(meshVerts[ic] - meshVerts[ia]);
                    accNormals[ia] += nrm;
                    accNormals[ib] += nrm;
                    accNormals[ic] += nrm;
                }
            });
            var meshNormals = new Vector3[totalVerts];
            Parallel.For(0, totalVerts, i => meshNormals[i] = accNormals[i].Normalized());

            return new GeometryData
            {
                Verts = meshVerts,
                Normals = meshNormals,
                Indices = meshIndices,
                VertOffsets = vertOffsets,
                TotalVerts = totalVerts
            };
        }

        /// <summary>
        /// 颜色重算（纯数据，后台线程安全）：每 tile 一个颜色展开到其全部顶点。
        /// 几何不变时只调这个 → 图层切换秒级。
        /// </summary>
        public static Color[] BuildColors(
            List<HexTile> tiles,
            Func<HexTile, Color> colorFn,
            GeometryData g,
            Action<float> progress = null)
        {
            var colors = new Color[g.TotalVerts];
            int done = 0;
            Parallel.For(0, tiles.Count, i =>
            {
                var tile = tiles[i];
                int n = tile.Corners.Length;
                if (n >= 3)
                {
                    Color c = colorFn(tile);
                    int off = g.VertOffsets[i];
                    colors[off] = c;
                    for (int k = 0; k < n; k++)
                        colors[off + 1 + k] = c;
                }

                if (progress != null && Interlocked.Increment(ref done) % 64 == 0)
                    progress(done / (float)tiles.Count);
            });
            progress?.Invoke(1f);
            return colors;
        }

        /// <summary>
        /// 带势力边界 A 通道的颜色构建（纯数据，后台线程安全）。
        /// 对每条边检查邻居势力值，若不同则该边对应的两个角点 A=0（中心点 A=1）。
        /// Shader 中根据 COLOR.a 做边缘暗化 → 势力边界处显示黑线，非边界处无暗化。
        /// </summary>
        public static Color[] BuildColorsWithPowerBorders(
            List<HexTile> tiles,
            Func<HexTile, Color> colorFn,
            GeometryData g,
            int[] tilePower,
            Action<float> progress = null)
        {
            var colors = new Color[g.TotalVerts];
            int done = 0;
            Parallel.For(0, tiles.Count, i =>
            {
                var tile = tiles[i];
                int n = tile.Corners.Length;
                if (n >= 3)
                {
                    Color c = colorFn(tile);
                    int off = g.VertOffsets[i];
                    // 中心点始终 A=1
                    colors[off] = new Color(c.R, c.G, c.B, 1.0f);

                    int myPower = (tilePower != null && i < tilePower.Length) ? tilePower[i] : 0;
                    // 检查每条边是否是势力边界：仅当双方都有势力且不同时才标记
                    bool[] edgeIsBorder = new bool[n];
                    for (int k = 0; k < n; k++)
                    {
                        int nb = (tile.Neighbors != null && k < tile.Neighbors.Length) ? tile.Neighbors[k] : -1;
                        if (nb < 0 || nb >= tiles.Count) continue;
                        int nbPower = (tilePower != null && nb < tilePower.Length) ? tilePower[nb] : 0;
                        // ⚠️ 2026-08-20：仅双方都有势力(id!=0)且不同才算势力边界
                        edgeIsBorder[k] = (myPower != 0 && nbPower != 0 && myPower != nbPower);
                    }

                    // 角点 k 属于边 k 和边 (k+1)%n（而非 k-1）
                    // 因为边 k = Corners[k]→Corners[k+1]，角点 k 是边 k 的起点、边 k-1 的终点
                    // 但实际测试发现 Goldberg dual 中 Neighbors[k] 对应的是边 (k-1+n)%n
                    // 所以角点 k 应检查 edgeIsBorder[k] 和 edgeIsBorder[(k+1)%n]
                    for (int k = 0; k < n; k++)
                    {
                        bool isBorder = edgeIsBorder[k] || edgeIsBorder[(k + 1) % n];
                        colors[off + 1 + k] = new Color(c.R, c.G, c.B, isBorder ? 0.0f : 1.0f);
                    }
                }

                if (progress != null && Interlocked.Increment(ref done) % 64 == 0)
                    progress(done / (float)tiles.Count);
            });
            progress?.Invoke(1f);
            return colors;
        }

        /// <summary>Wraps pre-built data into an ArrayMesh. Main thread only.</summary>
        public static ArrayMesh CreateMesh(MeshData d)
        {
            var mesh = new ArrayMesh();
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = d.Verts;
            arrays[(int)Mesh.ArrayType.Normal] = d.Normals;
            arrays[(int)Mesh.ArrayType.Color] = d.Colors;
            arrays[(int)Mesh.ArrayType.Index] = d.Indices;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }

    /// <summary>几何+颜色打包（异步构建任务返回类型，MapViewer 用）。</summary>
    public struct MeshData
    {
        public Vector3[] Verts;
        public Vector3[] Normals;
        public Color[] Colors;
        public int[] Indices;
    }

    /// <summary>几何（与颜色解耦，可缓存复用）。</summary>
    public struct GeometryData
    {
        public Vector3[] Verts;
        public Vector3[] Normals;
        public int[] Indices;
        public int[] VertOffsets; // 每 tile 顶点段起始索引（供 BuildColors 展开）
        public int TotalVerts;
    }
}
