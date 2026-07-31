using Godot;
using System.Collections.Generic;

public class SubdividedMesh
{
    public List<Vector3> UniqueVerts { get; }
    public List<(int v0, int v1, int v2)> Tris { get; }
    public List<List<int>> VertToTris { get; }
    public List<List<int>> TriNeighbors { get; }

    public SubdividedMesh(List<Vector3> verts, List<int> indices)
    {
        UniqueVerts = new List<Vector3>();
        Tris = new List<(int v0, int v1, int v2)>();

        var vertexLookup = new Dictionary<string, int>();
        for (int i = 0; i < indices.Count; i += 3)
        {
            int a = GetOrCreateVertex(verts[indices[i]], vertexLookup);
            int b = GetOrCreateVertex(verts[indices[i + 1]], vertexLookup);
            int c = GetOrCreateVertex(verts[indices[i + 2]], vertexLookup);
            Tris.Add((a, b, c));
        }

        VertToTris = new List<List<int>>(UniqueVerts.Count);
        for (int i = 0; i < UniqueVerts.Count; i++)
            VertToTris.Add(new List<int>());

        for (int triIndex = 0; triIndex < Tris.Count; triIndex++)
        {
            var tri = Tris[triIndex];
            VertToTris[tri.v0].Add(triIndex);
            VertToTris[tri.v1].Add(triIndex);
            VertToTris[tri.v2].Add(triIndex);
        }

        TriNeighbors = BuildTriangleNeighbors();
    }

    private int GetOrCreateVertex(Vector3 vertex, Dictionary<string, int> lookup)
    {
        var key = VertexKey(vertex);
        if (lookup.TryGetValue(key, out int index))
            return index;

        index = UniqueVerts.Count;
        UniqueVerts.Add(vertex);
        lookup[key] = index;
        return index;
    }

    private static string VertexKey(Vector3 vertex)
    {
        int x = Mathf.RoundToInt(vertex.X * 100000f);
        int y = Mathf.RoundToInt(vertex.Y * 100000f);
        int z = Mathf.RoundToInt(vertex.Z * 100000f);
        return $"{x}|{y}|{z}";
    }

    private List<List<int>> BuildTriangleNeighbors()
    {
        var neighbors = new List<List<int>>(Tris.Count);
        for (int i = 0; i < Tris.Count; i++)
            neighbors.Add(new List<int>());

        var edgeToTris = new Dictionary<(int, int), List<int>>();

        for (int triIndex = 0; triIndex < Tris.Count; triIndex++)
        {
            var tri = Tris[triIndex];
            AddEdge(tri.v0, tri.v1, triIndex, edgeToTris);
            AddEdge(tri.v1, tri.v2, triIndex, edgeToTris);
            AddEdge(tri.v2, tri.v0, triIndex, edgeToTris);
        }

        foreach (var kvp in edgeToTris)
        {
            var triIndices = kvp.Value;
            if (triIndices.Count == 2)
            {
                int a = triIndices[0];
                int b = triIndices[1];
                neighbors[a].Add(b);
                neighbors[b].Add(a);
            }
        }

        return neighbors;
    }

    private void AddEdge(int a, int b, int triIndex, Dictionary<(int, int), List<int>> edgeToTris)
    {
        var key = a < b ? (a, b) : (b, a);
        if (!edgeToTris.TryGetValue(key, out var list))
        {
            list = new List<int>();
            edgeToTris[key] = list;
        }
        list.Add(triIndex);
    }
}
