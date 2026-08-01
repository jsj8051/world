using Godot;
using System;
using System.Collections.Generic;
using World.HexPlanet;

namespace World.PlanetLOD
{

	/// <summary>
	/// Local Goldberg dual generation for one chunk.
	///
	/// A chunk is one triangle of the base grid (icosahedron subdivided baseSubdiv).
	/// To build its tiles we subdivide the chunk face PLUS a halo ring (all base
	/// faces touching its 3 corner vertices — 7 faces at n_base=8) at the chunk's
	/// own detail level n. Corner positions are shared across faces via a
	/// position-quantized key (1km cells), which also absorbs the ~1m float noise
	/// from different barycentric computation paths on shared edges — so chunk
	/// boundaries are watertight at any level.
	///
	/// Only tiles whose center vertex belongs to this chunk are emitted
	/// (ownerPredicate), so each tile is rendered exactly once.
	/// </summary>
	public class LocalGoldberg
	{
		public float Radius { get; }

		private readonly List<Vector3> _baseVerts;
		private readonly List<(int a, int b, int c)> _baseFaces;
		private readonly List<List<int>> _baseVertToFaces;

		public LocalGoldberg(List<Vector3> baseVerts, List<(int a, int b, int c)> baseFaces, float radius)
		{
			_baseVerts = baseVerts;
			_baseFaces = baseFaces;
			Radius = radius;

			_baseVertToFaces = new List<List<int>>(baseVerts.Count);
			for (int i = 0; i < baseVerts.Count; i++)
				_baseVertToFaces.Add(new List<int>());
			for (int f = 0; f < baseFaces.Count; f++)
			{
				var t = baseFaces[f];
				_baseVertToFaces[t.a].Add(f);
				_baseVertToFaces[t.b].Add(f);
				_baseVertToFaces[t.c].Add(f);
			}
		}

		/// <summary>
		/// Builds the tiles owned by chunk faceIdx at detail n.
		/// ownerPredicate decides tile ownership from the center position
		/// (call with ChunkAssigner.FindFace(p) == faceIdx for consistency).
		///
		/// Seam stitching: every halo face is subdivided at ITS OWN current level
		/// (this face → n, neighbor faces → faceLevel(neighbor)) instead of a
		/// uniform n. A boundary tile's corner is the centroid of a triangle on one
		/// side of the shared edge; that triangle is subdivided at the same level
		/// in BOTH chunks' builds, so both chunks produce bit-identical corners →
		/// adjacent chunks are watertight at ANY level combination.
		/// </summary>
		public List<HexTile> BuildChunkTiles(int faceIdx, int n, Func<Vector3, bool> ownerPredicate, Func<int, int> faceLevel)
		{
			var faces = CollectHaloFaces(faceIdx);
			var verts = new List<Vector3>();
			var cache = new Dictionary<(long, long, long), int>();
			var tris = new List<(int v0, int v1, int v2)>();

			// Base vertices of all halo faces go in first (stable indices).
			foreach (var f in faces)
			{
				var t = _baseFaces[f];
				GetOrCreate(_baseVerts[t.a], verts, cache);
				GetOrCreate(_baseVerts[t.b], verts, cache);
				GetOrCreate(_baseVerts[t.c], verts, cache);
			}

			foreach (var f in faces)
			{
				var t = _baseFaces[f];
				int lv = (f == faceIdx) ? n : faceLevel(f);
				if (lv < 2)
					lv = 2;
				SubdivideFace(f, faceIdx, _baseVerts[t.a], _baseVerts[t.b], _baseVerts[t.c], lv, verts, cache, tris);
			}

			// ── Local dual ──
			var vertToTris = new List<List<int>>(verts.Count);
			for (int i = 0; i < verts.Count; i++)
				vertToTris.Add(new List<int>());
			for (int ti = 0; ti < tris.Count; ti++)
			{
				var tr = tris[ti];
				vertToTris[tr.v0].Add(ti);
				vertToTris[tr.v1].Add(ti);
				vertToTris[tr.v2].Add(ti);
			}

			var tiles = new List<HexTile>();
			for (int v = 0; v < verts.Count; v++)
			{
				var ring = vertToTris[v];
				// A valid tile center needs a closed ring (>=5 for hex/pent grids).
				// Boundary-band vertices finer than the neighbor's level (e.g. a
				// 256-level point on a shared edge whose neighbor subdivides at 16)
				// only get triangles from this face — their neighbor-side ring is
				// missing, so they are NOT tile centers; that strip is covered by
				// the coarser boundary tiles instead. Skipping them removes the
				// malformed 3-corner tiles that looked like missing cells.
				if (ring.Count < 5)
					continue;
				if (!ownerPredicate(verts[v]))
					continue;

				// Corner = circumcenter (centroid projected onto sphere) of each ring face
				var centers = new List<Vector3>(ring.Count);
				for (int ti = 0; ti < ring.Count; ti++)
				{
					var tr = tris[ring[ti]];
					Vector3 centroid = (verts[tr.v0] + verts[tr.v1] + verts[tr.v2]) / 3f;
					centers.Add(centroid.Normalized() * Radius);
				}

				// Sort corners around the tile center (same scheme as GoldbergBuilder)
				Vector3 centerDir = verts[v].Normalized();
				Vector3 tangent = centerDir.Cross(Vector3.Up);
				if (tangent.LengthSquared() < 0.001f)
					tangent = centerDir.Cross(Vector3.Right);
				tangent = tangent.Normalized();
				Vector3 bitangent = centerDir.Cross(tangent).Normalized();

				var order = new int[ring.Count];
				for (int i = 0; i < ring.Count; i++) order[i] = i;
				Array.Sort(order, (x, y) =>
				{
					Vector3 dx = (centers[x] - verts[v]).Normalized();
					Vector3 dy = (centers[y] - verts[v]).Normalized();
					float ax = Mathf.Atan2(dx.Dot(bitangent), dx.Dot(tangent));
					float ay = Mathf.Atan2(dy.Dot(bitangent), dy.Dot(tangent));
					return ax.CompareTo(ay);
				});

				var sortedCenters = new Vector3[ring.Count];
				for (int i = 0; i < ring.Count; i++)
					sortedCenters[i] = centers[order[i]];

				tiles.Add(new HexTile
				{
					Id = v,
					Center = verts[v],
					Corners = sortedCenters,
					CornerFaceIndices = Array.Empty<int>(),
					Neighbors = Array.Empty<int>(),
					IsPentagon = ring.Count == 5
				});
			}
			return tiles;
		}

		/// <summary>
		/// The chunk face plus every base face touching its 3 corner vertices —
		/// the set of faces whose subdivision level affects this chunk's boundary
		/// corners. BuildChunkTiles subdivides exactly this set, so callers can
		/// snapshot levels for seam validation.
		/// </summary>
		public List<int> HaloFaces(int faceIdx)
		{
			var set = new HashSet<int> { faceIdx };
			var t = _baseFaces[faceIdx];
			foreach (int v in new[] { t.a, t.b, t.c })
			{
				foreach (int f in _baseVertToFaces[v])
					set.Add(f);
			}
			return new List<int>(set);
		}

		private List<int> CollectHaloFaces(int faceIdx)
		{
			return HaloFaces(faceIdx);
		}

		/// <summary>
		/// Subdivides one base face into an n×n triangle grid (same scheme as
		/// Icosahedron.Subdivide, per-face). Shared edge points match neighboring
		/// faces because endpoints and weights are identical; 1km quantization
		/// absorbs residual float noise.
		///
		/// mode 0 = full grid (the chunk's own face);
		/// mode 1 = only the row of triangles along the edge shared with the
		///          chunk face (halo neighbor: provides boundary-tile corners);
		/// mode 2 = only the single triangle at the corner shared with the chunk
		///          face (corner-adjacent halo face).
		/// This is the halo-band optimization: a n=256 neighbor contributes
		/// ~511 triangles instead of ~131k.
		/// </summary>
		private void SubdivideFace(int faceIdx, int chunkFaceIdx,
			Vector3 v0, Vector3 v1, Vector3 v2, int n,
			List<Vector3> verts, Dictionary<(long, long, long), int> cache, List<(int, int, int)> tris)
		{
			int mode;
			if (faceIdx == chunkFaceIdx)
			{
				mode = 0;
			}
			else
			{
				var tf = _baseFaces[chunkFaceIdx];
				int shared0 = -1, shared1 = -1;
				if (v0 == _baseVerts[tf.a] || v0 == _baseVerts[tf.b] || v0 == _baseVerts[tf.c]) shared0 = 0;
				if (v1 == _baseVerts[tf.a] || v1 == _baseVerts[tf.b] || v1 == _baseVerts[tf.c]) shared1 = 1;
				int sharedCount = (shared0 >= 0 ? 1 : 0) + (shared1 >= 0 ? 1 : 0);
				if (v2 == _baseVerts[tf.a] || v2 == _baseVerts[tf.b] || v2 == _baseVerts[tf.c])
					sharedCount++;
				if (sharedCount == 2)
				{
					mode = 1; // shared edge
					if (shared0 < 0) { Vector3 t = v0; v0 = v2; v2 = t; }
					else if (shared1 < 0) { Vector3 t = v1; v1 = v2; v2 = t; }
				}
				else
				{
					mode = 2; // shared corner only
					if (shared0 < 0)
					{
						if (shared1 >= 0) { Vector3 t = v0; v0 = v1; v1 = t; }
						else { Vector3 t = v0; v0 = v2; v2 = t; }
					}
				}
			}

			float invN = 1f / n;
			var grid = new List<int[]>();
			if (mode == 0)
			{
				for (int i = 0; i <= n; i++)
				{
					var row = new int[n - i + 1];
					for (int j = 0; j <= n - i; j++)
					{
						float w0 = 1f - i * invN - j * invN;
						float w1 = i * invN;
						float w2 = j * invN;
						Vector3 pt = (v0 * w0 + v1 * w1 + v2 * w2).Normalized() * Radius;
						row[j] = GetOrCreate(pt, verts, cache);
					}
					grid.Add(row);
				}
				for (int i = 0; i < n; i++)
				{
					for (int j = 0; j < n - i; j++)
					{
						int p00 = grid[i][j];
						int p10 = grid[i + 1][j];
						int p01 = grid[i][j + 1];
						tris.Add((p00, p10, p01));
						if (j < n - i - 1)
						{
							int p11 = grid[i + 1][j + 1];
							tris.Add((p10, p11, p01));
						}
					}
				}
			}
			else if (mode == 1)
			{
				// Shared edge (v0,v1) = the j=0 grid column. Only that column plus
				// the j=1 column of points is generated — the triangles along the
				// shared edge (j=0 row) provide the neighbor-side corners for the
				// chunk's boundary tiles.
				for (int i = 0; i <= n; i++)
				{
					int maxj = System.Math.Min(1, n - i);
					var row = new int[maxj + 1];
					for (int j = 0; j <= maxj; j++)
					{
						float w0 = 1f - i * invN - j * invN;
						float w1 = i * invN;
						float w2 = j * invN;
						Vector3 pt = (v0 * w0 + v1 * w1 + v2 * w2).Normalized() * Radius;
						row[j] = GetOrCreate(pt, verts, cache);
					}
					grid.Add(row);
				}
				for (int i = 0; i < n; i++)
				{
					tris.Add((grid[i][0], grid[i + 1][0], grid[i][1]));
					if (i < n - 1)
						tris.Add((grid[i + 1][0], grid[i + 1][1], grid[i][1]));
				}
			}
			else
			{
				// Shared corner (v0): the single triangle at that corner.
				for (int i = 0; i < 2; i++)
				{
					int maxj = (i == 0) ? 1 : 0;
					var row = new int[maxj + 1];
					for (int j = 0; j <= maxj; j++)
					{
						float w0 = 1f - i * invN - j * invN;
						float w1 = i * invN;
						float w2 = j * invN;
						Vector3 pt = (v0 * w0 + v1 * w1 + v2 * w2).Normalized() * Radius;
						row[j] = GetOrCreate(pt, verts, cache);
					}
					grid.Add(row);
				}
				tris.Add((grid[0][0], grid[1][0], grid[0][1]));
			}
		}

		private static int GetOrCreate(Vector3 v, List<Vector3> verts, Dictionary<(long, long, long), int> cache)
		{
			(long, long, long) key = VertexKey(v);
			if (cache.TryGetValue(key, out int idx))
				return idx;
			idx = verts.Count;
			verts.Add(v);
			cache[key] = idx;
			return idx;
		}

		private static (long, long, long) VertexKey(Vector3 v)
		{
			// 1km cells — see Icosahedron.VertexKey
			return ((long)System.Math.Round((double)v.X),
					(long)System.Math.Round((double)v.Y),
					(long)System.Math.Round((double)v.Z));
		}
	}
}
