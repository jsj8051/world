using System;
using System.Collections.Generic;
using System.Linq;

namespace World.Application.Loading;

/// <summary>加权阶段：标识、文案、权重。</summary>
internal readonly struct LoadStage
{
	public LoadStage(string id, string label, double weight)
	{
		if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || !IsStageId(id))
			throw new ArgumentException("阶段标识必须为 1–32 个小写标识符字符。", nameof(id));
		if (string.IsNullOrWhiteSpace(label) || label.Length > 40)
			throw new ArgumentException("阶段文案必须为 1–40 个字符。", nameof(label));
		if (!double.IsFinite(weight) || weight <= 0)
			throw new ArgumentOutOfRangeException(nameof(weight), weight, "阶段权重必须是正的有限值。");

		Id = id;
		Label = label;
		Weight = weight;
	}

	public string Id { get; }
	public string Label { get; }
	public double Weight { get; }

	static bool IsStageId(string id)
		=> id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}

/// <summary>浏览/生成的加权阶段计划；权重总和必须为 1。</summary>
internal sealed class LoadStagePlan
{
	public LoadStagePlan(LoadFlow flow, IEnumerable<LoadStage> stages)
	{
		if (flow is not (LoadFlow.MapBrowse or LoadFlow.WorldGeneration))
			throw new ArgumentOutOfRangeException(nameof(flow), flow, "演化不走加权阶段计划。");
		if (stages == null) throw new ArgumentNullException(nameof(stages));

		var list = stages.ToList();
		if (list.Count is < 2 or > 8)
			throw new ArgumentException("阶段数必须在 2–8 之间。", nameof(stages));

		var ids = new HashSet<string>(StringComparer.Ordinal);
		double total = 0;
		foreach (LoadStage stage in list)
		{
			if (!ids.Add(stage.Id))
				throw new ArgumentException($"阶段标识重复：{stage.Id}。", nameof(stages));
			total += stage.Weight;
		}
		if (!double.IsFinite(total) || Math.Abs(total - 1.0) > 1e-6)
			throw new ArgumentException($"阶段权重总和必须为 1，实得 {total:G17}。", nameof(stages));

		Flow = flow;
		Stages = list.AsReadOnly();
		TotalWeight = total;
	}

	public LoadFlow Flow { get; }
	public IReadOnlyList<LoadStage> Stages { get; }
	public double TotalWeight { get; }

	/// <summary>阶段内完成比例换算为整份计划的 0–1 进度；未知阶段视为已完成。</summary>
	internal float FractionThrough(string stageId, long done, long total)
	{
		double before = 0;
		foreach (LoadStage stage in Stages)
		{
			if (string.Equals(stage.Id, stageId, StringComparison.Ordinal))
			{
				double part = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 1;
				return (float)Math.Clamp((before + stage.Weight * part) / TotalWeight, 0, 1);
			}
			before += stage.Weight;
		}
		return 1f;
	}
}

/// <summary>冻结的出厂阶段计划；数值在第 5 步实测后可调，结构不变。</summary>
internal static class LoadStagePlans
{
	public static readonly LoadStagePlan Browse = new(LoadFlow.MapBrowse, new[]
	{
		new LoadStage("read", "读取与清单", 0.10),
		new LoadStage("decode", "并行解压/解码", 0.35),
		new LoadStage("facts", "世界事实装配", 0.30),
		new LoadStage("render", "画面装配", 0.25),
	});

	public static readonly LoadStagePlan Generation = new(LoadFlow.WorldGeneration, new[]
	{
		new LoadStage("plate", "板块", 0.10),
		new LoadStage("terrain", "地形", 0.20),
		new LoadStage("climate", "气候", 0.30),
		new LoadStage("hydrology", "水文", 0.15),
		new LoadStage("index", "空间索引", 0.10),
		new LoadStage("render", "画面装配", 0.15),
	});
}
