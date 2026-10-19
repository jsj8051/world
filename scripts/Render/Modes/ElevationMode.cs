using Godot;
using World.Data;                  // ElevationBands（海拔分档：档界与档位名的唯一处）
using World.Logic;                 // WorldGenSimulation（世界事实与地形高度）

namespace World.Render;

// 海拔模式（自然地理 · 注册序 = Id = 0）。
//
// 数据链：`Terrain.HeightM`（唯一表面真相）→ ElevationColor。**陆海同一条色带**——
// 九档硬色阶（深海 / 大洋 / 浅海 / 岸棚 / 低地 / 平原 / 高地 / 山地 / 雪线），档界即等高线。
// 档界与档位名在 `World.Data.ElevationBands`（世界侧的分类口径），本类只保留**颜色**这一表现层映射
// ⇒ 判读 / 文案 / 取色共用同一份档界，不会漂移。
public sealed class ElevationMode : MapMode
{
	readonly WorldGenSimulation _p;
	public ElevationMode(WorldGenSimulation p) => _p = p;
	public override string Name => "海拔";
	public override Color CellColorAt(int i) => ElevationColor(_p.Terrain.HeightM[i]);

	/// <summary>海拔 → 分档色（九档硬色阶；档界即等高线）。★档位判读走 `ElevationBands.Of`（与信息卡文案同一份档界）。</summary>
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
