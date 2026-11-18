using System;
using System.IO;

namespace World.Utils;

internal static class RootRelativePath
{
	internal static string NormalizeRoot(string rootDirectory)
	{
		if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathFullyQualified(rootDirectory))
			throw new ArgumentException("An absolute root directory is required.", nameof(rootDirectory));
		return Path.GetFullPath(rootDirectory);
	}

	internal static string Normalize(string relativePath)
	{
		if (string.IsNullOrWhiteSpace(relativePath))
			throw new ArgumentException("A relative file path is required.", nameof(relativePath));
		if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\')
			|| relativePath.IndexOfAny(new[] { ':', '\0' }) >= 0)
			throw new ArgumentException("The path must be relative to the root directory.", nameof(relativePath));

		var segments = new System.Collections.Generic.List<string>();
		foreach (string segment in relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
		{
			if (segment == ".") continue;
			if (segment == "..")
			{
				if (segments.Count == 0)
					throw new ArgumentException("The path must stay inside the root directory.", nameof(relativePath));
				segments.RemoveAt(segments.Count - 1);
			}
			else segments.Add(segment);
		}
		if (segments.Count == 0)
			throw new ArgumentException("A relative file path is required.", nameof(relativePath));
		return string.Join('/', segments);
	}

	internal static string Resolve(string rootDirectory, string relativePath)
	{
		string normalized = Normalize(relativePath);
		string root = NormalizeRoot(rootDirectory);
		string fullPath = Path.GetFullPath(Path.Combine(root, normalized));
		string relativeToRoot = Path.GetRelativePath(root, fullPath);
		if (relativeToRoot == "." || relativeToRoot == ".."
			|| relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| relativeToRoot.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
			|| Path.IsPathRooted(relativeToRoot))
			throw new ArgumentException("The path must stay inside the root directory.", nameof(relativePath));

		// A lexical check alone cannot prevent a link inside the root from redirecting file access.
		string current = root;
		foreach (string segment in relativeToRoot.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
		{
			current = Path.Combine(current, segment);
			try
			{
				if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
					throw new ArgumentException("Symbolic links and junctions are not supported in storage paths.", nameof(relativePath));
			}
			catch (FileNotFoundException) { break; }
			catch (DirectoryNotFoundException) { break; }
		}
		return fullPath;
	}
}
