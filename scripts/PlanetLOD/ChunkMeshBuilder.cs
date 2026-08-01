using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.HexPlanet;
using World.Surface;

namespace World.PlanetLOD
{

	/// <summary>
	/// Builds flat per-tile colored ArrayMeshes from tile subsets.
	/// Rules: vertices split per tile, one flat color per tile, triangle fan with
	/// outward winding, geometry normals from displaced positions (flat facet look).
	///
	/// 几何/颜色拆分：BuildGeometry 产出顶点/法线/索引（与颜色无关，可缓存）；
	/// BuildColors 按图层/色板重算颜色（几何不变时只需重算颜色 → 图层切换秒级）。
	/// 顶点按 tile 分割不共享 → 所有并行路径无锁无竞态。
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

				Vector3 centerPos = tile.Center.Normalized() * (radiusKm + tileElev[tile.Id] * elevationScaleKm);
				int centerIdx = meshVerts.Count;
				meshVerts.Add(centerPos);
				var centerColor = tileColor;
				centerColor.A = 1f; // tile interior
				meshColors.Add(centerColor);

				int firstCorner = meshVerts.Count;
				for (int i = 0; i < n; i++)
				{
					float fe = faceElev[tile.CornerFaceIndices[i]];
					Vector3 p = tile.Corners[i].Normalized() * (radiusKm + fe * elevationScaleKm);
					meshVerts.Add(p);
					var cornerColor = tileColor;
					cornerColor.A = 0f; // on the tile boundary → outline
					meshColors.Add(cornerColor);
				}

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

		/// <summary>
		/// Builds a flat per-tile colored ArrayMesh where every vertex elevation is
		/// sampled independently (elevAt) — same value at the same position across
		/// chunks and LOD levels, so adjacent chunks are watertight even at
		/// different detail levels.
		/// </summary>
		public static ArrayMesh BuildMeshSampled(
			List<HexTile> tiles,
			Func<Vector3, float> elevAt,
			Func<HexTile, Color> colorFn,
			float radiusKm,
			float elevationScaleKm,
			out int triCount)
		{
			var d = BuildMeshData(tiles, elevAt, colorFn, radiusKm, elevationScaleKm, out triCount);
			return CreateMesh(d);
		}

		/// <summary>几何 + 颜色一次构建（组合接口，向后兼容）。</summary>
		public static MeshData BuildMeshData(
			List<HexTile> tiles,
			Func<Vector3, float> elevAt,
			Func<HexTile, Color> colorFn,
			float radiusKm,
			float elevationScaleKm,
			out int triCount,
			Action<float> progress = null)
		{
			var g = BuildGeometry(tiles, elevAt, radiusKm, elevationScaleKm,
				progress == null ? null : p => progress(p * 0.7f));
			var colors = BuildColors(tiles, colorFn, g,
				progress == null ? null : p => progress(0.7f + p * 0.3f));
			triCount = g.Indices.Length / 3;
			return new MeshData
			{
				Verts = g.Verts,
				Normals = g.Normals,
				Colors = colors,
				Indices = g.Indices
			};
		}

		/// <summary>
		/// 几何构建（纯数据，后台线程安全）：顶点/索引/fan 法线。
		/// 结果可缓存——图层切换只需重算颜色。
		/// </summary>
		public static GeometryData BuildGeometry(
			List<HexTile> tiles,
			Func<Vector3, float> elevAt,
			float radiusKm,
			float elevationScaleKm,
			Action<float> progress = null)
		{
			int total = tiles.Count;

			// ── 第一遍：每 tile 顶点数 → 顶点偏移；三角形数 → 索引偏移 ──
			var vertOffsets = new int[total];
			var triOffsets = new int[total];
			int totalVerts = 0;
			int totalTris = 0;
			for (int i = 0; i < total; i++)
			{
				int n = tiles[i].Corners.Length;
				vertOffsets[i] = totalVerts;
				triOffsets[i] = totalTris;
				if (n >= 3)
				{
					totalVerts += n + 1; // 中心 + n 个角
					totalTris += n;      // fan：n 个三角形
				}
			}

			var meshVerts = new Vector3[totalVerts];
			var meshIndices = new int[totalTris * 3];

			// ── 第二遍：并行填充每 tile 的顶点/索引 ──
			int done = 0;
			Parallel.For(0, total, i =>
			{
				var tile = tiles[i];
				int n = tile.Corners.Length;
				if (n >= 3)
				{
					int off = vertOffsets[i];
					int centerIdx = off;

					Vector3 centerPos = tile.Center.Normalized() * (radiusKm + elevAt(tile.Center) * elevationScaleKm);
					meshVerts[centerIdx] = centerPos;

					for (int k = 0; k < n; k++)
					{
						Vector3 p = tile.Corners[k].Normalized() * (radiusKm + elevAt(tile.Corners[k]) * elevationScaleKm);
						meshVerts[off + 1 + k] = p;
					}

					int idxBase = triOffsets[i] * 3;
					for (int k = 0; k < n; k++)
					{
						int a = off + 1 + k;
						int b = off + 1 + (k + 1) % n;
						Vector3 va = meshVerts[a];
						Vector3 vb = meshVerts[b];
						Vector3 vc = meshVerts[centerIdx];

						Vector3 normal = (va - vc).Cross(vb - vc);
						if (normal.Dot(vc) < 0f)
						{
							meshIndices[idxBase + k * 3] = centerIdx;
							meshIndices[idxBase + k * 3 + 1] = b;
							meshIndices[idxBase + k * 3 + 2] = a;
						}
						else
						{
							meshIndices[idxBase + k * 3] = centerIdx;
							meshIndices[idxBase + k * 3 + 1] = a;
							meshIndices[idxBase + k * 3 + 2] = b;
						}
					}
				}

				if (progress != null && Interlocked.Increment(ref done) % 64 == 0)
					progress(done / (float)total);
			});
			progress?.Invoke(1f);

			// ── 第三遍：法线累积（每 tile 只写自己的顶点段，无竞态）+ 归一化 ──
			var accNormals = new Vector3[totalVerts];
			Parallel.For(0, total, i =>
			{
				int n = tiles[i].Corners.Length;
				if (n < 3)
					return;
				int idxBase = triOffsets[i] * 3;
				for (int k = 0; k < n; k++)
				{
					int ia = meshIndices[idxBase + k * 3];
					int ib = meshIndices[idxBase + k * 3 + 1];
					int ic = meshIndices[idxBase + k * 3 + 2];
					Vector3 nrm = (meshVerts[ib] - meshVerts[ia]).Cross(meshVerts[ic] - meshVerts[ia]);
					accNormals[ia] += nrm;
					accNormals[ib] += nrm;
					accNormals[ic] += nrm;
				}
			});
			var meshNormals = new Vector3[totalVerts];
			Parallel.For(0, totalVerts, i => meshNormals[i] = accNormals[i].Normalized());

			return new GeometryData
			{
				Verts = meshVerts,
				Normals = meshNormals,
				Indices = meshIndices,
				VertOffsets = vertOffsets,
				TotalVerts = totalVerts
			};
		}

		/// <summary>
		/// 颜色重算（纯数据，后台线程安全）：每 tile 一个颜色展开到其全部顶点。
		/// 几何不变时只调这个 → 图层切换秒级。
		/// </summary>
		public static Color[] BuildColors(
			List<HexTile> tiles,
			Func<HexTile, Color> colorFn,
			GeometryData g,
			Action<float> progress = null)
		{
			var colors = new Color[g.TotalVerts];
			int done = 0;
			Parallel.For(0, tiles.Count, i =>
			{
				var tile = tiles[i];
				int n = tile.Corners.Length;
				if (n >= 3)
				{
					Color c = colorFn(tile);
					int off = g.VertOffsets[i];
					colors[off] = c;
					for (int k = 0; k < n; k++)
						colors[off + 1 + k] = c;
				}

				if (progress != null && Interlocked.Increment(ref done) % 64 == 0)
					progress(done / (float)tiles.Count);
			});
			progress?.Invoke(1f);
			return colors;
		}

		/// <summary>Wraps geometry + colors into an ArrayMesh. Main thread only.</summary>
		public static ArrayMesh CreateMesh(GeometryData g, Color[] colors)
		{
			var mesh = new ArrayMesh();
			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = g.Verts;
			arrays[(int)Mesh.ArrayType.Normal] = g.Normals;
			arrays[(int)Mesh.ArrayType.Color] = colors;
			arrays[(int)Mesh.ArrayType.Index] = g.Indices;
			mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
			return mesh;
		}

		/// <summary>Wraps pre-built data into an ArrayMesh. Main thread only.</summary>
		public static ArrayMesh CreateMesh(MeshData d)
		{
			var mesh = new ArrayMesh();
			var arrays = new Godot.Collections.Array();
			arrays.Resize((int)Mesh.ArrayType.Max);
			arrays[(int)Mesh.ArrayType.Vertex] = d.Verts;
			arrays[(int)Mesh.ArrayType.Normal] = d.Normals;
			arrays[(int)Mesh.ArrayType.Color] = d.Colors;
			arrays[(int)Mesh.ArrayType.Index] = d.Indices;
			mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
			return mesh;
		}
	}

	public struct MeshData
	{
		public Vector3[] Verts;
		public Vector3[] Normals;
		public Color[] Colors;
		public int[] Indices;
	}

	/// <summary>几何（与颜色解耦，可缓存复用）。</summary>
	public struct GeometryData
	{
		public Vector3[] Verts;
		public Vector3[] Normals;
		public int[] Indices;
		public int[] VertOffsets; // 每 tile 顶点段起始索引（供 BuildColors 展开）
		public int TotalVerts;
	}
}
