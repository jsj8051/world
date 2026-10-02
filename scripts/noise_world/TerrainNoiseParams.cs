using System;

namespace World.NoiseWorld;

// 噪声栈参数 · 单一事实源（噪声地形-01 §4 定案）：
//   全部参数收进本类；调参面板由 [NoiseParam] 特性**反射自动生成**（手写数十个控件必错）；
//   导出 JSON = 同一结构 ⇒ 不存在"面板值与数据不一致"。
// 分期：本版接 L0-L5 全栈（第一刀 = L0+L1，第二刀 = L2 山系带 + L3 细节 + L4 权重 + L5 样条）。
//   字段声明顺序 = 面板行顺序（分组标题切换处插行）。
// 语义出处：docs/噪声地形/设计-...-01 §5 参数表；与初值偏离处在字段注释里说明理由。
public sealed class TerrainNoiseParams
{
	// ── 星球 ──
	// 种子不走面板滑块（离散大整数）：面板用"重掷种子"按钮派生；导出 JSON 仍含本字段。
	[NoiseParam("星球", "种子", 0, 0, 1, skipPanel: true)]
	public int Seed = 42;

	// ── L0 大陆性场：低频 fBm + 分位重映射（海陆比 = 切分位位置，滑块真正可控）──
	[NoiseParam("L0 大陆场", "大陆波长 km", 4000f, 16000f, 100f)]
	public float ContinentWavelengthKm = 9000f;

	[NoiseParam("L0 大陆场", "大陆倍频", 1, 6, 1)]
	public int ContinentOctaves = 4;

	// 目标陆地占比（**陆板占比**口径，2026-09-30"是什么就是什么"第一步）：每板一枚陆/洋身份，
	// 陆板数 = round(板数 × 此值)；陆地面积 = 陆板面积之和（跟随吞并结果，不再逐格钉住）。
	[NoiseParam("L0 大陆场", "目标陆地占比", 0.02f, 0.9f, 0.01f)]
	public float LandFractionTarget = 0.29f;

	// ── 板块联动（C：汇聚边界造山带；A 板倾向混合已随"身份定海陆"第一步退役）──
	// 注入纪律：碰撞带掩码在 L2 处与海岸掩码取 max（只作用陆侧 h ⇒ 造山不把海抬成陆）。
	// 板块划分/陆洋身份/运动向量来自 NoisePlates（种子 = 本类 Seed，重掷种子板块随地形一起变）。
	[NoiseParam("板块联动", "碰撞死区", 0.1f, 0.9f, 0.05f)]
	public float PlateConvThreshold = 0.4f;   // 接近速度 < 阈值×全球最大的边界不算碰撞（红 Blob 教训：无死区 = 满缘山）

	[NoiseParam("板块联动", "碰撞带宽（格）", 1, 12, 1)]
	public int PlateBeltWidthCells = 4;       // 碰撞边界向板内 BFS 扩散的宽（格；线性衰减到 0）

	[NoiseParam("板块联动", "碰撞带权重", 0f, 1.5f, 0.05f)]
	public float PlateBeltWeight = 1f;        // 碰撞带掩码权重（与海岸掩码取 max 后进山带混合；0 = 关闭 C）

	// ── L1 域扭曲：低频矢量场扭动采样坐标 p → normalize(p + A·w(p))（海岸线弯曲、去圆斑）──
	[NoiseParam("L1 域扭曲", "扭曲波长 km", 1000f, 12000f, 100f)]
	public float WarpWavelengthKm = 4000f;

	// 扭曲倍频：走势的关键旋钮。低倍频（1-2）= 大势圆弯；4 起位移场带 500-1000km 级结构，
	// 海岸线出现半岛/海湾/凹角的"性格"（振幅不变，只加中尺度走向）。
	[NoiseParam("L1 域扭曲", "扭曲倍频", 1, 6, 1)]
	public int WarpOctaves = 4;

	// 扭曲幅度（km，弧长口径）：900 km ≈ 0.14 rad ≈ 8°，海岸线明显弯而不过碎。
	[NoiseParam("L1 域扭曲", "扭曲幅度 km", 0f, 3000f, 25f)]
	public float WarpAmplitudeKm = 900f;

	// ── L2 山系带：ridged 噪声 (1−|n|)^k × 边缘带掩码（山贴大陆边缘，内陆渐隐）──
	[NoiseParam("L2 山系带", "山带波长 km", 300f, 2000f, 10f)]
	public float RidgeWavelengthKm = 700f;

	[NoiseParam("L2 山系带", "ridged 幂", 1f, 4f, 0.1f)]
	public float RidgePower = 2f;

	// 掩码锐度：越大山越集中在岸线，内陆越快让位给高原/平原。
	[NoiseParam("L2 山系带", "掩码锐度", 0.5f, 6f, 0.1f)]
	public float RidgeMaskSharpness = 2.5f;

	// ── L3 细节层：中高频 fBm（米域小振幅，仅陆地，岸线渐入）；海岸摆动 = 细节噪声在岸带门控下
	//    扰动归一化场（样条之前）⇒ 海岸线出湾/出半岛的 100km 级褶皱。关 L3 = 平滑实验态。
	[NoiseParam("L3 细节层", "细节波长 km", 40f, 600f, 5f)]
	public float DetailWavelengthKm = 120f;

	[NoiseParam("L3 细节层", "细节振幅 m", 0f, 1000f, 10f)]
	public float DetailAmplitudeM = 250f;

	// 摆动是归一化场单位（±0.04 ≈ 岸线米域 ±45m 的进退，res4 上 2-3 格宽的褶皱）。
	[NoiseParam("L3 细节层", "海岸摆动", 0f, 0.12f, 0.005f)]
	public float CoastWobble = 0.04f;

	// ── L4 组合权重：h = mix(大陆坡, 山带, 掩码×w1)，细节按 w2 加进米域 ──
	// w0 = 大陆坡满程（内陆高原顶格高度占比）；w1 = 山带强度（山多/山少）。
	[NoiseParam("L4 权重", "大陆场权重", 0f, 1.5f, 0.05f)]
	public float WeightContinent = 1f;

	[NoiseParam("L4 权重", "山带权重", 0f, 1.5f, 0.05f)]
	public float WeightRidge = 0.8f;

	[NoiseParam("L4 权重", "细节权重", 0f, 1f, 0.05f)]
	public float WeightDetail = 0.25f;

	// ── L5 样条：单调分段三次（位置口径固定：深海@-1 / 陆架@-0.5 / 海岸@0 / 丘陵@0.35 /
	//    高原@0.7 / 山脉@1；海平面 = 位置 0 的横切，与 L0 切分位严格对齐 ⇒ 海陆比不受样条影响）。
	//    高度值可调（面板强制递增防回折）。
	[NoiseParam("L5 样条", "深海平原 m", -8000f, -3000f, 50f)]
	public float SplineAbyssM = -6000f;

	[NoiseParam("L5 样条", "大陆架 m", -1000f, -50f, 10f)]
	public float SplineShelfM = -200f;

	[NoiseParam("L5 样条", "平原 m", -100f, 200f, 5f)]
	public float SplinePlainM = 0f;

	[NoiseParam("L5 样条", "丘陵 m", 100f, 800f, 10f)]
	public float SplineHillM = 400f;

	[NoiseParam("L5 样条", "高原 m", 800f, 2500f, 25f)]
	public float SplineHighlandM = 1500f;

	[NoiseParam("L5 样条", "山脉 m", 2500f, 8000f, 50f)]
	public float SplinePeakM = 4500f;

	// （设计文档 §5 的 SeaLevelM（样条横切位置）不入表：本架构海平面 = L0 分位切点，
	//   由"目标陆地占比"直接控制——同一海平面的两种口径，双旋钮会互相打架。）

	// ── fBm 通用（全层共用）──
	[NoiseParam("fBm 通用", "倍频因子", 1.5f, 4f, 0.05f)]
	public float OctaveLacunarity = 2f;

	[NoiseParam("fBm 通用", "振幅衰减", 0.3f, 0.7f, 0.01f)]
	public float OctaveGain = 0.5f;
}

/// <summary>面板反射特性：标注一个参数在调参面板里的行（分组 / 标签 / 范围 / 步长）。
/// int 字段自动取整；skipPanel = 参数不入面板（如种子走按钮），导出 JSON 仍包含。</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class NoiseParamAttribute : Attribute
{
	public string Group { get; }
	public string Label { get; }
	public float Min { get; }
	public float Max { get; }
	public float Step { get; }
	public bool SkipPanel { get; }

	public NoiseParamAttribute(string group, string label, float min, float max, float step,
		bool skipPanel = false)
	{
		Group = group;
		Label = label;
		Min = min;
		Max = max;
		Step = step;
		SkipPanel = skipPanel;
	}
}
