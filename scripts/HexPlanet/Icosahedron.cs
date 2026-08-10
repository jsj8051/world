using Godot;
using System;
using System.Collections.Generic;

namespace World.HexPlanet
{

    /// <summary>
    /// Generates a subdivided icosahedron mesh.
    ///
    /// The Subdivide method uses one-pass edge subdivision (not iterative loop subdivision).
    /// frequency = number of segments each icosahedron edge is divided into.
    ///
    /// Result: 20 × n² triangles, ~10n² + 2 unique vertices.
    /// n=16 → 5,120 triangles, 2,562 tiles.
    /// n=64 → 81,920 triangles, 40,962 tiles.
    /// </summary>
    public static class Icosahedron
    {
        /// <summary>
        /// Subdivide an icosahedron by dividing each edge into `n` segments.
        /// Generates a triangular grid within each face, deduplicated at edges/corners.
        /// </summary>
        public static void Subdivide(int n, float radius, out List<Vector3> verts, out List<int> indices)
        {
            var baseVerts = StandardTwelvePoints(radius);
            Subdivide(baseVerts, BaseFaces, n, radius, out verts, out indices);
        }

        /// <summary>
        /// Subdivide a base mesh with given vertices and faces.
        /// </summary>
        public static void Subdivide(List<Vector3> baseVerts, List<(int, int, int)> baseFaces, int n, float radius, out List<Vector3> verts, out List<int> indices)
        {
            verts = new List<Vector3>();
            indices = new List<int>();
            var cache = new Dictionary<string, int>();

            // Pre-populate cache with base vertices
            for (int i = 0; i < baseVerts.Count; i++)
                cache[VertexKey(baseVerts[i])] = i;

            verts.AddRange(baseVerts);

            float invN = 1f / n;

            foreach (var face in baseFaces)
            {
                Vector3 v0 = baseVerts[face.Item1];
                Vector3 v1 = baseVerts[face.Item2];
                Vector3 v2 = baseVerts[face.Item3];

                // ── Generate all points for this face ──
                // grid[i][j] = vertex index for point at barycentric (1-i/n-j/n, i/n, j/n)
                var grid = new List<List<int>>(n + 1);
                for (int i = 0; i <= n; i++)
                {
                    var row = new List<int>(n - i + 1);
                    for (int j = 0; j <= n - i; j++)
                    {
                        float w0 = 1f - i * invN - j * invN;
                        float w1 = i * invN;
                        float w2 = j * invN;

                        Vector3 pt = (v0 * w0 + v1 * w1 + v2 * w2).Normalized() * radius;
                        int idx = GetOrCreateVertex(pt, verts, cache);
                        row.Add(idx);
                    }
                    grid.Add(row);
                }

                // ── Generate triangles ──
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n - i; j++)
                    {
                        int p00 = grid[i][j];
                        int p10 = grid[i + 1][j];
                        int p01 = grid[i][j + 1];

                        // Lower triangle: (p00, p10, p01) — CCW from outside
                        indices.Add(p00);
                        indices.Add(p10);
                        indices.Add(p01);

                        if (j < n - i - 1)
                        {
                            int p11 = grid[i + 1][j + 1];
                            // Upper triangle: (p10, p11, p01) — CCW from outside
                            indices.Add(p10);
                            indices.Add(p11);
                            indices.Add(p01);
                        }
                    }
                }
            }

            GD.Print($"[Icosahedron] n={n} verts={verts.Count} tris={indices.Count / 3}  (expect verts≈{10 * n * n + 2}, tris={20 * n * n})");
        }

        /// <summary>
        /// Generate the 12 standard icosahedron vertices scaled to radius.
        /// </summary>
        private static List<Vector3> StandardTwelvePoints(float radius)
        {
            var points = TwelvePoints();
            for (int i = 0; i < points.Count; i++)
                points[i] = points[i].Normalized() * radius;
            return points;
        }

        /// <summary>
        /// Standard twelve vertices of an icosahedron (not normalized).
        /// </summary>
        private static List<Vector3> TwelvePoints()
        {
            var verts = new List<Vector3>();
            float phi = (1f + Mathf.Sqrt(5f)) / 2f;

            for (int axis = 0; axis < 3; axis++)
            {
                for (int s1 = 0; s1 < 2; s1++)
                {
                    for (int s2 = 0; s2 < 2; s2++)
                    {
                        float[] v = new float[3];
                        v[axis] = 0;
                        v[(axis + 1) % 3] = (s1 == 0 ? 1 : -1);
                        v[(axis + 2) % 3] = (s2 == 0 ? phi : -phi);
                        verts.Add(new Vector3(v[0], v[1], v[2]));
                    }
                }
            }
            return verts;
        }

        /// <summary>
        /// The 20 faces of an icosahedron as vertex index triples.
        /// </summary>
        public static readonly List<(int, int, int)> BaseFaces = new()
    {
        (0, 2, 4), (0, 2, 5), (0, 4, 8), (0, 5, 10), (0, 8, 10),
        (1, 3, 6), (1, 3, 7), (1, 6, 8), (1, 7, 10), (1, 8, 10),
        (2, 4, 9), (2, 5, 11), (2, 9, 11), (3, 6, 9), (3, 7, 11),
        (3, 9, 11), (4, 6, 8), (4, 6, 9), (5, 7, 10), (5, 7, 11)
    };

        private static string VertexKey(Vector3 v)
        {
            // Quantize to 1km cells. Coordinates are in km units (radius 6371 = 6371km),
            // so key = round(v): 1 key unit = 1km.
            // Cell must exceed float noise (~1-3m at radius 6371km: ULP≈0.5m +
            // Normalized/barycentric chain error) or identical vertices from adjacent
            // faces land in different cells — observed: subdivisions=96 produced 780
            // duplicate pairs (dist 0..1m) at 0.1m cells, 16 pairs at 100m cells.
            // 1km is still << nearest vertex spacing (~69km at n=96), so no false merges.
            long x = (long)Math.Round((double)v.X);
            long y = (long)Math.Round((double)v.Y);
            long z = (long)Math.Round((double)v.Z);
            return $"{x}|{y}|{z}";
        }

        private static int GetOrCreateVertex(Vector3 vertex, List<Vector3> verts, Dictionary<string, int> cache)
        {
            var key = VertexKey(vertex);
            if (cache.TryGetValue(key, out int index))
                return index;
            index = verts.Count;
            verts.Add(vertex);
            cache[key] = index;
            return index;
        }

        // ── Keep the old iterative Subdivide for backward compat ──
        // Converts iteration count to edge divisions via n = 2^frequency.
        // Used by HexPlanetGenerator and any existing code.

        public static void SubdivideIterative(List<Vector3> baseVerts, List<(int, int, int)> baseFaces, int frequency, float radius, out List<Vector3> verts, out List<int> indices)
        {
            int n = 1 << frequency;
            Subdivide(baseVerts, baseFaces, n, radius, out verts, out indices);
        }

        public static void SubdivideIterative(int frequency, float radius, out List<Vector3> verts, out List<int> indices)
        {
            int n = 1 << frequency;
            Subdivide(n, radius, out verts, out indices);
        }
    }
}
