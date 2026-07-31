using Godot;
using System.Collections.Generic;

/// <summary>
/// Indexed ArrayMesh planet — fully manual mesh construction.
///
/// Pipeline:
///   1. Icosahedron subdivision → vertices projected onto sphere.
///   2. Goldberg dual → hexagon/pentagon tiles (center + corners).
///   3. Each polygon split into a triangle fan (center, corner_i, corner_{i+1}).
///   4. Shared vertices (tile centers, face-centroid corners) are indexed once.
///   5. Winding order checked per triangle so normals always face outward
///      (fixes the southern-hemisphere missing-face problem at the source).
///   6. Vertices displaced radially by elevation; colored by elevation.
/// </summary>
public partial class IndexedPlanet : Node3D
{
    [Export] private int subdivisions = 96;
    [Export] private float radiusKm = 6330f;
    [Export] private int seed = 42;
    [Export] private float elevationScaleKm = 10f;

    public override void _Ready()
    {
        Generate();
    }

    private void Generate()
    {
        // ── 1. Topology: subdivided icosahedron + Goldberg dual ──
        Icosahedron.Subdivide(subdivisions, radiusKm, out var verts, out var indices);
        var subdividedMesh = new SubdividedMesh(verts, indices);
        var builder = new GoldbergBuilder(subdividedMesh, radiusKm);
        var tiles = builder.Tiles;

        // ── 2. Elevation (normalized [-1, 1]) ──
        var surfaceGen = new SurfaceGenerator(seed);
        surfaceGen.ApplyElevation(tiles);

        var tileElev = new float[tiles.Count];
        foreach (var t in tiles)
            tileElev[t.Id] = t.Elevation;

        var faceElev = new float[subdividedMesh.Tris.Count];
        for (int i = 0; i < subdividedMesh.Tris.Count; i++)
        {
            var tri = subdividedMesh.Tris[i];
            faceElev[i] = (tileElev[tri.v0] + tileElev[tri.v1] + tileElev[tri.v2]) / 3f;
        }

        // ── 3. Build indexed vertex list ──
        var meshVerts = new List<Vector3>();
        var meshNormals = new List<Vector3>();
        var meshColors = new List<Color>();
        var meshIndices = new List<int>();

        // Each corner = centroid of one triangle face → keyed by face index
        var cornerVert = new int[subdividedMesh.Tris.Count];
        for (int f = 0; f < cornerVert.Length; f++) cornerVert[f] = -1;

        // Each tile center → keyed by tile id
        var centerVert = new int[tiles.Count];
        for (int t = 0; t < centerVert.Length; t++) centerVert[t] = -1;

        // Helper: add a vertex (displaced by elevation) and return its index
        int AddVertex(Vector3 spherePoint, float elev)
        {
            Vector3 dir = spherePoint.Normalized();
            Vector3 pos = dir * (radiusKm + elev * elevationScaleKm);
            meshVerts.Add(pos);
            meshNormals.Add(dir);
            meshColors.Add(ElevationToColor(elev));
            return meshVerts.Count - 1;
        }

        foreach (var tile in tiles)
        {
            // Tile center vertex
            if (centerVert[tile.Id] == -1)
                centerVert[tile.Id] = AddVertex(tile.Center, tileElev[tile.Id]);

            // Corner vertices (shared across tiles — added once per face)
            for (int i = 0; i < tile.Corners.Length; i++)
            {
                int faceIdx = tile.CornerFaceIndices[i];
                if (cornerVert[faceIdx] == -1)
                    cornerVert[faceIdx] = AddVertex(tile.Corners[i], faceElev[faceIdx]);
            }
        }

        // ── 4. Build triangle indices with winding correction ──
        foreach (var tile in tiles)
        {
            if (tile.Corners == null || tile.Corners.Length < 3)
                continue;

            int centerIdx = centerVert[tile.Id];
            int cornerCount = tile.Corners.Length;

            for (int i = 0; i < cornerCount; i++)
            {
                int aIdx = cornerVert[tile.CornerFaceIndices[i]];
                int bIdx = cornerVert[tile.CornerFaceIndices[(i + 1) % cornerCount]];

                Vector3 va = meshVerts[aIdx];
                Vector3 vb = meshVerts[bIdx];
                Vector3 vc = meshVerts[centerIdx];

                // Winding check: normal must point away from sphere center
                Vector3 normal = (va - vc).Cross(vb - vc);
                if (normal.Dot(vc) < 0f)
                {
                    meshIndices.Add(centerIdx);
                    meshIndices.Add(bIdx);
                    meshIndices.Add(aIdx);
                }
                else
                {
                    meshIndices.Add(centerIdx);
                    meshIndices.Add(aIdx);
                    meshIndices.Add(bIdx);
                }
            }
        }

        // ── 5. Smooth normals from displaced geometry ──
        // Sphere-direction normals hide terrain relief (10km bumps vs 6330km radius ≈ 0.1° tilt).
        // Area-weighted face normals make mountains/valleys visible under lighting.
        var geomNormals = new Vector3[meshVerts.Count];
        for (int i = 0; i < meshIndices.Count; i += 3)
        {
            int ia = meshIndices[i];
            int ib = meshIndices[i + 1];
            int ic = meshIndices[i + 2];
            Vector3 n = (meshVerts[ib] - meshVerts[ia]).Cross(meshVerts[ic] - meshVerts[ia]);
            geomNormals[ia] += n;
            geomNormals[ib] += n;
            geomNormals[ic] += n;
        }
        for (int i = 0; i < meshNormals.Count; i++)
            meshNormals[i] = geomNormals[i].Normalized();

        // ── 6. Create ArrayMesh ──
        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = meshVerts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = meshNormals.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = meshColors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = meshIndices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // ── 6. Material: lit (default shading) so terrain relief shows via light/shadow ──
        var material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled // safety net
        };

        var instance = new MeshInstance3D
        {
            Name = "PlanetMesh",
            Mesh = mesh,
            MaterialOverride = material
        };
        AddChild(instance);

        GD.Print($"[IndexedPlanet] verts={meshVerts.Count}  indices={meshIndices.Count}  tris={meshIndices.Count / 3}  tiles={tiles.Count}");
    }

    /// <summary>
    /// Elevation color ramp (normalized [-1, 1]): deep ocean → ocean → beach → lowland → highland → snow.
    /// </summary>
    private static Color ElevationToColor(float e)
    {
        if (e < -0.2f)
        {
            float t = Mathf.Clamp((-e - 0.2f) / 0.8f, 0f, 1f);
            return new Color(0.02f, 0.10f, 0.25f).Lerp(new Color(0.06f, 0.35f, 0.60f), 1f - t);
        }
        if (e < 0.0f)
        {
            float t = (e + 0.2f) / 0.2f;
            return new Color(0.06f, 0.35f, 0.60f).Lerp(new Color(0.70f, 0.65f, 0.40f), t);
        }
        if (e < 0.3f)
        {
            float t = e / 0.3f;
            return new Color(0.70f, 0.65f, 0.40f).Lerp(new Color(0.30f, 0.65f, 0.10f), t);
        }
        if (e < 0.6f)
        {
            float t = (e - 0.3f) / 0.3f;
            return new Color(0.30f, 0.65f, 0.10f).Lerp(new Color(0.50f, 0.50f, 0.08f), t);
        }
        float s = Mathf.Clamp((e - 0.6f) / 0.4f, 0f, 1f);
        return new Color(0.50f, 0.50f, 0.08f).Lerp(new Color(0.95f, 0.97f, 1.00f), s);
    }
}
