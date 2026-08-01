using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace World.HexPlanet
{

    public class GoldbergBuilder
    {
        public List<HexTile> Tiles { get; }

        /// <summary>
        /// 每个 tile 的构建只读共享网格数据、只写自己的数组槽位 → 完全独立，
        /// 用 Parallel.For 并行加速（n 大时收益明显）。
        /// </summary>
        public GoldbergBuilder(SubdividedMesh mesh, float radius, Action<float> progress = null)
        {
            int vc = mesh.UniqueVerts.Count;
            var tilesArr = new HexTile[vc];
            int done = 0;

            Parallel.For(0, vc, vertexIndex =>
            {
                var triangleIndices = mesh.VertToTris[vertexIndex];
                if (triangleIndices.Count >= 3)
                {
                    var centers = new List<Vector3>();
                    var faceIndices = new List<int>();
                    var adjacentVertices = new HashSet<int>();
                    foreach (var faceIndex in triangleIndices)
                    {
                        var tri = mesh.Tris[faceIndex];
                        Vector3 a = mesh.UniqueVerts[tri.v0];
                        Vector3 b = mesh.UniqueVerts[tri.v1];
                        Vector3 c = mesh.UniqueVerts[tri.v2];
                        Vector3 circumcenter = ComputeTriangleCircumcenter(a, b, c, radius);
                        centers.Add(circumcenter);
                        faceIndices.Add(faceIndex);

                        if (tri.v0 != vertexIndex) adjacentVertices.Add(tri.v0);
                        if (tri.v1 != vertexIndex) adjacentVertices.Add(tri.v1);
                        if (tri.v2 != vertexIndex) adjacentVertices.Add(tri.v2);
                    }

                    Vector3 centerDirection = mesh.UniqueVerts[vertexIndex].Normalized();
                    Vector3 tangent = centerDirection.Cross(Vector3.Up);
                    if (tangent.LengthSquared() < 0.001f)
                        tangent = centerDirection.Cross(Vector3.Right);
                    tangent = tangent.Normalized();
                    Vector3 bitangent = centerDirection.Cross(tangent).Normalized();

                    var cornerEntries = new List<(Vector3 corner, int faceIndex)>();
                    for (int i = 0; i < centers.Count; i++)
                        cornerEntries.Add((centers[i], faceIndices[i]));

                    cornerEntries.Sort((x, y) =>
                    {
                        Vector3 dx = (x.corner - mesh.UniqueVerts[vertexIndex]).Normalized();
                        Vector3 dy = (y.corner - mesh.UniqueVerts[vertexIndex]).Normalized();
                        float angleX = Mathf.Atan2(dx.Dot(bitangent), dx.Dot(tangent));
                        float angleY = Mathf.Atan2(dy.Dot(bitangent), dy.Dot(tangent));
                        return angleX.CompareTo(angleY);
                    });

                    var sortedCenters = new List<Vector3>(cornerEntries.Count);
                    var sortedFaceIndices = new List<int>(cornerEntries.Count);
                    foreach (var entry in cornerEntries)
                    {
                        sortedCenters.Add(entry.corner);
                        sortedFaceIndices.Add(entry.faceIndex);
                    }

                    var neighborList = new List<int>(adjacentVertices);
                    neighborList.Sort((u, v) =>
                    {
                        Vector3 du = (mesh.UniqueVerts[u] - mesh.UniqueVerts[vertexIndex]).Normalized();
                        Vector3 dv = (mesh.UniqueVerts[v] - mesh.UniqueVerts[vertexIndex]).Normalized();
                        float angleU = Mathf.Atan2(du.Dot(bitangent), du.Dot(tangent));
                        float angleV = Mathf.Atan2(dv.Dot(bitangent), dv.Dot(tangent));
                        return angleU.CompareTo(angleV);
                    });

                    tilesArr[vertexIndex] = new HexTile
                    {
                        Id = vertexIndex,
                        Center = mesh.UniqueVerts[vertexIndex].Normalized() * radius,
                        Corners = sortedCenters.ToArray(),
                        CornerFaceIndices = sortedFaceIndices.ToArray(),
                        Neighbors = neighborList.ToArray(),
                        IsPentagon = neighborList.Count == 5
                    };
                }

                if (progress != null && Interlocked.Increment(ref done) % 64 == 0)
                    progress(done / (float)vc);
            });
            progress?.Invoke(1f);

            var tiles = new List<HexTile>(vc);
            foreach (var t in tilesArr)
                if (t != null)
                    tiles.Add(t);
            Tiles = tiles;
        }

        private static Vector3 ComputeTriangleCircumcenter(Vector3 a, Vector3 b, Vector3 c, float radius)
        {
            // Use the triangle centroid (average of vertices) projected onto the sphere.
            Vector3 centroid = (a + b + c) / 3f;
            return centroid.Normalized() * radius;
        }
    }
}
