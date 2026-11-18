using System;
using System.Collections.Generic;
using System.Linq;
using World.Logic;
using World.Render;

namespace World.Application.Export;

/// <summary>Renders the world's terrain and delegates image persistence to a writer.</summary>
public sealed class TerrainMapExport
{
	readonly IWorldFactsSource _source;
	readonly IReadOnlyList<MapMode> _modes;
	readonly IMapImageWriter _writer;

	public int Width { get; }
	public int Height { get; }

	public TerrainMapExport(IWorldFactsSource source, IReadOnlyList<MapMode> modes,
		IMapImageWriter writer, int width = MapExporter.DefaultWidth, int height = MapExporter.DefaultHeight)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_modes = modes ?? throw new ArgumentNullException(nameof(modes));
		_writer = writer ?? throw new ArgumentNullException(nameof(writer));
		if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
		if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
		Width = width;
		Height = height;
	}

	public string Export()
	{
		if (_source.Facts == null) throw new InvalidOperationException("世界尚未就绪");
		var terrain = _modes.FirstOrDefault(mode => mode is TerrainMode)
			?? throw new InvalidOperationException("模式表缺少地形模式，无法导出");
		byte[] pixels = MapExporter.RenderPixels(_source, terrain, Width, Height);
		return _writer.Write(pixels, Width, Height);
	}
}
