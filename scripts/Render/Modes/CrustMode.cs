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
	static readonly string[] Variants = { "壳型+运动", "壳型+构造", "仅壳型" };
	internal const int ConstructionVariant = 1;
	internal const int CrustOnlyVariant = 2;

	readonly IWorldFactsSource _p;
	WorldFacts _facts;
	Color[] _colors;
	int _variant;
	int _builtVariant = -1;

	public CrustMode(IWorldFactsSource p) => _p = p;

	public override string Name => "地壳";

	public override string ScaleCaption => _variant switch
	{
		ConstructionVariant => "表层壳型 · 土黄 陆壳 · 蓝 洋壳 · 白箭头 运动方向\n红 碰撞 · 橙 陆弧 · 黄 洋弧 · 紫 俯冲 · 绿 裂谷 · 青 洋脊",
		CrustOnlyVariant => "表层壳型 · 土黄 陆壳 · 蓝 洋壳",
		_ => "表层壳型 · 土黄 陆壳 · 蓝 洋壳 · 深色边带 板块边界 · 白箭头 运动方向",
	};

	public override IReadOnlyList<string> VariantOptions => Variants;
	public override int VariantIndex => _variant;
	public bool ShowMotion => _variant != CrustOnlyVariant;

	public override void SetVariant(int index)
	{
		if (index < 0 || index >= Variants.Length || index == _variant) return;
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
		_colors = BuildColors(facts, Influence, _variant);
		_builtVariant = _variant;
	}

	public override Color CellColorAt(int i)
	{
		if (_colors == null) return Colors.Magenta;   // 未重烘：不静默涂错色
		return _colors[i];
	}

	static Color[] BuildColors(WorldFacts facts, TectonicInfluenceSet influence, int variant)
	{
		int n = facts.Ball.CellDirs.Length;
		IReadOnlyList<CrustType> surface = facts.Surface.SurfaceCrustType;
		var colors = new Color[n];
		if (variant != ConstructionVariant)
		{
			int[] edgeStart = facts.PlateBoundaries.CellEdgeStart;
			for (int i = 0; i < n; i++)
			{
				Color crust = CrustColor(surface[i]);
				colors[i] = variant != CrustOnlyVariant && edgeStart[i] != edgeStart[i + 1]
					? crust.Darkened(0.38f) : crust;
			}
			return colors;
		}

		var bestWeight = new float[n];
		var bestColor = new Color[n];
		foreach (TectonicChannel channel in Enum.GetValues<TectonicChannel>())
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
			CrustType crust = surface[i];
			Color baseColor = CrustColor(crust);
			int dominant = CrustCellQuery.DominantNormalEdge(facts, i);
			if (dominant < 0)
			{
				colors[i] = baseColor.Lerp(bestColor[i], MathF.Min(0.9f, bestWeight[i]));
				continue;
			}

			// 边界格先显示本格的主导运动，周围的影响带只给非活动边界格着色。
			PlateBoundaryEdge edge = facts.PlateBoundaries.Edges[dominant];
			bool isA = edge.CellA == i;
			(CrustType typeA, CrustType typeB) = facts.Surface.Boundaries.SideTypes(dominant);
			BoundarySideRole role = isA ? facts.BoundaryFields.EdgeRoleA[dominant] : facts.BoundaryFields.EdgeRoleB[dominant];
			colors[i] = TectonicSourceOps.TryChannelOf(facts.BoundaryFields.EdgeKind[dominant],
				isA ? typeA : typeB, isA ? typeB : typeA, role == BoundarySideRole.Overriding, out TectonicChannel channel)
				? baseColor.Lerp(ColorOf(channel), 0.9f) : baseColor;
		}
		return colors;
	}

	static Color CrustColor(CrustType type) => type == CrustType.Continental ? ContinentalColor : OceanicColor;
}
