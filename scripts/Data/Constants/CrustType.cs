namespace World.Data;

/// <summary>
/// 板块壳型（2 类，**生成期**按格数加权随机判定）——本阶段唯一的陆海产物。
/// · <see cref="Oceanic"/>    洋壳：格由 ① 阶段铺**海面**高度；
/// · <see cref="Continental"/> 陆壳：格由 ① 阶段铺**陆面**高度 ⇒ 陆地长在陆壳上。
/// （海岸线不由壳型直接决定——陆面基线穿过海面处才是岸。）
///
/// 顺序即默认值：0 = Oceanic ⇒ 漏调 `AssignCrustTypes` 时全为洋壳；
/// `AssignCrustTypes` 自身保证至少一块陆壳（不会静默生成一个全是海的世界）。
/// </summary>
public enum CrustType
{
    Oceanic,
    Continental,
}
