using Godot;
using System.Collections.Generic;

/// <summary>
/// Chunked Goldberg planet — stage 1: static chunking.
///
/// The planet is generated once at `subdivisions` (a multiple of `baseSubdiv`),
/// Goldberg-dualed, then every tile is assigned to one of the 20×baseSubdiv²
/// base-grid triangles (chunks). Each chunk gets its own MeshInstance3D →
/// per-chunk frustum culling, and the chunk structure is the foundation for
/// per-chunk LOD (each chunk rebuilt at its own detail level) later.
///
/// Tile→chunk assignment: tile center vertex's containing base triangle
/// (two-level barycentric lookup). Corner positions stay globally consistent
/// (face-centroid cache), so chunk boundaries are watertight.
/// </summary>
public partial class ChunkPlanet : Node3D
{
    [Export] private int baseSubdiv = 8;      // chunk grid: 20 × 8² = 1280 chunks
    [Export] private int subdivisions = 256;  // global mesh resolution (multiple of baseSubdiv)
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
        // ── 1. Base grid (chunk layout) ──
        Icosahedron.Subdivide(baseSubdiv, radiusKm, out var baseVerts, out var baseIndices);
        var baseFaces = new List<(int a, int b, int c)>(baseIndices.Count / 3);
        for (int i = 0; i < baseIndices.Count; i += 3)
            baseFaces.Add((baseIndices[i], baseIndices[i + 1], baseIndices[i + 2]));

        // ── 2. Global grid + Goldberg dual ──
        Icosahedron.Subdivide(subdivisions, radiusKm, out var verts, out var indices);
        var subdividedMesh = new SubdividedMesh(verts, indices);
        var builder = new GoldbergBuilder(subdividedMesh, radiusKm);
        var tiles = builder.Tiles;

        // ── 3. Elevation ──
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

        // ── 4. Assign tiles to chunks ──
        var assigner = new ChunkAssigner(baseVerts, baseFaces, baseSubdiv);
        var tilesPerChunk = new List<List<int>>(baseFaces.Count);
        for (int f = 0; f < baseFaces.Count; f++)
            tilesPerChunk.Add(new List<int>());

        for (int i = 0; i < tiles.Count; i++)
        {
            int f = assigner.FindFace(subdividedMesh.UniqueVerts[i]);
            tilesPerChunk[f].Add(i);
        }

        // ── 5. Per-chunk meshes ──
        var shader = GD.Load<Shader>("res://shaders/planet_detail.gdshader");
        long totalTris = 0;
        for (int f = 0; f < baseFaces.Count; f++)
        {
            var chunkTiles = tilesPerChunk[f];
            if (chunkTiles.Count == 0)
                continue;

            var subset = new List<HexTile>(chunkTiles.Count);
            foreach (int ti in chunkTiles)
                subset.Add(tiles[ti]);

            var mesh = ChunkMeshBuilder.BuildMesh(
                subset, tileElev, faceElev, radiusKm, elevationScaleKm, highlightPentagons,
                out int tris);
            totalTris += tris;

            var instance = new MeshInstance3D
            {
                Name = $"Chunk{f}",
                Mesh = mesh,
                MaterialOverride = new ShaderMaterial { Shader = shader }
            };
            AddChild(instance);
        }

        GD.Print($"[ChunkPlanet] chunks={baseFaces.Count} tiles={tiles.Count} tris={totalTris}");
    }
}
