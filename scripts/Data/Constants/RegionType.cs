namespace World.Constants;

/// <summary>
/// **地质区域类型词表**（7 值）：区域是"大尺度地貌单元"的分类名，不是生成过程本身。
///
/// ★2026-10-11 迁入本层（原愚居于 `World.Logic` 的 `GeologicalRegions.cs` 内）：
///   它与 <see cref="BiomeType"/>（柯本气候型词表）**同类**——都是"世界事实的词表"，
///   而 `BiomeType` 早已在本层。留它在那里会造成一条**跨层倒挂**：
///   `World.Data.Carrier.PickedCell`（拾取结果快照）需要记录区域类型，
///   而数据层的判据明列"载体**不得**引用 `World.Logic` 的类型——否则成环"。
///   词表上提到本层后，数据层与表现层都能直接用它，**没有新增任何跨层边**。
///
/// ★**区域名文案不在这里**：`GeologicalRegions.TypeName`（枚举 → 短名）仍住在逻辑层，
///   因为本层的入层判据是"顶层类型 + **零方法** + 零计算属性 + 无嵌套"
///   （枚举本身是"零方法"的极端形态，故词表合格、映射函数不合格）。
///   ⇒ 词表归本层、文案归消费者，与 `BiomeType` 的处理一致。
///
/// ⚠️ 区分两个易混的绿色：本枚举是**区域类型**（Plain/Highland/…），
///   与 `BiomeType`（柯本气候型）不是一套分类，不要互相代入。
/// </summary>
public enum RegionType { Plain, Highland, Basin, Mountain, Plateau, Rift, Coastal }
