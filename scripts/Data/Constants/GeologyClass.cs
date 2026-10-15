namespace World.Data;

/// <summary>
/// 地质区域类别（8 类；**覆盖全球**——海格也有类别）。顺序即命名序。
///
/// 陆：Plain（平原）/ Basin（盆地）/ Coastline（海岸线）/ Mountain（山脉·碰撞）/ Rift（裂谷·张裂）。
/// 海：ShallowSea（浅海）/ DeepSea（深海）/ Ridge（洋中脊·张裂）。
/// </summary>
public enum GeologyClass
{
	Plain, Basin, Coastline, Mountain, Rift,
	ShallowSea, DeepSea, Ridge,
}
