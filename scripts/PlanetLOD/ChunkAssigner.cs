using Godot;
using System.Collections.Generic;

/// <summary>
/// Assigns a point on the sphere to the base-grid triangle (chunk) containing it.
/// Two-level lookup: 20 icosahedron top faces, then baseSubdiv² sub-faces inside.
/// Sub-faces are laid out contiguously in baseFaces (one top face's grid per block,
/// matching the generation order of Icosahedron.Subdivide).
/// </summary>
public class ChunkAssigner
{
    private readonly List<Vector3> _verts;          // base-grid vertices (indices 0..11 = icosa vertices)
    private readonly List<(int a, int b, int c)> _faces; // all base sub-faces
    private readonly int _facesPerTop;              // baseSubdiv²

    public ChunkAssigner(List<Vector3> baseVerts, List<(int a, int b, int c)> baseFaces, int baseSubdiv)
    {
        _verts = baseVerts;
        _faces = baseFaces;
        _facesPerTop = baseSubdiv * baseSubdiv;
    }

    public int FindFace(Vector3 p)
    {
        // Level 1: which of the 20 icosahedron faces contains p?
        int top = -1;
        for (int t = 0; t < Icosahedron.BaseFaces.Count; t++)
        {
            var f = Icosahedron.BaseFaces[t];
            if (PointInTri(p, _verts[f.Item1], _verts[f.Item2], _verts[f.Item3]))
            {
                top = t;
                break;
            }
        }
        if (top < 0)
            top = 0; // numeric edge case — never in practice

        // Level 2: sub-face inside that top face's contiguous block
        int start = top * _facesPerTop;
        for (int i = start; i < start + _facesPerTop; i++)
        {
            var f = _faces[i];
            if (PointInTri(p, _verts[f.a], _verts[f.b], _verts[f.c]))
                return i;
        }
        return start; // numeric edge case
    }

    /// <summary>
    /// Signed-area barycentric containment test in 3D (works for near-planar triangles).
    /// </summary>
    private static bool PointInTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 n = (b - a).Cross(c - a);
        float wa = (b - p).Cross(c - p).Dot(n);
        float wb = (c - p).Cross(a - p).Dot(n);
        float wc = (a - p).Cross(b - p).Dot(n);
        return (wa >= 0f && wb >= 0f && wc >= 0f) || (wa <= 0f && wb <= 0f && wc <= 0f);
    }
}
