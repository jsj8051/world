using System;
using System.Collections.Generic;
using System.Threading;
using World.Utils;

namespace World.Data;

/// <summary>板块参考坐标中的不可变高程。候选轮只复制实际写入的高程块。</summary>
internal sealed class PlateElevationState
{
	const int TileSize = 128;
	readonly Plate[] _plates;

	sealed record Plate(RotationD ReferencePose, Dictionary<int, float[]> Tiles);

	PlateElevationState(Plate[] plates) => _plates = plates;

	internal int PlateCount => _plates.Length;

	internal RotationD ReferencePose(int plate) => _plates[plate].ReferencePose;

	internal static PlateElevationState FromSurface(SculptingSurfaceState surface, CancellationToken cancellation)
	{
		var editor = new Editor(null, surface.SampledPlateOrientations);
		for (int cell = 0; cell < surface.CellCount; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			editor.Set(surface.PlateOfCell[cell], cell, surface.CrustTypes[cell], surface.HeightM[cell]);
		}
		return editor.Finish();
	}

	internal bool TryGet(int plate, int cell, CrustType crust, out float height)
	{
		var (tile, offset) = Address(cell, crust);
		height = _plates[plate].Tiles.TryGetValue(tile, out float[] heights) ? heights[offset] : float.NaN;
		return !float.IsNaN(height);
	}

	static (int Tile, int Offset) Address(int cell, CrustType crust)
		=> (2 * (cell / TileSize) + (int)crust, cell % TileSize);

	internal sealed class Editor
	{
		readonly PlateElevationState _source;
		Plate[] _plates;
		readonly bool[] _writable;
		readonly HashSet<(int Plate, int Tile)> _ownedTiles = new();

		internal Editor(PlateElevationState source, IReadOnlyList<RotationD> referencePoses)
		{
			_source = source;
			_plates = new Plate[referencePoses.Count];
			_writable = new bool[_plates.Length];
			if (source != null) Array.Copy(source._plates, _plates, source.PlateCount);
			for (int plate = source?.PlateCount ?? 0; plate < _plates.Length; plate++)
			{
				_plates[plate] = new Plate(referencePoses[plate], new Dictionary<int, float[]>());
				_writable[plate] = true;
			}
		}

		internal void Set(int plate, int cell, CrustType crust, float height)
		{
			var (tile, offset) = Address(cell, crust);
			MakeWritable(plate);
			var tiles = _plates[plate].Tiles;
			if (_ownedTiles.Add((plate, tile)))
			{
				if (tiles.TryGetValue(tile, out float[] previous)) tiles[tile] = (float[])previous.Clone();
				else
				{
					var empty = new float[TileSize];
					Array.Fill(empty, float.NaN);
					tiles.Add(tile, empty);
				}
			}
			tiles[tile][offset] = height;
		}

		internal void Remove(int plate, int cell, CrustType crust) => Set(plate, cell, crust, float.NaN);

		internal void ClearPlate(int plate)
		{
			if (_plates[plate].Tiles.Count == 0) return;
			MakeWritable(plate);
			_plates[plate].Tiles.Clear();
		}

		void MakeWritable(int plate)
		{
			if (_writable[plate]) return;
			Plate previous = _plates[plate];
			_plates[plate] = new Plate(previous.ReferencePose, new Dictionary<int, float[]>(previous.Tiles));
			_writable[plate] = true;
		}

		internal PlateElevationState Finish()
		{
			if (_plates == null) throw new InvalidOperationException("高程编辑已封存。");
			PlateElevationState state = Array.IndexOf(_writable, true) < 0 ? _source : new PlateElevationState(_plates);
			_plates = null;
			return state;
		}
	}
}
