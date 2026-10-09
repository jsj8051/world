using World.Data;               // WorldSpec / LandSeaSpec / TerrainSpec（纯数据形状）
using World.Constants;          // Geology（区域粒度默认值——2026-10-10 归常量族）

namespace World.WorldGen;

/// <summary>
/// **默认世界定义（退化档）**——"地球档"。
///
/// ★为什么单独成类型、而不是做成 spec 上的 `Earth` 静态属性（2026-10-10 用户拍板）：
///   三个 spec 类型住在 `World.Data`，数据层入层判据的最后一条是**不引用生成域类型**；
///   本档是 `static class` **常量/内容提供者**（静态属性 = 计算属性），本就不符合"纯数据载体"判据——
///   若挂在 spec 上会让 `Data → WorldGen` **反向成环**（既定方向是 `WorldGen → Data`）。
///   ⇒ 判据之外的类型一律不进数据层：**形状进 `World.Data`，内容（预设）留 `World.WorldGen`**。
///
/// ★数值来源（2026-10-10 迁移后）：`Seed`/`ContinentCount`/`LandFraction` 是**字面量**
///   （与参数化之前一字不差）；① 阶段的另 10 个世界参数（域扭曲 3 + 三尺度 7）同样是字面量——
///   它们原以**字段初值**形式住在 `LandSeaParams` 里（= 内容装在形状里，已随该类型删除），
///   现在逐字搬到这里（搬迁后 `LandSeaParams` 不复存在）。
///   `TargetRegionAreaKm2` 则**引用常量** `World.Constants.Geology`——
///   该常量原本寄生在生成器 `GeologicalRegions` 上，已迁常量族（见 `Geology` 头部）。
///   ★判别"进常量族还是留本档"的口径 = **消费者个数**：`Geology.TargetRegionAreaKm2` 有**两个**
///     消费者（本档 + `GeologicalRegions.Generate` 的缺省参数）故成具名常量；
///     其余各自只有本档**一个**消费者 ⇒ 直接字面量，由 `WorldSpecTests` 逐字钉住（同 `Seed=42`）。
///
/// ★退化档语义（永久原则 4）：本档 = 参数化之前的现行默认值
///   （原 `WorldGenPlanet` 的 `[Export]` 初值：种子 42 / 7 陆块 / 0.29 陆海比 / 区域粒度 = 常量默认）。
///   装配层 `[Export]` 初值与测试都引用本档 ⇒ **默认值只有一处定义**。
///
/// ★未来多预设（地球档 / 火星档 / 存档内嵌）：本档是**逻辑侧兜底**；
///   用户可编辑的预设应做成 Scene 侧的 `[GlobalClass] Resource`（`.tres`），
///   由装配层组装成 `WorldSpec` 后经 `Run()` 注入 —— **逻辑侧永远只认冻结后的 spec**。
/// </summary>
public static class WorldSpecDefaults
{
	/// <summary>默认世界（退化档）：与参数化之前逐点一致。</summary>
	public static WorldSpec Earth { get; } = new(
		42,
		// ★生产侧**唯一**的 `LandSeaSpec` 构造点，12 个字段全部走具名参数 ⇒ 顺序陷阱由编译器消除。
		new LandSeaSpec(
			ContinentCount: 7,
			LandFraction: 0.29f,
			// ── 域扭曲（原 `LandSeaParams` 字段初值，逐字搬入）──
			WarpWavelengthKm: 3000f,
			WarpOctaves: 3,
			WarpAmplitudeKm: 600f,
			// ── 三尺度轮廓调制 ──
			LowWavelengthKm: 5000f,
			LowOctaves: 2,
			LowAmplitude: 0.35f,
			MediumWavelengthKm: 1200f,
			MediumAmplitude: 0.25f,
			SmallWavelengthKm: 300f,
			SmallAmplitude: 0.12f),
		new TerrainSpec(Geology.TargetRegionAreaKm2));
}
