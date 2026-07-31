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
        var drawnEdges = new HashSet<long>();
        long totalTris = 0;
        int totalEdges = 0;
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

            var edgeMesh = ChunkMeshBuilder.BuildEdgeMesh(
                subset, faceElev, radiusKm, elevationScaleKm, drawnEdges, out int edges);
            totalEdges += edges;

            var instance = new MeshInstance3D
            {
                Name = $"Chunk{f}",
                Mesh = mesh,
                MaterialOverride = new ShaderMaterial { Shader = shader }
            };
            AddChild(instance);

            // Edge overlay (drawn once per shared edge, dark unshaded lines)
            if (edgeMesh != null)
            {
                var edgeMaterial = new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor = new Color(0.03f, 0.04f, 0.06f),
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled
                };
                var edgeInstance = new MeshInstance3D
                {
                    Name = $"Chunk{f}_Edges",
                    Mesh = edgeMesh,
                    MaterialOverride = edgeMaterial
                };
                AddChild(edgeInstance);
            }
        }

        GD.Print($"[ChunkPlanet] chunks={baseFaces.Count} tiles={tiles.Count} tris={totalTris} edges={totalEdges}");
    }
}
