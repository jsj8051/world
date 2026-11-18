using System;
using System.IO;
using System.Threading;
using World.Application.Storage;

namespace World.Tests;

internal static class FilePersistenceComponentTests
{
	sealed record Document(int Value);

	sealed class Storage<T> : IFileStorage<T>
	{
		internal T Data;
		internal string Path;
		internal Exception Failure;
		internal IFileImportObserver Observer;
		internal CancellationToken Token;
		internal int Calls;

		public void Export(string path, T document)
		{
			Calls++;
			if (Failure != null) throw Failure;
			(Path, Data) = (path, document);
		}

		public T Import(string path, IFileImportObserver observer = null, CancellationToken cancellation = default)
		{
			Calls++;
			cancellation.ThrowIfCancellationRequested();
			if (Failure != null) throw Failure;
			(Path, Observer, Token) = (path, observer, cancellation);
			return Data;
		}
	}

	sealed class Observer : IFileImportObserver
	{
		public void Report(int completed, int total) { }
	}

	static string Root => Path.Combine(Path.GetTempPath(), "world-component-tests");

	internal static void SavesCallerDataAndRelativePath()
	{
		var storage = new Storage<Document>();
		var saver = new FileSaveComponent<Document>(storage, Root);
		var data = new Document(42);
		string relative = Path.Combine("saves", "nested", "..", "sample.data");
		string result = saver.Save(data, relative);
		Program.True(ReferenceEquals(storage.Data, data), "Saver must not transform or replace caller data.");
		Program.True(storage.Path == Path.Combine(Root, "saves", "sample.data") && result == "saves/sample.data",
			"Saver must resolve the caller path under its injected root.");
		var integers = new Storage<int>();
		new FileSaveComponent<int>(integers, Root).Save(7, "counter.data");
		Program.True(integers.Data == 7, "Persistence must support non-map value types too.");
	}

	internal static void LoadsCallerTypeAndForwardsOptions()
	{
		var data = new Document(23);
		var storage = new Storage<Document> { Data = data };
		var observer = new Observer();
		using var cancellation = new CancellationTokenSource();
		var loader = new FileLoadComponent<Document>(storage, Root);
		Document result = loader.Load("saves/sample.data", observer, cancellation.Token);
		Program.True(ReferenceEquals(result, data) && storage.Path == Path.Combine(Root, "saves", "sample.data"),
			"Loader must return the requested data from the caller path.");
		Program.True(ReferenceEquals(storage.Observer, observer) && storage.Token == cancellation.Token,
			"Loader must forward observer and cancellation.");
		cancellation.Cancel();
		Program.Throws(() => loader.Load("sample.data", cancellation: cancellation.Token), "Cancellation was ignored.");
	}

	internal static void RejectsAbsoluteAndEscapingPaths()
	{
		var storage = new Storage<string>();
		var saver = new FileSaveComponent<string>(storage, Root);
		var loader = new FileLoadComponent<string>(storage, Root);
		string[] invalid = { null, "", " ", ".", "..", "../escape.data", "a/../../escape.data",
			Path.Combine(Root, "absolute.data"), "C:drive-relative.data", "file.data:stream", "res://file.data" };
		foreach (string path in invalid)
		{
			Program.Throws(() => saver.Save("value", path), "Save accepted an invalid path: " + path);
			Program.Throws(() => loader.Load(path), "Load accepted an invalid path: " + path);
		}
		Program.True(storage.Calls == 0, "Rejected paths must never reach storage.");
	}

	internal static void RejectsInvalidDependencies()
	{
		var storage = new Storage<int>();
		Program.Throws(() => new FileSaveComponent<int>(null, Root), "Missing save storage accepted.");
		Program.Throws(() => new FileLoadComponent<int>(null, Root), "Missing load storage accepted.");
		foreach (string root in new[] { null, "", "relative-root" })
		{
			Program.Throws(() => new FileSaveComponent<int>(storage, root), "Invalid save root accepted.");
			Program.Throws(() => new FileLoadComponent<int>(storage, root), "Invalid load root accepted.");
		}
	}

	internal static void PreservesStorageFailures()
	{
		var failure = new IOException("storage failure");
		var storage = new Storage<int> { Failure = failure };
		Exception saveError = null, loadError = null;
		try { new FileSaveComponent<int>(storage, Root).Save(1, "file.data"); }
		catch (Exception error) { saveError = error; }
		try { new FileLoadComponent<int>(storage, Root).Load("file.data"); }
		catch (Exception error) { loadError = error; }
		Program.True(ReferenceEquals(saveError, failure) && ReferenceEquals(loadError, failure),
			"Storage errors must propagate unchanged.");
		Program.True(storage.Calls == 2, "Storage should be invoked once per operation.");
	}
}
