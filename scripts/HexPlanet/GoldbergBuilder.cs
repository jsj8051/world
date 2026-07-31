using Godot;
using System.Collections.Generic;

public class GoldbergBuilder
{
    public List<HexTile> Tiles { get; }

    public GoldbergBuilder(SubdividedMesh mesh, float radius)
    {
        Tiles = new List<HexTile>(mesh.UniqueVerts.Count);

        for (int vertexIndex = 0; vertexIndex < mesh.UniqueVerts.Count; vertexIndex++)
        {
            var triangleIndices = mesh.VertToTris[vertexIndex];
            if (triangleIndices.Count < 3)
                continue;

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

            var tile = new HexTile
            {
                Id = vertexIndex,
                Center = mesh.UniqueVerts[vertexIndex].Normalized() * radius,
                Corners = sortedCenters.ToArray(),
                CornerFaceIndices = sortedFaceIndices.ToArray(),
                Neighbors = neighborList.ToArray(),
                IsPentagon = neighborList.Count == 5
            };

            Tiles.Add(tile);
        }
    }

    private static Vector3 ComputeTriangleCircumcenter(Vector3 a, Vector3 b, Vector3 c, float radius)
    {
        // Use the triangle centroid (average of vertices) projected onto the sphere.
        Vector3 centroid = (a + b + c) / 3f;
        return centroid.Normalized() * radius;
    }
}
