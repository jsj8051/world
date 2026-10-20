namespace World.Data;

/// <summary>
/// 海拔分档：把海拔归进 9 档，是判读口径，不是渲染策略（取色归 <c>ElevationMode</c>）。
/// 档界只有这一份，文案经 <see cref="Name"/>；海拔模式现为连续灰度、不再按档取色，两者仍同序。
/// </summary>
public static class ElevationBands
{
	/// <summary>档位（9 档，与 <see cref="UpperBoundsM"/> 同序）。</summary>
	public enum Band { DeepSea, Ocean, ShallowSea, Shelf, Lowland, Plain, Highland, Mountain, Snowline }

	/// <summary>
	/// 第 i 档的上界（米，不含；末档 +∞）：海拔 &lt; 本值即归本档。
	/// 地球尺度固定档界（−3000 / −1000 / −200 / 0 / 300 / 900 / 1800 / 2800），
	/// 不随世界 / seed 自适应 ⇒ 跨世界可比。
	/// </summary>
	public static readonly float[] UpperBoundsM =
	{
		-3000f, -1000f, -200f, 0f, 300f, 900f, 1800f, 2800f, float.PositiveInfinity,
	};

	static readonly string[] Names =
	{
		"深海", "大洋", "浅海", "岸棚", "低地", "平原", "高地", "山地", "雪线",
	};

	/// <summary>海拔（米）→ 档位；判读的唯一入口。</summary>
	public static Band Of(float elevationM)
	{
		for (int i = 0; i < UpperBoundsM.Length; i++)
			if (elevationM < UpperBoundsM[i]) return (Band)i;
		return Band.Snowline;
	}

	/// <summary>档位中文名（信息卡等文案用），与 <see cref="Of"/> 同表。</summary>
	public static string Name(Band band) => Names[(int)band];
}
