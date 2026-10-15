namespace World.Data;

/// <summary>
/// 汇聚边界上「某一侧」的角色。由**板块对**决定，与位置无关（欧拉矢量 / 壳型 / 密度都是逐板块常量）。
///
/// 与壳型是**两个正交维度**：同一个上盘，落在陆壳上成山脉、落在洋壳上成岛弧；下盘洋壳成海沟。
/// </summary>
public enum BoundaryRole
{
    /// <summary>无角色：非汇聚边界（张裂 / 走滑），或该格够不到任何汇聚边界。</summary>
    None,
    /// <summary>上盘（骑在上面）。</summary>
    Overriding,
    /// <summary>下盘（俯冲下去）。</summary>
    Subducting,
}
