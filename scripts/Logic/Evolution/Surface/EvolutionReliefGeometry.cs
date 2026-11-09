using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using World.Data;

namespace World.Logic;

/// <summary>在同一网格和演化定义下复用共享边到格心的几何采样。</summary>
internal sealed class EvolutionReliefGeometry
{
	internal readonly record struct Sample(int Cell, double DistanceKm);
	sealed record CacheEntry(int Face, Sample[] Samples);

	const int CacheBits = 12;
	const int MaxSamplesPerEntry = 512;
	readonly EvolutionGrid _grid;
	readonly double _reachKm;
	readonly CacheEntry[] _cache;

	public EvolutionReliefGeometry(EvolutionGrid grid, EvolutionSpec spec)
	{
		_grid = grid;
		_reachKm = Math.Max(spec.OceanRidgeReachKm, Math.Max(2 * spec.ArcReachKm, Math.Max(1.25 * spec.TrenchReachKm, 260)));
		_cache = grid.CellCount >= 20_000 ? new CacheEntry[1 << CacheBits] : Array.Empty<CacheEntry>();
	}

	/// <summary>未命中缓存时写入调用方的工作区，结果使用到下一次工作区复用之前。</summary>
	public ReadOnlySpan<Sample> Samples(int faceIndex, int[] visited, Queue<int> queue, List<Sample> scratch)
	{
		int key = (int)(unchecked((uint)faceIndex * 2654435761u) >> (32 - CacheBits));
		if (_cache.Length != 0)
		{
			CacheEntry cached = Volatile.Read(ref _cache[key]);
			if (cached?.Face == faceIndex) return cached.Samples;
		}

		scratch.Clear();
		ref readonly EvolutionFace face = ref _grid.Faces[faceIndex];
		int stamp = faceIndex + 1;
		visited[face.A] = stamp;
		queue.Enqueue(face.A);
		while (queue.TryDequeue(out int cell))
		{
			double distance = Math.Abs(SignedDistanceKm(face, _grid.Centers[cell], _grid.RadiusKm));
			if (distance > _reachKm + 2 * _grid.CellRadiusRad[cell] * _grid.RadiusKm) continue;
			scratch.Add(new Sample(cell, distance));
			foreach (int neighbor in _grid.Neighbors[cell])
				if (visited[neighbor] != stamp)
				{
					visited[neighbor] = stamp;
					queue.Enqueue(neighbor);
				}
		}

		if (_cache.Length == 0 || scratch.Count > MaxSamplesPerEntry) return CollectionsMarshal.AsSpan(scratch);
		// 每个槽只保留一个弧段。读者持有自己的数组引用，替换缓存不影响在读的帧。
		var samples = scratch.ToArray();
		Volatile.Write(ref _cache[key], new CacheEntry(faceIndex, samples));
		return samples;
	}

	internal static double SignedDistanceKm(in EvolutionFace face, SpherePointD point, double radiusKm)
	{
		double side = Dot(face.Outward, point);
		double start = Dot(face.Start, point), end = Dot(face.End, point);
		double angle = end >= face.CosAngle * start && start >= face.CosAngle * end
			? Math.Asin(Math.Clamp(Math.Abs(side), 0, 1))
			: Math.Acos(Math.Clamp(Math.Max(start, end), -1, 1));
		return Math.CopySign(angle * radiusKm, side);
	}

	static double Dot(SpherePointD a, SpherePointD b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
