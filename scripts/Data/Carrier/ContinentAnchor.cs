using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用

namespace World.Data;

// 数据层 `World.Data`（2026-10-09 建立）：**只有数据成员、零方法的纯载体**。
//   入层判据（机器可验）：顶层类型 + 零方法 + 零计算属性 + 无嵌套 + 不引用生成域类型。
//   为什么独立成层：这类载体此前散落在各生成器文件里（一个 .cs 装 2-3 个类型），
//     既看不出"世界线有哪些数据形状"，契约登记也只能靠人肉点名。
//   ★依赖方向（单向）：`World.Logic → World.Data`（生成器依赖载体）。
//     载体**不得**引用 `World.Logic` 的类型——否则成环。正因如此，
//     `FeatureField`（引用 `ITerrainField`/`TerrainDomain`）与嵌套类型 `GeoRegions.Region`
//     留在原地，未纳入本层；判据之外的类型一律不进。
//   本层类型须登记 `ArchitectureContractTests.NewWorldLineTypes`。
//  ★**本层分两段（2026-10-10；`Data/Spec/` 于 2026-10-11 改为参数实例）**——
//     子目录是**自由分组**（不进命名空间），段判据 = **"谁构造它"**：
//     · `Data/Spec/`   = **世界定义 · 参数实例**（`WorldSpec` / `LandSeaSpec` / `TerrainSpec`）：
//                        参数管理器构造并持有唯一实例、按段切片喂给各阶段；**可变类**（有身份、可改），
//                        属性初值 = 出厂档，内容真相源 = 正库参数表 `res/params/world_params.json`
//                        （由 `World.Params.WorldParamStore` 读入、`WorldParams` 持有）。
//     · `Data/Carrier/`= **生成链内部流通的数据形状**（本文件 / `MountainRidge` / `Scale3`）：
//                        生成器写、下游读，不由人直接调。
//     ⚠️ 二者**不是**"spec vs 非 spec"，而是"外部旋钮 vs 内部形状"：一个类型同时具备两边特征
//        （如已删的 `LandSeaParams`：装配层构造 = spec 侧，却持有运行时字段 + 自带初值 = 载体侧）
//        即说明它**没被规范化**，应按段拆开，而不是硬塞进某一段。
/// <summary>大陆锚点：中心方向 + 椭圆影响参数（shape = 轴比，rotation = 切平面内轴朝向）+ 海岸性格。</summary>
public sealed class ContinentAnchor
{
	/// <summary>中心单位方向。</summary>
	public Vector3 Dir;
	/// <summary>影响半径（km，球面弧长口径）——"size"。</summary>
	public float RadiusKm;
	/// <summary>shape 各向异性轴（切平面内正交单位向量；即 "rotation" 的载体）。</summary>
	public Vector3 AxisU, AxisV;
	/// <summary>沿 U/V 轴的拉伸比（≥1；1 = 圆形大陆，2 = 明显长条）。</summary>
	public float StretchU, StretchV;
	/// <summary>海岸复杂度乘子（≈[0.5,1.6]）：调制该大陆的半岛/海湾/细节幅度——"coastComplexity"。</summary>
	public float CoastComplexity;
	/// <summary>影响权重（≈[0.8,1.25]）：进影响场 ⇒ 归属 = 加权 Voronoi。</summary>
	public float Weight;
}
