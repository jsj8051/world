using System;
using System.IO;
using Godot;
using World.Application.Export;
using World.Utils;

namespace World.Infrastructure.Export;

/// <summary>Writes RGBA8 map pixels to a PNG beneath the game root and returns its relative path.</summary>
/// <example><code>string relativePath = new PngMapImageWriter().Write(pixels, width, height);</code></example>
public sealed class PngMapImageWriter : IMapImageWriter
{
	readonly string _gameRootDirectory;
	readonly Func<byte[], int, int, string, Error> _savePng;

	public PngMapImageWriter() : this(GamePaths.GameDirectory, SavePng) { }

	internal PngMapImageWriter(string gameRootDirectory, Func<byte[], int, int, string, Error> savePng)
	{
		_gameRootDirectory = RootRelativePath.NormalizeRoot(gameRootDirectory);
		_savePng = savePng ?? throw new ArgumentNullException(nameof(savePng));
	}

	/// <summary>Returns a path such as <c>map_exports/terrain_20261118_235432_123.png</c>.</summary>
	/// <exception cref="InvalidOperationException">The export directory or PNG could not be written.</exception>
	public string Write(byte[] pixels, int width, int height)
	{
		string relativePath = $"map_exports/terrain_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
		try
		{
			string absolutePath = RootRelativePath.Resolve(_gameRootDirectory, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
			Error error = _savePng(pixels, width, height, absolutePath);
			if (error != Error.Ok)
				throw new InvalidOperationException($"PNG 写入失败：{error}（{relativePath}）");
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			throw new InvalidOperationException($"PNG 写入失败：{error.GetType().Name}（{relativePath}）");
		}
		return relativePath;
	}

	static Error SavePng(byte[] pixels, int width, int height, string absolutePath)
	{
		using var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);
		return image.SavePng(absolutePath);
	}
}
