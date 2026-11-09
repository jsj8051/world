using System;

namespace World.Data;

/// <summary>
/// 一次演化历史的冻结定义：参数深副本、模型版本、网格身份与物理半径。
/// 冻结后不跟随 UI 参数变化；重演与回溯都以它为准。
/// </summary>
public sealed class EvolutionDefinition
{
	/// <summary>当前支持的模型版本。</summary>
	public const int CurrentModelVersion = 4;

	readonly WorldSpec _frozen;

	/// <summary>模型版本，决定积分、几何与收支语义。</summary>
	public int ModelVersion { get; }
	/// <summary>单次初始化历史的身份；不参与确定性结果比较。</summary>
	public Guid HistoryId { get; }
	/// <summary>H3 分辨率。</summary>
	public int H3Resolution { get; }
	/// <summary>物理球半径（km）。</summary>
	public double RadiusKm { get; }
	/// <summary>网格指纹（有序格号、顶点与半径的摘要）。</summary>
	public string GridFingerprint { get; }
	/// <summary>冻结的板块数（来自初始化参数）。</summary>
	public int PlateCount => _frozen.Plate.PlateCount;
	internal long StepYears => _frozen.Evolution.StepYears;

	EvolutionDefinition(int modelVersion, Guid historyId, WorldSpec frozen, int h3Resolution,
		double radiusKm, string gridFingerprint)
	{
		ModelVersion = modelVersion;
		HistoryId = historyId;
		_frozen = frozen;
		H3Resolution = h3Resolution;
		RadiusKm = radiusKm;
		GridFingerprint = gridFingerprint;
	}

	/// <summary>冻结当前世界定义；参数非法即抛。</summary>
	/// <exception cref="ArgumentException">分辨率、半径或指纹非法。</exception>
	/// <exception cref="ArgumentOutOfRangeException">半径非正或分辨率为负。</exception>
	/// <example><code>var d = EvolutionDefinition.Create(spec, 4, 6371, fingerprint);</code></example>
	public static EvolutionDefinition Create(WorldSpec spec, int h3Resolution, double radiusKm,
		string gridFingerprint)
	{
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (h3Resolution < 0) throw new ArgumentOutOfRangeException(nameof(h3Resolution), "分辨率不得为负");
		if (!(radiusKm > 0) || !double.IsFinite(radiusKm))
			throw new ArgumentOutOfRangeException(nameof(radiusKm), "半径必须是有限正数");
		if (string.IsNullOrWhiteSpace(gridFingerprint))
			throw new ArgumentException("网格指纹不能为空", nameof(gridFingerprint));
		spec.Evolution?.Validate();

		var frozen = new WorldSpec();
		spec.CopyTo(frozen);
		return new EvolutionDefinition(CurrentModelVersion, Guid.NewGuid(), frozen, h3Resolution,
			radiusKm, gridFingerprint);
	}

	/// <summary>取冻结参数的独立副本，供重演或只读展示；修改返回值不影响本定义。</summary>
	/// <example><code>WorldSpec copy = definition.CreateSpecCopy();</code></example>
	public WorldSpec CreateSpecCopy()
	{
		var copy = new WorldSpec();
		_frozen.CopyTo(copy);
		return copy;
	}

	/// <summary>校验版本与身份；不支持版本即抛。</summary>
	/// <exception cref="ArgumentException">模型版本不受支持或身份为空。</exception>
	/// <example><code>definition.Validate();</code></example>
	public void Validate()
	{
		if (ModelVersion != CurrentModelVersion)
			throw new ArgumentException($"不支持的演化模型版本 {ModelVersion}（当前 {CurrentModelVersion}）",
				nameof(ModelVersion));
		if (HistoryId == Guid.Empty) throw new ArgumentException("历史身份不能为空", nameof(HistoryId));
		if (H3Resolution < 0) throw new ArgumentOutOfRangeException(nameof(H3Resolution));
		if (!(RadiusKm > 0) || !double.IsFinite(RadiusKm))
			throw new ArgumentOutOfRangeException(nameof(RadiusKm));
		if (string.IsNullOrWhiteSpace(GridFingerprint))
			throw new ArgumentException("网格指纹不能为空", nameof(GridFingerprint));
	}
}
