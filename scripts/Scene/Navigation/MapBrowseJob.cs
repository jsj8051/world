using System;
using System.Threading;
using World.Data.Maps;
using World.Data.Work;
using World.Logic;

namespace World.Scene;

/// <summary>浏览加载的纯数据 Job：读包/校验/解码 + 世界事实装配，只发领域信号。</summary>
internal sealed class MapBrowseJob : IWorkSignalSource
{
	public event Action<WorkSignal> Signal;

	internal ImportedWorldFactsSource Run(string path, CancellationToken token)
	{
		Signal?.Invoke(WorkSignal.Started("decode", "并行解码"));
		GeographySnapshot snapshot = MapPackageReader.Read(path,
			(done, total) => Signal?.Invoke(WorkSignal.Unit("decode", $"并行解码 {done}/{total}", done, total)),
			token);

		token.ThrowIfCancellationRequested();
		Signal?.Invoke(WorkSignal.Started("facts", "装配世界事实"));
		ImportedWorldFactsSource source = ImportedWorldFactsFactory.Build(snapshot);
		Signal?.Invoke(WorkSignal.Unit("facts", "世界事实就绪", 1, 1));
		Signal?.Invoke(WorkSignal.Finished());
		return source;
	}
}
