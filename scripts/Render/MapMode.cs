using Godot;
using System.Collections.Generic;

namespace World.Render;

// 地图模式策略抽象（决策 08 §4.4 表现层保留资产 · 从 `World.NoiseWorld.NoiseMapMode` 迁入）。
//
// ★迁移原因：这一层是**纯表现抽象**，生成语义为零——它只回答"格 i 该画什么颜色"，
//   不认识任何生成类型。正因为如此它才被批准从 B 线旧单体回收，而不是随 B 线清退。
//   类名去掉 `Noise` 前缀：新架构已无"噪声线"概念（`NoiseTerrain`/`NoisePlates` 等已清退）。
//
// 取色纪律沿旧 new_HexWorld MapMode：取色方式统一由 `World.Utils.ColorRamp` 提供
//   （`RampSample` 线性 / `RampSampleSmooth` 三次平滑 / `RampLegendColors` 图例），复用不复制。
// ★色带归属（2026-10-08 收口）：**本抽象基类只装契约，不装色板**。各色带内聚其消费方文件
//   （`WorldGenMapModes` 的 Precip/Temperature/Diverging、`Domain.BiomeColors.TempStops`）；
//   模式在 `CellColorAt` 里自行决定用哪条色带，并直接调 `ColorRamp` 取色。
//模式类**只读**，不写任何状态；`BeginBake` 是唯一的重烘钩子（自适应域模式在此刷新 min-max）。
//
// ★模式契约三要素（2026-10-07 地图坞拍板，新增模式一律走统一入口）：
//   ① ParameterOptions/SetParameter —— 参数化模式：MapMode=温度，Parameter=月份（不是 13 个模式实体）；
//   ② ScaleCaption —— 色标说明（固定物理域模式在此声明域与单位；**禁止 min-max 自动拉伸**，
//      否则跨世界不可比较）；
//   ③ CellColorAt —— 唯一取色口。参数只改"取哪个值"，**不改生产公式、不为显示改数据**。
//
// ★注：曾有 `MapModeGroup`（WorldSemantic/Diagnostic）成员用于"诊断垫底、不与世界语义混排"，
//   因全仓库无生产消费者（仅诊断工具打印过）而删除（2026-10-08 用户拍板）——该约定现由
//   **注册顺序 + 注释**维持（见 `WorldGenMapModes.CreateAll`，诊断模式排末尾）。
//
// ★注：曾与本类同文件的 `ElevationBandMode`（抽象基类的参照实现）已于 2026-10-08 **撤销**——
//   其 `MapMode` 面（Id/Name/CellColorAt）无人使用（全仓无实例化），只把两张表并入
//   `WorldGenMapModes.ElevationColor` / `ElevationBandName`。
public abstract class MapMode
{
	public abstract int Id { get; }            // 模式号（坞按钮下标 = 注册序）
	public abstract string Name { get; }       // 按钮文案单一事实源
	public abstract Color CellColorAt(int i);  // sRGB 意图色（视图烘纹理时统一转 linear）

	/// <summary>参数化模式的选项表（null = 无参数行）。例：温度 = [年均, 1月…12月]；
	/// 季节气候 = 9 个派生指标。选项文字 = 参数按钮文案单一事实源。</summary>
	public virtual IReadOnlyList<string> ParameterOptions => null;

	/// <summary>当前参数下标（对应 <see cref="ParameterOptions"/>；无参数模式恒 0）。</summary>
	public virtual int ParameterIndex => 0;

	/// <summary>设置参数（坞参数按钮 → 控制器调；随后控制器负责触发重烘）。</summary>
	public virtual void SetParameter(int index) { }

	/// <summary>色标说明（坞图例行文案；null = 不显示）。固定物理域模式在此声明域与单位。</summary>
	public virtual string ScaleCaption => null;

	/// <summary>每次重烘颜色纹理前调用一次（自适应域模式在此刷新 min-max；O(n) 扫一遍可忽略）。</summary>
	public virtual void BeginBake() { }
}
