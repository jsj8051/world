using Godot;
using System.Collections.Generic;

/// <summary>
/// Goldberg hex/pentagon planet — flat per-tile coloring so the grid reads crisply.
///
/// Pipeline:
///   1. Icosahedron subdivision → vertices projected onto sphere.
///   2. Goldberg dual → hexagon/pentagon tiles (center + corners).
///   3. Each polygon split into a triangle fan (center, corner_i, corner_{i+1}).
///   4. Vertices are SPLIT per tile (not shared) so every tile gets ONE flat color —
///      tile boundaries appear as sharp cells and the Goldberg structure is visible.
///      Corner positions still use face-average elevation, so geometry stays
///      watertight (no cracks between tiles).
///   5. Winding checked per triangle so normals always face outward.
///   6. Vertices displaced radially by elevation; pentagon tiles tinted red
///      (toggleable) to verify the 12 pentagons.
///   7. Material: shader mixing flat vertex color with procedural 3D noise detail.
/// </summary>
public partial class IndexedPlanet : Node3D
{
    [Export] private int subdivisions = 96;
    [Export] private float radiusKm = 6330f;
    [Export] private int seed = 42;
    [Export] private float elevationScaleKm = 10f;
    [Export] private bool highlightPentagons = true;

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

        // Corner (face-centroid) elevation = mean of its 3 tiles → geometry continuity
        var faceElev = new float[subdividedMesh.Tris.Count];
        for (int i = 0; i < subdividedMesh.Tris.Count; i++)
        {
            var tri = subdividedMesh.Tris[i];
            faceElev[i] = (tileElev[tri.v0] + tileElev[tri.v1] + tileElev[tri.v2]) / 3f;
        }

        // ── 3. Build mesh — split vertices per tile (flat per-tile color) ──
        var meshVerts = new List<Vector3>();
        var meshColors = new List<Color>();
        var meshIndices = new List<int>();

        foreach (var tile in tiles)
        {
            int n = tile.Corners.Length;
            if (n < 3)
                continue;

            Color tileColor = PlanetColors.ElevationToColor(tile.Elevation);
            if (highlightPentagons && tile.IsPentagon)
                tileColor = tileColor.Lerp(Colors.Red, 0.55f);

            // Tile center vertex
            Vector3 centerPos = tile.Center.Normalized() * (radiusKm + tile.Elevation * elevationScaleKm);
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

        // ── 4. Normals from displaced geometry (split verts → flat facets) ──
        // Each vertex only sees its own tile's triangles, so faces shade as flat
        // cells — the low-poly Goldberg look.
        var accNormals = new Vector3[meshVerts.Count];
        for (int i = 0; i < meshIndices.Count; i += 3)
        {
            int ia = meshIndices[i];
            int ib = meshIndices[i + 1];
            int ic = meshIndices[i + 2];
            Vector3 n = (meshVerts[ib] - meshVerts[ia]).Cross(meshVerts[ic] - meshVerts[ia]);
            accNormals[ia] += n;
            accNormals[ib] += n;
            accNormals[ic] += n;
        }
        var meshNormals = new Vector3[meshVerts.Count];
        for (int i = 0; i < meshVerts.Count; i++)
            meshNormals[i] = accNormals[i].Normalized();

        // ── 5. Create ArrayMesh ──
        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = meshVerts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = meshNormals;
        arrays[(int)Mesh.ArrayType.Color] = meshColors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = meshIndices.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // ── 6. Material: flat vertex color + procedural noise detail ──
        var material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://shaders/planet_detail.gdshader")
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
}
