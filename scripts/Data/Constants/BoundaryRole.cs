namespace World.Data;

/// <summary>
/// 汇聚格的角色。由该格最近汇聚通道的类型 + 逐边两侧角色派生：
/// 陆陆分上/下盘，陆洋分山链/俯冲，洋洋分岛弧/海沟。
/// </summary>
public enum BoundaryRole
{
    /// <summary>无角色：非汇聚边界（张裂 / 走滑），或该格够不到任何汇聚边界。</summary>
    None,
    /// <summary>陆×陆的上盘（密度小的那块）：宽带高山 + 背后高原。</summary>
    Upper,
    /// <summary>陆×陆的下盘（密度大的那块）：同样成山 —— 碰撞带两侧都是山。</summary>
    Lower,
    /// <summary>陆×洋的**陆侧**（山链内移）；洋×洋的上盘（岛弧）。</summary>
    Arc,
    /// <summary>俯冲下去的那块：陆上是低矮增生楔，洋上是海沟。</summary>
    Slab,
}
