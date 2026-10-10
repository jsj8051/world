namespace World.Constants;

/// <summary>
/// **海拔分档**（9 档 + 档界）：这是**世界事实的分类口径**，不是渲染策略——
/// 回答"这个海拔属于哪一档"，与"这一档画什么颜色"无关。
///
/// ★2026-10-11 建（用户拍板"海拔分档表搬到世界侧"）。迁出前的实测问题：
///   色表（`ElevationMode.ElevationColor`）与名表（`ElevationMode.ElevationBandName`）
///   是**两张手抄的阈值表**，阈值逐档相同但文案已漂一处（`&lt;-1000` 档：色表注释"海洋"、名表"大洋"），
///   而名表文档却写着"与色表**同一阈值表**"——那句话当时是假的。
///   ⇒ 档界上提到本层后**只有一份**，色与名各自映射同一个枚举，漂移从"靠人眼比对"变成"编译期不可能"。
///
/// ★形态（**照既有先例，不是新发明**）：枚举嵌在 `static class` 内 —— 与
///   `GeologicalRegions`（内含 `public enum RegionType` + `static string TypeName(...)`）、
///   `Thermal`（常量族）同型。**常量族 `static class` 不进 `Data/` 的"零方法"登记**
///   （登记只针对 `Data/Carrier` 的纯载体）⇒ 方法放在这里是既定做法，不是破例。
///
/// ⚠️ 分工：**颜色属表现层**（`ElevationMode.ElevationColor` 按本枚举取色）、
///   **判读与文案属本层**（判读 = 世界事实的分类，文案 = 该分类的词表）。
/// </summary>
public static class ElevationBands
{
	/// <summary>档位（9 档，与 <see cref="UpperBoundsM"/> 同序）。</summary>
	public enum Band { DeepSea, Ocean, ShallowSea, Shelf, Lowland, Plain, Highland, Mountain, Snowline }

	/// <summary>
	/// **档界**（米）：第 i 档的上界（不含），末档 <c>float.PositiveInfinity</c> = 以上全部。
	/// 语义 = "海拔 &lt; 本值则归本档"，与迁出前的 `switch` 逐档等价（**阈值一字未改**）。
	/// ★这是**唯一的一份档界**：色带与文案都必须经 <see cref="Of"/>，不得各自写 `switch`。
	/// </summary>
	public static readonly float[] UpperBoundsM =
	{
		-3000f, -1000f, -200f, 0f, 300f, 900f, 1800f, 2800f, float.PositiveInfinity,
	};

	static readonly string[] Names =
	{
		"深海", "大洋", "浅海", "岸棚", "低地", "平原", "高地", "山地", "雪线",
	};

	/// <summary>海拔（米）→ 档位。**唯一的判读口**。</summary>
	public static Band Of(float elevationM)
	{
		for (int i = 0; i < UpperBoundsM.Length; i++)
			if (elevationM < UpperBoundsM[i]) return (Band)i;
		return Band.Snowline;   // 兜底（末档为 +∞，正常到不了）
	}

	/// <summary>档位中文名（文案消费方：信息卡）。★与 <see cref="Of"/> 同表 ⇒ 文案与判读恒一致。</summary>
	public static string Name(Band band) => Names[(int)band];
}
