namespace World.Data;

/// <summary>
/// 一次拾取的结果（**纯数据**：坐标 / 海拔 / 海陆 / 归属区域 / 各距离）——点选那一刻的**自足快照**。
/// 属 `Data/Carrier/`：查询链内部流通的数据形状（查询方写、界面读），不是 `Data/Spec/` 那种外部旋钮。
/// 是 struct 而非 class：**无身份的只读快照**（值语义、拷贝便宜、`in` 传参）。
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
	public int RegionId;                              // 区域图全球覆盖 ⇒ 恒 ≥ 0（海格也有区域）
	public GeologyClass RegionClass;                  // 区域类别（八类）
}
