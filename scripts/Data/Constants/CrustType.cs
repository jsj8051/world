namespace World.Data;

/// <summary>
/// 板块壳型（2 类，**生成期**按面积加权随机判定，早于陆地掩码）：
/// · <see cref="Oceanic"/>    洋壳：**不发**大陆锚点 ⇒ 只有别处锚点够不到的深海；
/// · <see cref="Continental"/> 陆壳：发一个大陆锚点 ⇒ 陆地产在这里。
///
/// 顺序即默认值：0 = Oceanic ⇒ 漏调 `AssignCrustTypes` 时全为洋壳，由
/// `BuildAnchors` 显式报错兜住（不会静默生成一个全是海的世界）。
/// </summary>
public enum CrustType
{
    Oceanic,
    Continental,
}
