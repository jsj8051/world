using World.Constants;    // RegionType（区域类型词表；常量族不是生成域类型）

namespace World.Data;

/// <summary>
/// 一次拾取的结果（**纯数据**：坐标 / 海拔 / 海陆 / 归属区域 / 各距离）——点选那一刻的**自足快照**。
///
/// ★2026-10-11 迁入本层（原住 `World.Render.WorldPicker.cs`）：按本层的入层判据（顶层类型 +
///   零方法 + 零计算属性 + 无嵌套 + 不引用生成域类型）它本就合格，原位置只是"拾取结果顺手放"。
///   迁出的动因是**消掉一条跨层边**：`World.UI.CellInfoCard` 要吃这份数据，而它在 `World.Render`
///   里 ⇒ 界面层被迫引用表现层。现在生产方（`WorldPicker`）与消费方（信息卡）都只认 `World.Data`。
///
/// ★留在 `Data/Carrier/` 的理由（"谁构造它"分段）：它属**生成链/查询链内部流通的数据形状**
///   （查询方写、界面读），不是 `Data/Spec/` 那种"外部旋钮"参数实例。
///
/// ★为什么是 struct 而非 class：**无身份的只读快照**（值语义、拷贝便宜、`in` 传参），
///   与 `Data/Carrier/` 另三个载体的差别只在"类 vs 结构体"这一形式，判据（零方法）相同。
/// </summary>
public struct PickedCell
{
	public ulong CellId;
	public int Index;
	public float LatDeg, LngDeg, ElevM;
	public bool IsLand;
	public float LandFraction;
	public int LandmassId;
	public int DistToCoast, DistToLand;
	public int RegionId;                              // -1 = 无区域
	public RegionType RegionType;                     // 仅 RegionId ≥ 0 时有义
}
