namespace World.Data;

/// <param name="Seed">世界种子（**全局层**：各阶段经 `SeedDerivation` 派生 salt）。</param>
/// <param name="LandSea">① 海陆骨架阶段配置。</param>
/// <param name="Terrain">② 区域与地形阶段配置。</param>
public readonly record struct WorldSpec(
	int Seed,
	LandSeaSpec LandSea,
	TerrainSpec Terrain);
