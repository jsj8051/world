using System;
using System.IO;
using System.Linq;
using Godot;
using World.Application.Export;
using World.Data;
using World.Logic;
using World.Infrastructure.Export;
using World.Render;

namespace World.Tests;

internal static class TerrainMapExportTests
{
	static readonly Lazy<IWorldFactsSource> ReadySource = new(CreateReadySource);

	public static void RejectsNullDependencies()
	{
		var source = new FakeSource();
		var writer = new FakeWriter();
		var modes = Array.Empty<MapMode>();
		ExpectArgument<ArgumentNullException>(() => new TerrainMapExport(null, modes, writer), "source");
		ExpectArgument<ArgumentNullException>(() => new TerrainMapExport(source, null, writer), "modes");
		ExpectArgument<ArgumentNullException>(() => new TerrainMapExport(source, modes, null), "writer");
		Program.True(writer.Calls == 0, "Invalid dependencies must not write an image.");
	}

	public static void RejectsInvalidDimensions()
	{
		var source = new FakeSource();
		var writer = new FakeWriter();
		var modes = new MapMode[] { new TerrainMode(source) };
		foreach (int invalid in new[] { int.MinValue, -1, 0 })
		{
			ExpectArgument<ArgumentOutOfRangeException>(
				() => new TerrainMapExport(source, modes, writer, invalid, 1), "width");
			ExpectArgument<ArgumentOutOfRangeException>(
				() => new TerrainMapExport(source, modes, writer, 1, invalid), "height");
		}
		var smallest = new TerrainMapExport(source, modes, writer, 1, 1);
		Program.True(smallest.Width == 1 && smallest.Height == 1 && writer.Calls == 0,
			"Positive dimensions must be accepted without rendering or writing during construction.");
	}

	public static void DefaultsMatchRenderer()
	{
		var writer = new FakeWriter();
		var export = new TerrainMapExport(new FakeSource(), Array.Empty<MapMode>(), writer);
		Program.True(export.Width == MapExporter.DefaultWidth && export.Height == MapExporter.DefaultHeight
			&& writer.Calls == 0, "Default dimensions must come from MapExporter, without eager writes.");
	}

	public static void MissingTerrainDoesNotWrite()
	{
		foreach (MapMode[] modes in new[] { Array.Empty<MapMode>(), new MapMode[] { new NonTerrainMode() } })
		{
			var writer = new FakeWriter();
			var export = new TerrainMapExport(ReadySource.Value, modes, writer, 16, 8);
			ExpectInvalidOperation(export.Export, "模式表缺少地形模式，无法导出");
			Program.True(writer.Calls == 0, "Missing terrain must fail before writing or using another map mode.");
		}
	}

	public static void UnreadyWorldDoesNotWrite()
	{
		var source = new FakeSource();
		foreach (MapMode[] modes in new[] { Array.Empty<MapMode>(), new MapMode[] { new TerrainMode(source) } })
		{
			var writer = new FakeWriter();
			var export = new TerrainMapExport(source, modes, writer, 16, 8);
			ExpectInvalidOperation(export.Export, "世界尚未就绪");
			Program.True(writer.Calls == 0, "Unready worlds must fail before rendering or writing.");
		}
	}

	public static void ExportsTerrainPixelsThroughWriter()
	{
		IWorldFactsSource source = ReadySource.Value;
		var terrain = new TerrainMode(source);
		var writer = new FakeWriter();
		const int width = 32, height = 16;
		var export = new TerrainMapExport(source, new MapMode[] { new NonTerrainMode(), terrain }, writer, width, height);
		string path = export.Export();
		byte[] expected = MapExporter.RenderPixels(source, terrain, width, height);
		Program.True(path == writer.Path && writer.Calls == 1 && writer.Width == width && writer.Height == height,
			"Export must select terrain, write exactly once with configured dimensions, and return the writer path.");
		Program.True(writer.Pixels.Length == width * height * 4 && writer.Pixels.SequenceEqual(expected),
			"The writer must receive the renderer's RGBA8 terrain pixels unchanged.");
		for (int offset = 3; offset < writer.Pixels.Length; offset += 4)
			Program.True(writer.Pixels[offset] == 255, "Terrain pixels must have opaque alpha.");
	}

	public static void WriterFailurePropagates()
	{
		IWorldFactsSource source = ReadySource.Value;
		var failure = new InvalidOperationException("writer failed");
		var writer = new FakeWriter { Failure = failure };
		var export = new TerrainMapExport(source, new MapMode[] { new TerrainMode(source) }, writer, 16, 8);
		try { export.Export(); }
		catch (InvalidOperationException error) when (ReferenceEquals(error, failure))
		{
			Program.True(writer.Calls == 1, "A failed writer must not be retried implicitly.");
			return;
		}
		throw new Exception("The original writer failure must propagate to the caller.");
	}

	public static void PngWriterReturnsGameRelativePath()
	{
		WithExportRoot(root =>
		{
			byte[] pixels = { 1, 2, 3, 255 };
			string writtenPath = null;
			var writer = new PngMapImageWriter(root, (actualPixels, width, height, absolutePath) =>
			{
				Program.True(ReferenceEquals(actualPixels, pixels) && width == 1 && height == 1,
					"The PNG encoder must receive the caller pixels and dimensions unchanged.");
				writtenPath = absolutePath;
				return Error.Ok;
			});

			string relativePath = writer.Write(pixels, 1, 1);
			Program.True(!Path.IsPathRooted(relativePath) && relativePath.StartsWith("map_exports/terrain_", StringComparison.Ordinal)
				&& relativePath.EndsWith(".png", StringComparison.Ordinal), "PNG exports must return game-root-relative paths.");
			Program.True(writtenPath == Path.GetFullPath(Path.Combine(root, relativePath))
				&& Directory.Exists(Path.Combine(root, "map_exports")), "Only filesystem and PNG encoding access may receive the absolute path.");
		});
	}

	public static void PngWriterFailureUsesRelativePath()
	{
		WithExportRoot(root =>
		{
			var writer = new PngMapImageWriter(root, (_, _, _, _) => Error.CantCreate);
			ExpectRelativePngFailure(() => writer.Write(new byte[4], 1, 1), root, nameof(Error.CantCreate));
			writer = new PngMapImageWriter(root, (_, _, _, _) => throw new IOException("Private path: " + root));
			ExpectRelativePngFailure(() => writer.Write(new byte[4], 1, 1), root, nameof(IOException));
		});
	}

	public static void PngWriterDirectoryFailureUsesRelativePath()
	{
		WithExportRoot(root =>
		{
			File.WriteAllText(Path.Combine(root, "map_exports"), "blocks export directory");
			bool encodingCalled = false;
			var writer = new PngMapImageWriter(root, (_, _, _, _) =>
			{
				encodingCalled = true;
				return Error.Ok;
			});
			ExpectRelativePngFailure(() => writer.Write(new byte[4], 1, 1), root, nameof(IOException));
			Program.True(!encodingCalled, "An unavailable export directory must fail before PNG encoding.");
		});
	}

	static void ExpectRelativePngFailure(Func<string> write, string root, string reason)
	{
		try { write(); }
		catch (InvalidOperationException error)
		{
			Program.True(error.Message.Contains("map_exports/terrain_", StringComparison.Ordinal)
				&& error.Message.Contains(reason, StringComparison.Ordinal)
				&& !error.Message.Contains(root, StringComparison.Ordinal) && error.InnerException == null,
				"Application-facing PNG failures must report the relative path without leaking an absolute path.");
			return;
		}
		throw new Exception("PNG write failure did not propagate.");
	}

	static void WithExportRoot(Action<string> check)
	{
		string root = Path.Combine(System.Environment.CurrentDirectory, ".tmp-export-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try { check(root); }
		finally { Directory.Delete(root, recursive: true); }
	}

	static IWorldFactsSource CreateReadySource()
	{
		var simulation = new WorldGenSimulation(1, PlanetGeometry.ProductionRadius);
		LegacyWorldGeneration.RunStatic(simulation, new WorldSpec());
		return simulation;
	}

	static void ExpectArgument<T>(Action action, string parameter) where T : ArgumentException
	{
		try { action(); }
		catch (T error) when (error.ParamName == parameter) { return; }
		throw new Exception($"Expected {typeof(T).Name} for {parameter}.");
	}

	static void ExpectInvalidOperation(Func<string> action, string message)
	{
		try { action(); }
		catch (InvalidOperationException error) when (error.Message == message) { return; }
		throw new Exception($"Expected InvalidOperationException: {message}");
	}

	sealed class FakeSource : IWorldFactsSource
	{
		public WorldFacts Facts => null;
	}

	sealed class NonTerrainMode : MapMode
	{
		public override string Name => "Not terrain";
		public override Color CellColorAt(int i) => throw new Exception("Non-terrain modes must not be rendered.");
	}

	sealed class FakeWriter : IMapImageWriter
	{
		public int Calls;
		public byte[] Pixels;
		public int Width;
		public int Height;
		public readonly string Path = "fake/terrain.png";
		public Exception Failure;

		public string Write(byte[] pixels, int width, int height)
		{
			Calls++;
			Pixels = pixels;
			Width = width;
			Height = height;
			if (Failure != null) throw Failure;
			return Path;
		}
	}
}
