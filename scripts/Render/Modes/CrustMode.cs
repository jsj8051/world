using System;
using System.Collections.Generic;
using Godot;
using World.Data;
using World.Logic;
using World.Utils;

namespace World.Render;

/// <summary>
/// 地壳图（成因层）：底色 = 陆壳 / 洋壳，叠加六类活动边界的最近影响。
/// 数据源全部来自 <see cref="WorldFacts"/>：板块表给壳型，边界集 + 逐边运动经
/// <see cref="TectonicInfluenceOps"/> 现算逐格通道与距离。显示半径是**判读尺度**，
/// 不参与生产公式；生产剖面仍由地形阶段另算。
/// </summary>
public sealed class CrustMode : MapMode
{
	/// <summary>显示半径（km；判读用，非生产参数）。超出该半径的格只保留底色。</summary>
	static float ReachOf(TectonicChannel channel) => channel switch
	{
		TectonicChannel.ContinentalCollision => 1000f,
		TectonicChannel.ContinentalArc => 800f,
		TectonicChannel.OceanicArc => 600f,
		TectonicChannel.Subduction => 600f,
		TectonicChannel.ContinentalRift => 800f,
		TectonicChannel.OceanicRidge => 1400f,
		_ => 600f,
	};

	// 通道色：与底色（土黄 / 深蓝）拉开明度与色相。
	static Color ColorOf(TectonicChannel channel) => channel switch
	{
		TectonicChannel.ContinentalCollision => new Color(0.75f, 0.10f, 0.08f),   // 暗红
		TectonicChannel.ContinentalArc => new Color(0.92f, 0.42f, 0.05f),          // 橙
		TectonicChannel.OceanicArc => new Color(0.96f, 0.86f, 0.10f),              // 黄
		TectonicChannel.Subduction => new Color(0.50f, 0.15f, 0.70f),              // 紫
		TectonicChannel.ContinentalRift => new Color(0.10f, 0.70f, 0.25f),         // 绿
		TectonicChannel.OceanicRidge => new Color(0.10f, 0.80f, 0.90f),            // 青
		_ => Colors.Magenta,
	};

	static readonly Color ContinentalColor = new(0.66f, 0.58f, 0.38f);
	static readonly Color OceanicColor = new(0.09f, 0.22f, 0.38f);

	readonly WorldGenSimulation _p;
	WorldFacts _facts;
	Color[] _colors;
	int _variant;
	int _builtVariant = -1;

	public CrustMode(WorldGenSimulation p) => _p = p;

	public override string Name => "地壳";

	public override string ScaleCaption => _variant == 0
		? "土黄陆壳 蓝洋壳｜红碰撞 橙陆弧 黄洋弧 紫俯冲 绿裂谷 青洋脊"
		: "土黄陆壳 蓝洋壳（仅壳型）";

	public override IReadOnlyList<string> VariantOptions => new[] { "壳型+构造", "仅壳型" };
	public override int VariantIndex => _variant;

	public override void SetVariant(int index)
	{
		if (index < 0 || index > 1 || index == _variant) return;
		_variant = index;
		_colors = null;   // 下次重烘换色
	}

	/// <summary>本帧使用的构造场（判读用；未重烘前为 null）。</summary>
	public TectonicInfluenceSet Influence { get; private set; }

	public override void BeginBake()
	{
		WorldFacts facts = _p.Facts;
		if (facts == null) return;
		if (ReferenceEquals(_facts, facts) && _colors != null && _builtVariant == _variant) return;

		_facts = facts;
		Influence = facts.Tectonic;
		_colors = BuildColors(facts, Influence, _variant == 0);
		_builtVariant = _variant;
	}

	public override Color CellColorAt(int i)
	{
		if (_colors == null) return Colors.Magenta;   // 未重烘：不静默涂错色
		return _colors[i];
	}

	static Color[] BuildColors(WorldFacts facts, TectonicInfluenceSet influence, bool showChannels)
	{
		int n = facts.Ball.CellDirs.Length;
		var colors = new Color[n];
		var bestWeight = new float[n];
		var bestColor = new Color[n];
		TectonicChannel[] channels = showChannels
			? Enum.GetValues<TectonicChannel>()
			: Array.Empty<TectonicChannel>();

		foreach (TectonicChannel channel in channels)
		{
			if (!influence.Has(channel)) continue;
			TectonicInfluenceField field = influence.Get(channel);
			float reach = ReachOf(channel);

			// 只按距离衰减：同通道不同边界的强度差异若也上色，最近源切换会拉出放射状色条，
			// 把「构造体制 + 距离」的判读带偏。强度留到生产剖面里用。
			for (int i = 0; i < n; i++)
			{
				if (field.SourceIndex[i] < 0) continue;
				float distance = field.DistanceKm[i];
				if (!(distance < reach)) continue;

				float weight = 1f - MathOps.Smoothstep(0f, reach, distance);
				if (weight <= bestWeight[i]) continue;
				bestWeight[i] = weight;
				bestColor[i] = ColorOf(channel);
			}
		}

		for (int i = 0; i < n; i++)
		{
			Color baseColor = facts.Crust.Type[i] == CrustType.Continental
				? ContinentalColor
				: OceanicColor;
			colors[i] = baseColor.Lerp(bestColor[i], MathF.Min(0.9f, bestWeight[i]));
		}
		return colors;
	}
}
