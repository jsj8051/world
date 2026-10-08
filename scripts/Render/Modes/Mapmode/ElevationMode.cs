using Godot;
using World.Render;                // MapMode（表现层契约基类）
using World.Render.Constants;      // MapSeaColor（跨模式共享海格底色）

namespace World.WorldGen;

// 海拔模式（自然地理 · 注册序 = Id = 0）。
//
// 数据链：FinalHeight（Composer.HeightM 单一表面真相）→ DisplayElevation（显示层别名，同一数组
//   非第二份数据）→ ElevationColor。海格统一深蓝底（与海拔色带深海档同色，画面连续）——
//   共享底色单一事实源 = MapSeaColor。
//
// ★色带/档位表归属（2026-10-08）：**模式专属派生表内聚模式文件**（2026-10-08 随模式拆分保持）。
//   原为独立的 `Render/ElevationBandMode`（`: MapMode`，B 线迁移资产）；解耦后 Render 侧已无
//   消费者、且全仓无人再实例化它（BallView 是最后一个）⇒ 其 `MapMode` 面成死代码，遂撤销该类，
//   只把**色带 + 档位名两张表**并入海拔模式所在类（与 `PrecipNorm`/`PrecipStops` 同构：
//   模式专属派生表作为模式类的 public static 成员）。两表共用同一阈值表 ⇒ 画面与文字口径恒一致。
//   宿主装配层取 `ElevationBandName` 喂格信息卡（信息卡为哑组件，不认识海拔语义）；
//   `tests/World.Tests.Local` 取 `ElevationColor` 画离线世界图——与场景海拔模式同一单一事实源。
public sealed class ElevationMode : MapMode
{
	readonly WorldGenPlanet _p;
	public ElevationMode(WorldGenPlanet p) => _p = p;
	public override string Name => "海拔";
	public override Color CellColorAt(int i) =>
		_p.Final.FinalLand[i]
			? ElevationColor(_p.DisplayElevation[i])   // 陆 = 合成海拔分档（HeightComposer 唯一出处）
			: MapSeaColor.Deep;                        // 海 = 统一深蓝底

	// ── 海拔分档色/名（9 档硬色阶 · 档界即等高线）──

	/// <summary>海拔 → 分档色（9 档硬色阶，含海陆；档界即等高线）。海拔模式的**陆地**取色来源。</summary>
	public static Color ElevationColor(float m) => m switch
	{
		< -3000f => new Color(0.05f, 0.16f, 0.42f),   // 深海
		< -1000f => new Color(0.08f, 0.25f, 0.55f),   // 海洋
		< -200f => new Color(0.15f, 0.40f, 0.68f),   // 浅海
		< 0f => new Color(0.55f, 0.75f, 0.80f),   // 岸棚
		< 300f => new Color(0.45f, 0.68f, 0.32f),   // 低地
		< 900f => new Color(0.30f, 0.58f, 0.24f),   // 平原
		< 1800f => new Color(0.38f, 0.50f, 0.26f),   // 高地
		< 2800f => new Color(0.52f, 0.45f, 0.34f),   // 山地
		_ => new Color(0.94f, 0.94f, 0.96f),   // 雪线
	};

	/// <summary>海拔 → 档位名（与 <see cref="ElevationColor"/> 同一阈值表）。消费者 = 宿主装配层
	/// （`WorldGenManager` 取此喂格信息卡；信息卡为哑组件，不认识海拔语义）。</summary>
	public static string ElevationBandName(float m) => m switch
	{
		< -3000f => "深海",
		< -1000f => "大洋",
		< -200f => "浅海",
		< 0f => "岸棚",
		< 300f => "低地",
		< 900f => "平原",
		< 1800f => "高地",
		< 2800f => "山地",
		_ => "雪线",
	};
}
