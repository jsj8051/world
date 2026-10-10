using Godot;
using World.Constants;             // ElevationBands（海拔分档：档界与档位名的唯一处）
using World.Render.Constants;      // MapSeaColor（跨模式共享海格底色）——★父命名空间看不见子，必须显式
using World.Logic;                   // WorldGenSimulation / RiverGeometry / RiverGraph（世界事实与水文事实）

namespace World.Render;

// 海拔模式（自然地理 · 注册序 = Id = 0）。
//
// 数据链：FinalHeight（Composer.HeightM 单一表面真相）→ DisplayElevation（显示层别名，同一数组
//   非第二份数据）→ ElevationColor。海格统一深蓝底（与海拔色带深海档同色，画面连续）——
//   共享底色单一事实源 = MapSeaColor。
//
// ★色带/档位表归属（2026-10-08）：**模式专属派生表内聚模式文件**（2026-10-08 随模式拆分保持）。
//   原为独立的 WRender/ElevationBandModeW（W: MapModeW，B 线迁移资产）；解耦后 Render 侧已无
//   消费者、且全仓无人再实例化它（BallView 是最后一个）⇒ 其 WMapModeW 面成死代码，遂撤销该类，
//   只把**色带 + 档位名两张表**并入海拔模式所在类（与 WPrecipNormW/WPrecipStopsW 同构：
//   模式专属派生表作为模式类的 public static 成员）。两表共用同一阈值表 ⇒ 画面与文字口径恒一致。
//   宿主装配层取 WElevationBandNameW 喂格信息卡（信息卡为哑组件，不认识海拔语义）；
//   Wscripts/Test/World.Tests.LocalW 取 WElevationColorW 画离线世界图——与场景海拔模式同一单一事实源。
public sealed class ElevationMode : MapMode
{
	readonly WorldGenSimulation _p;
	public ElevationMode(WorldGenSimulation p) => _p = p;
	public override string Name => "海拔";
	public override Color CellColorAt(int i) =>
		_p.Facts.Final.FinalLand[i]
			? ElevationColor(_p.Terrain.DisplayElevation[i])   // 陆 = 合成海拔分档（HeightComposer 唯一出处）
			: MapSeaColor.Deep;                        // 海 = 统一深蓝底

	// ── 海拔分档色（9 档硬色阶 · 档界即等高线）──
	// ★2026-10-11：**档界与档位名已上提到 `World.Constants.ElevationBands`**（世界侧的分类口径）。
	//   本类只保留**颜色**这一表现层专属映射 ⇒ 三处（判读 / 文案 / 取色）不再各写一遍阈值，
	//   此前色表与名表是两张手抄表且已漂一处（详见 ElevationBands 头注释）。

	/// <summary>海拔 → 分档色（9 档硬色阶，含海陆；档界即等高线）。海拔模式的**陆地**取色来源。
	/// ★档位判读走 `ElevationBands.Of`（与信息卡文案同一份档界）。</summary>
	public static Color ElevationColor(float m) => BandColor(ElevationBands.Of(m));

	/// <summary>档位 → 颜色（表现层专属映射；改色只动这里，改档界去 `ElevationBands`）。</summary>
	public static Color BandColor(ElevationBands.Band band) => band switch
	{
		ElevationBands.Band.DeepSea => new Color(0.05f, 0.16f, 0.42f),
		ElevationBands.Band.Ocean => new Color(0.08f, 0.25f, 0.55f),
		ElevationBands.Band.ShallowSea => new Color(0.15f, 0.40f, 0.68f),
		ElevationBands.Band.Shelf => new Color(0.55f, 0.75f, 0.80f),
		ElevationBands.Band.Lowland => new Color(0.45f, 0.68f, 0.32f),
		ElevationBands.Band.Plain => new Color(0.30f, 0.58f, 0.24f),
		ElevationBands.Band.Highland => new Color(0.38f, 0.50f, 0.26f),
		ElevationBands.Band.Mountain => new Color(0.52f, 0.45f, 0.34f),
		_ => new Color(0.94f, 0.94f, 0.96f),   // 雪线
	};
}
