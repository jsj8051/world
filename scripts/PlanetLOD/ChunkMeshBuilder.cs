using Godot;
using System.Collections.Generic;

/// <summary>
/// Builds a flat per-tile colored ArrayMesh from a subset of tiles.
/// Same construction rules as IndexedPlanet: vertices split per tile, one flat
/// color per tile, triangle fan with outward winding, geometry normals from
/// displaced positions (flat facet look).
/// </summary>
public static class ChunkMeshBuilder
{
    public static ArrayMesh BuildMesh(
        List<HexTile> tiles,
        float[] tileElev,
        float[] faceElev,
        float radiusKm,
        float elevationScaleKm,
        bool highlightPentagons,
        out int triCount)
    {
        var meshVerts = new List<Vector3>();
        var meshColors = new List<Color>();
        var meshIndices = new List<int>();

        foreach (var tile in tiles)
        {
            int n = tile.Corners.Length;
            if (n < 3)
                continue;

            Color tileColor = PlanetColors.ElevationToColor(tileElev[tile.Id]);
            if (highlightPentagons && tile.IsPentagon)
                tileColor = tileColor.Lerp(Colors.Red, 0.55f);

            // Tile center vertex
            Vector3 centerPos = tile.Center.Normalized() * (radiusKm + tileElev[tile.Id] * elevationScaleKm);
            int centerIdx = meshVerts.Count;
            meshVerts.Add(centerPos);
            meshColors.Add(tileColor);

            // Corner vertices (position continuous across tiles, color is this tile's)
            int firstCorner = meshVerts.Count;
            for (int i = 0; i < n; i++)
            {
                float fe = faceElev[tile.CornerFaceIndices[i]];
                Vector3 p = tile.Corners[i].Normalized() * (radiusKm + fe * elevationScaleKm);
                meshVerts.Add(p);
                meshColors.Add(tileColor);
            }

            // Triangle fan with winding correction (normal must point away from center)
            for (int i = 0; i < n; i++)
            {
                int a = firstCorner + i;
                int b = firstCorner + (i + 1) % n;
                Vector3 va = meshVerts[a];
                Vector3 vb = meshVerts[b];
                Vector3 vc = meshVerts[centerIdx];

                Vector3 normal = (va - vc).Cross(vb - vc);
                if (normal.Dot(vc) < 0f)
                {
                    meshIndices.Add(centerIdx);
                    meshIndices.Add(b);
                    meshIndices.Add(a);
                }
                else
                {
                    meshIndices.Add(centerIdx);
                    meshIndices.Add(a);
                    meshIndices.Add(b);
                }
            }
        }

        // Normals from displaced geometry (split verts → flat facets)
        var accNormals = new Vector3[meshVerts.Count];
        for (int i = 0; i < meshIndices.Count; i += 3)
        {
            int ia = meshIndices[i];
            int ib = meshIndices[i + 1];
            int ic = meshIndices[i + 2];
            Vector3 nrm = (meshVerts[ib] - meshVerts[ia]).Cross(meshVerts[ic] - meshVerts[ia]);
            accNormals[ia] += nrm;
            accNormals[ib] += nrm;
            accNormals[ic] += nrm;
        }
        var meshNormals = new Vector3[meshVerts.Count];
        for (int i = 0; i < meshVerts.Count; i++)
            meshNormals[i] = accNormals[i].Normalized();

        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = meshVerts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = meshNormals;
        arrays[(int)Mesh.ArrayType.Color] = meshColors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = meshIndices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        triCount = meshIndices.Count / 3;
        return mesh;
    }
}
